using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Verce.IntegrationTests.DataProtection;
using Verce.Platform.Outbox;
using Verce.Platform.Persistence;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Outbox;

/// <summary>Closes the remaining C contracts through the production dispatcher, not a test
/// double. ProbeConsumer exists only in this integration-test assembly and is registered only
/// in its temporary service provider.</summary>
[Collection(PostgresCollection.Name)]
public class OutboxDispatcherAndRetentionTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));

    public OutboxDispatcherAndRetentionTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE platform.outbox_message_attempt, platform.outbox_message RESTART IDENTITY CASCADE;
            CREATE TABLE IF NOT EXISTS platform.integration_probe_effect (
                idempotency_key text PRIMARY KEY,
                invocation_count integer NOT NULL DEFAULT 1);
            TRUNCATE TABLE platform.integration_probe_effect;
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private ServiceProvider BuildProvider(ProbeIntegrationEventConsumer consumer) =>
        BuildProviderWithConsumers(consumer);

    private ServiceProvider BuildProviderWithConsumers(params IIntegrationEventConsumer[] consumers)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Verce"] = _fixture.ConnectionString,
            ["DataProtection:DevKeyDirectory"] = Path.Combine(Path.GetTempPath(), $"verce-outbox-test-{Guid.NewGuid():N}"),
            // This is a bare ServiceCollection, not a generic Host — Quartz's own hosted service
            // needs IHostApplicationLifetime, which only a real Host provides. These tests exercise
            // OutboxDispatcher/OutboxRetentionService/OutboxConsumerStartupValidator directly and
            // never need a live scheduler.
            ["Outbox:SchedulingEnabled"] = "false",
        }).Build();
        services.AddVercePlatform(configuration, new FakeHostEnvironment("Development"), Array.Empty<System.Reflection.Assembly>());
        services.AddSingleton<IClock>(_clock); // override production wall clock for exact retention/retry assertions
        foreach (var consumer in consumers)
            services.AddScoped<IIntegrationEventConsumer>(_ => consumer);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private OutboxMessage Seed(string eventType = ProbeIntegrationEventConsumer.Type, int maxAttempts = 5, string? key = "probe:one") =>
        OutboxMessage.Enqueue(eventType, "{}", key, Guid.CreateVersion7(), null, null, "Probe", null, _clock.UtcNow, maxAttempts);

    [Fact]
    public async Task C6_real_dispatcher_applies_retry_ladder_and_records_the_failed_attempt()
    {
        await using var seed = _fixture.CreateContext();
        var message = Seed();
        seed.OutboxMessages.Add(message);
        await seed.SaveChangesAsync();

        var consumer = new ProbeIntegrationEventConsumer(_fixture.ConnectionString) { Mode = ProbeMode.RetryableFailure };
        await using var provider = BuildProvider(consumer);
        await using (var scope = provider.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(1, "probe-worker")).Should().Be(1);

        await using var verify = _fixture.CreateContext();
        var reloaded = await verify.OutboxMessages.SingleAsync(m => m.Id == message.Id);
        reloaded.Status.Should().Be(OutboxStatus.Pending);
        reloaded.AvailableAt.Should().Be(_clock.UtcNow.AddMinutes(1));
        var attempt = await verify.OutboxMessageAttempts.SingleAsync(a => a.OutboxMessageId == message.Id);
        attempt.Outcome.Should().Be(OutboxAttemptOutcome.RetryableFailure);
    }

    [Fact]
    public async Task C6_and_C7_real_dispatcher_makes_final_and_non_retryable_consumer_failures_terminal()
    {
        await using var seed = _fixture.CreateContext();
        var finalAttempt = Seed(maxAttempts: 1, key: "probe:final");
        seed.OutboxMessages.Add(finalAttempt);
        await seed.SaveChangesAsync();

        var consumer = new ProbeIntegrationEventConsumer(_fixture.ConnectionString) { Mode = ProbeMode.RetryableFailure };
        await using var provider = BuildProvider(consumer);
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(1, "probe-worker");

        var nonRetryable = Seed(key: "probe:nonretry");
        seed.OutboxMessages.Add(nonRetryable);
        await seed.SaveChangesAsync();
        consumer.Mode = ProbeMode.NonRetryableFailure;
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(1, "probe-worker");

        await using var verify = _fixture.CreateContext();
        var final = await verify.OutboxMessages.SingleAsync(m => m.Id == finalAttempt.Id);
        var nonRetry = await verify.OutboxMessages.SingleAsync(m => m.Id == nonRetryable.Id);
        final.Status.Should().Be(OutboxStatus.Failed);
        final.FailureReason.Should().Be("RETRY_BUDGET_EXHAUSTED");
        nonRetry.Status.Should().Be(OutboxStatus.Failed);
        nonRetry.FailureReason.Should().Be("NON_RETRYABLE");
    }

    [Fact]
    public async Task C11_duplicate_delivery_performs_one_durable_effect_and_both_deliveries_succeed()
    {
        await using var seed = _fixture.CreateContext();
        var message = Seed(key: "document:render-request-probe");
        seed.OutboxMessages.Add(message);
        await seed.SaveChangesAsync();

        var consumer = new ProbeIntegrationEventConsumer(_fixture.ConnectionString) { Mode = ProbeMode.DurableSuccess };
        await using var provider = BuildProvider(consumer);

        // Simulate a crash after the durable consumer effect but before the fenced completion.
        ClaimedMessage firstClaim;
        await using (var scope = provider.CreateAsyncScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
            firstClaim = (await processor.ClaimBatchAsync(1, "crashed-worker")).Single();
            await consumer.HandleAsync(firstClaim, CancellationToken.None);
        }
        _clock.UtcNow = _clock.UtcNow.Add(OutboxProcessor.DefaultLeaseDuration).AddSeconds(1);
        await using (var scope = provider.CreateAsyncScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
            await processor.ReclaimExpiredLeasesAsync();
            await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(1, "replacement-worker");
        }

        await using var verify = _fixture.CreateContext();
        (await verify.Database.SqlQueryRaw<int>("SELECT invocation_count AS \"Value\" FROM platform.integration_probe_effect WHERE idempotency_key = 'document:render-request-probe'").SingleAsync()).Should().Be(1);
        (await verify.OutboxMessages.SingleAsync(m => m.Id == message.Id)).Status.Should().Be(OutboxStatus.Processed);
        var history = await verify.OutboxMessageAttempts.Where(a => a.OutboxMessageId == message.Id).OrderBy(a => a.StartedAt).ToListAsync();
        history.Should().HaveCount(2);
        history.Should().Contain(a => a.Outcome == OutboxAttemptOutcome.LeaseExpired);
        history.Should().Contain(a => a.Outcome == OutboxAttemptOutcome.Succeeded);
        consumer.InvocationCount.Should().Be(2);
    }

    [Fact]
    public async Task C10_retention_prunes_terminal_records_and_keeps_unresolved_failure_and_history()
    {
        await using var seed = _fixture.CreateContext();
        var oldProcessed = Seed(key: "retention:old-processed");
        var recentProcessed = Seed(key: "retention:recent-processed");
        var activeFailure = Seed(key: "retention:active-failure");
        var dismissedFailure = Seed(key: "retention:dismissed-failure");
        seed.AddRange(oldProcessed, recentProcessed, activeFailure, dismissedFailure);
        await seed.SaveChangesAsync();

        await seed.Database.ExecuteSqlRawAsync("""
            UPDATE platform.outbox_message
            SET status = 'Processed', processed_at = {0}, available_at = NULL
            WHERE id = {1};
            UPDATE platform.outbox_message
            SET status = 'Processed', processed_at = {2}, available_at = NULL
            WHERE id = {3};
            UPDATE platform.outbox_message
            SET status = 'Failed', failure_disposition = 'Active', failed_at = {0}, available_at = NULL
            WHERE id = {4};
            UPDATE platform.outbox_message
            SET status = 'Failed', failure_disposition = 'Dismissed', failed_at = {13}, dismissed_at = {13}, available_at = NULL
            WHERE id = {5};
            INSERT INTO platform.outbox_message_attempt (id, outbox_message_id, execution_generation, attempt_number, processing_token, started_at)
            VALUES ({6}, {1}, 1, 1, {7}, {8}), ({9}, {4}, 1, 1, {10}, {8}), ({11}, {5}, 1, 1, {12}, {8});
            """, _clock.UtcNow.AddDays(-91), oldProcessed.Id, _clock.UtcNow.AddDays(-89), recentProcessed.Id,
            activeFailure.Id, dismissedFailure.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), _clock.UtcNow,
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), _clock.UtcNow.AddDays(-366));

        await using var provider = BuildProvider(new ProbeIntegrationEventConsumer(_fixture.ConnectionString));
        await using (var scope = provider.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<OutboxRetentionService>().PurgeEligibleAsync()).Should().Be(2);

        await using var verify = _fixture.CreateContext();
        (await verify.OutboxMessages.AnyAsync(m => m.Id == oldProcessed.Id)).Should().BeFalse();
        (await verify.OutboxMessages.AnyAsync(m => m.Id == dismissedFailure.Id)).Should().BeFalse();
        (await verify.OutboxMessages.AnyAsync(m => m.Id == recentProcessed.Id)).Should().BeTrue();
        (await verify.OutboxMessages.AnyAsync(m => m.Id == activeFailure.Id)).Should().BeTrue();
        (await verify.OutboxMessageAttempts.AnyAsync(a => a.OutboxMessageId == oldProcessed.Id)).Should().BeFalse();
        (await verify.OutboxMessageAttempts.AnyAsync(a => a.OutboxMessageId == activeFailure.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task H_RETENTION_001_a_racing_purge_and_requeue_never_leave_the_message_survived_with_its_history_gone()
    {
        await using var seed = _fixture.CreateContext();
        var message = Seed(key: "race:purge-vs-requeue");
        seed.OutboxMessages.Add(message);
        await seed.SaveChangesAsync();

        var farPast = _clock.UtcNow.AddDays(-366);
        await seed.Database.ExecuteSqlRawAsync("""
            UPDATE platform.outbox_message
            SET status = 'Failed', failure_disposition = 'Dismissed', failed_at = {0}, dismissed_at = {0}, available_at = NULL
            WHERE id = {1};
            INSERT INTO platform.outbox_message_attempt (id, outbox_message_id, execution_generation, attempt_number, processing_token, started_at, finished_at, outcome)
            VALUES ({2}, {1}, 1, 1, {3}, {0}, {0}, 'RetryableFailure');
            """, farPast, message.Id, Guid.CreateVersion7(), Guid.CreateVersion7());

        // A manual connection acquires the SAME row lock the real PurgeEligibleAsync's own
        // SELECT ... FOR UPDATE would also need, and holds it open — so BOTH the real
        // PurgeEligibleAsync and the real OutboxAdministrationService.RequeueAsync, started
        // concurrently below, genuinely queue behind it rather than merely running one after the
        // other. Releasing the gate (with no effect of its own) is what starts the actual race;
        // Postgres's own lock manager, not a guessed delay, decides which waiter goes first.
        await using var gateConnection = new NpgsqlConnection(_fixture.ConnectionString);
        await gateConnection.OpenAsync();
        var gatePid = (int)(await new NpgsqlCommand("SELECT pg_backend_pid();", gateConnection).ExecuteScalarAsync())!;
        await using var gateTransaction = await gateConnection.BeginTransactionAsync();
        await using (var gate = new NpgsqlCommand("SELECT id FROM platform.outbox_message WHERE id = @id FOR UPDATE;", gateConnection, gateTransaction))
        {
            gate.Parameters.AddWithValue("id", message.Id);
            await gate.ExecuteNonQueryAsync();
        }

        await using var purgeProvider = BuildProvider(new ProbeIntegrationEventConsumer(_fixture.ConnectionString));
        await using var purgeScope = purgeProvider.CreateAsyncScope();
        var purgeContext = purgeScope.ServiceProvider.GetRequiredService<VerceDbContext>();
        await purgeContext.Database.OpenConnectionAsync();
        var purgePid = (int)(await new NpgsqlCommand("SELECT pg_backend_pid();", (NpgsqlConnection)purgeContext.Database.GetDbConnection()).ExecuteScalarAsync())!;
        var purgeTask = Task.Run(() => purgeScope.ServiceProvider.GetRequiredService<OutboxRetentionService>().PurgeEligibleAsync());

        await using var requeueProvider = BuildProvider(new ProbeIntegrationEventConsumer(_fixture.ConnectionString));
        await using var requeueScope = requeueProvider.CreateAsyncScope();
        var requeueContext = requeueScope.ServiceProvider.GetRequiredService<VerceDbContext>();
        await requeueContext.Database.OpenConnectionAsync();
        var requeuePid = (int)(await new NpgsqlCommand("SELECT pg_backend_pid();", (NpgsqlConnection)requeueContext.Database.GetDbConnection()).ExecuteScalarAsync())!;
        var requeueTask = Task.Run(() => requeueScope.ServiceProvider.GetRequiredService<OutboxAdministrationService>()
            .RequeueAsync(message.Id, Guid.CreateVersion7(), "racing revival attempt"));

        await WaitUntilBothWaitingOnLockAsync(purgePid, requeuePid, gatePid);
        await gateTransaction.CommitAsync();

        await Task.WhenAll(purgeTask, requeueTask);

        await using var verify = _fixture.CreateContext();
        var survived = await verify.OutboxMessages.AnyAsync(m => m.Id == message.Id);
        var historyExists = await verify.OutboxMessageAttempts.AnyAsync(a => a.OutboxMessageId == message.Id);

        if (survived)
            historyExists.Should().BeTrue("a message that survives a racing purge must keep its full attempt history — H-RETENTION-001");
        else
            historyExists.Should().BeFalse("a purged message must never leave orphaned attempt history behind");
    }

    /// <summary>Polls pg_stat_activity (a real signal, not a guessed delay) until BOTH the purge
    /// and requeue backends are genuinely blocked waiting on the gate's lock, proving the two
    /// operations are actually racing rather than incidentally sequential.</summary>
    private async Task WaitUntilBothWaitingOnLockAsync(int purgePid, int requeuePid, int gatePid)
    {
        await using var probe = new NpgsqlConnection(_fixture.ConnectionString);
        await probe.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var cmd = new NpgsqlCommand(
                """
                SELECT count(*) FROM pg_stat_activity
                WHERE pid = ANY(@pids) AND wait_event_type = 'Lock';
                """, probe);
            cmd.Parameters.AddWithValue("pids", new[] { purgePid, requeuePid });
            var waiting = (long)(await cmd.ExecuteScalarAsync())!;
            if (waiting == 2) return;
            await Task.Delay(20);
        }

        throw new TimeoutException(
            $"Expected both pid {purgePid} (purge) and pid {requeuePid} (requeue) to be blocked waiting on " +
            $"gate pid {gatePid}'s lock within 10s, but they never both queued — the race never became real.");
    }

    [Fact]
    public async Task C12_production_startup_validation_accepts_only_one_consumer_below_the_lease()
    {
        await using var valid = BuildProvider(new ProbeIntegrationEventConsumer(_fixture.ConnectionString));
        var validator = valid.GetServices<IHostedService>().OfType<OutboxConsumerStartupValidator>().Single();
        await validator.StartAsync(CancellationToken.None);

        var invalid = new ProbeIntegrationEventConsumer(_fixture.ConnectionString) { Timeout = OutboxProcessor.DefaultLeaseDuration };
        await using var invalidProvider = BuildProvider(invalid);
        var invalidValidator = invalidProvider.GetServices<IHostedService>().OfType<OutboxConsumerStartupValidator>().Single();
        var act = async () => await invalidValidator.StartAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task M_C12_001_production_startup_validation_rejects_duplicate_EventType_consumer_registrations()
    {
        // Both probes share the SAME EventType ("ProbeIntegrationEvent" — a const on the type),
        // so registering two of them reproduces exactly the "two consumers bound to one event
        // type" defect the real OutboxConsumerRegistry constructor must reject at startup.
        await using var provider = BuildProviderWithConsumers(
            new ProbeIntegrationEventConsumer(_fixture.ConnectionString),
            new ProbeIntegrationEventConsumer(_fixture.ConnectionString));
        var validator = provider.GetServices<IHostedService>().OfType<OutboxConsumerStartupValidator>().Single();

        var act = async () => await validator.StartAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*{ProbeIntegrationEventConsumer.Type}*");
    }

    private enum ProbeMode { Success, RetryableFailure, NonRetryableFailure, DurableSuccess }

    private sealed class ProbeIntegrationEventConsumer : IIntegrationEventConsumer
    {
        public const string Type = "ProbeIntegrationEvent";
        private readonly string _connectionString;
        public ProbeIntegrationEventConsumer(string connectionString) => _connectionString = connectionString;
        public string EventType => Type;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
        public ProbeMode Mode { get; set; } = ProbeMode.Success;
        public int InvocationCount { get; private set; }

        public async Task HandleAsync(ClaimedMessage message, CancellationToken cancellationToken)
        {
            InvocationCount++;
            if (Mode == ProbeMode.NonRetryableFailure) throw new NonRetryableOutboxException("probe payload is invalid");
            if (Mode == ProbeMode.RetryableFailure) throw new InvalidOperationException("probe transient failure");
            if (Mode == ProbeMode.DurableSuccess)
            {
                await using var connection = new Npgsql.NpgsqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new Npgsql.NpgsqlCommand("""
                    INSERT INTO platform.integration_probe_effect (idempotency_key)
                    VALUES (@key) ON CONFLICT (idempotency_key) DO NOTHING;
                    """, connection);
                command.Parameters.AddWithValue("key", message.IdempotencyKey!);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }
}
