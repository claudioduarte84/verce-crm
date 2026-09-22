using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Verce.Modules.Documents;

/// <summary>
/// ADR-0007 §3/§7 (S7/S14 scope authority gate, OPTION A): the generic block-tree walker.
/// Consumes ONLY a <c>definition</c> jsonb document (block tree + theme), a <c>page_setup</c>
/// jsonb document, and an <see cref="IRenderContext"/> — it has ZERO knowledge of Quoting,
/// Customers, Settings or Production. The default VERCE proposal is DATA fed through this same
/// path as any future template would be; nothing about it is compiled in here.
///
/// Every dynamic value passes through <see cref="Encode"/> (SECURITY §8) or
/// <see cref="RichTextSanitizer"/>; there is no raw-HTML block (ADR-0007 §1). A block whose
/// <c>visibleWhen</c> is unsatisfied emits NOTHING — not even its own markup shell.
/// </summary>
public static class BlockTreeRenderer
{
    /// <summary>Renders the BODY region (everything between the repeating header/footer) as a
    /// complete, self-contained HTML document — the theme's CSS custom properties come from
    /// <c>definition.theme</c>, never a hardcoded palette.</summary>
    public static string BuildBodyHtml(JsonElement definition, IRenderContext context, string title)
    {
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var theme = definition.TryGetProperty("theme", out var themeNode) ? themeNode : default;
        var html = new StringBuilder();

        html.Append("<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><title>")
            .Append(Encode(title)).Append("</title><style>").Append(BuildThemeCss(theme)).Append("</style></head><body><div class=\"sheet\">");

        if (definition.TryGetProperty("body", out var bodyNode) && bodyNode.ValueKind == JsonValueKind.Array)
            foreach (var block in bodyNode.EnumerateArray())
                html.Append(RenderBodyBlock(block, context, culture));

        html.Append("</div></body></html>");
        return html.ToString();
    }

    /// <summary>Renders one repeating page region (header or footer) as a Playwright
    /// header/footer template — a constrained context (Playwright margin templates do NOT
    /// inherit the body document's own <c>&lt;style&gt;</c> block; every rule must live inside
    /// THIS fragment) that repeats on EVERY page natively (ADR-0007 §3.3). This is the actual
    /// mechanism behind a genuinely repeating document header, not merely in-flow page-1 content.
    ///
    /// F-05 (S7 final-findings correction): carries its OWN self-hosted <c>@font-face</c> block
    /// (the SAME embedded Inter/Archivo resources the body uses — see
    /// <see cref="FontFaceCss"/>/Fonts/README.md) rather than a host-dependent
    /// <c>Arial,sans-serif</c> fallback, and resolves its family names from the SAME theme tokens
    /// (<c>fontHeading</c>/<c>fontBody</c>) the body document reads — never hard-coded here.</summary>
    public static string BuildPageRegionTemplate(JsonElement definition, string regionKey, IRenderContext context, bool includePageNumber)
    {
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var theme = definition.TryGetProperty("theme", out var themeNode) ? themeNode : default;
        var fontBody = ThemeToken(theme, "fontBody", "'Inter',sans-serif");
        var fontHeading = ThemeToken(theme, "fontHeading", "'Archivo',sans-serif");
        var text = ThemeToken(theme, "colorText", "#14181D");

        if (!definition.TryGetProperty(regionKey, out var region) || region.ValueKind != JsonValueKind.Object)
            return "<span></span>";

        var blocksHtml = new StringBuilder();
        if (region.TryGetProperty("blocks", out var blocksNode) && blocksNode.ValueKind == JsonValueKind.Array)
            foreach (var block in blocksNode.EnumerateArray())
                blocksHtml.Append(RenderRegionBlock(block, context, culture, fontHeading));

        return $$"""
            <style>{{FontFaceCss}}</style>
            <div style="width:100%; font-size:8pt; font-family:{{fontBody}}; color:{{text}}; padding:0 14mm; display:flex; justify-content:space-between; align-items:center; box-sizing:border-box;">{{blocksHtml}}</div>
            """;
    }

    private static string ThemeToken(JsonElement theme, string key, string fallback) =>
        theme.ValueKind == JsonValueKind.Object && theme.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;

    /// <summary>
    /// F-02 (S7 final-findings correction) — the single high-level entry point that turns a
    /// resolved template + render context into REAL PDF bytes, with <c>page_setup</c> fully
    /// authoritative: size, orientation and BOTH regions' <c>repeatOn</c> (<c>ALL</c>,
    /// <c>FIRST_ONLY</c>, <c>ALL_EXCEPT_FIRST</c>, <c>NONE</c>) all genuinely drive the output —
    /// never accepted-and-silently-ignored. <c>FIRST_ONLY</c>/<c>ALL_EXCEPT_FIRST</c> cannot be
    /// expressed through Playwright/Chromium's header/footer template mechanism alone (it repeats
    /// ONE static template on every page — there is no per-page-conditional hook in the installed
    /// API), so when either region needs one of those two modes this renders TWO real, independent
    /// Chromium print passes — one with the page-1 header/footer content, one with the
    /// subsequent-pages content — and splices page 1 of the first onto pages 2..N of the second
    /// (<see cref="PdfPageMerger"/>). When neither region needs those modes (the common case,
    /// including the seeded default proposal, which uses ALL for both) exactly one pass runs —
    /// no rendering-cost regression for the ordinary case.
    /// </summary>
    public static async Task<RenderedDocument> RenderDocumentAsync(
        JsonElement definition, JsonElement pageSetup, IRenderContext context, string title, IHtmlToPdfRenderer renderer, CancellationToken cancellationToken)
    {
        var bodyHtml = BuildBodyHtml(definition, context, title);
        var headerHtml = BuildPageRegionTemplate(definition, "header", context, includePageNumber: false);
        var footerHtml = BuildPageRegionTemplate(definition, "footer", context, includePageNumber: true);

        var headerRepeat = RepeatOn(pageSetup, "header");
        var footerRepeat = RepeatOn(pageSetup, "footer");

        if (!NeedsTwoPasses(headerRepeat) && !NeedsTwoPasses(footerRepeat))
        {
            var options = BuildRenderOptions(pageSetup,
                headerHtml: headerRepeat == "NONE" ? null : headerHtml,
                footerHtml: footerRepeat == "NONE" ? null : footerHtml);
            var single = await renderer.RenderAsync(bodyHtml, options, cancellationToken);
            return new RenderedDocument(bodyHtml, single);
        }

        var firstPageOptions = BuildRenderOptions(pageSetup,
            headerHtml: VisibleOnPass(headerRepeat, isFirstPage: true) ? headerHtml : null,
            footerHtml: VisibleOnPass(footerRepeat, isFirstPage: true) ? footerHtml : null);
        var subsequentPagesOptions = BuildRenderOptions(pageSetup,
            headerHtml: VisibleOnPass(headerRepeat, isFirstPage: false) ? headerHtml : null,
            footerHtml: VisibleOnPass(footerRepeat, isFirstPage: false) ? footerHtml : null);

        var firstPagePass = await renderer.RenderAsync(bodyHtml, firstPageOptions, cancellationToken);
        var subsequentPagesPass = await renderer.RenderAsync(bodyHtml, subsequentPagesOptions, cancellationToken);
        var merged = PdfPageMerger.MergeFirstPageWithRest(firstPagePass.Bytes, subsequentPagesPass.Bytes);
        return new RenderedDocument(bodyHtml, firstPagePass with { Bytes = merged });
    }

    /// <summary>Bundles the exact HTML that was rendered (for content-addressed HTML storage)
    /// alongside the resulting PDF — the caller must never separately rebuild the body HTML for
    /// hashing, which could in principle diverge from what actually became the PDF.</summary>
    public sealed record RenderedDocument(string BodyHtml, PdfRenderResult Pdf);

    private static bool NeedsTwoPasses(string repeatOn) => repeatOn is "FIRST_ONLY" or "ALL_EXCEPT_FIRST";

    private static bool VisibleOnPass(string repeatOn, bool isFirstPage) => repeatOn switch
    {
        "ALL" => true,
        "NONE" => false,
        "FIRST_ONLY" => isFirstPage,
        "ALL_EXCEPT_FIRST" => !isFirstPage,
        _ => true, // DocumentTemplateValidator already rejects any other value before this can run
    };

    /// <summary>F-02: page size/orientation are read here, never hard-coded — exactly one of a
    /// named <c>Format</c> or a custom <c>WidthMm</c>/<c>HeightMm</c> pair is produced, matching
    /// whichever shape <see cref="DocumentTemplateValidator"/> already required <c>size</c> to
    /// have. <paramref name="headerHtml"/>/<paramref name="footerHtml"/> being <c>null</c> means
    /// "not shown on this pass" — the caller (<see cref="RenderDocumentAsync"/>) has already
    /// resolved that from the region's own <c>repeatOn</c>.</summary>
    /// <summary>A4/LETTER's own millimetre dimensions — resolved to EXPLICIT width/height here
    /// rather than handed to Playwright as a named <c>Format</c> string alongside its
    /// <c>Landscape</c> flag: that combination was proven, against a real rendered PDF's own
    /// MediaBox, NOT to swap dimensions reliably. Explicit width/height is unambiguous by
    /// construction — there is no engine-internal interaction left to depend on.</summary>
    private static readonly Dictionary<string, (decimal WidthMm, decimal HeightMm)> NamedSizesMm = new(StringComparer.Ordinal)
    {
        ["A4"] = (210m, 297m),
        ["LETTER"] = (215.9m, 279.4m),
    };

    public static PdfRenderOptions BuildRenderOptions(JsonElement pageSetup, string? headerHtml, string? footerHtml)
    {
        string Margin(string key, string fallback) =>
            pageSetup.TryGetProperty("margin", out var m) && m.TryGetProperty(key, out var v) ? $"{v.GetDecimal().ToString(CultureInfo.InvariantCulture)}mm" : fallback;

        var size = pageSetup.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "A4";
        var landscape = pageSetup.TryGetProperty("orientation", out var o) && o.ValueKind == JsonValueKind.String && o.GetString() == "LANDSCAPE";

        var (width, height) = size == "CUSTOM"
            ? (pageSetup.GetProperty("widthMm").GetDecimal(), pageSetup.GetProperty("heightMm").GetDecimal())
            : NamedSizesMm[size]; // DocumentTemplateValidator already rejects any other "size" value.
        if (landscape) (width, height) = (height, width);

        return new PdfRenderOptions(
            HeaderHtml: headerHtml,
            FooterHtml: footerHtml,
            MarginTop: Margin("top", "22mm"),
            MarginBottom: Margin("bottom", "18mm"),
            MarginLeft: Margin("left", "14mm"),
            MarginRight: Margin("right", "14mm"),
            Format: null,
            WidthMm: $"{width.ToString(CultureInfo.InvariantCulture)}mm",
            HeightMm: $"{height.ToString(CultureInfo.InvariantCulture)}mm",
            Landscape: false); // already baked into the explicit width/height swap above
    }

    private static string RepeatOn(JsonElement pageSetup, string regionKey) =>
        pageSetup.TryGetProperty(regionKey, out var region) && region.TryGetProperty("repeatOn", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()!
            : "ALL";

    // ---- body-region rendering (full CSS class vocabulary, styled via <style>/theme) ----

    private static string RenderBodyBlock(JsonElement block, IRenderContext context, CultureInfo culture)
    {
        if (!IsVisible(block, context)) return string.Empty;
        var type = RequireType(block);
        return type switch
        {
            "Logo" => string.Empty, // the seeded body never places a Logo outside header/footer regions
            "Text" => RenderText(block),
            "Header" => RenderSectionHeader(block),
            "DynamicField" => RenderDynamicField(block, context, culture, wrap: "p"),
            "RichText" => RenderRichText(block, context),
            "TechnicalHighlight" => RenderTechnicalHighlight(block, context),
            "ItemsTable" => RenderItemsTable(block, context, culture),
            "Totals" => RenderTotals(block, context, culture),
            "PageNumber" => string.Empty, // page numbers belong to the footer region, not the body
            _ => throw new InvalidOperationException($"DOCUMENT_TEMPLATE_UNKNOWN_BLOCK_TYPE:{type}"),
        };
    }

    private static string RenderRegionBlock(JsonElement block, IRenderContext context, CultureInfo culture, string fontHeading)
    {
        if (!IsVisible(block, context)) return string.Empty;
        var type = RequireType(block);
        return type switch
        {
            "Logo" => RenderRegionLogo(block, context),
            // F-05: the region's own identity text (e.g. the quote number in the header) is a
            // heading-weight element — it uses the SAME theme-driven fontHeading family the body
            // document's h1/h2/h3 use, never a bare inline font-weight over the region's default
            // (fontBody) family.
            "Text" => $"""<span style="font-weight:700; font-family:{Encode(fontHeading)};">{Encode(RequireString(block, "text"))}</span>""",
            "DynamicField" => RenderRegionDynamicField(block, context, culture),
            "PageNumber" => """<span><span class="pageNumber"></span>&nbsp;/&nbsp;<span class="totalPages"></span></span>""",
            _ => string.Empty, // header/footer regions only ever need the small family above
        };
    }

    private static string RenderText(JsonElement block)
    {
        var style = block.TryGetProperty("style", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : "body";
        return $"""<div class="text text--{Encode(style!)}">{Encode(RequireString(block, "text"))}</div>""";
    }

    private static string RenderSectionHeader(JsonElement block) =>
        $"""<h3 class="section-header">{Encode(RequireString(block, "text"))}</h3>""";

    private static string RenderDynamicField(JsonElement block, IRenderContext context, CultureInfo culture, string wrap)
    {
        var path = RequireString(block, "binding");
        var (kind, _) = ResolveDescriptor(path);
        var value = context.ResolveScalar(path);
        var formatted = FormatValue(value, kind, culture);
        if (string.IsNullOrEmpty(formatted)) return string.Empty;
        return $"""<{wrap} class="field">{Encode(formatted)}</{wrap}>""";
    }

    private static string RenderRichText(JsonElement block, IRenderContext context)
    {
        var path = RequireString(block, "binding");
        var value = context.ResolveScalar(path) as string;
        var safe = RichTextSanitizer.ToSafeHtml(value);
        return string.IsNullOrEmpty(safe) ? string.Empty : $"""<div class="rich-text">{safe}</div>""";
    }

    private static string RenderTechnicalHighlight(JsonElement block, IRenderContext context)
    {
        var path = RequireString(block, "binding");
        var rows = context.ResolveCollection(path);
        if (rows.Count == 0) return string.Empty;
        var html = new StringBuilder("""<div class="highlights">""");
        foreach (var row in rows)
        {
            var label = row.ResolveScalar("label") as string ?? string.Empty;
            var value = row.ResolveScalar("value") as string ?? string.Empty;
            html.Append($"""<div class="pair"><div class="label">{Encode(label)}</div><div class="value">{Encode(value)}</div></div>""");
        }
        html.Append("</div>");
        return html.ToString();
    }

    private static string RenderItemsTable(JsonElement block, IRenderContext context, CultureInfo culture)
    {
        var path = RequireString(block, "binding");
        var rows = context.ResolveCollection(path);
        if (!block.TryGetProperty("columns", out var columnsNode) || columnsNode.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("DOCUMENT_TEMPLATE_ITEMS_TABLE_COLUMNS_REQUIRED");
        var columns = columnsNode.EnumerateArray().ToList();

        if (rows.Count == 0)
        {
            var emptyBehavior = block.TryGetProperty("emptyBehavior", out var eb) && eb.ValueKind == JsonValueKind.String ? eb.GetString() : "HIDE_BLOCK";
            return emptyBehavior == "SHOW_EMPTY_MESSAGE" ? """<p class="items-empty">Nenhum item.</p>""" : string.Empty;
        }

        var html = new StringBuilder("""<table class="items"><thead><tr>""");
        foreach (var column in columns)
        {
            var align = column.TryGetProperty("align", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() == "right" ? " class=\"num\"" : string.Empty;
            html.Append($"<th{align}>{Encode(RequireString(column, "label"))}</th>");
        }
        html.Append("</tr></thead><tbody>");

        foreach (var row in rows)
        {
            html.Append("<tr>");
            foreach (var column in columns)
            {
                var colBinding = RequireString(column, "binding");
                var align = column.TryGetProperty("align", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() == "right" ? " class=\"num\"" : string.Empty;
                var format = column.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                var value = row.ResolveScalar(colBinding);
                var text = FormatColumnValue(value, format, culture);
                html.Append($"<td{align}>{Encode(text)}</td>");
            }
            html.Append("</tr>");
        }
        html.Append("</tbody></table>");
        return html.ToString();
    }

    private static string RenderTotals(JsonElement block, IRenderContext context, CultureInfo culture)
    {
        if (!block.TryGetProperty("rows", out var rowsNode) || rowsNode.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("DOCUMENT_TEMPLATE_TOTALS_ROWS_REQUIRED");

        var html = new StringBuilder("""<div class="totals"><table>""");
        var any = false;
        foreach (var row in rowsNode.EnumerateArray())
        {
            if (!IsVisible(row, context)) continue;
            any = true;
            var path = RequireString(row, "binding");
            var (kind, _) = ResolveDescriptor(path);
            var value = context.ResolveScalar(path);
            var formatted = FormatValue(value, kind, culture);
            var isTotal = row.TryGetProperty("emphasis", out var em) && em.ValueKind == JsonValueKind.True;
            var rowClass = isTotal ? " class=\"total\"" : "";
            html.Append($"""<tr{rowClass}><td class="label">{Encode(RequireString(row, "label"))}</td><td class="value">{Encode(formatted)}</td></tr>""");
        }
        html.Append("</table></div>");
        return any ? html.ToString() : string.Empty;
    }

    private static string RenderRegionLogo(JsonElement block, IRenderContext context)
    {
        var source = block.TryGetProperty("logoSource", out var ls) && ls.ValueKind == JsonValueKind.String ? ls.GetString() : "INHERIT_DEFAULT";
        var role = source == "SPECIFIC_ASSET" && block.TryGetProperty("assetRole", out var ar) ? ar.GetString() : null;
        var dataUri = context.ResolveLogo(role);
        return string.IsNullOrEmpty(dataUri) ? string.Empty : $"""<img src="{Encode(dataUri)}" style="max-height:14mm; max-width:50mm; object-fit:contain;" alt="">""";
    }

    private static string RenderRegionDynamicField(JsonElement block, IRenderContext context, CultureInfo culture)
    {
        var path = RequireString(block, "binding");
        var (kind, _) = ResolveDescriptor(path);
        var value = context.ResolveScalar(path);
        var formatted = FormatValue(value, kind, culture);
        return string.IsNullOrEmpty(formatted) ? string.Empty : $"<span>{Encode(formatted)}</span>";
    }

    // ---- shared helpers ----

    private static bool IsVisible(JsonElement block, IRenderContext context) =>
        VisibleWhenEvaluator.Evaluate(block.TryGetProperty("visibleWhen", out var vw) ? vw : null, context);

    private static string RequireType(JsonElement block) => RequireString(block, "type");

    private static string RequireString(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"DOCUMENT_TEMPLATE_MALFORMED_BLOCK:{property}");
        return value.GetString()!;
    }

    /// <summary>Publish-time (and defense-in-depth render-time) binding validation
    /// (ADR-0007 §3): an unknown path never silently renders blank.</summary>
    private static (BindingKind Kind, string Label) ResolveDescriptor(string path)
    {
        if (QuoteBindingCatalogue.Scalars.TryGetValue(path, out var descriptor)) return (descriptor.Kind, descriptor.Label);
        throw new InvalidOperationException($"{RenderErrorCodes.InvalidBinding}:{path}");
    }

    private static string FormatValue(object? value, BindingKind kind, CultureInfo culture) => (value, kind) switch
    {
        (null, _) => string.Empty,
        (string s, _) => s,
        (decimal d, BindingKind.Currency) => d.ToString("C2", culture),
        (decimal d, _) => d.ToString("N2", culture),
        (DateOnly date, _) => date.ToString("dd/MM/yyyy", culture),
        (DateTimeOffset dt, _) => dt.ToString("dd/MM/yyyy", culture),
        (bool b, _) => b ? "Sim" : "Não",
        _ => value.ToString() ?? string.Empty,
    };

    private static string FormatColumnValue(object? value, string? format, CultureInfo culture) => (value, format) switch
    {
        (null, _) => string.Empty,
        (decimal d, "MONEY") => d.ToString("C2", culture),
        (decimal d, "QUANTITY") => d == Math.Floor(d) ? d.ToString("N0", culture) : d.ToString("N2", culture),
        (string s, _) => s,
        (decimal d, _) => d.ToString("N2", culture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Fonts/README.md: self-hosted, embedded-in-the-assembly @font-face sources (Inter
    /// 400/700, Archivo 700, SIL OFL 1.1) — read once per process and reused, since Chromium
    /// blocks every outbound request the rendered page makes (HtmlToPdfRenderer's RouteAsync
    /// abort-all), so a CDN `@import`/`&lt;link&gt;` would silently fall back to an unproven
    /// system font. Base64 data URIs keep the whole rendered document self-contained, exactly
    /// like its embedded logo data URIs.</summary>
    private static readonly string FontFaceCss = BuildFontFaceCss();

    private static string BuildFontFaceCss()
    {
        string DataUri(string logicalName)
        {
            var assembly = typeof(BlockTreeRenderer).Assembly;
            using var stream = assembly.GetManifestResourceStream($"Verce.Modules.Documents.Fonts.{logicalName}")
                ?? throw new InvalidOperationException($"DOCUMENT_TEMPLATE_FONT_RESOURCE_MISSING:{logicalName}");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return $"data:font/woff2;base64,{Convert.ToBase64String(buffer.ToArray())}";
        }

        var interRegular = DataUri("Inter-Regular.woff2");
        var interBold = DataUri("Inter-Bold.woff2");
        var archivoBold = DataUri("Archivo-Bold.woff2");

        return $$"""
            @font-face { font-family: 'Inter'; font-style: normal; font-weight: 400; src: url({{interRegular}}) format('woff2'); }
            @font-face { font-family: 'Inter'; font-style: normal; font-weight: 700; src: url({{interBold}}) format('woff2'); }
            @font-face { font-family: 'Archivo'; font-style: normal; font-weight: 700; src: url({{archivoBold}}) format('woff2'); }
            """;
    }

    private static string BuildThemeCss(JsonElement theme)
    {
        var primary = ThemeToken(theme, "colorPrimary", "#C2410C");
        var text = ThemeToken(theme, "colorText", "#14181D");
        var muted = ThemeToken(theme, "colorMuted", "#55606B");
        var rule = ThemeToken(theme, "colorRule", "#DDD8CF");
        var fontHeading = ThemeToken(theme, "fontHeading", "'Archivo',sans-serif");
        var fontBody = ThemeToken(theme, "fontBody", "'Inter',sans-serif");

        return $$"""
            {{FontFaceCss}}
            /* F-02 (S7 final-findings correction): "size" is deliberately NOT set here — this
               previously hard-coded "@page { size: A4; ... }" silently fought the Playwright-level
               Width/Height PagePdfOptions BuildRenderOptions computes from the template's own
               page_setup, and won for at least the orientation swap (proven against a real
               rendered PDF's own MediaBox: LANDSCAPE requests were rendering as portrait). Page
               geometry is authoritative from page_setup alone, resolved in exactly one place
               (BuildRenderOptions) — this stylesheet only ever zeroes the BROWSER's own default
               print margin so PagePdfOptions.Margin is the sole source of margins, never doubled
               up with a second, CSS-level margin. */
            @page { margin: 0; }
            * { box-sizing: border-box; }
            body { font-family: {{fontBody}}; color: {{text}}; background: #F2F0EC; margin: 0; font-size: 10.5pt; line-height: 1.5; }
            h1, h2, h3 { font-family: {{fontHeading}}; font-weight: 700; margin: 0; color: {{text}}; }
            .sheet { background: #FFFFFF; padding: 0; }
            .section-header { font-size: 9pt; text-transform: uppercase; letter-spacing: .04em; color: {{primary}}; margin: 6mm 0 2mm 0; break-inside: avoid; break-after: avoid; }
            .text--title { font-size: 15pt; font-weight: 700; }
            .text--section { font-size: 11pt; font-weight: 700; }
            .text--body { font-size: 10.5pt; }
            .field { margin: 0 0 1mm 0; }
            .rich-text { margin: 0 0 4mm 0; }
            .rich-text p { margin: 0 0 2mm 0; }
            .highlights { display: grid; grid-template-columns: 1fr 1fr; gap: 2mm 8mm; margin-bottom: 4mm; }
            .highlights .pair { border-bottom: 1px solid {{rule}}; padding-bottom: 1mm; break-inside: avoid; }
            .highlights .pair .label { color: {{muted}}; font-size: 8.5pt; text-transform: uppercase; letter-spacing: .03em; }
            table.items { width: 100%; border-collapse: collapse; margin-bottom: 6mm; }
            table.items thead { display: table-header-group; }
            table.items th { text-align: left; font-size: 8.5pt; text-transform: uppercase; letter-spacing: .03em; color: {{muted}}; border-bottom: 2px solid {{text}}; padding: 2mm; }
            table.items td { padding: 2.5mm 2mm; border-bottom: 1px solid {{rule}}; vertical-align: top; }
            table.items tr { break-inside: avoid; }
            table.items th.num, table.items td.num { text-align: right; white-space: nowrap; }
            .totals { display: flex; justify-content: flex-end; margin-bottom: 8mm; break-inside: avoid; break-before: avoid; }
            .totals table { border-collapse: collapse; min-width: 70mm; }
            .totals td { padding: 1.5mm 2mm; }
            .totals td.label { color: {{muted}}; text-align: right; }
            .totals td.value { text-align: right; font-variant-numeric: tabular-nums; min-width: 28mm; }
            .totals tr.total td { font-weight: 700; font-size: 12pt; border-top: 2px solid {{text}}; padding-top: 2.5mm; }
            """;
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
