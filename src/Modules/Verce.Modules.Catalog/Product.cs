using Verce.Platform.Audit;
using Verce.SharedKernel.Domain;

namespace Verce.Modules.Catalog;

/// <summary>
/// S5 Product aggregate (ARCHITECTURE §3, DOMAIN-MODEL §4). Reconciled against the generic
/// Supply model S3/S4 actually built (see DOMAIN-MODEL's own "forward-reference note"): recipe
/// material lines reference <c>SupplyId</c> generically — there is no separate
/// <c>ProductFilamentComponent</c>/<c>ProductSupplyComponent</c> split, matching S4's CostEngine,
/// which already treats every material line identically regardless of Supply category.
///
/// <see cref="ProductRecipe"/> is an owned child, not a second aggregate root (mission §87):
/// S5 has no Quote or Production Order to ever mark a recipe "used", so the DOMAIN-MODEL's
/// revision-fork-on-edit invariant has no trigger yet and recipe mutation is simply part of
/// Product's own optimistic-concurrency consistency. <see cref="ProductRecipe.RevisionNumber"/>
/// is kept at 1 as a forward-compatible placeholder for the sprint that introduces that trigger.
/// </summary>
[Auditable]
public sealed class Product : AggregateRoot
{
    private readonly ProductRecipe _recipe;
    private Product() { _recipe = null!; }

    public Product(string code, string name, string? description)
    {
        Code = NormalizeCode(code);
        Name = Required(name, 2, 200, nameof(name));
        Description = Optional(description, 2000);
        Active = true;
        _recipe = new ProductRecipe(Id);
    }

    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public bool Active { get; private set; }
    public ProductRecipe Recipe => _recipe;

    public void UpdateDetails(string name, string? description)
    {
        Name = Required(name, 2, 200, nameof(name));
        Description = Optional(description, 2000);
    }

    public void Activate() => Active = true;
    public void Deactivate() => Active = false;

    /// <summary>Normalized to uppercase so a plain unique DB index enforces case-insensitive
    /// uniqueness by construction, exactly mirroring Supply.NormalizeCode (S3 mission §6).</summary>
    internal static string NormalizeCode(string code)
    {
        var trimmed = Required(code, 2, 40, nameof(code)).ToUpperInvariant();
        if (!trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) throw new ArgumentException("PRODUCT_CODE_INVALID_CHARACTERS");
        return trimmed;
    }

    internal static string Required(string value, int min, int max, string field)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length < min || trimmed.Length > max) throw new ArgumentException($"{field.ToUpperInvariant()}_INVALID");
        return trimmed;
    }

    internal static string? Optional(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : throw new ArgumentException("FIELD_TOO_LONG");
}

/// <summary>
/// Owned child of <see cref="Product"/> (mission §87): mutation goes through Product and shares
/// its single optimistic-concurrency <see cref="AggregateRoot.Version"/>. Process parameters
/// mirror S4's <c>CostCalculationInput</c> exactly — this is the persisted, reusable translation
/// source for the authoritative S4 <c>CostEngine</c> (mission §5/§12), never a second
/// implementation of costing math.
///
/// <see cref="WastagePercentOverride"/> follows ADR-0018's percentage-point convention for the
/// Costing module (5 = 5%) — the SAME convention the S4 Cost Laboratory already uses — not the
/// ADR-0002 fraction convention Pricing uses below. Null means "inherit
/// <c>costing.default_wastage_rate</c>"; explicit zero is a real override (ADR-0018).
/// </summary>
public sealed class ProductRecipe : Entity, IOwnedBy<Product>
{
    private readonly List<ProductRecipeMaterialLine> _materialLines = [];
    private readonly List<ProductRecipeAdditionalCostLine> _additionalCostLines = [];

    private ProductRecipe() { }
    internal ProductRecipe(Guid productId)
    {
        ProductId = productId;
        RevisionNumber = 1;
        OutputQuantity = 1;
    }

    public Guid ProductId { get; private set; }
    public Guid ParentId => ProductId;
    public int RevisionNumber { get; private set; }
    public decimal? WastagePercentOverride { get; private set; }
    public decimal? LaborMinutes { get; private set; }
    public decimal? LaborHourlyRateOverride { get; private set; }
    public decimal? MachineMinutes { get; private set; }
    public decimal? MachineHourlyRate { get; private set; }
    public int OutputQuantity { get; private set; } = 1;
    public string? Notes { get; private set; }
    public IReadOnlyList<ProductRecipeMaterialLine> MaterialLines => _materialLines.AsReadOnly();
    public IReadOnlyList<ProductRecipeAdditionalCostLine> AdditionalCostLines => _additionalCostLines.AsReadOnly();

    /// <summary>Replaces the recipe's process parameters. Material/additional-cost lines are
    /// managed independently (<see cref="ReplaceMaterialLines"/>/<see cref="ReplaceAdditionalCostLines"/>)
    /// so the API layer can validate/normalize Supply units (a cross-module concern — ADR-0001
    /// §3.1 — resolved in the composition root, never inside this pure aggregate) before handing
    /// back fully-formed line data.</summary>
    public void UpdateParameters(decimal? wastagePercentOverride, decimal? laborMinutes, decimal? laborHourlyRateOverride,
        decimal? machineMinutes, decimal? machineHourlyRate, int outputQuantity, string? notes)
    {
        if (wastagePercentOverride is < 0 or > 100) throw new ArgumentException("RECIPE_WASTAGE_PERCENT_INVALID");
        if (laborMinutes is < 0) throw new ArgumentException("RECIPE_LABOR_MINUTES_INVALID");
        if (laborHourlyRateOverride is < 0) throw new ArgumentException("RECIPE_LABOR_RATE_INVALID");
        if (machineMinutes is < 0) throw new ArgumentException("RECIPE_MACHINE_MINUTES_INVALID");
        if (machineHourlyRate is < 0) throw new ArgumentException("RECIPE_MACHINE_RATE_INVALID");
        if (outputQuantity < 1) throw new ArgumentException("RECIPE_OUTPUT_QUANTITY_INVALID");
        WastagePercentOverride = wastagePercentOverride;
        LaborMinutes = laborMinutes;
        LaborHourlyRateOverride = laborHourlyRateOverride;
        MachineMinutes = machineMinutes;
        MachineHourlyRate = machineHourlyRate;
        OutputQuantity = outputQuantity;
        Notes = Product.Optional(notes, 2000);
    }

    /// <summary>Wholesale replacement, matching S4's Cost Lab UX (add/remove lines freely) and
    /// avoiding a fragile line-by-line diff. Duplicate <see cref="ProductRecipeMaterialLine.SupplyId"/>
    /// values are deliberately allowed (mission §89): S4's CostEngine already treats duplicate
    /// Supply lines as independent (e.g. the same filament used with two different wastage
    /// overrides in the same print), so Catalog does not invent a stricter rule the engine does
    /// not need.</summary>
    public void ReplaceMaterialLines(IReadOnlyList<ProductRecipeMaterialLine> lines)
    {
        _materialLines.Clear();
        for (var i = 0; i < lines.Count; i++)
        {
            lines[i].SetSortOrder(i);
            _materialLines.Add(lines[i]);
        }
    }

    public void ReplaceAdditionalCostLines(IReadOnlyList<ProductRecipeAdditionalCostLine> lines)
    {
        _additionalCostLines.Clear();
        for (var i = 0; i < lines.Count; i++)
        {
            lines[i].SetSortOrder(i);
            _additionalCostLines.Add(lines[i]);
        }
    }
}

/// <summary>
/// One generic Supply material line (mission §8: never filament-specific). Entered/normalized
/// quantities are persisted TOGETHER at write time — exactly like S3's <c>InventoryMovement</c>
/// (DOMAIN-MODEL §3.4) — rather than recomputed later, so a historical line remains explainable
/// even if read back before Supply lookups happen. Unit-family validation and normalization
/// happen in the API composition root via S3's closed <c>SupplyUnitConversion</c> (ADR-0001
/// §3.1: a pure domain aggregate cannot reference the Inventory module assembly) — this
/// constructor trusts its caller exactly as S4's <c>CostEngine</c> trusts pre-resolved input.
/// </summary>
public sealed class ProductRecipeMaterialLine : Entity, IOwnedBy<ProductRecipe>
{
    private ProductRecipeMaterialLine() { }

    public ProductRecipeMaterialLine(Guid productRecipeId, Guid supplyId, decimal enteredQuantity, string enteredUnit,
        decimal normalizedQuantityBaseUnit, decimal? wastagePercentOverride, decimal? manualUnitCostOverride)
    {
        ProductRecipeId = productRecipeId;
        if (supplyId == Guid.Empty) throw new ArgumentException("RECIPE_LINE_SUPPLY_REQUIRED");
        if (enteredQuantity <= 0) throw new ArgumentException("RECIPE_LINE_QUANTITY_MUST_BE_POSITIVE");
        if (normalizedQuantityBaseUnit <= 0) throw new ArgumentException("RECIPE_LINE_QUANTITY_BELOW_BASE_PRECISION");
        if (string.IsNullOrWhiteSpace(enteredUnit)) throw new ArgumentException("RECIPE_LINE_UNIT_REQUIRED");
        if (wastagePercentOverride is < 0 or > 100) throw new ArgumentException("RECIPE_WASTAGE_PERCENT_INVALID");
        if (manualUnitCostOverride is < 0) throw new ArgumentException("RECIPE_LINE_MANUAL_COST_INVALID");
        SupplyId = supplyId;
        EnteredQuantity = enteredQuantity;
        EnteredUnit = enteredUnit;
        NormalizedQuantityBaseUnit = normalizedQuantityBaseUnit;
        WastagePercentOverride = wastagePercentOverride;
        ManualUnitCostOverride = manualUnitCostOverride;
    }

    public Guid ProductRecipeId { get; private set; }
    public Guid ParentId => ProductRecipeId;
    public Guid SupplyId { get; private set; }
    public decimal EnteredQuantity { get; private set; }
    public string EnteredUnit { get; private set; } = string.Empty;
    public decimal NormalizedQuantityBaseUnit { get; private set; }
    public decimal? WastagePercentOverride { get; private set; }
    public decimal? ManualUnitCostOverride { get; private set; }
    public int SortOrder { get; private set; }

    internal void SetSortOrder(int value) => SortOrder = value;
}

/// <summary>Direct cost line (S4's <c>AdditionalDirectCostInput</c> equivalent) — outsourced
/// finishing, packaging labor billed as a flat sum, etc.</summary>
public sealed class ProductRecipeAdditionalCostLine : Entity, IOwnedBy<ProductRecipe>
{
    private ProductRecipeAdditionalCostLine() { }

    public ProductRecipeAdditionalCostLine(Guid productRecipeId, string description, decimal amount)
    {
        ProductRecipeId = productRecipeId;
        Description = Product.Required(description, 1, 200, nameof(description));
        if (amount < 0) throw new ArgumentException("RECIPE_ADDITIONAL_COST_AMOUNT_INVALID");
        Amount = amount;
    }

    public Guid ProductRecipeId { get; private set; }
    public Guid ParentId => ProductRecipeId;
    public string Description { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public int SortOrder { get; private set; }

    internal void SetSortOrder(int value) => SortOrder = value;
}
