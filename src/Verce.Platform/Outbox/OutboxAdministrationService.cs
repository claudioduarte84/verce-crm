using Microsoft.EntityFrameworkCore;
using Verce.Platform.Audit;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Time;

namespace Verce.Platform.Outbox;

public enum OutboxRequeueOutcome
{
    Requeued,
    NotEligible,
}

/// <summary>
/// H-OUTBOX-002 / ADR-0012 §21: the ONLY supported entry point for a manual requeue from outside
/// Verce.Platform. <see cref="OutboxProcessor.RequeueAsync"/> is the fenced SQL primitive this
/// wraps, but that primitive has no notion of "who" or "why" — an actor and a non-blank reason
/// are mandatory here, and the mutation plus its audit row are written in ONE transaction
/// (CLAUDE.md §38: "if audit fails, the command fails"). Authorization (Owner-only) is enforced
/// by the caller's HTTP authorization policy, not by this service — this service trusts the
/// actor id it is given.
/// </summary>
public sealed class OutboxAdministrationService
{
    private readonly VerceDbContext _context;
    private readonly IClock _clock;

    public OutboxAdministrationService(VerceDbContext context, IClock clock)
    {
        _context = context;
        _clock = clock;
    }

    public async Task<OutboxRequeueOutcome> RequeueAsync(
        Guid messageId, Guid actorUserId, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A non-blank reason is required to requeue an outbox message.", nameof(reason));

        var now = _clock.UtcNow;
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        // The CTE locks the row (FOR UPDATE) and captures its PRE-update disposition in the same
        // statement the UPDATE runs in, so there is no separate read-then-write gap for a
        // concurrent purge/requeue to land in between (the same discipline H-RETENTION-001 fixed
        // on the retention side). Eligible from EITHER disposition (ADR-0012 §21): ACTIVE is the
        // normal case, DISMISSED is a deliberate revival — the audit operation name below is what
        // makes a revival visible as distinct from an ordinary requeue.
        var priorDisposition = await _context.Database.SqlQueryRaw<string>(
            """
            WITH prior AS (
                SELECT id, failure_disposition AS prior_disposition
                FROM platform.outbox_message
                WHERE id = {0} AND status = 'Failed'
                FOR UPDATE
            )
            UPDATE platform.outbox_message m
            SET status = 'Pending',
                execution_generation = m.execution_generation + 1,
                attempt_count = 0,
                failure_disposition = NULL,
                failed_at = NULL,
                failure_reason = NULL,
                available_at = {1},
                processing_token = NULL,
                lease_until = NULL
            FROM prior
            WHERE m.id = prior.id
            RETURNING prior.prior_disposition;
            """, messageId, now).ToListAsync(cancellationToken);

        if (priorDisposition.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OutboxRequeueOutcome.NotEligible;
        }

        // AuditLogEntry.Operation is capped at 16 chars (VerceDbContext) — both names fit and
        // share the OUTBOX_ prefix so the two are visually grouped in the audit trail.
        var operation = priorDisposition[0] == nameof(FailureDisposition.Dismissed)
            ? "OUTBOX_REVIVAL"
            : "OUTBOX_REQUEUE";

        _context.AuditLog.Add(new AuditLogEntry(
            occurredAt: now, operationStartedAt: now, userId: actorUserId, userDisplayName: null,
            entitySchema: "platform", entityTable: "outbox_message", entityId: messageId,
            operation: operation, changedColumns: null, oldValuesJson: null,
            newValuesJson: System.Text.Json.JsonSerializer.Serialize(new { reason }),
            correlationId: Guid.CreateVersion7(), requestId: null, waveIndex: 1, source: AuditSource.Api));
        await _context.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return OutboxRequeueOutcome.Requeued;
    }
}
