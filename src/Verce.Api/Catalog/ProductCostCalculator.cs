using Verce.Modules.Catalog;
using Verce.Modules.Costing;
using Verce.Modules.Settings;
using Verce.SharedKernel;

namespace Verce.Api.Catalog;

/// <summary>
/// Translates a persisted <see cref="ProductRecipe"/> into S4's authoritative
/// <see cref="CostCalculationInput"/> and runs the SAME <see cref="CostEngine"/> the Cost
/// Laboratory uses (mission §5/§12) — there is no second implementation of material
/// normalization, weighted acquisition cost, wastage, labor, machine cost, additional direct
/// costs, batch division or rounding anywhere in Catalog. This is a stateless read: no Supply,
/// InventoryMovement or Recipe row is ever touched (mission §15), exactly like S4's Cost Lab
/// endpoint the recipe reuses this same acquisition-cost port and Settings reader.
/// </summary>
public static class ProductCostCalculator
{
    public static async Task<CostCalculationResult> CalculateAsync(
        Product product, ICostingInventoryReader inventory, AppSettingValueReader settings, CancellationToken ct)
    {
        var recipe = product.Recipe;
        if (recipe.MaterialLines.Count == 0 && recipe.LaborMinutes is not (> 0)
            && recipe.MachineMinutes is not (> 0) && recipe.AdditionalCostLines.Count == 0)
            throw new ArgumentException("RECIPE_EMPTY");

        var supplyIds = recipe.MaterialLines.Select(x => x.SupplyId).Distinct().ToArray();
        var supplies = supplyIds.Length == 0
            ? new Dictionary<Guid, CostingSupplySource>()
            : await inventory.GetAsync(supplyIds, ct);

        var configuredWastage = await settings.GetDecimalAsync("costing.default_wastage_rate", ct);
        var configuredLaborRate = await settings.GetDecimalAsync("costing.default_labor_hourly_rate", ct);

        var resolvedMaterials = new List<ResolvedMaterialCostInput>(recipe.MaterialLines.Count);
        foreach (var line in recipe.MaterialLines)
        {
            if (!supplies.TryGetValue(line.SupplyId, out var supply)) throw new ArgumentException("SUPPLY_NOT_FOUND");

            decimal unitCost;
            MaterialCostSource source;
            if (line.ManualUnitCostOverride is { } manual)
            {
                unitCost = Rounding.ToInternal(manual);
                source = MaterialCostSource.MANUAL_OVERRIDE;
            }
            else if (supply.AcquisitionBasis is { } basis)
            {
                unitCost = basis.WeightedAverageUnitCost;
                source = MaterialCostSource.WEIGHTED_AVERAGE_ACQUISITION;
            }
            else
            {
                throw new ArgumentException("COST_BASIS_UNAVAILABLE");
            }

            resolvedMaterials.Add(new ResolvedMaterialCostInput(
                supply.Id, supply.Code, supply.Name, line.EnteredQuantity, line.EnteredUnit,
                line.NormalizedQuantityBaseUnit, supply.BaseUnit,
                line.WastagePercentOverride ?? recipe.WastagePercentOverride ?? configuredWastage,
                unitCost, source, supply.CurrentStockBaseUnit));
        }

        ResolvedLaborCostInput? labor = null;
        if (recipe.LaborMinutes is { } laborMinutes)
        {
            var rate = recipe.LaborHourlyRateOverride ?? configuredLaborRate;
            labor = new ResolvedLaborCostInput(laborMinutes, rate,
                recipe.LaborHourlyRateOverride.HasValue ? LaborRateSource.MANUAL_OVERRIDE : LaborRateSource.DEFAULT_SETTING);
        }

        MachineCostInput? machine = null;
        if (recipe.MachineMinutes is { } machineMinutes)
        {
            if (recipe.MachineHourlyRate is not { } machineRate) throw new ArgumentException("RECIPE_MACHINE_RATE_REQUIRED");
            machine = new MachineCostInput(machineMinutes, machineRate);
        }

        var additionalCosts = recipe.AdditionalCostLines
            .Select(x => new AdditionalDirectCostInput(x.Description, x.Amount)).ToArray();

        var input = new CostCalculationInput(resolvedMaterials, labor, machine, additionalCosts, recipe.OutputQuantity);
        return CostEngine.Calculate(input);
    }
}
