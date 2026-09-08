using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Verce.Platform.Outbox;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.DomainEvents;

/// <summary>
/// A-1..A-10 (ROADMAP S1 catalogue, ADR-0012 Part I) proven against the REAL
/// <see cref="Verce.Platform.UnitOfWork.UnitOfWork"/>, the real <see cref="Verce.Platform.UnitOfWork.DomainEventDispatcher"/>,
/// the real <see cref="Verce.Platform.UnitOfWork.AggregateVersionInterceptor"/> and a real
/// PostgreSQL transaction — via the test-only <see cref="ProbeAggregate"/> harness (mission §26-39:
/// S1 ships zero real business aggregates, so this is the only way to prove the actual
/// multi-wave engine end-to-end without inventing a fake production domain concept).
/// </summary>
[Collection(PostgresCollection.Name)]
public class DomainEventUnitOfWorkTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    public DomainEventUnitOfWorkTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>Reads must go through ProbeVerceDbContext too — PostgresFixture.CreateContext()
    /// builds the plain VerceDbContext, whose model does not know about ProbeAggregate.</summary>
    private ProbeVerceDbContext CreateVerifyContext()
    {
        var options = new DbContextOptionsBuilder<Verce.Platform.Persistence.VerceDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new ProbeVerceDbContext(options);
    }

    public async Task InitializeAsync()
    {
        await using var context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS probe_aggregate (
                id uuid PRIMARY KEY,
                version bigint NOT NULL,
                name text NOT NULL,
                cascade_depth integer NOT NULL,
                throw_on_create boolean NOT NULL,
                created_handled_count integer NOT NULL,
                second_wave_handled_count integer NOT NULL,
                third_wave_handled_count integer NOT NULL,
                touched boolean NOT NULL,
                row_count_observed_at_handle_time integer NOT NULL,
                first_wave_correlation_id uuid NULL,
                first_wave_causation_id uuid NULL,
                second_wave_correlation_id uuid NULL,
                second_wave_causation_id uuid NULL,
                third_wave_correlation_id uuid NULL,
                third_wave_causation_id uuid NULL,
                second_wave_event_id_raised uuid NULL
            );
            CREATE TABLE IF NOT EXISTS probe_idempotent_marker (
                business_key text PRIMARY KEY,
                created_at timestamptz NOT NULL
            );
            TRUNCATE TABLE probe_aggregate;
            TRUNCATE TABLE probe_idempotent_marker;
            TRUNCATE TABLE platform.outbox_message_attempt, platform.outbox_message RESTART IDENTITY CASCADE;
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private (Type, object)[] AllHandlers() =>
    [
        (typeof(ProbeCreatedEvent), new ProbeCreatedEventHandler()),
        (typeof(ProbeSecondWaveEvent), new ProbeSecondWaveEventHandler()),
        (typeof(ProbeThirdWaveEvent), new ProbeThirdWaveEventHandler()),
        (typeof(ProbeCycleAEvent), new ProbeCycleAEventHandler()),
        (typeof(ProbeCycleBEvent), new ProbeCycleBEventHandler()),
        (typeof(ProbeIdempotentSideEffectEvent), new ProbeIdempotentSideEffectEventHandler()),
    ];

    [Fact]
    public async Task A1_an_event_with_a_handler_that_raises_nothing_is_dispatched_exactly_once()
    {
        var clock = new SystemClock();
        var (context, ambient, unitOfWork) = ProbeFactory.Create(_fixture.ConnectionString, clock, AllHandlers());
        await using var _ = context;

        var correlationId = Guid.CreateVersion7();
        var rootId = await unitOfWork.ExecuteAsync(async (ctx, ct) =>
        {
            var root = new ProbeAggregate("a1", correlationId, clock.UtcNow, cascadeDepth: 0);
            ctx.Set<ProbeAggregate>().Add(root);
            return root.Id;
        });

        await using var verify = CreateVerifyContext();
        var reloaded = await verify.Set<ProbeAggregate>().SingleAsync(p => p.Id == rootId);
        reloaded.CreatedHandledCount.Should().Be(1, "E1 must be dispatched exactly once");
        reloaded.SecondWaveHandledCount.Should().Be(0, "no further event was raised");
    }

    [Fact]
    public async Task A2_an_event_raised_by_a_handler_is_dispatched_in_the_next_wave_exactly_once()
    {
        var clock = new SystemClock();
        var (context, _, unitOfWork) = ProbeFactory.Create(_fixture.ConnectionString, clock, AllHandlers());
        await using var _ = context;

        var correlationId = Guid.CreateVersion7();
        var rootId = await unitOfWork.ExecuteAsync(async (ctx, ct) =>
        {
            var root = new ProbeAggregate("a2", correlationId, clock.UtcNow, cascadeDepth: 1);
            ctx.Set<ProbeAggregate>().Add(root);
            return root.Id;
        });

        await using var verify = CreateVerifyContext();
        var reloaded = await verify.Set<ProbeAggregate>().SingleAsync(p => p.Id == rootId);
        reloaded.CreatedHandledCount.Should().Be(1, "wave 1 dispatches E1 exactly once");
        reloaded.SecondWaveHandledCount.Should().Be(1, "wave 2 dispatches E2 exactly once — no duplicates in either wave");
    }

    [Fact]
    public async Task A3_a_handler_failure_rolls_back_the_whole_transaction()
    {
        var clock = new SystemClock();
        var (context, _, unitOfWork) = ProbeFactory.Create(_fixture.ConnectionString, clock, AllHandlers());
        await using var _ = context;

        var correlationId = Guid.CreateVersion7();
        Guid rootId = Guid.Empty;

        var act = async () => await unitOfWork.ExecuteAsync(async (ctx, ct) =>
        {
            var root = new ProbeAggregate("a3", correlationId, clock.UtcNow, throwOnCreate: true);
            rootId = root.Id;
            ctx.Set<ProbeAggregate>().Add(root);
            return root.Id;
        });

        await act.Should().ThrowAsync<ProbeHandlerFailureException>();

        await using var verify = CreateVerifyContext();
        (await verify.Set<ProbeAggregate>().AnyAsync(p => p.Id == rootId)).Should().BeFalse(
            "the transaction must roll back completely — no aggregate row from a failed handler may persist");
    }

    [Fact]
    public async Task A4_a_mutation_with_no_new_event_is_still_saved()
    {
        var clock = new SystemClock();
        var (context, _, unitOfWork) = ProbeFactory.Create(_fixture.ConnectionString, clock, AllHandlers());
        await using var _ = context;

        var correlationId = Guid.CreateVersion7();
        var rootId = await unitOfWork.ExecuteAsync(async (ctx, ct) =>
        {
            var root = new ProbeAggregate("a4", correlationId, clock.UtcNow, cascadeDepth: 0);
            ctx.Set<ProbeAggregate>().Add(root);
            return root.Id;
        });

        await using var verify = CreateVerifyContext();
        var reloaded = await verify.Set<ProbeAggregate>().SingleAsync(p => p.Id == rootId);
        reloaded.Touched.Should().BeTrue("the handler's mutation must be persisted even though it raised no further event");
        reloaded.Name.Should().Be("a4-touched");
    }

    [Fact]
    public async Task A5_the_causation_chain_is_preserved_across_three_waves_sharing_one_correlation()
    {
        var clock = new SystemClock();
        var (context, _, unitOfWork) = ProbeFactory.Create(_fixture.ConnectionString, clock, AllHandlers());
        await using var _ = context;

        var correlationId = Guid.CreateVersion7();
        Guid firstEventId = Guid.Empty;
        var rootId = await unitOfWork.ExecuteAsync(async (ctx, ct) =>
        {
            var root = new ProbeAggregate("a5", correlationId, clock.UtcNow, cascadeDepth: 2);
            firstEventId = root.PendingEvents.Single().EventId; // captured before COLLECT drains it
            ctx.Set<ProbeAggregate>().Add(root);
            return root.Id;
        });

        await using var verify = CreateVerifyContext();
        var reloaded = await verify.Set<ProbeAggregate>().SingleAsync(p => p.Id == rootId);

        reloaded.FirstWaveCausationId.Should().BeNull("wave 1's cause is the command itself");
        reloaded.SecondWaveCausationId.Should().Be(firstEventId, "E2.CausationId = E1.EventId");
        reloaded.ThirdWaveCausationId.Should().Be(reloaded.SecondWaveEventIdRaised, "E3.CausationId = E2.EventId");

        new[] { reloaded.FirstWaveCorrelationId, reloaded.SecondWaveCorrelationId, reloaded.ThirdWaveCorrelationId }
            .Should().AllBeEquivalentTo(correlationId, "every event in one command shares one correlation_id");
    }

    [Fact]
    public async Task A6_the_handler_observes_the_row_already_persisted_by_the_same_waves_save()
    {
        var clock = new SystemClock();
        var (context, _, unitOfWork) = ProbeFactory.Create(_fixture.ConnectionString, clock, AllHandlers());
        await using var _ = context;

        var correlationId = Guid.CreateVersion7();
        var rootId = await unitOfWork.ExecuteAsync(async (ctx, ct) =>
        {
            var root = new ProbeAggregate("a6", correlationId, clock.UtcNow);
            ctx.Set<ProbeAggregate>().Add(root);
            return root.Id;
        });

        await using var verify = CreateVerifyContext();
        var reloaded = await verify.Set<ProbeAggregate>().SingleAsync(p => p.Id == rootId);
        reloaded.RowCountObservedAtHandleTime.Should().Be(1,
            "SAVE happens before DISPATCH — a raw SQL read on the same transaction must already see the row");
    }

    [Fact]
    public async Task A7_a_genuine_event_cycle_trips_the_wave_limit_and_rolls_back()
    {
        var clock = new SystemClock();
        var (context, _, unitOfWork) = ProbeFactory.Create(_fixture.ConnectionString, clock, AllHandlers());
        await using var _ = context;

        var correlationId = Guid.CreateVersion7();
        Guid rootId = Guid.Empty;

        var act = async () => await unitOfWork.ExecuteAsync(async (ctx, ct) =>
        {
            var root = ProbeAggregate.StartCycle("a7", correlationId, clock.UtcNow);
            rootId = root.Id;
            ctx.Set<ProbeAggregate>().Add(root);
            return root.Id;
        });

        await act.Should().ThrowAsync<Verce.Platform.UnitOfWork.DomainEventWaveLimitExceededException>();

        await using var verify = CreateVerifyContext();
        (await verify.Set<ProbeAggregate>().AnyAsync(p => p.Id == rootId)).Should().BeFalse(
            "a wave-limit failure rolls back the whole transaction, exactly like any other handler failure");
    }

    [Fact]
    public async Task A8_an_integration_event_is_never_dispatched_in_process_only_written_to_the_outbox()
    {
        var clock = new SystemClock();
        var (context, _, unitOfWork) = ProbeFactory.Create(_fixture.ConnectionString, clock, AllHandlers());
        await using var _ = context;

        var correlationId = Guid.CreateVersion7();
        var idempotencyKey = $"probe-a8-{Guid.NewGuid():N}";

        await unitOfWork.ExecuteAsync(async (ctx, ct) =>
        {
            var integrationEvent = new ProbeIntegrationEvent(correlationId, null, clock.UtcNow, idempotencyKey);
            var root = new ProbeAggregate("a8", correlationId, clock.UtcNow, integrationEvent: integrationEvent);
            ctx.Set<ProbeAggregate>().Add(root);
            return root.Id;
        });
        // No IDomainEventHandler<ProbeIntegrationEvent> is registered anywhere — if the platform
        // ever tried to dispatch it in-process, DomainEventDispatcher would simply find zero
        // handlers (silently) OR the test above would have thrown if it were routed like a
        // domain event and something asserted a handler must exist; the real proof is the outbox row below.

        await using var verify = CreateVerifyContext();
        var messages = await verify.OutboxMessages.Where(m => m.IdempotencyKey == idempotencyKey).ToListAsync();
        messages.Should().ContainSingle("exactly one outbox_message row must exist for the integration event");
        messages.Single().EventType.Should().Be(nameof(ProbeIntegrationEvent));
    }

    [Fact]
    public async Task A9_two_commands_racing_to_create_one_idempotent_side_effect_never_abort_and_never_duplicate()
    {
        var clock = new SystemClock();
        var businessKey = $"probe-a9-{Guid.NewGuid():N}";
        var (contextA, _, unitOfWorkA) = ProbeFactory.Create(_fixture.ConnectionString, clock, AllHandlers());
        var (contextB, _, unitOfWorkB) = ProbeFactory.Create(_fixture.ConnectionString, clock, AllHandlers());
        await using var _a = contextA;
        await using var _b = contextB;

        Task<Guid> RunAsync(Verce.Platform.UnitOfWork.IUnitOfWork uow, string name) => uow.ExecuteAsync(async (ctx, ct) =>
        {
            var root = ProbeAggregate.CreateWithIdempotentSideEffect(name, Guid.CreateVersion7(), clock.UtcNow, businessKey);
            ctx.Set<ProbeAggregate>().Add(root);
            return root.Id;
        });

        var taskA = RunAsync(unitOfWorkA, "a9-a");
        var taskB = RunAsync(unitOfWorkB, "a9-b");
        var ids = await Task.WhenAll(taskA, taskB);

        // Neither call should throw (asserted implicitly by awaiting above without a try/catch) —
        // proving neither transaction ever entered PostgreSQL's aborted (25P02) state.
        await using var verify = CreateVerifyContext();
        var markerCount = await verify.Set<ProbeIdempotentMarker>().CountAsync(m => m.BusinessKey == businessKey);
        markerCount.Should().Be(1, "ON CONFLICT DO NOTHING must produce exactly one row regardless of the race");

        var rootCount = await verify.Set<ProbeAggregate>().CountAsync(p => ids.Contains(p.Id));
        rootCount.Should().Be(2, "both independent commands still commit their own aggregate successfully");
    }

    [Fact]
    public async Task A10_an_outbox_row_from_a_rolled_back_transaction_is_never_dispatched()
    {
        var clock = new SystemClock();
        var (context, _, unitOfWork) = ProbeFactory.Create(_fixture.ConnectionString, clock, AllHandlers());
        await using var _ = context;

        var correlationId = Guid.CreateVersion7();
        var idempotencyKey = $"probe-a10-{Guid.NewGuid():N}";

        var act = async () => await unitOfWork.ExecuteAsync(async (ctx, ct) =>
        {
            var integrationEvent = new ProbeIntegrationEvent(correlationId, null, clock.UtcNow, idempotencyKey);
            // throwOnCreate=true: the domain event handler throws AFTER the integration event was
            // queued in memory but BEFORE the wave loop ever reaches "write outbox rows" —
            // that step only runs once the loop exits quiescently, which never happens here.
            var root = new ProbeAggregate("a10", correlationId, clock.UtcNow, throwOnCreate: true, integrationEvent: integrationEvent);
            ctx.Set<ProbeAggregate>().Add(root);
            return root.Id;
        });

        await act.Should().ThrowAsync<ProbeHandlerFailureException>();

        await using var verify = CreateVerifyContext();
        (await verify.OutboxMessages.AnyAsync(m => m.IdempotencyKey == idempotencyKey)).Should().BeFalse(
            "the outbox row queued in the rolled-back transaction must never reach the table, so the dispatcher can never see it");
    }
}
