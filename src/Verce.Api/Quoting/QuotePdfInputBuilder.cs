using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verce.Modules.Documents;
using Verce.Modules.Quoting;
using Verce.Modules.Settings;
using Verce.Platform.Persistence;

namespace Verce.Api.Quoting;

/// <summary>
/// The composition root's shared assembly step for a Quote PDF render (CLAUDE.md rule 11:
/// Documents never references Quoting/Customers/Settings directly). Used identically by the
/// synchronous HTTP endpoints (<see cref="QuotePdfEndpoints"/>) and the
/// <c>GenerateQuotePdfRequested</c> outbox consumer — one build path, so the two trigger
/// mechanisms can never drift into producing different content for the same revision.
///
/// S7/S14 scope authority gate §5 (OPTION A): every proposal-content field, INCLUDING the three
/// terms, is read from the immutable <see cref="QuoteRevision"/> snapshot ONLY — never from
/// Settings at render time. A term was already resolved once, at revision creation
/// (<c>QuotingEndpoints.ResolveCreateProposalContentAsync</c>), and a settings change afterward
/// must never alter an already-created revision's document, rendered or not.
/// </summary>
public static class QuotePdfInputBuilder
{
    public static async Task<QuotePdfInput> BuildAsync(VerceDbContext db, BrandAssetStorage brandAssetStorage,
        global::Verce.Modules.Quoting.Quote quote, QuoteRevision revision, CancellationToken ct)
    {
        var (customerEmail, customerPhone) = ParseContactsSnapshot(revision.CustomerContactsSnapshot);
        var customerAddressLine = ParsePrimaryAddressLine(revision.CustomerAddressesSnapshot);

        var companyProfile = await db.Set<CompanyProfile>().AsNoTracking().SingleOrDefaultAsync(ct);
        // DEFAULT-PROPOSAL-TEMPLATE §2.1/§2.9: the header uses the DOCUMENT_DEFAULT_LOGO role; the
        // footer uses the more compact SYSTEM_LOGO_COMPACT role, falling back to the same default —
        // two independently-resolved, independently-frozen logo roles (ADR-0016 §1/§6), never one
        // scalar shared by both regions.
        var (headerLogoDataUri, headerLogoVersionId) = await ResolveLogoAsync(db, brandAssetStorage, ["DOCUMENT_DEFAULT_LOGO", "SYSTEM_LOGO"], ct);
        var (footerLogoDataUri, footerLogoVersionId) = await ResolveLogoAsync(db, brandAssetStorage, ["SYSTEM_LOGO_COMPACT", "DOCUMENT_DEFAULT_LOGO", "SYSTEM_LOGO"], ct);

        var items = revision.Items.OrderBy(i => i.LineNumber).Select(i => new QuotePdfLineInput(
            i.LineNumber, i.ProductNameSnapshot, i.Description, i.Quantity, i.UnitPrice,
            i.DiscountKind.ToString(), i.DiscountAmount, i.NetUnitPrice, i.LineTotalAmount)).ToList();

        var companyAddressLine = companyProfile is null ? null : FormatCompanyAddress(companyProfile);
        var highlights = ParseHighlights(revision.TechnicalHighlightsJson);

        return new QuotePdfInput(
            revision.Id, quote.Number, quote.DisplayNumberFor(revision), DisplayStatus(revision.Status),
            revision.IssuedAt, revision.ValidUntil,
            revision.CustomerNameSnapshot, revision.CustomerDocumentSnapshot, customerEmail, customerPhone, customerAddressLine,
            items, revision.SubtotalAmount, revision.DiscountAmount, revision.TotalAmount,
            revision.Title, revision.Scope, highlights, revision.TechnicalNotes, revision.OutOfScope,
            revision.PaymentTerms, revision.DeliveryTerms, revision.Warranty, revision.Notes,
            companyProfile?.LegalName ?? "VERCE 3D", companyProfile?.TradeName ?? "VERCE 3D",
            companyProfile?.Document, companyProfile?.Email, companyProfile?.Phone, companyProfile?.Website, companyProfile?.Instagram,
            companyAddressLine, headerLogoDataUri, headerLogoVersionId, footerLogoDataUri, footerLogoVersionId);
    }

    public static async Task<(QuoteRevision? Revision, global::Verce.Modules.Quoting.Quote? Quote)> LoadRevisionAndQuoteAsync(
        VerceDbContext db, Guid quoteId, Guid revisionId, CancellationToken ct)
    {
        var revision = await db.Set<QuoteRevision>().AsNoTracking()
            .Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == revisionId && x.QuoteId == quoteId, ct);
        if (revision is null) return (null, null);
        var quote = await db.Set<global::Verce.Modules.Quoting.Quote>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == quoteId, ct);
        return (revision, quote);
    }

    private static IReadOnlyList<QuotePdfTechnicalHighlightInput> ParseHighlights(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var pairs = JsonSerializer.Deserialize<List<TechnicalHighlightPairDto>>(json);
            return pairs?.Select(p => new QuotePdfTechnicalHighlightInput(p.Label, p.Value)).ToList() ?? [];
        }
        catch (JsonException) { return []; }
    }

    private sealed record TechnicalHighlightPairDto(string Label, string Value);

    /// <summary>CLAUDE.md §1: the product UI's primary language is pt-BR, and this status badge
    /// is customer-facing (unlike the raw <c>QuoteRevisionStatus</c> enum shown to operators in
    /// the internal SPA) — so it is never the bare English enum name.</summary>
    private static string DisplayStatus(QuoteRevisionStatus status) => status switch
    {
        QuoteRevisionStatus.GENERATED => "Gerado",
        QuoteRevisionStatus.SENT => "Enviado",
        QuoteRevisionStatus.NEGOTIATING => "Em negociação",
        QuoteRevisionStatus.APPROVED => "Aprovado",
        QuoteRevisionStatus.CANCELED => "Cancelado",
        QuoteRevisionStatus.EXPIRED => "Expirado",
        QuoteRevisionStatus.SUPERSEDED => "Substituído",
        _ => "Status indisponível", // mission §74: never leak a raw/unknown future enum string onto a customer-facing document.
    };

    /// <summary>Which asset is "the" document logo is decided by <see cref="BrandingAssignment"/>
    /// (a role -> asset pointer, exactly one assignment per role), never by an asset's own
    /// <see cref="BrandAsset.BrandAssetTypeCode"/> — that code is just a category (multiple named
    /// assets can freely share it), so filtering by it directly is neither unique nor
    /// authoritative. Mirrors the exact role/asset join `GET /api/branding` already uses
    /// (<c>SettingsEndpoints.cs</c>), trying each role in order and falling back to the next.</summary>
    private static async Task<(string? DataUri, Guid? VersionId)> ResolveLogoAsync(VerceDbContext db, BrandAssetStorage brandAssetStorage, IReadOnlyList<string> roles, CancellationToken ct)
    {
        foreach (var role in roles)
        {
            var assignment = await db.Set<BrandingAssignment>().AsNoTracking().SingleOrDefaultAsync(a => a.Role == role, ct);
            if (assignment is null) continue;
            var asset = await db.Set<BrandAsset>().AsNoTracking()
                .SingleOrDefaultAsync(a => a.Id == assignment.BrandAssetId && a.IsActive && a.DeletedAt == null && a.CurrentVersionId != null, ct);
            if (asset?.CurrentVersionId is not { } versionId) continue;
            var version = await db.Set<BrandAssetVersion>().AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, ct);
            if (version is null) continue;
            try
            {
                await using var stream = await brandAssetStorage.OpenReadAsync(version.FilePath, ct);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                var base64 = Convert.ToBase64String(buffer.ToArray());
                return ($"data:{version.ContentType};base64,{base64}", version.Id);
            }
            catch (FileNotFoundException) { /* deterministic fallback: try the next candidate, then no logo */ }
        }
        return (null, null);
    }

    private static (string? Email, string? Phone) ParseContactsSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null);
        try
        {
            var dto = JsonSerializer.Deserialize<CustomerContactsSnapshotDto>(json);
            return (NullIfEmpty(dto?.Email), NullIfEmpty(dto?.Phone));
        }
        catch (JsonException) { return (null, null); }
    }

    private static string? ParsePrimaryAddressLine(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var addresses = JsonSerializer.Deserialize<List<CustomerAddressSnapshotDto>>(json);
            var chosen = addresses?.FirstOrDefault(a => a.IsPrimary) ?? addresses?.FirstOrDefault();
            if (chosen is null) return null;
            var line = $"{chosen.Street}, {chosen.Number}" + (string.IsNullOrWhiteSpace(chosen.Complement) ? "" : $" - {chosen.Complement}")
                + $" · {chosen.District}, {chosen.City}/{chosen.State} · {chosen.ZipCode}";
            return line;
        }
        catch (JsonException) { return null; }
    }

    private static string? FormatCompanyAddress(CompanyProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Street)) return null;
        var line = $"{profile.Street}, {profile.Number}" + (string.IsNullOrWhiteSpace(profile.Complement) ? "" : $" - {profile.Complement}");
        if (!string.IsNullOrWhiteSpace(profile.City)) line += $" · {profile.City}/{profile.State}";
        if (!string.IsNullOrWhiteSpace(profile.ZipCode)) line += $" · {profile.ZipCode}";
        return line;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record CustomerContactsSnapshotDto(string? Email, string? Phone);
    private sealed record CustomerAddressSnapshotDto(string Label, string ZipCode, string Street, string Number, string? Complement,
        string District, string City, string State, string Country, bool IsPrimary, bool IsDefaultShipping);
}
