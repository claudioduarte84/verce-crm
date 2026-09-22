using System.Diagnostics;

namespace Verce.Documents.Tests;

/// <summary>
/// Real, deterministic PDF page/text extraction for pagination proofs — never OCR, never a
/// fragile pixel-diff.
///
/// PdfPig provenance defect (S7 template-engine final pass, 2026-09-21): this project previously
/// depended on <c>UglyToad.PdfPig</c>. On this NuGet feed every transitive package
/// (<c>UglyToad.PdfPig.Core</c>/<c>.Fonts</c>/<c>.Tokenization</c>/<c>.Tokens</c>) resolves to a
/// single non-standard release, <c>1.7.0-custom-5</c> — <c>nuget.org</c>'s own search API shows it
/// owned by account <c>grinay</c> (not the real UglyToad.PdfPig maintainers), carrying 2.72M of
/// the package's 2.77M total downloads under a placeholder <c>"Package Description"</c>, with no
/// matching version in the real project's release history (which follows a <c>0.1.x</c> scheme).
/// That is a supply-chain red flag, not a usable dependency, and there is no clean version to pin
/// to on this feed — every dependant package still resolves to <c>1.7.0-custom-5</c> regardless
/// of which top-level version is requested. Removed entirely rather than shipped unverified.
///
/// Replacement: shell out to <c>pdftotext</c> (poppler/xpdf), a long-established, independently
/// distributed CLI tool with none of that provenance risk, and not a project dependency at all —
/// it never reaches production, only this test project's own verification step. It must be on
/// PATH wherever these tests run; see docs/OPERATIONS.md "Development prerequisites".
/// </summary>
internal static class PdfInspector
{
    internal sealed record PdfPages(IReadOnlyList<string> PageTexts)
    {
        public int NumberOfPages => PageTexts.Count;
        public string FullText => string.Join(" ", PageTexts);
    }

    internal static PdfPages ExtractPages(byte[] pdfBytes)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"verce-pdf-inspect-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(tempFile, pdfBytes);
        try
        {
            var startInfo = new ProcessStartInfo("pdftotext")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // "-raw" (content-stream order) rather than "-layout" (visual column/row
            // reconstruction): -layout's Y-coordinate line-clustering heuristic merges the
            // items-table's LAST row with the footer's page-number text into one glued line once
            // enough rows fit close to the bottom margin to fall within its tolerance — a
            // text-extraction artifact, not a real rendering defect (Chromium's print engine
            // physically cannot let body content bleed into the header/footer margin band). -raw
            // preserves each text-showing operator's own run as a separate token instead.
            startInfo.ArgumentList.Add("-raw");
            startInfo.ArgumentList.Add(tempFile);
            startInfo.ArgumentList.Add("-");

            Process process;
            try
            {
                process = Process.Start(startInfo) ?? throw new InvalidOperationException("PDF_INSPECTOR_PROCESS_START_FAILED");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
            {
                throw new InvalidOperationException(
                    "PDF_INSPECTOR_PDFTOTEXT_NOT_FOUND: 'pdftotext' must be on PATH to run PDF pagination tests " +
                    "(poppler-utils on Linux/CI, xpdf tools on Windows dev machines — see docs/OPERATIONS.md).", ex);
            }

            using (process)
            {
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"PDF_INSPECTOR_PDFTOTEXT_FAILED:{process.ExitCode}:{stderr}");

                // pdftotext inserts a form-feed (0x0C) after every page by default; splitting on
                // it gives exact per-page text without a second tool invocation for page count.
                var pages = stdout.Split('\f').ToList();
                if (pages.Count > 0 && string.IsNullOrWhiteSpace(pages[^1]))
                    pages.RemoveAt(pages.Count - 1);
                return new PdfPages(pages);
            }
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
