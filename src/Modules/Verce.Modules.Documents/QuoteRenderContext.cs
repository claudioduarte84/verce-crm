namespace Verce.Modules.Documents;

/// <summary>
/// ADR-0007 §3: the QUOTE document type's <see cref="IRenderContext"/> implementation, resolving
/// every path in <see cref="QuoteBindingCatalogue"/> against an already-frozen
/// <see cref="QuotePdfInput"/> — no live commercial recalculation, no I/O, no cross-module
/// reference. Assembled by the composition root (mirrors exactly how <c>QuotePdfInputBuilder</c>
/// already assembles <see cref="QuotePdfInput"/> itself in <c>Verce.Api</c>), then handed to the
/// generic <see cref="BlockTreeRenderer"/> — which never sees this type, only the
/// <see cref="IRenderContext"/> interface.
/// </summary>
public sealed class QuoteRenderContext : IRenderContext
{
    private readonly QuotePdfInput _input;

    public QuoteRenderContext(QuotePdfInput input) => _input = input;

    public object? ResolveScalar(string path) => path switch
    {
        "company.name" => _input.CompanyTradeName,
        "company.legalName" => _input.CompanyLegalName,
        "company.document" => _input.CompanyDocument,
        "company.email" => _input.CompanyEmail,
        "company.phone" => _input.CompanyPhone,
        "company.website" => _input.CompanyWebsite,
        "company.instagram" => _input.CompanyInstagram,
        "company.whatsapp" => null, // not carried on QuotePdfInput yet — no seeded block binds it
        "company.address" => _input.CompanyAddressLine,

        "quote.number" => _input.RevisionDisplayNumber,
        "quote.date" => DateOnly.FromDateTime(_input.IssuedAt.UtcDateTime),
        "quote.validUntil" => _input.ValidUntil,
        "quote.title" => _input.Title,
        "quote.status" => _input.RevisionStatusDisplay,
        "quote.scope" => _input.Scope,
        "quote.notes" => _input.Notes,
        "quote.technicalNotes" => _input.TechnicalNotes,

        "customer.name" => _input.CustomerName ?? "Cliente avulso",
        "customer.document" => _input.CustomerDocument,
        "customer.contact" => _input.CustomerPhone ?? _input.CustomerEmail,
        "customer.email" => _input.CustomerEmail,
        "customer.phone" => _input.CustomerPhone,

        "subtotal" => _input.SubtotalAmount,
        "discount" => _input.DiscountAmount,
        "total" => _input.TotalAmount,

        "paymentTerms" => _input.PaymentTerms,
        "deliveryTerms" => _input.DeliveryTerms,
        "warranty" => _input.Warranty,
        "outOfScope" => _input.OutOfScope,

        _ => throw new InvalidOperationException($"{RenderErrorCodes.InvalidBinding}:{path}"),
    };

    public IReadOnlyList<IRenderContext> ResolveCollection(string path) => path switch
    {
        "items[]" => _input.Items.Select(i => (IRenderContext)new QuoteLineRenderContext(i)).ToList(),
        "quote.technicalHighlights[]" => _input.TechnicalHighlights.Select(h => (IRenderContext)new PairRenderContext(h.Label, h.Value)).ToList(),
        _ => throw new InvalidOperationException($"{RenderErrorCodes.InvalidBinding}:{path}"),
    };

    /// <summary>DEFAULT-PROPOSAL-TEMPLATE §2.1/§2.9: <c>INHERIT_DEFAULT</c> (null role) resolves
    /// the header logo; <c>SPECIFIC_ASSET -&gt; COMPACT_LOGO</c> resolves the footer logo — two
    /// independently-frozen <c>BrandAssetVersion</c>s (ADR-0016 §1/§6), never one scalar shared
    /// by both regions.</summary>
    public string? ResolveLogo(string? assetRole) => assetRole switch
    {
        null or "" => _input.HeaderLogoDataUri,
        "COMPACT_LOGO" => _input.FooterLogoDataUri,
        _ => throw new InvalidOperationException($"DOCUMENT_TEMPLATE_UNKNOWN_LOGO_ROLE:{assetRole}"),
    };

    /// <summary>Row-scoped context for one <c>items[]</c> element — resolves ONLY the
    /// <c>item.*</c> paths (mission §14: forbidden fields — unit cost, margin, commission, fee —
    /// are never carried on <see cref="QuotePdfLineInput"/> in the first place, so there is
    /// physically nothing here for a template to bind even if it tried).</summary>
    private sealed class QuoteLineRenderContext(QuotePdfLineInput item) : IRenderContext
    {
        public object? ResolveScalar(string path) => path switch
        {
            "item.name" => item.ProductNameSnapshot,
            "item.description" => item.Description,
            "item.quantity" => item.Quantity,
            "item.unitPrice" => item.UnitPrice,
            "item.discount" => item.DiscountAmount,
            "item.lineTotal" => item.LineTotalAmount,
            _ => throw new InvalidOperationException($"{RenderErrorCodes.InvalidBinding}:{path}"),
        };

        public IReadOnlyList<IRenderContext> ResolveCollection(string path) =>
            throw new InvalidOperationException($"{RenderErrorCodes.InvalidBinding}:{path}");

        public string? ResolveLogo(string? assetRole) => throw new InvalidOperationException("DOCUMENT_TEMPLATE_LOGO_NOT_VALID_IN_ROW_CONTEXT");
    }

    /// <summary>Row-scoped context for one <c>quote.technicalHighlights[]</c> pair — resolves
    /// only <c>label</c>/<c>value</c> (DOMAIN-MODEL §8 rule 1: deliberately generic).</summary>
    private sealed class PairRenderContext(string label, string value) : IRenderContext
    {
        public object? ResolveScalar(string path) => path switch
        {
            "label" => label,
            "value" => value,
            _ => throw new InvalidOperationException($"{RenderErrorCodes.InvalidBinding}:{path}"),
        };

        public IReadOnlyList<IRenderContext> ResolveCollection(string path) =>
            throw new InvalidOperationException($"{RenderErrorCodes.InvalidBinding}:{path}");

        public string? ResolveLogo(string? assetRole) => throw new InvalidOperationException("DOCUMENT_TEMPLATE_LOGO_NOT_VALID_IN_ROW_CONTEXT");
    }
}
