using Verce.SharedKernel;
using Verce.SharedKernel.Results;

namespace Verce.Modules.Pricing;

public static class BracketResolution
{
    public const string ConsistencySearch = "CONSISTENCY_SEARCH";
    public const string PriceContainment = "PRICE_CONTAINMENT";
    public const string VersionFlat = "VERSION_FLAT";
    public const string BoundaryPinned = "BOUNDARY_PINNED";
}

public sealed record CommercialPricingInput(decimal UnitTotalCost, decimal DesiredMargin, PriceRoundingPolicy RoundingPolicy,
    decimal MarginWarningDenominator, decimal? ManualPriceOverride, CommercialDiscountKind DiscountKind, decimal DiscountValue,
    decimal VersionCommissionPercent, decimal VersionFixedFee, decimal? VersionMinimumFee, decimal? VersionMaximumFee,
    IReadOnlyList<PriceBracket> Brackets);

public enum CommercialDiscountKind { None, Percent, Amount }

public sealed record CommercialPricingResult(decimal SuggestedPrice, decimal UnitPrice, decimal DiscountAmount, decimal NetUnitPrice,
    Guid? BracketId, string BracketResolution, decimal FeeBasisAmount, decimal CommissionPercent, decimal FixedFee,
    decimal CommissionAmount, string? FeeClampApplied, bool PriceOverridden, bool DiscountApplied,
    decimal ExpectedProfitAmount, decimal EffectiveMarginPercent);

/// <summary>ADR-0022's S8A pipeline. It deliberately wraps rather than mutates the S5 engine,
/// keeping pre-S8 callers stable while making bracket selection and final fee basis explicit.</summary>
public static class CommercialPricingEngine
{
    public static Result<CommercialPricingResult> Calculate(CommercialPricingInput input)
    {
        if (input.ManualPriceOverride is < 0) return Result.Failure<CommercialPricingResult>("QUOTE_ITEM_PRICE_OVERRIDE_INVALID");
        var brackets = input.Brackets.OrderBy(x => x.MinPrice).ThenBy(x => x.SortOrder).ToArray();
        for (var index = 1; index < brackets.Length; index++)
            if (brackets[index - 1].MaxPrice is { } previousMax && brackets[index].MinPrice < previousMax)
                return Result.Failure<CommercialPricingResult>("PRICE_BRACKET_OVERLAP");

        var suggested = ResolveSuggested(input, brackets);
        if (suggested.IsFailure) return Result.Failure<CommercialPricingResult>(suggested.ErrorCode!);
        var unitPrice = input.ManualPriceOverride ?? suggested.Value.Price;
        var discount = input.DiscountKind switch
        {
            CommercialDiscountKind.None => 0m,
            CommercialDiscountKind.Percent => Rounding.ToMoney(unitPrice * input.DiscountValue),
            CommercialDiscountKind.Amount => Rounding.ToMoney(input.DiscountValue),
            _ => throw new ArgumentOutOfRangeException(nameof(input.DiscountKind))
        };
        var net = Rounding.ToMoney(unitPrice - discount);
        if (net <= 0) return Result.Failure<CommercialPricingResult>("DISCOUNT_EXCEEDS_PRICE");
        var final = ResolveContaining(input, brackets, net);
        if (final is null) return Result.Failure<CommercialPricingResult>("PRICING_BRACKET_NOT_FOUND");
        var terms = final.Value;
        var commission = Rounding.ToMoney(net * terms.CommissionPercent);
        string? clamp = null;
        if (terms.MinimumFee is { } minimum && commission < minimum) { commission = minimum; clamp = "MIN"; }
        if (terms.MaximumFee is { } maximum && commission > maximum) { commission = maximum; clamp = "MAX"; }
        var profit = Rounding.ToMoney(net - commission - terms.FixedFee - input.UnitTotalCost);
        var margin = net > 0 ? Rounding.ToPercent(profit / net) : 0m;
        return Result.Success(new CommercialPricingResult(suggested.Value.Price, unitPrice, discount, net,
            terms.Id, brackets.Length == 0 ? BracketResolution.VersionFlat : BracketResolution.PriceContainment,
            net, terms.CommissionPercent, terms.FixedFee, commission, clamp, input.ManualPriceOverride is not null,
            discount != 0m, profit, margin));
    }

    private static Result<(decimal Price, string Resolution)> ResolveSuggested(CommercialPricingInput input, IReadOnlyList<PriceBracket> brackets)
    {
        if (brackets.Count == 0)
        {
            var flat = PricingEngine.Calculate(ToLegacyInput(input, input.VersionCommissionPercent, input.VersionFixedFee, input.VersionMinimumFee, input.VersionMaximumFee));
            return flat.IsSuccess ? Result.Success((flat.Value.SuggestedPrice, BracketResolution.VersionFlat)) : Result.Failure<(decimal, string)>(flat.ErrorCode!);
        }
        var candidates = new List<(decimal Price, PriceBracket Bracket)>();
        foreach (var bracket in brackets)
        {
            var candidate = PricingEngine.Calculate(ToLegacyInput(input, bracket.CommissionPercent, bracket.FixedFee, bracket.MinimumFee, bracket.MaximumFee));
            if (candidate.IsSuccess && bracket.Contains(candidate.Value.SuggestedPrice)) candidates.Add((candidate.Value.SuggestedPrice, bracket));
        }
        if (candidates.Count > 0) return Result.Success((candidates.OrderBy(x => x.Price).ThenBy(x => x.Bracket.MinPrice).First().Price, BracketResolution.ConsistencySearch));
        foreach (var bracket in brackets)
        {
            var fee = Rounding.ToMoney(bracket.MinPrice * bracket.CommissionPercent);
            if (bracket.MinimumFee is { } minimum && fee < minimum) fee = minimum;
            if (bracket.MaximumFee is { } maximum && fee > maximum) fee = maximum;
            var achieved = bracket.MinPrice == 0m ? -1m : Rounding.ToPercent((bracket.MinPrice - fee - bracket.FixedFee - input.UnitTotalCost) / bracket.MinPrice);
            if (achieved >= input.DesiredMargin) return Result.Success((bracket.MinPrice, BracketResolution.BoundaryPinned));
        }
        return Result.Failure<(decimal, string)>("PRICING_BRACKET_DISCONTINUITY");
    }

    private static (Guid? Id, decimal CommissionPercent, decimal FixedFee, decimal? MinimumFee, decimal? MaximumFee)? ResolveContaining(
        CommercialPricingInput input, IReadOnlyList<PriceBracket> brackets, decimal price)
    {
        if (brackets.Count == 0) return (null, input.VersionCommissionPercent, input.VersionFixedFee, input.VersionMinimumFee, input.VersionMaximumFee);
        var bracket = brackets.SingleOrDefault(x => x.Contains(price));
        return bracket is null ? null : (bracket.Id, bracket.CommissionPercent, bracket.FixedFee, bracket.MinimumFee, bracket.MaximumFee);
    }

    private static PricingCalculationInput ToLegacyInput(CommercialPricingInput input, decimal commission, decimal fixedFee, decimal? minimum, decimal? maximum) =>
        new(input.UnitTotalCost, commission, fixedFee, input.DesiredMargin, input.RoundingPolicy, input.MarginWarningDenominator, minimum, maximum);
}
