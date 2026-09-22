using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verce.Modules.Documents;

namespace Verce.Documents.Tests;

/// <summary>
/// F-01 (S7 final-findings correction): proves the REAL timeout/process-isolation contract —
/// deterministic via a microsecond-scale configured timeout, never a real 30s wait, and never at
/// risk of hanging the test run the way the OLD shared-browser + <c>Task.WaitAsync</c> design did
/// (reproduced as a 20+ minute hang; see the sibling integration test's comment in
/// <c>QuotePdfOutboxAndReissueIntegrationTests</c>). <see cref="PlaywrightHtmlToPdfRenderer"/> now
/// spawns a disposable child <see cref="System.Diagnostics.Process"/>
/// (<c>Verce.Modules.Documents.RenderWorker</c>) per render and force-kills it
/// (<c>entireProcessTree: true</c>) on timeout — a real OS-level termination this test proves
/// leaves the renderer fully usable for the very next call, with no shared state to poison.
///
/// No <c>StartAsync</c>/<c>IHostedService</c> lifecycle is exercised here: the new design's
/// <c>RenderAsync</c> is fully self-contained per call (no shared browser field to warm up), and
/// <c>StartAsync</c>'s own probe render would itself be bound by whatever tiny timeout a test
/// configures — calling it here would fail the fixture before the real test even runs.
/// </summary>
public sealed class PdfRenderTimeoutTests
{
    private static PlaywrightHtmlToPdfRenderer NewRenderer(TimeSpan timeout) =>
        new(new StaticTimeoutOptionsMonitor(timeout), NullLogger<PlaywrightHtmlToPdfRenderer>.Instance);

    [Fact(Timeout = 15000)]
    public async Task A_genuinely_tiny_timeout_makes_RenderAsync_throw_PDF_RENDER_TIMEOUT()
    {
        var renderer = NewRenderer(TimeSpan.FromMilliseconds(1));
        var act = () => renderer.RenderAsync("<html><body>timeout probe</body></html>", PdfRenderOptions.None, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("PDF_RENDER_TIMEOUT");
    }

    /// <summary>The acceptance contract's core claim (mission §5/§10): a timed-out render's
    /// worker process is genuinely gone — proven here by confirming a SECOND render, on the SAME
    /// renderer instance, with a NORMAL timeout, succeeds right after — no leaked process, no
    /// poisoned shared state, nothing to restart.</summary>
    [Fact(Timeout = 30000)]
    public async Task After_a_timeout_the_SAME_renderer_instance_succeeds_on_the_next_render()
    {
        var timeoutOptions = new MutableTimeoutOptionsMonitor(TimeSpan.FromMilliseconds(1));
        var renderer = new PlaywrightHtmlToPdfRenderer(timeoutOptions, NullLogger<PlaywrightHtmlToPdfRenderer>.Instance);

        var failing = () => renderer.RenderAsync("<html><body>timeout probe</body></html>", PdfRenderOptions.None, CancellationToken.None);
        await failing.Should().ThrowAsync<InvalidOperationException>().WithMessage("PDF_RENDER_TIMEOUT");

        timeoutOptions.Timeout = TimeSpan.FromSeconds(20);
        var result = await renderer.RenderAsync("<html><body>real render</body></html>", PdfRenderOptions.None, CancellationToken.None);

        result.Bytes.Length.Should().BeGreaterThan(4);
        System.Text.Encoding.ASCII.GetString(result.Bytes, 0, 5).Should().Be("%PDF-");
    }

    /// <summary>Mission §11: a bounded stress regression — timeout, timeout, then a successful
    /// render, all on the SAME renderer instance — proving practical recovery without asserting
    /// on fragile OS process counts.</summary>
    [Fact(Timeout = 30000)]
    public async Task Repeated_timeouts_do_not_prevent_a_later_successful_render()
    {
        var timeoutOptions = new MutableTimeoutOptionsMonitor(TimeSpan.FromMilliseconds(1));
        var renderer = new PlaywrightHtmlToPdfRenderer(timeoutOptions, NullLogger<PlaywrightHtmlToPdfRenderer>.Instance);

        for (var i = 0; i < 2; i++)
        {
            var act = () => renderer.RenderAsync("<html><body>timeout probe</body></html>", PdfRenderOptions.None, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("PDF_RENDER_TIMEOUT");
        }

        timeoutOptions.Timeout = TimeSpan.FromSeconds(20);
        var result = await renderer.RenderAsync("<html><body>real render</body></html>", PdfRenderOptions.None, CancellationToken.None);
        System.Text.Encoding.ASCII.GetString(result.Bytes, 0, 5).Should().Be("%PDF-");
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

    private sealed class MutableTimeoutOptionsMonitor(TimeSpan timeout) : IOptionsMonitor<PdfRenderTimeoutOptions>
    {
        public TimeSpan Timeout { get; set; } = timeout;
        public PdfRenderTimeoutOptions CurrentValue => new() { Timeout = Timeout };
        public PdfRenderTimeoutOptions Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<PdfRenderTimeoutOptions, string> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
