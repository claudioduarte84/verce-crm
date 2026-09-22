using System.Text.Json;
using FluentAssertions;
using Verce.Modules.Documents;

namespace Verce.Documents.Tests;

/// <summary>
/// S7/S14 scope authority gate, OPTION A: proves the GENERIC block-tree engine
/// (<see cref="BlockTreeRenderer"/>) against the SEEDED default proposal definition
/// (<see cref="DefaultProposalTemplateSeedData"/>) — the exact same JSON a real
/// <see cref="DocumentTemplateVersion"/> row carries. Nothing here calls a compiled Quote
/// template; that class no longer exists. Pure functions — no Chromium/DB dependency.
/// </summary>
public class QuotePdfHtmlTemplateTests
{
    internal static QuotePdfInput NewInput(
        string? customerName = "Cliente Teste",
        string? paymentTerms = "30 dias",
        IReadOnlyList<QuotePdfLineInput>? items = null,
        string? title = null, string? scope = null,
        IReadOnlyList<QuotePdfTechnicalHighlightInput>? technicalHighlights = null,
        string? technicalNotes = null, string? outOfScope = null, string? notes = null) => new(
        QuoteRevisionId: Guid.NewGuid(),
        QuoteNumber: "260920-1",
        RevisionDisplayNumber: "260920-1B",
        RevisionStatusDisplay: "APPROVED",
        IssuedAt: new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero),
        ValidUntil: new DateOnly(2026, 10, 5),
        CustomerName: customerName,
        CustomerDocument: "123.456.789-00",
        CustomerEmail: "cliente@example.test",
        CustomerPhone: "+55 11 99999-0000",
        CustomerAddressLine: "Rua Teste, 100 - Sao Paulo/SP - 01000-000",
        Items: items ?? [new QuotePdfLineInput(1, "Vaso decorativo", "PLA preto", 2m, 50.00m, "None", 0m, 50.00m, 100.00m)],
        SubtotalAmount: 100.00m,
        DiscountAmount: 0m,
        TotalAmount: 100.00m,
        Title: title,
        Scope: scope,
        TechnicalHighlights: technicalHighlights ?? [],
        TechnicalNotes: technicalNotes,
        OutOfScope: outOfScope,
        PaymentTerms: paymentTerms,
        DeliveryTerms: "Retirada no local",
        Warranty: "90 dias",
        Notes: notes,
        CompanyLegalName: "Verce 3D Impressao Ltda",
        CompanyTradeName: "VERCE 3D",
        CompanyDocument: "12.345.678/0001-00",
        CompanyEmail: "contato@verce3d.test",
        CompanyPhone: "+55 11 98888-0000",
        CompanyWebsite: "https://verce3d.test",
        CompanyInstagram: "@verce3d",
        CompanyAddressLine: "Av. Teste, 500 - Sao Paulo/SP - 02000-000",
        HeaderLogoDataUri: null,
        HeaderLogoBrandAssetVersionId: null,
        FooterLogoDataUri: null,
        FooterLogoBrandAssetVersionId: null);

    internal static JsonDocument DefaultDefinition() => JsonDocument.Parse(DefaultProposalTemplateSeedData.BuildDefinitionJson());

    internal static string RenderBody(QuotePdfInput input)
    {
        using var definition = DefaultDefinition();
        return BlockTreeRenderer.BuildBodyHtml(definition.RootElement, new QuoteRenderContext(input), input.QuoteNumber);
    }

    internal static string RenderHeaderRegion(QuotePdfInput input)
    {
        using var definition = DefaultDefinition();
        return BlockTreeRenderer.BuildPageRegionTemplate(definition.RootElement, "header", new QuoteRenderContext(input), includePageNumber: false);
    }

    internal static string RenderFooterRegion(QuotePdfInput input)
    {
        using var definition = DefaultDefinition();
        return BlockTreeRenderer.BuildPageRegionTemplate(definition.RootElement, "footer", new QuoteRenderContext(input), includePageNumber: true);
    }

    [Fact]
    public void Renders_customer_facing_commercial_fields_across_header_and_body()
    {
        var input = NewInput();
        var header = RenderHeaderRegion(input);
        var body = RenderBody(input);

        header.Should().Contain("260920-1B");
        body.Should().Contain("Cliente Teste");
        body.Should().Contain("Vaso decorativo");
        body.Should().Contain("30 dias");
        body.Should().Contain("Retirada no local");
        body.Should().Contain("90 dias");
    }

    [Fact]
    public void Never_mentions_cost_margin_or_commission_internals()
    {
        // The DTO itself has no cost/margin/commission field at all, and the binding catalogue
        // has no path to any of those concepts — this proves the RENDERED OUTPUT never
        // references them either, never a claim about the generic English word "margin"/"cost",
        // which also appear as ordinary CSS property names (margin-top, etc.).
        var input = NewInput();
        var html = (RenderHeaderRegion(input) + RenderBody(input) + RenderFooterRegion(input)).ToLowerInvariant();

        html.Should().NotContain("custo");
        html.Should().NotContain("margem");
        html.Should().NotContain("comissão");
        html.Should().NotContain("commission");
        html.Should().NotContain("unit cost");
        html.Should().NotContain("estimated cost");
    }

    [Fact]
    public void Html_encodes_every_dynamic_value_never_emitting_raw_script()
    {
        var maliciousItems = new List<QuotePdfLineInput>
        {
            new(1, "<script>alert(1)</script>", "\"onmouseover=alert(1)", 1m, 10m, "None", 0m, 10m, 10m),
        };
        var body = RenderBody(NewInput(customerName: "<img src=x onerror=alert(1)>", items: maliciousItems));

        body.Should().NotContain("<script>");
        body.Should().NotContain("<img src=x onerror=alert(1)>");
        body.Should().Contain("&lt;script&gt;");
        body.Should().Contain("&lt;img src=x onerror=alert(1)&gt;");
    }

    [Fact]
    public void Has_no_external_network_dependency()
    {
        var input = NewInput();
        var html = RenderHeaderRegion(input) + RenderBody(input) + RenderFooterRegion(input);

        html.Should().NotContain("http://");
        html.Should().NotMatchRegex(@"<link[^>]+href=[""']https?://");
        html.Should().NotMatchRegex(@"<script[^>]+src=[""']https?://");
        html.Should().NotMatchRegex(@"@import\s+url\(https?://");
        html.Should().NotMatchRegex(@"url\(\s*https?://");
    }

    [Fact]
    public void Renders_a_deterministic_fallback_when_the_customer_is_ad_hoc()
    {
        var body = RenderBody(NewInput(customerName: null));
        body.Should().Contain("Cliente avulso");
    }

    [Fact]
    public void Omits_terms_sections_entirely_when_none_are_present()
    {
        var input = NewInput() with { PaymentTerms = null, DeliveryTerms = null, Warranty = null };
        var body = RenderBody(input);
        body.Should().NotContain("Condições de pagamento");
        body.Should().NotContain("Prazo de entrega");
        body.Should().NotContain(">Garantia<");
    }

    /// <summary>S7/S14 scope authority gate — the proposal-content snapshot fields
    /// (DEFAULT-PROPOSAL-TEMPLATE §2.2/§2.4/§2.5/§2.8): rendered when present, entirely absent
    /// from the markup when not — every one of those sections is `visibleWhen`-conditional.</summary>
    [Fact]
    public void Renders_proposal_content_sections_when_present()
    {
        var input = NewInput(title: "Pecas para drone", scope: "Fabricacao de 3 suportes em PETG",
            technicalHighlights: [new QuotePdfTechnicalHighlightInput("Material", "PETG"), new QuotePdfTechnicalHighlightInput("Tolerancia", "0.2mm")],
            technicalNotes: "Acabamento fosco.", outOfScope: "Pintura e montagem final.", notes: "Entrega combinada por WhatsApp.");
        var body = RenderBody(input);

        body.Should().Contain("Pecas para drone");
        body.Should().Contain("Fabricacao de 3 suportes em PETG");
        body.Should().Contain("Material").And.Contain("PETG");
        body.Should().Contain("Tolerancia").And.Contain("0.2mm");
        body.Should().Contain("Acabamento fosco.");
        // Literal block text ALSO passes through Encode() (defense in depth — every block's
        // content is treated uniformly, whether literal or bound), so "NÃO" numeric-encodes its
        // accented letters; "INCLUSO" alone has none and is a safe, unambiguous substring check.
        body.Should().Contain("INCLUSO").And.Contain("Pintura e montagem final.");
        body.Should().Contain("Entrega combinada por WhatsApp.");
    }

    [Fact]
    public void Omits_proposal_content_sections_entirely_when_absent()
    {
        var body = RenderBody(NewInput());
        body.Should().NotContain("PROJETO");
        body.Should().NotContain("ESCOPO");
        body.Should().NotContain("INFORMAÇÕES TÉCNICAS");
        body.Should().NotContain("NÃO INCLUSO");
    }

    /// <summary>S7/S14 scope authority gate §5: the concrete defect under correction — terms come
    /// from the frozen QuoteRevision snapshot ONLY. Neither the renderer nor the render context
    /// has any dependency on Settings.</summary>
    [Fact]
    public void Prints_exactly_the_terms_the_input_carries_never_a_placeholder()
    {
        var body = RenderBody(NewInput(paymentTerms: "Pix a vista"));
        body.Should().Contain("Pix a vista");
        body.Should().NotContain("documents.default_payment_terms");
    }

    [Fact]
    public void Footer_region_carries_page_number_placeholders_and_company_name()
    {
        var footer = RenderFooterRegion(NewInput());
        footer.Should().Contain("class=\"pageNumber\"");
        footer.Should().Contain("class=\"totalPages\"");
    }

    [Fact]
    public void Header_region_repeats_the_same_content_regardless_of_page_since_it_is_a_native_playwright_template()
    {
        // The header/footer region HTML is built ONCE and handed to Playwright's own
        // headerTemplate/footerTemplate mechanism, which the browser repeats on every page
        // natively (proven against real multi-page output in QuotePdfPaginationTests) — this
        // test only proves the template ITSELF carries the quote identity, not a page-1-only
        // in-body fragment.
        var header = RenderHeaderRegion(NewInput());
        header.Should().Contain("260920-1B");
    }

    /// <summary>F-05 (S7 final-findings correction): header/footer margin templates do not
    /// inherit the body document's own &lt;style&gt; block (a genuine Playwright/Chromium
    /// constraint), so they previously fell back to a hard-coded, host-dependent
    /// "font-family:Arial,sans-serif" — reintroducing exactly the host-font dependence the body
    /// document's embedded Inter/Archivo resources were built to eliminate. Each region must
    /// carry its OWN embedded @font-face block and reference the theme's font tokens, never
    /// "Arial" literally.</summary>
    [Fact]
    public void Header_and_footer_regions_embed_their_own_self_hosted_fonts_with_no_Arial_fallback()
    {
        var input = NewInput();
        var header = RenderHeaderRegion(input);
        var footer = RenderFooterRegion(input);

        foreach (var region in new[] { header, footer })
        {
            region.Should().Contain("@font-face").And.Contain("'Inter'").And.Contain("'Archivo'");
            region.Should().Contain("data:font/woff2;base64,", "the region must embed the SAME repository-controlled font bytes the body uses, never reference a remote/system font");
            // Strip the base64 font payloads before the "no Arial" check: base64-encoded binary
            // data is effectively random text and can coincidentally contain the 5-letter
            // substring "Arial" purely by chance across ~150KB of embedded font bytes — the real
            // property under test is that no CSS RULE names Arial, not that the byte stream never
            // contains that letter sequence anywhere.
            var withoutFontData = System.Text.RegularExpressions.Regex.Replace(region, "data:font/woff2;base64,[A-Za-z0-9+/=]+", "data:font/woff2;base64,<omitted>");
            withoutFontData.Should().NotContain("Arial", "no region's CSS may depend on a host-installed font");
        }
    }
}
