using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Verce.Infrastructure.Marketplaces.MercadoLivre;
using Verce.Modules.Commerce;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// M063 / C063-03 — ADR-0029 §4: the safe-read budget is 30 s PER OPERATION on a monotonic clock, every attempt window is min(10 s, remaining) and covers send, headers
/// AND the bounded body read, waits (backoff, Retry-After) consume the same budget, no wait that is not strictly shorter than the remaining budget is taken, and the token
/// POST's single 20 s window covers its body. BUD-1..BUD-7 and BUD-9 use SIMULATED time (exact, no tolerance); BUD-8 and BUD-9b measure real time with the production constants.
/// </summary>
public sealed class MercadoLivreConnectorM063BudgetTests
{
    private static readonly byte[] Secret = AdapterRig.Secret;
    private static readonly TimeSpan S10 = TimeSpan.FromSeconds(10);
    private const string ValidPage = """{"results":["MLB1"]}""";
    private static HttpResponseMessage Json(HttpStatusCode status, string body, string? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    private static Task<MarketplaceListingPage> ReadPageAsync(AdapterRig rig, CancellationToken ct = default) =>
        rig.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, null), ct);

    /// <summary>A stall that "waits out" its own attempt window: simulated time jumps to the window's deadline, which cancels the token the stalled operation is observing.</summary>
    private static Task<HttpResponseMessage> StallUntilWindowAsync(ManualTimeProvider time, CancellationToken ct)
    {
        time.AdvanceToNextTimer();
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("the window must have cancelled the stalled operation");
    }

    private static ValueTask<int> StallBodyUntilWindow(ManualTimeProvider time, CancellationToken ct)
    {
        time.AdvanceToNextTimer();
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("the window must have cancelled the stalled body read");
    }

    // ============================================================ BUD-1..BUD-3 — attempts, waits and the exact budget (simulated time)

    [Fact]
    public async Task BUD_1_three_stalled_attempts_use_windows_10_10_and_9_25_with_waits_250_and_500_totalling_exactly_30_seconds_and_no_fourth_attempt()
    {
        var time = new ManualTimeProvider();
        var rig = AdapterRig.Create((_, _, ct) => StallUntilWindowAsync(time, ct), time);
        var failure = (await FluentActions.Awaiting(() => ReadPageAsync(rig)).Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;

        failure.Classification.Should().Be(MarketplaceFailureClassification.RETRYABLE); failure.SafeCode.Should().Be("PROVIDER_UNREACHABLE"); failure.Certainty.Should().Be(MarketplaceSendCertainty.SENT_OR_UNKNOWN);
        rig.Requests.Should().HaveCount(3);
        rig.SimDelay!.Delays.Should().Equal(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500));
        // windows are min(10 s, remaining): the third attempt gets only what is left of the budget
        time.CreatedDueTimes.Should().Equal(S10, S10, TimeSpan.FromSeconds(9.25));
        time.Elapsed.Should().Be(TimeSpan.FromSeconds(30), "10 + 0.25 + 10 + 0.5 + 9.25: the whole operation, exactly the budget, zero tolerance");
    }

    [Fact]
    public async Task BUD_2_a_wait_that_is_not_strictly_shorter_than_the_remaining_budget_is_never_taken_and_the_last_classification_is_returned()
    {
        // 9 s spent inside the first window, then a 429 whose Retry-After equals exactly what is left (21 s): the wait is refused (not strictly shorter).
        var time = new ManualTimeProvider();
        var refused = AdapterRig.Create((_, _, _) => { time.Advance(TimeSpan.FromSeconds(9)); return Task.FromResult(Json((HttpStatusCode)429, "{}", "21")); }, time);
        var failure = (await FluentActions.Awaiting(() => ReadPageAsync(refused)).Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.RATE_LIMITED); failure.RetryAfterUntil.Should().Be(refused.Clock.UtcNow.AddSeconds(21));
        refused.Requests.Should().HaveCount(1); refused.SimDelay!.Delays.Should().BeEmpty();
        time.Elapsed.Should().Be(TimeSpan.FromSeconds(9));

        // one second less is strictly shorter than what is left: the wait IS taken, and the next attempt's window is only what remains after it
        var time2 = new ManualTimeProvider();
        var taken = AdapterRig.Create((_, n, _) => { if (n == 1) { time2.Advance(TimeSpan.FromSeconds(9)); return Task.FromResult(Json((HttpStatusCode)429, "{}", "20")); } return Task.FromResult(Json(HttpStatusCode.OK, ValidPage)); }, time2);
        (await ReadPageAsync(taken)).ExternalListingIds.Should().Equal("MLB1");
        taken.SimDelay!.Delays.Should().Equal(TimeSpan.FromSeconds(20)); // after 9 s + 20 s only 1 s of the budget is left for the retry
        time2.CreatedDueTimes.Should().Equal(S10, TimeSpan.FromSeconds(1));

        // the same rule for backoff: a slow first attempt (9.9 s of 10) and a second that eats its whole window leave 9.35 s after the 500 ms wait for the third
        var time3 = new ManualTimeProvider();
        var slow = AdapterRig.Create((_, n, ct) =>
        {
            if (n == 1) { time3.Advance(TimeSpan.FromSeconds(9.9)); return Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, "{}")); }
            return StallUntilWindowAsync(time3, ct);
        }, time3);
        await FluentActions.Awaiting(() => ReadPageAsync(slow)).Should().ThrowAsync<MarketplaceProviderCallException>();
        slow.Requests.Should().HaveCount(3); time3.Elapsed.Should().Be(TimeSpan.FromSeconds(30), "9.9 + 0.25 + 10 + 0.5 + 9.35");
        time3.CreatedDueTimes.Should().Equal(S10, S10, TimeSpan.FromSeconds(9.35));
    }

    [Fact]
    public async Task BUD_3_no_attempt_starts_when_nothing_is_left_of_the_budget()
    {
        // A wait that overshoots (a real delay can only ever overshoot) leaves nothing: the loop-top guard refuses a new attempt instead of opening a zero-length window.
        var time = new ManualTimeProvider();
        var rig = AdapterRig.Create((_, _, _) => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, "{}")), time);
        rig.SimDelay!.Overshoot = TimeSpan.FromSeconds(100);
        var failure = (await FluentActions.Awaiting(() => ReadPageAsync(rig)).Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.RETRYABLE);
        rig.Requests.Should().HaveCount(1, "remaining <= 0 after the wait: no second attempt may begin"); rig.SimDelay.Delays.Should().HaveCount(1);
        time.CreatedDueTimes.Should().ContainSingle();
    }

    // ============================================================ BUD-4/BUD-5 — the body read is inside the window and inside the retry loop

    [Fact]
    public async Task BUD_4_fast_headers_with_a_body_that_never_completes_expire_the_window_and_are_retried_inside_the_same_30_second_budget()
    {
        var time = new ManualTimeProvider();
        var rig = AdapterRig.Create((_, _, _) => Task.FromResult(ScriptedBodyContent.Ok((_, ct) => StallBodyUntilWindow(time, ct))), time);
        var failure = (await FluentActions.Awaiting(() => ReadPageAsync(rig)).Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.RETRYABLE); failure.Certainty.Should().Be(MarketplaceSendCertainty.SENT_OR_UNKNOWN);
        rig.Requests.Should().HaveCount(3);
        time.Elapsed.Should().Be(TimeSpan.FromSeconds(30), "the stalled BODY consumes the window exactly like a stalled send: before M063 it had no deadline at all");
        // one structured event per attempt, each carrying the attempt's real classification and no payload
        var events = rig.Logger.Entries.Where(e => (string)e.State["Operation"]! == "ITEMS_SCAN").ToList();
        events.Should().HaveCount(3); events.Select(e => e.State["Classification"]).Should().OnlyContain(c => (string?)c == "RETRYABLE"); events.Select(e => (int)e.State["Attempt"]!).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task BUD_4b_a_stalled_first_body_then_a_good_second_attempt_succeeds_after_one_window_plus_one_backoff()
    {
        var time = new ManualTimeProvider();
        var rig = AdapterRig.Create((_, n, _) => Task.FromResult(n == 1 ? ScriptedBodyContent.Ok((_, ct) => StallBodyUntilWindow(time, ct)) : ScriptedBodyContent.OkText(ValidPage)), time);
        var page = await ReadPageAsync(rig);
        page.ExternalListingIds.Should().Equal("MLB1");
        rig.Requests.Should().HaveCount(2); rig.SimDelay!.Delays.Should().Equal(TimeSpan.FromMilliseconds(250));
        time.Elapsed.Should().Be(TimeSpan.FromSeconds(10.25));
    }

    [Fact]
    public async Task BUD_4c_a_stalled_bulk_body_is_retried_inside_the_budget_and_keeps_one_bulk_evidence_event_per_attempt()
    {
        var time = new ManualTimeProvider();
        var valid = """[{"id":"MLB1","status_code":200,"body":{"id":"MLB1","seller_id":777,"site_id":"MLB","status":"active"}}]""";
        var rig = AdapterRig.Create((_, n, _) => Task.FromResult(n == 1 ? ScriptedBodyContent.Ok((_, ct) => StallBodyUntilWindow(time, ct)) : ScriptedBodyContent.OkText(valid)), time);
        var results = await rig.Connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", Secret, ["MLB1"]), CancellationToken.None);
        results.Single().Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
        var events = rig.Logger.Entries.Where(e => (string)e.State["Operation"]! == "ITEMS_BULK" && e.State.ContainsKey("RequestedIdCount")).ToList();
        events.Should().HaveCount(2, "attempt 1 (stalled body, zero evidence) and attempt 2 (the final, with the real envelope counts)");
        events[0].State["Classification"].Should().Be("RETRYABLE"); events[0].State["EnvelopeCount"].Should().Be(0);
        events[1].State["Classification"].Should().Be("SUCCESS"); events[1].State["EnvelopeCount"].Should().Be(1); events[1].State["Attempt"].Should().Be(2);
    }

    [Fact]
    public async Task BUD_5_a_truncated_body_is_retryable_and_retried_while_an_oversized_body_is_final_and_never_retried()
    {
        var time = new ManualTimeProvider();
        var truncated = AdapterRig.Create((_, n, _) => Task.FromResult(n == 1 ? ScriptedBodyContent.Ok((_, _) => throw new IOException("connection reset mid-body")) : ScriptedBodyContent.OkText(ValidPage)), time);
        (await ReadPageAsync(truncated)).ExternalListingIds.Should().Equal("MLB1");
        truncated.Requests.Should().HaveCount(2, "a truncated body is an incomplete response: RETRYABLE"); truncated.SimDelay!.Delays.Should().Equal(TimeSpan.FromMilliseconds(250));

        var oversizedDeclared = AdapterRig.Create((_, _, _) => Task.FromResult(ScriptedBodyContent.Ok((_, _) => throw new InvalidOperationException("must not be read"), declaredLength: 2 * 1024 * 1024)), new ManualTimeProvider());
        var failure = (await FluentActions.Awaiting(() => ReadPageAsync(oversizedDeclared)).Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.NON_RETRYABLE); failure.SafeCode.Should().Be("RESPONSE_TOO_LARGE");
        oversizedDeclared.Requests.Should().HaveCount(1);
        var events = oversizedDeclared.Logger.Entries.Where(e => (string)e.State["Operation"]! == "ITEMS_SCAN").ToList();
        events.Should().ContainSingle().Which.State["Classification"].Should().Be("RESPONSE_TOO_LARGE");

        var oversizedStreamed = AdapterRig.Create((_, _, _) => Task.FromResult(ScriptedBodyContent.Ok((buffer, _) => { buffer.Span.Fill((byte)'a'); return ValueTask.FromResult(buffer.Length); })), new ManualTimeProvider());
        (await FluentActions.Awaiting(() => ReadPageAsync(oversizedStreamed)).Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure.SafeCode.Should().Be("RESPONSE_TOO_LARGE");
        oversizedStreamed.Requests.Should().HaveCount(1, "an endless body is cut by the 1 MiB bound, finally");
    }

    // ============================================================ BUD-6 — Retry-After consumes the budget

    [Fact]
    public async Task BUD_6_Retry_After_inside_the_budget_is_waited_and_consumes_it_while_one_beyond_what_is_left_returns_RATE_LIMITED_immediately()
    {
        var time = new ManualTimeProvider();
        var within = AdapterRig.Create((_, n, _) => Task.FromResult(n == 1 ? Json((HttpStatusCode)429, "{}", "5") : Json(HttpStatusCode.OK, ValidPage)), time);
        (await ReadPageAsync(within)).ExternalListingIds.Should().Equal("MLB1");
        within.SimDelay!.Delays.Should().Equal(TimeSpan.FromSeconds(5)); time.Elapsed.Should().Be(TimeSpan.FromSeconds(5));

        var time2 = new ManualTimeProvider();
        var beyond = AdapterRig.Create((_, _, _) => Task.FromResult(Json((HttpStatusCode)429, "{}", "3600")), time2);
        var failure = (await FluentActions.Awaiting(() => ReadPageAsync(beyond)).Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.RATE_LIMITED); failure.RetryAfterUntil.Should().Be(beyond.Clock.UtcNow.AddSeconds(3600));
        beyond.Requests.Should().HaveCount(1); beyond.SimDelay!.Delays.Should().BeEmpty(); time2.Elapsed.Should().Be(TimeSpan.Zero);

        // time already spent by the attempt itself counts: 2 s spent + a 29 s Retry-After cannot fit in what is left (28 s)
        var time3 = new ManualTimeProvider();
        var spent = AdapterRig.Create((_, _, _) => { time3.Advance(TimeSpan.FromSeconds(2)); return Task.FromResult(Json((HttpStatusCode)429, "{}", "29")); }, time3);
        (await FluentActions.Awaiting(() => ReadPageAsync(spent)).Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure.Classification.Should().Be(MarketplaceFailureClassification.RATE_LIMITED);
        spent.Requests.Should().HaveCount(1); spent.SimDelay!.Delays.Should().BeEmpty();
    }

    // ============================================================ BUD-7 — cancellation (R2 unchanged)

    [Fact]
    public async Task BUD_7_cancellation_pre_send_not_final_and_final_attempt_follow_ADR_0025_F6_exactly()
    {
        // pre-send: nothing is sent, NOT_SENT path
        var pre = AdapterRig.Create((_, _, _) => Task.FromResult(Json(HttpStatusCode.OK, ValidPage)), new ManualTimeProvider());
        using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); await FluentActions.Awaiting(() => ReadPageAsync(pre, cancelled.Token)).Should().ThrowAsync<MarketplaceRequestNotSentException>(); }
        pre.Requests.Should().BeEmpty();

        // non-final attempt in flight: OperationCanceledException immediately, no further attempt, no dependence on the backoff wait
        foreach (var attempt in new[] { 1, 2 })
        {
            using var cts = new CancellationTokenSource(); var time = new ManualTimeProvider();
            var rig = AdapterRig.Create((_, n, _) => { if (n == attempt) { cts.Cancel(); throw new TaskCanceledException("caller cancelled", null, cts.Token); } return Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, "{}")); }, time);
            (await Record.ExceptionAsync(() => ReadPageAsync(rig, cts.Token))).Should().BeAssignableTo<OperationCanceledException>();
            rig.Requests.Should().HaveCount(attempt);
            rig.SimDelay!.Delays.Count.Should().Be(attempt - 1, "the cancelled attempt triggered no further wait");
        }

        // cancelled while the BODY is being read, non-final: same
        using (var cts = new CancellationTokenSource())
        {
            var rig = AdapterRig.Create((_, _, _) => Task.FromResult(ScriptedBodyContent.Ok((_, _) => { cts.Cancel(); throw new TaskCanceledException("caller cancelled mid-body", null, cts.Token); })), new ManualTimeProvider());
            (await Record.ExceptionAsync(() => ReadPageAsync(rig, cts.Token))).Should().BeAssignableTo<OperationCanceledException>();
            rig.Requests.Should().HaveCount(1);
        }

        // final attempt in flight: R2 — RETRYABLE / SENT_OR_UNKNOWN (the frozen "any exception after SendAsync" row), unchanged
        using (var cts = new CancellationTokenSource())
        {
            var rig = AdapterRig.Create((_, n, _) => { if (n == 3) { cts.Cancel(); throw new TaskCanceledException("caller cancelled", null, cts.Token); } return Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, "{}")); }, new ManualTimeProvider());
            var failure = (await FluentActions.Awaiting(() => ReadPageAsync(rig, cts.Token)).Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
            failure.Classification.Should().Be(MarketplaceFailureClassification.RETRYABLE); failure.Certainty.Should().Be(MarketplaceSendCertainty.SENT_OR_UNKNOWN);
            rig.Requests.Should().HaveCount(3);
        }
    }

    // ============================================================ BUD-8 — real time, production constants (the Gate 061 scenario)

    [Fact]
    public async Task BUD_8_real_time_three_stalled_attempts_with_the_production_constants_finish_within_30_25_seconds()
    {
        // The Gate 061 probe: a wire that never answers until its attempt is cancelled, real clock, real delay, jitter 0. Before M063: 30.8524341 s.
        // Per-attempt trace (monotonic offsets from the start of the call): when each stalled attempt began and when ITS cancellation reached the handler. It separates the executor's own logic
        // (windows are min(10 s, remaining), so latency never accumulates) from scheduling latency of the single final deadline firing.
        var stopwatch = new Stopwatch(); var trace = new List<object>();
        var rig = AdapterRig.Create(async (_, n, ct) =>
        {
            var begun = stopwatch.Elapsed.TotalSeconds;
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { lock (trace) trace.Add(new { attempt = n, begunAtSeconds = begun, cancelObservedAtSeconds = stopwatch.Elapsed.TotalSeconds, windowSeconds = stopwatch.Elapsed.TotalSeconds - begun }); throw; }
            throw new InvalidOperationException("unreachable");
        });
        // ONLY the operation is timed: the stopwatch stops the instant the call returns, before any assertion machinery runs. (M064: timing an assertion framework's cold-start JIT inside the
        // measured region produced a spurious 30.29 s on a fresh process; the handler had observed the final deadline at 30.0175 s.)
        Exception? thrown = null; var wallStart = DateTimeOffset.UtcNow; stopwatch.Start();
        try { await ReadPageAsync(rig); } catch (Exception ex) { thrown = ex; }
        stopwatch.Stop(); var wallEnd = DateTimeOffset.UtcNow;
        var failure = thrown.Should().BeOfType<MarketplaceProviderCallException>().Subject.Failure;
        Evidence.Record("bud8-real-time-30s", new { attempts = rig.Requests.Count, elapsedSeconds = stopwatch.Elapsed.TotalSeconds, monotonicSeconds = stopwatch.Elapsed.TotalSeconds, wallSeconds = (wallEnd - wallStart).TotalSeconds, wallStartUtc = wallStart, wallEndUtc = wallEnd, attemptTrace = trace, finalDeadlineOvershootSeconds = stopwatch.Elapsed.TotalSeconds - 30.0, returnPathAfterLastCancelSeconds = stopwatch.Elapsed.TotalSeconds - ((dynamic)trace[^1]).cancelObservedAtSeconds, classification = failure.Classification.ToString(), certainty = failure.Certainty.ToString(), contractBudgetSeconds = 30, toleranceSeconds = 0.25, gate061BaselineSeconds = 30.8524341 });
        rig.Requests.Should().HaveCount(3); failure.Classification.Should().Be(MarketplaceFailureClassification.RETRYABLE); failure.Certainty.Should().Be(MarketplaceSendCertainty.SENT_OR_UNKNOWN);
        stopwatch.Elapsed.TotalSeconds.Should().BeLessThanOrEqualTo(30.25).And.BeGreaterThan(29.5, "the three windows really were waited out: this is a measurement, not a shortcut");
    }

    // ============================================================ BUD-9 — pinned constants and the token POST window

    [Fact]
    public void BUD_9a_the_frozen_constants_are_pinned()
    {
        MarketplaceSafeReadExecutor.Budget.Should().Be(TimeSpan.FromSeconds(30));
        MarketplaceSafeReadExecutor.AttemptWindow.Should().Be(TimeSpan.FromSeconds(10));
        MercadoLivreConnector.TokenWindow.Should().Be(TimeSpan.FromSeconds(20));
        MarketplaceSafeReadExecutor.MaxAttempts.Should().Be(3);
        var jitter = new SystemMarketplaceJitter(); var samples = Enumerable.Range(0, 4000).Select(_ => jitter.Next()).ToArray();
        samples.Should().OnlyContain(s => s >= TimeSpan.Zero && s <= TimeSpan.FromMilliseconds(100));
        samples.Min().Should().BeLessThan(TimeSpan.FromMilliseconds(10)); samples.Max().Should().BeGreaterThan(TimeSpan.FromMilliseconds(90));
    }

    [Fact]
    public async Task BUD_9b_a_token_POST_whose_body_never_completes_is_cut_at_exactly_its_single_20_second_window_with_one_request_and_no_retry()
    {
        foreach (var operation in new[] { "refresh", "exchange" })
        {
            var time = new ManualTimeProvider();
            var rig = AdapterRig.Create((_, _, _) => Task.FromResult(ScriptedBodyContent.Ok((_, ct) => StallBodyUntilWindow(time, ct))), time);
            Func<Task> act = operation == "refresh"
                ? () => rig.Connector.RefreshAsync(new MarketplaceRefreshRequest(Guid.NewGuid(), MercadoLivreConnector.Code, Secret), CancellationToken.None)
                : () => rig.Connector.CompleteOnceAsync(new MarketplaceAuthorizationCompletionRequest(Guid.NewGuid(), "code"), CancellationToken.None);
            var thrown = await Record.ExceptionAsync(act);
            thrown.Should().BeAssignableTo<OperationCanceledException>(operation + ": the existing post-invocation ambiguous path (SENT_OR_UNKNOWN, never replayed) — not a typed retryable failure");
            rig.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Post);
            time.CreatedDueTimes.Should().Equal(TimeSpan.FromSeconds(20));
            time.Elapsed.Should().Be(TimeSpan.FromSeconds(20)); rig.SimDelay!.Delays.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task BUD_9c_real_time_a_token_POST_with_a_body_that_never_completes_ends_within_20_25_seconds_after_a_single_request()
    {
        var rig = AdapterRig.Create((_, _, _) => Task.FromResult(ScriptedBodyContent.Ok(async (buffer, ct) => { await Task.Delay(Timeout.Infinite, ct); return 0; })));
        var wallStart = DateTimeOffset.UtcNow; var stopwatch = Stopwatch.StartNew();
        var thrown = await Record.ExceptionAsync(() => rig.Connector.RefreshAsync(new MarketplaceRefreshRequest(Guid.NewGuid(), MercadoLivreConnector.Code, Secret), CancellationToken.None));
        stopwatch.Stop(); var wallEnd = DateTimeOffset.UtcNow;
        Evidence.Record("bud9c-real-time-token-post-20s", new { requests = rig.Requests.Count, elapsedSeconds = stopwatch.Elapsed.TotalSeconds, monotonicSeconds = stopwatch.Elapsed.TotalSeconds, wallSeconds = (wallEnd - wallStart).TotalSeconds, wallStartUtc = wallStart, wallEndUtc = wallEnd, exception = thrown?.GetType().Name, contractWindowSeconds = 20, toleranceSeconds = 0.25 });
        thrown.Should().BeAssignableTo<OperationCanceledException>(); rig.Requests.Should().HaveCount(1);
        stopwatch.Elapsed.TotalSeconds.Should().BeLessThanOrEqualTo(20.25).And.BeGreaterThan(19.5);
    }

    // ============================================================ the safe reader's other callers keep the same budget (bulk and users/me)

    [Fact]
    public async Task The_budget_is_per_operation_for_every_read_kind_bulk_scan_and_users_me_alike()
    {
        foreach (var kind in new[] { "bulk", "me" })
        {
            var time = new ManualTimeProvider();
            var rig = AdapterRig.Create((_, _, ct) => StallUntilWindowAsync(time, ct), time);
            Func<Task> act = kind == "bulk"
                ? () => rig.Connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", Secret, ["MLB1"]), CancellationToken.None)
                : () => rig.Connector.InspectAsync(new MarketplaceAccountInspectionRequest(Guid.NewGuid(), MercadoLivreConnector.Code, Secret), CancellationToken.None);
            await FluentActions.Awaiting(act).Should().ThrowAsync<MarketplaceProviderCallException>();
            rig.Requests.Should().HaveCount(3, kind); time.Elapsed.Should().Be(TimeSpan.FromSeconds(30), kind);
        }
    }
}

/// <summary>C063-05: raw measurements are persisted as JSON next to the TRX when <c>VERCE_M063_EVIDENCE_DIR</c> is set (never required for the test to pass).</summary>
internal static class Evidence
{
    public static void Record(string name, object payload)
    {
        var dir = Environment.GetEnvironmentVariable("VERCE_M063_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".json"), JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }
}
