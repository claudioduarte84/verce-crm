using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Verce.Platform.Persistence;
using Verce.SharedKernel;

namespace Verce.Modules.Inventory;

public sealed class SupplyCategoryConfiguration : IEntityTypeConfiguration<SupplyCategory>
{
    public void Configure(EntityTypeBuilder<SupplyCategory> b)
    {
        b.ToTable("supply_category", "inventory");
        b.HasKey(x => x.Code);
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
    }
}

public sealed class SupplyConfiguration : IEntityTypeConfiguration<Supply>
{
    /// <summary>Shadow-property name of the internal Supply pagination tie-breaker (ADR-0011
    /// §1.2.1), mirroring Customer's. Never a public CLR property, never serialized.</summary>
    public const string CreationSequence = "CreationSequence";

    private static readonly ValueConverter<SupplyBaseUnit, string> BaseUnitConverter = new(
        value => value.ToString(),
        value => Enum.Parse<SupplyBaseUnit>(value));

    private static readonly ValueConverter<FilamentMaterialType, string> MaterialTypeConverter = new(
        value => value.ToString(),
        value => Enum.Parse<FilamentMaterialType>(value));

    public void Configure(EntityTypeBuilder<Supply> b)
    {
        b.ToTable("supply", "inventory", table =>
        {
            table.HasCheckConstraint("ck_supply_base_unit", "base_unit IN ('Gram', 'Kilogram', 'Unit', 'Milliliter', 'Liter', 'Meter', 'Centimeter')");
            table.HasCheckConstraint("ck_supply_filament_material_type", "filament_material_type IS NULL OR filament_material_type IN ('Pla', 'PlaPlus', 'Petg', 'Abs', 'Asa', 'Tpu', 'Nylon', 'Pc', 'Pva', 'Other')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasApplicationMetadata();

        var creationSequence = b.Property<long>(CreationSequence)
            .HasColumnName("creation_sequence")
            .IsRequired()
            .ValueGeneratedOnAdd()
            .HasDefaultValueSql("nextval('inventory.supply_creation_sequence_seq')");
        creationSequence.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        b.HasIndex(CreationSequence).IsUnique();

        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.HasIndex(x => x.Name).HasMethod("gin").HasOperators("gin_trgm_ops");
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.CategoryCode).HasMaxLength(40).IsRequired();
        b.HasOne<SupplyCategory>().WithMany().HasForeignKey(x => x.CategoryCode).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.CategoryCode);
        b.Property(x => x.BaseUnit).HasConversion(BaseUnitConverter).HasMaxLength(16).IsRequired();
        b.Property(x => x.MinimumStock).HasColumnType($"numeric(14,{Rounding.QuantityScale})");
        b.Property(x => x.PreferredSupplier).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.Active).IsRequired();
        b.HasIndex(x => x.Active);
        b.Property(x => x.CurrentStockBaseUnit).HasColumnType($"numeric(14,{Rounding.QuantityScale})").IsRequired();
        b.Property(x => x.LatestPurchaseUnitCost).HasColumnType($"numeric(18,{Rounding.InternalScale})");
        b.Property(x => x.HasRecordedMovement).IsRequired();

        // Filament details are individual nullable scalar columns on THIS table, not an EF owned
        // type (see the comment on Supply's private _filament* fields for why) — mapped via their
        // private backing fields since the value is only ever exposed through the computed,
        // EF-ignored `FilamentDetails` property.
        b.Ignore(x => x.FilamentDetails);
        b.Property<FilamentMaterialType?>("_filamentMaterialType").HasColumnName("filament_material_type").HasConversion(new ValueConverter<FilamentMaterialType?, string?>(
            value => value == null ? null : value.Value.ToString(),
            value => value == null ? null : Enum.Parse<FilamentMaterialType>(value))).HasMaxLength(16);
        b.Property<string?>("_filamentBrand").HasColumnName("filament_brand").HasMaxLength(100);
        b.Property<string?>("_filamentColorName").HasColumnName("filament_color_name").HasMaxLength(100);
        b.Property<string?>("_filamentColorCode").HasColumnName("filament_color_code").HasMaxLength(20);
        b.Property<decimal?>("_filamentDiameterMm").HasColumnName("filament_diameter_mm").HasColumnType($"numeric(6,{Rounding.QuantityScale})");
        b.Property<decimal?>("_filamentSpoolNetWeightGrams").HasColumnName("filament_spool_net_weight_grams").HasColumnType($"numeric(12,{Rounding.GramsScale})");

        b.HasMany(x => x.InventoryMovements).WithOne().HasForeignKey(x => x.SupplyId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class InventoryMovementConfiguration : IEntityTypeConfiguration<InventoryMovement>
{
    private static readonly ValueConverter<InventoryMovementType, string> TypeConverter = new(
        value => value.ToString(),
        value => Enum.Parse<InventoryMovementType>(value));
    private static readonly ValueConverter<SupplyBaseUnit, string> EnteredUnitConverter = new(
        value => value.ToString(),
        value => Enum.Parse<SupplyBaseUnit>(value));

    public void Configure(EntityTypeBuilder<InventoryMovement> b)
    {
        b.ToTable("inventory_movement", "inventory", table =>
        {
            table.HasCheckConstraint("ck_inventory_movement_type", "type IN ('PurchaseReceipt', 'ManualIncrease', 'ManualDecrease', 'Consumption', 'ReturnIn', 'ReturnOut', 'InitialBalance', 'Correction')");
            table.HasCheckConstraint("ck_inventory_movement_entered_unit", "entered_unit IN ('Gram', 'Kilogram', 'Unit', 'Milliliter', 'Liter', 'Meter', 'Centimeter')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        // Movements are append-only historical facts, not an editable entity — CreatedAt/CreatedBy
        // are what "posted at/by" means for a ledger row; UpdatedAt/UpdatedBy simply never populate.
        b.HasApplicationMetadata();
        b.Property(x => x.SupplyId).IsRequired();
        b.Property(x => x.Type).HasConversion(TypeConverter).HasMaxLength(16).IsRequired();
        b.Property(x => x.EnteredQuantity).HasColumnType($"numeric(18,{SupplyUnitConversion.EnteredQuantityScale})").IsRequired();
        b.Property(x => x.EnteredUnit).HasConversion(EnteredUnitConverter).HasMaxLength(16).IsRequired();
        b.Property(x => x.QuantityDeltaBaseUnit).HasColumnType($"numeric(14,{Rounding.QuantityScale})").IsRequired();
        b.Property(x => x.OccurredAt).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(1000);
        b.Property(x => x.Reference).HasMaxLength(200);
        b.Property(x => x.Supplier).HasMaxLength(200);
        b.Property(x => x.UnitCostSnapshot).HasColumnType($"numeric(18,{Rounding.InternalScale})");
        b.Property(x => x.TotalCostSnapshot).HasColumnType($"numeric(18,{Rounding.MoneyScale})");
        b.HasIndex(x => new { x.SupplyId, x.OccurredAt });
    }
}
