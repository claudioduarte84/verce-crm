using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verce.Platform.Outbox;
using Verce.Platform.Persistence;
using Verce.SharedKernel.Domain;
using Verce.SharedKernel.Events;
using Verce.SharedKernel.Time;

namespace Verce.Platform.UnitOfWork;

public interface IUnitOfWork
{
    Task ExecuteAsync(Func<VerceDbContext, CancellationToken, Task> command, CancellationToken cancellationToken = default);
    Task<TResult> ExecuteAsync<TResult>(Func<VerceDbContext, CancellationToken, Task<TResult>> command, CancellationToken cancellationToken = default);
}

/// <summary>
/// One command = one transaction = one <see cref="VerceDbContext"/> = N save/dispatch waves
/// (ADR-0012 §3). Order is COLLECT -&gt; SAVE -&gt; DISPATCH, frozen — never
/// dispatch-before-save, never a commit between waves.
/// </summary>
public sealed class UnitOfWork : IUnitOfWork
{
    /// <summary>
    /// Double the deepest known legitimate chain in this domain (4 waves: approve -&gt; order
    /// -&gt; history -&gt; stock), leaving headroom for two future links. Anything beyond this is
    /// a cycle, not depth (ADR-0012 §7).
    /// </summary>
    public const int MaxEventWaves = 8;

    private readonly VerceDbContext _context;
    private readonly AmbientOperationContext _ambientContext;
    private readonly IDomainEventDispatcher _dispatcher;
    private readonly IClock _clock;

    public UnitOfWork(VerceDbContext context, AmbientOperationContext ambientContext, IDomainEventDispatcher dispatcher, IClock clock)
    {
        _context = context;
        _ambientContext = ambientContext;
        _dispatcher = dispatcher;
        _clock = clock;
    }

    public async Task ExecuteAsync(Func<VerceDbContext, CancellationToken, Task> command, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(async (ctx, ct) =>
        {
            await command(ctx, ct);
            return true;
        }, cancellationToken);
    }

    public async Task<TResult> ExecuteAsync<TResult>(Func<VerceDbContext, CancellationToken, Task<TResult>> command, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        var pendingIntegrationEvents = new List<IIntegrationEvent>();
        var chain = new List<DispatchedEventTrace>();

        try
        {
            // ---- wave 0: the command itself ----
            var result = await command(_context, cancellationToken);

            var waveIndex = 0;

            while (true)
            {
                // ---- COLLECT: drain every tracked aggregate's buffer BEFORE saving ----
                var drained = DrainAllPendingEvents();
                var domainEventsThisWave = drained.OfType<IDomainEvent>().ToList();
                pendingIntegrationEvents.AddRange(drained.OfType<IIntegrationEvent>());

                var hasPendingWrites = _context.ChangeTracker.HasChanges();

                if (domainEventsThisWave.Count == 0 && !hasPendingWrites)
                    break; // quiescent: no pending domain events AND no pending writes

                waveIndex++;
                if (waveIndex > MaxEventWaves)
                    throw new DomainEventWaveLimitExceededException(MaxEventWaves, chain);

                _ambientContext.WaveIndex = waveIndex;

                // ---- SAVE: persist this wave's writes (interceptors run here) ----
                await _context.SaveChangesAsync(cancellationToken);

                // ---- DISPATCH: the frozen snapshot only, never the live buffer ----
                foreach (var domainEvent in domainEventsThisWave)
                {
                    chain.Add(new DispatchedEventTrace(waveIndex, domainEvent.EventType, domainEvent.EventId, domainEvent.CausationId));
                    _ambientContext.CurrentEvent = domainEvent;
                    await _dispatcher.DispatchAsync(domainEvent, _context, cancellationToken);
                }
                _ambientContext.CurrentEvent = null;
                // events raised by handlers just now are in aggregate buffers -> next iteration
            }

            // ---- quiescent: write outbox rows for every integration event accumulated ----
            if (pendingIntegrationEvents.Count > 0)
            {
                foreach (var integrationEvent in pendingIntegrationEvents)
                {
                    var message = OutboxMessage.Enqueue(
                        eventType: integrationEvent.EventType,
                        payloadJson: JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType()),
                        idempotencyKey: integrationEvent.IdempotencyKey,
                        correlationId: integrationEvent.CorrelationId,
                        requestId: _ambientContext.RequestId,
                        actorUserId: _ambientContext.ActorUserId,
                        aggregateType: integrationEvent.GetType().Name,
                        aggregateId: null,
                        now: _clock.UtcNow);
                    _context.Add(message);
                }
                await _context.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private List<IEvent> DrainAllPendingEvents()
    {
        var drained = new List<IEvent>();
        foreach (var entry in _context.ChangeTracker.Entries<AggregateRoot>().ToList())
        {
            if (entry.Entity.PendingEvents.Count == 0) continue;
            drained.AddRange(entry.Entity.DrainPendingEvents());
        }
        return drained;
    }
}
