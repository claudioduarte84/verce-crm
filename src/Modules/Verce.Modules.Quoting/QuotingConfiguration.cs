using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Verce.Platform.Persistence;
using Verce.SharedKernel;

namespace Verce.Modules.Quoting;

public sealed class QuoteConfiguration : IEntityTypeConfiguration<Quote>
{
    public void Configure(EntityTypeBuilder<Quote> b)
    {
        b.ToTable("quote", "quoting");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasApplicationMetadata();

        b.Property(x => x.Number).HasMaxLength(20).IsRequired();
        b.HasIndex(x => x.Number).IsUnique();
        b.Property(x => x.NumberDate).HasColumnType("date").IsRequired();
        b.Property(x => x.NumberSequence).IsRequired();
        b.HasIndex(x => new { x.NumberDate, x.NumberSequence }).IsUnique();
        // No FK to customers.customer: ADR-0001 §3 forbids a module assembly reference to
        // another module (mirrors ProductRecipeMaterialLine.SupplyId) — existence is validated
        // by the composition root at write time.
        b.Property(x => x.CustomerId);
        b.Property(x => x.CurrentRevisionId).IsRequired();

        b.HasMany(x => x.Revisions).WithOne().HasForeignKey(x => x.QuoteId).OnDelete(DeleteBehavior.Cascade);

        // H-04: quote.current_revision_id -> quote_revision.id IS a real referential-integrity
        // requirement, but the two tables reference each other in the SAME insert (Quote and its
        // first QuoteRevision are created and saved together) — a plain FK would fail the very
        // first INSERT, since neither row exists yet when Postgres checks it per-statement. The
        // constraint is added as a DEFERRABLE INITIALLY DEFERRED raw-SQL ALTER TABLE in the
        // migration (Npgsql/EF Core has no fluent builder for DEFERRABLE, mirroring
        // fee_rule_version_no_overlap's EXCLUDE constraint) — left unmodeled here so
        // has-pending-model-changes never flags it as drift. DEFERRABLE INITIALLY DEFERRED
        // postpones the check to COMMIT, by which point both rows exist in the same transaction;
        // ON DELETE RESTRICT (the default) is correct — a Quote is never deleted while any
        // revision still points at it.
    }
}

public sealed class QuoteRevisionConfiguration : IEntityTypeConfiguration<QuoteRevision>
{
    private static readonly ValueConverter<QuoteRevisionStatus, string> StatusConverter = new(
        value => value.ToString(), value => Enum.Parse<QuoteRevisionStatus>(value));

    public void Configure(EntityTypeBuilder<QuoteRevision> b)
    {
        b.ToTable("quote_revision", "quoting", table =>
        {
            table.HasCheckConstraint("ck_quote_revision_status",
                "status IN ('GENERATED','SENT','NEGOTIATING','APPROVED','CANCELED','EXPIRED','SUPERSEDED')");
            // H-04: matches the domain guard in QuoteRevision's constructor exactly
            // (QUOTE_REVISION_INDEX_INVALID) — never legitimately violated, so a DB CHECK cannot
            // reject any real intermediate state.
            table.HasCheckConstraint("ck_quote_revision_index_positive", "revision_index >= 1");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasApplicationMetadata();

        b.Property(x => x.QuoteId).IsRequired();
        b.Property(x => x.RevisionIndex).IsRequired();
        b.HasIndex(x => new { x.QuoteId, x.RevisionIndex }).IsUnique();
        b.Property(x => x.RevisionSuffix).HasMaxLength(8).IsRequired();
        b.Property(x => x.Status).HasConversion(StatusConverter).HasMaxLength(16).IsRequired();
        b.Property(x => x.SalesChannelId).IsRequired();
        b.Property(x => x.IssuedAt).IsRequired();
        b.Property(x => x.ValidityDays).IsRequired();
        b.Property(x => x.ValidUntil).HasColumnType("date").IsRequired();

        b.Property(x => x.CustomerId);
        b.Property(x => x.CustomerNameSnapshot).HasMaxLength(200);
        b.Property(x => x.CustomerDocumentSnapshot).HasMaxLength(32);
        b.Property(x => x.CustomerContactsSnapshot).HasColumnType("jsonb");
        b.Property(x => x.CustomerAddressesSnapshot).HasColumnType("jsonb");

        b.Property(x => x.SupersededByRevisionId);
        b.Property(x => x.SourceRevisionId);
        b.Property(x => x.ApprovedAt);
        b.Property(x => x.ApprovedBy);

        // S7 proposal-content snapshot (ADR-0016 §7, DOMAIN-MODEL §8) — all optional, frozen at
        // construction. Never mutated after the revision is persisted.
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Scope).HasMaxLength(4000);
        b.Property(x => x.TechnicalHighlightsJson).HasColumnName("technical_highlights").HasColumnType("jsonb");
        b.Property(x => x.TechnicalNotes).HasMaxLength(4000);
        b.Property(x => x.OutOfScope).HasMaxLength(4000);
        b.Property(x => x.PaymentTerms).HasMaxLength(2000);
        b.Property(x => x.DeliveryTerms).HasMaxLength(2000);
        b.Property(x => x.Warranty).HasMaxLength(2000);
        b.Property(x => x.Notes).HasMaxLength(4000);
        b.Property(x => x.InternalNotes).HasMaxLength(4000);

        b.Property(x => x.SubtotalAmount).HasColumnType($"numeric(18,{Rounding.MoneyScale})").IsRequired();
        b.Property(x => x.DiscountAmount).HasColumnType($"numeric(18,{Rounding.MoneyScale})").IsRequired();
        b.Property(x => x.TotalAmount).HasColumnType($"numeric(18,{Rounding.MoneyScale})").IsRequired();
        b.Property(x => x.TotalCostAmount).HasColumnType($"numeric(18,{Rounding.MoneyScale})").IsRequired();
        b.Property(x => x.ExpectedProfitAmount).HasColumnType($"numeric(18,{Rounding.MoneyScale})").IsRequired();
        b.Property(x => x.EffectiveMarginPercent).HasColumnType($"numeric(9,{Rounding.PercentScale})").IsRequired();

        // Self-referencing FK to the revision that superseded this one — no cascade (a superseded
        // revision must never disappear merely because its successor is later deleted, which
        // never actually happens, but the FK action documents intent).
        b.HasOne<QuoteRevision>().WithMany().HasForeignKey(x => x.SupersededByRevisionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<QuoteRevision>().WithMany().HasForeignKey(x => x.SourceRevisionId).OnDelete(DeleteBehavior.Restrict);

        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.QuoteRevisionId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.History).WithOne().HasForeignKey(x => x.QuoteRevisionId).OnDelete(DeleteBehavior.Cascade);

        // H-04/H-02: shaped exactly for ExpireQuotesService's own query predicate
        // (STATE-MACHINES §1.6) — non-superseded, still-undecided, ordered by the boundary the
        // job actually filters on. A plain b.HasIndex(x => x.Status) does not serve that
        // predicate; this partial index does.
        b.HasIndex(x => x.ValidUntil)
            .HasFilter("superseded_by_revision_id IS NULL AND status IN ('GENERATED','SENT','NEGOTIATING')")
            .HasDatabaseName("ix_quote_revision_expiration_eligible");
    }
}

public sealed class QuoteItemConfiguration : IEntityTypeConfiguration<QuoteItem>
{
    private static readonly ValueConverter<QuoteFixedFeeApplication, string> FeeApplicationConverter = new(
        value => value.ToString(), value => Enum.Parse<QuoteFixedFeeApplication>(value));
    private static readonly ValueConverter<QuoteDiscountKind, string> DiscountKindConverter = new(
        value => value.ToString(), value => Enum.Parse<QuoteDiscountKind>(value));

    public void Configure(EntityTypeBuilder<QuoteItem> b)
    {
        // H-04: each CHECK matches an UNCONDITIONAL domain guard in QuoteItem's own constructor
        // (never relaxed for a GENERATED-state intermediate row) — so it can never reject a
        // legitimate row. UnitPrice/NetUnitPrice are deliberately NOT constrained here: the
        // domain allows a non-positive UnitPrice before approval (STATE-MACHINES §1.5's
        // QUOTE_ITEM_INVALID_PRICE guard fires only at Approve), so a DB CHECK on that column
        // would reject a real, currently-legal intermediate state.
        b.ToTable("quote_item", "quoting", table =>
        {
            table.HasCheckConstraint("ck_quote_item_quantity_positive", "quantity > 0");
            table.HasCheckConstraint("ck_quote_item_discount_kind", "discount_kind IN ('None','Percent','Amount')");
            table.HasCheckConstraint("ck_quote_item_fixed_fee_application", "fixed_fee_application IN ('PerUnit','PerOrder')");
            table.HasCheckConstraint("ck_quote_item_manual_price_override_non_negative", "manual_price_override IS NULL OR manual_price_override >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasApplicationMetadata();

        b.Property(x => x.QuoteRevisionId).IsRequired();
        b.Property(x => x.LineNumber).IsRequired();
        b.HasIndex(x => new { x.QuoteRevisionId, x.LineNumber }).IsUnique();

        // B-01: provenance only — never a navigation, never re-interpreted as cross-quote
        // identity. No FK: a line's own source revision can be pruned/retained independently.
        b.Property(x => x.SourceQuoteItemId);

        b.Property(x => x.ProductId);
        b.Property(x => x.ProductRecipeId);
        b.Property(x => x.ProductNameSnapshot).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.Quantity).HasColumnType($"numeric(14,{Rounding.QuantityScale})").IsRequired();

        b.Property(x => x.UnitTotalCost).HasColumnType($"numeric(18,{Rounding.InternalScale})").IsRequired();
        b.Property(x => x.CostEngineVersion).HasMaxLength(40).IsRequired();
        b.Property(x => x.DesiredMarginPercent).HasColumnType($"numeric(9,{Rounding.PercentScale})").IsRequired();

        b.Property(x => x.SalesChannelId).IsRequired();
        b.Property(x => x.FeeRuleVersionId);
        b.Property(x => x.CommissionPercent).HasColumnType($"numeric(9,{Rounding.PercentScale})").IsRequired();
        b.Property(x => x.FixedFeeApplication).HasConversion(FeeApplicationConverter).HasMaxLength(16).IsRequired();
        b.Property(x => x.RawFixedFee).HasColumnType($"numeric(18,{Rounding.InternalScale})").IsRequired();
        b.Property(x => x.AllocatedOrderFee).HasColumnType($"numeric(18,{Rounding.MoneyScale})").IsRequired();
        b.Property(x => x.FixedFeePerUnit).HasColumnType($"numeric(18,{Rounding.InternalScale})").IsRequired();
        b.Property(x => x.RoundingPolicyApplied).HasMaxLength(16).IsRequired();

        b.Property(x => x.SuggestedUnitPrice).HasColumnType($"numeric(18,{Rounding.MoneyScale})").IsRequired();
        b.Property(x => x.CommissionAmountPerUnit).HasColumnType($"numeric(18,{Rounding.MoneyScale})").IsRequired();
        b.Property(x => x.FeeClampApplied).HasMaxLength(8);
        b.Property(x => x.BracketId);
        b.Property(x => x.BracketResolution).HasMaxLength(24);
        b.Property(x => x.FeeBasisAmount).HasColumnType($"numeric(18,{Rounding.MoneyScale})");
        b.Property(x => x.ManualPriceOverride).HasColumnType($"numeric(18,{Rounding.MoneyScale})");
        b.Property(x => x.PriceOverridden).IsRequired();
        b.Property(x => x.UnitPrice).HasColumnType($"numeric(18,{Rounding.MoneyScale})").IsRequired();

        b.Property(x => x.DiscountKind).HasConversion(DiscountKindConverter).HasMaxLength(16).IsRequired();
        b.Property(x => x.DiscountValue).HasColumnType($"numeric(18,{Rounding.InternalScale})").IsRequired();
        b.Property(x => x.DiscountAmount).HasColumnType($"numeric(18,{Rounding.MoneyScale})").IsRequired();
        b.Property(x => x.NetUnitPrice).HasColumnType($"numeric(18,{Rounding.MoneyScale})").IsRequired();

        b.Property(x => x.LineTotalAmount).HasColumnType($"numeric(18,{Rounding.MoneyScale})").IsRequired();
        b.Property(x => x.LineCostAmount).HasColumnType($"numeric(18,{Rounding.MoneyScale})").IsRequired();
        b.Property(x => x.LineFeeAmount).HasColumnType($"numeric(18,{Rounding.MoneyScale})").IsRequired();
        b.Property(x => x.ExpectedProfitAmount).HasColumnType($"numeric(18,{Rounding.MoneyScale})").IsRequired();
        b.Property(x => x.EffectiveMarginPercent).HasColumnType($"numeric(9,{Rounding.PercentScale})").IsRequired();

        // B-02: the 1:1 full cost breakdown and its two 1:N component collections, all owned
        // directly by this QuoteItem (flat ownership chain).
        b.HasOne(x => x.CostSnapshot).WithOne().HasForeignKey<QuoteItemCostSnapshot>(x => x.QuoteItemId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Materials).WithOne().HasForeignKey(x => x.QuoteItemId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.AdditionalCosts).WithOne().HasForeignKey(x => x.QuoteItemId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class QuoteItemCostSnapshotConfiguration : IEntityTypeConfiguration<QuoteItemCostSnapshot>
{
    public void Configure(EntityTypeBuilder<QuoteItemCostSnapshot> b)
    {
        b.ToTable("quote_item_cost_snapshot", "quoting");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();

        b.Property(x => x.QuoteItemId).IsRequired();
        b.HasIndex(x => x.QuoteItemId).IsUnique();

        b.Property(x => x.EngineVersion).HasMaxLength(40).IsRequired();
        b.Property(x => x.MaterialCostBeforeWastage).HasColumnType($"numeric(18,{Rounding.InternalScale})").IsRequired();
        b.Property(x => x.MaterialWastageCost).HasColumnType($"numeric(18,{Rounding.InternalScale})").IsRequired();
        b.Property(x => x.MaterialsTotalCost).HasColumnType($"numeric(18,{Rounding.InternalScale})").IsRequired();
        b.Property(x => x.LaborMinutes).HasColumnType($"numeric(14,{Rounding.QuantityScale})");
        b.Property(x => x.LaborHourlyRate).HasColumnType($"numeric(18,{Rounding.InternalScale})");
        b.Property(x => x.LaborRateSource).HasMaxLength(24);
        b.Property(x => x.LaborCost).HasColumnType($"numeric(18,{Rounding.InternalScale})").IsRequired();
        b.Property(x => x.MachineMinutes).HasColumnType($"numeric(14,{Rounding.QuantityScale})");
        b.Property(x => x.MachineHourlyRate).HasColumnType($"numeric(18,{Rounding.InternalScale})");
        b.Property(x => x.MachineCost).HasColumnType($"numeric(18,{Rounding.InternalScale})").IsRequired();
        b.Property(x => x.AdditionalDirectCostsTotal).HasColumnType($"numeric(18,{Rounding.InternalScale})").IsRequired();
        b.Property(x => x.TotalEstimatedCost).HasColumnType($"numeric(18,{Rounding.InternalScale})").IsRequired();
        b.Property(x => x.OutputQuantity).IsRequired();
        b.Property(x => x.EstimatedUnitCost).HasColumnType($"numeric(18,{Rounding.InternalScale})").IsRequired();
    }
}

public sealed class QuoteItemMaterialSnapshotConfiguration : IEntityTypeConfiguration<QuoteItemMaterialSnapshot>
{
    public void Configure(EntityTypeBuilder<QuoteItemMaterialSnapshot> b)
    {
        b.ToTable("quote_item_material_snapshot", "quoting");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();

        b.Property(x => x.QuoteItemId).IsRequired();
        b.Property(x => x.LineNumber).IsRequired();
        b.HasIndex(x => new { x.QuoteItemId, x.LineNumber }).IsUnique();

        // No FK to inventory.supply: ADR-0001 §3.1 — a plain ID reference, resolved by the
        // composition root only, exactly like ProductRecipeMaterialLine.SupplyId.
        b.Property(x => x.SupplyId).IsRequired();
        b.Property(x => x.SupplyCodeSnapshot).HasMaxLength(40).IsRequired();
        b.Property(x => x.SupplyNameSnapshot).HasMaxLength(200).IsRequired();
        b.Property(x => x.EnteredQuantity).HasColumnType("numeric(18,8)").IsRequired();
        b.Property(x => x.EnteredUnit).HasMaxLength(16).IsRequired();
        b.Property(x => x.NormalizedQuantityBaseUnit).HasColumnType($"numeric(14,{Rounding.QuantityScale})").IsRequired();
        b.Property(x => x.BaseUnit).HasMaxLength(16).IsRequired();
        b.Property(x => x.WastagePercent).HasColumnType($"numeric(9,{Rounding.PercentScale})").IsRequired();
        b.Property(x => x.EffectiveQuantityBaseUnit).HasColumnType($"numeric(14,{Rounding.QuantityScale})").IsRequired();
        b.Property(x => x.CostSource).HasMaxLength(32).IsRequired();
        b.Property(x => x.CostPolicy).HasMaxLength(32).IsRequired();
        b.Property(x => x.UnitCostBaseUnit).HasColumnType($"numeric(18,{Rounding.InternalScale})").IsRequired();
        b.Property(x => x.CostBeforeWastage).HasColumnType($"numeric(18,{Rounding.InternalScale})").IsRequired();
        b.Property(x => x.WastageCost).HasColumnType($"numeric(18,{Rounding.InternalScale})").IsRequired();
        b.Property(x => x.CostAfterWastage).HasColumnType($"numeric(18,{Rounding.InternalScale})").IsRequired();
        b.Property(x => x.CurrentStockBaseUnitAtIssue).HasColumnType($"numeric(14,{Rounding.QuantityScale})").IsRequired();
        b.Property(x => x.ExceededCurrentStockAtIssue).IsRequired();
    }
}

public sealed class QuoteItemAdditionalCostSnapshotConfiguration : IEntityTypeConfiguration<QuoteItemAdditionalCostSnapshot>
{
    public void Configure(EntityTypeBuilder<QuoteItemAdditionalCostSnapshot> b)
    {
        b.ToTable("quote_item_additional_cost_snapshot", "quoting");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();

        b.Property(x => x.QuoteItemId).IsRequired();
        b.Property(x => x.LineNumber).IsRequired();
        b.HasIndex(x => new { x.QuoteItemId, x.LineNumber }).IsUnique();

        b.Property(x => x.Description).HasMaxLength(200).IsRequired();
        b.Property(x => x.Amount).HasColumnType($"numeric(18,{Rounding.InternalScale})").IsRequired();
    }
}

public sealed class QuoteStatusHistoryConfiguration : IEntityTypeConfiguration<QuoteStatusHistory>
{
    private static readonly ValueConverter<QuoteRevisionStatus?, string?> NullableStatusConverter = new(
        value => value == null ? null : value.ToString(), value => value == null ? null : Enum.Parse<QuoteRevisionStatus>(value));
    private static readonly ValueConverter<QuoteRevisionStatus, string> StatusConverter = new(
        value => value.ToString(), value => Enum.Parse<QuoteRevisionStatus>(value));
    private static readonly ValueConverter<QuoteHistoryTrigger, string> TriggerConverter = new(
        value => value.ToString(), value => Enum.Parse<QuoteHistoryTrigger>(value));

    public void Configure(EntityTypeBuilder<QuoteStatusHistory> b)
    {
        b.ToTable("quote_status_history", "quoting");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();

        b.Property(x => x.QuoteRevisionId).IsRequired();
        b.Property(x => x.FromStatus).HasConversion(NullableStatusConverter).HasMaxLength(16);
        b.Property(x => x.ToStatus).HasConversion(StatusConverter).HasMaxLength(16).IsRequired();
        b.Property(x => x.ChangedAt).IsRequired();
        b.Property(x => x.ChangedBy);
        b.Property(x => x.Trigger).HasConversion(TriggerConverter).HasMaxLength(16).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(2000);

        b.HasIndex(x => new { x.QuoteRevisionId, x.ChangedAt });
        b.HasIndex(x => x.ToStatus);
        b.HasIndex(x => x.ChangedAt);
    }
}
