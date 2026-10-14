using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verce.Infrastructure.Marketplaces.MercadoLivre;
using Verce.Modules.Commerce;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// M064 (Correct + Validate) — adversarial review of C063-01 and C063-03, written AFTER reading the M063 code. Each test targets something the M063 suites did not prove:
/// an INDEPENDENT oracle for the exact-price algorithm (BigInteger arithmetic, never the code under test), constant allocation for hostile lexemes, the classification returned when
/// a wait exhausts the budget (ADR-0029 §4 "the last attempt's classification"), the body deadline on a REAL HTTP stack (SocketsHttpHandler over loopback, not a scripted stream),
/// and real-time cancellation/stall measurements with monotonic AND wall-clock evidence.
/// </summary>
public sealed class MercadoLivreConnectorM064ReviewTests
{
    // ============================================================ C063-01 — independent oracle

    private enum Expected { Valid, NotPositive, Precision }

    /// <summary>The ADR-0029 §1 decision computed with exact integer arithmetic only: value = mantissa × 10^e10; valid iff positive, an exact multiple of 0.01 and below 10^16.</summary>
    private static (Expected Result, decimal Value) Oracle(string lexeme)
    {
        var negative = lexeme[0] == '-'; var s = negative ? lexeme[1..] : lexeme;
        var ePos = s.IndexOfAny(['e', 'E']);
        var mantissaText = ePos < 0 ? s : s[..ePos];
        var exponent = ePos < 0 ? 0 : int.Parse(s[(ePos + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var dot = mantissaText.IndexOf('.');
        var integerPart = dot < 0 ? mantissaText : mantissaText[..dot]; var fraction = dot < 0 ? "" : mantissaText[(dot + 1)..];
        var mantissa = BigInteger.Parse(integerPart + fraction, CultureInfo.InvariantCulture);
        var e10 = exponent - fraction.Length;
        if (mantissa.IsZero || negative) return (Expected.NotPositive, 0m);
        var shift = e10 + 2;                                                        // cents = mantissa × 10^shift
        BigInteger cents;
        if (shift >= 0) { if (shift > 40) return (Expected.Precision, 0m); cents = mantissa * BigInteger.Pow(10, shift); }
        else
        {
            var divisor = BigInteger.Pow(10, -shift);
            cents = BigInteger.DivRem(mantissa, divisor, out var remainder);
            if (!remainder.IsZero) return (Expected.Precision, 0m);
        }
        if (cents >= BigInteger.Pow(10, 18)) return (Expected.Precision, 0m);       // value >= 10^16 does not fit numeric(18,2)
        return (Expected.Valid, (decimal)cents / 100m);
    }

    private static void Verify(string lexeme)
    {
        var expected = Oracle(lexeme);
        var actual = MercadoLivreConnector.ExactPrice.Parse(lexeme, out var value);
        var mapped = actual switch { MercadoLivreConnector.ExactPriceResult.Valid => Expected.Valid, MercadoLivreConnector.ExactPriceResult.NotPositive => Expected.NotPositive, _ => Expected.Precision };
        mapped.Should().Be(expected.Result, "lexeme {0}", lexeme);
        if (expected.Result == Expected.Valid) { value.Should().Be(expected.Value, "lexeme {0}", lexeme); value.Scale.Should().Be(2, "lexeme {0}", lexeme); }
    }

    [Fact]
    public void ExactPrice_agrees_with_an_independent_BigInteger_oracle_on_60000_seeded_random_lexemes()
    {
        var random = new Random(064); var sb = new StringBuilder();
        for (var i = 0; i < 60_000; i++)
        {
            sb.Clear();
            if (random.Next(5) == 0) sb.Append('-');
            var integerDigits = random.Next(1, 22);
            if (integerDigits == 1) sb.Append((char)('0' + random.Next(10)));
            else { sb.Append((char)('1' + random.Next(9))); for (var k = 1; k < integerDigits; k++) sb.Append(random.Next(4) == 0 ? '0' : (char)('0' + random.Next(10))); }
            if (random.Next(10) < 6)
            {
                sb.Append('.'); var fractionDigits = random.Next(1, 34);
                for (var k = 0; k < fractionDigits; k++) sb.Append(random.Next(3) == 0 ? '0' : (char)('0' + random.Next(10)));
            }
            if (random.Next(10) < 4)
            {
                sb.Append(random.Next(2) == 0 ? 'e' : 'E');
                var sign = random.Next(3); if (sign == 1) sb.Append('+'); else if (sign == 2) sb.Append('-');
                if (random.Next(4) == 0) sb.Append('0', random.Next(1, 6));       // leading zeros of the exponent
                sb.Append(random.Next(0, 70).ToString(CultureInfo.InvariantCulture));
            }
            Verify(sb.ToString());
        }
    }

    [Fact]
    public void ExactPrice_agrees_with_the_oracle_around_every_boundary_of_numeric_18_2_however_the_same_value_is_spelled()
    {
        var random = new Random(65);
        var anchors = new BigInteger[] { 0, 1, 9, 10, 99, 100, 101, 999_999_999_999_999_999, 1_000_000_000_000_000_000, 1_000_000_000_000_000_001, 999_999_999_999_999_990, 1_000_000_000_000_000_010, 12_345_678_901_234_567 };
        foreach (var anchor in anchors)
            for (var delta = -3; delta <= 3; delta++)
            {
                var cents = anchor + delta; if (cents.Sign < 0) continue;
                var digits = cents.ToString(CultureInfo.InvariantCulture).PadLeft(3, '0');
                var plain = digits[..^2] + "." + digits[^2..];                          // cents → "units.cc"
                Verify(plain); Verify("-" + plain);
                Verify(plain + new string('0', random.Next(1, 40)));                    // trailing zeros are not information
                Verify(plain + new string('0', random.Next(0, 10)) + "1");              // a non-zero digit beyond the cents
                // the same value as digits × 10^-k, spelled with every plausible exponent form
                var asInteger = cents.ToString(CultureInfo.InvariantCulture);
                Verify(asInteger + "E-2"); Verify(asInteger + "e-02"); Verify(asInteger + "0E-3"); Verify(asInteger + "00000E-0000007"); Verify(asInteger + ".0E-2");
                Verify(plain.Replace(".", "") + "E-2"); Verify("0." + digits + "E2"); Verify("0.0" + digits + "E3");
            }
        Verify("0.0000000000000000000000000001E+28");                                   // exactly 1
        Verify("0.00000000000000000000000000010E+28"); Verify("0.0000000000000000000000000001E+30"); Verify("1" + new string('0', 600) + "E-600"); Verify("1" + new string('0', 600) + "E-598");
    }

    [Fact]
    public void ExactPrice_cancelling_exponents_inside_huge_lexemes_are_judged_by_the_exact_value()
    {
        var zeros = new string('0', 899_998);
        var exact = "1" + zeros + "E-899998";                                           // = 1
        MercadoLivreConnector.ExactPrice.Parse(exact, out var one).Should().Be(MercadoLivreConnector.ExactPriceResult.Valid); one.Should().Be(1.00m); one.Scale.Should().Be(2);
        MercadoLivreConnector.ExactPrice.Parse("1" + zeros + "E-899999", out _).Should().Be(MercadoLivreConnector.ExactPriceResult.Valid, "10^899998 × 10^-899999 = 0.1, scale 1");
        MercadoLivreConnector.ExactPrice.Parse("1" + zeros + "E-900001", out _).Should().Be(MercadoLivreConnector.ExactPriceResult.PrecisionUnsupported, "10^-3 has scale 3");
        MercadoLivreConnector.ExactPrice.Parse("0." + zeros + "1E+899999", out var shifted).Should().Be(MercadoLivreConnector.ExactPriceResult.Valid); shifted.Should().Be(1.00m);
    }

    [Fact]
    public void ExactPrice_allocates_a_constant_amount_for_a_900000_digit_lexeme_and_finishes_quickly()
    {
        var lexeme = new string('9', 450_000) + "." + new string('9', 449_999) + "E-3";   // ~900k digits, a hostile but in-limit payload
        _ = MercadoLivreConnector.ExactPrice.Parse("1", out _);                          // warm up
        var before = GC.GetAllocatedBytesForCurrentThread(); var stopwatch = Stopwatch.StartNew();
        var result = MercadoLivreConnector.ExactPrice.Parse(lexeme, out _);
        stopwatch.Stop(); var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        result.Should().Be(MercadoLivreConnector.ExactPriceResult.PrecisionUnsupported);
        allocated.Should().BeLessThan(2_048, "the digit string is addressed in place: allocation must not scale with the lexeme");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    // ============================================================ C063-03 — what the caller gets when the budget is gone

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    private static Task<MarketplaceListingPage> ReadPageAsync(AdapterRig rig, CancellationToken ct = default) =>
        rig.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", AdapterRig.Secret, null), ct);

    [Fact]
    public async Task BUD_3b_when_a_wait_overshoots_the_budget_the_LAST_attempts_own_classification_is_returned_not_a_generic_one()
    {
        // last attempt answered 503: PROVIDER_UNAVAILABLE / SENT_AND_RESPONSE_RECEIVED
        var time = new ManualTimeProvider();
        var unavailable = AdapterRig.Create((_, _, _) => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, "{}")), time); unavailable.SimDelay!.Overshoot = TimeSpan.FromSeconds(100);
        var f503 = (await FluentActions.Awaiting(() => ReadPageAsync(unavailable)).Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        f503.Classification.Should().Be(MarketplaceFailureClassification.RETRYABLE); f503.SafeCode.Should().Be("PROVIDER_UNAVAILABLE"); f503.Certainty.Should().Be(MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED);
        unavailable.Requests.Should().HaveCount(1);

        // last attempt answered 429 with Retry-After: RATE_LIMITED keeps its RetryAfterUntil
        var time2 = new ManualTimeProvider();
        var limited = AdapterRig.Create((_, _, _) => Task.FromResult(Json((HttpStatusCode)429, "{}", "1")), time2); limited.SimDelay!.Overshoot = TimeSpan.FromSeconds(100);
        var f429 = (await FluentActions.Awaiting(() => ReadPageAsync(limited)).Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        f429.Classification.Should().Be(MarketplaceFailureClassification.RATE_LIMITED); f429.SafeCode.Should().Be("PROVIDER_RATE_LIMITED"); f429.RetryAfterUntil.Should().Be(limited.Clock.UtcNow.AddSeconds(1));
        limited.Requests.Should().HaveCount(1);

        // last attempt was a transport failure: unreachable, sent-or-unknown
        var time3 = new ManualTimeProvider();
        var broken = AdapterRig.Create((_, _, _) => throw new HttpRequestException("reset"), time3); broken.SimDelay!.Overshoot = TimeSpan.FromSeconds(100);
        var fNet = (await FluentActions.Awaiting(() => ReadPageAsync(broken)).Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        fNet.Classification.Should().Be(MarketplaceFailureClassification.RETRYABLE); fNet.SafeCode.Should().Be("PROVIDER_UNREACHABLE"); fNet.Certainty.Should().Be(MarketplaceSendCertainty.SENT_OR_UNKNOWN);
    }

    // ============================================================ C063-03 — the body deadline on a REAL HTTP stack (SocketsHttpHandler over loopback)

    private enum Wire { StallBody, Good, SilentNoHeaders }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(); private readonly Task _loop; private readonly Func<int, Wire> _behavior;
        private int _connections;
        public int Connections => Volatile.Read(ref _connections);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public LoopbackServer(Func<int, Wire> behavior) { _behavior = behavior; _listener.Start(); _loop = Task.Run(AcceptLoopAsync); }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var index = Interlocked.Increment(ref _connections);
                    _ = Task.Run(() => ServeAsync(client, index));
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task ServeAsync(TcpClient client, int index)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream(); var buffer = new byte[4096]; var seen = new StringBuilder();
                    while (!seen.ToString().Contains("\r\n\r\n")) { var n = await stream.ReadAsync(buffer, _stop.Token); if (n == 0) return; seen.Append(Encoding.ASCII.GetString(buffer, 0, n)); }
                    switch (_behavior(index))
                    {
                        case Wire.SilentNoHeaders: break;                                                       // accept, read the request, never answer
                        case Wire.StallBody:
                            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 1000\r\n\r\n{"), _stop.Token);
                            break;                                                                              // headers + 1 byte of a 1000-byte body, then silence
                        case Wire.Good:
                            var body = "{\"results\":[\"MLB1\"]}";
                            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n" + body), _stop.Token);
                            return;
                    }
                    while (await stream.ReadAsync(buffer, _stop.Token) > 0) { }                                  // hold until the client gives up and closes
                }
                catch (Exception) { /* the client abandoned the connection, or the server is shutting down */ }
            }
        }

        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); try { await _loop; } catch { } _stop.Dispose(); }
    }

    private static (MarketplaceSafeReadExecutor Executor, HttpClient Client) RealStack()
    {
        var sender = new MarketplaceHttpSender(NullLogger<MarketplaceHttpSender>.Instance);
        var executor = new MarketplaceSafeReadExecutor(sender, new SystemMarketplaceDelay(), new FixedJitter(TimeSpan.Zero), new TestClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z")));
        var client = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        return (executor, client);
    }

    private static Task<MarketplaceSafeReadResult> ReadAsync(MarketplaceSafeReadExecutor executor, HttpClient client, int port, TimeSpan window, CancellationToken ct = default) =>
        executor.ExecuteReadAsync(client, () => new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/users/777/items/search"), window, "PROVIDER_UNREACHABLE", "MERCADO_LIVRE", "ITEMS_SCAN", null, null, MarketplaceBoundedBodyReader.ReadAsync, ct);

    [Fact]
    public async Task Real_stack_a_body_that_stalls_after_the_headers_is_cut_by_the_attempt_window_retried_and_never_hangs()
    {
        await using var server = new LoopbackServer(_ => Wire.StallBody); var (executor, client) = RealStack(); using var _ = client;
        var stopwatch = Stopwatch.StartNew();
        var failure = (await FluentActions.Awaiting(() => ReadAsync(executor, client, server.Port, TimeSpan.FromMilliseconds(300))).Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        stopwatch.Stop();
        failure.Classification.Should().Be(MarketplaceFailureClassification.RETRYABLE); failure.Certainty.Should().Be(MarketplaceSendCertainty.SENT_OR_UNKNOWN); failure.SafeCode.Should().Be("PROVIDER_UNREACHABLE");
        server.Connections.Should().Be(3, "three attempts, each on its own connection: the stalled one was abandoned when its window expired");
        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.2), "3 × 0.3 s windows + 0.25 s + 0.5 s waits").And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(3.5));
    }

    [Fact]
    public async Task Real_stack_a_stalled_first_body_then_a_good_second_attempt_succeeds()
    {
        await using var server = new LoopbackServer(n => n == 1 ? Wire.StallBody : Wire.Good); var (executor, client) = RealStack(); using var _ = client;
        var read = await ReadAsync(executor, client, server.Port, TimeSpan.FromMilliseconds(300));
        read.Body.Should().Be("{\"results\":[\"MLB1\"]}"); server.Connections.Should().Be(2);
    }

    [Fact]
    public async Task Real_stack_a_server_that_never_sends_headers_is_cut_by_the_same_window()
    {
        await using var server = new LoopbackServer(_ => Wire.SilentNoHeaders); var (executor, client) = RealStack(); using var _ = client;
        var failure = (await FluentActions.Awaiting(() => ReadAsync(executor, client, server.Port, TimeSpan.FromMilliseconds(300))).Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.RETRYABLE); server.Connections.Should().Be(3);
    }

    [Fact]
    public async Task Real_stack_an_external_cancellation_during_a_stalled_body_ends_promptly_as_OperationCanceled_with_no_further_attempt()
    {
        await using var server = new LoopbackServer(_ => Wire.StallBody); var (executor, client) = RealStack(); using var _ = client;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var stopwatch = Stopwatch.StartNew();
        var thrown = await Record.ExceptionAsync(() => ReadAsync(executor, client, server.Port, TimeSpan.FromSeconds(10), cts.Token));
        stopwatch.Stop();
        thrown.Should().BeAssignableTo<OperationCanceledException>(); stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        server.Connections.Should().Be(1, "cancelled on the first (non-final) attempt: no second connection");
    }

    // ============================================================ C063-03 — real-time measurements with monotonic AND wall-clock evidence

    private sealed record Timing(double MonotonicSeconds, DateTimeOffset WallStartUtc, DateTimeOffset WallEndUtc)
    {
        public double WallSeconds => (WallEndUtc - WallStartUtc).TotalSeconds;
        public static async Task<Timing> MeasureAsync(Func<Task> work)
        {
            var wall = DateTimeOffset.UtcNow; var stopwatch = Stopwatch.StartNew();
            try { await work(); } catch { /* the caller inspects the outcome; timing is recorded either way */ }
            stopwatch.Stop(); return new Timing(stopwatch.Elapsed.TotalSeconds, wall, DateTimeOffset.UtcNow);
        }
    }

    [Fact]
    public async Task BUD_8b_real_time_three_attempts_whose_BODY_never_completes_finish_within_30_25_seconds_with_the_production_constants()
    {
        var stopwatch = new Stopwatch(); var trace = new List<object>();
        var rig = AdapterRig.Create((_, n, _) => Task.FromResult(ScriptedBodyContent.Ok(async (_, ct) =>
        {
            var begun = stopwatch.Elapsed.TotalSeconds;
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { lock (trace) trace.Add(new { attempt = n, begunAtSeconds = begun, cancelObservedAtSeconds = stopwatch.Elapsed.TotalSeconds }); throw; }
            return 0;
        })));
        // Only the operation is timed (see BUD_8): the stopwatch stops when the call returns, before any assertion runs.
        Exception? thrown = null; var wallStart = DateTimeOffset.UtcNow; stopwatch.Start();
        try { await ReadPageAsync(rig); } catch (Exception ex) { thrown = ex; }
        stopwatch.Stop(); var wallEnd = DateTimeOffset.UtcNow;
        var timing = new Timing(stopwatch.Elapsed.TotalSeconds, wallStart, wallEnd);
        var failure = thrown.Should().BeOfType<MarketplaceProviderCallException>().Subject.Failure;
        Evidence.Record("bud8b-real-time-stalled-body-30s", new { requests = rig.Requests.Count, monotonicSeconds = timing.MonotonicSeconds, wallSeconds = timing.WallSeconds, wallStartUtc = timing.WallStartUtc, wallEndUtc = timing.WallEndUtc, attemptTrace = trace, finalDeadlineOvershootSeconds = timing.MonotonicSeconds - 30.0, classification = failure.Classification.ToString(), certainty = failure.Certainty.ToString(), contractBudgetSeconds = 30, toleranceSeconds = 0.25 });
        rig.Requests.Should().HaveCount(3); failure.Classification.Should().Be(MarketplaceFailureClassification.RETRYABLE); failure.Certainty.Should().Be(MarketplaceSendCertainty.SENT_OR_UNKNOWN);
        timing.MonotonicSeconds.Should().BeLessThanOrEqualTo(30.25).And.BeGreaterThan(29.5);
        timing.WallSeconds.Should().BeLessThanOrEqualTo(30.30, "wall-clock agrees with the monotonic measurement (same machine, no clock adjustment expected)");
    }

    [Fact]
    public async Task BUD_10_real_time_an_external_cancellation_of_a_stalled_first_attempt_returns_promptly_with_one_request_and_no_retry()
    {
        var rig = AdapterRig.Create(async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException("unreachable"); });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        Exception? thrown = null;
        var timing = await Timing.MeasureAsync(async () => { try { await ReadPageAsync(rig, cts.Token); } catch (Exception ex) { thrown = ex; throw; } });
        Evidence.Record("bud10-real-time-external-cancel", new { requests = rig.Requests.Count, monotonicSeconds = timing.MonotonicSeconds, wallSeconds = timing.WallSeconds, wallStartUtc = timing.WallStartUtc, wallEndUtc = timing.WallEndUtc, exception = thrown?.GetType().Name, cancelledAfterSeconds = 1.5 });
        thrown.Should().BeAssignableTo<OperationCanceledException>("the caller's cancellation is honored on a non-final attempt");
        rig.Requests.Should().HaveCount(1, "no second attempt after cancellation");
        timing.MonotonicSeconds.Should().BeInRange(1.4, 2.5, "returned within moments of the cancellation, not after the 10 s window");
    }
}
