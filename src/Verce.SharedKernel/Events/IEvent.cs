namespace Verce.SharedKernel.Events;

/// <summary>
/// Common envelope for every event in the system (ADR-0012 §1-2). A concrete event
/// implements EXACTLY ONE of <see cref="IDomainEvent"/> or <see cref="IIntegrationEvent"/> —
/// never this interface directly, and never both. Enforced by Verce.Architecture.Tests.
/// </summary>
public interface IEvent
{
    /// <summary>UUID v7, assigned at construction inside the aggregate.</summary>
    Guid EventId { get; }

    /// <summary>The CLR type name, used for diagnostics and outbox routing.</summary>
    string EventType { get; }

    /// <summary>The moment the domain decided — assigned at construction from IClock.UtcNow.</summary>
    DateTimeOffset OccurredAtUtc { get; }

    /// <summary>One value per command, shared across every Unit of Work wave (ADR-0012 §2).</summary>
    Guid CorrelationId { get; }

    /// <summary>
    /// The EventId of the event currently being dispatched when this one was raised, or null
    /// in wave 1 (its cause is the command itself, identified by CorrelationId).
    /// </summary>
    Guid? CausationId { get; }
}

/// <summary>
/// Synchronous domain event (ADR-0012 §1). Dispatched between save waves, INSIDE the business
/// transaction, through the ambient DbContext only. Handler failure rolls back the whole
/// transaction. Never routes through the outbox — used only when the effect must be atomic
/// with its cause (e.g. QuoteApproved -&gt; create ProductionOrder).
/// </summary>
public interface IDomainEvent : IEvent
{
}

/// <summary>
/// Integration event (ADR-0012 §1, renamed from IPostCommitEvent in the re-gate corrections).
/// Written to the outbox in the same transaction as the business change, processed only AFTER
/// commit, with at-least-once delivery. Consumers MUST be idempotent (ADR-0012 §22).
/// Used when the effect may lag or fail independently of the business action (PDF rendering,
/// AI calls, e-mail, smart-plug polling).
/// </summary>
public interface IIntegrationEvent : IEvent
{
    /// <summary>
    /// The business idempotency key for this effect (ADR-0012 §22). Must be stable across
    /// retries of the SAME logical effect and distinct across genuinely different effects.
    /// </summary>
    string IdempotencyKey { get; }
}

/// <summary>
/// Base implementation providing the envelope fields. Concrete events derive from
/// <see cref="DomainEventBase"/> or implement <see cref="IIntegrationEvent"/> directly with
/// their own idempotency key.
/// </summary>
public abstract class DomainEventBase : IDomainEvent
{
    protected DomainEventBase(Guid correlationId, Guid? causationId, DateTimeOffset occurredAtUtc)
    {
        EventId = Guid.CreateVersion7();
        EventType = GetType().Name;
        OccurredAtUtc = occurredAtUtc;
        CorrelationId = correlationId;
        CausationId = causationId;
    }

    public Guid EventId { get; }
    public string EventType { get; }
    public DateTimeOffset OccurredAtUtc { get; }
    public Guid CorrelationId { get; }
    public Guid? CausationId { get; }
}
