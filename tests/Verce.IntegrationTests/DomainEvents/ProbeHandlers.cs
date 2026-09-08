using Microsoft.EntityFrameworkCore;
using Npgsql;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;

namespace Verce.IntegrationTests.DomainEvents;

public sealed class ProbeCreatedEventHandler : IDomainEventHandler<ProbeCreatedEvent>
{
    public async Task HandleAsync(ProbeCreatedEvent domainEvent, VerceDbContext context, CancellationToken cancellationToken)
    {
        var root = await context.Set<ProbeAggregate>().FirstAsync(p => p.Id == domainEvent.AggregateId, cancellationToken);

        // A-6: read the row back through raw SQL on the SAME connection/transaction — if
        // COLLECT->SAVE->DISPATCH were violated (dispatch before save), this would see 0 rows,
        // not the tracked in-memory value EF would otherwise report regardless of persistence.
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM probe_aggregate WHERE id = @id", connection);
        cmd.Parameters.AddWithValue("id", domainEvent.AggregateId);
        var rowCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));

        root.HandleCreated(domainEvent.CorrelationId, domainEvent.CausationId, domainEvent.EventId, domainEvent.OccurredAtUtc, rowCount);
    }
}

public sealed class ProbeSecondWaveEventHandler : IDomainEventHandler<ProbeSecondWaveEvent>
{
    public async Task HandleAsync(ProbeSecondWaveEvent domainEvent, VerceDbContext context, CancellationToken cancellationToken)
    {
        var root = await context.Set<ProbeAggregate>().FirstAsync(p => p.Id == domainEvent.AggregateId, cancellationToken);
        root.HandleSecondWave(domainEvent.CorrelationId, domainEvent.CausationId, domainEvent.EventId, domainEvent.OccurredAtUtc);
    }
}

public sealed class ProbeThirdWaveEventHandler : IDomainEventHandler<ProbeThirdWaveEvent>
{
    public async Task HandleAsync(ProbeThirdWaveEvent domainEvent, VerceDbContext context, CancellationToken cancellationToken)
    {
        var root = await context.Set<ProbeAggregate>().FirstAsync(p => p.Id == domainEvent.AggregateId, cancellationToken);
        root.HandleThirdWave(domainEvent.CorrelationId, domainEvent.CausationId, domainEvent.OccurredAtUtc);
    }
}

/// <summary>A-7: keeps the cycle alive forever — proof of the wave-limit guard, not legitimate depth.</summary>
public sealed class ProbeCycleAEventHandler : IDomainEventHandler<ProbeCycleAEvent>
{
    public async Task HandleAsync(ProbeCycleAEvent domainEvent, VerceDbContext context, CancellationToken cancellationToken)
    {
        var root = await context.Set<ProbeAggregate>().FirstAsync(p => p.Id == domainEvent.AggregateId, cancellationToken);
        root.RaiseCycleB(domainEvent.CorrelationId, domainEvent.EventId, domainEvent.OccurredAtUtc);
    }
}

public sealed class ProbeCycleBEventHandler : IDomainEventHandler<ProbeCycleBEvent>
{
    public async Task HandleAsync(ProbeCycleBEvent domainEvent, VerceDbContext context, CancellationToken cancellationToken)
    {
        var root = await context.Set<ProbeAggregate>().FirstAsync(p => p.Id == domainEvent.AggregateId, cancellationToken);
        root.RaiseCycleA(domainEvent.CorrelationId, domainEvent.EventId, domainEvent.OccurredAtUtc);
    }
}

/// <summary>A-9: the idempotent shared side effect — ON CONFLICT DO NOTHING is what makes two
/// concurrent commands racing to create it safe (never a 25P02 abort, never a duplicate).</summary>
public sealed class ProbeIdempotentSideEffectEventHandler : IDomainEventHandler<ProbeIdempotentSideEffectEvent>
{
    public async Task HandleAsync(ProbeIdempotentSideEffectEvent domainEvent, VerceDbContext context, CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO probe_idempotent_marker (business_key, created_at)
            VALUES ({domainEvent.BusinessKey}, {domainEvent.OccurredAtUtc})
            ON CONFLICT (business_key) DO NOTHING;
            """, cancellationToken);
    }
}
