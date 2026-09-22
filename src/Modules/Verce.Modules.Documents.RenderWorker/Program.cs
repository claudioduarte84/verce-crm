using System.Text.Json;
using Microsoft.Playwright;
using Verce.Modules.Documents.RenderWorker;

// S7 final-findings correction F-01: a dedicated, disposable process PER RENDER. The parent
// (Verce.Modules.Documents.PlaywrightHtmlToPdfRenderer) launches this executable, waits for it to
// exit within its configured deadline, and — if it does not — calls Process.Kill(entireProcessTree:
// true) on it. That is a real OS-level termination of this ENTIRE process (and any Chromium/driver
// descendants it spawned), which the .NET Playwright API gives no other way to force: it exposes
// no cancellable render operation and no process handle for an in-process browser (verified against
// the actual installed 1.62.0 API surface — there is no BrowserTypeLaunchServerOptions/
// IBrowserServer/process/PID member anywhere on IPlaywright/IBrowserType/IBrowser). Isolating each
// render in its own process turns an unkillable in-process hang into an ordinary killable child
// process — the parent's guarantee ("the timed-out render is gone, the next render is unaffected")
// holds regardless of what state Chromium's IPC connection was in when the deadline hit.
//
// No internal timeout lives here: the PARENT is the sole timeout authority (a single deadline
// enforced by the process's own Kill, not two independent, potentially-disagreeing budgets).
//
// Usage: Verce.Modules.Documents.RenderWorker <request-json-path> <response-json-path>
// Exit code 0 = response.Success is authoritative; exit code != 0 = worker crashed before it could
// even write a response (the parent treats a missing/unwritten response file as PDF_RENDER_FAILED).

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Verce.Modules.Documents.RenderWorker <request-json-path> <response-json-path>");
    return 2;
}

var requestPath = args[0];
var responsePath = args[1];

try
{
    var requestJson = await File.ReadAllTextAsync(requestPath);
    var request = JsonSerializer.Deserialize<RenderRequest>(requestJson, JsonOptions.Value)
        ?? throw new InvalidOperationException("PDF_RENDER_WORKER_REQUEST_INVALID");

    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        Headless = true,
        // Sandboxed, non-root (SECURITY §8) — no --no-sandbox/--disable-setuid-sandbox flags.
        Args = ["--disable-gpu"],
    });

    var page = await browser.NewPageAsync();
    // Defense in depth: the template is fully self-contained, so this should never actually
    // intercept a real request — abort anything that somehow tries.
    await page.RouteAsync("**/*", route => route.AbortAsync());
    await page.SetContentAsync(request.Html, new PageSetContentOptions { WaitUntil = WaitUntilState.Load });

    var pdfOptions = new PagePdfOptions
    {
        PrintBackground = true,
        Margin = new Margin { Top = request.MarginTop, Bottom = request.MarginBottom, Left = request.MarginLeft, Right = request.MarginRight },
        DisplayHeaderFooter = request.DisplayHeaderFooter,
        HeaderTemplate = request.HeaderHtml ?? "<span></span>",
        FooterTemplate = request.FooterHtml ?? "<span></span>",
        Landscape = request.Landscape,
    };
    // F-02: page size is authoritative — Width/Height (custom millimetres, e.g. for a future
    // SHIPPING_LABEL template) takes precedence over a named Format when both would otherwise
    // apply; DocumentTemplateValidator guarantees exactly one of them is actually set.
    if (request.WidthMm is not null && request.HeightMm is not null) { pdfOptions.Width = request.WidthMm; pdfOptions.Height = request.HeightMm; }
    else pdfOptions.Format = request.Format ?? "A4";

    var bytes = await page.PdfAsync(pdfOptions);
    await File.WriteAllBytesAsync(request.OutputPdfPath, bytes);

    var response = new RenderResponse(Success: true, ErrorCode: null, ChromiumVersion: browser.Version,
        RenderEngineVersion: $"Microsoft.Playwright/{typeof(IPlaywright).Assembly.GetName().Version}");
    await File.WriteAllTextAsync(responsePath, JsonSerializer.Serialize(response, JsonOptions.Value));
    return 0;
}
catch (Exception ex)
{
    try
    {
        var response = new RenderResponse(Success: false, ErrorCode: "PDF_RENDER_FAILED", ChromiumVersion: null, RenderEngineVersion: null,
            ErrorDetail: ex.Message);
        await File.WriteAllTextAsync(responsePath, JsonSerializer.Serialize(response, JsonOptions.Value));
    }
    catch
    {
        // Writing the failure response itself failed (e.g. disk full) — the parent's own
        // "response file missing/unreadable" fallback still yields PDF_RENDER_FAILED.
    }
    return 1;
}

namespace Verce.Modules.Documents.RenderWorker
{
    internal static class JsonOptions
    {
        public static readonly JsonSerializerOptions Value = new(JsonSerializerDefaults.Web);
    }

    /// <summary>Mirrors the parent's own request shape by JSON property name only — deliberately
    /// NOT a shared assembly reference (this worker stays a minimal, independently-launchable
    /// executable with no dependency on Verce.Modules.Documents itself).</summary>
    internal sealed record RenderRequest(
        string Html, string? HeaderHtml, string? FooterHtml, bool DisplayHeaderFooter,
        string MarginTop, string MarginBottom, string MarginLeft, string MarginRight,
        string? Format, string? WidthMm, string? HeightMm, bool Landscape,
        string OutputPdfPath);

    internal sealed record RenderResponse(
        bool Success, string? ErrorCode, string? ChromiumVersion, string? RenderEngineVersion, string? ErrorDetail = null);
}
