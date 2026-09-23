using System.Text.Json.Serialization;
using Verce.Platform.Audit;
using Verce.SharedKernel;
using Verce.SharedKernel.Domain;

namespace Verce.Modules.Finance;

[JsonConverter(typeof(JsonStringEnumConverter<AccountingTreatment>))]
public enum AccountingTreatment { OPERATING_EXPENSE, INVENTORY_PURCHASE, ASSET_ACQUISITION }

[Auditable]
public sealed class ExpenseCategory : AggregateRoot
{
    private ExpenseCategory() { }

    public ExpenseCategory(string name, AccountingTreatment defaultTreatment, bool isActive = true)
    {
        Name = Required(name, 120, "EXPENSE_CATEGORY_NAME");
        DefaultTreatment = ValidateTreatment(defaultTreatment);
        IsActive = isActive;
    }

    public string Name { get; private set; } = string.Empty;
    public AccountingTreatment DefaultTreatment { get; private set; }
    public bool IsActive { get; private set; }

    public void Update(string name, AccountingTreatment defaultTreatment, bool isActive)
    {
        Name = Required(name, 120, "EXPENSE_CATEGORY_NAME");
        DefaultTreatment = ValidateTreatment(defaultTreatment);
        IsActive = isActive;
    }

    private static AccountingTreatment ValidateTreatment(AccountingTreatment treatment) =>
        Enum.IsDefined(treatment) ? treatment : throw new ArgumentException("EXPENSE_TREATMENT_INVALID");

    internal static string Required(string? value, int maximum, string error) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximum
            ? throw new ArgumentException($"{error}_INVALID") : value.Trim();
}

[Auditable]
public sealed class Expense : AggregateRoot
{
    private Expense() { }

    public Expense(Guid expenseCategoryId, string description, decimal amount, DateOnly incurredOn,
        AccountingTreatment accountingTreatment, DateOnly? paidOn = null, string? paymentMethod = null,
        string? supplierName = null, string? documentNumber = null, Guid? inventoryMovementId = null,
        Guid? salesChannelId = null, Guid? machineId = null, string? attachmentPath = null, string? notes = null)
    {
        Apply(expenseCategoryId, description, amount, incurredOn, accountingTreatment, paidOn, paymentMethod,
            supplierName, documentNumber, inventoryMovementId, salesChannelId, machineId, attachmentPath, notes);
    }

    public Guid ExpenseCategoryId { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public DateOnly IncurredOn { get; private set; }
    public DateOnly? PaidOn { get; private set; }
    public string? PaymentMethod { get; private set; }
    public string? SupplierName { get; private set; }
    public string? DocumentNumber { get; private set; }
    public AccountingTreatment AccountingTreatment { get; private set; }
    public Guid? InventoryMovementId { get; private set; }
    public Guid? SalesChannelId { get; private set; }
    public Guid? MachineId { get; private set; }
    public string? AttachmentPath { get; private set; }
    public string? Notes { get; private set; }

    public void Update(Guid expenseCategoryId, string description, decimal amount, DateOnly incurredOn,
        AccountingTreatment accountingTreatment, DateOnly? paidOn = null, string? paymentMethod = null,
        string? supplierName = null, string? documentNumber = null, Guid? inventoryMovementId = null,
        Guid? salesChannelId = null, Guid? machineId = null, string? attachmentPath = null, string? notes = null) =>
        Apply(expenseCategoryId, description, amount, incurredOn, accountingTreatment, paidOn, paymentMethod,
            supplierName, documentNumber, inventoryMovementId, salesChannelId, machineId, attachmentPath, notes);

    private void Apply(Guid expenseCategoryId, string description, decimal amount, DateOnly incurredOn,
        AccountingTreatment accountingTreatment, DateOnly? paidOn, string? paymentMethod, string? supplierName,
        string? documentNumber, Guid? inventoryMovementId, Guid? salesChannelId, Guid? machineId,
        string? attachmentPath, string? notes)
    {
        if (expenseCategoryId == Guid.Empty) throw new ArgumentException("EXPENSE_CATEGORY_REQUIRED");
        if (amount <= 0) throw new ArgumentException("EXPENSE_AMOUNT_INVALID");
        if (!Enum.IsDefined(accountingTreatment)) throw new ArgumentException("EXPENSE_TREATMENT_INVALID");
        if (inventoryMovementId is not null && accountingTreatment != AccountingTreatment.INVENTORY_PURCHASE)
            throw new ArgumentException("EXPENSE_INVENTORY_MOVEMENT_REQUIRES_INVENTORY_PURCHASE");
        if (paidOn is { } paid && paid < incurredOn) throw new ArgumentException("EXPENSE_PAID_DATE_INVALID");

        ExpenseCategoryId = expenseCategoryId;
        Description = ExpenseCategory.Required(description, 1000, "EXPENSE_DESCRIPTION");
        Amount = Rounding.ToMoney(amount);
        IncurredOn = incurredOn;
        PaidOn = paidOn;
        AccountingTreatment = accountingTreatment;
        InventoryMovementId = inventoryMovementId;
        SalesChannelId = salesChannelId;
        MachineId = machineId;
        PaymentMethod = Optional(paymentMethod, 100, "EXPENSE_PAYMENT_METHOD");
        SupplierName = Optional(supplierName, 300, "EXPENSE_SUPPLIER_NAME");
        DocumentNumber = Optional(documentNumber, 200, "EXPENSE_DOCUMENT_NUMBER");
        AttachmentPath = Optional(attachmentPath, 2000, "EXPENSE_ATTACHMENT_PATH");
        Notes = Optional(notes, 4000, "EXPENSE_NOTES");
    }

    private static string? Optional(string? value, int maximum, string error) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= maximum
            ? value.Trim() : throw new ArgumentException($"{error}_INVALID");
}
