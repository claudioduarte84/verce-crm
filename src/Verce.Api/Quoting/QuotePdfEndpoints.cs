using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Verce.Api.Authorization;
using Verce.Modules.Documents;
using Verce.Modules.Quoting;
using Verce.Modules.Settings;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;

namespace Verce.Api.Quoting;

public sealed record QuotePdfMetadataResponse(Guid Id, Guid QuoteRevisionId, string DocumentTypeCode, Guid DocumentTemplateVersionId,
    string PdfSha256, long PdfSizeBytes, DateTimeOffset IssuedAt, bool IsCurrent, string DownloadUrl);

/// <summary>ADR-0016 §5: "the audit log records who re-rendered and why". S7 final-findings
/// correction F-03 freezes the rule: a FIRST render never asks (there is nothing yet to
/// explain — <c>POST /pdf</c> takes no reason at all), but a deliberate REISSUE always requires
/// a non-blank <c>Reason</c>, trimmed, bounded at 2000 characters
/// (<see cref="GeneratedDocument.ReissueReason"/>'s own column length) — enforced here, never a
/// judgment call left to the caller.</summary>
public sealed record QuotePdfReissueRequest(string? Reason);

/// <summary>ADR-0016 §5: every document ever issued for one revision — the revision timeline's
/// "document history" surface (mission §115), never just the current one.</summary>
public sealed record QuotePdfHistoryItemResponse(Guid Id, DateTimeOffset IssuedAt, bool IsCurrent, string PdfSha256, string DownloadUrl);

/// <summary>
/// S7 Quote PDF V1 (mission §22-44). The composition root — never Documents itself
/// (mission §55) — resolves every cross-module value (the immutable QuoteRevision snapshot,
/// Customer snapshot already frozen on the revision, Company Profile, the DOCUMENT_LOGO/
/// PRIMARY_LOGO Brand Asset, and the CURRENT `documents.default_*` Settings) into a single
/// strongly-typed <see cref="QuotePdfInput"/> before ever calling into
/// <see cref="Verce.Modules.Documents.QuotePdfService"/>. Settings/Brand Asset/Company Profile are
/// read ONLY on first materialization (mission §33/§35) — a plain download never re-assembles
/// this input and never re-reads them.
/// </summary>
public static class QuotePdfEndpoints
{
    /// <summary>Matches <see cref="GeneratedDocument.ReissueReason"/>'s own column length.</summary>
    private const int MaxReissueReasonLength = 2000;

    public static IEndpointRouteBuilder MapQuotePdfEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/quotes/{quoteId:guid}/revisions/{revisionId:guid}/pdf");

        // POST: materialize-or-return the CURRENT document. A real state mutation (a permanent,
        // immutable row may be created) — QuotingManage + CSRF, mirroring every other Quote
        // mutation. Never generates a second document while a current one already exists — that
        // is what /reissue below is for (mission §60-63/§37).
        group.MapPost("", async (Guid quoteId, Guid revisionId, HttpContext http, IAntiforgery antiforgery,
            UserManager<ApplicationUser> users, VerceDbContext db, BrandAssetStorage brandAssetStorage,
            QuotePdfService pdfService, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return Results.Problem(statusCode: 400, title: "Requisição inválida.");
            var actor = await users.GetUserAsync(http.User);
            if (actor is null) return Results.Unauthorized();

            var (revision, quote) = await QuotePdfInputBuilder.LoadRevisionAndQuoteAsync(db, quoteId, revisionId, ct);
            if (revision is null || quote is null) return Results.NotFound();

            GeneratedDocument document;
            try
            {
                var input = await QuotePdfInputBuilder.BuildAsync(db, brandAssetStorage, quote, revision, ct);
                document = await pdfService.GenerateOrGetCurrentAsync(input, actor.Id, DateTimeOffset.UtcNow, ct);
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("PDF_RENDER_", StringComparison.Ordinal))
            {
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Não foi possível gerar o PDF.",
                    extensions: new Dictionary<string, object?> { ["code"] = ex.Message });
            }

            return Results.Ok(ToMetadataResponse(document, quoteId, revisionId));
        }).RequireAuthorization(Permissions.QuotingManage)
          .Produces<QuotePdfMetadataResponse>()
          .Produces(StatusCodes.Status404NotFound)
          .Produces(StatusCodes.Status502BadGateway);

        // POST /reissue: ALWAYS renders and inserts a NEW document, even when a current one
        // already exists — for the operator's explicit "reemitir" action (mission §63/§37). The
        // previous current document is superseded, never mutated (ADR-0016 §5).
        group.MapPost("/reissue", async (Guid quoteId, Guid revisionId, QuotePdfReissueRequest? request, HttpContext http, IAntiforgery antiforgery,
            UserManager<ApplicationUser> users, VerceDbContext db, BrandAssetStorage brandAssetStorage,
            QuotePdfService pdfService, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return Results.Problem(statusCode: 400, title: "Requisição inválida.");
            var actor = await users.GetUserAsync(http.User);
            if (actor is null) return Results.Unauthorized();

            // F-03: a deliberate reissue always requires a non-blank "why" — never optional,
            // never a placeholder generated on the caller's behalf.
            var reason = request?.Reason?.Trim();
            if (string.IsNullOrEmpty(reason))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "O motivo da reemissão é obrigatório.",
                    extensions: new Dictionary<string, object?> { ["code"] = "DOCUMENT_REISSUE_REASON_REQUIRED" });
            }
            if (reason.Length > MaxReissueReasonLength)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "O motivo da reemissão é longo demais.",
                    extensions: new Dictionary<string, object?> { ["code"] = "DOCUMENT_REISSUE_REASON_TOO_LONG" });
            }

            var (revision, quote) = await QuotePdfInputBuilder.LoadRevisionAndQuoteAsync(db, quoteId, revisionId, ct);
            if (revision is null || quote is null) return Results.NotFound();

            GeneratedDocument document;
            try
            {
                var input = await QuotePdfInputBuilder.BuildAsync(db, brandAssetStorage, quote, revision, ct);
                document = await pdfService.ReissueAsync(input, actor.Id, DateTimeOffset.UtcNow, reason, ct);
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("PDF_RENDER_", StringComparison.Ordinal))
            {
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Não foi possível gerar o PDF.",
                    extensions: new Dictionary<string, object?> { ["code"] = ex.Message });
            }

            return Results.Ok(ToMetadataResponse(document, quoteId, revisionId));
        }).RequireAuthorization(Permissions.QuotingManage)
          .Produces<QuotePdfMetadataResponse>()
          .Produces(StatusCodes.Status400BadRequest)
          .Produces(StatusCodes.Status404NotFound)
          .Produces(StatusCodes.Status502BadGateway);

        // GET: download the CURRENT already-materialized artifact — never implicitly generates.
        // Quote read permission is sufficient: a Viewer may download, never generate/reissue.
        group.MapGet("", async (Guid quoteId, Guid revisionId, VerceDbContext db, QuotePdfService pdfService, CancellationToken ct) =>
        {
            var revisionExists = await db.Set<QuoteRevision>().AsNoTracking().AnyAsync(x => x.Id == revisionId && x.QuoteId == quoteId, ct);
            if (!revisionExists) return Results.NotFound();

            var document = await pdfService.FindCurrentAsync(revisionId, ct);
            if (document is null) return Results.NotFound();
            return await DownloadAsync(quoteId, revisionId, document, db, pdfService, ct);
        }).RequireAuthorization(Permissions.QuotingRead)
          .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
          .Produces(StatusCodes.Status404NotFound);

        // GET /history: every document ever issued for this revision, newest first (mission
        // §115) — the revision timeline's data source for historical-document selection.
        group.MapGet("/history", async (Guid quoteId, Guid revisionId, VerceDbContext db, QuotePdfService pdfService, CancellationToken ct) =>
        {
            var revisionExists = await db.Set<QuoteRevision>().AsNoTracking().AnyAsync(x => x.Id == revisionId && x.QuoteId == quoteId, ct);
            if (!revisionExists) return Results.NotFound();

            var documents = await pdfService.ListForRevisionAsync(revisionId, ct);
            var items = documents.Select(d => new QuotePdfHistoryItemResponse(d.Id, d.IssuedAt, d.IsCurrent, d.PdfSha256,
                $"/api/quotes/{quoteId}/revisions/{revisionId}/pdf/{d.Id}")).ToList();
            return Results.Ok(items);
        }).RequireAuthorization(Permissions.QuotingRead)
          .Produces<IReadOnlyList<QuotePdfHistoryItemResponse>>()
          .Produces(StatusCodes.Status404NotFound);

        // GET /{documentId}: download a SPECIFIC historical document — current or superseded
        // (mission §61/§116). Rendering/downloading is never a lifecycle transition, so this
        // works for a revision in ANY status.
        group.MapGet("/{documentId:guid}", async (Guid quoteId, Guid revisionId, Guid documentId, VerceDbContext db, QuotePdfService pdfService, CancellationToken ct) =>
        {
            var revisionExists = await db.Set<QuoteRevision>().AsNoTracking().AnyAsync(x => x.Id == revisionId && x.QuoteId == quoteId, ct);
            if (!revisionExists) return Results.NotFound();

            var document = await pdfService.FindByIdAsync(documentId, revisionId, ct);
            if (document is null) return Results.NotFound();
            return await DownloadAsync(quoteId, revisionId, document, db, pdfService, ct);
        }).RequireAuthorization(Permissions.QuotingRead)
          .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
          .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> DownloadAsync(Guid quoteId, Guid revisionId, GeneratedDocument document,
        VerceDbContext db, QuotePdfService pdfService, CancellationToken ct)
    {
        var quoteForFilename = await db.Set<global::Verce.Modules.Quoting.Quote>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == quoteId, ct);
        var revisionForFilename = await db.Set<QuoteRevision>().AsNoTracking().SingleAsync(x => x.Id == revisionId, ct);
        var displayNumber = quoteForFilename is null ? revisionId.ToString("N") : quoteForFilename.DisplayNumberFor(revisionForFilename);
        var fileName = SanitizeFileName($"orcamento-{displayNumber}.pdf");

        var bytes = await pdfService.ReadPdfBytesAsync(document, ct);
        return Results.File(bytes, "application/pdf", fileName);
    }

    private static QuotePdfMetadataResponse ToMetadataResponse(GeneratedDocument document, Guid quoteId, Guid revisionId) =>
        new(document.Id, document.SourceId, document.DocumentTypeCode, document.DocumentTemplateVersionId,
            document.PdfSha256, document.PdfSizeBytes, document.IssuedAt, document.IsCurrent,
            $"/api/quotes/{quoteId}/revisions/{revisionId}/pdf/{document.Id}");

    /// <summary>Sanitizes a Content-Disposition filename derived from operator-controlled display
    /// text (mission §41) — strips path separators/control characters, never trusts the input.</summary>
    private static string SanitizeFileName(string candidate)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(candidate.Where(c => !invalid.Contains(c) && c is not ('/' or '\\')).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "orcamento.pdf" : sanitized;
    }

    private static async Task<bool> IsValidCsrf(HttpContext http, IAntiforgery antiforgery)
    {
        try { await antiforgery.ValidateRequestAsync(http); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }
}
