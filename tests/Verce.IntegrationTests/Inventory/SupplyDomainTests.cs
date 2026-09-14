using FluentAssertions;
using Verce.Modules.Inventory;

namespace Verce.IntegrationTests.Inventory;

public class SupplyDomainTests
{
    private static Supply NewSupply(SupplyBaseUnit unit = SupplyBaseUnit.Gram, decimal? minimumStock = null, FilamentDetails? filament = null) =>
        new("FIL-PLA-PRETO", "PLA Preto", "Filamento PLA preto fosco", "FILAMENT", unit, minimumStock, "Fornecedor X", "Notas", filament);

    [Fact]
    public void Code_is_normalized_to_uppercase_and_trimmed()
    {
        var supply = new Supply("  fil-pla-preto  ", "PLA Preto", null, "FILAMENT", SupplyBaseUnit.Gram, null, null, null, null);
        supply.Code.Should().Be("FIL-PLA-PRETO");
    }

    [Theory]
    [InlineData("F")] // too short
    [InlineData("FIL PRETO")] // space not allowed
    [InlineData("FIL@PRETO")] // symbol not allowed
    public void Invalid_codes_are_rejected(string code)
    {
        Action action = () => new Supply(code, "PLA Preto", null, "FILAMENT", SupplyBaseUnit.Gram, null, null, null, null);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Name_must_be_within_bounds()
    {
        Action tooShort = () => new Supply("FIL-X", "A", null, "FILAMENT", SupplyBaseUnit.Gram, null, null, null, null);
        tooShort.Should().Throw<ArgumentException>().WithMessage("NAME_INVALID");
    }

    [Fact]
    public void Negative_minimum_stock_is_rejected()
    {
        Action action = () => NewSupply(minimumStock: -1);
        action.Should().Throw<ArgumentException>().WithMessage("MINIMUM_STOCK_MUST_BE_NON_NEGATIVE");
    }

    [Fact]
    public void New_supply_starts_with_zero_stock_and_no_recorded_movement()
    {
        var supply = NewSupply();
        supply.CurrentStockBaseUnit.Should().Be(0);
        supply.HasRecordedMovement.Should().BeFalse();
        supply.IsLowStock.Should().BeFalse();
    }

    [Fact]
    public void Filament_details_require_valid_fields()
    {
        Action badDiameter = () => NewSupply(filament: new FilamentDetails(FilamentMaterialType.Pla, "Voolt3D", "Preto", null, 0, 1000));
        badDiameter.Should().Throw<ArgumentException>().WithMessage("FILAMENT_DIAMETER_MUST_BE_POSITIVE");

        var supply = NewSupply(filament: new FilamentDetails(FilamentMaterialType.Pla, "Voolt3D", "Preto", "#000000", 1.75m, 1000));
        supply.FilamentDetails.Should().NotBeNull();
        supply.FilamentDetails!.Brand.Should().Be("Voolt3D");
    }

    [Fact]
    public void Update_can_clear_filament_details()
    {
        var supply = NewSupply(filament: new FilamentDetails(FilamentMaterialType.Pla, "Voolt3D", "Preto", null, 1.75m, 1000));
        supply.Update("PLA Preto", null, "FILAMENT", null, null, null, null);
        supply.FilamentDetails.Should().BeNull();
    }

    // ---- Initial balance (mission §18/§39) ----

    [Fact]
    public void Initial_balance_sets_stock_and_can_only_be_recorded_once()
    {
        var supply = NewSupply();
        supply.RecordInitialBalance(500, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, "NF-001", null);
        supply.CurrentStockBaseUnit.Should().Be(500);
        supply.HasRecordedMovement.Should().BeTrue();

        Action again = () => supply.RecordInitialBalance(100, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, null);
        again.Should().Throw<InvalidOperationException>().WithMessage("INITIAL_BALANCE_ALREADY_RECORDED");
    }

    [Fact]
    public void Initial_balance_requires_a_positive_representable_quantity()
    {
        var supply = NewSupply();
        Action action = () => supply.RecordInitialBalance(-1, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, null);
        action.Should().Throw<ArgumentException>().WithMessage("QUANTITY_MUST_BE_POSITIVE");
    }

    // ---- Unit conversion (mission §9) ----

    [Theory]
    [InlineData(1, SupplyBaseUnit.Kilogram, SupplyBaseUnit.Gram, 1000)]
    [InlineData(1, SupplyBaseUnit.Liter, SupplyBaseUnit.Milliliter, 1000)]
    [InlineData(1, SupplyBaseUnit.Meter, SupplyBaseUnit.Centimeter, 100)]
    [InlineData(2500, SupplyBaseUnit.Gram, SupplyBaseUnit.Kilogram, 2.5)]
    public void Compatible_unit_conversions_are_exact(decimal quantity, SupplyBaseUnit entered, SupplyBaseUnit baseUnit, decimal expected)
    {
        SupplyUnitConversion.ToBaseUnit(quantity, entered, baseUnit).Should().Be(expected);
    }

    [Fact]
    public void Positive_quantity_that_survives_base_precision_is_accepted()
    {
        var supply = NewSupply(SupplyBaseUnit.Kilogram);
        var movement = supply.RecordManualIncrease(1, SupplyBaseUnit.Gram, "Ajuste fino", DateTimeOffset.UtcNow);

        movement.EnteredQuantity.Should().Be(1);
        movement.EnteredUnit.Should().Be(SupplyBaseUnit.Gram);
        movement.QuantityDeltaBaseUnit.Should().Be(0.001m);
    }

    [Fact]
    public void Every_quantity_command_rejects_a_positive_value_that_rounds_to_zero_in_the_base_unit()
    {
        var now = DateTimeOffset.UtcNow;
        Action[] commands =
        [
            () => NewSupply(SupplyBaseUnit.Kilogram).RecordInitialBalance(0.01m, SupplyBaseUnit.Gram, now, null, null),
            () => NewSupply(SupplyBaseUnit.Kilogram).RecordPurchaseReceipt(0.01m, SupplyBaseUnit.Gram, now, null, 1m, null, null, null),
            () => NewSupply(SupplyBaseUnit.Kilogram).RecordManualIncrease(0.01m, SupplyBaseUnit.Gram, "Contagem", now),
            () => NewSupply(SupplyBaseUnit.Kilogram).RecordManualDecrease(0.01m, SupplyBaseUnit.Gram, "Contagem", now),
            () => NewSupply(SupplyBaseUnit.Kilogram).RecordCorrection(0.01m, SupplyBaseUnit.Gram, "Contagem", now),
        ];

        foreach (var command in commands)
            command.Should().Throw<ArgumentException>().WithMessage("QUANTITY_BELOW_BASE_PRECISION");
    }

    [Fact]
    public void Incompatible_unit_conversion_is_rejected()
    {
        Action action = () => SupplyUnitConversion.ToBaseUnit(1, SupplyBaseUnit.Gram, SupplyBaseUnit.Liter);
        action.Should().Throw<ArgumentException>().WithMessage("UNIT_CONVERSION_NOT_SUPPORTED");
    }

    [Fact]
    public void Unit_typed_supply_rejects_a_different_entered_unit()
    {
        var supply = new Supply("BOX-20X20", "Caixa 20x20", null, "PACKAGING", SupplyBaseUnit.Unit, null, null, null, null);
        Action action = () => supply.RecordPurchaseReceipt(1, SupplyBaseUnit.Kilogram, DateTimeOffset.UtcNow, null, null, null, null, null);
        action.Should().Throw<ArgumentException>().WithMessage("UNIT_CONVERSION_NOT_SUPPORTED");
    }

    [Fact]
    public void Purchase_receipt_entered_in_kilograms_is_persisted_in_grams_for_a_gram_based_supply()
    {
        var supply = NewSupply(SupplyBaseUnit.Gram);
        var movement = supply.RecordPurchaseReceipt(1, SupplyBaseUnit.Kilogram, DateTimeOffset.UtcNow, null, 89.90m, "Fornecedor X", "NF-100", null);
        supply.CurrentStockBaseUnit.Should().Be(1000);
        movement.EnteredQuantity.Should().Be(1);
        movement.EnteredUnit.Should().Be(SupplyBaseUnit.Kilogram);
        movement.QuantityDeltaBaseUnit.Should().Be(1000);
        // R$89,90 for 1000g => R$0.0899/g
        supply.LatestPurchaseUnitCost.Should().Be(0.0899m);
    }

    [Fact]
    public void Purchase_receipt_unit_cost_is_interpreted_per_entered_unit()
    {
        var supply = NewSupply(SupplyBaseUnit.Gram);
        supply.RecordPurchaseReceipt(1, SupplyBaseUnit.Kilogram, DateTimeOffset.UtcNow, 89.90m, null, null, null, null);
        supply.LatestPurchaseUnitCost.Should().Be(0.0899m);
    }

    [Fact]
    public void Purchase_receipt_canonicalizes_consistent_costs_and_rejects_conflicting_costs_without_posting()
    {
        var supply = NewSupply(SupplyBaseUnit.Gram);
        var movement = supply.RecordPurchaseReceipt(1, SupplyBaseUnit.Kilogram, DateTimeOffset.UtcNow, 89.90m, 89.90m, null, null, null);
        movement.UnitCostSnapshot.Should().Be(0.0899m);
        movement.TotalCostSnapshot.Should().Be(89.90m);

        var rejected = NewSupply(SupplyBaseUnit.Gram);
        Action mismatch = () => rejected.RecordPurchaseReceipt(1, SupplyBaseUnit.Kilogram, DateTimeOffset.UtcNow, 89.90m, 1m, null, null, null);
        mismatch.Should().Throw<ArgumentException>().WithMessage("PURCHASE_COST_MISMATCH");
        rejected.CurrentStockBaseUnit.Should().Be(0);
        rejected.InventoryMovements.Should().BeEmpty();
    }

    [Fact]
    public void Purchase_receipt_rejects_non_positive_quantity()
    {
        var supply = NewSupply();
        Action action = () => supply.RecordPurchaseReceipt(0, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, null, null, null, null);
        action.Should().Throw<ArgumentException>().WithMessage("QUANTITY_MUST_BE_POSITIVE");
    }

    // ---- Manual adjustments (mission §23) ----

    [Fact]
    public void Manual_increase_requires_a_reason()
    {
        var supply = NewSupply();
        Action noReason = () => supply.RecordManualIncrease(10, SupplyBaseUnit.Gram, " ", DateTimeOffset.UtcNow);
        noReason.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Manual_increase_adds_to_stock()
    {
        var supply = NewSupply();
        supply.RecordManualIncrease(50, SupplyBaseUnit.Gram, "Contagem física", DateTimeOffset.UtcNow);
        supply.CurrentStockBaseUnit.Should().Be(50);
    }

    [Fact]
    public void Manual_decrease_below_zero_is_rejected()
    {
        var supply = NewSupply();
        supply.RecordInitialBalance(100, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, null);
        Action action = () => supply.RecordManualDecrease(150, SupplyBaseUnit.Gram, "Amostra", DateTimeOffset.UtcNow);
        action.Should().Throw<InsufficientStockException>().WithMessage("INSUFFICIENT_STOCK");
        supply.CurrentStockBaseUnit.Should().Be(100, "a rejected decrease must not mutate stock");
    }

    [Fact]
    public void Manual_decrease_exactly_to_zero_is_allowed()
    {
        var supply = NewSupply();
        supply.RecordInitialBalance(100, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, null);
        supply.RecordManualDecrease(100, SupplyBaseUnit.Gram, "Uso total", DateTimeOffset.UtcNow);
        supply.CurrentStockBaseUnit.Should().Be(0);
    }

    // ---- Correction (mission §13) ----

    [Fact]
    public void Correction_reconciles_to_the_counted_quantity()
    {
        var supply = NewSupply();
        supply.RecordInitialBalance(100, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, null);
        var movement = supply.RecordCorrection(80, SupplyBaseUnit.Gram, "Contagem física", DateTimeOffset.UtcNow);
        supply.CurrentStockBaseUnit.Should().Be(80);
        movement.QuantityDeltaBaseUnit.Should().Be(-20);
        movement.Type.Should().Be(InventoryMovementType.Correction);
    }

    [Fact]
    public void Correction_with_no_actual_change_is_rejected()
    {
        var supply = NewSupply();
        supply.RecordInitialBalance(100, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, null);
        Action action = () => supply.RecordCorrection(100, SupplyBaseUnit.Gram, "Contagem física", DateTimeOffset.UtcNow);
        action.Should().Throw<ArgumentException>().WithMessage("CORRECTION_QUANTITY_UNCHANGED");
    }

    // ---- Movement immutability (mission §14) ----

    [Fact]
    public void Movements_expose_no_mutation_API()
    {
        typeof(InventoryMovement).GetMethods()
            .Where(m => m.Name is "Update" or "Delete" or "Remove")
            .Should().BeEmpty("InventoryMovement must be append-only — no Update/Delete/Remove method may exist");
    }

    // ---- Activation ----

    [Fact]
    public void Deactivate_and_activate_toggle_active_flag()
    {
        var supply = NewSupply();
        supply.Active.Should().BeTrue();
        supply.Deactivate();
        supply.Active.Should().BeFalse();
        supply.Activate();
        supply.Active.Should().BeTrue();
    }

    // ---- Low stock ----

    [Fact]
    public void Low_stock_is_true_when_stock_is_at_or_below_minimum()
    {
        var supply = NewSupply(minimumStock: 100);
        supply.RecordInitialBalance(100, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, null);
        supply.IsLowStock.Should().BeTrue();

        supply.RecordManualIncrease(1, SupplyBaseUnit.Gram, "Compra pequena", DateTimeOffset.UtcNow);
        supply.IsLowStock.Should().BeFalse();
    }
}
