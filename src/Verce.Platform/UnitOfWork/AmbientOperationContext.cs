using Verce.SharedKernel.Events;

namespace Verce.Platform.UnitOfWork;

/// <summary>
/// Per-request/per-command context shared across every Unit of Work wave (ADR-0012 §10).
/// Registered as a scoped service so every collaborator resolved within one HTTP request or one
/// CLI invocation observes the same instance.
/// </summary>
public sealed class AmbientOperationContext
{
    public Guid CorrelationId { get; }
    public Guid? RequestId { get; }
    public Guid? ActorUserId { get; private set; }
    public string? ActorDisplayName { get; private set; }
    public AuditSource Source { get; }
    public DateTimeOffset OperationStartedAtUtc { get; }

    /// <summary>Mutated by the Unit of Work as it advances through waves.</summary>
    public int WaveIndex { get; internal set; }

    /// <summary>
    /// The event currently being dispatched, if any — read by AggregateRoot.Raise to stamp
    /// CausationId on any event raised by a handler (ADR-0012 §5).
    /// </summary>
    public IEvent? CurrentEvent { get; internal set; }

    /// <summary>
    /// UoW-scoped memory of which aggregate roots have already had their version decided this
    /// Unit of Work (ADR-0011 §2.3-2.4). Prevents deriving "was it Added?" from EF's transient
    /// EntityState, which changes after the first SaveChanges.
    /// </summary>
    public HashSet<Guid> VersionHandledAggregates { get; } = new();

    public AmbientOperationContext(Guid correlationId, AuditSource source, DateTimeOffset operationStartedAtUtc, Guid? requestId = null)
    {
        CorrelationId = correlationId;
        Source = source;
        OperationStartedAtUtc = operationStartedAtUtc;
        RequestId = requestId;
    }

    public void SetActor(Guid? userId, string? displayName)
    {
        ActorUserId = userId;
        ActorDisplayName = displayName;
    }
}

/// <summary>Audit/event actor source (ADR-0010 §Multi-wave alignment). No MIGRATION value —
/// migrations never pass through the SaveChanges interceptor.</summary>
public enum AuditSource
{
    Api,
    Job,
    Cli,
    System,
}
