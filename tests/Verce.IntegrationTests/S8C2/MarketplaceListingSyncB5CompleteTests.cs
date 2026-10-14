using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Quartz;
using Verce.Infrastructure.Marketplaces;
using Verce.Modules.Commerce;
using Verce.Platform.Audit;
using Verce.Platform.Persistence;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.S8C2;

/// <summary>Handoff B5/B6 completion (Opus 022 contract): shared admission guards, durable provider-wide fence, active-run arbiter, best-effort trigger,
/// B6 options, dispatcher, executor host, scheduling registration, per-provider limiter and the application read projections.</summary>
public sealed partial class MarketplaceListingSyncExecutionIntegrationTests
{
    // ---------- shared fakes ----------

    /// <summary>Records scheduler calls through a DispatchProxy over the real IScheduler interface (no extra package).</summary>
    public class RecordingScheduler : DispatchProxy
    {
        public List<JobKey> Triggered { get; } = [];
        public bool Registered { get; set; } = true;
        public Exception? ThrowOnTrigger { get; set; }
        public Func<int>? ObserveRuns { get; set; }
        public int RunsVisibleAtTrigger { get; private set; } = -1;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case "CheckExists": return Task.FromResult(Registered);
                case "TriggerJob":
                    if (ObserveRuns is { } observe) RunsVisibleAtTrigger = observe();
                    Triggered.Add((JobKey)args![0]!);
                    if (ThrowOnTrigger is { } failure) throw failure;
                    return Task.CompletedTask;
                default: return targetMethod.ReturnType == typeof(Task) ? Task.CompletedTask : targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
            }
        }
    }

    private sealed class SchedulerFactoryStub(IScheduler scheduler) : ISchedulerFactory
    {
        public Task<IReadOnlyList<IScheduler>> GetAllSchedulers(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<IScheduler>>([scheduler]);
        public Task<IScheduler> GetScheduler(CancellationToken cancellationToken = default) => Task.FromResult(scheduler);
        public Task<IScheduler?> GetScheduler(string schedName, CancellationToken cancellationToken = default) => Task.FromResult<IScheduler?>(scheduler);
    }

    private static (RecordingScheduler Recorder, ISchedulerFactory Factory) NewScheduler()
    {
        var proxy = DispatchProxy.Create<IScheduler, RecordingScheduler>(); return ((RecordingScheduler)(object)proxy, new SchedulerFactoryStub(proxy));
    }

    private sealed class HookedScopeFactory(IServiceScopeFactory inner, Action onCreate) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() { onCreate(); return inner.CreateScope(); }
    }

    /// <summary>A reader for another provider code, so the real provider has no registered listing reader.</summary>
    private sealed class OtherProviderReader : IMarketplaceListingReader
    {
        public string ProviderCode => "OTHER_PROVIDER";
        public Task<MarketplaceListingPage> ReadListingPageAsync(MarketplaceListingPageRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<MarketplaceListingDetailResult>> ReadListingDetailsAsync(MarketplaceListingDetailRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>Parks inside the provider page call until released or cancelled, and records how many calls are inside it at once.</summary>
    private sealed class BlockingReader : IMarketplaceListingReader
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered, _concurrent, _max;
        public string ProviderCode => "MERCADO_LIVRE";
        public int Entered => Volatile.Read(ref _entered);
        public int Max => Volatile.Read(ref _max);
        public void Release() => _release.TrySetResult();
        public async Task<MarketplaceListingPage> ReadListingPageAsync(MarketplaceListingPageRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _entered); var now = Interlocked.Increment(ref _concurrent);
            int seen; do { seen = Volatile.Read(ref _max); } while (now > seen && Interlocked.CompareExchange(ref _max, now, seen) != seen);
            try { await _release.Task.WaitAsync(ct); } finally { Interlocked.Decrement(ref _concurrent); }
            return new MarketplaceListingPage([], null, true, 0);
        }
        public Task<IReadOnlyList<MarketplaceListingDetailResult>> ReadListingDetailsAsync(MarketplaceListingDetailRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        // Condition-driven wait with a deadlock guard; it never decides a semantic outcome.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (!condition()) { if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException(what); await Task.Delay(10); }
    }

    private async Task<int> RunCountAsync() { await using var db = Context(); return await db.Set<MarketplaceListingSyncRun>().CountAsync(); }
    private async Task<int> AuditCountAsync() { await using var db = Context(); return await db.Set<AuditLogEntry>().CountAsync(x => x.EntityTable == "marketplace_listing_sync_run"); }

    private async Task<Guid> CreateRunAtAsync(Guid account, DateTimeOffset requestedAt)
    {
        await using var db = Context(); return (await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), requestedAt)).Id;
    }

    private MarketplaceListingSyncDispatcher CreateDispatcher(VerceDbContext db, MarketplaceListingSyncExecutor executor, MarketplaceListingSyncOptions settings)
        => new(db, _clock, Options.Create(settings), executor, NullLogger<MarketplaceListingSyncDispatcher>.Instance);

    private (MarketplaceListingSyncExecutor Executor, List<VerceDbContext> Contexts) CreateHost(IMarketplaceListingReader reader, MarketplaceListingSyncOptions settings, MarketplaceProviderInFlightLimiter? limiter = null)
    {
        var contexts = new List<VerceDbContext>(); var services = new ServiceCollection();
        services.AddScoped(_ => { var created = Context(); lock (contexts) contexts.Add(created); return created; });
        services.AddScoped(sp => CreateExecution(sp.GetRequiredService<VerceDbContext>(), reader, settings: settings, limiter: limiter));
        return (new MarketplaceListingSyncExecutor(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<MarketplaceListingSyncExecutor>.Instance), contexts);
    }

    private async Task InsertOperationRowAsync(string kind, string phase, string provider, Guid? account)
    {
        await using var db = Context();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO commerce.marketplace_provider (code,name) VALUES ({provider},{provider}) ON CONFLICT DO NOTHING");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,created_at,updated_at,cleanup_attempt_count,version) VALUES ({Guid.NewGuid()},{kind},{phase},'PENDING','NOT_REQUIRED',{account},{provider},{_clock.UtcNow},{_clock.UtcNow},0,1)");
    }

    // ---------- T01 / T02: durable provider-wide callback fence ----------

    [Theory]
    [InlineData("CONNECT_NEW", "EXTERNAL_IN_FLIGHT", "MERCADO_LIVRE", true)]
    [InlineData("RECONNECT", "SECRET_PERSISTED", "MERCADO_LIVRE", true)]
    [InlineData("CONNECT_NEW", "PREPARED", "MERCADO_LIVRE", false)]
    [InlineData("CONNECT_NEW", "EXTERNAL_IN_FLIGHT", "SHOPEE", false)]
    public async Task T01_durable_provider_wide_fence_refuses_admission_only_for_a_same_provider_in_flight_callback(string kind, string phase, string provider, bool refused)
    {
        var account = await SeedAccountAsync(); await InsertOperationRowAsync(kind, phase, provider, null);
        var (recorder, factory) = NewScheduler(); MarketplaceListingSyncRequestResult result;
        await using (var db = Context()) result = await CreateService(db, new MarketplaceListingSyncOptions { SchedulingEnabled = true }, factory).StartFullAsync(account, Guid.NewGuid());
        if (refused)
        {
            result.Succeeded.Should().BeFalse(); result.Code.Should().Be("MARKETPLACE_AUTHORIZATION_NOT_USABLE"); result.Reason.Should().Be("AUTHORIZATION_NOT_USABLE");
            (await RunCountAsync()).Should().Be(0); (await AuditCountAsync()).Should().Be(0); recorder.Triggered.Should().BeEmpty();
        }
        else { result.Succeeded.Should().BeTrue(); recorder.Triggered.Should().HaveCount(1); }
    }

    [Fact]
    public async Task T02_fence_appearing_after_claim_stops_execution_before_any_provider_reader_call()
    {
        var account = await SeedAccountAsync(); var runId = await CreateRunAtAsync(account, _clock.UtcNow); MarketplaceListingSyncLease lease;
        await using (var db = Context()) lease = (await new MarketplaceListingSyncLeaseAuthority(db).ClaimAsync(runId, _clock.UtcNow, TimeSpan.FromMinutes(2)))!;
        await InsertOperationRowAsync("CONNECT_NEW", "EXTERNAL_IN_FLIGHT", "MERCADO_LIVRE", null);
        var reader = new ScriptedReader(["MLB-FENCED"], _clock); MarketplaceListingSyncRun result;
        await using (var db = Context()) result = (await CreateExecution(db, reader).RunClaimedAsync(lease))!;
        result.Status.Should().Be(MarketplaceListingSyncStatus.FAILED); result.SafeResultCode.Should().Be("AUTHORIZATION_NOT_USABLE");
        reader.PageCalls.Should().Be(0); reader.DetailBatches.Should().BeEmpty();
    }

    // ---------- T03 / T04: unknown ids and retry precedence ----------

    [Fact]
    public async Task T03_unknown_account_or_run_throws_key_not_found_and_writes_nothing()
    {
        await SeedAccountAsync(); var actor = Guid.NewGuid(); await using var db = Context(); var service = CreateService(db);
        var calls = new Func<Task>[] { () => service.StartFullAsync(Guid.NewGuid(), actor), () => service.RetryAsync(Guid.NewGuid(), actor), () => service.GetStatusAsync(Guid.NewGuid()), () => service.GetRunAsync(Guid.NewGuid()), () => service.GetItemsAsync(Guid.NewGuid(), null, 1, 10) };
        foreach (var call in calls) (await Record.ExceptionAsync(call)).Should().BeOfType<KeyNotFoundException>();
        (await RunCountAsync()).Should().Be(0); (await AuditCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task T04_retry_precedence_is_source_then_eligibility_then_account_guards_then_active_run()
    {
        // RUNNING source is itself the active run but must report NOTHING_TO_RETRY, not ALREADY_ACTIVE.
        var account = await SeedAccountAsync(); var runningId = await CreateRunAtAsync(account, _clock.UtcNow);
        await using (var db = Context()) (await new MarketplaceListingSyncLeaseAuthority(db).ClaimAsync(runningId, _clock.UtcNow, TimeSpan.FromMinutes(2))).Should().NotBeNull();
        MarketplaceListingSyncRequestResult running; await using (var db = Context()) running = await CreateService(db).RetryAsync(runningId, Guid.NewGuid());
        running.Code.Should().Be("LISTING_SYNC_NOTHING_TO_RETRY"); running.ActiveRunId.Should().BeNull();

        // SUCCEEDED source + failing account guard => eligibility wins.
        var second = await SeedAccountAsync(); var (succeededId, succeeded) = await RunFullAsync(second, new ScriptedReader(["MLB-T4-OK"], _clock)); succeeded.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        await using (var db = Context()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account SET active = false WHERE id = {second}");
        MarketplaceListingSyncRequestResult none; await using (var db = Context()) none = await CreateService(db).RetryAsync(succeededId, Guid.NewGuid());
        none.Code.Should().Be("LISTING_SYNC_NOTHING_TO_RETRY");
    }

    [Fact]
    public async Task T04_eligible_partial_source_with_a_failing_account_guard_returns_that_guard()
    {
        var (account, originalId) = await BuildPartialOriginalAsync();
        await using (var db = Context()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account SET active = false WHERE id = {account}");
        MarketplaceListingSyncRequestResult result; await using (var db = Context()) result = await CreateService(db).RetryAsync(originalId, Guid.NewGuid());
        result.Code.Should().Be("MARKETPLACE_ACCOUNT_INACTIVE"); result.Reason.Should().Be("ACCOUNT_INACTIVE");
        (await RunCountAsync()).Should().Be(1);
    }

    // ---------- T05: complete guard matrix (code + blockedReason), identical through the status availability ----------

    [Theory]
    [InlineData("UPDATE commerce.marketplace_account SET active=false", "MARKETPLACE_ACCOUNT_INACTIVE", "ACCOUNT_INACTIVE")]
    [InlineData("UPDATE pricing.sales_channel SET active=false", "MARKETPLACE_ACCOUNT_CHANNEL_INVALID", "CHANNEL_INVALID")]
    [InlineData("--provider-reader-missing", "MARKETPLACE_PROVIDER_NOT_SUPPORTED", "PROVIDER_NOT_CONFIGURED")]
    [InlineData("UPDATE commerce.marketplace_provider_capability SET state='UNKNOWN' WHERE capability_code='LISTINGS_READ'", "COMMERCE_CAPABILITY_UNAVAILABLE", "CAPABILITY_UNSUPPORTED")]
    [InlineData("UPDATE commerce.marketplace_account_capability SET state='UNKNOWN'", "COMMERCE_CAPABILITY_UNAVAILABLE", "GRANT_UNKNOWN")]
    [InlineData("UPDATE commerce.marketplace_account_capability SET state='DENIED'", "COMMERCE_CAPABILITY_UNAVAILABLE", "GRANT_DENIED")]
    [InlineData("UPDATE commerce.marketplace_account_connection SET authorization_state='REAUTHORIZATION_REQUIRED'", "MARKETPLACE_AUTHORIZATION_NOT_USABLE", "AUTHORIZATION_NOT_USABLE")]
    [InlineData("INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,created_at,updated_at,cleanup_attempt_count,version) VALUES (gen_random_uuid(),'REFRESH','EXTERNAL_IN_FLIGHT','PENDING','NOT_REQUIRED','@account','MERCADO_LIVRE',now(),now(),0,1)", "MARKETPLACE_AUTHORIZATION_NOT_USABLE", "AUTHORIZATION_NOT_USABLE")]
    [InlineData("INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,created_at,updated_at,cleanup_attempt_count,version) VALUES (gen_random_uuid(),'CONNECT_NEW','EXTERNAL_IN_FLIGHT','PENDING','NOT_REQUIRED',NULL,'MERCADO_LIVRE',now(),now(),0,1)", "MARKETPLACE_AUTHORIZATION_NOT_USABLE", "AUTHORIZATION_NOT_USABLE")]
    [InlineData("UPDATE commerce.marketplace_account_connection SET runtime_retry_after_until='2099-01-01T00:00:00Z'", "COMMERCE_OPERATION_UNAVAILABLE", "RATE_LIMITED")]
    [InlineData("UPDATE commerce.marketplace_account_connection SET runtime_availability='UNAVAILABLE'", "COMMERCE_OPERATION_UNAVAILABLE", "RUNTIME_UNAVAILABLE")]
    [InlineData("--active-run", "LISTING_SYNC_ALREADY_ACTIVE", "SYNC_ACTIVE")]
    public async Task T05_every_guard_refuses_with_its_code_and_blocked_reason_and_creates_nothing(string mutation, string code, string reason)
    {
        var account = await SeedAccountAsync(); Guid? active = null; IMarketplaceListingReader? reader = null;
        if (mutation == "--provider-reader-missing") reader = new OtherProviderReader();
        else if (mutation == "--active-run") active = await CreateRunAtAsync(account, _clock.UtcNow);
        else await using (var db = Context()) await db.Database.ExecuteSqlRawAsync(mutation.Replace("@account", account.ToString()));
        var (recorder, factory) = NewScheduler(); var baselineRuns = await RunCountAsync(); var settings = new MarketplaceListingSyncOptions { SchedulingEnabled = true };
        MarketplaceListingSyncRequestResult result; MarketplaceListingSyncStatusView status;
        await using (var db = Context()) result = await CreateService(db, settings, factory, reader).StartFullAsync(account, Guid.NewGuid());
        await using (var db = Context()) status = await CreateService(db, settings, factory, reader).GetStatusAsync(account);
        result.Succeeded.Should().BeFalse(); result.Code.Should().Be(code); result.Reason.Should().Be(reason);
        if (reason == "RATE_LIMITED") result.RetryAfterUntil.Should().Be(DateTimeOffset.Parse("2099-01-01T00:00:00Z"));
        if (active is not null) result.ActiveRunId.Should().Be(active);
        status.Availability.CanStart.Should().BeFalse(); status.Availability.BlockedReason.Should().Be(reason, "availability comes from the same evaluator as start");
        if (active is not null) status.Availability.ActiveRunId.Should().Be(active);
        (await RunCountAsync()).Should().Be(baselineRuns); (await AuditCountAsync()).Should().Be(0); recorder.Triggered.Should().BeEmpty();
    }

    // ---------- T06: active-run arbiter ----------

    private async Task<(Task<MarketplaceListingSyncRequestResult> Request, Guid Winner)> StartLosingTheRaceAsync(Action onFreshScope)
    {
        // A competing uncommitted QUEUED run is invisible to the pre-check; the service insert blocks on the partial unique index until it commits.
        var account = _raceAccount = await SeedAccountAsync();
        var competing = Context("b5-race-competitor"); var tx = await competing.Database.BeginTransactionAsync();
        var winner = (await new MarketplaceListingSyncRunKernel(competing).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow)).Id;
        var request = Task.Run(async () => { await using var db = Context("b5-race-service"); return await CreateService(db, onFreshScope: onFreshScope).StartFullAsync(account, Guid.NewGuid()); });
        await WaitUntilAsync(() => request.IsCompleted || LockWaitObserved("b5-race-service"), "service request never blocked on the unique index");
        request.IsCompleted.Should().BeFalse("the request must have blocked on the competing uncommitted run");
        await tx.CommitAsync(); await tx.DisposeAsync(); await competing.DisposeAsync();
        return (request, winner);
    }

    private Guid _raceAccount;

    private bool LockWaitObserved(string application)
    {
        using var observer = new NpgsqlConnection(_connectionString); observer.Open();
        using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name = @a AND wait_event_type = 'Lock')", observer); command.Parameters.AddWithValue("a", application);
        return (bool)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task T06_active_run_race_is_reread_from_a_fresh_scope_and_reports_the_winner()
    {
        var scopes = 0; var (request, winner) = await StartLosingTheRaceAsync(() => Interlocked.Increment(ref scopes));
        var result = await WithSafetyTimeout(request);
        result.Code.Should().Be("LISTING_SYNC_ALREADY_ACTIVE"); result.Reason.Should().Be("SYNC_ACTIVE"); result.ActiveRunId.Should().Be(winner); scopes.Should().Be(1, "the winner is re-read through exactly one fresh scope");
        (await AuditCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task T06_active_run_race_rethrows_the_original_violation_when_the_winner_already_terminalized()
    {
        Guid winnerId = Guid.Empty;
        var (request, winner) = await StartLosingTheRaceAsync(() =>
        {
            using var connection = new NpgsqlConnection(_connectionString); connection.Open();
            using var command = new NpgsqlCommand("UPDATE commerce.marketplace_listing_sync_run SET status='FAILED', safe_result_code='TEST_DONE', finished_at=now(), lease_token=NULL, lease_until=NULL WHERE marketplace_account_id=@a", connection);
            command.Parameters.AddWithValue("a", _raceAccount); command.ExecuteNonQuery();
        });
        winnerId = winner; winnerId.Should().NotBeEmpty();
        (await Record.ExceptionAsync(() => request)).Should().BeOfType<DbUpdateException>("no active winner remains, so no invented result is returned");
    }

    [Fact]
    public async Task T06_a_unique_violation_on_another_constraint_is_never_treated_as_an_active_run()
    {
        var account = await SeedAccountAsync();
        await using (var ddl = Context())
        {
            await ddl.Database.ExecuteSqlRawAsync("CREATE FUNCTION test_b5_other_unique() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.entity_table = 'marketplace_listing_sync_run' THEN RAISE EXCEPTION 'duplicate' USING ERRCODE = '23505', CONSTRAINT = 'ux_some_other_constraint'; END IF; RETURN NEW; END; $$");
            await ddl.Database.ExecuteSqlRawAsync("CREATE TRIGGER test_b5_other_unique_trigger AFTER INSERT ON platform.audit_log FOR EACH ROW EXECUTE FUNCTION test_b5_other_unique()");
        }
        Exception? thrown; await using (var db = Context()) thrown = await Record.ExceptionAsync(() => CreateService(db).StartFullAsync(account, Guid.NewGuid()));
        thrown.Should().BeOfType<DbUpdateException>(); ((PostgresException)thrown!.InnerException!).ConstraintName.Should().Be("ux_some_other_constraint");
        (await RunCountAsync()).Should().Be(0); (await AuditCountAsync()).Should().Be(0);
    }

    // ---------- T07: best-effort dispatch trigger ----------

    [Fact]
    public async Task T07_trigger_fires_exactly_once_after_commit_with_the_canonical_job_key()
    {
        var account = await SeedAccountAsync(); var (recorder, factory) = NewScheduler(); recorder.ObserveRuns = () => { using var db = Context(); return db.Set<MarketplaceListingSyncRun>().Count(); };
        MarketplaceListingSyncRequestResult result; await using (var db = Context()) result = await CreateService(db, new MarketplaceListingSyncOptions { SchedulingEnabled = true }, factory).StartFullAsync(account, Guid.NewGuid());
        result.Succeeded.Should().BeTrue(); recorder.Triggered.Should().ContainSingle().Which.Should().Be(MarketplaceListingSyncDispatchJobKeys.Job);
        recorder.RunsVisibleAtTrigger.Should().Be(1, "the trigger runs after the run + audit unit of work committed");
        MarketplaceListingSyncDispatchJobKeys.Job.Name.Should().Be("MarketplaceListingSyncDispatchJob"); MarketplaceListingSyncDispatchJobKeys.Job.Group.Should().Be("marketplaces");
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("cancelled")]
    public async Task T07_scheduler_failure_or_cancellation_after_commit_still_returns_created_with_one_audit(string failure)
    {
        var account = await SeedAccountAsync(); var (recorder, factory) = NewScheduler(); recorder.ThrowOnTrigger = failure == "throws" ? new InvalidOperationException("scheduler down") : new OperationCanceledException();
        MarketplaceListingSyncRequestResult result; await using (var db = Context()) result = await CreateService(db, new MarketplaceListingSyncOptions { SchedulingEnabled = true }, factory).StartFullAsync(account, Guid.NewGuid());
        result.Succeeded.Should().BeTrue(); recorder.Triggered.Should().HaveCount(1);
        await using var verify = Context(); var run = await verify.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(); run.Status.Should().Be(MarketplaceListingSyncStatus.QUEUED);
        (await AuditEventsAsync(run.Id)).Should().Equal("LISTING_SYNC_REQUESTED");
    }

    [Fact]
    public async Task T07_no_trigger_for_refusal_rollback_disabled_scheduling_or_an_unregistered_job()
    {
        var account = await SeedAccountAsync(); var (recorder, factory) = NewScheduler(); var enabled = new MarketplaceListingSyncOptions { SchedulingEnabled = true };
        // disabled scheduling
        await using (var db = Context()) (await CreateService(db, new MarketplaceListingSyncOptions { SchedulingEnabled = false }, factory).StartFullAsync(account, Guid.NewGuid())).Succeeded.Should().BeTrue();
        recorder.Triggered.Should().BeEmpty();
        await using (var db = Context()) await db.Database.ExecuteSqlRawAsync("UPDATE commerce.marketplace_listing_sync_run SET status='FAILED', safe_result_code='TEST_DONE', finished_at=now(), lease_token=NULL, lease_until=NULL");
        // unregistered job
        recorder.Registered = false;
        await using (var db = Context()) (await CreateService(db, enabled, factory).StartFullAsync(account, Guid.NewGuid())).Succeeded.Should().BeTrue();
        recorder.Triggered.Should().BeEmpty(); recorder.Registered = true;
        await using (var db = Context()) await db.Database.ExecuteSqlRawAsync("UPDATE commerce.marketplace_listing_sync_run SET status='FAILED', safe_result_code='TEST_DONE', finished_at=now(), lease_token=NULL, lease_until=NULL");
        // refusal
        await using (var db = Context()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account SET active = false WHERE id = {account}");
        await using (var db = Context()) (await CreateService(db, enabled, factory).StartFullAsync(account, Guid.NewGuid())).Succeeded.Should().BeFalse();
        recorder.Triggered.Should().BeEmpty();
        // rollback
        await using (var db = Context()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account SET active = true WHERE id = {account}");
        await InstallAuditFailureTriggerAsync();
        await using (var db = Context()) (await Record.ExceptionAsync(() => CreateService(db, enabled, factory).StartFullAsync(account, Guid.NewGuid()))).Should().NotBeNull();
        recorder.Triggered.Should().BeEmpty();
    }

    // ---------- T08: B6 options ----------

    [Fact]
    public async Task T08_max_attempts_option_is_applied_to_full_and_failed_only_runs()
    {
        var (_, originalId) = await BuildPartialOriginalAsync();
        await using (var db = Context()) await db.Database.ExecuteSqlRawAsync("UPDATE commerce.marketplace_listing_sync_run SET status='FAILED', safe_result_code='TEST_DONE', finished_at=now(), lease_token=NULL, lease_until=NULL WHERE status IN ('QUEUED','RUNNING')");
        var settings = new MarketplaceListingSyncOptions { MaxAttempts = 7 }; MarketplaceListingSyncRequestResult retry; await using (var db = Context()) retry = await CreateService(db, settings).RetryAsync(originalId, Guid.NewGuid());
        await using var verify = Context(); (await verify.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == retry.RunId)).MaxAttempts.Should().Be(7);
        var second = await SeedAccountAsync(); MarketplaceListingSyncRequestResult full; await using (var db = Context()) full = await CreateService(db, new MarketplaceListingSyncOptions { MaxAttempts = 5 }).StartFullAsync(second, Guid.NewGuid());
        (await verify.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == full.RunId)).MaxAttempts.Should().Be(5);
    }

    [Fact]
    public async Task T08_lease_seconds_option_sets_the_claim_lease()
    {
        var account = await SeedAccountAsync(); var runId = await CreateRunAtAsync(account, _clock.UtcNow); var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MarketplaceListingSyncExecutionTestHooks.BeforeFinishAsync = async _ => { parked.TrySetResult(); await release.Task; };
        try
        {
            var task = Task.Run(async () => { await using var db = Context(); return await CreateExecution(db, new ScriptedReader(["MLB-LEASE-OPT"], _clock), settings: new MarketplaceListingSyncOptions { LeaseSeconds = 300 }).RunAsync(runId); });
            await WithSafetyTimeout(parked.Task, "never reached the pre-finish barrier");
            await using (var verify = Context()) (await verify.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId)).LeaseUntil.Should().Be(_clock.UtcNow.AddSeconds(300));
            release.TrySetResult(); (await WithSafetyTimeout(task))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        }
        finally { MarketplaceListingSyncExecutionTestHooks.BeforeFinishAsync = null; release.TrySetResult(); }
    }

    [Theory]
    [InlineData("DispatchIntervalSeconds", 9)] [InlineData("DispatchIntervalSeconds", 3601)]
    [InlineData("MaxConcurrentRuns", 0)] [InlineData("MaxConcurrentRuns", 9)]
    [InlineData("MaxInFlightRequestsPerProvider", 0)] [InlineData("MaxInFlightRequestsPerProvider", 9)]
    [InlineData("MaxAttempts", 0)] [InlineData("MaxAttempts", 11)]
    [InlineData("LeaseSeconds", 29)] [InlineData("LeaseSeconds", 901)]
    [InlineData("DetailBatchSize", 0)] [InlineData("DetailBatchSize", 21)]
    public async Task T08_out_of_range_listing_sync_options_fail_startup_validation(string key, int value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"Marketplaces:ListingSync:{key}"] = value.ToString() }).Build();
        var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddOptions<MarketplaceListingSyncOptions>().Bind(configuration.GetSection(MarketplaceListingSyncOptions.SectionName)).ValidateOnStart();
            services.AddSingleton<IValidateOptions<MarketplaceListingSyncOptions>, MarketplaceListingSyncOptionsValidator>();
        }).Build();
        (await Record.ExceptionAsync(() => host.StartAsync())).Should().BeOfType<OptionsValidationException>().Which.Message.Should().Contain(key);
        new MarketplaceListingSyncOptionsValidator().Validate(null, new MarketplaceListingSyncOptions()).Succeeded.Should().BeTrue("the frozen defaults are valid");
        await Task.CompletedTask;
    }

    // ---------- T09: dispatcher ----------

    [Fact]
    public async Task T09_dispatcher_claims_queued_runs_by_requested_at_up_to_the_free_capacity()
    {
        var a = await SeedAccountAsync(); var b = await SeedAccountAsync(); var c = await SeedAccountAsync();
        var t0 = _clock.UtcNow; var runA = await CreateRunAtAsync(a, t0.AddSeconds(2)); var runB = await CreateRunAtAsync(b, t0); var runC = await CreateRunAtAsync(c, t0.AddSeconds(1));
        var reader = new BlockingReader(); var settings = new MarketplaceListingSyncOptions { MaxConcurrentRuns = 2 }; var (executor, _) = CreateHost(reader, settings);
        try
        {
            await using (var db = Context()) (await CreateDispatcher(db, executor, settings).RunOnceAsync(CancellationToken.None)).Should().Be(2);
            await WaitUntilAsync(() => reader.Entered == 2, "both claimed runs should reach the provider");
            await using (var verify = Context())
            {
                (await verify.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runB)).Status.Should().Be(MarketplaceListingSyncStatus.RUNNING);
                (await verify.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runC)).Status.Should().Be(MarketplaceListingSyncStatus.RUNNING);
                (await verify.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runA)).Status.Should().Be(MarketplaceListingSyncStatus.QUEUED, "the latest requested_at is not claimed beyond capacity");
            }
            executor.Executing.Should().Be(2);
            await using (var db = Context()) (await CreateDispatcher(db, executor, settings).RunOnceAsync(CancellationToken.None)).Should().Be(0, "no free capacity");
        }
        finally { reader.Release(); await executor.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task T09_dispatcher_reclaims_an_expired_lease_with_a_new_token()
    {
        var account = await SeedAccountAsync(); var runId = await CreateRunAtAsync(account, _clock.UtcNow); MarketplaceListingSyncLease first;
        await using (var db = Context()) first = (await new MarketplaceListingSyncLeaseAuthority(db).ClaimAsync(runId, _clock.UtcNow, TimeSpan.FromMinutes(2)))!;
        _clock.UtcNow = _clock.UtcNow.AddMinutes(3); var reader = new BlockingReader(); var settings = new MarketplaceListingSyncOptions(); var (executor, _) = CreateHost(reader, settings);
        try
        {
            await using (var db = Context()) (await CreateDispatcher(db, executor, settings).RunOnceAsync(CancellationToken.None)).Should().Be(1);
            await using var verify = Context(); var run = await verify.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId);
            run.LeaseToken.Should().NotBe(first.LeaseToken).And.NotBeNull(); run.AttemptCount.Should().Be(2); run.Status.Should().Be(MarketplaceListingSyncStatus.RUNNING);
        }
        finally { reader.Release(); await executor.StopAsync(CancellationToken.None); }
    }

    private async Task<(Guid Account, Guid RunId, Guid Token)> ExhaustedRunAsync()
    {
        var account = await SeedAccountAsync(); var runId = await CreateRunAtAsync(account, _clock.UtcNow); MarketplaceListingSyncLease lease;
        await using (var db = Context())
        {
            lease = (await new MarketplaceListingSyncLeaseAuthority(db).ClaimAsync(runId, _clock.UtcNow, TimeSpan.FromMinutes(2)))!;
            var kernel = new MarketplaceListingSyncRunKernel(db); await kernel.RegisterTargetsWithLeaseAsync(runId, lease.LeaseToken, ["MLB-EX-1", "MLB-EX-2"], MarketplaceListingSyncItemOrigin.ENUMERATED, _clock.UtcNow, TimeSpan.FromMinutes(2));
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_listing_sync_run SET attempt_count = max_attempts WHERE id = {runId}");
        }
        _clock.UtcNow = _clock.UtcNow.AddMinutes(3); return (account, runId, lease.LeaseToken);
    }

    [Fact]
    public async Task T09_exhausted_run_is_terminalized_with_its_pending_items_and_exactly_one_finished_audit()
    {
        var (_, runId, _) = await ExhaustedRunAsync(); var settings = new MarketplaceListingSyncOptions(); var (executor, _) = CreateHost(new BlockingReader(), settings);
        await using (var db = Context()) (await CreateDispatcher(db, executor, settings).RunOnceAsync(CancellationToken.None)).Should().Be(0, "an exhausted run is never claimed");
        await using var verify = Context(); var run = await verify.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId);
        run.Status.Should().Be(MarketplaceListingSyncStatus.FAILED); run.SafeResultCode.Should().Be("RUN_EXECUTION_ATTEMPTS_EXHAUSTED"); run.LeaseToken.Should().BeNull(); run.LeaseUntil.Should().BeNull(); run.FinishedAt.Should().NotBeNull();
        (await verify.Set<MarketplaceListingSyncRunItem>().Where(x => x.SyncRunId == runId).ToListAsync()).Should().OnlyContain(x => x.Outcome == MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR && x.SafeErrorCode == "RUN_ENDED_BEFORE_ITEM_PROCESSED");
        var rows = await AuditRowsAsync(runId); rows.Should().ContainSingle(); rows[0].Source.Should().Be(Verce.Platform.UnitOfWork.AuditSource.Job);
        using var doc = JsonDocument.Parse(rows[0].NewValuesJson!); doc.RootElement.GetProperty("event").GetString().Should().Be("LISTING_SYNC_FINISHED"); doc.RootElement.GetProperty("status").GetString().Should().Be("FAILED"); doc.RootElement.GetProperty("safeResultCode").GetString().Should().Be("RUN_EXECUTION_ATTEMPTS_EXHAUSTED");
        var counters = doc.RootElement.GetProperty("counters"); counters.GetProperty("total").GetInt32().Should().Be(2); counters.GetProperty("pending").GetInt32().Should().Be(0); counters.GetProperty("blockingRetryable").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task T09_run_reclaimed_between_read_and_compare_and_swap_is_not_terminalized_and_not_audited()
    {
        var (_, runId, _) = await ExhaustedRunAsync(); var settings = new MarketplaceListingSyncOptions(); var (executor, _) = CreateHost(new BlockingReader(), settings); var newToken = Guid.NewGuid();
        MarketplaceListingSyncExecutionTestHooks.BeforeExhaustedTerminalizationAsync = async (_, _) => { await using var db = Context(); await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_listing_sync_run SET lease_token = {newToken} WHERE id = {runId}"); };
        try { await using var db = Context(); await CreateDispatcher(db, executor, settings).RunOnceAsync(CancellationToken.None); }
        finally { MarketplaceListingSyncExecutionTestHooks.BeforeExhaustedTerminalizationAsync = null; }
        await using var verify = Context(); var run = await verify.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId);
        run.Status.Should().Be(MarketplaceListingSyncStatus.RUNNING); run.LeaseToken.Should().Be(newToken, "the compare-and-swap is fenced by the stale token the dispatcher observed, even when the new holder's lease has also lapsed");
        (await verify.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.SyncRunId == runId && x.Outcome == MarketplaceListingSyncItemOutcome.PENDING)).Should().Be(2);
        (await AuditRowsAsync(runId)).Should().BeEmpty();
    }

    [Fact]
    public async Task T09_one_runs_claim_failure_does_not_stop_the_other_runs_and_the_job_forbids_concurrent_execution()
    {
        var a = await SeedAccountAsync(); var b = await SeedAccountAsync(); var t0 = _clock.UtcNow; var runA = await CreateRunAtAsync(a, t0); var runB = await CreateRunAtAsync(b, t0.AddSeconds(1));
        await using (var ddl = Context())
        {
#pragma warning disable EF1002 // runA is a locally generated Guid; PostgreSQL DDL cannot parameterize a function body.
            await ddl.Database.ExecuteSqlRawAsync($"CREATE FUNCTION test_b5_claim_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.id = '{runA}' AND NEW.status = 'RUNNING' THEN RAISE EXCEPTION 'test claim failure' USING ERRCODE = 'P0001'; END IF; RETURN NEW; END; $$");
#pragma warning restore EF1002
            await ddl.Database.ExecuteSqlRawAsync("CREATE TRIGGER test_b5_claim_fail_trigger BEFORE UPDATE ON commerce.marketplace_listing_sync_run FOR EACH ROW EXECUTE FUNCTION test_b5_claim_fail()");
        }
        var reader = new BlockingReader(); var settings = new MarketplaceListingSyncOptions(); var (executor, _) = CreateHost(reader, settings);
        try
        {
            await using (var db = Context()) (await CreateDispatcher(db, executor, settings).RunOnceAsync(CancellationToken.None)).Should().Be(1);
            await using var verify = Context();
            (await verify.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runA)).Status.Should().Be(MarketplaceListingSyncStatus.QUEUED);
            (await verify.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runB)).Status.Should().Be(MarketplaceListingSyncStatus.RUNNING);
        }
        finally { reader.Release(); await executor.StopAsync(CancellationToken.None); }
        typeof(MarketplaceListingSyncDispatchJob).GetCustomAttribute<DisallowConcurrentExecutionAttribute>().Should().NotBeNull();
        typeof(MarketplaceListingSyncDispatchJob).GetInterfaces().Should().Contain(typeof(IJob));
    }

    // ---------- T10: executor host ----------

    [Fact]
    public async Task T10_executor_runs_two_accounts_concurrently_in_separate_scopes_and_rejects_a_duplicate_run()
    {
        var a = await SeedAccountAsync(); var b = await SeedAccountAsync(); var runA = await CreateRunAtAsync(a, _clock.UtcNow); var runB = await CreateRunAtAsync(b, _clock.UtcNow);
        MarketplaceListingSyncLease leaseA, leaseB; await using (var db = Context()) { var authority = new MarketplaceListingSyncLeaseAuthority(db); leaseA = (await authority.ClaimAsync(runA, _clock.UtcNow, TimeSpan.FromMinutes(2)))!; leaseB = (await authority.ClaimAsync(runB, _clock.UtcNow, TimeSpan.FromMinutes(2)))!; }
        var reader = new BlockingReader(); var (executor, contexts) = CreateHost(reader, new MarketplaceListingSyncOptions { MaxInFlightRequestsPerProvider = 8 });
        try
        {
            executor.TryStart(leaseA).Should().BeTrue(); executor.TryStart(leaseB).Should().BeTrue(); executor.TryStart(leaseA).Should().BeFalse("the same RunId is already executing in this process");
            await WaitUntilAsync(() => reader.Entered == 2, "both executions should be inside the provider call"); reader.Max.Should().Be(2); executor.Executing.Should().Be(2);
            lock (contexts) contexts.Distinct().Should().HaveCount(2, "each execution has its own scope and context");
        }
        finally { reader.Release(); await executor.StopAsync(CancellationToken.None); }
        executor.Executing.Should().Be(0);
    }

    [Fact]
    public async Task T10_stopping_the_host_leaves_a_parked_run_running_without_a_finished_audit_and_reclaimable()
    {
        var account = await SeedAccountAsync(); var runId = await CreateRunAtAsync(account, _clock.UtcNow); MarketplaceListingSyncLease lease;
        await using (var db = Context()) lease = (await new MarketplaceListingSyncLeaseAuthority(db).ClaimAsync(runId, _clock.UtcNow, TimeSpan.FromMinutes(2)))!;
        var reader = new BlockingReader(); var (executor, _) = CreateHost(reader, new MarketplaceListingSyncOptions()); executor.TryStart(lease).Should().BeTrue();
        await WaitUntilAsync(() => reader.Entered == 1, "execution should park in the provider call"); await executor.StopAsync(CancellationToken.None);
        executor.Executing.Should().Be(0); executor.TryStart(lease).Should().BeFalse("a stopped host starts nothing");
        await using (var verify = Context()) { var run = await verify.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId); run.Status.Should().Be(MarketplaceListingSyncStatus.RUNNING); run.LeaseToken.Should().Be(lease.LeaseToken); }
        (await AuditRowsAsync(runId)).Should().BeEmpty("host stop never fabricates a FINISHED row");
        _clock.UtcNow = _clock.UtcNow.AddMinutes(3); await using (var db = Context()) (await CreateExecution(db, new ScriptedReader(["MLB-RECLAIM"], _clock)).RunAsync(runId))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
    }

    [Fact]
    public async Task T10_a_lease_lost_execution_does_not_crash_the_host()
    {
        var account = await SeedAccountAsync(); var runId = await CreateRunAtAsync(account, _clock.UtcNow); MarketplaceListingSyncLease lease;
        await using (var db = Context()) lease = (await new MarketplaceListingSyncLeaseAuthority(db).ClaimAsync(runId, _clock.UtcNow, TimeSpan.FromMinutes(2)))!;
        await using (var db = Context()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_listing_sync_run SET lease_token = {Guid.NewGuid()} WHERE id = {runId}");
        var (executor, _) = CreateHost(new ScriptedReader([], _clock), new MarketplaceListingSyncOptions());
        executor.TryStart(lease).Should().BeTrue(); await WaitUntilAsync(() => executor.Executing == 0, "the lease-lost execution should end");
        executor.TryStart(lease).Should().BeTrue("the host is still alive and accepts work"); await executor.StopAsync(CancellationToken.None);
        (await AuditRowsAsync(runId)).Should().BeEmpty();
    }

    // ---------- T11: scheduling registration ----------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T11_scheduling_flag_controls_the_quartz_job_and_trigger_registration(bool enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Marketplaces:ListingSync:SchedulingEnabled"] = enabled.ToString(), ["Marketplaces:ListingSync:DispatchIntervalSeconds"] = "45" }).Build();
        var services = new ServiceCollection(); services.AddVerceMarketplaceListingSyncScheduling(configuration);
        if (!enabled) { services.Any(d => d.ServiceType == typeof(ISchedulerFactory)).Should().BeFalse("no Quartz job/trigger is registered when scheduling is disabled"); return; }
        var quartz = services.BuildServiceProvider().GetRequiredService<IOptions<QuartzOptions>>().Value;
        quartz.JobDetails.Should().ContainSingle(j => j.Key.Equals(MarketplaceListingSyncDispatchJobKeys.Job) && j.Durable && j.ConcurrentExecutionDisallowed);
        var trigger = quartz.Triggers.Should().ContainSingle(t => t.JobKey.Equals(MarketplaceListingSyncDispatchJobKeys.Job)).Subject; trigger.Should().BeAssignableTo<ISimpleTrigger>().Which.RepeatInterval.Should().Be(TimeSpan.FromSeconds(45));
        await Task.CompletedTask;
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test"; public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider(); public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Production"; public string ContentRootPath { get; set; } = ""; public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public async Task T11_executor_dispatcher_service_trigger_and_limiter_are_registered_without_requiring_the_real_provider_or_scheduling()
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddVerceMarketplaceInfrastructure(new ConfigurationBuilder().Build(), new TestEnvironment());
        bool Has(Type type, ServiceLifetime lifetime) => services.Any(d => d.ServiceType == type && d.Lifetime == lifetime);
        Has(typeof(MarketplaceListingSyncExecutor), ServiceLifetime.Singleton).Should().BeTrue(); Has(typeof(MarketplaceListingSyncDispatcher), ServiceLifetime.Scoped).Should().BeTrue();
        Has(typeof(MarketplaceListingSyncService), ServiceLifetime.Scoped).Should().BeTrue(); Has(typeof(MarketplaceListingSyncDispatchTrigger), ServiceLifetime.Singleton).Should().BeTrue();
        Has(typeof(MarketplaceProviderInFlightLimiter), ServiceLifetime.Singleton).Should().BeTrue(); Has(typeof(IHostedService), ServiceLifetime.Singleton).Should().BeTrue();
        services.Any(d => d.ServiceType == typeof(ISchedulerFactory)).Should().BeFalse("without the ML-enabled composition no listing-sync Quartz job exists");
        await Task.CompletedTask;
    }

    // ---------- T12: per-provider in-flight limiter ----------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task T12_in_flight_limit_bounds_simultaneous_provider_calls_per_provider(int limit)
    {
        var a = await SeedAccountAsync(); var b = await SeedAccountAsync(); var runA = await CreateRunAtAsync(a, _clock.UtcNow); var runB = await CreateRunAtAsync(b, _clock.UtcNow);
        var settings = new MarketplaceListingSyncOptions { MaxInFlightRequestsPerProvider = limit }; var limiter = new MarketplaceProviderInFlightLimiter(Options.Create(settings)); var reader = new BlockingReader();
        await using var dbA = Context(); await using var dbB = Context();
        var taskA = Task.Run(() => CreateExecution(dbA, reader, settings: settings, limiter: limiter).RunAsync(runA)); var taskB = Task.Run(() => CreateExecution(dbB, reader, settings: settings, limiter: limiter).RunAsync(runB));
        try
        {
            if (limit == 1)
            {
                await WaitUntilAsync(() => reader.Entered == 1 && limiter.Waiting == 1, "one execution should be in the provider call while the other waits for a slot");
                reader.Entered.Should().Be(1); reader.Max.Should().Be(1);
                reader.Release(); await Task.WhenAll(taskA, taskB).WaitAsync(TimeSpan.FromSeconds(60)); reader.Max.Should().Be(1, "never more than one simultaneous send with a limit of one");
            }
            else
            {
                await WaitUntilAsync(() => reader.Entered == 2, "both executions may be in the provider call with a limit of two"); reader.Max.Should().Be(2);
                reader.Release(); await Task.WhenAll(taskA, taskB).WaitAsync(TimeSpan.FromSeconds(60));
            }
        }
        finally { reader.Release(); }
    }

    // ---------- T13 / T14 / T15: read projections ----------

    private async Task<Dictionary<string, int>> SqlGroupAsync(string column, Guid runId)
    {
        await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT {column}, count(*) FROM commerce.marketplace_listing_sync_run_item WHERE sync_run_id = @r GROUP BY 1", connection); command.Parameters.AddWithValue("r", runId);
        var result = new Dictionary<string, int>(); await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) result[reader.GetString(0)] = (int)reader.GetInt64(1); return result;
    }

    [Fact]
    public async Task T13_get_run_counters_equal_a_direct_grouping_and_the_projection_hides_internal_fields()
    {
        var (_, originalId) = await BuildPartialOriginalAsync(); _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        MarketplaceListingSyncRequestResult retry; await using (var db = Context()) retry = await CreateService(db).RetryAsync(originalId, Guid.NewGuid());
        foreach (var runId in new[] { originalId, retry.RunId!.Value })
        {
            MarketplaceListingSyncRunDetail detail; await using (var db = Context()) detail = await CreateService(db).GetRunAsync(runId);
            detail.Counters.ByOutcome.Should().BeEquivalentTo(await SqlGroupAsync("outcome", runId)); detail.Counters.ByOrigin.Should().BeEquivalentTo(await SqlGroupAsync("origin", runId));
            detail.Counters.Total.Should().Be(detail.Counters.ByOutcome.Values.Sum());
            detail.Counters.Found.Should().Be(detail.Counters.ByOutcome.GetValueOrDefault("FOUND"));
            detail.Counters.BlockingRetryable.Should().Be(new[] { "NOT_FOUND", "ACCESS_DENIED", "TRANSIENT_ERROR" }.Sum(k => detail.Counters.ByOutcome.GetValueOrDefault(k)));
            detail.Counters.BlockingNonRetryable.Should().Be(new[] { "SELLER_MISMATCH", "PERMANENT_ERROR" }.Sum(k => detail.Counters.ByOutcome.GetValueOrDefault(k)));
            detail.Counters.Pending.Should().Be(detail.Counters.ByOutcome.GetValueOrDefault("PENDING"));
            await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT count(*) FILTER (WHERE listing_created), count(*) FILTER (WHERE observation_appended) FROM commerce.marketplace_listing_sync_run_item WHERE sync_run_id = @r", connection); command.Parameters.AddWithValue("r", runId);
            await using var reader = await command.ExecuteReaderAsync(); (await reader.ReadAsync()).Should().BeTrue(); detail.Counters.ListingsCreated.Should().Be((int)reader.GetInt64(0)); detail.Counters.ObservationsAppended.Should().Be((int)reader.GetInt64(1));
        }
        MarketplaceListingSyncRunDetail original; await using (var db = Context()) original = await CreateService(db).GetRunAsync(originalId);
        original.Counters.Total.Should().Be(4); original.Counters.Found.Should().Be(1); original.Counters.BlockingRetryable.Should().Be(2); original.Counters.BlockingNonRetryable.Should().Be(1); original.Counters.ListingsCreated.Should().Be(1);
        MarketplaceListingSyncRunDetail pending; await using (var db = Context()) pending = await CreateService(db).GetRunAsync(retry.RunId!.Value); pending.Counters.Pending.Should().Be(2); pending.Counters.ByOrigin["RETRY_TARGET"].Should().Be(2);

        var forbidden = new[] { "LeaseToken", "LeaseUntil", "HeartbeatAt", "ConfirmedOperationIdAtStart", "ProviderErrorCode", "LastFailureClassification", "ProviderCode", "CredentialReference", "RequestedByUserId", "MaxAttempts" };
        var names = new HashSet<string>(); var pending2 = new Queue<Type>([typeof(MarketplaceListingSyncRunDetail), typeof(MarketplaceListingSyncItemView), typeof(MarketplaceListingSyncStatusView)]); var seen = new HashSet<Type>();
        while (pending2.Count > 0)
        {
            var type = pending2.Dequeue(); if (!seen.Add(type)) continue;
            foreach (var property in type.GetProperties()) { names.Add(property.Name); if (property.PropertyType.Namespace?.StartsWith("Verce.Infrastructure") == true) pending2.Enqueue(property.PropertyType); }
        }
        names.Intersect(forbidden).Should().BeEmpty();
    }

    [Fact]
    public async Task T14_get_items_normalizes_paging_filters_orders_by_external_id_and_flags_retryable()
    {
        var account = await SeedAccountAsync(); var runId = await CreateRunAtAsync(account, _clock.UtcNow);
        await using (var db = Context())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO commerce.marketplace_listing_sync_run_item (id,sync_run_id,external_listing_id,origin,outcome,created_at,updated_at,attempt_count,listing_created,observation_appended)
                SELECT gen_random_uuid(),{runId},'MLB-IT-' || lpad(n::text,3,'0'),'ENUMERATED','PENDING',{_clock.UtcNow},{_clock.UtcNow},0,false,false FROM generate_series(1,150) AS n
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_listing_sync_run_item SET outcome='TRANSIENT_ERROR', safe_error_code='LISTING_READ_FAILED', completed_at={_clock.UtcNow}, attempt_count=1 WHERE sync_run_id={runId} AND external_listing_id <= 'MLB-IT-010'");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_listing_sync_run_item SET outcome='PERMANENT_ERROR', safe_error_code='LISTING_RESPONSE_INVALID', completed_at={_clock.UtcNow}, attempt_count=1 WHERE sync_run_id={runId} AND external_listing_id = 'MLB-IT-011'");
        }
        async Task<MarketplaceListingSyncPage<MarketplaceListingSyncItemView>> Page(MarketplaceListingSyncItemOutcome? outcome, int page, int size) { await using var db = Context(); return await CreateService(db).GetItemsAsync(runId, outcome, page, size); }
        var zero = await Page(null, 1, 0); zero.PageSize.Should().Be(1); zero.Items.Should().HaveCount(1); var big = await Page(null, 1, 500); big.PageSize.Should().Be(100); big.Items.Should().HaveCount(100); big.Total.Should().Be(150);
        (await Page(null, 0, 10)).Page.Should().Be(1); (await Page(null, -3, 10)).Items[0].ExternalListingId.Should().Be("MLB-IT-001");
        var transient = await Page(MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR, 1, 100); transient.Total.Should().Be(10); transient.Items.Should().OnlyContain(x => x.Retryable && x.SafeErrorCode == "LISTING_READ_FAILED");
        var permanent = await Page(MarketplaceListingSyncItemOutcome.PERMANENT_ERROR, 1, 100); permanent.Items.Should().ContainSingle().Which.Retryable.Should().BeFalse();
        (await Page(MarketplaceListingSyncItemOutcome.PENDING, 1, 100)).Items.Should().OnlyContain(x => !x.Retryable);
        var first = await Page(null, 1, 100); var second = await Page(null, 2, 100); second.Items.Should().HaveCount(50);
        var all = first.Items.Concat(second.Items).Select(x => x.ExternalListingId).ToList(); all.Should().OnlyHaveUniqueItems().And.HaveCount(150).And.Equal(Enumerable.Range(1, 150).Select(n => $"MLB-IT-{n:000}"));
    }

    [Fact]
    public async Task T15_status_health_matrix_and_availability_follow_the_run_history()
    {
        var account = await SeedAccountAsync(); var actor = Guid.NewGuid();
        async Task<MarketplaceListingSyncStatusView> Status() { await using var db = Context(); return await CreateService(db).GetStatusAsync(account); }
        async Task<Guid> Start() { await using var db = Context(); return (await CreateService(db).StartFullAsync(account, actor)).RunId!.Value; }
        async Task<MarketplaceListingSyncRun> Execute(Guid id, ScriptedReader reader) { await using var db = Context(); return (await CreateExecution(db, reader).RunAsync(id))!; }
        ScriptedReader Reader(bool failBad) { var reader = new ScriptedReader(["MLB-ST-OK", "MLB-ST-BAD"], _clock); if (failBad) reader.Outcomes["MLB-ST-BAD"] = MarketplaceListingReadOutcome.FAILED; return reader; }

        var never = await Status(); never.Summary.Health.Should().Be(MarketplaceListingSyncHealth.NEVER_RUN); never.Availability.CanStart.Should().BeTrue(); never.Availability.BlockedReason.Should().BeNull(); never.Summary.LatestRun.Should().BeNull();

        var full1 = await Start(); var queued = await Status();
        queued.Summary.Health.Should().Be(MarketplaceListingSyncHealth.IN_PROGRESS); queued.Summary.ActiveRun!.RunId.Should().Be(full1); queued.Availability.CanStart.Should().BeFalse(); queued.Availability.BlockedReason.Should().Be("SYNC_ACTIVE"); queued.Availability.ActiveRunId.Should().Be(full1);

        (await Execute(full1, Reader(true))).Status.Should().Be(MarketplaceListingSyncStatus.PARTIAL);
        var attention = await Status(); attention.Summary.Health.Should().Be(MarketplaceListingSyncHealth.ATTENTION_REQUIRED); attention.Summary.OpenBlockingItemCount.Should().Be(1); attention.Summary.LastSucceededFullRunAt.Should().BeNull(); attention.Availability.CanStart.Should().BeTrue(); attention.Summary.ActiveRun.Should().BeNull();

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1); MarketplaceListingSyncRequestResult retry; await using (var db = Context()) retry = await CreateService(db).RetryAsync(full1, actor);
        var retrying = await Status(); retrying.Summary.Health.Should().Be(MarketplaceListingSyncHealth.IN_PROGRESS); retrying.Summary.ActiveRun!.Kind.Should().Be(MarketplaceListingSyncKind.FAILED_ONLY); retrying.Summary.LatestFullRun!.RunId.Should().Be(full1);
        (await Execute(retry.RunId!.Value, new ScriptedReader([], _clock))).Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        var healed = await Status(); healed.Summary.Health.Should().Be(MarketplaceListingSyncHealth.HEALTHY, "the PARTIAL full run has no open blocker after the same-root retry found its item"); healed.Summary.OpenBlockingItemCount.Should().Be(0); healed.Summary.LastSucceededFullRunAt.Should().BeNull("no FULL run has SUCCEEDED yet");

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1); var full2 = await Start(); var succeeded = await Execute(full2, Reader(false)); succeeded.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        var healthy = await Status(); healthy.Summary.Health.Should().Be(MarketplaceListingSyncHealth.HEALTHY); healthy.Summary.LastSucceededFullRunAt.Should().Be(succeeded.FinishedAt);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1); var full3 = await Start(); var failedReader = new ScriptedReader(["MLB-ST-OK", "MLB-ST-BAD"], _clock); failedReader.Outcomes["MLB-ST-OK"] = MarketplaceListingReadOutcome.FAILED; failedReader.Outcomes["MLB-ST-BAD"] = MarketplaceListingReadOutcome.FAILED;
        (await Execute(full3, failedReader)).Status.Should().Be(MarketplaceListingSyncStatus.FAILED);
        var failed = await Status(); failed.Summary.Health.Should().Be(MarketplaceListingSyncHealth.FAILED); failed.Summary.LastSucceededFullRunAt.Should().Be(succeeded.FinishedAt, "the latest SUCCEEDED full run is still full2"); failed.Summary.LatestRun!.RunId.Should().Be(full3);
    }
}
