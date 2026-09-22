using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Verce.Modules.Documents;

/// <summary>
/// F-02 (S7 final-findings correction): splices page 1 of one Chromium print pass onto pages
/// 2..N of a second pass — the deterministic mechanism <see cref="BlockTreeRenderer"/> uses to
/// implement <c>page_setup</c>'s <c>FIRST_ONLY</c>/<c>ALL_EXCEPT_FIRST</c> <c>repeatOn</c> modes,
/// which Playwright/Chromium's header/footer template mechanism cannot express on its own (it
/// renders one static template on every page — there is no per-page-conditional hook; verified
/// against the installed Microsoft.Playwright 1.62.0 API). Both source PDFs are two REAL,
/// independently Chromium-rendered documents of the SAME body content and margins — only their
/// header/footer template differs between the two passes — so they always share the same page
/// count and geometry; this only ever IMPORTS whole existing pages, never generates or edits
/// page content.
/// </summary>
internal static class PdfPageMerger
{
    /// <summary>If <paramref name="firstPageSource"/> has exactly one page, it already IS the
    /// whole document — returned as-is, no merge, no need to have even considered the second
    /// pass's remaining pages.</summary>
    public static byte[] MergeFirstPageWithRest(byte[] firstPageSource, byte[] restPagesSource)
    {
        using var firstDoc = PdfReader.Open(new MemoryStream(firstPageSource), PdfDocumentOpenMode.Import);
        using var restDoc = PdfReader.Open(new MemoryStream(restPagesSource), PdfDocumentOpenMode.Import);
        if (firstDoc.PageCount != restDoc.PageCount)
            throw new InvalidOperationException("PDF_RENDER_PAGE_COUNT_MISMATCH");
        if (firstDoc.PageCount <= 1) return firstPageSource;
        using var output = new PdfDocument();

        output.AddPage(firstDoc.Pages[0]);
        for (var i = 1; i < restDoc.PageCount; i++) output.AddPage(restDoc.Pages[i]);

        using var outStream = new MemoryStream();
        output.Save(outStream);
        return outStream.ToArray();
    }
}
