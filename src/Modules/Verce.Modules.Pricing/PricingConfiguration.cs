using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Verce.Platform.Persistence;
using Verce.SharedKernel;

namespace Verce.Modules.Pricing;

public sealed class SalesChannelConfiguration : IEntityTypeConfiguration<SalesChannel>
{
    private static readonly ValueConverter<SalesChannelKind, string> KindConverter = new(
        value => value.ToString(), value => Enum.Parse<SalesChannelKind>(value));

    public void Configure(EntityTypeBuilder<SalesChannel> b)
    {
        b.ToTable("sales_channel", "pricing", table =>
            table.HasCheckConstraint("ck_sales_channel_kind", "kind IN ('Direct', 'Marketplace', 'Other')"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasApplicationMetadata();

        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Kind).HasConversion(KindConverter).HasMaxLength(16).IsRequired();
        b.Property(x => x.DefaultMarginPercent).HasColumnType($"numeric(9,{Rounding.PercentScale})");
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.Active).IsRequired();
        b.HasIndex(x => x.Active);
    }
}

public sealed class FeeRuleConfiguration : IEntityTypeConfiguration<FeeRule>
{
    public void Configure(EntityTypeBuilder<FeeRule> b)
    {
        b.ToTable("fee_rule", "pricing");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasApplicationMetadata();

        b.Property(x => x.SalesChannelId).IsRequired();
        // Exactly one FeeRule per channel for S5 (FeeRule.cs remarks) — enforced here, not just
        // by convention, so a second POST can never create an ambiguous "which rule applies".
        b.HasIndex(x => x.SalesChannelId).IsUnique();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Active).IsRequired();

        b.HasMany(x => x.Versions).WithOne().HasForeignKey(x => x.FeeRuleId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class FeeRuleVersionConfiguration : IEntityTypeConfiguration<FeeRuleVersion>
{
    private static readonly ValueConverter<FixedFeeApplication, string> ApplicationConverter = new(
        value => value.ToString(), value => Enum.Parse<FixedFeeApplication>(value));

    public void Configure(EntityTypeBuilder<FeeRuleVersion> b)
    {
        b.ToTable("fee_rule_version", "pricing", table =>
            table.HasCheckConstraint("ck_fee_rule_version_fixed_fee_application", "fixed_fee_application IN ('PerUnit', 'PerOrder')"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasApplicationMetadata();

        b.Property(x => x.FeeRuleId).IsRequired();
        b.Property(x => x.ValidFrom).HasColumnType("date").IsRequired();
        b.Property(x => x.ValidUntil).HasColumnType("date");
        b.Property(x => x.CommissionPercent).HasColumnType($"numeric(9,{Rounding.PercentScale})").IsRequired();
        b.Property(x => x.FixedFee).HasColumnType($"numeric(18,{Rounding.InternalScale})").IsRequired();
        b.Property(x => x.FixedFeeApplication).HasConversion(ApplicationConverter).HasMaxLength(16).IsRequired();
        b.Property(x => x.MinimumFee).HasColumnType($"numeric(18,{Rounding.InternalScale})");
        b.Property(x => x.MaximumFee).HasColumnType($"numeric(18,{Rounding.InternalScale})");
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.HasIndex(x => new { x.FeeRuleId, x.ValidFrom });
        b.HasMany(x => x.Brackets).WithOne().HasForeignKey(x => x.FeeRuleVersionId).OnDelete(DeleteBehavior.Cascade);

        // ADR-0005 §1: non-overlap is a database EXCLUDE constraint, not application code — see
        // the migration's raw SQL (Npgsql/EF Core has no fluent builder for EXCLUDE). Left
        // unmodeled here deliberately so `has-pending-model-changes` never flags it as drift.
    }
}

public sealed class PriceBracketConfiguration : IEntityTypeConfiguration<PriceBracket>
{
    public void Configure(EntityTypeBuilder<PriceBracket> b)
    {
        b.ToTable("price_bracket", "pricing", table =>
        {
            table.HasCheckConstraint("ck_price_bracket_range", "min_price >= 0 AND (max_price IS NULL OR max_price > min_price)");
            table.HasCheckConstraint("ck_price_bracket_commission", "commission_percent >= 0 AND commission_percent < 1");
            table.HasCheckConstraint("ck_price_bracket_fixed_fee", "fixed_fee >= 0");
            table.HasCheckConstraint("ck_price_bracket_fee_range", "minimum_fee IS NULL OR maximum_fee IS NULL OR minimum_fee <= maximum_fee");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.HasApplicationMetadata();
        b.Property(x => x.FeeRuleVersionId).IsRequired();
        b.Property(x => x.MinPrice).HasColumnType($"numeric(18,{Rounding.MoneyScale})").IsRequired();
        b.Property(x => x.MaxPrice).HasColumnType($"numeric(18,{Rounding.MoneyScale})");
        b.Property(x => x.CommissionPercent).HasColumnType($"numeric(9,{Rounding.PercentScale})").IsRequired();
        b.Property(x => x.FixedFee).HasColumnType($"numeric(18,{Rounding.MoneyScale})").IsRequired();
        b.Property(x => x.MinimumFee).HasColumnType($"numeric(18,{Rounding.MoneyScale})"); b.Property(x => x.MaximumFee).HasColumnType($"numeric(18,{Rounding.MoneyScale})");
        b.Property(x => x.SortOrder).IsRequired(); b.HasIndex(x => new { x.FeeRuleVersionId, x.SortOrder }).IsUnique();
    }
}
