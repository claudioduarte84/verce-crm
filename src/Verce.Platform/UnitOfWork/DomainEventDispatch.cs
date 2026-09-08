using Verce.Platform.Persistence;
using Verce.SharedKernel.Events;

namespace Verce.Platform.UnitOfWork;

/// <summary>
/// A synchronous domain event handler (ADR-0012 §1, §9). MUST NOT depend on
/// <c>IDbContextFactory&lt;&gt;</c>, <c>IServiceScopeFactory</c>, <c>IServiceProvider</c>,
/// <c>HttpClient</c>, or any I/O abstraction — enforced by Verce.Architecture.Tests. It receives
/// the ambient <see cref="VerceDbContext"/> and must use it only.
/// </summary>
public interface IDomainEventHandler<in TEvent> where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, VerceDbContext context, CancellationToken cancellationToken);
}

/// <summary>Dispatches a drained domain event to every registered handler, in registration
/// order (ADR-0012 §6) — never assembly-scan order.</summary>
public interface IDomainEventDispatcher
{
    Task DispatchAsync(IDomainEvent domainEvent, VerceDbContext context, CancellationToken cancellationToken);
}

/// <summary>
/// One entry in the causation chain accumulated during a Unit of Work, used to make a
/// <see cref="DomainEventWaveLimitExceededException"/> diagnosable from a single log line
/// (ADR-0012 §7).
/// </summary>
public sealed record DispatchedEventTrace(int WaveIndex, string EventType, Guid EventId, Guid? CausationId);

/// <summary>
/// Thrown when a Unit of Work exceeds <see cref="UnitOfWorkOptions.MaxEventWaves"/> — a
/// cycle, not depth (ADR-0012 §7). Rolls back the whole transaction; maps to HTTP 500 as a
/// programming error, never a user error.
/// </summary>
public sealed class DomainEventWaveLimitExceededException : Exception
{
    public IReadOnlyList<DispatchedEventTrace> Chain { get; }

    public DomainEventWaveLimitExceededException(int maxWaves, IReadOnlyList<DispatchedEventTrace> chain)
        : base($"Unit of Work exceeded the maximum of {maxWaves} domain-event waves. " +
               "This indicates a cycle between handlers, not legitimate depth. Chain: " +
               string.Join(" -> ", chain.Select(c => $"[w{c.WaveIndex}]{c.EventType}({c.EventId:N})")))
    {
        Chain = chain;
    }
}
