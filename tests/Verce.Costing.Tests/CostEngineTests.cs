using FluentAssertions;
using Verce.Modules.Costing;

namespace Verce.Costing.Tests;

public class CostEngineTests
{
    [Fact]
    public void Material_cost_applies_line_wastage_to_quantity_and_cost()
    {
        var result = Calculate([Material(quantity: 100m, wastage: 5m, cost: 0.10m)]);

        result.Materials.Should().ContainSingle();
        result.Materials[0].EffectiveQuantityBaseUnit.Should().Be(105m);
        result.Materials[0].CostBeforeWastage.Should().Be(10m);
        result.Materials[0].WastageCost.Should().Be(0.5m);
        result.Materials[0].CostAfterWastage.Should().Be(10.5m);
    }

    [Fact]
    public void CR_13_1_G13_weighted_acquisition_input_is_costed_without_changing_its_basis()
    {
        var result = Calculate([Material(quantity: 100m, wastage: 5m, cost: 0.116667m)]);
        result.Materials[0].CostAfterWastage.Should().Be(12.250035m);
    }

    [Fact]
    public void CR_13_2_G14_material_wastage_uses_percentage_points()
    {
        var result = Calculate([Material(quantity: 100m, wastage: 5m, cost: 0.10m)]);
        result.Materials[0].WastageCost.Should().Be(0.5m);
    }

    [Fact]
    public void Multiple_and_duplicate_material_lines_remain_independent()
    {
        var first = Material(quantity: 10m, cost: 2m);
        var second = first with { EnteredQuantity = 5m, NormalizedQuantityBaseUnit = 5m };

        var result = Calculate([first, second]);

        result.Materials.Should().HaveCount(2);
        result.Materials.Select(line => line.CostAfterWastage).Should().Equal(20m, 10m);
        result.Totals.MaterialsTotalCost.Should().Be(30m);
    }

    [Fact]
    public void Labor_uses_decimal_minutes_over_sixty()
    {
        var result = Calculate([], labor: new(30m, 40m, LaborRateSource.DEFAULT_SETTING));
        result.Totals.LaborCost.Should().Be(20m);
        result.Labor!.RateSource.Should().Be(LaborRateSource.DEFAULT_SETTING);
    }

    [Fact]
    public void CR_13_3_G15_labor_uses_manual_zero_rate_as_a_real_override()
    {
        var result = Calculate([], labor: new(30m, 0m, LaborRateSource.MANUAL_OVERRIDE));
        result.Labor!.RateSource.Should().Be(LaborRateSource.MANUAL_OVERRIDE);
        result.Labor.Cost.Should().Be(0m);
    }

    [Fact]
    public void Machine_uses_decimal_minutes_over_sixty()
    {
        var result = Calculate([], machine: new(120m, 3m));
        result.Totals.MachineCost.Should().Be(6m);
    }

    [Fact]
    public void CR_13_4_G16_machine_cost_uses_decimal_minutes_over_sixty()
    {
        Calculate([], machine: new(120m, 3m)).Machine!.Cost.Should().Be(6m);
    }

    [Fact]
    public void Additional_costs_and_batch_output_produce_unit_estimate()
    {
        var result = Calculate([], additional: [new("Terceirização", 125m)], output: 10);
        result.Totals.TotalEstimatedCost.Should().Be(125m);
        result.Totals.EstimatedUnitCost.Should().Be(12.5m);
    }

    [Fact]
    public void CR_13_5_G17_batch_division_retains_six_decimal_places()
    {
        var result = Calculate([], additional: [new("Serviço", 39.95m)], output: 2);
        result.Totals.EstimatedUnitCost.Should().Be(19.975m);
    }

    [Fact]
    public void Combined_components_sum_to_fifty()
    {
        var result = Calculate(
            [Material(quantity: 100m, cost: 0.10m)],
            labor: new(30m, 40m, LaborRateSource.MANUAL_OVERRIDE),
            machine: new(120m, 3m),
            additional: [new("Acabamento", 14m)]);

        result.Totals.TotalEstimatedCost.Should().Be(50m);
    }

    [Fact]
    public void CR_13_6_G18_multi_material_full_costing_is_reconciled()
    {
        var result = Calculate([Material(quantity: 120m, wastage: 5m, cost: .1m), Material(quantity: 35m, wastage: 5m, cost: .2m)],
            labor: new(20m, 30m, LaborRateSource.MANUAL_OVERRIDE), machine: new(180m, 2m), additional: [new("Packaging", 4m)], output: 2);
        result.Totals.TotalEstimatedCost.Should().Be(39.95m);
        result.Totals.EstimatedUnitCost.Should().Be(19.975m);
    }

    [Fact]
    public void Manual_override_source_is_explicit()
    {
        var result = Calculate([Material(cost: 0.25m, source: MaterialCostSource.MANUAL_OVERRIDE)]);
        result.Materials[0].CostSource.Should().Be(MaterialCostSource.MANUAL_OVERRIDE);
        result.Materials[0].CostPolicy.Should().Be("MANUAL_OVERRIDE");
    }

    [Fact]
    public void CR_13_7_G19_manual_material_override_is_explicit()
    {
        Calculate([Material(cost: .25m, source: MaterialCostSource.MANUAL_OVERRIDE)]).Materials[0].CostSource
            .Should().Be(MaterialCostSource.MANUAL_OVERRIDE);
    }

    [Fact]
    public void CR_13_2_explicit_zero_wastage_does_not_fall_back_to_default()
    {
        Calculate([Material(quantity: 100m, wastage: 0m, cost: .1m)]).Materials[0].WastageCost.Should().Be(0m);
    }

    [Fact]
    public void Effective_quantity_above_stock_warns_but_still_calculates()
    {
        var result = Calculate([Material(quantity: 100m, wastage: 10m, stock: 105m)]);
        result.Materials[0].ExceedsCurrentStock.Should().BeTrue();
        result.Materials[0].Warnings.Should().ContainSingle(CostEngine.StockWarning);
        result.Totals.TotalEstimatedCost.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Precision_and_rounding_are_half_up_at_the_governed_scales()
    {
        var result = Calculate([Material(quantity: 1.23455m, wastage: 0m, cost: 0.1234567m)]);
        result.Materials[0].NormalizedQuantityBaseUnit.Should().Be(1.2346m);
        result.Materials[0].UnitCostBaseUnit.Should().Be(0.123457m);
        result.Materials[0].CostAfterWastage.Should().Be(0.15242m);
    }

    [Fact]
    public void Empty_calculation_is_rejected()
    {
        var action = () => Calculate([]);
        action.Should().Throw<ArgumentException>().WithMessage("COST_CALCULATION_EMPTY");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Invalid_wastage_is_rejected(decimal wastage)
    {
        var action = () => Calculate([Material(wastage: wastage)]);
        action.Should().Throw<ArgumentException>().WithMessage("WASTAGE_PERCENT_INVALID");
    }

    [Fact]
    public void Invalid_output_quantity_is_rejected()
    {
        var action = () => Calculate([Material()], output: 0);
        action.Should().Throw<ArgumentException>().WithMessage("OUTPUT_QUANTITY_INVALID");
    }

    [Theory]
    [InlineData(-1)]
    public void CR_13_1_negative_material_quantity_is_rejected(decimal value) =>
        AssertCode(() => Calculate([Material(quantity: value)]), "MATERIAL_QUANTITY_MUST_BE_POSITIVE");

    [Theory]
    [InlineData(-1)]
    public void CR_13_3_negative_labor_minutes_and_rate_are_rejected(decimal value)
    {
        AssertCode(() => Calculate([], labor: new(value, 1m, LaborRateSource.DEFAULT_SETTING)), "LABOR_MINUTES_INVALID");
        AssertCode(() => Calculate([], labor: new(1m, value, LaborRateSource.DEFAULT_SETTING)), "LABOR_RATE_INVALID");
    }

    [Theory]
    [InlineData(-1)]
    public void CR_13_4_negative_machine_minutes_and_rate_are_rejected(decimal value)
    {
        AssertCode(() => Calculate([], machine: new(value, 1m)), "MACHINE_MINUTES_INVALID");
        AssertCode(() => Calculate([], machine: new(1m, value)), "MACHINE_RATE_INVALID");
    }

    [Theory]
    [InlineData(-1)]
    public void CR_13_8_negative_additional_amount_is_rejected(decimal value) =>
        AssertCode(() => Calculate([], additional: [new("Serviço", value)]), "ADDITIONAL_COST_AMOUNT_INVALID");

    private static void AssertCode(Action action, string code) => action.Should().Throw<ArgumentException>().WithMessage(code);

    private static CostCalculationResult Calculate(
        IReadOnlyList<ResolvedMaterialCostInput> materials,
        ResolvedLaborCostInput? labor = null,
        MachineCostInput? machine = null,
        IReadOnlyList<AdditionalDirectCostInput>? additional = null,
        int output = 1) =>
        CostEngine.Calculate(new CostCalculationInput(materials, labor, machine, additional ?? [], output));

    private static ResolvedMaterialCostInput Material(
        decimal quantity = 1m,
        decimal wastage = 0m,
        decimal cost = 1m,
        decimal stock = 1_000m,
        MaterialCostSource source = MaterialCostSource.WEIGHTED_AVERAGE_ACQUISITION) =>
        new(Guid.NewGuid(), "MAT-001", "Material", quantity, "Gram", quantity, "Gram", wastage, cost, source, stock);
}
