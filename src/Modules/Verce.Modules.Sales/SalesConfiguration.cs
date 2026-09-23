using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Verce.Platform.Persistence;
using Verce.SharedKernel;

namespace Verce.Modules.Sales;

public sealed class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> b)
    {
        b.ToTable("sale", "sales", t =>
        {
            t.HasCheckConstraint("ck_sale_source", "(source = 'QUOTE_CONVERSION' AND quote_revision_id IS NOT NULL AND marketplace_account_id IS NULL AND external_order_id IS NULL) OR (source = 'MANUAL_ENTRY' AND quote_revision_id IS NULL AND marketplace_account_id IS NULL AND external_order_id IS NULL) OR (source = 'MARKETPLACE_ORDER' AND quote_revision_id IS NULL AND marketplace_account_id IS NOT NULL AND external_order_id IS NOT NULL)");
            t.HasCheckConstraint("ck_sale_status", "status IN ('CONFIRMED','CANCELED')");
            t.HasCheckConstraint("ck_sale_fee_source", "fee_source IN ('LOCAL_RULE','PROVIDER_REPORTED')");
            t.HasCheckConstraint("ck_sale_cost_basis", "cost_basis IN ('ESTIMATED','MIXED','ACTUAL')");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Version).IsConcurrencyToken(); b.HasApplicationMetadata();
        b.Property(x => x.SaleNumber).HasMaxLength(32).IsRequired(); b.HasIndex(x => x.SaleNumber).IsUnique();
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(24).IsRequired(); b.Property(x => x.FeeSource).HasConversion<string>().HasMaxLength(24).IsRequired(); b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired(); b.Property(x => x.CostBasis).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(x => x.ExternalOrderId).HasMaxLength(200); b.Property(x => x.CustomerNameSnapshot).HasMaxLength(300); b.Property(x => x.ExternalOrderCode).HasMaxLength(200); b.Property(x => x.Notes).HasMaxLength(4000);
        foreach (var p in new[] { nameof(Sale.GrossAmount), nameof(Sale.DiscountAmount), nameof(Sale.NetAmount), nameof(Sale.ChannelFeeAmount), nameof(Sale.ShippingAmount), nameof(Sale.TotalCostAmount), nameof(Sale.GrossProfitAmount) }) b.Property(p).HasColumnType($"numeric(18,{Rounding.MoneyScale})");
        b.Property(x => x.EffectiveMarginPercent).HasColumnType($"numeric(9,{Rounding.PercentScale})");
        b.HasIndex(x => new { x.QuoteRevisionId }).HasFilter("quote_revision_id IS NOT NULL AND status <> 'CANCELED'").IsUnique();
        b.HasIndex(x => new { x.MarketplaceAccountId, x.ExternalOrderId }).IsUnique();
        b.HasIndex(x => x.ConversionRequestId).IsUnique(); b.HasIndex(x => x.SoldDate).HasFilter("status = 'CONFIRMED'").HasDatabaseName("ix_sale_confirmed_sold_date");
        b.HasIndex(x => new { x.SalesChannelId, x.SoldDate }); b.HasIndex(x => x.CustomerId);
        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.History).WithOne().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Cascade);
    }
}
public sealed class SaleItemConfiguration : IEntityTypeConfiguration<SaleItem>
{
    public void Configure(EntityTypeBuilder<SaleItem> b) { b.ToTable("sale_item", "sales"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.HasApplicationMetadata(); b.Property(x => x.ProductNameSnapshot).HasMaxLength(300).IsRequired(); b.HasIndex(x => new { x.SaleId, x.LineNumber }).IsUnique(); foreach (var p in new[] { nameof(SaleItem.UnitPrice), nameof(SaleItem.DiscountAmount), nameof(SaleItem.LineTotalAmount), nameof(SaleItem.LineCostAmount), nameof(SaleItem.ChannelFeeAmount), nameof(SaleItem.GrossProfitAmount) }) b.Property(p).HasColumnType($"numeric(18,{Rounding.MoneyScale})"); b.Property(x => x.UnitCostAmount).HasColumnType($"numeric(18,{Rounding.InternalScale})"); b.Property(x => x.Quantity).HasColumnType("numeric(14,4)"); b.Property(x => x.EffectiveMarginPercent).HasColumnType($"numeric(9,{Rounding.PercentScale})"); }
}
public sealed class SaleStatusHistoryConfiguration : IEntityTypeConfiguration<SaleStatusHistory>
{
    public void Configure(EntityTypeBuilder<SaleStatusHistory> b) { b.ToTable("sale_status_history", "sales"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.HasApplicationMetadata(); b.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(16); b.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(16).IsRequired(); b.Property(x => x.Reason).HasMaxLength(1000); b.HasIndex(x => new { x.SaleId, x.ChangedAt }); }
}
