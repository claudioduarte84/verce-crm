namespace Verce.Modules.Documents;

/// <summary>
/// S7 §56: a strongly-typed, closed input for the built-in Quote PDF V1 template — never a
/// generic binding-expression/arbitrary-object-graph mechanism (that is S14 scope). Assembled
/// entirely by the composition root (Verce.Api) from the immutable QuoteRevision snapshot plus
/// Company Profile/Brand Asset/Settings data (mission §55: Documents never references Quoting/
/// Customers/Settings/Production). This is also exactly <c>GeneratedDocument.RenderDataSnapshotJson</c>
/// (ADR-0016 §1) — the fully-resolved render context, frozen verbatim at first materialization.
///
/// Deliberately excludes EVERY internal cost/margin/commission field (mission §32) — the renderer
/// physically cannot leak what this record does not carry.
/// </summary>
public sealed record QuotePdfInput(
    Guid QuoteRevisionId,
    string QuoteNumber,
    string RevisionDisplayNumber,
    string RevisionStatusDisplay,
    DateTimeOffset IssuedAt,
    DateOnly ValidUntil,
    string? CustomerName,
    string? CustomerDocument,
    string? CustomerEmail,
    string? CustomerPhone,
    string? CustomerAddressLine,
    IReadOnlyList<QuotePdfLineInput> Items,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TotalAmount,
    // S7/S14 scope authority gate §5 (OPTION A): every field below is read from the QuoteRevision
    // snapshot ONLY — frozen at issue, ADR-0016 §7 — and NEVER re-read from Settings/Company
    // Profile/Product at render time. A settings change after the revision was created must never
    // alter an already-issued (or not-yet-rendered) revision's document.
    string? Title,
    string? Scope,
    IReadOnlyList<QuotePdfTechnicalHighlightInput> TechnicalHighlights,
    string? TechnicalNotes,
    string? OutOfScope,
    string? PaymentTerms,
    string? DeliveryTerms,
    string? Warranty,
    string? Notes,
    string CompanyLegalName,
    string CompanyTradeName,
    string? CompanyDocument,
    string? CompanyEmail,
    string? CompanyPhone,
    string? CompanyWebsite,
    string? CompanyInstagram,
    string? CompanyAddressLine,
    string? HeaderLogoDataUri,
    Guid? HeaderLogoBrandAssetVersionId,
    string? FooterLogoDataUri,
    Guid? FooterLogoBrandAssetVersionId)
{
    /// <summary>ADR-0016 §5.1/§38 — every distinct resolved brand asset version actually used by
    /// this document, frozen together (never a single scalar: the default proposal resolves TWO
    /// logo roles, header and footer, which may legitimately differ).</summary>
    public IReadOnlyList<Guid> BrandAssetVersionIds =>
        new[] { HeaderLogoBrandAssetVersionId, FooterLogoBrandAssetVersionId }.Where(x => x is not null).Select(x => x!.Value).Distinct().ToArray();
}

/// <summary>One <c>{label, value}</c> technical-highlight pair (DOMAIN-MODEL §8 rule 1) —
/// presentation only, deliberately generic.</summary>
public sealed record QuotePdfTechnicalHighlightInput(string Label, string Value);

/// <summary>One customer-visible proposal line — price/quantity/discount/total only, exactly what
/// a customer already sees in the app's own pricing UI. No unit cost, no margin, no fee.</summary>
public sealed record QuotePdfLineInput(
    int LineNumber,
    string ProductNameSnapshot,
    string? Description,
    decimal Quantity,
    decimal UnitPrice,
    string DiscountKind,
    decimal DiscountAmount,
    decimal NetUnitPrice,
    decimal LineTotalAmount);
