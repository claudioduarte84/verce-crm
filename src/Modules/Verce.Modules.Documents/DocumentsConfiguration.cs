using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Verce.Platform.Persistence;

namespace Verce.Modules.Documents;

public sealed class DocumentTypeConfiguration : IEntityTypeConfiguration<DocumentType>
{
    public void Configure(EntityTypeBuilder<DocumentType> b)
    {
        b.ToTable("document_type", "documents");
        b.HasKey(x => x.Code);
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.IsActive).IsRequired();
    }
}

public sealed class DocumentTemplateConfiguration : IEntityTypeConfiguration<DocumentTemplate>
{
    public void Configure(EntityTypeBuilder<DocumentTemplate> b)
    {
        b.ToTable("document_template", "documents");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasApplicationMetadata();

        b.Property(x => x.DocumentTypeCode).HasMaxLength(32).IsRequired();
        b.HasOne<DocumentType>().WithMany().HasForeignKey(x => x.DocumentTypeCode).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.IsDefault).IsRequired();
        b.Property(x => x.IsActive).IsRequired();
        b.Property(x => x.DeletedAt);

        // ADR-0007 §7: at most one active, non-deleted default template per DocumentType.
        b.HasIndex(x => x.DocumentTypeCode).IsUnique().HasFilter("is_default AND deleted_at IS NULL")
            .HasDatabaseName("ix_document_template_default_per_type");

        b.HasMany(x => x.Versions).WithOne().HasForeignKey(x => x.DocumentTemplateId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DocumentTemplateVersionConfiguration : IEntityTypeConfiguration<DocumentTemplateVersion>
{
    private static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DocumentTemplateVersionStatus, string> StatusConverter = new(
        value => value.ToString(), value => Enum.Parse<DocumentTemplateVersionStatus>(value));

    public void Configure(EntityTypeBuilder<DocumentTemplateVersion> b)
    {
        b.ToTable("document_template_version", "documents", table =>
            table.HasCheckConstraint("ck_document_template_version_status", "status IN ('DRAFT','PUBLISHED','ARCHIVED')"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasApplicationMetadata();

        b.Property(x => x.DocumentTemplateId).IsRequired();
        b.Property(x => x.VersionNumber).IsRequired();
        b.Property(x => x.Status).HasConversion(StatusConverter).HasMaxLength(16).IsRequired();
        b.Property(x => x.SchemaVersion).IsRequired();
        b.Property(x => x.DefinitionJson).HasColumnName("definition").HasColumnType("jsonb").IsRequired();
        b.Property(x => x.PageSetupJson).HasColumnName("page_setup").HasColumnType("jsonb").IsRequired();
        b.Property(x => x.PublishedAt);
        b.Property(x => x.PublishedBy);

        b.HasIndex(x => new { x.DocumentTemplateId, x.VersionNumber }).IsUnique();
        // ADR-0007 §7.2: at most one open DRAFT per template. S7 never creates a DRAFT (it mints
        // versions already PUBLISHED), but the constraint is real infrastructure S14's authoring
        // workflow depends on, not merely documented intent.
        b.HasIndex(x => x.DocumentTemplateId).IsUnique().HasFilter("status = 'DRAFT'")
            .HasDatabaseName("ix_document_template_version_one_open_draft");
    }
}

public sealed class GeneratedDocumentConfiguration : IEntityTypeConfiguration<GeneratedDocument>
{
    public void Configure(EntityTypeBuilder<GeneratedDocument> b)
    {
        b.ToTable("generated_document", "documents", table =>
        {
            table.HasCheckConstraint("ck_generated_document_purpose", "purpose IN ('PREVIEW', 'ISSUED')");
            table.HasCheckConstraint("ck_generated_document_pdf_size_positive", "pdf_size_bytes > 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasApplicationMetadata();

        b.Property(x => x.RenderRequestId).IsRequired();
        b.Property(x => x.DocumentTypeCode).HasMaxLength(32).IsRequired();
        b.HasOne<DocumentType>().WithMany().HasForeignKey(x => x.DocumentTypeCode).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.SourceType).HasMaxLength(32).IsRequired();
        // No FK on SourceId: a plain cross-module reference (CLAUDE.md rule 11) — for S7 always a
        // quoting.quote_revision.id, validated by the composition root, never by this module.
        b.Property(x => x.SourceId).IsRequired();
        // Real FK, RESTRICT: a DocumentTemplateVersion can never be deleted while a document cites it.
        b.Property(x => x.DocumentTemplateVersionId).IsRequired();
        b.HasOne<DocumentTemplateVersion>().WithMany().HasForeignKey(x => x.DocumentTemplateVersionId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Purpose).HasMaxLength(16).IsRequired();
        b.Property(x => x.IsCurrent).IsRequired();
        b.Property(x => x.RenderDataSnapshotJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.HtmlSha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.HtmlStorageKey).HasMaxLength(300).IsRequired();
        b.Property(x => x.PdfSha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.PdfStorageKey).HasMaxLength(300).IsRequired();
        b.Property(x => x.PdfSizeBytes).IsRequired();
        b.Property(x => x.ChromiumVersion).HasMaxLength(64);
        b.Property(x => x.RenderEngineVersion).HasMaxLength(64).IsRequired();
        // uuid[] — Npgsql-native array column (no declarative FK is possible on an array element
        // anyway; this is audit-only, resolved once at render time).
        b.Property(x => x.BrandAssetVersionIds).HasColumnType("uuid[]").IsRequired();
        b.Property(x => x.GeneratedByUserId);
        b.Property(x => x.IssuedAt).IsRequired();
        b.Property(x => x.ReissueReason).HasMaxLength(2000);

        // ADR-0012 §22 / DATA-DICTIONARY (render_request_id trap note): THIS is the real
        // idempotency identity — a retried delivery of the SAME request converges here; a
        // deliberate re-render mints a fresh RenderRequestId and inserts a NEW row.
        b.HasIndex(x => x.RenderRequestId).IsUnique().HasDatabaseName("ix_generated_document_render_request_id");
        // ADR-0016 §5: at most one CURRENT document per (source type, source, document type) at
        // any time — the database invariant behind "the newest render is `is_current`", enforced
        // under concurrency by Postgres itself, never only by application code.
        b.HasIndex(x => new { x.SourceType, x.SourceId, x.DocumentTypeCode }).IsUnique().HasFilter("is_current")
            .HasDatabaseName("ix_generated_document_current_per_source");
        b.HasIndex(x => x.PdfSha256);
    }
}
