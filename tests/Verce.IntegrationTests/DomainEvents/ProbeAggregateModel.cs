using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verce.Platform.Ownership;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Domain;
using Verce.SharedKernel.Events;

namespace Verce.IntegrationTests.DomainEvents;

// Test-only aggregate proving the A-series contracts (ADR-0012 Part I) against the REAL
// UnitOfWork/VerceDbContext/DomainEventDispatcher/AggregateVersionInterceptor — never a parallel
// fake algorithm. It exists ONLY in Verce.IntegrationTests; no production module references it.
// S1 ships zero real business aggregates, so this is the only way to prove the actual multi-wave
// engine end-to-end before S2's first aggregate arrives.

public sealed class ProbeHandlerFailureException : Exception
{
    public ProbeHandlerFailureException() : base("probe handler deliberately failed (A-3)") { }
}

public sealed class ProbeCreatedEvent : DomainEventBase
{
    public Guid AggregateId { get; }
    public ProbeCreatedEvent(Guid correlationId, Guid? causationId, DateTimeOffset now, Guid aggregateId)
        : base(correlationId, causationId, now) => AggregateId = aggregateId;
}

public sealed class ProbeSecondWaveEvent : DomainEventBase
{
    public Guid AggregateId { get; }
    public ProbeSecondWaveEvent(Guid correlationId, Guid? causationId, DateTimeOffset now, Guid aggregateId)
        : base(correlationId, causationId, now) => AggregateId = aggregateId;
}

public sealed class ProbeThirdWaveEvent : DomainEventBase
{
    public Guid AggregateId { get; }
    public ProbeThirdWaveEvent(Guid correlationId, Guid? causationId, DateTimeOffset now, Guid aggregateId)
        : base(correlationId, causationId, now) => AggregateId = aggregateId;
}

/// <summary>A-7: a self-perpetuating pair — A's handler raises B, B's handler raises A — used
/// ONLY to prove the wave-limit guard trips on a genuine cycle, never legitimate depth.</summary>
public sealed class ProbeCycleAEvent : DomainEventBase
{
    public Guid AggregateId { get; }
    public ProbeCycleAEvent(Guid correlationId, Guid? causationId, DateTimeOffset now, Guid aggregateId)
        : base(correlationId, causationId, now) => AggregateId = aggregateId;
}

public sealed class ProbeCycleBEvent : DomainEventBase
{
    public Guid AggregateId { get; }
    public ProbeCycleBEvent(Guid correlationId, Guid? causationId, DateTimeOffset now, Guid aggregateId)
        : base(correlationId, causationId, now) => AggregateId = aggregateId;
}

/// <summary>A-9: raised by an "approval"-like mutation; its handler ensures a shared,
/// business-keyed side effect exists via a raw ON CONFLICT DO NOTHING insert — the same pattern
/// ADR-0012 §11/A-9 requires for two concurrent commands racing to create one side effect.</summary>
public sealed class ProbeIdempotentSideEffectEvent : DomainEventBase
{
    public Guid AggregateId { get; }
    public string BusinessKey { get; }
    public ProbeIdempotentSideEffectEvent(Guid correlationId, Guid? causationId, DateTimeOffset now, Guid aggregateId, string businessKey)
        : base(correlationId, causationId, now)
    {
        AggregateId = aggregateId;
        BusinessKey = businessKey;
    }
}

/// <summary>A-8/A-10: the ONLY integration event in this harness — routes through the outbox,
/// never through IDomainEventDispatcher (no IDomainEventHandler is ever registered for it).</summary>
public sealed class ProbeIntegrationEvent : IIntegrationEvent
{
    public Guid EventId { get; } = Guid.CreateVersion7();
    public string EventType => nameof(ProbeIntegrationEvent);
    public DateTimeOffset OccurredAtUtc { get; }
    public Guid CorrelationId { get; }
    public Guid? CausationId { get; }
    public string IdempotencyKey { get; }

    public ProbeIntegrationEvent(Guid correlationId, Guid? causationId, DateTimeOffset now, string idempotencyKey)
    {
        CorrelationId = correlationId;
        CausationId = causationId;
        OccurredAtUtc = now;
        IdempotencyKey = idempotencyKey;
    }
}

public sealed class ProbeAggregate : AggregateRoot
{
    public string Name { get; private set; } = string.Empty;
    public int CascadeDepth { get; private set; }
    public bool ThrowOnCreate { get; private set; }

    public int CreatedHandledCount { get; private set; }
    public int SecondWaveHandledCount { get; private set; }
    public int ThirdWaveHandledCount { get; private set; }
    public bool Touched { get; private set; }

    /// <summary>A-6: set by the E1 handler from a RAW SQL row count read through the same
    /// connection/transaction — proves the row was already persisted (COLLECT→SAVE→DISPATCH),
    /// not merely tracked in memory.</summary>
    public int RowCountObservedAtHandleTime { get; private set; }

    public Guid? FirstWaveCorrelationId { get; private set; }
    public Guid? FirstWaveCausationId { get; private set; }
    public Guid? SecondWaveCorrelationId { get; private set; }
    public Guid? SecondWaveCausationId { get; private set; }
    public Guid? ThirdWaveCorrelationId { get; private set; }
    public Guid? ThirdWaveCausationId { get; private set; }
    public Guid? SecondWaveEventIdRaised { get; private set; }

    private ProbeAggregate() { }

    public ProbeAggregate(
        string name, Guid correlationId, DateTimeOffset now,
        int cascadeDepth = 0, bool throwOnCreate = false, ProbeIntegrationEvent? integrationEvent = null)
    {
        Name = name;
        CascadeDepth = cascadeDepth;
        ThrowOnCreate = throwOnCreate;
        Raise(new ProbeCreatedEvent(correlationId, null, now, Id));
        if (integrationEvent is not null) Raise(integrationEvent);
    }

    /// <summary>Bypasses the normal creation event entirely — used only by the A-7 wave-limit
    /// test, which needs to start a cycle, not the create→cascade flow.</summary>
    public static ProbeAggregate StartCycle(string name, Guid correlationId, DateTimeOffset now)
    {
        var root = new ProbeAggregate { Name = name };
        root.Raise(new ProbeCycleAEvent(correlationId, null, now, root.Id));
        return root;
    }

    /// <summary>A-9: bypasses the normal creation event too — this aggregate exists only to
    /// carry the "approval" that triggers the idempotent shared side effect.</summary>
    public static ProbeAggregate CreateWithIdempotentSideEffect(string name, Guid correlationId, DateTimeOffset now, string businessKey)
    {
        var root = new ProbeAggregate { Name = name };
        root.Raise(new ProbeIdempotentSideEffectEvent(correlationId, null, now, root.Id, businessKey));
        return root;
    }

    public void HandleCreated(Guid correlationId, Guid? causationId, Guid eventId, DateTimeOffset now, int rowCountObserved)
    {
        CreatedHandledCount++;
        FirstWaveCorrelationId = correlationId;
        FirstWaveCausationId = causationId;
        RowCountObservedAtHandleTime = rowCountObserved;
        Touched = true;
        Name = Name + "-touched"; // A-4: a mutation with no new event, must still be saved

        if (ThrowOnCreate) throw new ProbeHandlerFailureException();

        if (CascadeDepth >= 1)
        {
            var e2 = new ProbeSecondWaveEvent(correlationId, eventId, now, Id);
            SecondWaveEventIdRaised = e2.EventId;
            Raise(e2);
        }
    }

    public void HandleSecondWave(Guid correlationId, Guid? causationId, Guid eventId, DateTimeOffset now)
    {
        SecondWaveHandledCount++;
        SecondWaveCorrelationId = correlationId;
        SecondWaveCausationId = causationId;
        if (CascadeDepth >= 2)
            Raise(new ProbeThirdWaveEvent(correlationId, eventId, now, Id));
    }

    public void HandleThirdWave(Guid correlationId, Guid? causationId, DateTimeOffset now)
    {
        ThirdWaveHandledCount++;
        ThirdWaveCorrelationId = correlationId;
        ThirdWaveCausationId = causationId;
    }

    public void RaiseCycleA(Guid correlationId, Guid? causationId, DateTimeOffset now) =>
        Raise(new ProbeCycleAEvent(correlationId, causationId, now, Id));

    public void RaiseCycleB(Guid correlationId, Guid? causationId, DateTimeOffset now) =>
        Raise(new ProbeCycleBEvent(correlationId, causationId, now, Id));
}

/// <summary>
/// A-9's idempotent side effect target: a bare, business-keyed marker row a synchronous handler
/// ensures exists via <c>INSERT ... ON CONFLICT (business_key) DO NOTHING</c>. Deliberately
/// decoupled from ProbeAggregate's own Version so this test proves ONLY the idempotent-insert
/// contract, never conflated with root-version conflict handling (that is B-5, tested elsewhere).
/// </summary>
public sealed class ProbeIdempotentMarker
{
    public string BusinessKey { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    private ProbeIdempotentMarker() { }
    public ProbeIdempotentMarker(string businessKey, DateTimeOffset createdAt)
    {
        BusinessKey = businessKey;
        CreatedAt = createdAt;
    }
}

/// <summary>
/// Test-only subclass of the REAL VerceDbContext (Option A, mission §29) — adds test-only tables
/// on top of the actual composed model; no production code ever references this type.
/// </summary>
public sealed class ProbeVerceDbContext : VerceDbContext
{
    public DbSet<ProbeAggregate> ProbeAggregates => Set<ProbeAggregate>();
    public DbSet<ProbeIdempotentMarker> ProbeIdempotentMarkers => Set<ProbeIdempotentMarker>();

    public ProbeVerceDbContext(DbContextOptions<VerceDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ProbeAggregate>(b =>
        {
            b.ToTable("probe_aggregate");
            b.HasKey(x => x.Id);
            b.Property(x => x.Version).IsConcurrencyToken();
        });

        builder.Entity<ProbeIdempotentMarker>(b =>
        {
            b.ToTable("probe_idempotent_marker");
            b.HasKey(x => x.BusinessKey);
        });
    }
}

public static class ProbeFactory
{
    public static (ProbeVerceDbContext Context, AmbientOperationContext Ambient, IUnitOfWork UnitOfWork) Create(
        string connectionString, Verce.SharedKernel.Time.IClock clock, params (Type EventType, object Handler)[] handlers)
    {
        var registry = AggregateOwnershipRegistry.BuildAndValidate(new[] { typeof(ProbeAggregate).Assembly });
        var ambientContext = new AmbientOperationContext(Guid.CreateVersion7(), AuditSource.Api, clock.UtcNow);
        var versionInterceptor = new AggregateVersionInterceptor(registry, ambientContext);

        var options = new DbContextOptionsBuilder<VerceDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(versionInterceptor)
            .Options;

        var context = new ProbeVerceDbContext(options);

        var services = new ServiceCollection();
        foreach (var (eventType, handler) in handlers)
        {
            var handlerInterface = typeof(IDomainEventHandler<>).MakeGenericType(eventType);
            services.AddSingleton(handlerInterface, handler);
        }
        var dispatcher = new DomainEventDispatcher(services.BuildServiceProvider());
        var unitOfWork = new Verce.Platform.UnitOfWork.UnitOfWork(context, ambientContext, dispatcher, clock);

        return (context, ambientContext, unitOfWork);
    }
}
