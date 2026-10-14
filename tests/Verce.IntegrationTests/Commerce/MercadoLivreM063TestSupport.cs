using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Verce.Infrastructure.Marketplaces.MercadoLivre;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// M063 support: a hand-written simulated monotonic clock (no package), so ADR-0029 §4's "30 s budget" is proved with exact simulated durations instead of
/// sleeping or tolerating scheduler noise. <see cref="TimeProvider"/> is what the production executor and sender measure every deadline on.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private long _ticks;
    private readonly List<ManualTimer> _timers = [];

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_gate) return _ticks; }
    public override DateTimeOffset GetUtcNow() { lock (_gate) return new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks); }
    public TimeSpan Elapsed { get { lock (_gate) return TimeSpan.FromTicks(_ticks); } }

    /// <summary>The due time of every one-shot timer ever created through this provider, in creation order: for the executor these are exactly the attempt windows.</summary>
    public List<TimeSpan> CreatedDueTimes { get; } = [];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate) { _timers.Add(timer); if (dueTime != Timeout.InfiniteTimeSpan) CreatedDueTimes.Add(dueTime); }
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves simulated time forward by <paramref name="by"/>, firing every timer that comes due on the way, in order.</summary>
    public void Advance(TimeSpan by)
    {
        long target; lock (_gate) target = _ticks + by.Ticks;
        while (true)
        {
            ManualTimer? due = null;
            lock (_gate)
            {
                foreach (var t in _timers) if (t.DueTicks is { } d && d <= target && (due is null || d < due.DueTicks)) due = t;
                if (due is null) { _ticks = target; return; }
                _ticks = Math.Max(_ticks, due.DueTicks!.Value);
                due.DueTicks = null;
            }
            due.Fire();
        }
    }

    /// <summary>Jumps exactly to the earliest pending timer and fires it (a stalled operation "waits out" its own deadline, no more, no less).</summary>
    public void AdvanceToNextTimer()
    {
        long? next = null;
        lock (_gate) foreach (var t in _timers) if (t.DueTicks is { } d && (next is null || d < next)) next = d;
        if (next is { } n) Advance(TimeSpan.FromTicks(Math.Max(0, n - GetTimestamp())));
    }

    internal long NowTicks { get { lock (_gate) return _ticks; } }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long? DueTicks { get; set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate) DueTicks = dueTime == Timeout.InfiniteTimeSpan ? null : owner._ticks + Math.Max(0, dueTime.Ticks);
            return true;
        }
        public void Fire() => callback(state);
        public void Dispose() { lock (owner._gate) { DueTicks = null; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

/// <summary>Records every backoff/Retry-After wait; with a <see cref="ManualTimeProvider"/> the wait also consumes simulated time (so the budget is consumed exactly).</summary>
internal sealed class SimulatedDelay(ManualTimeProvider? manual) : IMarketplaceDelay
{
    public List<TimeSpan> Delays { get; } = [];
    /// <summary>A real delay can only overshoot what was asked; set this to simulate it.</summary>
    public TimeSpan Overshoot { get; set; }
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(duration);
        manual?.Advance(duration + Overshoot);
        return Task.CompletedTask;
    }
}

internal sealed class FixedJitter(TimeSpan value) : IMarketplaceJitter { public TimeSpan Next() => value; }

internal sealed record RigRequest(HttpMethod Method, Uri Uri);

/// <summary>A real <see cref="MercadoLivreConnector"/> + real sender/executor over a scripted wire. Pass a <see cref="ManualTimeProvider"/> for deterministic budget proofs,
/// or nothing for real time with the production constants.</summary>
internal sealed class AdapterRig
{
    public required MercadoLivreConnector Connector { get; init; }
    public required List<RigRequest> Requests { get; init; }
    public required SimulatedDelay? SimDelay { get; init; }
    public required CapturingLoggerProvider Logger { get; init; }
    public required TestClock Clock { get; init; }
    public ManualTimeProvider? Time { get; init; }

    public static byte[] Secret { get; } = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"m063-access-secret","refresh_token":"m063-refresh-secret","user_id":"777"}""");

    public static AdapterRig Create(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond, ManualTimeProvider? time = null, IMarketplaceJitter? jitter = null)
    {
        var options = Options.Create(new MercadoLivreOptions { Enabled = true, ClientId = "900000001", RedirectUri = "https://host.example/api/commerce/marketplace-authorizations/MERCADO_LIVRE/callback", ResolvedClientSecret = "m063-client-secret" });
        var clock = new TestClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z")); var requests = new List<RigRequest>(); var logger = new CapturingLoggerProvider();
        var handler = new ScriptedHandler(async (request, ct) => { RigRequest entry = new(request.Method, request.RequestUri!); int n; lock (requests) { requests.Add(entry); n = requests.Count; } return await respond(request, n, ct); });
        var sender = new MarketplaceHttpSender(new Microsoft.Extensions.Logging.Logger<MarketplaceHttpSender>(new Microsoft.Extensions.Logging.LoggerFactory([logger])), time);
        SimulatedDelay? simDelay = time is null ? null : new SimulatedDelay(time);
        IMarketplaceDelay delay = simDelay is null ? new SystemMarketplaceDelay() : simDelay;
        var reader = new MarketplaceSafeReadExecutor(sender, delay, jitter ?? new FixedJitter(TimeSpan.Zero), clock);
        return new AdapterRig { Connector = new MercadoLivreConnector(new SingleClient(handler), sender, reader, options, clock), Requests = requests, SimDelay = simDelay, Logger = logger, Clock = clock, Time = time };
    }

    public static AdapterRig Create(Func<HttpRequestMessage, int, HttpResponseMessage> respond, ManualTimeProvider? time = null, IMarketplaceJitter? jitter = null) =>
        Create((r, n, _) => Task.FromResult(respond(r, n)), time, jitter);

    private sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }

    private sealed class SingleClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler);
        public HttpClient CreateClient(string name) => _client;
    }
}

/// <summary>A 200 response whose body is produced by <paramref name="read"/> on every read: use it to stall (wait for the attempt deadline), truncate (throw an IOException) or
/// stream a valid document. It hands the reader the stream directly (no buffering) so the bounded streaming reader is what is exercised.</summary>
internal sealed class ScriptedBodyContent(Func<Memory<byte>, CancellationToken, ValueTask<int>> read, long? declaredLength = null) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("the reader must use the read stream directly");
    protected override bool TryComputeLength(out long length) { length = declaredLength ?? 0; return declaredLength is not null; }
    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new ScriptedStream(read));
    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult<Stream>(new ScriptedStream(read));

    public static HttpResponseMessage Ok(Func<Memory<byte>, CancellationToken, ValueTask<int>> read, long? declaredLength = null) =>
        new(HttpStatusCode.OK) { Content = new ScriptedBodyContent(read, declaredLength) };

    /// <summary>A body that delivers <paramref name="text"/> then ends normally.</summary>
    public static HttpResponseMessage OkText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text); var position = 0;
        return Ok((buffer, _) =>
        {
            var count = Math.Min(buffer.Length, bytes.Length - position);
            bytes.AsMemory(position, count).CopyTo(buffer); position += count;
            return ValueTask.FromResult(count);
        });
    }

    private sealed class ScriptedStream(Func<Memory<byte>, CancellationToken, ValueTask<int>> read) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("async only");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => read(buffer, cancellationToken);
    }
}
