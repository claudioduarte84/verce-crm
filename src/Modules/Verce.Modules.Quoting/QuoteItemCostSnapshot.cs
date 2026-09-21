using Verce.SharedKernel.Domain;

namespace Verce.Modules.Quoting;

/// <summary>
/// B-02 correction: every cost component S4's <c>CostEngine</c> returns today, frozen 1:1 with
/// its <see cref="QuoteItem"/> (DATA-MODEL §9 <c>quote_item_cost_snapshot</c>) — never collapsed
/// to the single <c>UnitTotalCost</c> scalar. The per-material and per-additional-cost lines live
/// in the sibling collections below, each owned directly by the same <see cref="QuoteItem"/> so
/// the ownership chain stays flat and unambiguous.
/// </summary>
public sealed class QuoteItemCostSnapshot : Entity, IOwnedBy<QuoteItem>
{
    private QuoteItemCostSnapshot() { }

    internal QuoteItemCostSnapshot(Guid quoteItemId, QuoteItemCostSnapshotInput input)
    {
        QuoteItemId = quoteItemId;
        EngineVersion = input.EngineVersion;
        MaterialCostBeforeWastage = input.MaterialCostBeforeWastage;
        MaterialWastageCost = input.MaterialWastageCost;
        MaterialsTotalCost = input.MaterialsTotalCost;
        LaborMinutes = input.LaborMinutes;
        LaborHourlyRate = input.LaborHourlyRate;
        LaborRateSource = input.LaborRateSource;
        LaborCost = input.LaborCost;
        MachineMinutes = input.MachineMinutes;
        MachineHourlyRate = input.MachineHourlyRate;
        MachineCost = input.MachineCost;
        AdditionalDirectCostsTotal = input.AdditionalDirectCostsTotal;
        TotalEstimatedCost = input.TotalEstimatedCost;
        OutputQuantity = input.OutputQuantity;
        EstimatedUnitCost = input.EstimatedUnitCost;
    }

    public Guid QuoteItemId { get; private set; }
    public Guid ParentId => QuoteItemId;

    public string EngineVersion { get; private set; } = string.Empty;
    public decimal MaterialCostBeforeWastage { get; private set; }
    public decimal MaterialWastageCost { get; private set; }
    public decimal MaterialsTotalCost { get; private set; }
    public decimal? LaborMinutes { get; private set; }
    public decimal? LaborHourlyRate { get; private set; }
    public string? LaborRateSource { get; private set; }
    public decimal LaborCost { get; private set; }
    public decimal? MachineMinutes { get; private set; }
    public decimal? MachineHourlyRate { get; private set; }
    public decimal MachineCost { get; private set; }
    public decimal AdditionalDirectCostsTotal { get; private set; }
    public decimal TotalEstimatedCost { get; private set; }
    public int OutputQuantity { get; private set; }
    public decimal EstimatedUnitCost { get; private set; }
}

/// <summary>One material/supply contribution line, frozen from S4's <c>MaterialCostBreakdown</c>
/// (B-02) — "which materials contributed to this quoted cost, at what rate" stays answerable
/// forever, independent of the live Supply/recipe.</summary>
public sealed class QuoteItemMaterialSnapshot : Entity, IOwnedBy<QuoteItem>
{
    private QuoteItemMaterialSnapshot() { }

    internal QuoteItemMaterialSnapshot(Guid quoteItemId, int lineNumber, QuoteItemMaterialSnapshotInput input)
    {
        QuoteItemId = quoteItemId;
        LineNumber = lineNumber;
        SupplyId = input.SupplyId;
        SupplyCodeSnapshot = input.SupplyCode;
        SupplyNameSnapshot = input.SupplyName;
        EnteredQuantity = input.EnteredQuantity;
        EnteredUnit = input.EnteredUnit;
        NormalizedQuantityBaseUnit = input.NormalizedQuantityBaseUnit;
        BaseUnit = input.BaseUnit;
        WastagePercent = input.WastagePercent;
        EffectiveQuantityBaseUnit = input.EffectiveQuantityBaseUnit;
        CostSource = input.CostSource;
        CostPolicy = input.CostPolicy;
        UnitCostBaseUnit = input.UnitCostBaseUnit;
        CostBeforeWastage = input.CostBeforeWastage;
        WastageCost = input.WastageCost;
        CostAfterWastage = input.CostAfterWastage;
        CurrentStockBaseUnitAtIssue = input.CurrentStockBaseUnit;
        ExceededCurrentStockAtIssue = input.ExceedsCurrentStock;
    }

    public Guid QuoteItemId { get; private set; }
    public Guid ParentId => QuoteItemId;
    public int LineNumber { get; private set; }

    /// <summary>ID reference only — never a cross-module navigation property (CLAUDE.md rule 11).</summary>
    public Guid SupplyId { get; private set; }
    public string SupplyCodeSnapshot { get; private set; } = string.Empty;
    public string SupplyNameSnapshot { get; private set; } = string.Empty;
    public decimal EnteredQuantity { get; private set; }
    public string EnteredUnit { get; private set; } = string.Empty;
    public decimal NormalizedQuantityBaseUnit { get; private set; }
    public string BaseUnit { get; private set; } = string.Empty;
    public decimal WastagePercent { get; private set; }
    public decimal EffectiveQuantityBaseUnit { get; private set; }
    public string CostSource { get; private set; } = string.Empty;
    public string CostPolicy { get; private set; } = string.Empty;
    public decimal UnitCostBaseUnit { get; private set; }
    public decimal CostBeforeWastage { get; private set; }
    public decimal WastageCost { get; private set; }
    public decimal CostAfterWastage { get; private set; }
    public decimal CurrentStockBaseUnitAtIssue { get; private set; }
    public bool ExceededCurrentStockAtIssue { get; private set; }
}

/// <summary>One additional direct cost line, frozen from S4's <c>AdditionalDirectCostBreakdown</c> (B-02).</summary>
public sealed class QuoteItemAdditionalCostSnapshot : Entity, IOwnedBy<QuoteItem>
{
    private QuoteItemAdditionalCostSnapshot() { }

    internal QuoteItemAdditionalCostSnapshot(Guid quoteItemId, int lineNumber, QuoteItemAdditionalCostSnapshotInput input)
    {
        QuoteItemId = quoteItemId;
        LineNumber = lineNumber;
        Description = input.Description;
        Amount = input.Amount;
    }

    public Guid QuoteItemId { get; private set; }
    public Guid ParentId => QuoteItemId;
    public int LineNumber { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
}
