using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Verce.Platform.Persistence;

namespace Verce.Modules.Customers;

public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    /// <summary>Shadow-property name of the internal Customer pagination tie-breaker
    /// (ADR-0011 §1.2.1). Never a public CLR property, never serialized, never user-editable.</summary>
    public const string CreationSequence = "CreationSequence";

    public void Configure(EntityTypeBuilder<Customer> b)
    {
        b.ToTable("customer", "customers", table => table.HasCheckConstraint("ck_customer_person_type", "person_type IN ('INDIVIDUAL', 'COMPANY')")); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Version).IsConcurrencyToken(); b.HasApplicationMetadata();
        var creationSequence = b.Property<long>(CreationSequence)
            .HasColumnName("creation_sequence")
            .IsRequired()
            .ValueGeneratedOnAdd()
            .HasDefaultValueSql("nextval('customers.customer_creation_sequence_seq')");
        creationSequence.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        b.HasIndex(CreationSequence).IsUnique();
        var personTypeConverter = new ValueConverter<PersonType, string>(value => value == PersonType.Individual ? "INDIVIDUAL" : "COMPANY", value => value == "INDIVIDUAL" ? PersonType.Individual : PersonType.Company);
        b.Property(x => x.PersonType).HasConversion(personTypeConverter).HasMaxLength(16); b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.TradeName).HasMaxLength(200); b.Property(x => x.Document).HasMaxLength(14); b.Property(x => x.Email).HasMaxLength(320); b.Property(x => x.Phone).HasMaxLength(50); b.Property(x => x.Notes).HasMaxLength(4000);
        b.HasIndex(x => x.Document).IsUnique().HasFilter("document IS NOT NULL AND deleted_at IS NULL");
        b.HasIndex(x => x.Name).HasMethod("gin").HasOperators("gin_trgm_ops"); b.HasIndex(x => x.IsActive).HasFilter("deleted_at IS NULL");
        b.HasMany(x => x.Addresses).WithOne().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
    }
}
public sealed class CustomerAddressConfiguration : IEntityTypeConfiguration<CustomerAddress>
{
    public void Configure(EntityTypeBuilder<CustomerAddress> b)
    {
        b.ToTable("customer_address", "customers"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.HasApplicationMetadata(); b.Property(x => x.Label).HasMaxLength(100).IsRequired(); b.Property(x => x.ZipCode).HasMaxLength(20).IsRequired(); b.Property(x => x.Street).HasMaxLength(200).IsRequired(); b.Property(x => x.Number).HasMaxLength(30).IsRequired(); b.Property(x => x.Complement).HasMaxLength(200); b.Property(x => x.District).HasMaxLength(100).IsRequired(); b.Property(x => x.City).HasMaxLength(100).IsRequired(); b.Property(x => x.State).HasMaxLength(2).IsRequired(); b.Property(x => x.Country).HasMaxLength(2).IsRequired(); b.Property(x => x.Notes).HasMaxLength(1000);
        b.HasIndex(x => x.CustomerId, "ux_customer_address_primary").IsUnique().HasFilter("is_primary");
        b.HasIndex(x => x.CustomerId, "ux_customer_address_default_shipping").IsUnique().HasFilter("is_default_shipping");
        b.HasIndex(x => x.CustomerId, "ix_customer_address_customer_id");
    }
}
