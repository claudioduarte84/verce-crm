using Verce.SharedKernel;

namespace Verce.Modules.Quoting;

/// <summary>One line's allocation input: its line number (the tie-break key, CR-07.7) and its
/// estimated-cost basis (<c>round6(unitTotalCost * quantity)</c>, computed by the caller — this
/// calculator is pure and never resolves cost itself).</summary>
public sealed record AllocationLineInput(int LineNumber, decimal Basis);

/// <summary>This line's allocated share of the order fee. <c>Σ AllocatedAmount == orderFee</c>
/// exactly, always (CR-07.7a).</summary>
public sealed record AllocationLineResult(int LineNumber, decimal AllocatedAmount);

/// <summary>
/// Pure, deterministic H-004-remainder allocator (CR-07.7, ADR-0020 Part C). Partitions one
/// <c>PER_ORDER</c> fixed fee across the lines of its fee group in proportion to line estimated
/// cost, using floor + largest-remainder so the parts always sum to exactly the whole — no
/// DbContext, no clock, no randomness, matching the same "resolve first, calculate second"
/// boundary as <see cref="PricingEngine"/>/S4's CostEngine (this class deliberately has none of
/// those dependencies; the composition root resolves every input).
/// </summary>
public static class PerOrderFeeAllocator
{
    /// <summary>Allocates <paramref name="orderFee"/> across <paramref name="lines"/>. Recomputes
    /// the WHOLE group from scratch every time (ADR-0020 §A.7/§C.6) — never called for less than
    /// the complete, current fee group, and never patched incrementally.</summary>
    public static IReadOnlyList<AllocationLineResult> Allocate(decimal orderFee, IReadOnlyList<AllocationLineInput> lines)
    {
        if (orderFee < 0) throw new ArgumentException("PER_ORDER_ALLOCATION_NEGATIVE_FEE");
        // B-03: a fractional-cent fee (e.g. 1.005) cannot be partitioned exactly — the residual
        // step below assumes `(orderFee - flooredSum) * 100` is an exact integer, which is false
        // for anything finer than whole cents. Reject defensively here too: this calculator must
        // never trust that every caller resolved its fee through Pricing's own HTTP validation.
        if (!Rounding.HasMoneyPrecision(orderFee)) throw new ArgumentException("FIXED_FEE_PRECISION_INVALID");
        if (lines.Count == 0) return [];

        // CR-07.7 degenerate input: orderFee = 0 -> every alloc_i = 0, no partitioning needed.
        if (orderFee == 0m) return lines.Select(l => new AllocationLineResult(l.LineNumber, 0m)).ToArray();

        var totalBasis = lines.Sum(l => l.Basis);

        // CR-07.7 degenerate input: totalBasis = 0 (every line zero-cost) -> equal-per-line
        // fallback, using the SAME algorithm with basis_i = 1 for every line. No division by
        // zero, no special-cased second code path.
        var (basisOf, effectiveTotalBasis) = totalBasis == 0m
            ? (lines.ToDictionary(l => l.LineNumber, _ => 1m), (decimal)lines.Count)
            : (lines.ToDictionary(l => l.LineNumber, l => l.Basis), totalBasis);

        // Step 1: exact share at full decimal precision, then floor to 2 decimals (truncate
        // toward zero — every basis/fee here is non-negative, so floor == truncate).
        var exactShares = lines.ToDictionary(l => l.LineNumber, l => orderFee * basisOf[l.LineNumber] / effectiveTotalBasis);
        var floored = exactShares.ToDictionary(kv => kv.Key, kv => FloorToCents(kv.Value));

        // Step 2: the residual is an exact integer number of cents, GIVEN the whole-cent
        // precision guard above — decimal arithmetic never loses precision here, so this is not
        // a fuzzy epsilon comparison; Math.Round is defense in depth only (never masks a
        // fractional-cent fee, which is already rejected before this line is ever reached).
        var flooredSum = floored.Values.Sum();
        var residualCents = (int)Math.Round((orderFee - flooredSum) * 100m, 0, MidpointRounding.AwayFromZero);

        // Step 3-4: rank by remainder DESCENDING, tie-break by line_number ASCENDING — never
        // database row order, never the operator-editable sort_order (CR-07.7).
        var ranking = lines
            .Select(l => l.LineNumber)
            .OrderByDescending(lineNumber => exactShares[lineNumber] - floored[lineNumber])
            .ThenBy(lineNumber => lineNumber)
            .ToArray();

        // Step 5: add R$0.01 to each of the first `residualCents` lines in that ranking.
        var allocations = new Dictionary<int, decimal>(floored);
        for (var i = 0; i < residualCents; i++)
        {
            allocations[ranking[i]] += 0.01m;
        }

        return lines.Select(l => new AllocationLineResult(l.LineNumber, allocations[l.LineNumber])).ToArray();
    }

    private static decimal FloorToCents(decimal value) => Math.Floor(value * 100m) / 100m;
}
