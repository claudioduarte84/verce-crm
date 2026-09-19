using Microsoft.AspNetCore.Antiforgery;
using Verce.Api.Authorization;
using Verce.Modules.Costing;
using Verce.Modules.Inventory;
using Verce.Modules.Settings;
using Verce.SharedKernel;

namespace Verce.Api.Costing;

public sealed record CostingMaterialRequest(
    Guid SupplyId,
    decimal Quantity,
    SupplyBaseUnit EnteredUnit,
    decimal? WastagePercentOverride,
    decimal? ManualUnitCostOverride);

public sealed record CostingLaborRequest(decimal Minutes, decimal? ManualHourlyRateOverride);
public sealed record CostingMachineRequest(decimal Minutes, decimal HourlyRate);
public sealed record CostingAdditionalDirectCostRequest(string Description, decimal Amount);
public sealed record CostCalculationRequest(
    IReadOnlyList<CostingMaterialRequest>? Materials,
    decimal? DefaultWastagePercent,
    CostingLaborRequest? Labor,
    CostingMachineRequest? Machine,
    IReadOnlyList<CostingAdditionalDirectCostRequest>? AdditionalDirectCosts,
    int OutputQuantity = 1,
    bool IncludeInactiveSupplies = false);

public sealed record SupplyCostBasisResponse(
    Guid SupplyId,
    string SupplyCode,
    string SupplyName,
    SupplyBaseUnit BaseUnit,
    bool Active,
    decimal CurrentStockBaseUnit,
    string Policy,
    decimal? WeightedAverageUnitCost,
    decimal EligibleQuantityBaseUnit,
    int EligibleReceiptCount,
    bool Available);

public sealed record CostingSupplyListItemResponse(
    Guid Id,
    string Code,
    string Name,
    SupplyBaseUnit BaseUnit,
    bool Active,
    decimal CurrentStockBaseUnit,
    string Policy,
    decimal? WeightedAverageUnitCost,
    bool CostBasisAvailable);

public static class CostingEndpoints
{
    private static readonly HashSet<string> ControlledRequestCodes = new(StringComparer.Ordinal)
    {
        "ADDITIONAL_COST_AMOUNT_INVALID", "ADDITIONAL_COST_DESCRIPTION_INVALID", "COST_CALCULATION_EMPTY",
        "COST_INPUT_INVALID", "CURRENT_STOCK_INVALID", "LABOR_MINUTES_INVALID", "LABOR_RATE_INVALID",
        "MACHINE_MINUTES_INVALID", "MACHINE_RATE_INVALID", "MATERIAL_INVALID", "MATERIAL_QUANTITY_MUST_BE_POSITIVE",
        "MATERIAL_UNIT_COST_INVALID", "OUTPUT_QUANTITY_INVALID", "QUANTITY_BELOW_BASE_PRECISION",
        "QUANTITY_BELOW_ENTERED_PRECISION", "QUANTITY_MUST_BE_POSITIVE", "UNIT_CONVERSION_NOT_SUPPORTED",
        "WASTAGE_PERCENT_INVALID",
    };
    public static IEndpointRouteBuilder MapCostingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/costing");

        group.MapGet("/supplies", async (string? search, bool includeInactive, ICostingInventoryReader inventory, CancellationToken ct) =>
        {
            var supplies = await inventory.SearchAsync(search, includeInactive, ct);
            var response = supplies.Select(supply =>
            {
                var basis = supply.AcquisitionBasis;
                return new CostingSupplyListItemResponse(supply.Id, supply.Code, supply.Name, ParseUnit(supply.BaseUnit),
                    supply.Active, supply.CurrentStockBaseUnit, CostEngine.WeightedAveragePolicy,
                    basis?.WeightedAverageUnitCost, basis is not null);
            }).ToArray();
            return Results.Ok(response);
        }).RequireAuthorization(Permissions.CostingRead).Produces<IReadOnlyList<CostingSupplyListItemResponse>>();

        group.MapGet("/supplies/{id:guid}/cost-basis", async (Guid id, ICostingInventoryReader inventory, CancellationToken ct) =>
        {
            var supply = await inventory.GetAsync(id, ct);
            if (supply is null) return Results.NotFound();
            var basis = supply.AcquisitionBasis;
            return Results.Ok(new SupplyCostBasisResponse(
                supply.Id, supply.Code, supply.Name, ParseUnit(supply.BaseUnit), supply.Active,
                supply.CurrentStockBaseUnit, CostEngine.WeightedAveragePolicy,
                basis?.WeightedAverageUnitCost, basis?.EligibleQuantityBaseUnit ?? 0m,
                basis?.EligibleReceiptCount ?? 0, basis is not null));
        }).RequireAuthorization(Permissions.CostingRead).Produces<SupplyCostBasisResponse>().Produces(StatusCodes.Status404NotFound);

        group.MapPost("/calculate", async (CostCalculationRequest request, HttpContext http,
            IAntiforgery antiforgery, ICostingInventoryReader inventory, AppSettingValueReader settings, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return Problem("ANTIFORGERY_VALIDATION_FAILED");
            try
            {
                var materials = request.Materials ?? [];
                var additionalCosts = request.AdditionalDirectCosts ?? [];
                var supplyIds = materials.Select(line => line.SupplyId).Distinct().ToArray();
                var supplies = await inventory.GetAsync(supplyIds, ct);
                if (supplies.Count != supplyIds.Length) return Problem("SUPPLY_NOT_FOUND", StatusCodes.Status404NotFound);
                if (!request.IncludeInactiveSupplies && supplies.Values.Any(supply => !supply.Active))
                    return Problem("INACTIVE_SUPPLY_REQUIRES_EXPLICIT_OPT_IN");

                var configuredWastage = await settings.GetDecimalAsync("costing.default_wastage_rate", ct);
                var configuredLaborRate = await settings.GetDecimalAsync("costing.default_labor_hourly_rate", ct);
                var resolvedMaterials = new List<ResolvedMaterialCostInput>(materials.Count);

                foreach (var line in materials)
                {
                    var supply = supplies[line.SupplyId];
                    var baseUnit = ParseUnit(supply.BaseUnit);
                    var normalized = SupplyUnitConversion.NormalizePositive(line.Quantity, line.EnteredUnit, baseUnit);
                    decimal unitCost;
                    MaterialCostSource source;
                    if (line.ManualUnitCostOverride is { } manual)
                    {
                        if (manual < 0) throw new ArgumentException("MATERIAL_UNIT_COST_INVALID");
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
                        return Problem("COST_BASIS_UNAVAILABLE", StatusCodes.Status422UnprocessableEntity);
                    }

                    resolvedMaterials.Add(new ResolvedMaterialCostInput(
                        supply.Id, supply.Code, supply.Name, normalized.EnteredQuantity, line.EnteredUnit.ToString(),
                        normalized.QuantityBaseUnit, supply.BaseUnit,
                        line.WastagePercentOverride ?? request.DefaultWastagePercent ?? configuredWastage,
                        unitCost, source, supply.CurrentStockBaseUnit));
                }

                ResolvedLaborCostInput? labor = null;
                if (request.Labor is { } laborRequest)
                {
                    var manual = laborRequest.ManualHourlyRateOverride;
                    labor = new ResolvedLaborCostInput(laborRequest.Minutes, manual ?? configuredLaborRate,
                        manual.HasValue ? LaborRateSource.MANUAL_OVERRIDE : LaborRateSource.DEFAULT_SETTING);
                }

                var input = new CostCalculationInput(
                    resolvedMaterials,
                    labor,
                    request.Machine is null ? null : new MachineCostInput(request.Machine.Minutes, request.Machine.HourlyRate),
                    additionalCosts.Select(line => new AdditionalDirectCostInput(line.Description, line.Amount)).ToArray(),
                    request.OutputQuantity);
                return Results.Ok(CostEngine.Calculate(input));
            }
            catch (ArgumentException ex) when (ex.Message.StartsWith("SETTING_", StringComparison.Ordinal))
            {
                return Problem("COSTING_CONFIGURATION_INVALID", StatusCodes.Status500InternalServerError);
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("SETTING_", StringComparison.Ordinal))
            {
                return Problem("COSTING_CONFIGURATION_INVALID", StatusCodes.Status500InternalServerError);
            }
            catch (ArgumentException ex)
            {
                return Problem(ControlledRequestCodes.Contains(ex.Message) ? ex.Message : "COSTING_REQUEST_INVALID");
            }
            catch (OverflowException)
            {
                return Problem("COST_VALUE_OUT_OF_RANGE");
            }
        }).RequireAuthorization(Permissions.CostingCalculate).Produces<CostCalculationResult>()
            .Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        return app;
    }

    private static SupplyBaseUnit ParseUnit(string value) =>
        Enum.TryParse<SupplyBaseUnit>(value, out var unit) ? unit : throw new InvalidOperationException("SUPPLY_BASE_UNIT_INVALID");

    private static async Task<bool> IsValidCsrf(HttpContext http, IAntiforgery antiforgery)
    {
        try { await antiforgery.ValidateRequestAsync(http); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    private static IResult Problem(string code, int status = StatusCodes.Status400BadRequest) =>
        Results.Problem(statusCode: status, title: "Não foi possível calcular o custo.",
            extensions: new Dictionary<string, object?> { ["code"] = code });

}
