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
        return OrderFixedFeeAllocator.Allocate(orderFee,
                lines.Select(x => new OrderFeeAllocationInput(x.LineNumber, x.Basis)).ToArray())
            .Select(x => new AllocationLineResult(x.LineNumber, x.AllocatedAmount)).ToArray();
    }
}
