using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Verce.Platform.Persistence;

namespace Verce.Modules.Production;

public sealed class ProductionOrderConfiguration : IEntityTypeConfiguration<ProductionOrder>
{
    private static readonly ValueConverter<ProductionOrderStatus, string> StatusConverter = new(
        value => value.ToString(), value => Enum.Parse<ProductionOrderStatus>(value));

    public void Configure(EntityTypeBuilder<ProductionOrder> b)
    {
        b.ToTable("production_order", "production", table => table.HasCheckConstraint(
            "ck_production_order_status",
            "status IN ('QUEUED','IN_PRODUCTION','READY','SHIPPED','DELIVERED','CANCELED')"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasApplicationMetadata();

        b.Property(x => x.OrderNumber).HasMaxLength(20).IsRequired();
        b.HasIndex(x => x.OrderNumber).IsUnique();
        b.Property(x => x.NumberDate).HasColumnType("date").IsRequired();
        b.Property(x => x.NumberSequence).IsRequired();
        b.HasIndex(x => new { x.NumberDate, x.NumberSequence }).IsUnique();

        // No FK to quoting.quote/quote_revision: ADR-0001 §3 forbids a module assembly reference
        // to another module's tables — these are plain ID references, validated only by the
        // idempotent domain-event handler that writes them (ADR-0020 §A.8).
        b.Property(x => x.QuoteId).IsRequired();
        b.HasIndex(x => x.QuoteId);
        b.Property(x => x.QuoteRevisionId).IsRequired();
        b.HasIndex(x => x.QuoteRevisionId).IsUnique(); // STATE-MACHINES §4.3: the idempotency key

        b.Property(x => x.Status).HasConversion(StatusConverter).HasMaxLength(16).IsRequired();
        b.HasIndex(x => new { x.Status, x.NumberDate });

        b.Property(x => x.HasPendingRevision).IsRequired();
        b.Property(x => x.SupersededByOrderId);
        b.Property(x => x.CancellationReason).HasMaxLength(64);

        b.HasOne<ProductionOrder>().WithMany().HasForeignKey(x => x.SupersededByOrderId).OnDelete(DeleteBehavior.Restrict);
    }
}
