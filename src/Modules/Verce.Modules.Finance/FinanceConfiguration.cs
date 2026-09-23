using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Verce.Platform.Persistence;
using Verce.SharedKernel;

namespace Verce.Modules.Finance;

public sealed class ExpenseCategoryConfiguration : IEntityTypeConfiguration<ExpenseCategory>
{
    public void Configure(EntityTypeBuilder<ExpenseCategory> b)
    {
        b.ToTable("expense_category", "finance", t => t.HasCheckConstraint("ck_expense_category_default_treatment",
            "default_treatment IN ('OPERATING_EXPENSE','INVENTORY_PURCHASE','ASSET_ACQUISITION')"));
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Version).IsConcurrencyToken(); b.HasApplicationMetadata();
        b.Property(x => x.Name).HasMaxLength(120).IsRequired(); b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.DefaultTreatment).HasConversion<string>().HasMaxLength(24).IsRequired();
        b.Property(x => x.IsActive).IsRequired();
    }
}

public sealed class ExpenseConfiguration : IEntityTypeConfiguration<Expense>
{
    public void Configure(EntityTypeBuilder<Expense> b)
    {
        b.ToTable("expense", "finance", t =>
        {
            t.HasCheckConstraint("ck_expense_amount_positive", "amount > 0");
            t.HasCheckConstraint("ck_expense_treatment", "accounting_treatment IN ('OPERATING_EXPENSE','INVENTORY_PURCHASE','ASSET_ACQUISITION')");
            t.HasCheckConstraint("ck_expense_inventory_movement_treatment", "inventory_movement_id IS NULL OR accounting_treatment = 'INVENTORY_PURCHASE'");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Version).IsConcurrencyToken(); b.HasApplicationMetadata();
        b.Property(x => x.ExpenseCategoryId).IsRequired();
        b.HasOne<ExpenseCategory>().WithMany().HasForeignKey(x => x.ExpenseCategoryId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Description).HasMaxLength(1000).IsRequired();
        b.Property(x => x.Amount).HasColumnType($"numeric(18,{Rounding.MoneyScale})").IsRequired();
        b.Property(x => x.IncurredOn).HasColumnType("date").IsRequired(); b.Property(x => x.PaidOn).HasColumnType("date");
        b.Property(x => x.AccountingTreatment).HasConversion<string>().HasMaxLength(24).IsRequired();
        b.Property(x => x.PaymentMethod).HasMaxLength(100); b.Property(x => x.SupplierName).HasMaxLength(300); b.Property(x => x.DocumentNumber).HasMaxLength(200);
        b.Property(x => x.AttachmentPath).HasMaxLength(2000); b.Property(x => x.Notes).HasMaxLength(4000);
        b.HasIndex(x => x.InventoryMovementId).IsUnique().HasFilter("inventory_movement_id IS NOT NULL");
        b.HasIndex(x => x.IncurredOn).HasFilter("accounting_treatment = 'OPERATING_EXPENSE'").HasDatabaseName("ix_expense_operating_incurred_on");
        b.HasIndex(x => new { x.SalesChannelId, x.IncurredOn }); b.HasIndex(x => x.ExpenseCategoryId);
    }
}
