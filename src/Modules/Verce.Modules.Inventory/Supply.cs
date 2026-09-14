using System.Text.Json.Serialization;
using Verce.Platform.Audit;
using Verce.SharedKernel.Domain;

namespace Verce.Modules.Inventory;

/// <summary>Governed base-unit catalogue (S3 mission §8) — every Supply's stock is normalized
/// and persisted in exactly one of these. Conversions are deliberately limited to the compatible
/// pairs the domain actually needs (kg↔g, L↔mL, m↔cm); no general unit-conversion framework.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SupplyBaseUnit>))]
public enum SupplyBaseUnit { Gram, Kilogram, Unit, Milliliter, Liter, Meter, Centimeter }

/// <summary>Deterministic conversion between a Supply's base unit and the handful of compatible
/// units an operator might naturally enter a quantity in (S3 mission §9) — e.g. buying "1 kg" of
/// a filament whose ledger is denominated in grams. Deliberately NOT a general unit-conversion
/// framework: only the three compatible pairs the domain actually needs, both directions, plus
/// same-unit passthrough; anything else (e.g. Gram→Liter, or any conversion involving Unit) is
/// rejected rather than guessed.</summary>
public static class SupplyUnitConversion
{
    /// <summary>Entered quantities are immutable business facts. Eight decimal places preserve
    /// ordinary supplier/document quantities without turning inventory into a measurement system.
    /// The authoritative stock projection remains <see cref="Rounding.QuantityScale"/> (four).
    /// </summary>
    public const int EnteredQuantityScale = 8;

    private static readonly Dictionary<(SupplyBaseUnit From, SupplyBaseUnit To), decimal> Factors = new()
    {
        [(SupplyBaseUnit.Kilogram, SupplyBaseUnit.Gram)] = 1000m,
        [(SupplyBaseUnit.Gram, SupplyBaseUnit.Kilogram)] = 0.001m,
        [(SupplyBaseUnit.Liter, SupplyBaseUnit.Milliliter)] = 1000m,
        [(SupplyBaseUnit.Milliliter, SupplyBaseUnit.Liter)] = 0.001m,
        [(SupplyBaseUnit.Meter, SupplyBaseUnit.Centimeter)] = 100m,
        [(SupplyBaseUnit.Centimeter, SupplyBaseUnit.Meter)] = 0.01m,
    };

    /// <summary>True if a quantity entered in <paramref name="enteredUnit"/> can be converted to
    /// <paramref name="baseUnit"/> — same unit, or one of the governed compatible pairs.</summary>
    public static bool IsCompatible(SupplyBaseUnit enteredUnit, SupplyBaseUnit baseUnit) =>
        enteredUnit == baseUnit || Factors.ContainsKey((enteredUnit, baseUnit));

    /// <summary>Canonicalizes a positive entered quantity, converts it to the Supply's base unit
    /// and applies the persisted base precision. A positive input which becomes zero at that
    /// precision is rejected before it can become a zero-delta movement or cost denominator.</summary>
    public static InventoryQuantity NormalizePositive(decimal quantity, SupplyBaseUnit enteredUnit, SupplyBaseUnit baseUnit)
    {
        if (quantity <= 0) throw new ArgumentException("QUANTITY_MUST_BE_POSITIVE");
        if (!Enum.IsDefined(enteredUnit) || !Enum.IsDefined(baseUnit)) throw new ArgumentException("UNIT_CONVERSION_NOT_SUPPORTED");

        var enteredQuantity = Verce.SharedKernel.Rounding.ToScale(quantity, EnteredQuantityScale);
        if (enteredQuantity <= 0) throw new ArgumentException("QUANTITY_BELOW_ENTERED_PRECISION");

        decimal quantityBaseUnit;
        if (enteredUnit == baseUnit) quantityBaseUnit = Verce.SharedKernel.Rounding.ToQuantity(enteredQuantity);
        else if (Factors.TryGetValue((enteredUnit, baseUnit), out var factor)) quantityBaseUnit = Verce.SharedKernel.Rounding.ToQuantity(enteredQuantity * factor);
        else throw new ArgumentException("UNIT_CONVERSION_NOT_SUPPORTED");

        if (quantityBaseUnit <= 0) throw new ArgumentException("QUANTITY_BELOW_BASE_PRECISION");
        return new InventoryQuantity(enteredQuantity, quantityBaseUnit);
    }

    /// <summary>Compatibility helper retaining the established conversion API. Commands must use
    /// this normalized result rather than accepting an unrepresentable positive input.</summary>
    public static decimal ToBaseUnit(decimal quantity, SupplyBaseUnit enteredUnit, SupplyBaseUnit baseUnit)
        => NormalizePositive(quantity, enteredUnit, baseUnit).QuantityBaseUnit;
}

public readonly record struct InventoryQuantity(decimal EnteredQuantity, decimal QuantityBaseUnit);

/// <summary>Every stock-changing fact, ever. Append-only — there is no Update/Delete anywhere
/// on this type (S3 mission §14). "Current stock" is a running balance maintained on
/// <see cref="Supply"/> in the SAME Unit of Work as each movement, which is what lets the
/// aggregate's ordinary optimistic-concurrency <see cref="AggregateRoot.Version"/> token also be
/// the non-negative-stock concurrency guard — see ADR-0017.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InventoryMovementType>))]
public enum InventoryMovementType
{
    PurchaseReceipt,
    ManualIncrease,
    ManualDecrease,
    Consumption,
    ReturnIn,
    ReturnOut,
    InitialBalance,
    Correction,
}

/// <summary>Thrown when a decreasing movement would take a Supply's stock below zero (S3 mission
/// §16). Mapped to HTTP 409 by the API layer — it is a state conflict, not a malformed request.</summary>
public sealed class InsufficientStockException(decimal requested, decimal available)
    : Exception("INSUFFICIENT_STOCK")
{
    public decimal Requested { get; } = requested;
    public decimal Available { get; } = available;
}

/// <summary>Structured metadata for filament supplies (S3 mission §10). An owned value object,
/// not a separate aggregate: a black PLA and a bicolor PLA remain economically distinct Supply
/// rows with their own code, cost and stock (mission §11) — this block only carries the physical
/// attributes recipes will eventually select by. Present only when <see cref="Supply.Category"/>
/// is a filament-shaped category; enforced by <see cref="Supply.ApplyFilamentDetails"/>, not by
/// a database constraint (category is operator-editable master data, not a closed enum).</summary>
public sealed record FilamentDetails(FilamentMaterialType MaterialType, string Brand, string ColorName, string? ColorCode, decimal DiameterMm, decimal SpoolNetWeightGrams);

[JsonConverter(typeof(JsonStringEnumConverter<FilamentMaterialType>))]
public enum FilamentMaterialType { Pla, PlaPlus, Petg, Abs, Asa, Tpu, Nylon, Pc, Pva, Other }

/// <summary>Category 3 reference data (ADR-0011 §1): a small, curated classification vocabulary
/// for Supply, not a calculation input (S3 mission §7) — behavior never branches on
/// <see cref="Code"/>, only display/filtering does.</summary>
public sealed class SupplyCategory : IReferenceData
{
    private SupplyCategory() { }
    public SupplyCategory(string code, string name)
    {
        Code = Supply.Required(code, 1, 40, nameof(code)).ToUpperInvariant();
        Name = Supply.Required(name, 1, 100, nameof(name));
    }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public bool IsActive { get; private set; } = true;
    public void Deactivate() => IsActive = false;
    public void Activate() => IsActive = true;
}

/// <summary>The authoritative registry entry for a material/production input (S3 mission §5).
/// <see cref="Code"/> and <see cref="BaseUnit"/> are immutable once set (§6/§8): the code is how
/// future modules (recipes, purchase records) refer to this row, and the base unit is the frame
/// every historical movement quantity is already denominated in — changing either after the fact
/// would silently reinterpret history. Stock is never a bare mutable number (§12): it only ever
/// changes through one of the <c>Record*</c> methods below, each of which appends an immutable
/// <see cref="InventoryMovement"/> in the SAME operation.</summary>
[Auditable]
public sealed class Supply : AggregateRoot
{
    private readonly List<InventoryMovement> _movements = [];
    private Supply() { }

    public Supply(string code, string name, string? description, string categoryCode, SupplyBaseUnit baseUnit,
        decimal? minimumStock, string? preferredSupplier, string? notes, FilamentDetails? filamentDetails)
    {
        Code = NormalizeCode(code);
        CategoryCode = Required(categoryCode, 1, 40, nameof(categoryCode)).ToUpperInvariant();
        BaseUnit = Enum.IsDefined(baseUnit) ? baseUnit : throw new ArgumentException("BASE_UNIT_INVALID");
        ApplyDetails(name, description, minimumStock, preferredSupplier, notes, filamentDetails);
    }

    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string CategoryCode { get; private set; } = string.Empty;
    public SupplyBaseUnit BaseUnit { get; private set; }
    public decimal? MinimumStock { get; private set; }
    public string? PreferredSupplier { get; private set; }
    public string? Notes { get; private set; }
    public bool Active { get; private set; } = true;

    // ---- Filament details (S3 mission §10): stored as individual nullable scalar columns,
    // NOT an EF owned type. An `OwnsOne` value object is itself registered as an EF "entity
    // type" and would need its own PK-category marker (ADR-0011 §1) for a concept that has no
    // identity at all — it is display/selection metadata on ONE Supply row. The public
    // `FilamentDetails` property below composes them back into the value object callers use; EF
    // maps only the scalar fields (`b.Ignore(x => x.FilamentDetails)` in InventoryConfiguration).
    private FilamentMaterialType? _filamentMaterialType;
    private string? _filamentBrand;
    private string? _filamentColorName;
    private string? _filamentColorCode;
    private decimal? _filamentDiameterMm;
    private decimal? _filamentSpoolNetWeightGrams;

    public FilamentDetails? FilamentDetails =>
        _filamentMaterialType is null ? null
        : new FilamentDetails(_filamentMaterialType.Value, _filamentBrand!, _filamentColorName!, _filamentColorCode, _filamentDiameterMm!.Value, _filamentSpoolNetWeightGrams!.Value);

    /// <summary>The maintained running balance — SUM of every movement's signed quantity, kept
    /// consistent by construction (every <c>Record*</c> method updates this and appends the
    /// movement in the same call, inside the same Unit of Work). Never assigned directly by
    /// application/API code (S3 mission §12).</summary>
    public decimal CurrentStockBaseUnit { get; private set; }

    /// <summary>Informational latest purchase cost — NOT a costing policy. S4 owns how a
    /// component's cost is actually derived for a recipe (S3 mission §21/§22).</summary>
    public decimal? LatestPurchaseUnitCost { get; private set; }

    /// <summary>Whether any movement has ever been posted — cheaper than loading the full
    /// movement history just to answer "is initial balance still allowed?" (S3 mission §39).</summary>
    public bool HasRecordedMovement { get; private set; }

    public IReadOnlyCollection<InventoryMovement> InventoryMovements => _movements.AsReadOnly();

    public bool IsLowStock => MinimumStock is { } minimum && CurrentStockBaseUnit <= minimum;

    public void Update(string name, string? description, string categoryCode, decimal? minimumStock, string? preferredSupplier, string? notes, FilamentDetails? filamentDetails)
    {
        CategoryCode = Required(categoryCode, 1, 40, nameof(categoryCode)).ToUpperInvariant();
        ApplyDetails(name, description, minimumStock, preferredSupplier, notes, filamentDetails);
    }

    public void Deactivate() => Active = false;
    public void Activate() => Active = true;

    /// <summary>One-time only (S3 mission §18/§39): once ANY movement exists, the ledger — not a
    /// fresh assignment — is the only legitimate way to change stock. <paramref name="enteredUnit"/>
    /// need not match <see cref="BaseUnit"/> as long as they are dimensionally compatible.</summary>
    public InventoryMovement RecordInitialBalance(decimal quantity, SupplyBaseUnit enteredUnit, DateTimeOffset occurredAt, string? reference, string? notes)
    {
        if (HasRecordedMovement) throw new InvalidOperationException("INITIAL_BALANCE_ALREADY_RECORDED");
        var normalized = SupplyUnitConversion.NormalizePositive(quantity, enteredUnit, BaseUnit);
        var movement = new InventoryMovement(Id, InventoryMovementType.InitialBalance, normalized.EnteredQuantity, enteredUnit, normalized.QuantityBaseUnit, occurredAt, notes, reference, null, null, null);
        return Post(movement, normalized.QuantityBaseUnit);
    }

    public InventoryMovement RecordPurchaseReceipt(decimal quantity, SupplyBaseUnit enteredUnit, DateTimeOffset occurredAt, decimal? unitCost, decimal? totalCost, string? supplier, string? reference, string? notes)
    {
        var normalized = SupplyUnitConversion.NormalizePositive(quantity, enteredUnit, BaseUnit);
        // unitCost is understood as cost PER ENTERED UNIT (what the operator actually typed,
        // e.g. R$/kg for a spool priced by the kilogram) and converted to cost-per-base-unit via
        // the SAME factor as the quantity — totalCost is dimension-free and needs no conversion.
        if (unitCost is < 0 || totalCost is < 0) throw new ArgumentException("COST_MUST_BE_NON_NEGATIVE");
        var conversionFactor = enteredUnit == BaseUnit ? 1m : NormalizePositiveUnitFactor(enteredUnit, BaseUnit);
        var totalFromUnitCost = unitCost.HasValue ? Verce.SharedKernel.Rounding.ToMoney(unitCost.Value * normalized.EnteredQuantity) : (decimal?)null;
        decimal? canonicalTotalCost = totalCost.HasValue ? Verce.SharedKernel.Rounding.ToMoney(totalCost.Value) : null;
        if (totalFromUnitCost.HasValue && canonicalTotalCost.HasValue && totalFromUnitCost.Value != canonicalTotalCost.Value)
            throw new ArgumentException("PURCHASE_COST_MISMATCH");

        var resolvedTotalCost = canonicalTotalCost ?? totalFromUnitCost;
        var resolvedUnitCost = unitCost.HasValue
            ? Verce.SharedKernel.Rounding.ToInternal(unitCost.Value / conversionFactor)
            : (resolvedTotalCost.HasValue ? Verce.SharedKernel.Rounding.ToInternal(resolvedTotalCost.Value / normalized.QuantityBaseUnit) : (decimal?)null);
        var movement = new InventoryMovement(Id, InventoryMovementType.PurchaseReceipt, normalized.EnteredQuantity, enteredUnit, normalized.QuantityBaseUnit, occurredAt, notes, reference, supplier, resolvedUnitCost, resolvedTotalCost);
        var posted = Post(movement, normalized.QuantityBaseUnit);
        if (resolvedUnitCost.HasValue) LatestPurchaseUnitCost = resolvedUnitCost;
        return posted;
    }

    public InventoryMovement RecordManualIncrease(decimal quantity, SupplyBaseUnit enteredUnit, string reason, DateTimeOffset occurredAt)
    {
        var normalized = SupplyUnitConversion.NormalizePositive(quantity, enteredUnit, BaseUnit);
        var movement = new InventoryMovement(Id, InventoryMovementType.ManualIncrease, normalized.EnteredQuantity, enteredUnit, normalized.QuantityBaseUnit, occurredAt, RequireReason(reason), null, null, null, null);
        return Post(movement, normalized.QuantityBaseUnit);
    }

    public InventoryMovement RecordManualDecrease(decimal quantity, SupplyBaseUnit enteredUnit, string reason, DateTimeOffset occurredAt)
    {
        var normalized = SupplyUnitConversion.NormalizePositive(quantity, enteredUnit, BaseUnit);
        if (CurrentStockBaseUnit - normalized.QuantityBaseUnit < 0) throw new InsufficientStockException(normalized.QuantityBaseUnit, CurrentStockBaseUnit);
        var movement = new InventoryMovement(Id, InventoryMovementType.ManualDecrease, normalized.EnteredQuantity, enteredUnit, -normalized.QuantityBaseUnit, occurredAt, RequireReason(reason), null, null, null, null);
        return Post(movement, -normalized.QuantityBaseUnit);
    }

    /// <summary>Reconciles the ledger to a freshly counted physical quantity (S3 mission §13/§23
    /// — "physical count correction" is the canonical example reason). The caller supplies the
    /// counted absolute quantity, not a delta — this method derives the signed movement and
    /// rejects a "correction" that would not actually change anything.</summary>
    public InventoryMovement RecordCorrection(decimal countedQuantity, SupplyBaseUnit enteredUnit, string reason, DateTimeOffset occurredAt)
    {
        var normalized = SupplyUnitConversion.NormalizePositive(countedQuantity, enteredUnit, BaseUnit);
        var delta = normalized.QuantityBaseUnit - CurrentStockBaseUnit;
        if (delta == 0) throw new ArgumentException("CORRECTION_QUANTITY_UNCHANGED");
        var movement = new InventoryMovement(Id, InventoryMovementType.Correction, normalized.EnteredQuantity, enteredUnit, delta, occurredAt, RequireReason(reason), null, null, null, null);
        return Post(movement, delta);
    }

    private static decimal NormalizePositiveUnitFactor(SupplyBaseUnit enteredUnit, SupplyBaseUnit baseUnit) =>
        SupplyUnitConversion.NormalizePositive(1m, enteredUnit, baseUnit).QuantityBaseUnit;

    private InventoryMovement Post(InventoryMovement movement, decimal signedDelta)
    {
        _movements.Add(movement);
        CurrentStockBaseUnit += signedDelta;
        HasRecordedMovement = true;
        return movement;
    }

    private void ApplyDetails(string name, string? description, decimal? minimumStock, string? preferredSupplier, string? notes, FilamentDetails? filamentDetails)
    {
        Name = Required(name, 2, 200, nameof(name));
        Description = Optional(description, 2000);
        if (minimumStock is < 0) throw new ArgumentException("MINIMUM_STOCK_MUST_BE_NON_NEGATIVE");
        MinimumStock = minimumStock;
        PreferredSupplier = Optional(preferredSupplier, 200);
        Notes = Optional(notes, 2000);
        ApplyFilamentDetails(filamentDetails);
    }

    private void ApplyFilamentDetails(FilamentDetails? details)
    {
        if (details is null)
        {
            _filamentMaterialType = null; _filamentBrand = null; _filamentColorName = null;
            _filamentColorCode = null; _filamentDiameterMm = null; _filamentSpoolNetWeightGrams = null;
            return;
        }
        if (!Enum.IsDefined(details.MaterialType)) throw new ArgumentException("FILAMENT_MATERIAL_TYPE_INVALID");
        if (details.DiameterMm <= 0) throw new ArgumentException("FILAMENT_DIAMETER_MUST_BE_POSITIVE");
        if (details.SpoolNetWeightGrams <= 0) throw new ArgumentException("FILAMENT_SPOOL_WEIGHT_MUST_BE_POSITIVE");
        _filamentMaterialType = details.MaterialType;
        _filamentBrand = Required(details.Brand, 1, 100, nameof(details.Brand));
        _filamentColorName = Required(details.ColorName, 1, 100, nameof(details.ColorName));
        _filamentColorCode = Optional(details.ColorCode, 20);
        _filamentDiameterMm = details.DiameterMm;
        _filamentSpoolNetWeightGrams = details.SpoolNetWeightGrams;
    }

    private static string RequireReason(string reason) => Required(reason, 2, 500, nameof(reason));

    /// <summary>Normalized to uppercase so a plain unique DB index enforces case-insensitive
    /// uniqueness by construction (S3 mission §6) — no functional/expression index needed.</summary>
    internal static string NormalizeCode(string code)
    {
        var trimmed = Required(code, 2, 40, nameof(code)).ToUpperInvariant();
        if (!trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) throw new ArgumentException("CODE_INVALID_CHARACTERS");
        return trimmed;
    }

    internal static string Required(string value, int min, int max, string field)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length < min || trimmed.Length > max) throw new ArgumentException($"{field.ToUpperInvariant()}_INVALID");
        return trimmed;
    }
    internal static string? Optional(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : throw new ArgumentException("FIELD_TOO_LONG");
}

/// <summary>An immutable, historical fact about a Supply's stock (S3 mission §14/§15). No public
/// setter, no Update method, no Delete anywhere in this type — a correction is always a NEW
/// movement (<see cref="InventoryMovementType.Correction"/>), never an edit of an old one.</summary>
[Auditable]
public sealed class InventoryMovement : Entity, IOwnedBy<Supply>
{
    private InventoryMovement() { }
    internal InventoryMovement(Guid supplyId, InventoryMovementType type, decimal enteredQuantity, SupplyBaseUnit enteredUnit, decimal quantityDeltaBaseUnit, DateTimeOffset occurredAt,
        string? reason, string? reference, string? supplier, decimal? unitCostSnapshot, decimal? totalCostSnapshot)
    {
        if (enteredQuantity <= 0) throw new ArgumentException("QUANTITY_MUST_BE_POSITIVE");
        if (!Enum.IsDefined(enteredUnit)) throw new ArgumentException("ENTERED_UNIT_INVALID");
        if (!HasValidDeltaSign(type, quantityDeltaBaseUnit)) throw new ArgumentException("MOVEMENT_DELTA_SIGN_INVALID");
        SupplyId = supplyId;
        Type = type;
        EnteredQuantity = enteredQuantity;
        EnteredUnit = enteredUnit;
        QuantityDeltaBaseUnit = quantityDeltaBaseUnit;
        OccurredAt = occurredAt;
        Reason = Supply.Optional(reason, 1000);
        Reference = Supply.Optional(reference, 200);
        Supplier = Supply.Optional(supplier, 200);
        UnitCostSnapshot = unitCostSnapshot;
        TotalCostSnapshot = totalCostSnapshot;
    }

    public Guid SupplyId { get; private set; }
    public Guid ParentId => SupplyId;
    public InventoryMovementType Type { get; private set; }
    public decimal EnteredQuantity { get; private set; }
    public SupplyBaseUnit EnteredUnit { get; private set; }
    public decimal QuantityDeltaBaseUnit { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public string? Reason { get; private set; }
    public string? Reference { get; private set; }
    public string? Supplier { get; private set; }
    public decimal? UnitCostSnapshot { get; private set; }
    public decimal? TotalCostSnapshot { get; private set; }

    private static bool HasValidDeltaSign(InventoryMovementType type, decimal delta) => type switch
    {
        InventoryMovementType.PurchaseReceipt or InventoryMovementType.ManualIncrease or InventoryMovementType.InitialBalance or InventoryMovementType.ReturnIn => delta > 0,
        InventoryMovementType.ManualDecrease or InventoryMovementType.Consumption or InventoryMovementType.ReturnOut => delta < 0,
        InventoryMovementType.Correction => delta != 0,
        _ => false,
    };
}
