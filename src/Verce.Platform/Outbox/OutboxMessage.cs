using Verce.SharedKernel.Domain;

namespace Verce.Platform.Outbox;

public enum OutboxStatus
{
    Pending,
    Processing,
    Processed,
    Failed,
}

/// <summary>Meaningful only while Status == Failed (ADR-0012 §18). No Resolved value — a
/// requeued message that succeeds ends at Status = Processed with no disposition at all;
/// success IS what "resolved" means.</summary>
public enum FailureDisposition
{
    Active,
    Dismissed,
}

/// <summary>
/// Technical/framework-adjacent table (ADR-0011 Category 4 — application-owned technical
/// table, marked <see cref="ITechnicalTable"/>): PK is uuid for insertion locality on a hot
/// table, but concurrency is governed by the lease + fencing token, not by
/// <see cref="AggregateRoot.Version"/>.
///
/// Four separate concepts (ADR-0012 §19.1):
///  - business identity:            Id + IdempotencyKey — never changes
///  - current retry round:          ExecutionGeneration — increments on requeue
///  - budget within the round:      AttemptCount vs MaxAttempts
///  - historical attempt identity:  (Id, ExecutionGeneration, AttemptNumber) — in OutboxMessageAttempt
/// </summary>
public sealed class OutboxMessage : TechnicalEntity, ITechnicalTable
{
    public string EventType { get; private set; } = string.Empty;
    public int MessageSchemaVersion { get; private set; } = 1;
    public string PayloadJson { get; private set; } = string.Empty;
    public string? IdempotencyKey { get; private set; }

    public OutboxStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Null while Processed or Failed — a terminal message is not scheduled for anything.</summary>
    public DateTimeOffset? AvailableAt { get; private set; }

    /// <summary>The current retry round. Starts at 1; incremented only by manual requeue
    /// (ADR-0012 §19.2). Never derived from attempt history at runtime.</summary>
    public int ExecutionGeneration { get; private set; } = 1;

    public Guid? ProcessingToken { get; private set; }
    public DateTimeOffset? ProcessingStartedAt { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public string? WorkerId { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }
    public DateTimeOffset? FailedAt { get; private set; }
    public string? FailureReason { get; private set; }
    public FailureDisposition? FailureDispositionValue { get; private set; }
    public DateTimeOffset? DismissedAt { get; private set; }
    public Guid? DismissedBy { get; private set; }
    public string? DismissalReason { get; private set; }

    /// <summary>Attempts consumed in the CURRENT generation. Incremented at claim; reset to 0
    /// on requeue (ADR-0012 §19).</summary>
    public int AttemptCount { get; private set; }

    public int MaxAttempts { get; private set; } = 5;
    public string? LastError { get; private set; }
    public DateTimeOffset? LastErrorAt { get; private set; }

    public Guid CorrelationId { get; private set; }
    public Guid? RequestId { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public string AggregateType { get; private set; } = string.Empty;
    public Guid? AggregateId { get; private set; }

    /// <summary>requeue_count is NOT stored — derived, so it can never drift from the generation
    /// it is supposed to describe (ADR-0012 §19.2 correction).</summary>
    public int RequeueCount => ExecutionGeneration - 1;

    private OutboxMessage() { }

    public static OutboxMessage Enqueue(
        string eventType,
        string payloadJson,
        string? idempotencyKey,
        Guid correlationId,
        Guid? requestId,
        Guid? actorUserId,
        string aggregateType,
        Guid? aggregateId,
        DateTimeOffset now,
        int maxAttempts = 5,
        int messageSchemaVersion = 1)
    {
        return new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventType = eventType,
            MessageSchemaVersion = messageSchemaVersion,
            PayloadJson = payloadJson,
            IdempotencyKey = idempotencyKey,
            Status = OutboxStatus.Pending,
            CreatedAt = now,
            AvailableAt = now,
            ExecutionGeneration = 1,
            AttemptCount = 0,
            MaxAttempts = maxAttempts,
            CorrelationId = correlationId,
            RequestId = requestId,
            ActorUserId = actorUserId,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
        };
    }

    /// <summary>True when the message could be claimed right now (ADR-0012 §14, §25.1) —
    /// the SAME predicate used by the claim query and by health-stall eligibility.</summary>
    public bool IsEligible(DateTimeOffset now) =>
        Status == OutboxStatus.Pending && AvailableAt is not null && AvailableAt <= now && AttemptCount < MaxAttempts;
}
