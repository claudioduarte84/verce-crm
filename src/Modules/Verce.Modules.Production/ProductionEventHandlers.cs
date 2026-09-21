using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verce.Modules.Quoting.Contracts;
using Verce.Platform.Audit;
using Verce.Platform.Numbering;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Time;

namespace Verce.Modules.Production;

/// <summary>
/// The production-conversion contract (ADR-0020 §A.6, §A.8). Creates the <c>ProductionOrder</c>
/// idempotently, in the SAME transaction as approval — if this throws, the whole
/// <c>QuoteApproved</c> command (including the revision's APPROVED transition) rolls back
/// (ADR-0012 §2). Applies the full ADR-0020 §A.2 matrix for a previous QUEUED order of the same
/// quote.
///
/// F-02 correction: ownership for a given <c>QuoteRevisionId</c> is decided BEFORE any number is
/// allocated. A transaction-scoped PostgreSQL advisory lock
/// (<c>pg_advisory_xact_lock(hashtextextended(quoteRevisionId::text, 0))</c>) serializes every
/// concurrent/replay caller for the SAME revision — the lock key is a deterministic 64-bit hash
/// of the revision id (collision odds are the same as any 64-bit hash; different revisions almost
/// never collide and, even if they did, would only serialize two unrelated approvals against each
/// other rather than corrupt anything), held only for this transaction and released automatically
/// at COMMIT/ROLLBACK — never a process-global or distributed lock. Once serialized, the handler
/// checks for an existing row FIRST; only the genuine creator ever allocates a
/// <see cref="SequentialNumberAllocator"/> number, so a replay or a loser never advances the daily
/// sequence and leaves no numbering gap.
///
/// B-04 correction (kept as defense in depth): creation is still a real PostgreSQL
/// <c>INSERT ... ON CONFLICT (quote_revision_id) DO NOTHING RETURNING id</c> — never a
/// check-then-insert. Under the normal, lock-disciplined path this can never actually lose (every
/// caller for this revision is already serialized above it), but it remains the database-level
/// backstop CLAUDE.md rule 19 requires: it can NEVER raise a unique-violation even if some future
/// caller reached this code outside the lock discipline. The row is re-selected afterwards either
/// way so it is normally EF-tracked for the rest of this handler (and any later same-UoW
/// mutation, e.g. cancelling a previous QUEUED order) — domain state and EF state always agree.
/// </summary>
public sealed class CreateProductionOrderOnQuoteApproved : IDomainEventHandler<QuoteApprovedEvent>
{
    private readonly AmbientOperationContext _ambientContext;

    public CreateProductionOrderOnQuoteApproved(AmbientOperationContext ambientContext)
    {
        _ambientContext = ambientContext;
    }

    public async Task HandleAsync(QuoteApprovedEvent domainEvent, VerceDbContext context, CancellationToken cancellationToken)
    {
        // F-02: serialize every caller for this EXACT revision before touching existence,
        // numbering or insertion. Transaction-scoped — auto-released at COMMIT/ROLLBACK, never
        // held past this Unit of Work, never a process-global or distributed primitive.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({domainEvent.QuoteRevisionId.ToString()}::text, 0));",
            cancellationToken);

        var existing = await context.Set<ProductionOrder>()
            .SingleOrDefaultAsync(o => o.QuoteRevisionId == domainEvent.QuoteRevisionId, cancellationToken);

        ProductionOrder order;
        if (existing is not null)
        {
            // Replay/no-op: another transaction already created this revision's order (possibly
            // one that has since progressed past QUEUED) — NOT this handler's concern to
            // recreate, renumber or re-audit. No number is allocated on this path.
            order = existing;
        }
        else
        {
            var organizationDate = OrganizationTimeZone.ToOrganizationDate(domainEvent.ApprovedAt);
            var sequence = await SequentialNumberAllocator.AllocateAsync(
                context, SequentialNumberAllocator.ProductionOrderSeries, organizationDate, cancellationToken);
            var candidateId = Guid.CreateVersion7();
            var orderNumber = $"{organizationDate:yyMMdd}-{sequence}";

            // Atomic get-or-create: this statement can NEVER raise a unique-violation — a losing
            // concurrent transaction simply gets zero rows back from RETURNING, never a Postgres
            // error. Under the advisory-lock discipline above, this branch is only ever reached
            // by the single serialized winner, so it always inserts; the ON CONFLICT remains as
            // the documented database-level backstop (§14/§15 of the mission that introduced it).
            var insertedIds = await context.Database.SqlQueryRaw<Guid>(
                """
                INSERT INTO production.production_order
                    (id, order_number, number_date, number_sequence, quote_id, quote_revision_id, status, has_pending_revision, created_at, version)
                VALUES
                    ({0}, {1}, {2}, {3}, {4}, {5}, 'QUEUED', false, now(), 1)
                ON CONFLICT (quote_revision_id) DO NOTHING
                RETURNING id
                """,
                candidateId, orderNumber, organizationDate, sequence, domainEvent.QuoteId, domainEvent.QuoteRevisionId)
                .ToListAsync(cancellationToken);

            if (insertedIds.Count > 0)
            {
                order = await context.Set<ProductionOrder>().SingleAsync(o => o.Id == insertedIds[0], cancellationToken);

                // The raw INSERT bypasses AuditSaveChangesInterceptor (it never sees an EF
                // "Added" entry for this row) — CLAUDE.md rule 38 still requires the row to be
                // audited in THIS business transaction, so it is added here explicitly, mirroring
                // the interceptor's own EntityState.Added shape as closely as a hand-built entry
                // can (N-02): "ADDED" (not a SQL verb — matches entry.State.ToString() exactly),
                // the SAME ambient actor/correlation fields the interceptor itself reads, and a
                // newValues-only JSON snapshot of every persisted column (oldValues stays null,
                // exactly like a genuine Added entry — there is no prior state to diff against).
                // Picked up by the next wave's ordinary SaveChanges — this handler never calls
                // SaveChanges itself (ADR-0012 §9: ordering stays COLLECT -> SAVE -> DISPATCH).
                var newValues = context.Entry(order).Properties.ToDictionary(p => p.Metadata.Name, p => p.CurrentValue);
                context.Add(new AuditLogEntry(
                    occurredAt: domainEvent.OccurredAtUtc,
                    operationStartedAt: _ambientContext.OperationStartedAtUtc,
                    userId: _ambientContext.ActorUserId,
                    userDisplayName: _ambientContext.ActorDisplayName,
                    entitySchema: "production",
                    entityTable: "production_order",
                    entityId: order.Id,
                    operation: nameof(EntityState.Added).ToUpperInvariant(),
                    changedColumns: null,
                    oldValuesJson: null,
                    newValuesJson: JsonSerializer.Serialize(newValues),
                    correlationId: _ambientContext.CorrelationId,
                    requestId: _ambientContext.RequestId,
                    waveIndex: _ambientContext.WaveIndex,
                    source: _ambientContext.Source));
            }
            else
            {
                // Rare on-conflict loser reached outside the lock discipline (defense in depth
                // only — see §15 of the mission that required this): resolve the existing row,
                // never fabricate a second "Created" audit for it.
                order = await context.Set<ProductionOrder>().SingleAsync(o => o.QuoteRevisionId == domainEvent.QuoteRevisionId, cancellationToken);
            }
        }

        var previousOrders = await context.Set<ProductionOrder>()
            .Where(o => o.QuoteId == domainEvent.QuoteId && o.QuoteRevisionId != domainEvent.QuoteRevisionId)
            .ToListAsync(cancellationToken);

        foreach (var previous in previousOrders)
        {
            switch (previous.Status)
            {
                case ProductionOrderStatus.QUEUED:
                    // ADR-0020 §A.2: cancel the superseded QUEUED order and link the
                    // replacement — idempotent: a replay/retry that reaches this a second time
                    // finds `previous` already CANCELED (matched by the `default` branch below
                    // on its next pass through a fresh query), never re-cancelling it or
                    // re-pointing SupersededByOrderId at a different order.
                    previous.CancelAsSupersededByRevision(order.Id);
                    break;
                case ProductionOrderStatus.IN_PRODUCTION or ProductionOrderStatus.READY or ProductionOrderStatus.SHIPPED:
                    // H-05: the composition root's pre-check (STATE-MACHINES §1.5) is only an
                    // optimization — THIS is the authoritative, in-transaction guard. A race
                    // that reaches here must still surface as the SAME stable, documented
                    // contract the pre-check itself returns, never a generic 500.
                    throw new ArgumentException("PRODUCTION_ORDER_IN_PROGRESS");
                default:
                    // DELIVERED / CANCELED: terminal, left untouched (ADR-0020 §A.2 rule 2).
                    break;
            }
        }
    }
}

/// <summary>ADR-0020 §A.6/§A.7: a newer revision was constructed over a non-terminal order.
/// Flags <c>HasPendingRevision</c> — advisory only, never a guard (§A.4).</summary>
public sealed class MaintainHasPendingRevisionOnQuoteRevised : IDomainEventHandler<QuoteRevisedEvent>
{
    public async Task HandleAsync(QuoteRevisedEvent domainEvent, VerceDbContext context, CancellationToken cancellationToken)
    {
        var order = await context.Set<ProductionOrder>()
            .FirstOrDefaultAsync(o => o.QuoteRevisionId == domainEvent.PreviousRevisionId, cancellationToken);
        if (order is null || !order.IsNonTerminal) return;
        order.SetHasPendingRevision(true);
    }
}

/// <summary>ADR-0020 §A.6: the pending revision was canceled without being approved — clears the
/// flag on this quote's order, which simply returns to the normal queue with NO state
/// transition (it was never itself canceled; STATE-MACHINES §3 Case A step 4). Because a quote
/// has exactly one current revision at a time, resolving it (this event) always means the order
/// that was flagged for THIS quote has nothing left pending.</summary>
public sealed class ClearHasPendingRevisionOnQuoteCanceled : IDomainEventHandler<QuoteCanceledEvent>
{
    public async Task HandleAsync(QuoteCanceledEvent domainEvent, VerceDbContext context, CancellationToken cancellationToken)
    {
        var order = await context.Set<ProductionOrder>()
            .FirstOrDefaultAsync(o => o.QuoteId == domainEvent.QuoteId && o.HasPendingRevision, cancellationToken);
        order?.SetHasPendingRevision(false);
    }
}

/// <summary>Identical effect to <see cref="ClearHasPendingRevisionOnQuoteCanceled"/>, for the
/// job-driven expiration path (STATE-MACHINES §1.6).</summary>
public sealed class ClearHasPendingRevisionOnQuoteExpired : IDomainEventHandler<QuoteExpiredEvent>
{
    public async Task HandleAsync(QuoteExpiredEvent domainEvent, VerceDbContext context, CancellationToken cancellationToken)
    {
        var order = await context.Set<ProductionOrder>()
            .FirstOrDefaultAsync(o => o.QuoteId == domainEvent.QuoteId && o.HasPendingRevision, cancellationToken);
        order?.SetHasPendingRevision(false);
    }
}
