using FluentAssertions;
using PdfSharp.Pdf;
using Verce.Modules.Documents;

namespace Verce.Documents.Tests;

public sealed class PdfPageMergerTests
{
    [Fact]
    public void Rejects_sources_with_different_page_counts()
    {
        var first = PdfWithPages(2);
        var second = PdfWithPages(1);

        var act = () => PdfPageMerger.MergeFirstPageWithRest(first, second);

        act.Should().Throw<InvalidOperationException>().WithMessage("PDF_RENDER_PAGE_COUNT_MISMATCH");
    }

    private static byte[] PdfWithPages(int count)
    {
        using var document = new PdfDocument();
        for (var i = 0; i < count; i++) document.AddPage();
        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }
}
