using FluentAssertions;
using Verce.Modules.Pricing;

namespace Verce.Pricing.Tests;

/// <summary>
/// S5 pricing golden corpus (mission §44 — fresh namespace, never reusing legacy G-identifiers).
/// Implements CALCULATION-RULES CR-07.1/CR-07.2/CR-07.4/CR-07.6 against the REAL
/// <see cref="PricingEngine"/>, not just SharedKernel primitives — this is the sprint that
/// actually builds the engine those earlier golden tests anticipated.
/// </summary>
public class PricingEngineTests
{
    private static PricingCalculationInput Input(decimal cost = 80m, decimal commission = 0m, decimal fixedFee = 0m,
        decimal margin = 0.20m, PriceRoundingPolicy policy = PriceRoundingPolicy.CENT, decimal warningDenominator = 0.10m,
        decimal? minFee = null, decimal? maxFee = null) =>
        new(cost, commission, fixedFee, margin, policy, warningDenominator, minFee, maxFee);

    [Fact]
    public void P1_direct_pricing()
    {
        // TotalCost=80, DesiredMargin=20% => 80 / (1 - 0.20) = 100 (mission §45).
        var result = PricingEngine.Calculate(Input(cost: 80m, margin: 0.20m, policy: PriceRoundingPolicy.NONE));
        result.IsSuccess.Should().BeTrue();
        result.Value.Denominator.Should().Be(0.80m);
        result.Value.SuggestedPrice.Should().Be(100m);
        result.Value.CommissionAmount.Should().Be(0m);
    }

    [Fact]
    public void P2_direct_pricing_with_zero_margin_returns_cost_unchanged()
    {
        var result = PricingEngine.Calculate(Input(cost: 80m, margin: 0m, policy: PriceRoundingPolicy.NONE));
        result.IsSuccess.Should().BeTrue();
        result.Value.Denominator.Should().Be(1m);
        result.Value.SuggestedPrice.Should().Be(80m);
    }

    [Fact]
    public void P3_marketplace_pricing()
    {
        // (70 + 5) / (1 - (0.10 + 0.15)) = 75 / 0.75 = 100 (mission §46).
        var result = PricingEngine.Calculate(Input(cost: 70m, commission: 0.10m, fixedFee: 5m, margin: 0.15m, policy: PriceRoundingPolicy.NONE));
        result.IsSuccess.Should().BeTrue();
        result.Value.Denominator.Should().Be(0.75m);
        result.Value.SuggestedPrice.Should().Be(100m);
        result.Value.CommissionAmount.Should().Be(10m);
    }

    [Fact]
    public void P4_marketplace_fixed_fee_is_added_before_division()
    {
        var withFee = PricingEngine.Calculate(Input(cost: 70m, commission: 0.10m, fixedFee: 5m, margin: 0.15m, policy: PriceRoundingPolicy.NONE));
        var withoutFee = PricingEngine.Calculate(Input(cost: 70m, commission: 0.10m, fixedFee: 0m, margin: 0.15m, policy: PriceRoundingPolicy.NONE));
        withFee.Value.SuggestedPrice.Should().Be(100m);
        withoutFee.Value.SuggestedPrice.Should().Be(93.33m); // 70 / 0.75 = 93.3333... truncated (NONE policy) to 2dp
        withFee.Value.SuggestedPrice.Should().NotBe(withoutFee.Value.SuggestedPrice);
    }

    [Fact]
    public void P5_zero_commission_marketplace_collapses_to_the_direct_formula()
    {
        // ADR-0005 §2: Direct sale is the degenerate marketplace — one formula, no special case.
        var marketplaceZeroCommission = PricingEngine.Calculate(Input(cost: 80m, commission: 0m, fixedFee: 0m, margin: 0.20m, policy: PriceRoundingPolicy.NONE));
        var direct = PricingEngine.Calculate(Input(cost: 80m, margin: 0.20m, policy: PriceRoundingPolicy.NONE));
        marketplaceZeroCommission.Value.SuggestedPrice.Should().Be(direct.Value.SuggestedPrice).And.Be(100m);
    }

    [Fact]
    public void P6_invalid_direct_denominator_is_rejected()
    {
        // DesiredMargin = 100% => denominator <= 0.
        var result = PricingEngine.Calculate(Input(cost: 80m, commission: 0m, margin: 1.0m));
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PRICING_INVALID_MARGIN"); // margin itself is out of [0,1) before the denominator is even computed
    }

    [Fact]
    public void P6b_invalid_direct_denominator_via_commission_plus_margin_at_the_boundary()
    {
        var result = PricingEngine.Calculate(Input(cost: 80m, commission: 0.60m, margin: 0.45m));
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PRICING_INVALID_DENOMINATOR");
    }

    [Fact]
    public void P7_invalid_marketplace_denominator_at_exactly_100_percent_is_rejected()
    {
        var result = PricingEngine.Calculate(Input(cost: 80m, commission: 0.50m, margin: 0.50m));
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PRICING_INVALID_DENOMINATOR");
    }

    [Fact]
    public void P7b_extreme_but_valid_margin_returns_a_warning_not_a_rejection()
    {
        // commission 18% + margin 80% => denominator 0.02, valid but far below the 0.10 warning threshold.
        var result = PricingEngine.Calculate(Input(cost: 80m, commission: 0.18m, margin: 0.80m, policy: PriceRoundingPolicy.NONE));
        result.IsSuccess.Should().BeTrue();
        result.Value.Warnings.Should().ContainSingle().Which.Should().Be("PRICING_EXTREME_MARGIN");
    }

    [Theory]
    [InlineData(PriceRoundingPolicy.CENT, 40.52)]
    [InlineData(PriceRoundingPolicy.TEN_CENTS, 40.60)]
    [InlineData(PriceRoundingPolicy.WHOLE, 41.00)]
    [InlineData(PriceRoundingPolicy.NINETY_NINE, 40.99)]
    [InlineData(PriceRoundingPolicy.NONE, 40.51)]
    public void P8_rounding_policy_matches_CALCULATION_RULES_CR_07_4_worked_table(PriceRoundingPolicy policy, decimal expected)
    {
        // unitTotalCost=15.0438, fixedFee=4.00, commission=18%, margin=35% => rawPrice = 40.5187234...
        var result = PricingEngine.Calculate(new PricingCalculationInput(15.0438m, 0.18m, 4.00m, 0.35m, policy, 0.10m, null, null));
        result.IsSuccess.Should().BeTrue();
        result.Value.SuggestedPrice.Should().Be(expected);
    }

    [Theory]
    // Terra B-01: NINETY_NINE must be a true ceiling to the next commercial N.99 — never a
    // reduction below the raw price. commission=0/margin=0/fixedFee=0 collapses rawPrice to
    // exactly the supplied cost, letting these boundary values be asserted directly.
    [InlineData(40.00, 40.99)]
    [InlineData(40.50, 40.99)]
    [InlineData(40.98, 40.99)]
    [InlineData(40.99, 40.99)] // exact x.99 stays x.99
    [InlineData(40.991, 41.99)] // just above x.99 — the original bug returned 40.99 here (a reduction)
    [InlineData(40.995, 41.99)] // the exact case Terra's B-01 finding named
    [InlineData(41.00, 41.99)]
    [InlineData(41.98, 41.99)]
    [InlineData(41.99, 41.99)]
    [InlineData(41.991, 42.99)]
    [InlineData(0.00, 0.99)] // smallest allowed non-negative cost
    public void P8b_NINETY_NINE_never_rounds_below_the_raw_price(decimal rawPrice, decimal expected)
    {
        var result = PricingEngine.Calculate(Input(cost: rawPrice, commission: 0m, fixedFee: 0m, margin: 0m, policy: PriceRoundingPolicy.NINETY_NINE));
        result.IsSuccess.Should().BeTrue();
        result.Value.SuggestedPrice.Should().Be(expected);
        result.Value.SuggestedPrice.Should().BeGreaterThanOrEqualTo(result.Value.RawPrice,
            "NINETY_NINE must never reduce a price below the raw value it rounds — that would silently erode the requested margin");
    }

    [Fact]
    public void P8c_NINETY_NINE_ceiling_invariant_holds_across_several_integer_ranges()
    {
        foreach (var rawPrice in new[] { 9.995m, 10.001m, 99.991m, 100.995m, 999.999m })
        {
            var result = PricingEngine.Calculate(Input(cost: rawPrice, commission: 0m, fixedFee: 0m, margin: 0m, policy: PriceRoundingPolicy.NINETY_NINE));
            result.IsSuccess.Should().BeTrue();
            result.Value.SuggestedPrice.Should().BeGreaterThanOrEqualTo(rawPrice);
            (result.Value.SuggestedPrice % 1).Should().Be(0.99m, $"the commercial ending must be .99 for raw price {rawPrice}");
        }
    }

    [Fact]
    public void P9_current_product_cost_is_the_engines_UnitTotalCost_input_verbatim()
    {
        // The engine trusts its resolved input exactly like S4's CostEngine (ADR-0018) — this
        // proves the boundary itself: whatever cost is supplied becomes the numerator, unaltered.
        var costFromCatalog = 18.1375m; // a real ProductCostCalculator output (see integration tests)
        var result = PricingEngine.Calculate(Input(cost: costFromCatalog, commission: 0.10m, fixedFee: 5m, margin: 0.35m, policy: PriceRoundingPolicy.NONE));
        result.IsSuccess.Should().BeTrue();
        result.Value.UnitTotalCost.Should().Be(costFromCatalog);
    }

    [Fact]
    public void P10_fee_rule_version_resolution_picks_the_version_valid_at_the_instant()
    {
        var rule = new FeeRule(Guid.NewGuid(), "Test rule");
        rule.AddVersion(new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 1), commissionPercent: 0.10m, fixedFee: 5m,
            FixedFeeApplication.PerUnit, minimumFee: null, maximumFee: null, notes: null);
        rule.AddVersion(new DateOnly(2026, 6, 1), null, commissionPercent: 0.12m, fixedFee: 6m,
            FixedFeeApplication.PerUnit, minimumFee: null, maximumFee: null, notes: null);

        rule.ResolveVersionAt(new DateOnly(2026, 3, 1))!.CommissionPercent.Should().Be(0.10m);
        rule.ResolveVersionAt(new DateOnly(2026, 6, 1))!.CommissionPercent.Should().Be(0.12m, "ValidFrom is inclusive, ValidUntil is exclusive — the boundary belongs to the NEW version");
        rule.ResolveVersionAt(new DateOnly(2025, 12, 31)).Should().BeNull("no version covers a date before the first one begins");
    }

    [Fact]
    public void P10b_fee_rule_version_construction_rejects_an_inverted_window()
    {
        var rule = new FeeRule(Guid.NewGuid(), "Test rule");
        var act = () => rule.AddVersion(new DateOnly(2026, 6, 1), new DateOnly(2026, 1, 1), 0.10m, 0m, FixedFeeApplication.PerUnit, null, null, null);
        act.Should().Throw<ArgumentException>().WithMessage("FEE_RULE_VERSION_INVALID_WINDOW");
    }

    [Fact]
    public void Fee_clamp_minimum_is_applied_when_the_computed_commission_is_below_it()
    {
        var result = PricingEngine.Calculate(Input(cost: 10m, commission: 0.05m, margin: 0.10m, minFee: 5m, policy: PriceRoundingPolicy.NONE));
        result.IsSuccess.Should().BeTrue();
        result.Value.FeeClampApplied.Should().Be("MIN");
        result.Value.CommissionAmount.Should().Be(5m);
    }

    [Fact]
    public void Fee_clamp_maximum_is_applied_when_the_computed_commission_exceeds_it()
    {
        var result = PricingEngine.Calculate(Input(cost: 1000m, commission: 0.30m, margin: 0.10m, maxFee: 50m, policy: PriceRoundingPolicy.NONE));
        result.IsSuccess.Should().BeTrue();
        result.Value.FeeClampApplied.Should().Be("MAX");
        result.Value.CommissionAmount.Should().Be(50m);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.0)]
    public void Invalid_commission_percent_is_rejected(decimal commission)
    {
        var result = PricingEngine.Calculate(Input(commission: commission));
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PRICING_INVALID_COMMISSION");
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.0)]
    public void Invalid_desired_margin_is_rejected(decimal margin)
    {
        var result = PricingEngine.Calculate(Input(margin: margin));
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PRICING_INVALID_MARGIN");
    }

    [Fact]
    public void Negative_cost_is_rejected()
    {
        var result = PricingEngine.Calculate(Input(cost: -1m));
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PRICING_COST_INVALID");
    }

    [Fact]
    public void Negative_fixed_fee_is_rejected()
    {
        var result = PricingEngine.Calculate(Input(fixedFee: -1m));
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PRICING_FIXED_FEE_INVALID");
    }
}
