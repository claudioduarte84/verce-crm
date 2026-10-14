using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Verce.Infrastructure.Marketplaces;
using Verce.Infrastructure.Marketplaces.MercadoLivre;
using Verce.Modules.Commerce;
using Verce.Platform.Audit;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.S8C2;

/// <summary>
/// M059 / Gate 058 E01 — the executed evidence the D1–D12 re-gate found missing for D3, D4, D5, D6, D7 and the
/// execution-side parts of D9. Every test drives the REAL <see cref="MarketplaceListingSyncExecution"/>,
/// real <see cref="MercadoLivreConnector"/> parsing (scripted HTTP, never the network), real
/// <see cref="MarketplaceListingSyncService"/> and a disposable Testcontainers PostgreSQL. Nothing here is a
/// mock of the code under proof: where a seam is faked it is the provider's wire (HTTP), the protected
/// credential store and the pagination reader, exactly as in the existing B2 suite.
/// </summary>
public sealed partial class MarketplaceListingSyncExecutionIntegrationTests
{
    // ======================================================================== shared provider fixture

    private static string ElementJson(string id, int statusCode, Action<JsonObject>? mutateBody = null)
    {
        var body = new JsonObject
        {
            ["id"] = id, ["seller_id"] = 777, ["site_id"] = "MLB", ["status"] = "paused", ["title"] = "Provider " + id, ["price"] = 12.34m, ["currency_id"] = "BRL",
            ["attributes"] = new JsonArray(new JsonObject { ["id"] = "SELLER_SKU", ["value_name"] = "SKU-" + id }),
            ["last_updated"] = "2026-09-01T10:00:00.000Z",
        };
        mutateBody?.Invoke(body);
        return new JsonObject { ["id"] = id, ["status_code"] = statusCode, ["body"] = statusCode == 200 ? body : new JsonObject { ["id"] = id } }.ToJsonString();
    }

    private static readonly string FrozenBulkAttributeString = "body.id,body.seller_id,body.site_id,body.title,body.price,body.currency_id,body.status,body.sub_status,body.permalink,body.last_updated,body.attributes,body.variations";

    /// <summary>Scripted Mercado Livre wire: records every /items/bulk request (ids + attributes) and answers each id with a configurable element.</summary>
    private sealed class BulkProvider
    {
        public List<string[]> BulkRequests { get; } = [];
        public List<string> Attributes { get; } = [];
        public List<string> OtherRequests { get; } = [];
        public Dictionary<string, int> RequestsPerId { get; } = new(StringComparer.Ordinal);
        /// <summary>Returns the raw JSON element for an id, or null for the default healthy element.</summary>
        public Func<string, string?>? Element { get; set; }
        /// <summary>M060: optional whole-response override for the n-th /items/bulk call (1-based); null falls through to the per-element script.</summary>
        public Func<int, HttpResponseMessage?>? WholeResponse { get; set; }
        public HttpResponseMessage Handle(HttpRequestMessage request)
        {
            if (request.RequestUri!.AbsolutePath != "/items/bulk") { OtherRequests.Add(request.RequestUri.PathAndQuery); return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }; }
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
            var ids = query["ids"]!.Split(',');
            BulkRequests.Add(ids); Attributes.Add(query["attributes"]!);
            foreach (var id in ids) RequestsPerId[id] = RequestsPerId.GetValueOrDefault(id) + 1;
            if (WholeResponse?.Invoke(BulkRequests.Count) is { } whole) return whole;
            var json = "[" + string.Join(",", ids.Select(id => Element?.Invoke(id) ?? ElementJson(id, 200))) + "]";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>One "host boot": brand-new DI container, DbContext, service, execution, connector and in-memory reader state over the SAME database.</summary>
    private sealed class Boot(ServiceProvider provider, IServiceScope scope, VerceDbContext db, MarketplaceListingSyncService service, MarketplaceListingSyncExecution execution, BulkProvider wire, ScriptedReader pages) : IAsyncDisposable
    {
        public VerceDbContext Db => db;
        public MarketplaceListingSyncService Service => service;
        public MarketplaceListingSyncExecution Execution => execution;
        public BulkProvider Wire => wire;
        public ScriptedReader Pages => pages;
        public async ValueTask DisposeAsync() { scope.Dispose(); await provider.DisposeAsync(); }
    }

    private Boot NewBoot(IReadOnlyList<string> enumeratedIds, Action<BulkProvider>? configure = null, int detailBatchSize = 20, ICommerceProductEligibilityReader? eligibility = null)
    {
        var wire = new BulkProvider(); configure?.Invoke(wire);
        var pages = new ScriptedReader(enumeratedIds, _clock);
        var connector = CreateRealConnector(wire.Handle, new BoundedLogProvider());
        var reader = new ConnectorDetailReader(pages, connector);
        var provider = new ServiceCollection().AddScoped(_ => Context()).BuildServiceProvider();
        var scope = provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VerceDbContext>();
        return new Boot(provider, scope, db, CreateService(db, reader: reader), CreateExecution(db, reader, detailBatchSize, eligibility), wire, pages);
    }

    private async Task<string> JsonAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var scalar = await command.ExecuteScalarAsync(); return scalar is null or DBNull ? "null" : Convert.ToString(scalar)!;
    }

    private Task<string> RunRowAsync(Guid runId) => JsonAsync("SELECT to_jsonb(r)::text FROM commerce.marketplace_listing_sync_run r WHERE id = @id", ("id", runId));
    private Task<string> RunItemsAsync(Guid runId) => JsonAsync("SELECT coalesce(jsonb_agg(to_jsonb(i) ORDER BY i.external_listing_id), '[]'::jsonb)::text FROM commerce.marketplace_listing_sync_run_item i WHERE sync_run_id = @id", ("id", runId));

    private async Task<int> ListingCountAsync(Guid account, Func<IQueryable<MarketplaceListing>, IQueryable<MarketplaceListing>>? filter = null)
    {
        await using var db = Context(); IQueryable<MarketplaceListing> query = db.Set<MarketplaceListing>().Where(x => x.MarketplaceAccountId == account);
        return await (filter?.Invoke(query) ?? query).CountAsync();
    }

    // ======================================================================== D3 — G02 stale-worker race (E01-1)

    /// <summary>Everything the stale worker must leave untouched, INCLUDING the two surfaces the Gate 058 matrix found absent from the earlier snapshot: the whole AuditLog and the account root.</summary>
    private sealed record ExtendedSnapshot(StaleSnapshot Core, string AuditLog, string AccountRoot, string Connection, string Capabilities);

    private async Task<ExtendedSnapshot> ExtendedSnapshotAsync(Guid account, Guid runId)
    {
        StaleSnapshot core; await using (var db = Context()) core = await SnapshotAsync(db, account, runId);
        return new ExtendedSnapshot(core,
            await JsonAsync("SELECT coalesce(jsonb_agg(to_jsonb(a) ORDER BY a.id), '[]'::jsonb)::text FROM platform.audit_log a"),
            await JsonAsync("SELECT to_jsonb(m)::text FROM commerce.marketplace_account m WHERE id = @a", ("a", account)),
            await JsonAsync("SELECT to_jsonb(c)::text FROM commerce.marketplace_account_connection c WHERE marketplace_account_id = @a", ("a", account)),
            await JsonAsync("SELECT coalesce(jsonb_agg(to_jsonb(c) ORDER BY c.capability_code), '[]'::jsonb)::text FROM commerce.marketplace_account_capability c WHERE marketplace_account_id = @a", ("a", account)));
    }

    [Fact]
    public async Task D3_1_stale_A_parked_before_the_guard_is_fenced_out_after_B_reclaims_and_commits_and_leaves_AuditLog_and_account_root_untouched()
    {
        var account = await SeedAccountAsync(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MarketplaceListingSyncExecutionTestHooks.BeforeFencedDetailPersistenceAsync = async _ => { entered.TrySetResult(); await resume.Task; };
        try
        {
            Guid runId; await using (var db = Context()) runId = (await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow)).Id;
            Guid t1Token; var t1 = Task.Run(async () => { await using var db = Context(); return await Record.ExceptionAsync(() => CreateExecution(db, new ScriptedReader(["MLB-D3-1"], _clock)).RunAsync(runId)); });
            await WithSafetyTimeout(entered.Task, "A never reached the pre-guard barrier");
            await using (var db = Context()) t1Token = (await db.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId)).LeaseToken!.Value;

            _clock.UtcNow = _clock.UtcNow.AddMinutes(3);                                           // past lease_until
            await using (var db = Context()) (await CreateExecution(db, new ScriptedReader(["MLB-D3-1"], _clock)).RunAsync(runId))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED, "B (T2) commits a valid chunk for the same item");
            var before = await ExtendedSnapshotAsync(account, runId);
            before.Core.AttemptCount.Should().Be(2, "claim A + reclaim B");
            (await ListingCountAsync(account)).Should().Be(1);
            before.AuditLog.Should().Contain("LISTING_SYNC_FINISHED", "B's finished audit exists, so the audit snapshot is not vacuous");

            resume.TrySetResult();
            (await WithSafetyTimeout(t1)).Should().BeOfType<MarketplaceListingSyncLeaseLostException>("A's guard UPDATE affects 0 rows");
            var after = await ExtendedSnapshotAsync(account, runId);
            after.Should().BeEquivalentTo(before, "no listing, observation, run item, run counter/phase/status, account root, connection, capability or AuditLog row may change after the stale worker resumes");
            after.AuditLog.Should().Be(before.AuditLog);
            after.AccountRoot.Should().Be(before.AccountRoot);

            // An old token never validates again: it can neither heartbeat, write, nor finish.
            await using (var db = Context())
            {
                var authority = new MarketplaceListingSyncLeaseAuthority(db);
                (await authority.HeartbeatAsync(runId, t1Token, _clock.UtcNow, TimeSpan.FromMinutes(2))).Should().BeFalse();
                (await Record.ExceptionAsync(() => authority.ExecuteWithLeaseAsync(runId, t1Token, _clock.UtcNow, TimeSpan.FromMinutes(2), _ => Task.CompletedTask))).Should().BeOfType<MarketplaceListingSyncLeaseLostException>();
                (await Record.ExceptionAsync(() => new MarketplaceListingSyncRunKernel(db).FinalizeWithLeaseAsync(runId, t1Token, null, _clock.UtcNow, TimeSpan.FromMinutes(2)))).Should().BeOfType<MarketplaceListingSyncLeaseLostException>();
            }
            (await ExtendedSnapshotAsync(account, runId)).Should().BeEquivalentTo(before);
        }
        finally { MarketplaceListingSyncExecutionTestHooks.BeforeFencedDetailPersistenceAsync = null; resume.TrySetResult(); }
    }

    /// <summary>Polls pg_stat_activity/pg_blocking_pids until the waiter's statement touching <paramref name="relation"/> is blocked by the blocker's backend.</summary>
    private async Task WaitUntilBlockedOnRelationAsync(string waiterApplication, string blockerApplication, string relation, Task waiter)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        await using var observer = new NpgsqlConnection(_connectionString); await observer.OpenAsync();
        while (true)
        {
            waiter.IsCompleted.Should().BeFalse($"{waiterApplication} finished without ever being blocked by {blockerApplication}: the lease guard is not serializing the reclaim");
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity w JOIN pg_stat_activity b ON b.pid = ANY (pg_blocking_pids(w.pid))
                                WHERE w.application_name = @waiter AND b.application_name = @blocker AND w.wait_event_type = 'Lock' AND w.query ILIKE '%' || @relation || '%')
                """, observer);
            command.Parameters.AddWithValue("waiter", waiterApplication); command.Parameters.AddWithValue("blocker", blockerApplication); command.Parameters.AddWithValue("relation", relation);
            if ((bool)(await command.ExecuteScalarAsync())!) return;
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"{waiterApplication} was never observed blocked by {blockerApplication}");
            await Task.Delay(25);
        }
    }

    [Theory]
    [InlineData(3, false)]   // B.now = A's guard time: A's committed guard moved lease_until past it => the reclaim re-evaluates to 0 rows
    [InlineData(10, true)]   // B.now beyond A's new lease_until: B reclaims, but only AFTER A's transaction committed
    public async Task D3_2_A_parked_after_its_guard_blocks_B_reclaim_on_the_run_row_lock_and_B_re_evaluates_lease_until_after_A_commits(int reclaimAtMinutes, bool reclaimSucceeds)
    {
        var account = await SeedAccountAsync(); var t0 = _clock.UtcNow;
        var parkedInTx = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var releaseTx = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parkedBeforeFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var releaseFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A's guard runs at T0+3min (so it extends lease_until to T0+5min): the hook fires before the guard's clock read.
        MarketplaceListingSyncExecutionTestHooks.BeforeFencedDetailPersistenceAsync = _ => { _clock.UtcNow = t0.AddMinutes(3); return Task.CompletedTask; };
        MarketplaceListingSyncExecutionTestHooks.AfterAuthorizationValidationAsync = async _ => { parkedInTx.TrySetResult(); await releaseTx.Task; };
        MarketplaceListingSyncExecutionTestHooks.BeforeFinishAsync = async _ => { parkedBeforeFinish.TrySetResult(); await releaseFinish.Task; };
        try
        {
            Guid runId; await using (var db = Context()) runId = (await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), t0)).Id;
            var a = Task.Run(async () => { await using var db = Context("d3-2-a"); return await Record.ExceptionAsync(async () => await CreateExecution(db, new ScriptedReader(["MLB-D3-2"], _clock)).RunAsync(runId)); });
            await WithSafetyTimeout(parkedInTx.Task, "A never parked inside its fenced transaction");
            Guid tokenA; await using (var db = Context()) tokenA = (await db.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId)).LeaseToken!.Value;

            MarketplaceListingSyncLease? reclaimed = null;
            var b = Task.Run(async () => { await using var db = Context("d3-2-b"); reclaimed = await new MarketplaceListingSyncLeaseAuthority(db).ReclaimAsync(runId, t0.AddMinutes(reclaimAtMinutes), TimeSpan.FromMinutes(2)); });
            await WaitUntilBlockedOnRelationAsync("d3-2-b", "d3-2-a", "marketplace_listing_sync_run", b);   // the database's own lock graph, not timing
            b.IsCompleted.Should().BeFalse("B's reclaim must wait for A's open transaction");
            await using (var observer = Context())
            {
                var row = await observer.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId);
                row.LeaseToken.Should().Be(tokenA, "B has not been able to take the lease while A's transaction is open"); row.AttemptCount.Should().Be(1);
                (await observer.Set<MarketplaceListing>().AnyAsync(x => x.ExternalListingId == "MLB-D3-2")).Should().BeFalse("A's effects are not visible before A commits");
            }

            releaseTx.TrySetResult();
            await WithSafetyTimeout(parkedBeforeFinish.Task, "A never finished its fenced chunk");          // A committed; the detail work is done
            await WithSafetyTimeout(b, "B never completed after A committed");
            await using (var observer = Context())
            {
                var item = await observer.Set<MarketplaceListingSyncRunItem>().AsNoTracking().SingleAsync(x => x.SyncRunId == runId);
                item.Outcome.Should().Be(MarketplaceListingSyncItemOutcome.FOUND, "A's chunk committed atomically before B's reclaim could take effect");
                (await observer.Set<MarketplaceListing>().CountAsync(x => x.ExternalListingId == "MLB-D3-2")).Should().Be(1);
                (await observer.Set<MarketplaceListingObservation>().CountAsync()).Should().Be(1);
            }

            if (!reclaimSucceeds)
            {
                reclaimed.Should().BeNull("after A's guard extended lease_until beyond B's clock, B's re-evaluation matches 0 rows");
                await using (var observer = Context()) (await observer.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId)).LeaseToken.Should().Be(tokenA);
                releaseFinish.TrySetResult();
                (await WithSafetyTimeout(a)).Should().BeNull("A still owns the lease, so its finish succeeds");
                await using var after = Context(); (await after.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId)).Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
                (await after.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId)).AttemptCount.Should().Be(1);
            }
            else
            {
                reclaimed.Should().NotBeNull("lease_until (T0+5min) < B.now (T0+10min) once A has committed");
                reclaimed!.LeaseToken.Should().NotBe(tokenA);
                await using (var observer = Context()) { var row = await observer.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId); row.LeaseToken.Should().Be(reclaimed.LeaseToken); row.AttemptCount.Should().Be(2); }
                releaseFinish.TrySetResult();
                (await WithSafetyTimeout(a)).Should().BeOfType<MarketplaceListingSyncLeaseLostException>("A's token is dead the moment B's reclaim committed: it cannot finish");
                await using (var db = Context()) (await CreateExecution(db, new ScriptedReader(["MLB-D3-2"], _clock)).RunClaimedAsync(reclaimed))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
                await using var after = Context();
                (await after.Set<MarketplaceListing>().CountAsync(x => x.ExternalListingId == "MLB-D3-2")).Should().Be(1, "B must not duplicate A's committed effect");
                (await after.Set<MarketplaceListingObservation>().CountAsync()).Should().Be(1);
                (await after.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.SyncRunId == runId && x.Outcome == MarketplaceListingSyncItemOutcome.FOUND)).Should().Be(1);
            }
            (await AuditRowsAsync(runId)).Count(x => x.NewValuesJson!.Contains("LISTING_SYNC_FINISHED")).Should().Be(1, "exactly one finished audit regardless of the race outcome");
        }
        finally
        {
            MarketplaceListingSyncExecutionTestHooks.BeforeFencedDetailPersistenceAsync = null; MarketplaceListingSyncExecutionTestHooks.AfterAuthorizationValidationAsync = null; MarketplaceListingSyncExecutionTestHooks.BeforeFinishAsync = null;
            releaseTx.TrySetResult(); releaseFinish.TrySetResult();
        }
    }

    // ======================================================================== D4 — G03 failed identity (E01-2)

    [Fact]
    public async Task D4_G03_provider_100_95_5_element_500_twice_persists_across_a_host_restart_and_POST_retry_heals_to_HEALTHY_without_enumeration()
    {
        var account = await SeedAccountAsync("777"); var actor = Guid.NewGuid();
        var ids = Enumerable.Range(1, 100).Select(i => $"MLB{9000000000L + i}").ToArray();
        var failing = new[] { ids[2], ids[24], ids[46], ids[70], ids[97] };        // one per detail chunk
        Guid originalId;

        // ------------------------------------------------ boot 1: the failing run
        await using (var boot = NewBoot(ids, p => p.Element = id => failing.Contains(id) ? ElementJson(id, 500) : null))
        {
            var started = await boot.Service.StartFullAsync(account, actor); started.Succeeded.Should().BeTrue(); originalId = started.RunId!.Value;
            var run = await boot.Execution.RunAsync(originalId);
            run!.Status.Should().Be(MarketplaceListingSyncStatus.PARTIAL); run.EnumerationCompleted.Should().BeTrue(); run.SafeResultCode.Should().NotBeNullOrEmpty();

            boot.Wire.RequestsPerId.Where(x => failing.Contains(x.Key)).Select(x => x.Value).Should().OnlyContain(n => n == 2, "each previously unknown ID returned element 500 twice: the original read plus the ONE in-execution re-request");
            boot.Wire.RequestsPerId.Where(x => !failing.Contains(x.Key)).Select(x => x.Value).Should().OnlyContain(n => n == 1);
            boot.Wire.BulkRequests.Count(r => r.Length == 20).Should().Be(5); boot.Wire.BulkRequests.Count(r => r.Length == 1).Should().Be(5);
            boot.Wire.Attributes.Should().OnlyContain(a => a == FrozenBulkAttributeString);
            boot.Wire.OtherRequests.Should().BeEmpty();
            boot.Pages.PageCalls.Should().Be(2, "one FULL enumeration of two scan pages");

            var detail = await boot.Service.GetRunAsync(originalId);
            detail.Counters.Total.Should().Be(100); detail.Counters.Found.Should().Be(95); detail.Counters.BlockingRetryable.Should().Be(5); detail.Counters.BlockingNonRetryable.Should().Be(0); detail.Counters.Pending.Should().Be(0);
            detail.Counters.ByOutcome["TRANSIENT_ERROR"].Should().Be(5);
            var failedItems = await boot.Service.GetItemsAsync(originalId, MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR, 1, 100);
            failedItems.Items.Select(x => x.ExternalListingId).Should().BeEquivalentTo(failing);
            failedItems.Items.Should().OnlyContain(x => x.SafeErrorCode == "LISTING_READ_FAILED" && x.MarketplaceListingId == null && x.Retryable && x.Origin == MarketplaceListingSyncItemOrigin.ENUMERATED);
            var status = await boot.Service.GetStatusAsync(account);
            status.Summary.Health.Should().Be(MarketplaceListingSyncHealth.ATTENTION_REQUIRED); status.Summary.OpenBlockingItemCount.Should().Be(5);
        }
        (await ListingCountAsync(account)).Should().Be(95, "no listing row may exist for an ID the provider never resolved");
        await using (var db = Context()) (await db.Set<MarketplaceListing>().AnyAsync(x => failing.Contains(x.ExternalListingId))).Should().BeFalse();
        var originalRunBefore = await RunRowAsync(originalId); var originalItemsBefore = await RunItemsAsync(originalId);

        // ------------------------------------------------ boot 2: a NEW host (new DI container, same database)
        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);          // a restart takes real time; the summary orders a retry strictly after the run it heals
        Guid retryId;
        await using (var boot = NewBoot([], p => p.Element = null))
        {
            var persisted = await boot.Service.GetItemsAsync(originalId, MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR, 1, 100);
            persisted.Total.Should().Be(5, "the five unresolved identities survived the restart");
            (await boot.Service.GetStatusAsync(account)).Summary.OpenBlockingItemCount.Should().Be(5);

            var retry = await boot.Service.RetryAsync(originalId, actor);                          // POST /retry is a thin wrapper over exactly this call
            retry.Succeeded.Should().BeTrue(); retryId = retry.RunId!.Value;
            var queued = await boot.Service.GetRunAsync(retryId);
            queued.Run.Kind.Should().Be(MarketplaceListingSyncKind.FAILED_ONLY); queued.Run.RetryOfRunId.Should().Be(originalId); queued.Run.Status.Should().Be(MarketplaceListingSyncStatus.QUEUED);
            queued.Counters.Total.Should().Be(5); queued.Counters.ByOrigin.Should().BeEquivalentTo(new Dictionary<string, int> { ["RETRY_TARGET"] = 5 });
            (await boot.Service.GetItemsAsync(retryId, null, 1, 100)).Items.Select(x => x.ExternalListingId).Should().BeEquivalentTo(failing, "targets are derived server-side from the source run, never supplied by the caller");

            var executed = await boot.Execution.RunAsync(retryId);
            executed!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
            boot.Pages.PageCalls.Should().Be(0, "a FAILED_ONLY retry makes no enumeration call"); boot.Wire.OtherRequests.Should().BeEmpty();
            boot.Wire.BulkRequests.Should().ContainSingle().Which.Should().BeEquivalentTo(failing);
            var retryDetail = await boot.Service.GetRunAsync(retryId);
            retryDetail.Counters.Found.Should().Be(5); retryDetail.Counters.ListingsCreated.Should().Be(5); retryDetail.Counters.ObservationsAppended.Should().Be(5);
            var status = await boot.Service.GetStatusAsync(account);
            status.Summary.Health.Should().Be(MarketplaceListingSyncHealth.HEALTHY); status.Summary.OpenBlockingItemCount.Should().Be(0);
        }
        (await ListingCountAsync(account)).Should().Be(100);
        await using (var db = Context()) (await db.Set<MarketplaceListing>().Where(x => failing.Contains(x.ExternalListingId)).CountAsync()).Should().Be(5);
        (await RunRowAsync(originalId)).Should().Be(originalRunBefore, "the original run is immutable history");
        (await RunItemsAsync(originalId)).Should().Be(originalItemsBefore, "so are its items");
        await using (var db = Context())
        {
            var retryRun = await db.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == retryId);
            retryRun.RetryOfRunId.Should().Be(originalId); retryRun.RootRunId.Should().Be((await db.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == originalId)).RootRunId);
        }
    }

    // ======================================================================== D6 — G05 direct re-read matrix (E01-4)

    public enum D6Setup { UnknownEnumerated, KnownEnumerated, KnownNotEnumerated }
    public enum D6Outcome { Found, NotFound, AccessDenied, SellerMismatch, Transient, Unmapped, Malformed }

    private sealed record D6Expectation(MarketplaceListingSyncItemOutcome Item, string? Code, bool Retryable, int ExpectedRequestsForTarget);
    private static D6Expectation Expect(D6Outcome outcome) => outcome switch
    {
        D6Outcome.Found => new(MarketplaceListingSyncItemOutcome.FOUND, null, false, 1),
        D6Outcome.NotFound => new(MarketplaceListingSyncItemOutcome.NOT_FOUND, "LISTING_NOT_FOUND_AT_PROVIDER", true, 1),
        D6Outcome.AccessDenied => new(MarketplaceListingSyncItemOutcome.ACCESS_DENIED, "LISTING_ACCESS_DENIED", true, 1),
        D6Outcome.SellerMismatch => new(MarketplaceListingSyncItemOutcome.SELLER_MISMATCH, "LISTING_SELLER_MISMATCH", false, 1),
        D6Outcome.Transient => new(MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR, "LISTING_READ_FAILED", true, 2),            // element 500 => FAILED => one in-execution re-request
        D6Outcome.Unmapped => new(MarketplaceListingSyncItemOutcome.PERMANENT_ERROR, "LISTING_STATUS_UNMAPPED", false, 1),
        D6Outcome.Malformed => new(MarketplaceListingSyncItemOutcome.PERMANENT_ERROR, "LISTING_RESPONSE_INVALID", false, 1),      // B2: INVALID_RESPONSE must NOT be re-requested
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static string D6Element(string id, D6Outcome outcome) => outcome switch
    {
        D6Outcome.Found => ElementJson(id, 200),
        D6Outcome.NotFound => ElementJson(id, 404),
        D6Outcome.AccessDenied => ElementJson(id, 403),
        D6Outcome.SellerMismatch => ElementJson(id, 200, b => b["seller_id"] = 888),
        D6Outcome.Transient => ElementJson(id, 500),
        D6Outcome.Unmapped => ElementJson(id, 200, b => b["status"] = "some_future_provider_status"),
        D6Outcome.Malformed => new JsonObject { ["id"] = id, ["status_code"] = 200, ["body"] = null }.ToJsonString(),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    public static TheoryData<D6Setup, D6Outcome> D6Matrix()
    {
        var data = new TheoryData<D6Setup, D6Outcome>();
        foreach (var setup in Enum.GetValues<D6Setup>()) foreach (var outcome in Enum.GetValues<D6Outcome>()) data.Add(setup, outcome);
        return data;
    }

    [Theory]
    [MemberData(nameof(D6Matrix))]
    public async Task D6_G05_every_origin_by_outcome_asserts_item_listing_run_summary_and_retry_eligibility_end_to_end_through_the_real_adapter(D6Setup setup, D6Outcome outcome)
    {
        var account = await SeedAccountAsync("777"); var actor = Guid.NewGuid();
        const string target = "MLB9100000001"; const string peer = "MLB9100000002"; var expected = Expect(outcome);
        var known = setup != D6Setup.UnknownEnumerated;
        if (known)
        {
            await using var db = Context();
            db.Add(new MarketplaceListing(account, target, "SKU-OLD", MarketplaceListingStatus.ACTIVE, "Old title", 5.00m)); await db.SaveChangesAsync();
        }
        var enumerated = setup == D6Setup.KnownNotEnumerated ? new[] { peer } : new[] { target, peer };
        var expectedOrigin = setup == D6Setup.KnownNotEnumerated ? MarketplaceListingSyncItemOrigin.KNOWN_NOT_ENUMERATED : MarketplaceListingSyncItemOrigin.ENUMERATED;

        await using var boot = NewBoot(enumerated, p => p.Element = id => id == target ? D6Element(id, outcome) : null);
        var runId = (await boot.Service.StartFullAsync(account, actor)).RunId!.Value;
        var run = (await boot.Execution.RunAsync(runId))!;

        // ---- run item: outcome / code / origin / listing link
        var item = (await boot.Service.GetItemsAsync(runId, null, 1, 100)).Items.Single(x => x.ExternalListingId == target);
        item.Outcome.Should().Be(expected.Item); item.SafeErrorCode.Should().Be(expected.Code); item.Origin.Should().Be(expectedOrigin);
        boot.Wire.RequestsPerId[target].Should().Be(expected.ExpectedRequestsForTarget, "only a FAILED element is re-requested once; INVALID_RESPONSE/UNMAPPED/404/403/SELLER_MISMATCH are final on first sight");
        boot.Wire.RequestsPerId[peer].Should().Be(1);
        (await boot.Service.GetItemsAsync(runId, null, 1, 100)).Items.Single(x => x.ExternalListingId == peer).Outcome.Should().Be(MarketplaceListingSyncItemOutcome.FOUND);

        // ---- listing facts and sync state per §E.5 (or no row)
        await using (var db = Context())
        {
            var listing = await db.Set<MarketplaceListing>().AsNoTracking().Include(x => x.Observations).SingleOrDefaultAsync(x => x.MarketplaceAccountId == account && x.ExternalListingId == target);
            if (outcome == D6Outcome.Found)
            {
                listing.Should().NotBeNull(); item.MarketplaceListingId.Should().Be(listing!.Id);
                listing.SyncState.Should().Be(MarketplaceSyncState.SYNCED); listing.SyncError.Should().BeNull(); listing.TitleSnapshot.Should().Be("Provider " + target); listing.ObservedPrice.Should().Be(12.34m); listing.ObservedStatus.Should().Be(MarketplaceListingStatus.PAUSED); listing.ExternalSku.Should().Be("SKU-" + target);
            }
            else if (!known)
            {
                listing.Should().BeNull("an unknown ID that did not resolve never gets a listing row"); item.MarketplaceListingId.Should().BeNull();
            }
            else
            {
                listing.Should().NotBeNull(); item.MarketplaceListingId.Should().BeNull();
                listing!.SyncState.Should().Be(MarketplaceSyncState.ERROR); listing.SyncError.Should().Be(expected.Code);
                listing.TitleSnapshot.Should().Be("Old title", "facts are never replaced from a non-FOUND read (and SELLER_MISMATCH discards them)"); listing.ObservedPrice.Should().Be(5.00m); listing.ObservedStatus.Should().Be(MarketplaceListingStatus.ACTIVE); listing.ExternalSku.Should().Be("SKU-OLD");
                listing.MarketplaceAccountId.Should().Be(account, "a listing is never reassigned");
            }
            (await db.Set<MarketplaceListing>().AsNoTracking().SingleAsync(x => x.ExternalListingId == peer)).SyncState.Should().Be(MarketplaceSyncState.SYNCED);
        }

        // ---- run outcome per §E.6, summary health, open items
        var status = await boot.Service.GetStatusAsync(account);
        if (outcome == D6Outcome.Found)
        {
            run.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED); status.Summary.Health.Should().Be(MarketplaceListingSyncHealth.HEALTHY); status.Summary.OpenBlockingItemCount.Should().Be(0);
            (await ListingCountAsync(account, q => q.Where(x => x.SyncState == MarketplaceSyncState.ERROR))).Should().Be(0, "a SUCCEEDED FULL run leaves no listing of the account in ERROR");
        }
        else
        {
            run.Status.Should().Be(MarketplaceListingSyncStatus.PARTIAL, "one blocking item next to a FOUND peer"); status.Summary.Health.Should().Be(MarketplaceListingSyncHealth.ATTENTION_REQUIRED); status.Summary.OpenBlockingItemCount.Should().Be(1);
        }
        var counters = (await boot.Service.GetRunAsync(runId)).Counters;
        counters.Total.Should().Be(2); counters.Pending.Should().Be(0);
        counters.Found.Should().Be(outcome == D6Outcome.Found ? 2 : 1);
        counters.BlockingRetryable.Should().Be(!expected.Retryable || outcome == D6Outcome.Found ? 0 : 1);
        counters.BlockingNonRetryable.Should().Be(outcome is D6Outcome.SellerMismatch or D6Outcome.Unmapped or D6Outcome.Malformed ? 1 : 0);

        // ---- retry eligibility: copied or not by a FAILED_ONLY retry
        var retry = await boot.Service.RetryAsync(runId, actor);
        if (expected.Retryable && outcome != D6Outcome.Found)
        {
            retry.Succeeded.Should().BeTrue();
            var retryItems = (await boot.Service.GetItemsAsync(retry.RunId!.Value, null, 1, 100)).Items;
            retryItems.Should().ContainSingle().Which.Should().Match<MarketplaceListingSyncItemView>(x => x.ExternalListingId == target && x.Origin == MarketplaceListingSyncItemOrigin.RETRY_TARGET && x.Outcome == MarketplaceListingSyncItemOutcome.PENDING);
        }
        else { retry.Succeeded.Should().BeFalse(); retry.Code.Should().Be("LISTING_SYNC_NOTHING_TO_RETRY", "SELLER_MISMATCH/PERMANENT outcomes (and a fully resolved run) are never copied by a retry"); }
    }

    [Theory]
    [InlineData(D6Outcome.NotFound)]
    [InlineData(D6Outcome.AccessDenied)]
    [InlineData(D6Outcome.SellerMismatch)]
    [InlineData(D6Outcome.Transient)]
    [InlineData(D6Outcome.Unmapped)]
    [InlineData(D6Outcome.Malformed)]
    public async Task D6_G05_a_FULL_run_with_no_FOUND_item_and_a_blocking_item_is_FAILED_and_the_summary_health_is_FAILED(D6Outcome outcome)
    {
        var account = await SeedAccountAsync("777"); const string target = "MLB9200000001";
        await using var boot = NewBoot([target], p => p.Element = id => D6Element(id, outcome));
        var runId = (await boot.Service.StartFullAsync(account, Guid.NewGuid())).RunId!.Value;
        (await boot.Execution.RunAsync(runId))!.Status.Should().Be(MarketplaceListingSyncStatus.FAILED, "§E.6: found = 0 and blocking > 0");
        var status = await boot.Service.GetStatusAsync(account);
        status.Summary.Health.Should().Be(MarketplaceListingSyncHealth.FAILED); status.Summary.OpenBlockingItemCount.Should().Be(1);
    }

    [Fact]
    public async Task M059_B1_extreme_provider_prices_never_abort_the_run_and_persist_as_unknown_price_while_peers_and_the_runtime_stay_healthy()
    {
        var account = await SeedAccountAsync("777"); var ids = new[] { "MLB9600000001", "MLB9600000002", "MLB9600000003", "MLB9600000004" };
        var prices = new Dictionary<string, decimal> { [ids[0]] = decimal.MaxValue, [ids[1]] = 10000000000000000m, [ids[2]] = 9999999999999999.99m, [ids[3]] = 19.90m };
        await using var boot = NewBoot(ids, p => p.Element = id => ElementJson(id, 200, b => b["price"] = prices[id]));
        var runId = (await boot.Service.StartFullAsync(account, Guid.NewGuid())).RunId!.Value;
        var run = (await boot.Execution.RunAsync(runId))!;

        run.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED, "an unrepresentable price is a per-field normalization result, never a batch or run failure (it used to surface as OverflowException -> PROVIDER_UNAVAILABLE)");
        run.SafeResultCode.Should().NotBe("PROVIDER_UNAVAILABLE");
        boot.Wire.RequestsPerId.Values.Should().OnlyContain(n => n == 1, "no element failed, so nothing was re-requested");
        await using var db = Context();
        var listings = await db.Set<MarketplaceListing>().AsNoTracking().Include(x => x.Observations).Where(x => x.MarketplaceAccountId == account).ToDictionaryAsync(x => x.ExternalListingId);
        listings.Should().HaveCount(4);
        listings[ids[0]].ObservedPrice.Should().BeNull("decimal.MaxValue is not a storable price: unknown, never zero, never saturated");
        listings[ids[1]].ObservedPrice.Should().BeNull("one unit above numeric(18,2) would otherwise fail the INSERT with 22003");
        listings[ids[2]].ObservedPrice.Should().Be(9999999999999999.99m, "the exact numeric(18,2) ceiling is preserved");
        listings[ids[3]].ObservedPrice.Should().Be(19.90m);
        listings.Values.Should().OnlyContain(x => x.SyncState == MarketplaceSyncState.SYNCED && x.Observations.Count == 1 && x.ObservedStatus == MarketplaceListingStatus.PAUSED && x.TitleSnapshot!.StartsWith("Provider "));
        (await db.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.SyncRunId == runId && x.Outcome == MarketplaceListingSyncItemOutcome.FOUND)).Should().Be(4);
        (await db.Set<MarketplaceAccountConnection>().AsNoTracking().SingleAsync(x => x.MarketplaceAccountId == account)).RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.AVAILABLE, "a provider-unavailable runtime effect must not be recorded for a bad price");
    }

    [Fact]
    public async Task D6_G05_an_incomplete_enumeration_fails_the_run_registers_no_missing_reconciliation_and_changes_no_listing()
    {
        var account = await SeedAccountAsync("777"); const string known = "MLB9300000001"; const string enumerated = "MLB9300000002";
        await using (var db = Context()) { db.Add(new MarketplaceListing(account, known, "SKU-K", MarketplaceListingStatus.ACTIVE, "Known title", 7.00m)); await db.SaveChangesAsync(); }
        var before = await JsonAsync("SELECT coalesce(jsonb_agg(to_jsonb(l) ORDER BY l.external_listing_id), '[]'::jsonb)::text FROM commerce.marketplace_listing l WHERE marketplace_account_id = @a", ("a", account));
        var observationsBefore = await JsonAsync("SELECT coalesce(jsonb_agg(to_jsonb(o) ORDER BY o.id), '[]'::jsonb)::text FROM commerce.marketplace_listing_observation o");

        await using var boot = NewBoot([]);
        boot.Pages.Pages.Enqueue(new MarketplaceListingPage([enumerated], new Cursor(), false, 0));
        boot.Pages.Pages.Enqueue(new MarketplaceListingPage(["MLB9300000003"], null, true, RejectedIdentifierCount: 1));      // a rejected identifier => the enumeration cannot be trusted as complete
        var runId = (await boot.Service.StartFullAsync(account, Guid.NewGuid())).RunId!.Value;
        var run = (await boot.Execution.RunAsync(runId))!;

        run.Status.Should().Be(MarketplaceListingSyncStatus.FAILED); run.EnumerationCompleted.Should().BeFalse(); run.SafeResultCode.Should().Be("LISTING_ENUMERATION_INCOMPLETE");
        var items = (await boot.Service.GetItemsAsync(runId, null, 1, 100)).Items;
        items.Should().NotContain(x => x.Origin == MarketplaceListingSyncItemOrigin.KNOWN_NOT_ENUMERATED, "missing reconciliation runs only after a COMPLETE enumeration");
        items.Should().NotContain(x => x.ExternalListingId == known);
        items.Where(x => x.Outcome == MarketplaceListingSyncItemOutcome.PENDING).Should().BeEmpty("the stop turns every PENDING item into TRANSIENT_ERROR with the stop code");
        items.Should().OnlyContain(x => x.Outcome == MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR);
        boot.Wire.BulkRequests.Should().BeEmpty("no detail read happens after an incomplete enumeration");
        (await JsonAsync("SELECT coalesce(jsonb_agg(to_jsonb(l) ORDER BY l.external_listing_id), '[]'::jsonb)::text FROM commerce.marketplace_listing l WHERE marketplace_account_id = @a", ("a", account))).Should().Be(before, "no listing row may change");
        (await JsonAsync("SELECT coalesce(jsonb_agg(to_jsonb(o) ORDER BY o.id), '[]'::jsonb)::text FROM commerce.marketplace_listing_observation o")).Should().Be(observationsBefore);
        (await boot.Service.GetStatusAsync(account)).Summary.Health.Should().Be(MarketplaceListingSyncHealth.FAILED);
    }

    // ======================================================================== D7 — G06 chunking (E01-5)

    [Theory]
    [InlineData(20, new[] { 20, 20, 5 })]
    [InlineData(7, new[] { 7, 7, 7, 7, 7, 7, 3 })]
    public async Task D7_G06_45_pending_items_are_requested_in_exact_chunks_with_the_exact_frozen_attributes_string(int detailBatchSize, int[] expectedChunkSizes)
    {
        var account = await SeedAccountAsync("777");
        var ids = Enumerable.Range(1, 45).Select(i => $"MLB{9400000000L + i}").ToArray();
        await using var boot = NewBoot(ids, detailBatchSize: detailBatchSize);
        var runId = (await boot.Service.StartFullAsync(account, Guid.NewGuid())).RunId!.Value;
        (await boot.Execution.RunAsync(runId))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);

        boot.Wire.BulkRequests.Select(r => r.Length).Should().Equal(expectedChunkSizes);
        boot.Wire.BulkRequests.SelectMany(r => r).Should().Equal(ids.OrderBy(x => x, StringComparer.Ordinal), "chunks are consecutive slices of the deterministic pending order and no ID is requested twice");
        boot.Wire.Attributes.Should().OnlyContain(a => a == FrozenBulkAttributeString);
        boot.Wire.OtherRequests.Should().BeEmpty("never the deprecated /items?ids= or any other endpoint");
        (await ListingCountAsync(account)).Should().Be(45);
    }

    // ======================================================================== D5 — G04 SKU authority variants (E01-3)

    private async Task<Guid> SyncOnceAsync(Guid account, IReadOnlyList<string> ids, Func<string, NormalizedListingFacts>? facts = null, int minuteOffset = 0, ICommerceProductEligibilityReader? eligibility = null)
    {
        var reader = new ScriptedReader(ids, _clock);
        foreach (var id in ids) if (facts is not null) reader.Facts[id] = facts(id);
        await using var db = Context();
        var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow.AddMinutes(minuteOffset));
        var result = await CreateExecution(db, reader, productEligibility: eligibility).RunAsync(run.Id);
        result!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED); return run.Id;
    }

    private async Task<MarketplaceListing> ListingAsync(Guid account, string externalId)
    {
        await using var db = Context(); return await db.Set<MarketplaceListing>().AsNoTracking().Include(x => x.Observations).SingleAsync(x => x.MarketplaceAccountId == account && x.ExternalListingId == externalId);
    }

    [Fact]
    public async Task D5_G04_two_ACTIVE_mappings_injected_with_the_unique_index_dropped_resolve_to_NEEDS_REVIEW_and_the_state_is_preserved_by_later_imports()
    {
        var account = await SeedAccountAsync(); var productA = await SeedProductAsync(true); var productB = await SeedProductAsync(true); const string sku = "SKU-DUP";
        // Disposable database only: remove the partial unique index so a corrupted state can be injected, which is exactly what the defence-in-depth branch exists for.
        await using (var ddl = Context())
            await ddl.Database.ExecuteSqlRawAsync("""
                DO $$ DECLARE r record; BEGIN
                  FOR r IN SELECT indexname FROM pg_indexes WHERE schemaname = 'commerce' AND tablename = 'marketplace_account_sku_mapping' AND indexdef ILIKE 'CREATE UNIQUE INDEX%(marketplace_account_id, external_sku)%' LOOP
                    EXECUTE 'DROP INDEX commerce.' || quote_ident(r.indexname); END LOOP; END $$;
                """);
        await using (var db = Context())
        {
            var first = new MarketplaceAccountSkuMapping(account, sku, productA, Guid.NewGuid(), _clock.UtcNow); var second = new MarketplaceAccountSkuMapping(account, sku, productB, Guid.NewGuid(), _clock.UtcNow);
            db.AddRange(first, second); await db.SaveChangesAsync();
            (await db.Set<MarketplaceAccountSkuMapping>().CountAsync(x => x.MarketplaceAccountId == account && x.ExternalSku == sku && x.Active)).Should().Be(2, "the injected corrupted state is real");
        }
        await SyncOnceAsync(account, ["MLB-DUP"], id => FactsFor(id, sku));
        var listing = await ListingAsync(account, "MLB-DUP");
        listing.LinkageState.Should().Be(MarketplaceLinkageState.NEEDS_REVIEW, "ambiguous authority must never pick a product"); listing.ProductId.Should().BeNull(); listing.SkuMappingId.Should().BeNull(); listing.ChannelOfferId.Should().BeNull();

        // Repair the data (deactivate the second row) and import again: NEEDS_REVIEW is a human state, never auto-promoted by a later import.
        await using (var db = Context()) { var second = await db.Set<MarketplaceAccountSkuMapping>().SingleAsync(x => x.MarketplaceAccountId == account && x.ProductId == productB); second.Deactivate(Guid.NewGuid(), _clock.UtcNow); await db.SaveChangesAsync(); }
        await SyncOnceAsync(account, ["MLB-DUP"], id => FactsFor(id, sku), minuteOffset: 1);
        var again = await ListingAsync(account, "MLB-DUP");
        again.LinkageState.Should().Be(MarketplaceLinkageState.NEEDS_REVIEW, "NEEDS_REVIEW is preserved across imports"); again.ProductId.Should().BeNull();
        (await ListingAsync(account, "MLB-DUP")).Observations.Should().HaveCount(1, "the repeat import changed no observed fact, so no new observation");
    }

    [Fact]
    public async Task D5_G04_a_listing_with_more_than_one_variation_is_NEEDS_REVIEW_even_with_a_unique_active_mapping()
    {
        var account = await SeedAccountAsync(); var product = await SeedProductAsync(true);
        await using (var db = Context()) { db.Add(new MarketplaceAccountSkuMapping(account, "SKU-VAR", product, Guid.NewGuid(), _clock.UtcNow)); await db.SaveChangesAsync(); }
        await SyncOnceAsync(account, ["MLB-V1", "MLB-V2"], id => FactsFor(id, "SKU-VAR") with { VariationCount = id == "MLB-V2" ? 3 : 1 });
        (await ListingAsync(account, "MLB-V1")).LinkageState.Should().Be(MarketplaceLinkageState.LINKED, "exactly one variation links");
        var multi = await ListingAsync(account, "MLB-V2");
        multi.LinkageState.Should().Be(MarketplaceLinkageState.NEEDS_REVIEW); multi.ProductId.Should().BeNull(); multi.VariationCount.Should().Be(3);
    }

    [Fact]
    public async Task D5_G04_a_provider_SKU_change_on_a_linked_listing_keeps_its_link_and_the_new_SKU_links_nothing()
    {
        var account = await SeedAccountAsync(); var product = await SeedProductAsync(true); var otherProduct = await SeedProductAsync(true); Guid mappingId;
        await using (var db = Context())
        {
            var mapping = new MarketplaceAccountSkuMapping(account, "SKU-OLD", product, Guid.NewGuid(), _clock.UtcNow); db.Add(mapping);
            db.Add(new MarketplaceAccountSkuMapping(account, "SKU-NEW", otherProduct, Guid.NewGuid(), _clock.UtcNow));   // even a valid mapping for the NEW sku must not relink
            await db.SaveChangesAsync(); mappingId = mapping.Id;
        }
        await SyncOnceAsync(account, ["MLB-CHG"], id => FactsFor(id, "SKU-OLD"));
        var linked = await ListingAsync(account, "MLB-CHG");
        linked.LinkageState.Should().Be(MarketplaceLinkageState.LINKED); linked.ProductId.Should().Be(product); linked.SkuMappingId.Should().Be(mappingId);

        await SyncOnceAsync(account, ["MLB-CHG"], id => FactsFor(id, "SKU-NEW"), minuteOffset: 1);
        var changed = await ListingAsync(account, "MLB-CHG");
        changed.ExternalSku.Should().Be("SKU-NEW", "the observed SKU follows the provider");
        changed.LinkageState.Should().Be(MarketplaceLinkageState.LINKED); changed.ProductId.Should().Be(product, "the link is kept"); changed.SkuMappingId.Should().Be(mappingId); changed.LinkageSource.Should().Be(MarketplaceListingLinkageSource.DETERMINISTIC_SKU);
        changed.Observations.Should().HaveCount(2, "the SKU change is a real observed change");

        // …and a SKU removal keeps it as well.
        await SyncOnceAsync(account, ["MLB-CHG"], id => FactsFor(id, "SKU-NEW") with { ExternalSku = null }, minuteOffset: 2);
        var removed = await ListingAsync(account, "MLB-CHG"); removed.ExternalSku.Should().BeNull(); removed.LinkageState.Should().Be(MarketplaceLinkageState.LINKED); removed.ProductId.Should().Be(product);
    }

    [Fact]
    public async Task D5_G04_operator_unlink_suppresses_auto_relink_on_the_following_import_even_though_the_mapping_is_still_active()
    {
        var account = await SeedAccountAsync(); var product = await SeedProductAsync(true);
        await using (var db = Context()) { db.Add(new MarketplaceAccountSkuMapping(account, "SKU-UNL", product, Guid.NewGuid(), _clock.UtcNow)); await db.SaveChangesAsync(); }
        await SyncOnceAsync(account, ["MLB-UNL"], id => FactsFor(id, "SKU-UNL"));
        (await ListingAsync(account, "MLB-UNL")).LinkageState.Should().Be(MarketplaceLinkageState.LINKED);

        await using (var db = Context()) { var listing = await db.Set<MarketplaceListing>().SingleAsync(x => x.ExternalListingId == "MLB-UNL"); listing.Unlink(); await db.SaveChangesAsync(); }
        var unlinked = await ListingAsync(account, "MLB-UNL"); unlinked.AutoLinkSuppressed.Should().BeTrue(); unlinked.LinkageState.Should().Be(MarketplaceLinkageState.UNLINKED);

        await SyncOnceAsync(account, ["MLB-UNL"], id => FactsFor(id, "SKU-UNL") with { Title = "Changed so the import really writes" }, minuteOffset: 1);
        var after = await ListingAsync(account, "MLB-UNL");
        after.LinkageState.Should().Be(MarketplaceLinkageState.UNLINKED, "suppression prevents the re-link"); after.ProductId.Should().BeNull(); after.SkuMappingId.Should().BeNull(); after.AutoLinkSuppressed.Should().BeTrue();
        after.TitleSnapshot.Should().Be("Changed so the import really writes", "the import itself still applied the provider facts");
        after.ChannelOfferId.Should().BeNull("a Offer is never created or linked by listing sync");
        await using var verify = Context(); (await verify.Set<ChannelOffer>().CountAsync()).Should().Be(0);
    }

    // ======================================================================== D9 — execution-side residuals (E01-7)

    [Fact]
    public async Task D9_provider_sync_observations_with_a_null_ProviderObservedAt_are_retained_by_retention_keeping_exactly_the_latest_per_listing_and_NEEDS_REVIEW_is_untouched()
    {
        var account = await SeedAccountAsync(); var productA = await SeedProductAsync(true); var productB = await SeedProductAsync(true);
        await using (var ddl = Context())
            await ddl.Database.ExecuteSqlRawAsync("DO $$ DECLARE r record; BEGIN FOR r IN SELECT indexname FROM pg_indexes WHERE schemaname = 'commerce' AND tablename = 'marketplace_account_sku_mapping' AND indexdef ILIKE 'CREATE UNIQUE INDEX%(marketplace_account_id, external_sku)%' LOOP EXECUTE 'DROP INDEX commerce.' || quote_ident(r.indexname); END LOOP; END $$;");
        await using (var db = Context()) { db.Add(new MarketplaceAccountSkuMapping(account, "SKU-RET", productA, Guid.NewGuid(), _clock.UtcNow)); db.Add(new MarketplaceAccountSkuMapping(account, "SKU-RET", productB, Guid.NewGuid(), _clock.UtcNow)); await db.SaveChangesAsync(); }

        // Three genuinely different observations (A -> B -> A would be 3 as well): price changes with ProviderObservedAt unknown.
        for (var i = 0; i < 3; i++)
            await SyncOnceAsync(account, ["MLB-RET"], id => FactsFor(id, "SKU-RET") with { ObservedPrice = 10m + i, ProviderObservedAt = null }, minuteOffset: i);
        var listing = await ListingAsync(account, "MLB-RET");
        listing.Observations.Should().HaveCount(3).And.OnlyContain(o => o.ProviderObservedAt == null && o.Provenance == ObservationProvenance.PROVIDER_SYNC);
        listing.LinkageState.Should().Be(MarketplaceLinkageState.NEEDS_REVIEW);
        var latestPrice = listing.ObservedPrice;

        // Age every observation past the retention window, then purge.
        await using (var db = Context()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_listing_observation SET ingested_at = {_clock.UtcNow.AddDays(-400)}");
        int purged;
        await using (var db = Context())
            purged = await new Verce.Api.Commerce.MarketplaceListingObservationRetentionService(db, new Verce.Modules.Settings.AppSettingValueReader(db), _clock).PurgeExpiredAsync();
        purged.Should().Be(2, "every expired observation except the latest of the listing is purged — null ProviderObservedAt must not make a row immune or deletable");
        var after = await ListingAsync(account, "MLB-RET");
        after.Observations.Should().ContainSingle().Which.ObservedPrice.Should().Be(latestPrice, "the single survivor is the latest observation");
        after.LinkageState.Should().Be(MarketplaceLinkageState.NEEDS_REVIEW, "retention never touches linkage"); after.ObservedPrice.Should().Be(latestPrice);
    }

    [Fact]
    public async Task D9_no_secret_token_scroll_id_url_query_or_provider_pii_value_reaches_logs_audit_rows_db_rows_or_the_application_DTOs_after_a_full_real_adapter_run()
    {
        var account = await SeedAccountAsync("777"); var telemetry = new BoundedLogProvider();
        const string accessSentinel = "test-only-access-token"; const string refreshSentinel = "test-only-refresh-token"; const string scrollSentinel = "SENTINEL-SCROLL-ID-9f3a";
        const string pii = "pii.sentinel@example.test";
        var wire = new BulkProvider { Element = id => ElementJson(id, 200, b => { b["seller_address"] = new JsonObject { ["city"] = pii }; b["email"] = pii; b["pictures"] = new JsonArray(new JsonObject { ["url"] = "https://pii.example.test/secret?token=" + scrollSentinel }); }) };
        var connector = CreateRealConnector(wire.Handle, telemetry);
        var pages = new ScriptedReader(["MLB9500000001", "MLB9500000002"], _clock);
        pages.Pages.Enqueue(new MarketplaceListingPage(["MLB9500000001"], new MercadoLivreScrollCursor(scrollSentinel), false, 0));
        pages.Pages.Enqueue(new MarketplaceListingPage(["MLB9500000002"], null, true, 0));
        var reader = new ConnectorDetailReader(pages, connector);
        Guid runId;
        await using (var db = Context())
        {
            var service = CreateService(db, reader: reader); runId = (await service.StartFullAsync(account, Guid.NewGuid())).RunId!.Value;
            (await CreateExecution(db, reader).RunAsync(runId))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
            var dtoJson = JsonSerializer.Serialize(new object[] { await service.GetRunAsync(runId), await service.GetItemsAsync(runId, null, 1, 100), await service.GetStatusAsync(account) });
            foreach (var secret in new[] { accessSentinel, refreshSentinel, scrollSentinel, pii, "Authorization", "Bearer" }) dtoJson.Should().NotContain(secret, "DTOs are allowlisted");
        }
        telemetry.Entries.Should().NotBeEmpty();
        foreach (var entry in telemetry.Entries) foreach (var secret in new[] { accessSentinel, refreshSentinel, scrollSentinel, pii, "Bearer" }) entry.Should().NotContain(secret);

        // Every persisted surface: the whole AuditLog and every row of every commerce table, serialized.
        var audit = await JsonAsync("SELECT coalesce(jsonb_agg(to_jsonb(a)), '[]'::jsonb)::text FROM platform.audit_log a");
        var tables = new List<string>();
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = 'commerce' AND table_type = 'BASE TABLE'", connection);
            await using var rows = await command.ExecuteReaderAsync(); while (await rows.ReadAsync()) tables.Add(rows.GetString(0));
        }
        tables.Should().Contain(["marketplace_listing", "marketplace_listing_observation", "marketplace_listing_sync_run", "marketplace_listing_sync_run_item", "marketplace_account"]);
        var everything = new StringBuilder(audit);
        foreach (var table in tables) everything.Append(await JsonAsync($"SELECT coalesce(jsonb_agg(to_jsonb(t)), '[]'::jsonb)::text FROM commerce.\"{table}\" t"));
        var persisted = everything.ToString();
        persisted.Length.Should().BeGreaterThan(1000, "the sweep is not vacuous");
        foreach (var secret in new[] { accessSentinel, refreshSentinel, scrollSentinel, pii, "scroll_id", "Bearer " }) persisted.Should().NotContain(secret);
    }
}
