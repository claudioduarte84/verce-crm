using Verce.SharedKernel.Domain;

namespace Verce.Platform.Outbox;

public enum OutboxAttemptOutcome
{
    Succeeded,
    RetryableFailure,
    NonRetryableFailure,
    LeaseExpired,
}

/// <summary>
/// Append-only attempt history (ADR-0012 §19.3). Historical identity is
/// (OutboxMessageId, ExecutionGeneration, AttemptNumber) — NOT AttemptNumber alone, which is
/// what caused the B-RG3-001 collision: attempt_count resets to 0 on requeue, so without the
/// generation column, the first claim after a requeue would insert attempt_number = 1 again and
/// collide with the previous round's history.
/// </summary>
public sealed class OutboxMessageAttempt : TechnicalEntity, ITechnicalTable
{
    public Guid OutboxMessageId { get; private set; }
    public int ExecutionGeneration { get; private set; }
    public int AttemptNumber { get; private set; }
    public Guid ProcessingToken { get; private set; }
    public string? WorkerId { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public OutboxAttemptOutcome? Outcome { get; private set; }
    public string? ErrorType { get; private set; }
    public string? ErrorMessage { get; private set; }

    private OutboxMessageAttempt() { }

    public static OutboxMessageAttempt StartedNow(
        Guid outboxMessageId, int executionGeneration, int attemptNumber,
        Guid processingToken, string? workerId, DateTimeOffset startedAt)
    {
        return new OutboxMessageAttempt
        {
            Id = Guid.CreateVersion7(),
            OutboxMessageId = outboxMessageId,
            ExecutionGeneration = executionGeneration,
            AttemptNumber = attemptNumber,
            ProcessingToken = processingToken,
            WorkerId = workerId,
            StartedAt = startedAt,
        };
    }

    /// <summary>Finalizes this attempt row. Called by the same fenced transition that updates
    /// the parent OutboxMessage, so both always agree.</summary>
    public void Finish(DateTimeOffset finishedAt, OutboxAttemptOutcome outcome, string? errorType, string? errorMessage)
    {
        FinishedAt = finishedAt;
        Outcome = outcome;
        ErrorType = errorType;
        ErrorMessage = errorMessage;
    }
}
