using FluentAssertions;
using Verce.SharedKernel;
using Verce.SharedKernel.ValueObjects;

namespace Verce.SharedKernel.Tests;

/// <summary>
/// Reproduces the golden cases from docs/CALCULATION-RULES.md §13 using only the SharedKernel
/// primitives (Money, Grams, Rounding). The CostEngine and PricingEngine themselves are S4/S5
/// scope — these tests exist to prove the ARITHMETIC BUILDING BLOCKS reproduce the documented
/// results exactly, so a future engine built on them inherits correctness rather than having to
/// discover it.
/// </summary>
public class CalculationRulesGoldenCaseTests
{
    [Fact]
    public void G1_single_filament_component()
    {
        // 72g PLA Preto @ R$89,90/kg => R$6,472800 (CR-01.1)
        var grams = new Grams(72m);
        var pricePerKg = new Money(89.90m);

        var cost = Rounding.ToInternal(grams.ToKilograms() * pricePerKg.Amount);

        cost.Should().Be(6.472800m);
    }

    [Fact]
    public void G2_multiple_filament_components()
    {
        // 72g @ 89.90/kg + 18g @ 94.50/kg => R$8,173800 (CR-01.2)
        var c1 = Rounding.ToInternal(new Grams(72m).ToKilograms() * 89.90m);
        var c2 = Rounding.ToInternal(new Grams(18m).ToKilograms() * 94.50m);

        var total = Rounding.ToInternal(c1 + c2);

        total.Should().Be(8.173800m);
    }

    [Fact]
    public void G3_laboratory_worked_example_unit_total_cost()
    {
        // CR-06.5: PLA Preto 84g + PLA Laranja 22g + Cola manual 0.40 + Parafuso 4un@0.18
        // + Energia 0.21kWh@0.92 + Máquina 3h25min@1.20/h => unitTotalCost R$15,043800
        var filamentCost = Rounding.ToInternal(
            Rounding.ToInternal(new Grams(84m).ToKilograms() * 89.90m) +
            Rounding.ToInternal(new Grams(22m).ToKilograms() * 94.50m));

        var suppliesCost = Rounding.ToInternal(4m * 0.18m);
        var manualCost = 0.400000m;
        var energyCost = Rounding.ToInternal(0.21m * 0.92m);

        var printHours = 12_300m / 3600m; // 3h25min = 12300s
        var machineCost = Rounding.ToInternal(printHours * 1.20m);

        var directCost = Rounding.ToInternal(filamentCost + suppliesCost + manualCost + energyCost + machineCost);
        var unitTotalCost = directCost; // wastage 0% in this example

        filamentCost.Should().Be(9.630600m);
        suppliesCost.Should().Be(0.720000m);
        energyCost.Should().Be(0.193200m);
        machineCost.Should().Be(4.100000m);
        directCost.Should().Be(15.043800m);
        unitTotalCost.Should().Be(15.043800m);
    }

    [Fact]
    public void G4_direct_sale_pricing()
    {
        // unitTotalCost / (1 - margin) => 15.0438 / 0.65 = 23.14 (CR-07.2)
        var unitTotalCost = 15.0438m;
        var margin = 0.35m;

        var suggestedPrice = Rounding.ToMoney(unitTotalCost / (1 - margin));

        suggestedPrice.Should().Be(23.14m);
    }

    [Fact]
    public void G5_marketplace_pricing()
    {
        // (cost + fixedFee) / (1 - (commission + margin)) => (15.0438+4.00)/0.47 = 40.52 (CR-07.2)
        var unitTotalCost = 15.0438m;
        var fixedFee = 4.00m;
        var commission = 0.18m;
        var margin = 0.35m;

        var denominator = 1 - (commission + margin);
        var suggestedPrice = Rounding.ToMoney((unitTotalCost + fixedFee) / denominator);

        suggestedPrice.Should().Be(40.52m);
    }

    [Fact]
    public void G6_invalid_denominator_is_detectable_before_division()
    {
        // commission 0.60 + margin 0.45 => denominator <= 0, must never be divided (CR-07.1)
        var commission = 0.60m;
        var margin = 0.45m;

        var denominator = 1 - (commission + margin);

        denominator.Should().BeLessThanOrEqualTo(0);
        // The future PricingEngine (S5) must check this BEFORE dividing and return
        // PRICING_INVALID_DENOMINATOR rather than a silently wrong number.
    }

    [Fact]
    public void G10_estimated_vs_actual_variance()
    {
        // 90g estimated, 96g actual => +6g, +0.066667 relative (CR-09.4)
        var estimated = new Grams(90m);
        var actual = new Grams(96m);

        var absoluteVariance = actual.Value - estimated.Value;
        var relativeVariance = Rounding.ToPercent(absoluteVariance / estimated.Value);

        absoluteVariance.Should().Be(6m);
        relativeVariance.Should().Be(0.066667m);
    }
}
