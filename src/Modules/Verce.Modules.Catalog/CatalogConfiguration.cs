using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Verce.Platform.Persistence;
using Verce.SharedKernel;

namespace Verce.Modules.Catalog;

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    /// <summary>Shadow-property name of the internal Product pagination tie-breaker
    /// (ADR-0011 §1.2.1), mirroring Customer/Supply. Never a public CLR property.</summary>
    public const string CreationSequence = "CreationSequence";

    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.ToTable("product", "catalog");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasApplicationMetadata();

        var creationSequence = b.Property<long>(CreationSequence)
            .HasColumnName("creation_sequence")
            .IsRequired()
            .ValueGeneratedOnAdd()
            .HasDefaultValueSql("nextval('catalog.product_creation_sequence_seq')");
        creationSequence.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        b.HasIndex(CreationSequence).IsUnique();

        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.HasIndex(x => x.Name).HasMethod("gin").HasOperators("gin_trgm_ops");
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.Active).IsRequired();
        b.HasIndex(x => x.Active);

        // ProductRecipe is 1:1 owned-child navigation via the backing field, mirroring
        // Customer.Addresses' field-based access convention — Product always constructs its
        // Recipe in the same call, so there is never a null recipe to model as optional.
        b.HasOne(x => x.Recipe).WithOne().HasForeignKey<ProductRecipe>(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ProductRecipeConfiguration : IEntityTypeConfiguration<ProductRecipe>
{
    public void Configure(EntityTypeBuilder<ProductRecipe> b)
    {
        b.ToTable("product_recipe", "catalog");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasApplicationMetadata();
        b.HasIndex(x => x.ProductId).IsUnique();

        b.Property(x => x.RevisionNumber).IsRequired();
        b.Property(x => x.WastagePercentOverride).HasColumnType($"numeric(9,{Rounding.PercentScale})");
        b.Property(x => x.LaborMinutes).HasColumnType($"numeric(14,{Rounding.QuantityScale})");
        b.Property(x => x.LaborHourlyRateOverride).HasColumnType($"numeric(18,{Rounding.InternalScale})");
        b.Property(x => x.MachineMinutes).HasColumnType($"numeric(14,{Rounding.QuantityScale})");
        b.Property(x => x.MachineHourlyRate).HasColumnType($"numeric(18,{Rounding.InternalScale})");
        b.Property(x => x.OutputQuantity).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(2000);

        b.HasMany(x => x.MaterialLines).WithOne().HasForeignKey(x => x.ProductRecipeId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.AdditionalCostLines).WithOne().HasForeignKey(x => x.ProductRecipeId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ProductRecipeMaterialLineConfiguration : IEntityTypeConfiguration<ProductRecipeMaterialLine>
{
    public void Configure(EntityTypeBuilder<ProductRecipeMaterialLine> b)
    {
        b.ToTable("product_recipe_material_line", "catalog");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        // No FK to inventory.supply: ADR-0001 §3.1 forbids a module assembly reference to
        // another module, and a cross-schema FK would not change that boundary anyway — Supply
        // existence/activity is validated by the API composition root at write time, exactly
        // like S4's CostingEndpoints validates SupplyId before calling CostEngine.
        b.Property(x => x.SupplyId).IsRequired();
        b.HasIndex(x => x.SupplyId);
        b.Property(x => x.EnteredQuantity).HasColumnType("numeric(18,8)").IsRequired();
        b.Property(x => x.EnteredUnit).HasMaxLength(16).IsRequired();
        b.Property(x => x.NormalizedQuantityBaseUnit).HasColumnType($"numeric(14,{Rounding.QuantityScale})").IsRequired();
        b.Property(x => x.WastagePercentOverride).HasColumnType($"numeric(9,{Rounding.PercentScale})");
        b.Property(x => x.ManualUnitCostOverride).HasColumnType($"numeric(18,{Rounding.InternalScale})");
        b.Property(x => x.SortOrder).IsRequired();
        b.HasIndex(x => new { x.ProductRecipeId, x.SortOrder });
    }
}

public sealed class ProductRecipeAdditionalCostLineConfiguration : IEntityTypeConfiguration<ProductRecipeAdditionalCostLine>
{
    public void Configure(EntityTypeBuilder<ProductRecipeAdditionalCostLine> b)
    {
        b.ToTable("product_recipe_additional_cost_line", "catalog");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Description).HasMaxLength(200).IsRequired();
        b.Property(x => x.Amount).HasColumnType($"numeric(18,{Rounding.InternalScale})").IsRequired();
        b.Property(x => x.SortOrder).IsRequired();
        b.HasIndex(x => new { x.ProductRecipeId, x.SortOrder });
    }
}
