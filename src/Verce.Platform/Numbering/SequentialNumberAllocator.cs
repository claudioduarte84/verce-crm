using Microsoft.EntityFrameworkCore;
using Verce.Platform.Persistence;

namespace Verce.Platform.Numbering;

/// <summary>
/// ADR-0004 §1: a shared, per-business-date atomic sequence, allocated by one
/// <c>INSERT ... ON CONFLICT DO UPDATE ... RETURNING</c> statement executed INSIDE the caller's
/// own transaction, so a rolled-back command also rolls back its number allocation (no gaps).
/// The row-level lock Postgres takes on the conflicting row serializes same-day allocation
/// without a separate <c>SELECT ... FOR UPDATE</c>.
///
/// Lives in <c>Verce.Platform</c> — not in the Quoting module — specifically so both Quoting
/// (<c>series = "QUOTE"</c>) and Production (<c>series = "PRODUCTION_ORDER"</c>) can allocate
/// from the SAME counter table without either module referencing the other's assembly
/// (CLAUDE.md rule 11); ADR-0004 itself specifies "production orders reuse the same table... so
/// the mechanism is built and tested once." The table
/// (<c>quoting.quote_number_counter</c>) is deliberately NOT modeled as an EF entity — mirrors
/// <c>fee_rule_version_no_overlap</c>'s EXCLUDE constraint in Pricing — so
/// <c>has-pending-model-changes</c> never flags it as drift; it exists only via the migration
/// and this raw-SQL allocator.
/// </summary>
public static class SequentialNumberAllocator
{
    public const string QuoteSeries = "QUOTE";
    public const string ProductionOrderSeries = "PRODUCTION_ORDER";

    public static async Task<int> AllocateAsync(VerceDbContext db, string series, DateOnly organizationDate, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQueryRaw<int>(
            """
            INSERT INTO quoting.quote_number_counter (series, counter_date, last_sequence)
            VALUES ({0}, {1}, 1)
            ON CONFLICT (series, counter_date)
            DO UPDATE SET last_sequence = quoting.quote_number_counter.last_sequence + 1
            RETURNING last_sequence
            """, series, organizationDate).ToListAsync(cancellationToken);
        return rows[0];
    }
}
