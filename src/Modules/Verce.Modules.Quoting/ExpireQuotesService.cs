using Microsoft.EntityFrameworkCore;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Time;

namespace Verce.Modules.Quoting;

/// <summary>
/// STATE-MACHINES §1.6: the eligibility query (current, non-superseded, non-terminal, past
/// <c>ValidUntil</c> in the organization timezone) is read-only and BOUNDED (H-02); each
/// expiration is its OWN <see cref="IUnitOfWork"/> command — one aggregate mutated per
/// transaction (CLAUDE.md §6), and one quote's failure can never block another's: a
/// <see cref="DbUpdateConcurrencyException"/> on one quote (e.g. it was approved/revised
/// concurrently, between the eligibility read and its own transaction) is caught and isolated to
/// THAT quote only — it never aborts the rest of the sweep. <see cref="Quote.ExpireCurrentRevision"/>
/// is itself idempotent, so a quote that raced away WITHOUT a version conflict (already expired by
/// a concurrent run) is silently skipped — "re-running it changes nothing" — and is correctly
/// excluded from the returned count, which reflects only revisions this call ACTUALLY transitioned
/// to EXPIRED, never every row merely attempted.
/// </summary>
public sealed class ExpireQuotesService
{
    /// <summary>H-02: caps the eligibility query so one sweep never scans/loads an unbounded
    /// number of rows — a large backlog is drained over several scheduled runs instead.</summary>
    public const int DefaultBatchSize = 200;

    /// <summary>F-04: a finite technical ceiling on <see cref="ExpireEligibleAsync"/>'s
    /// <c>batchSize</c> — an operational safety cap (this method's per-quote work is a full
    /// per-Quote Unit of Work, not a cheap row scan), never a business limit. Any positive int up
    /// to and including <see cref="int.MaxValue"/> was previously accepted, defeating H-02's own
    /// bounded-query invariant; this is the deterministic ceiling both the Quartz-bound
    /// <see cref="ExpireQuotesJobOptions.BatchSize"/> (validated at startup) and this method's own
    /// defensive check enforce.</summary>
    public const int MaxBatchSize = 1000;

    private readonly VerceDbContext _readContext;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public ExpireQuotesService(VerceDbContext readContext, IUnitOfWork unitOfWork, IClock clock)
    {
        _readContext = readContext;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<int> ExpireEligibleAsync(CancellationToken cancellationToken, int batchSize = DefaultBatchSize)
    {
        // F-04: defensive even when configuration validation already ran — a direct internal
        // invocation (e.g. from a test, or a future caller) must never be able to reach
        // Take(int.MaxValue) merely by skipping the composition-root's own startup check.
        if (batchSize < 1 || batchSize > MaxBatchSize) throw new ArgumentException("EXPIRE_QUOTES_BATCH_SIZE_INVALID");
        var organizationToday = _clock.OrganizationToday();

        // Deterministic ordering (oldest deadline first, then QuoteId as a tiebreaker) so
        // successive batches make forward progress across a backlog larger than one batch.
        var eligibleQuoteIds = await _readContext.Set<QuoteRevision>().AsNoTracking()
            .Where(r => r.SupersededByRevisionId == null
                && (r.Status == QuoteRevisionStatus.GENERATED || r.Status == QuoteRevisionStatus.SENT || r.Status == QuoteRevisionStatus.NEGOTIATING)
                && r.ValidUntil < organizationToday)
            .OrderBy(r => r.ValidUntil).ThenBy(r => r.QuoteId)
            .Select(r => r.QuoteId)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var expiredCount = 0;
        foreach (var quoteId in eligibleQuoteIds)
        {
            try
            {
                var actuallyExpired = await _unitOfWork.ExecuteAsync(async (db, ct) =>
                {
                    var quote = await db.Set<Quote>().Include(x => x.Revisions).ThenInclude(x => x.History)
                        .SingleOrDefaultAsync(x => x.Id == quoteId, ct);
                    if (quote is null) return false;
                    var statusBefore = quote.CurrentRevision.Status;
                    quote.ExpireCurrentRevision(Guid.CreateVersion7(), _clock.UtcNow, _clock.OrganizationToday());
                    return statusBefore != QuoteRevisionStatus.EXPIRED && quote.CurrentRevision.Status == QuoteRevisionStatus.EXPIRED;
                }, cancellationToken);
                if (actuallyExpired) expiredCount++;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Isolated to this one quote — a concurrent write already moved it on (approved,
                // revised, canceled). It is no longer this sweep's concern; a later run will
                // re-evaluate it if it is still eligible.
            }
        }

        return expiredCount;
    }
}
