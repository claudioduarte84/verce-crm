using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Verce.IntegrationTests.Auth;
using Verce.Platform.Outbox;
using Verce.Platform.Persistence;
using Verce.Platform.Scheduling;

namespace Verce.IntegrationTests.Outbox;

/// <summary>
/// B-OUTBOX-001 (S1 Gate Corrections): proves the outbox actually runs on its own, on a real
/// schedule, through the REAL host — not merely that the dispatcher/retention/reclaim SERVICES
/// exist and work when called manually (that was already covered elsewhere). Every test here
/// enables Quartz explicitly (<c>Outbox:SchedulingEnabled=true</c>); every OTHER test in this
/// project leaves it disabled by <see cref="VerceWebApplicationFactory"/>'s own default so it
/// never races a live scheduler.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OutboxSchedulingTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public OutboxSchedulingTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE platform.outbox_message_attempt, platform.outbox_message RESTART IDENTITY CASCADE;
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private VerceWebApplicationFactory CreateSchedulingFactory(IReadOnlyDictionary<string, string?>? overrides = null)
    {
        // VerceWebApplicationFactory's own constructor resets Quartz.Logging.LogProvider and
        // serializes all environment mutation on a process-wide lock (M-TESTHOST-001) — nothing
        // extra is needed here.
        var settings = new Dictionary<string, string?> { ["Outbox:SchedulingEnabled"] = "true" };
        if (overrides is not null) foreach (var (key, value) in overrides) settings[key] = value;
        return new VerceWebApplicationFactory(_fixture.ConnectionString, settings);
    }

    private static async Task<IReadOnlyCollection<ITrigger>> GetTriggersAsync(IServiceProvider services, JobKey jobKey)
    {
        var schedulerFactory = services.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler();
        return await scheduler.GetTriggersOfJob(jobKey);
    }

    // ---- Registration (§60): each of the three approved jobs is actually bound to a trigger ----

    [Fact]
    public async Task Registration_the_dispatcher_job_is_registered_with_a_trigger()
    {
        await using var factory = CreateSchedulingFactory();
        var triggers = await GetTriggersAsync(factory.Services, OutboxJobKeys.Dispatcher);
        triggers.Should().ContainSingle();
    }

    [Fact]
    public async Task Registration_the_lease_reclaim_job_is_registered_with_a_trigger()
    {
        await using var factory = CreateSchedulingFactory();
        var triggers = await GetTriggersAsync(factory.Services, OutboxJobKeys.LeaseReclaim);
        triggers.Should().ContainSingle();
    }

    [Fact]
    public async Task Registration_the_retention_job_is_registered_with_a_trigger()
    {
        await using var factory = CreateSchedulingFactory();
        var triggers = await GetTriggersAsync(factory.Services, OutboxJobKeys.Retention);
        triggers.Should().ContainSingle();
    }

    // ---- Cadence (§60, ARCHITECTURE.md §6.1): every 15s / every 1min / daily by default ----

    [Fact]
    public async Task Cadence_the_dispatcher_job_defaults_to_a_15_second_interval()
    {
        await using var factory = CreateSchedulingFactory();
        var triggers = await GetTriggersAsync(factory.Services, OutboxJobKeys.Dispatcher);
        var simple = triggers.Single().Should().BeAssignableTo<ISimpleTrigger>().Subject;
        simple.RepeatInterval.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Cadence_the_lease_reclaim_job_defaults_to_a_1_minute_interval()
    {
        await using var factory = CreateSchedulingFactory();
        var triggers = await GetTriggersAsync(factory.Services, OutboxJobKeys.LeaseReclaim);
        var simple = triggers.Single().Should().BeAssignableTo<ISimpleTrigger>().Subject;
        simple.RepeatInterval.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Cadence_the_retention_job_defaults_to_a_daily_cron_schedule()
    {
        await using var factory = CreateSchedulingFactory();
        var triggers = await GetTriggersAsync(factory.Services, OutboxJobKeys.Retention);
        var cron = triggers.Single().Should().BeAssignableTo<ICronTrigger>().Subject;
        cron.CronExpressionString.Should().Be("0 0 3 * * ?");
    }

    // ---- Host execution (§60): the scheduler, running in the REAL host, actually produces the
    // effect on its own — nothing in the test calls DispatchBatchAsync/ReclaimExpiredLeasesAsync/
    // PurgeEligibleAsync directly. Fast test cadences (1s) keep this bounded without a guessed sleep.

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Condition was never satisfied within {timeout}.");
    }

    [Fact]
    public async Task HostExecution_the_dispatcher_job_claims_a_pending_message_on_its_own()
    {
        await using var factory = CreateSchedulingFactory(new Dictionary<string, string?>
        {
            ["Outbox:DispatchIntervalSeconds"] = "1",
        });

        await using var seed = _fixture.CreateContext();
        var message = OutboxMessage.Enqueue(
            eventType: "SchedulingProbeEvent", payloadJson: "{}", idempotencyKey: "scheduling:dispatch",
            correlationId: Guid.CreateVersion7(), requestId: null, actorUserId: null,
            aggregateType: "SchedulingProbe", aggregateId: null, now: DateTimeOffset.UtcNow, maxAttempts: 5);
        seed.OutboxMessages.Add(message);
        await seed.SaveChangesAsync();

        _ = factory.Services; // force the host (and its scheduler) to actually start

        // No registered consumer exists for this event type in the production host (S1 ships
        // with zero business consumers) — the dispatcher claiming it and failing it as
        // NON_RETRYABLE is exactly the OutboxConsumerRegistry contract, and still proves the job
        // fired on its own: attempt_count only ever moves at claim time.
        await WaitUntilAsync(async () =>
        {
            await using var verify = _fixture.CreateContext();
            var current = await verify.OutboxMessages.SingleAsync(m => m.Id == message.Id);
            return current.AttemptCount > 0;
        }, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task HostExecution_the_lease_reclaim_job_returns_a_crashed_lease_to_pending_on_its_own()
    {
        await using var factory = CreateSchedulingFactory(new Dictionary<string, string?>
        {
            ["Outbox:LeaseReclaimIntervalSeconds"] = "1",
        });

        await using var seed = _fixture.CreateContext();
        var message = OutboxMessage.Enqueue(
            eventType: "SchedulingProbeEvent", payloadJson: "{}", idempotencyKey: "scheduling:reclaim",
            correlationId: Guid.CreateVersion7(), requestId: null, actorUserId: null,
            aggregateType: "SchedulingProbe", aggregateId: null, now: DateTimeOffset.UtcNow, maxAttempts: 5);
        seed.OutboxMessages.Add(message);
        await seed.SaveChangesAsync();

        // A worker that crashed mid-attempt: PROCESSING, lease already expired, one attempt spent.
        await seed.Database.ExecuteSqlRawAsync("""
            UPDATE platform.outbox_message
            SET status = 'Processing', processing_token = {0}, lease_until = {1}, attempt_count = 1
            WHERE id = {2};
            """, Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(-10), message.Id);

        _ = factory.Services;

        await WaitUntilAsync(async () =>
        {
            await using var verify = _fixture.CreateContext();
            var current = await verify.OutboxMessages.SingleAsync(m => m.Id == message.Id);
            return current.Status == OutboxStatus.Pending;
        }, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task HostExecution_the_retention_job_purges_an_eligible_processed_message_on_its_own()
    {
        // Quartz cron is seconds-first — fires every second, fast enough to bound this test
        // without touching the approved production expression anywhere else.
        await using var factory = CreateSchedulingFactory(new Dictionary<string, string?>
        {
            ["Outbox:RetentionCronSchedule"] = "* * * * * ?",
        });

        await using var seed = _fixture.CreateContext();
        var message = OutboxMessage.Enqueue(
            eventType: "SchedulingProbeEvent", payloadJson: "{}", idempotencyKey: "scheduling:retention",
            correlationId: Guid.CreateVersion7(), requestId: null, actorUserId: null,
            aggregateType: "SchedulingProbe", aggregateId: null, now: DateTimeOffset.UtcNow, maxAttempts: 5);
        seed.OutboxMessages.Add(message);
        await seed.SaveChangesAsync();
        await seed.Database.ExecuteSqlRawAsync("""
            UPDATE platform.outbox_message
            SET status = 'Processed', processed_at = {0}, available_at = NULL
            WHERE id = {1};
            """, DateTimeOffset.UtcNow.AddDays(-91), message.Id);

        _ = factory.Services;

        await WaitUntilAsync(async () =>
        {
            await using var verify = _fixture.CreateContext();
            return !await verify.OutboxMessages.AnyAsync(m => m.Id == message.Id);
        }, TimeSpan.FromSeconds(15));
    }
}
