namespace Verce.SharedKernel;

/// <summary>
/// The single deterministic allocation authority for an order-level fixed fee. It partitions
/// whole cents by largest remainder, with the supplied line key as a stable tie-breaker.
/// </summary>
public sealed record OrderFeeAllocationInput(int LineNumber, decimal Basis);
public sealed record OrderFeeAllocationResult(int LineNumber, decimal AllocatedAmount);

public static class OrderFixedFeeAllocator
{
    public static IReadOnlyList<OrderFeeAllocationResult> Allocate(decimal orderFee, IReadOnlyList<OrderFeeAllocationInput> lines)
    {
        if (orderFee < 0) throw new ArgumentException("PER_ORDER_ALLOCATION_NEGATIVE_FEE");
        if (!Rounding.HasMoneyPrecision(orderFee)) throw new ArgumentException("FIXED_FEE_PRECISION_INVALID");
        if (lines.Count == 0) return [];
        if (orderFee == 0m) return lines.Select(x => new OrderFeeAllocationResult(x.LineNumber, 0m)).ToArray();

        var totalBasis = lines.Sum(x => x.Basis);
        var bases = totalBasis == 0m
            ? lines.ToDictionary(x => x.LineNumber, _ => 1m)
            : lines.ToDictionary(x => x.LineNumber, x => x.Basis);
        var denominator = totalBasis == 0m ? (decimal)lines.Count : totalBasis;
        var exact = lines.ToDictionary(x => x.LineNumber, x => orderFee * bases[x.LineNumber] / denominator);
        var floor = exact.ToDictionary(x => x.Key, x => Math.Floor(x.Value * 100m) / 100m);
        var remaining = (int)Math.Round((orderFee - floor.Values.Sum()) * 100m, 0, MidpointRounding.AwayFromZero);
        var ranked = lines.Select(x => x.LineNumber)
            .OrderByDescending(x => exact[x] - floor[x]).ThenBy(x => x).ToArray();
        for (var i = 0; i < remaining; i++) floor[ranked[i]] += .01m;
        return lines.Select(x => new OrderFeeAllocationResult(x.LineNumber, floor[x.LineNumber])).ToArray();
    }
}
