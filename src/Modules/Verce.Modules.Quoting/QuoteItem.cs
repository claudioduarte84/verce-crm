using Verce.SharedKernel;
using Verce.SharedKernel.Domain;

namespace Verce.Modules.Quoting;

/// <summary>One material/supply line historically frozen from S4's <c>MaterialCostBreakdown</c>
/// (B-02 correction) — every component the CostEngine returns TODAY, preserved so "which
/// materials contributed to this quoted cost, at what rate" is answerable forever, even after the
/// live Supply's acquisition basis, recipe or stock changes.</summary>
public sealed record QuoteItemMaterialSnapshotInput(
    Guid SupplyId, string SupplyCode, string SupplyName, decimal EnteredQuantity, string EnteredUnit,
    decimal NormalizedQuantityBaseUnit, string BaseUnit, decimal WastagePercent, decimal EffectiveQuantityBaseUnit,
    string CostSource, string CostPolicy, decimal UnitCostBaseUnit, decimal CostBeforeWastage, decimal WastageCost,
    decimal CostAfterWastage, decimal CurrentStockBaseUnit, bool ExceedsCurrentStock);

/// <summary>One additional direct cost line, frozen from S4's <c>AdditionalDirectCostBreakdown</c>.</summary>
public sealed record QuoteItemAdditionalCostSnapshotInput(string Description, decimal Amount);

/// <summary>
/// Every cost component S4's CostEngine returns TODAY (B-02 correction) — never collapsed to a
/// single total. Ad-hoc lines (no Product) populate this with <c>EngineVersion = "MANUAL"</c>,
/// zero materials/labor/machine, and the manual cost as both the total and the unit cost.
/// </summary>
public sealed record QuoteItemCostSnapshotInput(
    string EngineVersion,
    decimal MaterialCostBeforeWastage,
    decimal MaterialWastageCost,
    decimal MaterialsTotalCost,
    decimal? LaborMinutes,
    decimal? LaborHourlyRate,
    string? LaborRateSource,
    decimal LaborCost,
    decimal? MachineMinutes,
    decimal? MachineHourlyRate,
    decimal MachineCost,
    decimal AdditionalDirectCostsTotal,
    decimal TotalEstimatedCost,
    int OutputQuantity,
    decimal EstimatedUnitCost,
    IReadOnlyList<QuoteItemMaterialSnapshotInput> Materials,
    IReadOnlyList<QuoteItemAdditionalCostSnapshotInput> AdditionalCosts);

/// <summary>
/// Fully-resolved input for one quote line, computed by the composition root BEFORE the
/// revision is constructed (mirrors how <c>ProductEndpoints</c> resolves Supply/cost lookups
/// before calling into the Catalog aggregate). Everything price-formula-shaped
/// (<c>SuggestedUnitPrice</c>, <c>CommissionAmountPerUnit</c>, <c>FeeClampApplied</c>) is the
/// direct output of <c>Verce.Modules.Pricing.PricingEngine.Calculate</c> — Quoting never
/// reimplements that formula (CR-07.1/07.2/07.4/07.6 stay Pricing's). <see cref="AllocatedOrderFee"/>
/// is the direct output of <see cref="PerOrderFeeAllocator"/> for a <c>PerOrder</c> line.
/// Only CR-08.1-08.4 (override/discount/line totals/profit) are computed here, inside
/// <see cref="QuoteItem"/> itself — this is the genuinely new S6 math.
/// <see cref="SourceQuoteItemId"/> (B-01 correction) identifies the R(n) line this candidate was
/// cloned from — null for a genuinely new line. The composition root, never this record's
/// consumer, is responsible for having copied every source field verbatim before constructing
/// this snapshot for a carried-forward line.
/// </summary>
public sealed record QuoteItemSnapshot(
    Guid? SourceQuoteItemId,
    Guid? ProductId,
    Guid? ProductRecipeId,
    string ProductNameSnapshot,
    string? Description,
    decimal Quantity,
    QuoteItemCostSnapshotInput CostSnapshot,
    decimal DesiredMarginPercent,
    Guid SalesChannelId,
    Guid? FeeRuleVersionId,
    decimal CommissionPercent,
    QuoteFixedFeeApplication FixedFeeApplication,
    decimal RawFixedFee,
    decimal AllocatedOrderFee,
    string RoundingPolicyApplied,
    decimal SuggestedUnitPrice,
    decimal CommissionAmountPerUnit,
    string? FeeClampApplied,
    decimal? ManualPriceOverride,
    QuoteDiscountKind DiscountKind,
    decimal DiscountValue);

/// <summary>
/// One quote line (DOMAIN-MODEL §8 <c>QuoteItem</c>). Owned by <see cref="QuoteRevision"/>;
/// immutable once its parent revision is persisted (ADR-0003, ADR-0020 §A.7) — an "edit" is
/// always a new line on a new revision, never a mutation here. Its full cost breakdown lives in
/// the owned <see cref="QuoteItemCostSnapshot"/> (B-02 correction) — never collapsed to a single
/// <c>UnitTotalCost</c> scalar, though that scalar is still kept directly on this row for CR-08.3's
/// own arithmetic (it equals <c>CostSnapshot.EstimatedUnitCost</c> by construction).
/// </summary>
public sealed class QuoteItem : Entity, IOwnedBy<QuoteRevision>
{
    private QuoteItemCostSnapshot _costSnapshot = null!;
    private readonly List<QuoteItemMaterialSnapshot> _materials = [];
    private readonly List<QuoteItemAdditionalCostSnapshot> _additionalCosts = [];

    private QuoteItem() { }

    public QuoteItem(Guid quoteRevisionId, int lineNumber, QuoteItemSnapshot snapshot)
    {
        QuoteRevisionId = quoteRevisionId;
        LineNumber = lineNumber;

        if (snapshot.Quantity <= 0) throw new ArgumentException("QUOTE_ITEM_QUANTITY_INVALID");
        if (snapshot.CostSnapshot.EstimatedUnitCost < 0) throw new ArgumentException("QUOTE_ITEM_COST_INVALID");
        if (string.IsNullOrWhiteSpace(snapshot.ProductNameSnapshot)) throw new ArgumentException("QUOTE_ITEM_NAME_REQUIRED");
        if (snapshot.ManualPriceOverride is < 0) throw new ArgumentException("QUOTE_ITEM_PRICE_OVERRIDE_INVALID");

        SourceQuoteItemId = snapshot.SourceQuoteItemId;
        ProductId = snapshot.ProductId;
        ProductRecipeId = snapshot.ProductRecipeId;
        ProductNameSnapshot = snapshot.ProductNameSnapshot.Trim();
        Description = string.IsNullOrWhiteSpace(snapshot.Description) ? null : snapshot.Description.Trim();
        Quantity = Rounding.ToQuantity(snapshot.Quantity);
        UnitTotalCost = Rounding.ToInternal(snapshot.CostSnapshot.EstimatedUnitCost);
        CostEngineVersion = snapshot.CostSnapshot.EngineVersion;
        DesiredMarginPercent = snapshot.DesiredMarginPercent;
        SalesChannelId = snapshot.SalesChannelId;
        FeeRuleVersionId = snapshot.FeeRuleVersionId;
        CommissionPercent = snapshot.CommissionPercent;
        FixedFeeApplication = snapshot.FixedFeeApplication;
        RawFixedFee = Rounding.ToInternal(snapshot.RawFixedFee);
        AllocatedOrderFee = Rounding.ToMoney(snapshot.AllocatedOrderFee);
        RoundingPolicyApplied = snapshot.RoundingPolicyApplied;
        SuggestedUnitPrice = snapshot.SuggestedUnitPrice;
        CommissionAmountPerUnit = snapshot.CommissionAmountPerUnit;
        FeeClampApplied = snapshot.FeeClampApplied;
        ManualPriceOverride = snapshot.ManualPriceOverride;
        DiscountKind = snapshot.DiscountKind;
        DiscountValue = snapshot.DiscountValue;

        _costSnapshot = new QuoteItemCostSnapshot(Id, snapshot.CostSnapshot);
        for (var i = 0; i < snapshot.CostSnapshot.Materials.Count; i++)
            _materials.Add(new QuoteItemMaterialSnapshot(Id, i + 1, snapshot.CostSnapshot.Materials[i]));
        for (var i = 0; i < snapshot.CostSnapshot.AdditionalCosts.Count; i++)
            _additionalCosts.Add(new QuoteItemAdditionalCostSnapshot(Id, i + 1, snapshot.CostSnapshot.AdditionalCosts[i]));

        // CR-07.3: fixedFeePerUnit — PER_UNIT charges the raw fee directly; PER_ORDER derives it
        // from THIS line's allocated share (never the whole order fee — that was the
        // pre-ADR-0020 double-count CR-07.3/CR-08.3 corrected).
        FixedFeePerUnit = FixedFeeApplication == QuoteFixedFeeApplication.PerUnit
            ? RawFixedFee
            : Rounding.ToInternal(AllocatedOrderFee / Quantity);

        // CR-08.1: an override always wins and is always recorded.
        UnitPrice = ManualPriceOverride ?? SuggestedUnitPrice;
        PriceOverridden = ManualPriceOverride is not null;

        // CR-08.2.
        DiscountAmount = DiscountKind switch
        {
            QuoteDiscountKind.None => 0m,
            QuoteDiscountKind.Percent => Rounding.ToMoney(UnitPrice * DiscountValue),
            QuoteDiscountKind.Amount => Rounding.ToMoney(DiscountValue),
            _ => throw new ArgumentException("QUOTE_ITEM_DISCOUNT_KIND_INVALID"),
        };
        var netUnitPrice = UnitPrice - DiscountAmount;
        if (netUnitPrice <= 0) throw new ArgumentException("DISCOUNT_EXCEEDS_PRICE");
        NetUnitPrice = netUnitPrice;

        // CR-08.3.
        LineTotalAmount = Rounding.ToMoney(NetUnitPrice * Quantity);
        LineCostAmount = Rounding.ToMoney(UnitTotalCost * Quantity);
        var perUnitFeePortion = Rounding.ToMoney(CommissionAmountPerUnit * Quantity);
        LineFeeAmount = FixedFeeApplication == QuoteFixedFeeApplication.PerUnit
            ? perUnitFeePortion + Rounding.ToMoney(FixedFeePerUnit * Quantity)
            : perUnitFeePortion + AllocatedOrderFee;

        // CR-08.4.
        ExpectedProfitAmount = Rounding.ToMoney(LineTotalAmount - LineFeeAmount - LineCostAmount);
        EffectiveMarginPercent = LineTotalAmount > 0 ? Rounding.ToPercent(ExpectedProfitAmount / LineTotalAmount) : 0m;
    }

    public Guid QuoteRevisionId { get; private set; }
    public Guid ParentId => QuoteRevisionId;
    public int LineNumber { get; private set; }

    /// <summary>B-01: the R(n) line this one was cloned from, if any — null for a genuinely new
    /// line. Provenance only; never re-interpreted as identity across quotes.</summary>
    public Guid? SourceQuoteItemId { get; private set; }

    public Guid? ProductId { get; private set; }
    public Guid? ProductRecipeId { get; private set; }
    public string ProductNameSnapshot { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public decimal Quantity { get; private set; }

    public decimal UnitTotalCost { get; private set; }
    public string CostEngineVersion { get; private set; } = string.Empty;
    public decimal DesiredMarginPercent { get; private set; }

    public Guid SalesChannelId { get; private set; }
    public Guid? FeeRuleVersionId { get; private set; }
    public decimal CommissionPercent { get; private set; }
    public QuoteFixedFeeApplication FixedFeeApplication { get; private set; }
    public decimal RawFixedFee { get; private set; }
    /// <summary>This line's share of the order-level fee (ADR-0020 §C, CR-07.7). Always 0 for a
    /// <c>PerUnit</c> line — allocation only exists for <c>PerOrder</c>.</summary>
    public decimal AllocatedOrderFee { get; private set; }
    public decimal FixedFeePerUnit { get; private set; }
    public string RoundingPolicyApplied { get; private set; } = string.Empty;

    public decimal SuggestedUnitPrice { get; private set; }
    public decimal CommissionAmountPerUnit { get; private set; }
    public string? FeeClampApplied { get; private set; }
    public decimal? ManualPriceOverride { get; private set; }
    public bool PriceOverridden { get; private set; }
    public decimal UnitPrice { get; private set; }

    public QuoteDiscountKind DiscountKind { get; private set; }
    public decimal DiscountValue { get; private set; }
    public decimal DiscountAmount { get; private set; }
    public decimal NetUnitPrice { get; private set; }

    public decimal LineTotalAmount { get; private set; }
    public decimal LineCostAmount { get; private set; }
    public decimal LineFeeAmount { get; private set; }
    public decimal ExpectedProfitAmount { get; private set; }
    public decimal EffectiveMarginPercent { get; private set; }

    /// <summary>B-02: the full historically-frozen cost breakdown (materials, labor, machine,
    /// additional direct costs) — never just the scalar <see cref="UnitTotalCost"/>.</summary>
    public QuoteItemCostSnapshot CostSnapshot => _costSnapshot;
    public IReadOnlyList<QuoteItemMaterialSnapshot> Materials => _materials.AsReadOnly();
    public IReadOnlyList<QuoteItemAdditionalCostSnapshot> AdditionalCosts => _additionalCosts.AsReadOnly();
}
