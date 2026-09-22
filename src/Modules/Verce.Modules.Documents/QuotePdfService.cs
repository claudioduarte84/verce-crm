using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verce.Platform.Persistence;

namespace Verce.Modules.Documents;

/// <summary>
/// Orchestrates Quote PDF materialization through the GENERIC template engine (S7/S14 scope
/// authority gate, OPTION A): resolves the PUBLISHED <see cref="DocumentTemplateVersion"/> for
/// <c>QUOTE</c>'s default template, walks its <c>definition</c>/<c>page_setup</c> via
/// <see cref="BlockTreeRenderer"/> against a <see cref="QuoteRenderContext"/>, and renders with
/// Chromium. Rendering (the slow, external Chromium call) happens BEFORE any database
/// transaction — holding a DB transaction or advisory lock across a multi-second external
/// process call is avoided by design. Only the short supersede-then-insert step is
/// transaction-scoped, guarded by a <c>pg_advisory_xact_lock</c> keyed by the artifact's own
/// identity. A render/validation failure throws BEFORE any row is written — there is no code
/// path that can leave a corrupt or zero-byte artifact "completed"; a retry is always possible
/// because nothing was ever persisted.
/// </summary>
public sealed class QuotePdfService
{
    private readonly VerceDbContext _db;
    private readonly IHtmlToPdfRenderer _renderer;
    private readonly IDocumentStorage _storage;

    public QuotePdfService(VerceDbContext db, IHtmlToPdfRenderer renderer, IDocumentStorage storage)
    {
        _db = db;
        _renderer = renderer;
        _storage = storage;
    }

    /// <summary>The plain "generate or return" contract a first-time POST/an outbox delivery
    /// uses: if a CURRENT document already exists, it is returned untouched — no render, no new
    /// row. Concurrent first-generation callers converge on ONE row even though each mints its
    /// own candidate <c>renderRequestId</c> before racing.</summary>
    public async Task<GeneratedDocument> GenerateOrGetCurrentAsync(QuotePdfInput input, Guid? actorUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = await FindCurrentAsync(input.QuoteRevisionId, cancellationToken);
        if (current is not null) return current;
        return await MaterializeAsync(input, Guid.CreateVersion7(), actorUserId, now, convergeOnExistingCurrent: true, reissueReason: null, cancellationToken);
    }

    /// <summary>The outbox consumer's entry point: <paramref name="renderRequestId"/> is the
    /// event's own stable idempotency key (ADR-0012 §22, minted once at approval).</summary>
    public Task<GeneratedDocument> MaterializeForRequestAsync(QuotePdfInput input, Guid renderRequestId, Guid? actorUserId, DateTimeOffset now, CancellationToken cancellationToken) =>
        MaterializeAsync(input, renderRequestId, actorUserId, now, convergeOnExistingCurrent: false, reissueReason: null, cancellationToken);

    /// <summary>The explicit, operator-initiated "reemitir" action (ADR-0016 §5): ALWAYS renders
    /// and inserts a new row, even if a current one already exists. <paramref name="reissueReason"/>
    /// is the operator-typed "why" ADR-0016 §5 requires the audit trail to carry.</summary>
    public Task<GeneratedDocument> ReissueAsync(QuotePdfInput input, Guid? actorUserId, DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        // This service boundary is shared by every deliberate reissue caller. Validate before
        // even a lookup so no template, renderer, storage or database work follows bad input.
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("DOCUMENT_REISSUE_REASON_REQUIRED");
        var trimmedReason = reason.Trim();
        if (trimmedReason.Length > 2000) throw new InvalidOperationException("DOCUMENT_REISSUE_REASON_TOO_LONG");
        return MaterializeAsync(input, Guid.CreateVersion7(), actorUserId, now, convergeOnExistingCurrent: false, trimmedReason, cancellationToken);
    }

    private async Task<GeneratedDocument> MaterializeAsync(QuotePdfInput input, Guid renderRequestId, Guid? actorUserId, DateTimeOffset now,
        bool convergeOnExistingCurrent, string? reissueReason, CancellationToken cancellationToken)
    {
        var existingForRequest = await FindByRenderRequestIdAsync(renderRequestId, cancellationToken);
        if (existingForRequest is not null) return existingForRequest;

        var templateVersion = await ResolvePublishedDefaultVersionAsync(cancellationToken);
        using var definitionDoc = JsonDocument.Parse(templateVersion.DefinitionJson);
        using var pageSetupDoc = JsonDocument.Parse(templateVersion.PageSetupJson);
        var context = new QuoteRenderContext(input);

        // F-02: page_setup's size/orientation/repeatOn are fully authoritative here — including,
        // when a region needs FIRST_ONLY/ALL_EXCEPT_FIRST, the real two-Chromium-pass-plus-merge
        // path (BlockTreeRenderer.RenderDocumentAsync); this call site no longer knows or cares
        // which path ran.
        var rendered = await BlockTreeRenderer.RenderDocumentAsync(
            definitionDoc.RootElement, pageSetupDoc.RootElement, context, input.QuoteNumber, _renderer, cancellationToken);

        var htmlBytes = Encoding.UTF8.GetBytes(rendered.BodyHtml);
        var htmlSha256 = Sha256Hex(htmlBytes);

        ValidatePdfBytes(rendered.Pdf.Bytes);
        var pdfSha256 = Sha256Hex(rendered.Pdf.Bytes);

        var htmlKey = await _storage.StoreAsync(htmlBytes, htmlSha256, ".html", cancellationToken);
        var pdfKey = await _storage.StoreAsync(rendered.Pdf.Bytes, pdfSha256, ".pdf", cancellationToken);
        var renderDataJson = JsonSerializer.Serialize(input);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var lockKey = $"{input.QuoteRevisionId:D}|{QuoteBindingCatalogue.DocumentTypeCode}";
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}::text, 0));", cancellationToken);

        var raceLoserExisting = await FindByRenderRequestIdAsync(renderRequestId, cancellationToken);
        if (raceLoserExisting is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return raceLoserExisting;
        }

        var previousCurrent = await _db.Set<GeneratedDocument>().SingleOrDefaultAsync(d =>
            d.SourceType == QuoteDocumentKinds.SourceType && d.SourceId == input.QuoteRevisionId
            && d.DocumentTypeCode == QuoteBindingCatalogue.DocumentTypeCode && d.IsCurrent, cancellationToken);

        if (convergeOnExistingCurrent && previousCurrent is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return previousCurrent;
        }

        previousCurrent?.MarkSuperseded();

        var document = new GeneratedDocument(Guid.CreateVersion7(), renderRequestId, QuoteBindingCatalogue.DocumentTypeCode,
            QuoteDocumentKinds.SourceType, input.QuoteRevisionId, templateVersion.Id,
            renderDataJson, htmlSha256, htmlKey, pdfSha256, pdfKey, rendered.Pdf.Bytes.LongLength,
            rendered.Pdf.ChromiumVersion, rendered.Pdf.RenderEngineVersion, input.BrandAssetVersionIds,
            actorUserId, now, reissueReason);
        _db.Add(document);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return document;
    }

    /// <summary>ADR-0007 §7.2 / mission §10: only a PUBLISHED version may ever render.</summary>
    private async Task<DocumentTemplateVersion> ResolvePublishedDefaultVersionAsync(CancellationToken cancellationToken)
    {
        var template = await _db.Set<DocumentTemplate>().AsNoTracking()
            .Include(t => t.Versions)
            .SingleOrDefaultAsync(t => t.DocumentTypeCode == QuoteBindingCatalogue.DocumentTypeCode && t.IsDefault && t.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("DOCUMENT_TEMPLATE_DEFAULT_NOT_FOUND");
        var published = template.Versions.Where(v => v.Status == DocumentTemplateVersionStatus.PUBLISHED)
            .OrderByDescending(v => v.VersionNumber).FirstOrDefault()
            ?? throw new InvalidOperationException(RenderErrorCodes.NotPublished);
        return published;
    }

    /// <summary>Renders a SPECIFIC template version regardless of whether it is still the
    /// current default — used only by tests proving DRAFT refusal / historical V1 vs V2
    /// behaviour; production code always goes through <see cref="ResolvePublishedDefaultVersionAsync"/>.</summary>
    internal async Task<DocumentTemplateVersion> ResolveVersionForTestingAsync(Guid documentTemplateVersionId, CancellationToken cancellationToken) =>
        await _db.Set<DocumentTemplateVersion>().AsNoTracking().SingleAsync(v => v.Id == documentTemplateVersionId, cancellationToken);

    public Task<GeneratedDocument?> FindCurrentAsync(Guid quoteRevisionId, CancellationToken cancellationToken) =>
        _db.Set<GeneratedDocument>().AsNoTracking().SingleOrDefaultAsync(d =>
            d.SourceType == QuoteDocumentKinds.SourceType && d.SourceId == quoteRevisionId
            && d.DocumentTypeCode == QuoteBindingCatalogue.DocumentTypeCode && d.IsCurrent, cancellationToken);

    public Task<GeneratedDocument?> FindByRenderRequestIdAsync(Guid renderRequestId, CancellationToken cancellationToken) =>
        _db.Set<GeneratedDocument>().AsNoTracking().SingleOrDefaultAsync(d => d.RenderRequestId == renderRequestId, cancellationToken);

    public Task<GeneratedDocument?> FindByIdAsync(Guid documentId, Guid quoteRevisionId, CancellationToken cancellationToken) =>
        _db.Set<GeneratedDocument>().AsNoTracking().SingleOrDefaultAsync(d =>
            d.Id == documentId && d.SourceType == QuoteDocumentKinds.SourceType && d.SourceId == quoteRevisionId
            && d.DocumentTypeCode == QuoteBindingCatalogue.DocumentTypeCode, cancellationToken);

    /// <summary>The revision timeline's document-history surface: every document ever issued for
    /// this revision, newest first — never just the current one.</summary>
    public Task<List<GeneratedDocument>> ListForRevisionAsync(Guid quoteRevisionId, CancellationToken cancellationToken) =>
        _db.Set<GeneratedDocument>().AsNoTracking()
            .Where(d => d.SourceType == QuoteDocumentKinds.SourceType && d.SourceId == quoteRevisionId && d.DocumentTypeCode == QuoteBindingCatalogue.DocumentTypeCode)
            .OrderByDescending(d => d.IssuedAt).ToListAsync(cancellationToken);

    public Task<byte[]> ReadPdfBytesAsync(GeneratedDocument document, CancellationToken cancellationToken) =>
        _storage.ReadAsync(document.PdfStorageKey, cancellationToken);

    private static void ValidatePdfBytes(byte[] bytes)
    {
        if (bytes.Length == 0) throw new InvalidOperationException("PDF_RENDER_EMPTY");
        if (bytes.LongLength > QuoteDocumentKinds.MaxPdfSizeBytes) throw new InvalidOperationException("PDF_RENDER_TOO_LARGE");
        ReadOnlySpan<byte> signature = "%PDF-"u8;
        if (bytes.Length < signature.Length || !bytes.AsSpan(0, signature.Length).SequenceEqual(signature))
            throw new InvalidOperationException("PDF_RENDER_INVALID_SIGNATURE");
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
