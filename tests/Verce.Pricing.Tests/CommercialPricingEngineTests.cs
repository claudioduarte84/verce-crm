using FluentAssertions;
using Verce.Modules.Pricing;

namespace Verce.Pricing.Tests;

public class CommercialPricingEngineTests
{
    private static IReadOnlyList<PriceBracket> Brackets()
    {
        var rule = new FeeRule(Guid.NewGuid(), "Marketplace");
        var version = rule.AddVersion(new DateOnly(2026, 1, 1), null, 0.10m, 0m, FixedFeeApplication.PerUnit, null, null, null,
            priceBrackets:
            [
                new PriceBracketInput(0m, 80m, 0.10m, 0m, null, null, 1),
                new PriceBracketInput(80m, null, 0.20m, 1m, null, null, 2),
            ]);
        return version.Brackets;
    }

    [Fact]
    public void H005_manual_override_and_discount_re_resolve_fee_on_final_net_price()
    {
        var brackets = Brackets();
        var result = CommercialPricingEngine.Calculate(new CommercialPricingInput(
            UnitTotalCost: 50m, DesiredMargin: 0.20m, RoundingPolicy: PriceRoundingPolicy.NONE, MarginWarningDenominator: 0.10m,
            ManualPriceOverride: 120m, DiscountKind: CommercialDiscountKind.Amount, DiscountValue: 30m,
            VersionCommissionPercent: 0.10m, VersionFixedFee: 0m, VersionMinimumFee: null, VersionMaximumFee: null, Brackets: brackets));

        result.IsSuccess.Should().BeTrue();
        result.Value.SuggestedPrice.Should().BeLessThan(80m, "the suggested price must be resolved in bracket A");
        result.Value.UnitPrice.Should().Be(120m);
        result.Value.NetUnitPrice.Should().Be(90m);
        result.Value.BracketId.Should().Be(brackets[1].Id);
        result.Value.BracketResolution.Should().Be(BracketResolution.PriceContainment);
        result.Value.FeeBasisAmount.Should().Be(90m);
        result.Value.CommissionPercent.Should().Be(0.20m);
        result.Value.FixedFee.Should().Be(1m);
        result.Value.CommissionAmount.Should().Be(18m);
        result.Value.FeeClampApplied.Should().BeNull();
        result.Value.ExpectedProfitAmount.Should().Be(21m);
        result.Value.EffectiveMarginPercent.Should().Be(0.233333m);
        result.Value.PriceOverridden.Should().BeTrue();
        result.Value.DiscountApplied.Should().BeTrue();
    }

    [Fact]
    public void H005_discount_only_re_resolves_final_fee_bracket_and_reports_price_containment()
    {
        var brackets = Brackets();
        var result = CommercialPricingEngine.Calculate(new CommercialPricingInput(
            UnitTotalCost: 60m, DesiredMargin: 0.20m, RoundingPolicy: PriceRoundingPolicy.NONE, MarginWarningDenominator: 0.10m,
            ManualPriceOverride: null, DiscountKind: CommercialDiscountKind.Amount, DiscountValue: 30m,
            VersionCommissionPercent: 0.10m, VersionFixedFee: 0m, VersionMinimumFee: null, VersionMaximumFee: null, Brackets: brackets));

        result.IsSuccess.Should().BeTrue();
        result.Value.SuggestedPrice.Should().Be(101.66m, "the consistency search resolves bracket B");
        result.Value.SuggestedPrice.Should().BeGreaterThanOrEqualTo(80m);
        result.Value.NetUnitPrice.Should().Be(71.66m);
        result.Value.BracketId.Should().Be(brackets[0].Id);
        result.Value.BracketResolution.Should().Be(BracketResolution.PriceContainment);
        result.Value.FeeBasisAmount.Should().Be(71.66m);
        result.Value.CommissionAmount.Should().Be(7.17m);
        result.Value.FixedFee.Should().Be(0m);
        result.Value.FeeClampApplied.Should().BeNull();
        result.Value.ExpectedProfitAmount.Should().Be(4.49m);
        result.Value.EffectiveMarginPercent.Should().Be(0.062657m);
        result.Value.PriceOverridden.Should().BeFalse();
        result.Value.DiscountApplied.Should().BeTrue();
    }

    [Fact]
    public void H005_upper_bound_is_exclusive_and_open_last_bracket_contains_boundary()
    {
        var result = CommercialPricingEngine.Calculate(new CommercialPricingInput(
            UnitTotalCost: 10m, DesiredMargin: 0.10m, RoundingPolicy: PriceRoundingPolicy.NONE, MarginWarningDenominator: 0.10m,
            ManualPriceOverride: 100m, DiscountKind: CommercialDiscountKind.None, DiscountValue: 0m,
            VersionCommissionPercent: 0m, VersionFixedFee: 0m, VersionMinimumFee: null, VersionMaximumFee: null, Brackets: Brackets()));

        result.IsSuccess.Should().BeTrue();
        result.Value.CommissionPercent.Should().Be(0.20m);
        result.Value.FixedFee.Should().Be(1m);
    }

    [Fact]
    public void H005_clamps_after_rounding()
    {
        var result = CommercialPricingEngine.Calculate(new CommercialPricingInput(
            UnitTotalCost: 10m, DesiredMargin: 0.10m, RoundingPolicy: PriceRoundingPolicy.NONE, MarginWarningDenominator: 0.10m,
            ManualPriceOverride: 50m, DiscountKind: CommercialDiscountKind.None, DiscountValue: 0m,
            VersionCommissionPercent: 0.01m, VersionFixedFee: 0m, VersionMinimumFee: 2m, VersionMaximumFee: null, Brackets: []));

        result.IsSuccess.Should().BeTrue();
        result.Value.CommissionAmount.Should().Be(2m);
        result.Value.FeeClampApplied.Should().Be("MIN");
    }
}
