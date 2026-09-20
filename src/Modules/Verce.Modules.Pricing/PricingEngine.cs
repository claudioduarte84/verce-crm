using System.Text.Json.Serialization;
using Verce.SharedKernel;
using Verce.SharedKernel.Results;

namespace Verce.Modules.Pricing;

/// <summary>Setting <c>pricing.price_rounding_policy</c> (S2, already seeded). CR-07.4.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PriceRoundingPolicy>))]
public enum PriceRoundingPolicy { CENT, TEN_CENTS, WHOLE, NINETY_NINE, NONE }

[JsonConverter(typeof(JsonStringEnumConverter<PricingChannelKind>))]
public enum PricingChannelKind { DIRECT, MARKETPLACE }

/// <summary>Everything CR-07.1/CR-07.2/CR-07.6 need, fully resolved before the pure engine runs
/// — exactly the same "resolve first, calculate second" boundary S4's CostEngine established
/// (ADR-0018). <see cref="CommissionPercent"/> and <see cref="DesiredMargin"/> are ADR-0002
/// fractions (0.18 = 18%): Pricing does NOT use ADR-0018's Costing-only percentage-point
/// exception (mission §16 — never mix "18" and "0.18" implicitly).</summary>
public sealed record PricingCalculationInput(
    decimal UnitTotalCost,
    decimal CommissionPercent,
    decimal FixedFee,
    decimal DesiredMargin,
    PriceRoundingPolicy RoundingPolicy,
    decimal MarginWarningDenominator,
    decimal? MinimumFee,
    decimal? MaximumFee);

public sealed record PricingCalculationResult(
    decimal UnitTotalCost,
    decimal CommissionPercent,
    decimal FixedFee,
    decimal DesiredMargin,
    decimal Denominator,
    decimal RawPrice,
    decimal SuggestedPrice,
    decimal CommissionAmount,
    string? FeeClampApplied,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Pure, deterministic S5 pricing calculator (DOMAIN-MODEL §7, CALCULATION-RULES §7). One
/// formula for both Direct and Marketplace channels (ADR-0005 §2) — Direct sale is simply
/// CommissionPercent=0/FixedFee=0, never an `if (channel.Kind == DIRECT)` branch. No DbContext,
/// no HttpContext, no clock, no randomness — every input is resolved by the API composition
/// root (current Product cost, the applicable FeeRuleVersion, the Settings defaults) exactly
/// like S4's CostEngine boundary.
///
/// Implements CR-07.1 (validity guard + PRICING_EXTREME_MARGIN warning), CR-07.2 (price
/// formula), CR-07.4 (rounding policy) and CR-07.6 (min/max fee clamp). CR-07.5 (price brackets)
/// and CR-08.x (discount/line totals/effective margin) are Quote-scope (S6+) and out of S5.
/// </summary>
public static class PricingEngine
{
    public static Result<PricingCalculationResult> Calculate(PricingCalculationInput input)
    {
        if (input.UnitTotalCost < 0) return Result.Failure<PricingCalculationResult>("PRICING_COST_INVALID");
        if (input.FixedFee < 0) return Result.Failure<PricingCalculationResult>("PRICING_FIXED_FEE_INVALID");
        if (input.CommissionPercent < 0 || input.CommissionPercent >= 1) return Result.Failure<PricingCalculationResult>("PRICING_INVALID_COMMISSION");
        if (input.DesiredMargin < 0 || input.DesiredMargin >= 1) return Result.Failure<PricingCalculationResult>("PRICING_INVALID_MARGIN");
        if (!Enum.IsDefined(input.RoundingPolicy)) return Result.Failure<PricingCalculationResult>("PRICING_ROUNDING_POLICY_INVALID");
        if (input.MinimumFee is < 0) return Result.Failure<PricingCalculationResult>("FEE_RULE_VERSION_MINIMUM_FEE_INVALID");
        if (input.MaximumFee is < 0) return Result.Failure<PricingCalculationResult>("FEE_RULE_VERSION_MAXIMUM_FEE_INVALID");

        // CR-07.1: computed and guarded BEFORE any division — the engine never returns a number
        // derived from a non-positive denominator.
        var denominator = 1m - (input.CommissionPercent + input.DesiredMargin);
        if (denominator <= 0) return Result.Failure<PricingCalculationResult>("PRICING_INVALID_DENOMINATOR");

        var warnings = new List<string>();
        if (denominator < input.MarginWarningDenominator) warnings.Add("PRICING_EXTREME_MARGIN");

        // CR-07.2: fixedFeePerUnit = FixedFee under FixedFeeApplication.PER_UNIT (the only S5
        // scope — see FeeRule.cs remarks). rawPrice is kept at full decimal precision; the
        // rounding POLICY is what decides the final presented price (CR-07.4), not a fixed
        // round-then-policy pipeline — this is resolved against CR-07.4's own worked table
        // (NONE truncates the raw price, not an already round2'd value), which is the more
        // concrete of the two sources and is what this implementation matches exactly.
        var rawPrice = (input.UnitTotalCost + input.FixedFee) / denominator;
        var suggestedPrice = ApplyRoundingPolicy(rawPrice, input.RoundingPolicy);

        // CR-07.6: commission is computed on the PRESENTED price, then clamped.
        var commissionAmount = Rounding.ToMoney(suggestedPrice * input.CommissionPercent);
        string? feeClampApplied = null;
        if (input.MinimumFee is { } minimumFee && commissionAmount < minimumFee) { commissionAmount = minimumFee; feeClampApplied = "MIN"; }
        if (input.MaximumFee is { } maximumFee && commissionAmount > maximumFee) { commissionAmount = maximumFee; feeClampApplied = "MAX"; }

        return Result.Success(new PricingCalculationResult(
            Rounding.ToInternal(input.UnitTotalCost), input.CommissionPercent, Rounding.ToMoney(input.FixedFee), input.DesiredMargin,
            Rounding.ToPercent(denominator), Rounding.ToInternal(rawPrice), suggestedPrice, commissionAmount, feeClampApplied, warnings));
    }

    /// <summary>CR-07.4. All policies except NONE and CENT round UP, never down — rounding a
    /// price down silently erodes the margin the operator asked for.</summary>
    private static decimal ApplyRoundingPolicy(decimal rawPrice, PriceRoundingPolicy policy) => policy switch
    {
        PriceRoundingPolicy.CENT => Rounding.ToMoney(rawPrice),
        PriceRoundingPolicy.TEN_CENTS => Math.Ceiling(rawPrice * 10m) / 10m,
        PriceRoundingPolicy.WHOLE => Math.Ceiling(rawPrice),
        PriceRoundingPolicy.NINETY_NINE => ApplyNinetyNine(rawPrice),
        PriceRoundingPolicy.NONE => Math.Truncate(rawPrice * 100m) / 100m,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "PRICING_ROUNDING_POLICY_INVALID"),
    };

    /// <summary>CR-07.4 (Terra B-01 correction): the smallest commercial price of the form N.99
    /// that is greater than or equal to <paramref name="rawPrice"/> — a true ceiling, never a
    /// reduction. <c>Math.Floor(rawPrice) + 0.99m</c> alone is NOT sufficient: for a fractional
    /// part above .99 (e.g. 40.995, whose floor+0.99 is 40.99 — BELOW the raw price) the naive
    /// formula rounds down. The fix is deterministic decimal-only: compute the same floor+0.99
    /// candidate, and if it still falls short of the raw price, it must belong to the NEXT
    /// integer's .99 instead.</summary>
    private static decimal ApplyNinetyNine(decimal rawPrice)
    {
        var candidate = Math.Floor(rawPrice) + 0.99m;
        return candidate < rawPrice ? candidate + 1m : candidate;
    }
}
