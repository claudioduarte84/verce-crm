using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PdfSharp.Pdf.IO;
using Verce.Modules.Documents;

namespace Verce.Documents.Tests;

/// <summary>
/// ROADMAP S7 Exit / ADR-0007 Compliance checks / DEFAULT-PROPOSAL-TEMPLATE §3: "the template
/// renders correctly at 1, 5 and 25 items with a repeated table header and x / y page numbers" —
/// AND, per the S7/S14 scope authority gate's OPTION A closure, a genuinely repeating DOCUMENT
/// header region too. Rebuilt against the GENERIC block-tree engine
/// (<see cref="BlockTreeRenderer"/> + the seeded <see cref="DefaultProposalTemplateSeedData"/>
/// definition) — no compiled Quote template is called anywhere in this file. A real Chromium PDF,
/// parsed with a real PDF text-extraction tool via <see cref="PdfInspector"/> (never OCR, never a
/// fragile pixel-diff).
///
/// F-01/F-02 (S7 final-findings correction): renders through the REAL production entry point
/// (<see cref="BlockTreeRenderer.RenderDocumentAsync"/> against a REAL
/// <see cref="PlaywrightHtmlToPdfRenderer"/>, which spawns the real isolated worker process per
/// render) — never a hand-rolled raw Playwright call bypassing that pipeline — so these proofs
/// also cover the process-isolated renderer and the page_setup-authoritative size/orientation/
/// repeatOn contract, including the two-pass-plus-merge path for FIRST_ONLY/ALL_EXCEPT_FIRST.
/// </summary>
public sealed class QuotePdfPaginationTests
{
    private static readonly PlaywrightHtmlToPdfRenderer Renderer =
        new(new StaticTimeoutOptionsMonitor(TimeSpan.FromSeconds(30)), NullLogger<PlaywrightHtmlToPdfRenderer>.Instance);

    private static QuotePdfInput WithItems(int count)
    {
        var items = Enumerable.Range(1, count)
            .Select(i => new QuotePdfLineInput(i, $"Item de teste {i}", $"Descricao do item {i}", 1m, 10m, "None", 0m, 10m, 10m))
            .ToList();
        return QuotePdfHtmlTemplateTests.NewInput(items: items) with { SubtotalAmount = 10m * count, TotalAmount = 10m * count };
    }

    private static string PageSetupJson(string size = "A4", string orientation = "PORTRAIT",
        string headerRepeatOn = "ALL", string footerRepeatOn = "ALL", decimal? widthMm = null, decimal? heightMm = null)
    {
        var root = new System.Text.Json.Nodes.JsonObject
        {
            ["size"] = size,
            ["orientation"] = orientation,
            ["margin"] = new System.Text.Json.Nodes.JsonObject { ["top"] = 22, ["bottom"] = 18, ["left"] = 14, ["right"] = 14 },
            ["header"] = new System.Text.Json.Nodes.JsonObject { ["repeatOn"] = headerRepeatOn },
            ["footer"] = new System.Text.Json.Nodes.JsonObject { ["repeatOn"] = footerRepeatOn },
        };
        if (widthMm is not null) root["widthMm"] = widthMm;
        if (heightMm is not null) root["heightMm"] = heightMm;
        return root.ToJsonString();
    }

    private static async Task<byte[]> RenderAsync(QuotePdfInput input, string? pageSetupJson = null)
    {
        using var definition = QuotePdfHtmlTemplateTests.DefaultDefinition();
        using var pageSetup = JsonDocument.Parse(pageSetupJson ?? DefaultProposalTemplateSeedData.BuildPageSetupJson());
        var context = new QuoteRenderContext(input);
        var rendered = await BlockTreeRenderer.RenderDocumentAsync(
            definition.RootElement, pageSetup.RootElement, context, input.QuoteNumber, Renderer, CancellationToken.None);
        return rendered.Pdf.Bytes;
    }

    [Fact(Timeout = 60000)]
    public async Task Renders_correctly_with_1_item()
    {
        var bytes = await RenderAsync(WithItems(1));
        var document = PdfInspector.ExtractPages(bytes);
        document.NumberOfPages.Should().Be(1);
        document.FullText.Should().Contain("Item de teste 1");
        AssertPageNumbers(document, expectedTotal: 1);
        AssertDocumentHeaderRepeatsOnEveryPage(document);
    }

    [Fact(Timeout = 60000)]
    public async Task Renders_correctly_with_5_items_all_present_no_duplicates()
    {
        var bytes = await RenderAsync(WithItems(5));
        var document = PdfInspector.ExtractPages(bytes);
        var text = document.FullText;
        for (var i = 1; i <= 5; i++)
        {
            CountOccurrences(text, $"Item de teste {i}").Should().Be(1, $"item {i} must appear exactly once — no dropped, no duplicated row");
        }
        AssertPageNumbers(document, expectedTotal: document.NumberOfPages);
        AssertDocumentHeaderRepeatsOnEveryPage(document);
    }

    [Fact(Timeout = 60000)]
    public async Task Renders_correctly_with_25_items_spanning_multiple_pages_with_repeated_header_and_page_numbers()
    {
        var bytes = await RenderAsync(WithItems(25));
        var document = PdfInspector.ExtractPages(bytes);

        // ADR-0007 §3.2: "a single-page assumption anywhere in the pipeline is a defect" — 25
        // items on A4 with this row height MUST span more than one page.
        document.NumberOfPages.Should().BeGreaterThan(1, "25 items must force pagination — a single-page render here is exactly the defect ADR-0007 §3.2 forbids");

        var text = document.FullText;
        for (var i = 1; i <= 25; i++)
        {
            CountOccurrences(text, $"Item de teste {i}").Should().BeGreaterThanOrEqualTo(1, $"item {i} must be present somewhere in the document");
        }

        // Repeated ITEMS-TABLE header: the "Qtd." column header (never customer content) must
        // occur on every page the table spans across, proving <thead> repeats natively. Chromium
        // rasterizes the header's CSS text-transform:uppercase into the PDF's actual text runs
        // ("QTD."), so the match is case-insensitive.
        var pagesWithTableHeader = document.PageTexts.Count(t => t.Contains("QTD.", StringComparison.OrdinalIgnoreCase));
        pagesWithTableHeader.Should().BeGreaterThan(1, "the items-table header must repeat on every page it spans, not just the first");

        AssertPageNumbers(document, expectedTotal: document.NumberOfPages);

        // S7/S14 scope authority gate — closes the previously-open pagination gap: the
        // repeating DOCUMENT header region (page_setup.header.repeatOn = ALL) must appear on
        // EVERY page, not only page 1, proven against real multi-page Chromium output.
        AssertDocumentHeaderRepeatsOnEveryPage(document);
    }

    // ---- F-02: page_setup is authoritative — size / orientation ----

    [Fact(Timeout = 60000)]
    public async Task Default_size_and_orientation_render_A4_portrait_geometry()
    {
        var bytes = await RenderAsync(WithItems(1), PageSetupJson());
        var (widthPt, heightPt) = ReadFirstPageSizePoints(bytes);
        // A4 portrait: 210mm x 297mm ≈ 595 x 842 points (1mm ≈ 2.8346pt) — allow a small
        // tolerance for Chromium's own rounding.
        widthPt.Should().BeApproximately(595, 3);
        heightPt.Should().BeApproximately(842, 3);
        widthPt.Should().BeLessThan(heightPt, "portrait must be narrower than it is tall");
    }

    [Fact(Timeout = 60000)]
    public async Task LETTER_size_renders_different_real_geometry_than_A4()
    {
        var a4Bytes = await RenderAsync(WithItems(1), PageSetupJson(size: "A4"));
        var letterBytes = await RenderAsync(WithItems(1), PageSetupJson(size: "LETTER"));

        var (a4Width, a4Height) = ReadFirstPageSizePoints(a4Bytes);
        var (letterWidth, letterHeight) = ReadFirstPageSizePoints(letterBytes);

        // Letter: 8.5in x 11in = 612 x 792 points exactly — genuinely distinct from A4's
        // 595 x 842, proven against REAL PDF page geometry (MediaBox), never merely that a
        // "Format" string was passed somewhere.
        letterWidth.Should().BeApproximately(612, 2);
        letterHeight.Should().BeApproximately(792, 2);
        (a4Width, a4Height).Should().NotBe((letterWidth, letterHeight));
    }

    [Fact(Timeout = 60000)]
    public async Task LANDSCAPE_orientation_swaps_width_and_height_relative_to_portrait()
    {
        var portraitBytes = await RenderAsync(WithItems(1), PageSetupJson(orientation: "PORTRAIT"));
        var landscapeBytes = await RenderAsync(WithItems(1), PageSetupJson(orientation: "LANDSCAPE"));

        var (portraitWidth, portraitHeight) = ReadFirstPageSizePoints(portraitBytes);
        var (landscapeWidth, landscapeHeight) = ReadFirstPageSizePoints(landscapeBytes);

        landscapeWidth.Should().BeApproximately(portraitHeight, 3, "landscape must be exactly the portrait geometry rotated 90°");
        landscapeHeight.Should().BeApproximately(portraitWidth, 3);
        landscapeWidth.Should().BeGreaterThan(landscapeHeight, "landscape must be wider than it is tall");
    }

    [Fact(Timeout = 60000)]
    public async Task CUSTOM_millimetre_size_renders_the_exact_requested_geometry()
    {
        // ADR-0007 §3.3: "custom millimetres for labels" — proven generically here (the mechanism
        // itself), independent of any future SHIPPING_LABEL template.
        var bytes = await RenderAsync(WithItems(1), PageSetupJson(size: "CUSTOM", widthMm: 100, heightMm: 150));
        var (widthPt, heightPt) = ReadFirstPageSizePoints(bytes);
        widthPt.Should().BeApproximately(100m * 2.8346m, 2);
        heightPt.Should().BeApproximately(150m * 2.8346m, 2);
    }

    // ---- F-02: page_setup is authoritative — header/footer repeatOn ----

    [Fact(Timeout = 60000)]
    public async Task Header_FIRST_ONLY_appears_on_page_1_and_nowhere_else()
    {
        var bytes = await RenderAsync(WithItems(25), PageSetupJson(headerRepeatOn: "FIRST_ONLY", footerRepeatOn: "ALL"));
        var document = PdfInspector.ExtractPages(bytes);
        document.NumberOfPages.Should().BeGreaterThan(1, "the test needs a genuine multi-page document to prove page-1-only placement");

        document.PageTexts[0].Should().Contain("PROPOSTA COMERCIAL", "the header must appear on page 1");
        for (var i = 1; i < document.PageTexts.Count; i++)
            document.PageTexts[i].Should().NotContain("PROPOSTA COMERCIAL", $"the header must NOT appear on page {i + 1} under FIRST_ONLY");
    }

    [Fact(Timeout = 60000)]
    public async Task Header_ALL_EXCEPT_FIRST_is_absent_on_page_1_and_present_on_every_later_page()
    {
        var bytes = await RenderAsync(WithItems(25), PageSetupJson(headerRepeatOn: "ALL_EXCEPT_FIRST", footerRepeatOn: "ALL"));
        var document = PdfInspector.ExtractPages(bytes);
        document.NumberOfPages.Should().BeGreaterThan(1);

        document.PageTexts[0].Should().NotContain("PROPOSTA COMERCIAL", "the header must be ABSENT on page 1 under ALL_EXCEPT_FIRST");
        for (var i = 1; i < document.PageTexts.Count; i++)
            document.PageTexts[i].Should().Contain("PROPOSTA COMERCIAL", $"the header must appear on page {i + 1}");
    }

    [Fact(Timeout = 60000)]
    public async Task Header_NONE_is_absent_on_every_page()
    {
        var document = PdfInspector.ExtractPages(await RenderAsync(WithItems(25), PageSetupJson(headerRepeatOn: "NONE", footerRepeatOn: "ALL")));
        document.NumberOfPages.Should().BeGreaterThan(1);
        document.PageTexts.Should().OnlyContain(page => !page.Contains("PROPOSTA COMERCIAL"));
    }

    [Fact(Timeout = 60000)]
    public async Task Footer_FIRST_ONLY_appears_on_page_1_and_nowhere_else()
    {
        var bytes = await RenderAsync(WithItems(25), PageSetupJson(headerRepeatOn: "ALL", footerRepeatOn: "FIRST_ONLY"));
        var document = PdfInspector.ExtractPages(bytes);
        document.NumberOfPages.Should().BeGreaterThan(1);

        // The footer's own identity marker (company website, always present in the footer only)
        // proves footer presence/absence independent of the page-number text, which every page
        // still needs regardless of the FOOTER's own repeatOn (page numbering is asserted
        // separately from footer *content* visibility in the two ALL tests above).
        document.PageTexts[0].Should().Contain("verce3d", "the footer must appear on page 1");
        for (var i = 1; i < document.PageTexts.Count; i++)
            document.PageTexts[i].Should().NotContain("verce3d", $"the footer must NOT appear on page {i + 1} under FIRST_ONLY");
    }

    [Fact(Timeout = 60000)]
    public async Task Footer_ALL_EXCEPT_FIRST_is_absent_on_page_1_and_present_on_every_later_page()
    {
        var bytes = await RenderAsync(WithItems(25), PageSetupJson(headerRepeatOn: "ALL", footerRepeatOn: "ALL_EXCEPT_FIRST"));
        var document = PdfInspector.ExtractPages(bytes);
        document.NumberOfPages.Should().BeGreaterThan(1);

        document.PageTexts[0].Should().NotContain("verce3d", "the footer must be ABSENT on page 1 under ALL_EXCEPT_FIRST");
        for (var i = 1; i < document.PageTexts.Count; i++)
            document.PageTexts[i].Should().Contain("verce3d", $"the footer must appear on page {i + 1}");
    }

    [Fact(Timeout = 60000)]
    public async Task Footer_NONE_is_absent_on_every_page()
    {
        var document = PdfInspector.ExtractPages(await RenderAsync(WithItems(25), PageSetupJson(headerRepeatOn: "ALL", footerRepeatOn: "NONE")));
        document.NumberOfPages.Should().BeGreaterThan(1);
        document.PageTexts.Should().OnlyContain(page => !page.Contains("verce3d", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(Timeout = 60000)]
    public async Task Body_content_and_item_count_are_identical_regardless_of_repeatOn_mode()
    {
        // The two-pass-plus-merge mechanism must never drop, duplicate or reorder BODY content —
        // only the header/footer regions differ between the two Chromium passes it composes.
        var uniform = PdfInspector.ExtractPages(await RenderAsync(WithItems(25), PageSetupJson()));
        var twoPass = PdfInspector.ExtractPages(await RenderAsync(WithItems(25), PageSetupJson(headerRepeatOn: "FIRST_ONLY", footerRepeatOn: "ALL_EXCEPT_FIRST")));

        twoPass.NumberOfPages.Should().Be(uniform.NumberOfPages, "splicing two passes together must never change the page count");
        for (var i = 1; i <= 25; i++)
            // "Item de teste 1" is a literal substring of "Item de teste 10".."19" too — count
            // only occurrences NOT immediately followed by another digit.
            CountWholeNumberOccurrences(twoPass.FullText, $"Item de teste {i}").Should().Be(1);
    }

    private static int CountWholeNumberOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            var nextCharIndex = index + needle.Length;
            var followedByDigit = nextCharIndex < haystack.Length && char.IsDigit(haystack[nextCharIndex]);
            if (!followedByDigit) count++;
            index = nextCharIndex;
        }
        return count;
    }

    /// <summary>DEFAULT-PROPOSAL-TEMPLATE §2.1/§3: the repeating page-header region (logo/title/
    /// number/date, native Playwright <c>headerTemplate</c>) must appear on every page, proven by
    /// its literal "PROPOSTA COMERCIAL" title text — never merely a page-1 in-body fragment.</summary>
    private static void AssertDocumentHeaderRepeatsOnEveryPage(PdfInspector.PdfPages document)
    {
        var pages = document.PageTexts;
        for (var i = 0; i < pages.Count; i++)
            pages[i].Should().Contain("PROPOSTA COMERCIAL", $"the repeating document header must appear on page {i + 1} of {pages.Count}, not only the first");
    }

    /// <summary>ADR-0007 §3.3: real "x / y" page numbers via Playwright's native footer template —
    /// asserted against the ACTUAL rendered PDF text, never merely that footer HTML contains a
    /// Playwright class name.</summary>
    private static void AssertPageNumbers(PdfInspector.PdfPages document, int expectedTotal)
    {
        var pages = document.PageTexts;
        for (var i = 0; i < pages.Count; i++)
        {
            var pageNumber = i + 1;
            var normalized = pages[i].Replace(" ", "");
            normalized.Should().Contain($"{pageNumber}/{expectedTotal}",
                $"page {pageNumber} of {pages.Count} must display its own 'x / y' position, not a static or wrong value");
        }
    }

    /// <summary>Real PDF page geometry (MediaBox), read with PdfSharp — the same real, vetted
    /// dependency <see cref="PdfPageMerger"/> uses in production, never OCR, never a pixel-diff.</summary>
    private static (decimal WidthPt, decimal HeightPt) ReadFirstPageSizePoints(byte[] pdfBytes)
    {
        using var document = PdfReader.Open(new MemoryStream(pdfBytes), PdfDocumentOpenMode.Import);
        var page = document.Pages[0];
        return ((decimal)page.Width.Point, (decimal)page.Height.Point);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private sealed class StaticTimeoutOptionsMonitor(TimeSpan timeout) : IOptionsMonitor<PdfRenderTimeoutOptions>
    {
        public PdfRenderTimeoutOptions CurrentValue { get; } = new() { Timeout = timeout };
        public PdfRenderTimeoutOptions Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<PdfRenderTimeoutOptions, string> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
