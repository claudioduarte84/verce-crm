using FluentAssertions;
using Verce.Modules.Finance;

namespace Verce.Finance.Tests;

public class ExpenseTests
{
    [Theory]
    [InlineData(AccountingTreatment.OPERATING_EXPENSE)]
    [InlineData(AccountingTreatment.ASSET_ACQUISITION)]
    public void Ordinary_expense_accepts_approved_treatments(AccountingTreatment treatment)
    {
        var expense = new Expense(Guid.NewGuid(), "Despesa", 10.50m, new DateOnly(2026, 9, 22), treatment, salesChannelId: Guid.NewGuid());
        expense.AccountingTreatment.Should().Be(treatment);
        expense.SalesChannelId.Should().NotBeNull();
    }

    [Fact]
    public void Linked_inventory_movement_requires_inventory_purchase_treatment()
    {
        Action create = () => new Expense(Guid.NewGuid(), "Filamento", 89.90m, new DateOnly(2026, 9, 22), AccountingTreatment.OPERATING_EXPENSE,
            inventoryMovementId: Guid.NewGuid());
        create.Should().Throw<ArgumentException>().WithMessage("EXPENSE_INVENTORY_MOVEMENT_REQUIRES_INVENTORY_PURCHASE");
    }

    [Fact]
    public void Inventory_purchase_link_is_preserved()
    {
        var movement = Guid.NewGuid();
        var expense = new Expense(Guid.NewGuid(), "Compra", 35m, new DateOnly(2026, 9, 22), AccountingTreatment.INVENTORY_PURCHASE,
            inventoryMovementId: movement);
        expense.InventoryMovementId.Should().Be(movement);
    }
}
