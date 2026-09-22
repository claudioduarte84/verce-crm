using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verce.Modules.Documents;

/// <summary>Mission §45: an injectable timeout seam — production always binds this to 30s
/// (SECURITY §8); a test can configure a small value through the SAME DI options path to prove
/// the timeout contract deterministically, without ever actually waiting 30 real seconds.</summary>
public sealed class PdfRenderTimeoutOptions
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed record PdfRenderResult(byte[] Bytes, string? ChromiumVersion, string RenderEngineVersion);

/// <summary>ADR-0007 §3.3: the repeating page-header/footer region and page numbering (Playwright
/// <c>headerTemplate</c>/<c>footerTemplate</c> — native browser print headers/footers, which
/// repeat on EVERY page automatically, distinct from the document's own body content). Margins
/// must leave room for whatever height the header/footer templates actually render at.
///
/// F-02 (S7 final-findings correction): page size/orientation are now part of the render
/// contract the caller resolves from <c>page_setup</c> — never a hard-coded "A4" the renderer
/// silently substitutes regardless of what was persisted. Exactly one of
/// (<see cref="Format"/>) or (<see cref="WidthMm"/> + <see cref="HeightMm"/>) is set; which one
/// is decided by <see cref="BlockTreeRenderer.BuildRenderOptions"/> from the template's own
/// <c>page_setup.size</c>, validated at publish time by <see cref="DocumentTemplateValidator"/>.</summary>
public sealed record PdfRenderOptions(
    string? HeaderHtml, string? FooterHtml, string MarginTop, string MarginBottom, string MarginLeft, string MarginRight,
    string? Format = "A4", string? WidthMm = null, string? HeightMm = null, bool Landscape = false)
{
    public static readonly PdfRenderOptions None = new(null, null, "0", "0", "0", "0");
}

public interface IHtmlToPdfRenderer
{
    Task<PdfRenderResult> RenderAsync(string html, PdfRenderOptions options, CancellationToken cancellationToken);
}

/// <summary>
/// SECURITY §8 / ADR-0016 §6: headless, sandboxed Chromium, loading ONLY locally-generated HTML
/// via <c>SetContentAsync</c> (never a URL), with the render context's own network access
/// blocked — the fully self-contained template (embedded/local assets only) means this should
/// never observe an outbound request in the first place; blocking it is defense in depth, not a
/// load-bearing dependency of the template's correctness.
///
/// F-01 (S7 final-findings correction): every render runs in its OWN disposable OS process
/// (<c>Verce.Modules.Documents.RenderWorker</c>), never a browser shared across renders. The
/// installed Microsoft.Playwright 1.62.0 .NET API exposes no way to cancel an in-flight
/// <c>SetContentAsync</c>/<c>PdfAsync</c> call and no process handle for the browser it manages —
/// verified by reflecting over <c>IPlaywright</c>/<c>IBrowserType</c>/<c>IBrowser</c>: there is no
/// <c>BrowserTypeLaunchServerOptions</c>, no <c>IBrowserServer</c>, no PID/Kill member anywhere.
/// A previous design shared one long-lived browser and enforced its 30s budget with
/// <c>Task.WaitAsync</c>; that only abandons the CALLER's await — the abandoned Playwright IPC
/// call keeps running underneath and was reproduced leaving the shared browser's connection
/// unresponsive, hanging a later <c>CloseAsync</c> for 20+ minutes. Spawning a real child
/// <see cref="Process"/> per render fixes this at the root: on timeout,
/// <see cref="Process.Kill(bool)"/> with <c>entireProcessTree: true</c> is an unconditional
/// OS-level termination (SIGKILL / TerminateProcess) of that process and every Chromium/driver
/// descendant it spawned — it cannot hang, and it can never poison a render that hasn't started
/// yet, because there is nothing shared left to poison.
/// </summary>
public sealed class PlaywrightHtmlToPdfRenderer : IHtmlToPdfRenderer, IHostedService
{
    private readonly IOptionsMonitor<PdfRenderTimeoutOptions> _timeoutOptions;
    private readonly ILogger<PlaywrightHtmlToPdfRenderer> _logger;
    private readonly string _workerDllPath;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // A render deadline and process termination are deliberately separate budgets. Killing a
    // process tree returns before Windows/Linux have necessarily reaped every child.
    private static readonly TimeSpan WorkerTerminationTimeout = TimeSpan.FromSeconds(2);

    public PlaywrightHtmlToPdfRenderer(IOptionsMonitor<PdfRenderTimeoutOptions> timeoutOptions, ILogger<PlaywrightHtmlToPdfRenderer> logger)
    {
        _timeoutOptions = timeoutOptions;
        _logger = logger;
        _workerDllPath = Path.Combine(AppContext.BaseDirectory, "Verce.Modules.Documents.RenderWorker.dll");
    }

    /// <summary>Mission §8: a hosted-lifecycle component may still validate the environment at
    /// startup — but a full disposable-worker render probe here would pay a real OS-process-spawn
    /// + Chromium-launch cost on EVERY host startup (every test's own <c>WebApplicationFactory</c>
    /// included), whether or not that host ever renders a single PDF; multiplied across a large
    /// integration suite that measurably slowed the whole run. The worker DLL's mere presence is
    /// what a misconfigured deployment (a broken publish, a missing ProjectReference) would
    /// actually get wrong, so that is what this checks — fast, and still fails closed with a clear
    /// error naming the missing path. The FIRST real render remains the genuine, unavoidable proof
    /// that Chromium itself launches correctly in this environment, exactly like any other runtime
    /// dependency this codebase does not eagerly probe at startup (e.g. the database connection).</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_workerDllPath))
            throw new InvalidOperationException($"PDF_RENDERER_WORKER_MISSING:{_workerDllPath}");
        return Task.CompletedTask;
    }

    /// <summary>Nothing persistent to tear down anymore — each render already cleans up its own
    /// worker process and temp files as it completes.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<PdfRenderResult> RenderAsync(string html, PdfRenderOptions options, CancellationToken cancellationToken)
    {
        var deadline = _timeoutOptions.CurrentValue.Timeout;
        var workDir = Directory.CreateTempSubdirectory("verce-pdf-render-");
        try
        {
            var requestPath = Path.Combine(workDir.FullName, "request.json");
            var responsePath = Path.Combine(workDir.FullName, "response.json");
            var outputPdfPath = Path.Combine(workDir.FullName, "output.pdf");

            var hasCustomSize = options.WidthMm is not null && options.HeightMm is not null;
            var request = new WorkerRenderRequest(
                Html: html,
                HeaderHtml: options.HeaderHtml, FooterHtml: options.FooterHtml,
                DisplayHeaderFooter: options.HeaderHtml is not null || options.FooterHtml is not null,
                MarginTop: options.MarginTop, MarginBottom: options.MarginBottom, MarginLeft: options.MarginLeft, MarginRight: options.MarginRight,
                Format: hasCustomSize ? null : options.Format ?? "A4",
                WidthMm: hasCustomSize ? options.WidthMm : null, HeightMm: hasCustomSize ? options.HeightMm : null,
                Landscape: options.Landscape,
                OutputPdfPath: outputPdfPath);
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, JsonOptions), cancellationToken);

            using var process = StartWorkerProcess(requestPath, responsePath);

            using var timeoutCts = new CancellationTokenSource(deadline);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                await TerminateAndReapWorkerAsync(process);
                throw new InvalidOperationException("PDF_RENDER_TIMEOUT");
            }
            catch (OperationCanceledException)
            {
                // The CALLER's own token fired (not our deadline) — still a real termination, no
                // zombie worker left running after we stop awaiting it.
                await TerminateAndReapWorkerAsync(process);
                throw;
            }

            return await ReadWorkerResultAsync(process.ExitCode, responsePath, outputPdfPath, cancellationToken);
        }
        finally
        {
            await TryDeleteDirectoryAsync(workDir.FullName);
        }
    }

    private Process StartWorkerProcess(string requestPath, string responsePath)
    {
        // Re-invoke whatever launched THIS process: in every real deployment shape this repo
        // uses (dotnet run, dotnet test, `ENTRYPOINT ["dotnet", "Verce.Api.dll"]`), the current
        // process's own path IS the dotnet muxer — reusing it needs no PATH lookup. The "dotnet"
        // fallback only matters for a hypothetical self-contained apphost, never exercised here.
        var currentExeName = Path.GetFileName(Environment.ProcessPath ?? string.Empty);
        var isDotnetMuxer = currentExeName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || currentExeName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
        var fileName = isDotnetMuxer ? Environment.ProcessPath! : "dotnet";

        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(_workerDllPath);
        startInfo.ArgumentList.Add(requestPath);
        startInfo.ArgumentList.Add(responsePath);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = false };
        process.Start();
        // Redirected streams MUST be drained asynchronously — an unread, filled OS pipe buffer
        // would block the WORKER writing to it, which is exactly the kind of hang this whole
        // redesign exists to eliminate. The worker communicates only via the response JSON file;
        // its stdout/stderr are diagnostic noise this process discards.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>An unconditional OS-level kill — SIGKILL/TerminateProcess via .NET's own
    /// implementation, which cannot itself hang waiting for the target to cooperate. Swallows the
    /// (rare, benign) race where the process exits between the timeout firing and this call.</summary>
    private static void KillWorker(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already exited */ }
    }

    /// <summary>Terminates the dedicated worker tree and observes its exit before IPC cleanup.
    /// Both observations are bounded: an uncooperative OS process can never convert a PDF
    /// timeout into an indefinitely blocked API request.</summary>
    private async Task TerminateAndReapWorkerAsync(Process process)
    {
        KillWorker(process);
        if (await ObserveExitAsync(process)) return;

        // One final defensive kill covers the narrow race where the original tree expands while
        // the first kill is being issued. We still do not wait indefinitely afterwards.
        KillWorker(process);
        if (!await ObserveExitAsync(process))
            _logger.LogWarning("PDF render worker {WorkerId} did not exit within the bounded termination window.", SafeProcessId(process));
    }

    private static async Task<bool> ObserveExitAsync(Process process)
    {
        try
        {
            if (process.HasExited) return true;
            using var terminationCts = new CancellationTokenSource(WorkerTerminationTimeout);
            await process.WaitForExitAsync(terminationCts.Token);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (InvalidOperationException) { return true; }
    }

    private static int? SafeProcessId(Process process)
    {
        try { return process.Id; }
        catch (InvalidOperationException) { return null; }
    }

    private static async Task<PdfRenderResult> ReadWorkerResultAsync(int exitCode, string responsePath, string outputPdfPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(responsePath))
            throw new InvalidOperationException("PDF_RENDER_FAILED"); // crashed before writing anything at all

        var response = JsonSerializer.Deserialize<WorkerRenderResponse>(await File.ReadAllTextAsync(responsePath, cancellationToken), JsonOptions)
            ?? throw new InvalidOperationException("PDF_RENDER_FAILED");

        if (!response.Success || exitCode != 0)
            throw new InvalidOperationException(response.ErrorCode ?? "PDF_RENDER_FAILED");

        var bytes = await File.ReadAllBytesAsync(outputPdfPath, cancellationToken);
        return new PdfRenderResult(bytes, response.ChromiumVersion, response.RenderEngineVersion ?? "unknown");
    }

    private async Task TryDeleteDirectoryAsync(string path)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50));
                    continue;
                }
                _logger.LogWarning(ex, "Could not remove PDF render temporary directory {WorkDirectory} after worker termination.", path);
            }
        }
    }

    /// <summary>Mirrors <c>Verce.Modules.Documents.RenderWorker</c>'s own request/response
    /// records by JSON property name only — deliberately not a shared assembly reference (the
    /// worker stays independently launchable with no dependency on this assembly).</summary>
    private sealed record WorkerRenderRequest(
        string Html, string? HeaderHtml, string? FooterHtml, bool DisplayHeaderFooter,
        string MarginTop, string MarginBottom, string MarginLeft, string MarginRight,
        string? Format, string? WidthMm, string? HeightMm, bool Landscape,
        string OutputPdfPath);

    private sealed record WorkerRenderResponse(
        bool Success, string? ErrorCode, string? ChromiumVersion, string? RenderEngineVersion, string? ErrorDetail = null);
}
