using System.Diagnostics;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Verce.Infrastructure.Marketplaces.MercadoLivre;
using Verce.Modules.Commerce;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// M067 / C067-03 — ADR-0030 §4: the wait before the next safe-read attempt is <c>d = max(base + jitter, Retry-After floor)</c> (base 250 ms then 500 ms, jitter one sample in 0..100 ms,
/// floor = RetryAfterUntil − now for a 429 only), admitted only when strictly shorter than the monotonic remaining budget; otherwise the last attempt's classification is returned.
/// Every case of the 066 boundary table is proved with SIMULATED time and a NON-ZERO jitter (the M063 suite used jitter 0, where max(base, floor) + jitter and max(base + jitter, floor)
/// coincide — which is why it never saw the Gate 065 defect), and RT-11 proves the formula on the real clock, real delay and production constants.
/// </summary>
public sealed class MercadoLivreConnectorM067WaitTests
{
    private const string ValidPage = """{"results":["MLB1"]}""";
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    private sealed class CountingJitter(TimeSpan value) : IMarketplaceJitter
    {
        public int Samples { get; private set; }
        public TimeSpan Next() { Samples++; return value; }
    }

    /// <summary>A wall clock that moves forward on EVERY read: parsing a Retry-After and computing its floor see different instants (the "negative floor" boundary).</summary>
    private sealed class AdvancingClock(DateTimeOffset start, TimeSpan step) : IClock
    {
        private long _reads;
        public DateTimeOffset UtcNow => start + step * (++_reads);
        public DateOnly OrganizationToday() => DateOnly.FromDateTime(start.UtcDateTime);
        public string OrganizationTimeZoneId => SystemClock.DefaultOrganizationTimeZoneId;
    }

    private sealed class RecordingRealDelay : IMarketplaceDelay
    {
        public List<TimeSpan> Requested { get; } = [];
        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) { Requested.Add(duration); return Task.Delay(duration, cancellationToken); }
    }

    private sealed class CancellingDelay(CancellationTokenSource caller) : IMarketplaceDelay
    {
        public List<TimeSpan> Delays { get; } = [];
        public async Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) { Delays.Add(duration); caller.Cancel(); await Task.Delay(Timeout.Infinite, cancellationToken); }
    }

    private sealed class Rig
    {
        public required MercadoLivreConnector Connector { get; init; }
        public required List<Uri> Requests { get; init; }
        public required CountingJitter Jitter { get; init; }
        public SimulatedDelay? Delay { get; init; }
        public required IClock Clock { get; init; }
        public ManualTimeProvider? Time { get; init; }

        public static Rig Create(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond, double jitterSeconds, ManualTimeProvider? time = null, IClock? clock = null, IMarketplaceDelay? delay = null)
        {
            var options = Options.Create(new MercadoLivreOptions { Enabled = true, ClientId = "900000001", RedirectUri = "https://host.example/api/commerce/marketplace-authorizations/MERCADO_LIVRE/callback", ResolvedClientSecret = "m067-client-secret" });
            clock ??= new TestClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z")); var requests = new List<Uri>(); var jitter = new CountingJitter(S(jitterSeconds));
            var handler = new Scripted(async (request, ct) => { int n; lock (requests) { requests.Add(request.RequestUri!); n = requests.Count; } return await respond(request, n, ct); });
            var sender = new MarketplaceHttpSender(Microsoft.Extensions.Logging.Abstractions.NullLogger<MarketplaceHttpSender>.Instance, time);
            var simulated = time is null || delay is not null ? null : new SimulatedDelay(time);
            var reader = new MarketplaceSafeReadExecutor(sender, delay ?? (simulated is null ? new SystemMarketplaceDelay() : simulated), jitter, clock);
            return new Rig { Connector = new MercadoLivreConnector(new One(new HttpClient(handler)), sender, reader, options, clock), Requests = requests, Jitter = jitter, Delay = simulated, Clock = clock, Time = time };
        }

        private sealed class Scripted(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
        }
        private sealed class One(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    }

    private static Task<MarketplaceListingPage> ReadPageAsync(Rig rig, CancellationToken ct = default) =>
        rig.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", AdapterRig.Secret, null), ct);

    private static Task<HttpResponseMessage> StallUntilWindow(ManualTimeProvider time, CancellationToken ct)
    {
        time.AdvanceToNextTimer(); ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("the window must have cancelled the stalled operation");
    }

    private static async Task<MarketplaceProviderFailure> FailureAsync(Rig rig) =>
        (await FluentActions.Awaiting(() => ReadPageAsync(rig)).Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;

    private static readonly List<object> Table = [];
    private static void Row(string id, object data) { lock (Table) Table.Add(new { id, data }); Evidence.Record("wait-" + id, data); }

    // ============================================================ WAIT-1 — the Gate 065 case (table case 4)

    [Fact]
    public async Task WAIT_1_Gate065_case_a_429_at_9_95_s_with_Retry_After_20_and_jitter_0_1_waits_exactly_20_s_and_the_retry_gets_a_0_05_s_window()
    {
        var time = new ManualTimeProvider();
        var rig = Rig.Create((_, n, _) => { if (n == 1) { time.Advance(S(9.95)); return Task.FromResult(Json((HttpStatusCode)429, "{}", "20")); } return Task.FromResult(Json(HttpStatusCode.OK, ValidPage)); }, 0.1, time);
        (await ReadPageAsync(rig)).ExternalListingIds.Should().Equal("MLB1");
        rig.Requests.Should().HaveCount(2, "the contract admits the retry: 20 s < 20.05 s remaining (the old max(base, floor) + jitter = 20.1 s refused it)");
        rig.Delay!.Delays.Should().Equal(S(20)); // with a dominating floor the wait IS the floor: jitter is never added on top
        rig.Time!.CreatedDueTimes.Should().Equal(S(10), S(0.05));
        rig.Time.Elapsed.Should().Be(S(29.95));
        Row("case04", new { remainingSeconds = 20.05, retryAfterSeconds = 20, baseSeconds = 0.25, jitterSeconds = 0.1, effectiveWaitSeconds = rig.Delay.Delays[0].TotalSeconds, requests = rig.Requests.Count, nextWindowSeconds = rig.Time.CreatedDueTimes[1].TotalSeconds, oldFormulaWaitSeconds = 20.1 });
    }

    // ============================================================ WAIT-2 / WAIT-3 — which bound dominates (cases 1, 2, 3, 12)

    [Fact]
    public async Task WAIT_2_a_dominating_Retry_After_floor_is_waited_exactly_not_floor_plus_jitter_and_a_zero_Retry_After_means_the_plain_backoff()
    {
        var time = new ManualTimeProvider();
        var dominating = Rig.Create((_, n, _) => { if (n == 1) { time.Advance(S(1)); return Task.FromResult(Json((HttpStatusCode)429, "{}", "5")); } return Task.FromResult(Json(HttpStatusCode.OK, ValidPage)); }, 0.1, time);
        await ReadPageAsync(dominating);
        dominating.Delay!.Delays.Should().Equal(S(5)); // case 3: d = max(0.25 + 0.1, 5) = 5 exactly (the old formula waited 5.1 s)
        Row("case03", new { remainingSeconds = 29, retryAfterSeconds = 5, baseSeconds = 0.25, jitterSeconds = 0.1, effectiveWaitSeconds = 5.0, requests = 2 });

        var time0 = new ManualTimeProvider();
        var zero = Rig.Create((_, n, _) => Task.FromResult(n == 1 ? Json((HttpStatusCode)429, "{}", "0") : Json(HttpStatusCode.OK, ValidPage)), 0, time0);
        await ReadPageAsync(zero);
        zero.Delay!.Delays.Should().Equal(S(0.25)); // case 1: Retry-After 0 is no future instant (parser returns none): the plain 250 ms backoff
        Row("case01", new { remainingSeconds = 30, retryAfterSeconds = 0, baseSeconds = 0.25, jitterSeconds = 0, effectiveWaitSeconds = 0.25, requests = 2 });
    }

    [Fact]
    public async Task WAIT_3_when_base_plus_jitter_exceeds_the_floor_or_there_is_no_floor_the_wait_is_base_plus_jitter()
    {
        var time = new ManualTimeProvider();
        var plain = Rig.Create((_, n, _) => { if (n == 1) { time.Advance(S(1)); return Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, "{}")); } return Task.FromResult(Json(HttpStatusCode.OK, ValidPage)); }, 0.08, time);
        await ReadPageAsync(plain);
        plain.Delay!.Delays.Should().Equal(S(0.33)); // case 2: 5xx, 0.25 + 0.08, remaining 29 s
        Row("case02", new { remainingSeconds = 29, baseSeconds = 0.25, jitterSeconds = 0.08, effectiveWaitSeconds = 0.33, requests = 2 });

        // case 12: a floor of 0.2 s (HTTP-date one second after a wall clock at .8) is smaller than 0.25 + 0.03
        var time2 = new ManualTimeProvider(); var clock = new TestClock(DateTimeOffset.Parse("2026-01-01T00:00:00.8Z"));
        var smallFloor = Rig.Create((_, n, _) => Task.FromResult(n == 1 ? Json((HttpStatusCode)429, "{}", "Thu, 01 Jan 2026 00:00:01 GMT") : Json(HttpStatusCode.OK, ValidPage)), 0.03, time2, clock);
        await ReadPageAsync(smallFloor);
        smallFloor.Delay!.Delays.Should().Equal(S(0.28)); // case 12: max(0.25 + 0.03, 0.2) = 0.28
        Row("case12", new { remainingSeconds = 30, floorSeconds = 0.2, baseSeconds = 0.25, jitterSeconds = 0.03, effectiveWaitSeconds = 0.28, requests = 2 });
    }

    // ============================================================ WAIT-4 / WAIT-5 — the jitter and the budget (cases 8, 9, 10, 11)

    /// <summary>Attempt 1 consumes <paramref name="firstSeconds"/> then answers 429 Retry-After 19 (floor dominates: wait 19 s); attempt 2 stalls its whole 10 s window. Remaining after attempt 2 = 30 − firstSeconds − 29.</summary>
    private static async Task<(Rig Rig, int Requests)> AfterTwoAttemptsAsync(double firstSeconds, double jitterSeconds)
    {
        var time = new ManualTimeProvider();
        var rig = Rig.Create((_, n, ct) =>
        {
            if (n == 1) { time.Advance(S(firstSeconds)); return Task.FromResult(Json((HttpStatusCode)429, "{}", "19")); }
            return StallUntilWindow(time, ct);
        }, jitterSeconds, time);
        await FluentActions.Awaiting(() => ReadPageAsync(rig)).Should().ThrowAsync<MarketplaceProviderCallException>();
        return (rig, rig.Requests.Count);
    }

    [Fact]
    public async Task WAIT_4_a_jitter_that_makes_the_backoff_reach_the_remaining_budget_is_refused_and_one_that_fits_is_taken()
    {
        // case 8: remaining 0.55 s after attempt 2; 0.5 + 0.06 = 0.56 >= 0.55 → refused, last classification (the stalled attempt: RETRYABLE / PROVIDER_UNREACHABLE)
        var refused = await AfterTwoAttemptsAsync(0.45, 0.06);
        refused.Requests.Should().Be(2); refused.Rig.Delay!.Delays.Should().Equal(S(19));
        Row("case08", new { remainingSeconds = 0.55, baseSeconds = 0.5, jitterSeconds = 0.06, effectiveWaitSeconds = 0.56, requests = 2, outcome = "refused: last classification" });

        // case 9: 0.5 + 0.04 = 0.54 < 0.55 → taken; the third attempt's window is the 0.01 s that is left
        var taken = await AfterTwoAttemptsAsync(0.45, 0.04);
        taken.Requests.Should().Be(3); taken.Rig.Delay!.Delays.Should().Equal(S(19), S(0.54)); taken.Rig.Time!.CreatedDueTimes.Should().Equal(S(10), S(10), S(0.01));
        Row("case09", new { remainingSeconds = 0.55, baseSeconds = 0.5, jitterSeconds = 0.04, effectiveWaitSeconds = 0.54, requests = 3, thirdWindowSeconds = 0.01 });

        // the refused case returns the LAST attempt's own classification
        var time = new ManualTimeProvider();
        var rig = Rig.Create((_, n, ct) => { if (n == 1) { time.Advance(S(0.45)); return Task.FromResult(Json((HttpStatusCode)429, "{}", "19")); } return StallUntilWindow(time, ct); }, 0.06, time);
        var failure = await FailureAsync(rig);
        failure.Classification.Should().Be(MarketplaceFailureClassification.RETRYABLE); failure.SafeCode.Should().Be("PROVIDER_UNREACHABLE"); failure.Certainty.Should().Be(MarketplaceSendCertainty.SENT_OR_UNKNOWN);
    }

    [Fact]
    public async Task WAIT_5_a_wait_that_EQUALS_the_remaining_budget_is_refused_and_so_is_one_with_almost_nothing_left()
    {
        var equal = await AfterTwoAttemptsAsync(0.4, 0.1);     // remaining 0.6; 0.5 + 0.1 = 0.6 → not strictly shorter
        equal.Requests.Should().Be(2); equal.Rig.Delay!.Delays.Should().Equal(S(19));
        Row("case10", new { remainingSeconds = 0.6, baseSeconds = 0.5, jitterSeconds = 0.1, effectiveWaitSeconds = 0.6, requests = 2, outcome = "refused: equality" });

        var nearly = await AfterTwoAttemptsAsync(0.8, 0);       // remaining 0.2; 0.5 ≥ 0.2
        nearly.Requests.Should().Be(2);
        Row("case11", new { remainingSeconds = 0.2, baseSeconds = 0.5, jitterSeconds = 0, effectiveWaitSeconds = 0.5, requests = 2, outcome = "refused: almost exhausted" });
    }

    // ============================================================ WAIT-6 — Retry-After against the budget, with jitter (cases 5, 6, 7)

    [Theory]
    [InlineData("case05", 9.0, "21", 21.0, "the wait equals what is left")]
    [InlineData("case06", 2.0, "29", 28.0, "the wait is longer than what is left")]
    [InlineData("case07", 0.0, "3600", 30.0, "Retry-After far beyond the whole budget")]
    public async Task WAIT_6_a_Retry_After_that_does_not_fit_is_never_slept_the_RATE_LIMITED_failure_keeps_its_RetryAfterUntil(string id, double spentSeconds, string retryAfter, double remainingSeconds, string why)
    {
        var time = new ManualTimeProvider();
        var rig = Rig.Create((_, _, _) => { if (spentSeconds > 0) time.Advance(S(spentSeconds)); return Task.FromResult(Json((HttpStatusCode)429, "{}", retryAfter)); }, 0.1, time);
        var failure = await FailureAsync(rig);
        failure.Classification.Should().Be(MarketplaceFailureClassification.RATE_LIMITED); failure.SafeCode.Should().Be("PROVIDER_RATE_LIMITED");
        failure.RetryAfterUntil.Should().Be(rig.Clock.UtcNow.AddSeconds(int.Parse(retryAfter)), why);
        rig.Requests.Should().ContainSingle(); rig.Delay!.Delays.Should().BeEmpty();
        Row(id, new { remainingSeconds, retryAfterSeconds = int.Parse(retryAfter), baseSeconds = 0.25, jitterSeconds = 0.1, requests = 1, outcome = "refused: RATE_LIMITED + RetryAfterUntil", why });
    }

    // ============================================================ WAIT-7 — no wait (and no jitter sample) after the last attempt (cases 15, 16)

    [Fact]
    public async Task WAIT_7_after_the_third_attempt_there_is_no_wait_and_no_jitter_sample_and_the_last_classification_is_returned()
    {
        var time = new ManualTimeProvider();
        var last429 = Rig.Create((_, n, _) => Task.FromResult(n < 3 ? Json(HttpStatusCode.ServiceUnavailable, "{}") : Json((HttpStatusCode)429, "{}", "1")), 0.1, time);
        var f429 = await FailureAsync(last429);
        f429.Classification.Should().Be(MarketplaceFailureClassification.RATE_LIMITED); f429.RetryAfterUntil.Should().Be(last429.Clock.UtcNow.AddSeconds(1));
        last429.Requests.Should().HaveCount(3); last429.Delay!.Delays.Should().Equal(S(0.35), S(0.6)); last429.Jitter.Samples.Should().Be(2, "one sample per wait decision, none after the final attempt");
        Row("case15", new { attempt = 3, requests = 3, waits = last429.Delay.Delays.Select(d => d.TotalSeconds), jitterSamples = last429.Jitter.Samples, outcome = "RATE_LIMITED + RetryAfterUntil" });

        var time2 = new ManualTimeProvider();
        var last5xx = Rig.Create((_, _, _) => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, "{}")), 0.1, time2);
        var f5xx = await FailureAsync(last5xx);
        f5xx.Classification.Should().Be(MarketplaceFailureClassification.RETRYABLE); f5xx.SafeCode.Should().Be("PROVIDER_UNAVAILABLE"); f5xx.Certainty.Should().Be(MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED);
        last5xx.Requests.Should().HaveCount(3); last5xx.Jitter.Samples.Should().Be(2);
        Row("case16", new { attempt = 3, requests = 3, jitterSamples = last5xx.Jitter.Samples, outcome = "RETRYABLE / PROVIDER_UNAVAILABLE" });
    }

    // ============================================================ WAIT-8 — external cancellation during the wait (case 17)

    [Fact]
    public async Task WAIT_8_an_external_cancellation_during_a_Retry_After_wait_ends_as_OperationCanceled_after_one_request_never_as_a_provider_failure()
    {
        using var caller = new CancellationTokenSource(); var delay = new CancellingDelay(caller); var time = new ManualTimeProvider();
        var rig = Rig.Create((_, _, _) => { time.Advance(S(1)); return Task.FromResult(Json((HttpStatusCode)429, "{}", "5")); }, 0.1, time, delay: delay);
        var thrown = await Record.ExceptionAsync(() => ReadPageAsync(rig, caller.Token));
        thrown.Should().BeAssignableTo<OperationCanceledException>(); rig.Requests.Should().ContainSingle(); delay.Delays.Should().Equal(S(5));
        Row("case17", new { requests = 1, waitAskedSeconds = 5, exception = thrown!.GetType().Name });
    }

    // ============================================================ WAIT-9 / WAIT-10 — a floor that is already in the past, and a Retry-After on a 5xx (cases 13, 14)

    [Fact]
    public async Task WAIT_9_a_floor_that_became_negative_while_the_response_was_processed_never_dominates()
    {
        var clock = new AdvancingClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), S(1.3)); var time = new ManualTimeProvider();
        var rig = Rig.Create((_, n, _) => Task.FromResult(n == 1 ? Json((HttpStatusCode)429, "{}", "1") : Json(HttpStatusCode.OK, ValidPage)), 0.02, time, clock);
        await ReadPageAsync(rig);
        rig.Delay!.Delays.Should().Equal(S(0.27)); // case 13: Retry-After 1 s parsed at T, the wall clock then moved 1.3 s: floor = −0.3 s, so d = 0.25 + 0.02
        Row("case13", new { floorSeconds = -0.3, baseSeconds = 0.25, jitterSeconds = 0.02, effectiveWaitSeconds = 0.27, requests = 2 });
    }

    [Fact]
    public async Task WAIT_10_a_Retry_After_header_on_a_5xx_is_ignored_only_a_429_has_a_floor()
    {
        var time = new ManualTimeProvider();
        var rig = Rig.Create((_, n, _) => Task.FromResult(n == 1 ? Json(HttpStatusCode.ServiceUnavailable, "{}", "30") : Json(HttpStatusCode.OK, ValidPage)), 0.05, time);
        await ReadPageAsync(rig);
        rig.Delay!.Delays.Should().Equal(S(0.30)); // case 14
        Row("case14", new { remainingSeconds = 30, ignoredRetryAfterSeconds = 30, baseSeconds = 0.25, jitterSeconds = 0.05, effectiveWaitSeconds = 0.30, requests = 2 });
    }

    [Fact]
    public async Task WAIT_regression_with_jitter_zero_the_two_formulas_coincide_so_the_whole_M063_matrix_keeps_its_exact_waits()
    {
        var time = new ManualTimeProvider();
        var rig = Rig.Create((_, n, _) => Task.FromResult(n < 3 ? Json(HttpStatusCode.ServiceUnavailable, "{}") : Json(HttpStatusCode.OK, ValidPage)), 0, time);
        await ReadPageAsync(rig);
        rig.Delay!.Delays.Should().Equal(S(0.25), S(0.5));
    }

    // ============================================================ RT-11 — the formula on the real clock, real delay, production constants

    [Fact]
    public async Task RT_11_real_time_a_429_with_Retry_After_2_and_jitter_0_9_waits_about_2_seconds_not_2_9()
    {
        // base 0.25 + jitter 0.9 = 1.15 < floor (≈ 2 s): d = floor. The old formula asked for max(0.25, ≈2) + 0.9 ≈ 2.9 s.
        // M068: the discriminating fact is the wait the EXECUTOR REQUESTS from the real delay (immune to scheduler load); the measured interval between the two requests is the real-clock
        // sanity check and only needs a lower bound plus a generous allowance for a loaded machine (a 24-job CPU storm added ≈ 0.33 s in the review).
        var stopwatch = new Stopwatch(); double? firstAnswered = null, secondStarted = null; var wallStart = DateTimeOffset.UtcNow; var delay = new RecordingRealDelay();
        var rig = Rig.Create((_, n, _) =>
        {
            if (n == 1) { firstAnswered = stopwatch.Elapsed.TotalSeconds; return Task.FromResult(Json((HttpStatusCode)429, "{}", "2")); }
            secondStarted = stopwatch.Elapsed.TotalSeconds; return Task.FromResult(Json(HttpStatusCode.OK, ValidPage));
        }, 0.9, clock: new SystemClock(), delay: delay);
        stopwatch.Start(); await ReadPageAsync(rig); stopwatch.Stop();
        var interval = secondStarted!.Value - firstAnswered!.Value; var requested = delay.Requested.Single().TotalSeconds;
        Evidence.Record("rt11-real-time-retry-after", new { requests = rig.Requests.Count, retryAfterSeconds = 2, jitterSeconds = 0.9, baseSeconds = 0.25, requestedWaitSeconds = requested, intervalBetweenAttemptsSeconds = interval, totalSeconds = stopwatch.Elapsed.TotalSeconds, wallStartUtc = wallStart, wallEndUtc = DateTimeOffset.UtcNow, oldFormulaRequestedAtLeastSeconds = 2.89, newFormulaRequestedSeconds = "1.50..2.00" });
        rig.Requests.Should().HaveCount(2);
        requested.Should().BeInRange(1.50, 2.00, "d = the Retry-After floor (2 s minus the time spent since it was parsed, up to ≈ 50 ms under a CPU storm): above the 1.15 s of base + jitter alone, below the ≥ 2.89 s of the old formula");
        interval.Should().BeGreaterThanOrEqualTo(requested - 0.05).And.BeLessThan(requested + 1.0, "the real delay really waited what was requested (generous upper bound: scheduler latency)");
        rig.Jitter.Samples.Should().Be(1);
    }
}
