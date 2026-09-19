using System.Text.Json.Serialization;
using Verce.SharedKernel;

namespace Verce.Modules.Costing;

[JsonConverter(typeof(JsonStringEnumConverter<MaterialCostSource>))]
public enum MaterialCostSource
{
    WEIGHTED_AVERAGE_ACQUISITION,
    MANUAL_OVERRIDE,
}

[JsonConverter(typeof(JsonStringEnumConverter<LaborRateSource>))]
public enum LaborRateSource
{
    DEFAULT_SETTING,
    MANUAL_OVERRIDE,
}

public sealed record ResolvedMaterialCostInput(
    Guid SupplyId,
    string SupplyCode,
    string SupplyName,
    decimal EnteredQuantity,
    string EnteredUnit,
    decimal NormalizedQuantityBaseUnit,
    string BaseUnit,
    decimal WastagePercent,
    decimal UnitCostBaseUnit,
    MaterialCostSource CostSource,
    decimal CurrentStockBaseUnit);

public sealed record ResolvedLaborCostInput(decimal Minutes, decimal HourlyRate, LaborRateSource RateSource);
public sealed record MachineCostInput(decimal Minutes, decimal HourlyRate);
public sealed record AdditionalDirectCostInput(string Description, decimal Amount);
public sealed record CostCalculationInput(
    IReadOnlyList<ResolvedMaterialCostInput> Materials,
    ResolvedLaborCostInput? Labor,
    MachineCostInput? Machine,
    IReadOnlyList<AdditionalDirectCostInput> AdditionalDirectCosts,
    int OutputQuantity);

public sealed record MaterialCostBreakdown(
    Guid SupplyId,
    string SupplyCode,
    string SupplyName,
    decimal EnteredQuantity,
    string EnteredUnit,
    decimal NormalizedQuantityBaseUnit,
    string BaseUnit,
    decimal WastagePercent,
    decimal EffectiveQuantityBaseUnit,
    MaterialCostSource CostSource,
    string CostPolicy,
    decimal UnitCostBaseUnit,
    decimal CostBeforeWastage,
    decimal WastageCost,
    decimal CostAfterWastage,
    decimal CurrentStockBaseUnit,
    bool ExceedsCurrentStock,
    IReadOnlyList<string> Warnings);

public sealed record LaborCostBreakdown(decimal Minutes, decimal HourlyRate, LaborRateSource RateSource, decimal Cost);
public sealed record MachineCostBreakdown(decimal Minutes, decimal HourlyRate, decimal Cost);
public sealed record AdditionalDirectCostBreakdown(string Description, decimal Amount);
public sealed record CostTotals(
    decimal MaterialCostBeforeWastage,
    decimal MaterialWastageCost,
    decimal MaterialsTotalCost,
    decimal LaborCost,
    decimal MachineCost,
    decimal AdditionalDirectCosts,
    decimal TotalEstimatedCost,
    int OutputQuantity,
    decimal EstimatedUnitCost);

public sealed record CostCalculationResult(
    string EngineVersion,
    IReadOnlyList<MaterialCostBreakdown> Materials,
    LaborCostBreakdown? Labor,
    MachineCostBreakdown? Machine,
    IReadOnlyList<AdditionalDirectCostBreakdown> AdditionalDirectCosts,
    CostTotals Totals);

/// <summary>
/// Pure, deterministic S4 cost engine. All I/O (settings, inventory and HTTP) is resolved before
/// this boundary; the engine performs decimal arithmetic only and has no persistence side effects.
/// </summary>
public static class CostEngine
{
    public const string Version = "1.0.0";
    public const string WeightedAveragePolicy = "WEIGHTED_AVERAGE_ACQUISITION";
    public const string StockWarning = "REQUESTED_QUANTITY_EXCEEDS_CURRENT_STOCK";
    public const decimal MaximumWastagePercent = 100m;
    public const decimal MaximumRateOrAmount = 1_000_000_000_000m;
    public const decimal MaximumMinutes = 525_600m;

    public static CostCalculationResult Calculate(CostCalculationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.OutputQuantity < 1 || input.OutputQuantity > 1_000_000)
            throw new ArgumentException("OUTPUT_QUANTITY_INVALID");
        if (input.Materials is null || input.AdditionalDirectCosts is null)
            throw new ArgumentException("COST_INPUT_INVALID");

        var materialResults = input.Materials.Select(CalculateMaterial).ToArray();
        var labor = CalculateLabor(input.Labor);
        var machine = CalculateMachine(input.Machine);
        var additional = input.AdditionalDirectCosts.Select(CalculateAdditional).ToArray();

        var hasComponent = materialResults.Length > 0
            || input.Labor is { Minutes: > 0 }
            || input.Machine is { Minutes: > 0 }
            || additional.Any(item => item.Amount > 0);
        if (!hasComponent) throw new ArgumentException("COST_CALCULATION_EMPTY");

        var materialBefore = Sum(materialResults.Select(item => item.CostBeforeWastage));
        var wastage = Sum(materialResults.Select(item => item.WastageCost));
        var materialsTotal = Sum(materialResults.Select(item => item.CostAfterWastage));
        var laborCost = labor?.Cost ?? 0m;
        var machineCost = machine?.Cost ?? 0m;
        var additionalCost = Sum(additional.Select(item => item.Amount));
        var total = Rounding.ToInternal(materialsTotal + laborCost + machineCost + additionalCost);
        var unit = Rounding.ToInternal(total / input.OutputQuantity);

        return new CostCalculationResult(
            Version,
            materialResults,
            labor,
            machine,
            additional,
            new CostTotals(materialBefore, wastage, materialsTotal, laborCost, machineCost,
                additionalCost, total, input.OutputQuantity, unit));
    }

    private static MaterialCostBreakdown CalculateMaterial(ResolvedMaterialCostInput material)
    {
        if (material.SupplyId == Guid.Empty || string.IsNullOrWhiteSpace(material.SupplyCode)
            || string.IsNullOrWhiteSpace(material.SupplyName) || string.IsNullOrWhiteSpace(material.EnteredUnit)
            || string.IsNullOrWhiteSpace(material.BaseUnit))
            throw new ArgumentException("MATERIAL_INVALID");
        if (material.EnteredQuantity <= 0 || material.NormalizedQuantityBaseUnit <= 0)
            throw new ArgumentException("MATERIAL_QUANTITY_MUST_BE_POSITIVE");
        ValidateWastage(material.WastagePercent);
        ValidateNonNegative(material.UnitCostBaseUnit, "MATERIAL_UNIT_COST_INVALID");
        ValidateNonNegative(material.CurrentStockBaseUnit, "CURRENT_STOCK_INVALID");

        var normalized = Rounding.ToQuantity(material.NormalizedQuantityBaseUnit);
        if (normalized <= 0) throw new ArgumentException("QUANTITY_BELOW_BASE_PRECISION");
        var effective = Rounding.ToQuantity(normalized * (1m + material.WastagePercent / 100m));
        var before = Rounding.ToInternal(normalized * material.UnitCostBaseUnit);
        var after = Rounding.ToInternal(effective * material.UnitCostBaseUnit);
        var waste = Rounding.ToInternal(after - before);
        var exceeds = effective > material.CurrentStockBaseUnit;

        return new MaterialCostBreakdown(
            material.SupplyId,
            material.SupplyCode,
            material.SupplyName,
            material.EnteredQuantity,
            material.EnteredUnit,
            normalized,
            material.BaseUnit,
            material.WastagePercent,
            effective,
            material.CostSource,
            material.CostSource == MaterialCostSource.MANUAL_OVERRIDE ? "MANUAL_OVERRIDE" : WeightedAveragePolicy,
            Rounding.ToInternal(material.UnitCostBaseUnit),
            before,
            waste,
            after,
            Rounding.ToQuantity(material.CurrentStockBaseUnit),
            exceeds,
            exceeds ? [StockWarning] : []);
    }

    private static LaborCostBreakdown? CalculateLabor(ResolvedLaborCostInput? labor)
    {
        if (labor is null) return null;
        ValidateMinutes(labor.Minutes, "LABOR_MINUTES_INVALID");
        ValidateNonNegative(labor.HourlyRate, "LABOR_RATE_INVALID");
        return new LaborCostBreakdown(labor.Minutes, Rounding.ToInternal(labor.HourlyRate), labor.RateSource,
            Rounding.ToInternal(labor.Minutes / 60m * labor.HourlyRate));
    }

    private static MachineCostBreakdown? CalculateMachine(MachineCostInput? machine)
    {
        if (machine is null) return null;
        ValidateMinutes(machine.Minutes, "MACHINE_MINUTES_INVALID");
        ValidateNonNegative(machine.HourlyRate, "MACHINE_RATE_INVALID");
        return new MachineCostBreakdown(machine.Minutes, Rounding.ToInternal(machine.HourlyRate),
            Rounding.ToInternal(machine.Minutes / 60m * machine.HourlyRate));
    }

    private static AdditionalDirectCostBreakdown CalculateAdditional(AdditionalDirectCostInput input)
    {
        var description = input.Description?.Trim() ?? string.Empty;
        if (description.Length is < 1 or > 200) throw new ArgumentException("ADDITIONAL_COST_DESCRIPTION_INVALID");
        ValidateNonNegative(input.Amount, "ADDITIONAL_COST_AMOUNT_INVALID");
        return new AdditionalDirectCostBreakdown(description, Rounding.ToInternal(input.Amount));
    }

    private static void ValidateWastage(decimal value)
    {
        if (value < 0 || value > MaximumWastagePercent)
            throw new ArgumentException("WASTAGE_PERCENT_INVALID");
    }

    private static void ValidateMinutes(decimal value, string code)
    {
        if (value < 0 || value > MaximumMinutes) throw new ArgumentException(code);
    }

    private static void ValidateNonNegative(decimal value, string code)
    {
        if (value < 0 || value > MaximumRateOrAmount) throw new ArgumentException(code);
    }

    private static decimal Sum(IEnumerable<decimal> values) => Rounding.ToInternal(values.Sum());
}
