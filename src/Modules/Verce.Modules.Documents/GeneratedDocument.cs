using Verce.Platform.Audit;
using Verce.SharedKernel.Domain;

namespace Verce.Modules.Documents;

/// <summary>
/// ADR-0016: an immutable, historically-stable generated document, rendered by the generic
/// <see cref="BlockTreeRenderer"/> from a PUBLISHED <see cref="DocumentTemplateVersion"/> — never
/// compiled renderer code (S7/S14 scope authority gate, OPTION A, 2026-09-21).
/// <see cref="RenderDataSnapshotJson"/> is the fully-resolved <see cref="QuotePdfInput"/> exactly
/// as handed to the renderer (ADR-0016 §1) — the answer to "what did this document say?" without
/// joining to a single live table. This module never references Quoting/Customers/Settings/
/// Production directly (CLAUDE.md rule 11): the composition root (Verce.Api) resolves every
/// cross-module value into <see cref="QuotePdfInput"/> before calling in.
///
/// Identity is <see cref="RenderRequestId"/> (ADR-0012 §22), never
/// <c>(source, kind, template)</c> — DATA-DICTIONARY names that key as the exact trap
/// (`generated_document.render_request_id` note: "using the template version as the dedup key…
/// would block a legitimate re-issue"). A RETRY of the same request converges on the same row; a
/// DELIBERATE re-render (after a branding/company/template change, or an operator explicitly
/// re-issuing) is a NEW request and creates a NEW, additional row. <see cref="IsCurrent"/> marks
/// the newest row per <see cref="SourceType"/>/<see cref="SourceId"/>/<see cref="DocumentTypeCode"/>
/// — the ONLY field this aggregate ever allows to change after construction (see
/// <see cref="MarkSuperseded"/>); every earlier row remains retrievable, unmutated (ADR-0016 §5).
/// </summary>
[Auditable]
public sealed class GeneratedDocument : AggregateRoot
{
    private GeneratedDocument() { }

    internal GeneratedDocument(Guid id, Guid renderRequestId, string documentTypeCode, string sourceType, Guid sourceId,
        Guid documentTemplateVersionId, string renderDataSnapshotJson, string htmlSha256, string htmlStorageKey,
        string pdfSha256, string pdfStorageKey, long pdfSizeBytes,
        string? chromiumVersion, string renderEngineVersion, IReadOnlyList<Guid> brandAssetVersionIds,
        Guid? generatedByUserId, DateTimeOffset issuedAt, string? reissueReason)
        : base(id)
    {
        if (renderRequestId == Guid.Empty) throw new ArgumentException("GENERATED_DOCUMENT_RENDER_REQUEST_ID_REQUIRED");
        if (string.IsNullOrWhiteSpace(documentTypeCode)) throw new ArgumentException("GENERATED_DOCUMENT_TYPE_REQUIRED");
        if (string.IsNullOrWhiteSpace(sourceType)) throw new ArgumentException("GENERATED_DOCUMENT_SOURCE_TYPE_REQUIRED");
        if (sourceId == Guid.Empty) throw new ArgumentException("GENERATED_DOCUMENT_SOURCE_ID_REQUIRED");
        if (documentTemplateVersionId == Guid.Empty) throw new ArgumentException("GENERATED_DOCUMENT_TEMPLATE_VERSION_REQUIRED");
        if (pdfSizeBytes <= 0) throw new ArgumentException("GENERATED_DOCUMENT_PDF_EMPTY");

        RenderRequestId = renderRequestId;
        DocumentTypeCode = documentTypeCode;
        SourceType = sourceType;
        SourceId = sourceId;
        DocumentTemplateVersionId = documentTemplateVersionId;
        Purpose = "ISSUED";
        IsCurrent = true;
        RenderDataSnapshotJson = renderDataSnapshotJson;
        HtmlSha256 = htmlSha256;
        HtmlStorageKey = htmlStorageKey;
        PdfSha256 = pdfSha256;
        PdfStorageKey = pdfStorageKey;
        PdfSizeBytes = pdfSizeBytes;
        ChromiumVersion = chromiumVersion;
        RenderEngineVersion = renderEngineVersion;
        BrandAssetVersionIds = brandAssetVersionIds.Distinct().ToArray();
        GeneratedByUserId = generatedByUserId;
        IssuedAt = issuedAt;
        ReissueReason = string.IsNullOrWhiteSpace(reissueReason) ? null : reissueReason.Trim();
    }

    /// <summary>ADR-0012 §22: the outbox/synchronous-request idempotency key. Unique — a retried
    /// delivery of the SAME request converges here; a deliberate re-render mints a new one.</summary>
    public Guid RenderRequestId { get; private set; }

    /// <summary>FK to <see cref="DocumentType"/> (mission §36) — <c>"QUOTE"</c> in S7.</summary>
    public string DocumentTypeCode { get; private set; } = string.Empty;

    /// <summary>Mission §35: no ambiguous implicit source identity — <c>"QUOTE_REVISION"</c> for
    /// every S7 document.</summary>
    public string SourceType { get; private set; } = string.Empty;

    /// <summary>Plain ID reference — the owning revision's id. No FK: Documents never references
    /// Quoting's tables (CLAUDE.md rule 11).</summary>
    public Guid SourceId { get; private set; }

    /// <summary>Mission §37: a real FK to the exact <see cref="DocumentTemplateVersion"/> that
    /// produced this document — <c>ON DELETE RESTRICT</c> (a version can never vanish out from
    /// under a document that cites it), never a free-text template-version string.</summary>
    public Guid DocumentTemplateVersionId { get; private set; }

    public string Purpose { get; private set; } = string.Empty;

    /// <summary>ADR-0016 §5: the most recent row for this (SourceType, SourceId,
    /// DocumentTypeCode) — the ONLY field this aggregate ever allows to change after
    /// construction (see <see cref="MarkSuperseded"/>). Every earlier row remains retrievable,
    /// unmutated.</summary>
    public bool IsCurrent { get; private set; }

    public string RenderDataSnapshotJson { get; private set; } = string.Empty;
    public string HtmlSha256 { get; private set; } = string.Empty;
    public string HtmlStorageKey { get; private set; } = string.Empty;
    public string PdfSha256 { get; private set; } = string.Empty;
    public string PdfStorageKey { get; private set; } = string.Empty;
    public long PdfSizeBytes { get; private set; }
    public string? ChromiumVersion { get; private set; }
    public string RenderEngineVersion { get; private set; } = string.Empty;

    /// <summary>ADR-0016 §1/§6 (brand asset snapshot): every distinct <c>BrandAssetVersion</c> id
    /// actually used to render this document (e.g. header AND footer logo roles) — audit-only,
    /// never re-resolved for a re-download. An array (Npgsql-native <c>uuid[]</c>), never a
    /// scalar.</summary>
    public Guid[] BrandAssetVersionIds { get; private set; } = [];

    public Guid? GeneratedByUserId { get; private set; }
    public DateTimeOffset IssuedAt { get; private set; }

    /// <summary>ADR-0016 §5: "the audit log records who re-rendered and why" — <c>who</c> is
    /// <see cref="GeneratedByUserId"/> (already captured, plus the generic audit interceptor's
    /// own actor); this is the <c>why</c>, captured only on a DELIBERATE re-issue of an
    /// already-issued revision (null for a first-ever render — there is nothing to explain yet).</summary>
    public string? ReissueReason { get; private set; }

    /// <summary>The ONLY mutation ever applied to an existing row (ADR-0016 §5): flips
    /// <see cref="IsCurrent"/> to false when a NEWER document for the same
    /// (SourceType, SourceId, DocumentTypeCode) is issued. Never touches the snapshot, hashes,
    /// storage keys, template reference or any other field.</summary>
    internal void MarkSuperseded() => IsCurrent = false;
}

/// <summary>The closed catalogue of document kinds S7 actually ships — never a free-text field a
/// caller could set to something else.</summary>
public static class QuoteDocumentKinds
{
    public const string SourceType = "QUOTE_REVISION";

    /// <summary>Maximum bytes accepted for a generated artifact — generous for a text-and-table
    /// commercial proposal (typically tens to a few hundred KB), while still a real technical
    /// ceiling against a pathological render.</summary>
    public const long MaxPdfSizeBytes = 10 * 1024 * 1024;
}
