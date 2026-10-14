using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Verce.Modules.Commerce;

namespace Verce.Infrastructure.Marketplaces.MercadoLivre;

/// <summary>Injectable so tests never sleep (ADR-0025 §F.4/§F.7 "tests never sleep").</summary>
public interface IMarketplaceDelay { Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken); }
public sealed class SystemMarketplaceDelay : IMarketplaceDelay
{
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) => duration > TimeSpan.Zero ? Task.Delay(duration, cancellationToken) : Task.CompletedTask;
}

/// <summary>0..100ms injected jitter (§F.4). Deterministic fakes let tests assert exact bounds.</summary>
public interface IMarketplaceJitter { TimeSpan Next(); }
public sealed class SystemMarketplaceJitter : IMarketplaceJitter
{
    public TimeSpan Next() => TimeSpan.FromMilliseconds(Random.Shared.Next(0, 101));
}

/// <summary>§F.4: Retry-After is a lower bound; an invalid header gives no invented value. Accepts
/// a nonnegative integer second count or a valid future HTTP-date.
///
/// A-03: neither ADR-0025 §F.4 nor the Mercado Livre handoff states an UPPER bound. Without one, a
/// malformed or adversarial header (an absurd second count, or an HTTP-date decades out) would
/// parse successfully and install a <c>RuntimeRetryAfterUntil</c> that de-facto permanently locks
/// the account out — a provider's `Retry-After` is only ever a hint, never a mandate strong enough
/// to justify that. <see cref="MaxDuration"/> is a VERCE-chosen safety ceiling, disclosed here as an
/// explicit assumption (never a claim about what Mercado Livre would actually send): a header
/// requesting a longer wait is clamped to it, not rejected outright, because the underlying signal
/// ("the provider wants us to slow down") is still real and worth honoring up to a bounded cap. A
/// header that parses to a NON-future instant (at or before <paramref name="now"/> — including a
/// literal <c>0</c>) carries no retry guard at all and returns <c>null</c>, matching "a valid FUTURE
/// HTTP-date" above.</summary>
public static class RetryAfterParser
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);

    public static DateTimeOffset? Parse(string? rawValue, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return null;
        var trimmed = rawValue.Trim();
        DateTimeOffset? candidate = null;
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
            candidate = now.AddSeconds(seconds);
        // RFC 7231 §7.1.3 defines Retry-After's HTTP-date branch as IMF-fixdate — exactly the "R"
        // (RFC1123) round-trip format. A loose, culture-sensitive DateTimeOffset.TryParse (the
        // previous implementation) accepts all sorts of ambiguous non-conformant strings (e.g. a
        // bare "3.5" silently parsing as a date) that are not a real HTTP-date at all — never a
        // provider's actual header shape, and never worth the ambiguity. TryParseExact against the
        // one conformant pattern is both more correct and removes that whole class of surprises.
        else if (DateTimeOffset.TryParseExact(trimmed, "R", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            candidate = date;
        if (candidate is not { } until || until <= now) return null;
        var ceiling = now + MaxDuration;
        return until > ceiling ? ceiling : until;
    }
}

/// <summary>
/// ADR-0025 §F.2 (S8C2-G01, Astra G01_CLOSED): the ENTIRE send-certainty boundary lives in one
/// place. <see cref="SendAsync"/> never throws for a network-shaped failure — it always returns a
/// <see cref="MarketplaceSendOutcome"/> whose <see cref="MarketplaceSendOutcome.SendInvoked"/> flag
/// is the ONLY fact any caller may use to decide NOT_SENT vs SENT_OR_UNKNOWN. The flag is set
/// exactly once, immediately before (and never after) the call to <c>HttpClient.SendAsync</c>, so
/// every exception after that point — of ANY type, DNS/TCP/TLS/reset/timeout/cancellation included
/// — leaves it <c>true</c>. Only a failure that happens before this line (request/body
/// materialization, a local guard, pre-send cancellation) can produce <c>false</c>. Callers must
/// never re-derive certainty from the exception's type or message.
/// </summary>
public sealed class MarketplaceHttpSender
{
    private readonly ILogger<MarketplaceHttpSender> _logger;

    /// <summary>ADR-0029 §4: every deadline of the safe-read policy is measured on THIS monotonic source (default <see cref="System.TimeProvider.System"/>), never on wall-clock
    /// time. Injectable only so tests can prove the budget with simulated time.</summary>
    public TimeProvider TimeProvider { get; }

    public MarketplaceHttpSender(ILogger<MarketplaceHttpSender> logger, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Sends within a single window of <paramref name="timeout"/> (the window covers only send + headers; a caller that must also bound the body read owns the
    /// window itself and calls <see cref="SendWithinAsync"/>).</summary>
    public async Task<MarketplaceSendOutcome> SendAsync(HttpClient client, Func<HttpRequestMessage> requestFactory, TimeSpan timeout, CancellationToken callerToken)
    {
        using var windowCts = new CancellationTokenSource(timeout, TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, windowCts.Token);
        return await SendWithinAsync(client, requestFactory, linked.Token, callerToken);
    }

    /// <summary>The send-certainty boundary (ADR-0025 §F.2) inside a window the CALLER owns: <paramref name="windowToken"/> is the caller token linked with the attempt
    /// deadline, so the same deadline can also govern the bounded body read that follows (ADR-0029 §4). <paramref name="callerToken"/> alone decides the
    /// pre-send-cancellation (NOT_SENT) case.</summary>
    public async Task<MarketplaceSendOutcome> SendWithinAsync(HttpClient client, Func<HttpRequestMessage> requestFactory, CancellationToken windowToken, CancellationToken callerToken)
    {
        HttpRequestMessage request;
        try { request = requestFactory(); }
        catch (Exception ex) { return new MarketplaceSendOutcome(false, null, ex, TimeSpan.Zero); }

        if (callerToken.IsCancellationRequested)
            return new MarketplaceSendOutcome(false, null, new OperationCanceledException("PRE_SEND_CANCELLED", null, callerToken), TimeSpan.Zero);

        var stopwatch = Stopwatch.StartNew();
        var sendInvoked = false;
        try
        {
            sendInvoked = true; // THE single flag, set immediately before SendAsync — nothing else decides certainty.
            // A-08: ResponseHeadersRead — NEVER ResponseContentRead, which buffers the entire body
            // before this call even returns and defeats any bound applied afterward. The body is
            // read through the caller's own hard-bounded streaming reader instead.
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, windowToken);
            stopwatch.Stop();
            return new MarketplaceSendOutcome(true, response, null, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new MarketplaceSendOutcome(sendInvoked, null, ex, stopwatch.Elapsed);
        }
        // The request is intentionally not disposed here: HttpClient does not require the caller
        // to dispose it, and doing so would race a diagnostic/test observer that still needs to
        // read the outbound request (e.g. its form body) after this call returns.
    }

    /// <summary>The one frozen structured log line (Stream A7). Never logs a body, header, token,
    /// secret, code, state or query string.</summary>
    public void LogCall(string provider, string operation, int? httpStatus, string classification, string certainty, int attempt, double durationMs, Guid? accountId, string correlationId, (int RequestedIdCount, int EnvelopeCount, int UsableEnvelopeCount, string Histogram)? bulkEvidence)
    {
        if (bulkEvidence is { } bulk)
            _logger.LogInformation(
                "MarketplaceProviderCall provider={Provider} operation={Operation} status={HttpStatus} classification={Classification} certainty={Certainty} attempt={Attempt} durationMs={DurationMs} account={AccountId} correlation={CorrelationId} requestedIdCount={RequestedIdCount} envelopeCount={EnvelopeCount} usableEnvelopeCount={UsableEnvelopeCount} histogram={Histogram}",
                provider, operation, httpStatus?.ToString(CultureInfo.InvariantCulture) ?? "none", classification, certainty, attempt, durationMs, accountId, correlationId, bulk.RequestedIdCount, bulk.EnvelopeCount, bulk.UsableEnvelopeCount, bulk.Histogram);
        else
            _logger.LogInformation(
                "MarketplaceProviderCall provider={Provider} operation={Operation} status={HttpStatus} classification={Classification} certainty={Certainty} attempt={Attempt} durationMs={DurationMs} account={AccountId} correlation={CorrelationId}",
                provider, operation, httpStatus?.ToString(CultureInfo.InvariantCulture) ?? "none", classification, certainty, attempt, durationMs, accountId, correlationId);
    }
}

public sealed record MarketplaceSendOutcome(bool SendInvoked, HttpResponseMessage? Response, Exception? Exception, TimeSpan Duration);

/// <summary>
/// Per-logical-call state supplied only by <c>ITEMS_BULK</c>.  The safe reader owns retries while
/// the connector owns response-body parsing, so this carries the stable correlation and the final
/// attempt number across that boundary without ever carrying a request URL, id, or body.
/// </summary>
public sealed class MarketplaceBulkTelemetry
{
    public MarketplaceBulkTelemetry(int requestedIdCount, string correlationId)
    {
        RequestedIdCount = requestedIdCount;
        CorrelationId = correlationId;
    }

    public int RequestedIdCount { get; }
    public string CorrelationId { get; }
    public int CurrentAttempt { get; set; }
}

/// <summary>One completed safe read: the response (status/headers; its content is already consumed when a body reader was supplied) and the fully read, bounded body of a
/// 2xx answer (null for any other status or when no reader was supplied).</summary>
public sealed record MarketplaceSafeReadResult(HttpResponseMessage Response, string? Body);

/// <summary>The one bounded (1 MiB) streaming body reader shared by every adapter read. A trustworthy oversized Content-Length fails before any byte is read; otherwise the
/// stream is read through a hard bound that stops the instant it is crossed (ADR-0025 §F.6, A-08).</summary>
internal static class MarketplaceBoundedBodyReader
{
    public const long MaxResponseBodyBytes = 1024 * 1024;

    public static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } declaredLength && declaredLength > MaxResponseBodyBytes)
            throw new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.NON_RETRYABLE, "RESPONSE_TOO_LARGE", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED));

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxResponseBodyBytes)
                throw new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.NON_RETRYABLE, "RESPONSE_TOO_LARGE", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED));
            buffer.Write(chunk, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>
/// §F.4 safe-read policy, as completed by ADR-0029 §4: GET only, at most 3 attempts inside ONE 30-second budget per operation (one invocation = one logical read),
/// measured on the sender's monotonic <see cref="TimeProvider"/> from before the first attempt. Each attempt runs in a window of min(10 s, remaining) that covers
/// send, headers AND the bounded body read; backoff and Retry-After waits consume the same budget (wait d = max(250 ms then 500 ms + injected jitter 0..100 ms, Retry-After floor): ADR-0030 §4). No attempt starts
/// with nothing left, and a wait that is not strictly shorter than the remaining budget is never taken: the last attempt's classification is returned instead.
/// <c>Retry-After</c> (seconds or future HTTP-date, invalid ignored) is a lower bound for the wait, never a base for jitter. Retries <c>RETRYABLE</c> and <c>RATE_LIMITED</c>. POSTs never use
/// this executor.
/// </summary>
public sealed class MarketplaceSafeReadExecutor
{
    private static readonly TimeSpan[] BaseDelays = [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)];
    internal const int MaxAttempts = 3;
    /// <summary>The per-operation budget (ADR-0025 §F.4 "≤ 3 attempts within 30 s").</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    /// <summary>The per-attempt deadline for reads (ADR-0025 §F.4 "reads 10 s"); the effective window is min(this, remaining budget).</summary>
    public static readonly TimeSpan AttemptWindow = TimeSpan.FromSeconds(10);

    private readonly MarketplaceHttpSender _sender;
    private readonly IMarketplaceDelay _delay;
    private readonly IMarketplaceJitter _jitter;
    private readonly Verce.SharedKernel.Time.IClock _clock;

    public MarketplaceSafeReadExecutor(MarketplaceHttpSender sender, IMarketplaceDelay delay, IMarketplaceJitter jitter, Verce.SharedKernel.Time.IClock clock)
    { _sender = sender; _delay = delay; _jitter = jitter; _clock = clock; }

    public async Task<HttpResponseMessage> ExecuteAsync(HttpClient client, Func<HttpRequestMessage> requestFactory, TimeSpan perAttemptTimeout, string safeUnreachableCode, CancellationToken cancellationToken) =>
        (await ExecuteReadAsync(client, requestFactory, perAttemptTimeout, safeUnreachableCode, "MERCADO_LIVRE", "UNSPECIFIED", null, null, null, cancellationToken)).Response;

    public async Task<HttpResponseMessage> ExecuteAsync(HttpClient client, Func<HttpRequestMessage> requestFactory, TimeSpan perAttemptTimeout, string safeUnreachableCode, string provider, string operation, Guid? accountId, CancellationToken cancellationToken) =>
        (await ExecuteReadAsync(client, requestFactory, perAttemptTimeout, safeUnreachableCode, provider, operation, accountId, null, null, cancellationToken)).Response;

    /// <summary>A-08: emits the one mandatory safe structured provider-call log event PER ATTEMPT (never once per logical operation) — success, retry-and-continue, and final
    /// failure alike — including an attempt that fails while reading the body. The event never carries Authorization, a token, a body, or a raw query string.</summary>
    public async Task<HttpResponseMessage> ExecuteAsync(HttpClient client, Func<HttpRequestMessage> requestFactory, TimeSpan perAttemptTimeout, string safeUnreachableCode, string provider, string operation, Guid? accountId, MarketplaceBulkTelemetry? bulkTelemetry, CancellationToken cancellationToken) =>
        (await ExecuteReadAsync(client, requestFactory, perAttemptTimeout, safeUnreachableCode, provider, operation, accountId, bulkTelemetry, null, cancellationToken)).Response;

    /// <summary>The body-aware form: when <paramref name="readBody"/> is supplied, a 2xx answer's body is read INSIDE the attempt window and before the executor returns; a
    /// body that cannot be completed in the window (stall, truncation, reset) is an incomplete response — RETRYABLE, SENT_OR_UNKNOWN, retried inside the same budget.
    /// An oversized body is NON_RETRYABLE and never retried.</summary>
    public async Task<MarketplaceSafeReadResult> ExecuteReadAsync(HttpClient client, Func<HttpRequestMessage> requestFactory, TimeSpan perAttemptTimeout, string safeUnreachableCode, string provider, string operation, Guid? accountId, MarketplaceBulkTelemetry? bulkTelemetry,
        Func<HttpResponseMessage, CancellationToken, Task<string>>? readBody, CancellationToken cancellationToken)
    {
        var correlationId = bulkTelemetry?.CorrelationId ?? Guid.NewGuid().ToString("N");
        var time = _sender.TimeProvider;
        var started = time.GetTimestamp();
        TimeSpan Remaining() => Budget - time.GetElapsedTime(started);
        MarketplaceProviderCallException? lastFailure = null;   // the classification of the last attempt: what is returned when the budget is gone (ADR-0029 §4)

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var remaining = Remaining();
            var window = remaining < perAttemptTimeout ? remaining : perAttemptTimeout;
            // Nothing left (a real wait can only overshoot): no attempt begins, and the caller gets the LAST attempt's own classification — never a generic one.
            if (window <= TimeSpan.Zero) throw lastFailure ?? new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.RETRYABLE, safeUnreachableCode, MarketplaceSendCertainty.SENT_OR_UNKNOWN));
            if (bulkTelemetry is not null) bulkTelemetry.CurrentAttempt = attempt;

            using var windowCts = new CancellationTokenSource(window, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, windowCts.Token);
            var outcome = await _sender.SendWithinAsync(client, requestFactory, linked.Token, cancellationToken);
            if (!outcome.SendInvoked)
            {
                LogAttempt(null, "NONE", "NOT_SENT", attempt, outcome.Duration.TotalMilliseconds);
                throw new MarketplaceRequestNotSentException("PROVIDER_REQUEST_NOT_SENT");
            }

            if (outcome.Response is null)
            {
                LogAttempt(null, "RETRYABLE", "SENT_OR_UNKNOWN", attempt, outcome.Duration.TotalMilliseconds);
                await RetryOrThrowAsync(new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.RETRYABLE, safeUnreachableCode, MarketplaceSendCertainty.SENT_OR_UNKNOWN)), attempt, null);
                continue;
            }

            var response = outcome.Response;
            var status = (int)response.StatusCode;
            if (status == 429)
            {
                var retryAfter = RetryAfterParser.Parse(response.Headers.TryGetValues("Retry-After", out var v) ? v.FirstOrDefault() : null, _clock.UtcNow);
                LogAttempt(status, "RATE_LIMITED", "SENT_AND_RESPONSE_RECEIVED", attempt, outcome.Duration.TotalMilliseconds);
                response.Dispose();
                await RetryOrThrowAsync(new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.RATE_LIMITED, "PROVIDER_RATE_LIMITED", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED, RetryAfterUntil: retryAfter)), attempt, retryAfter);
                continue;
            }
            if (status == 408 || status is >= 500 and <= 599)
            {
                LogAttempt(status, "RETRYABLE", "SENT_AND_RESPONSE_RECEIVED", attempt, outcome.Duration.TotalMilliseconds);
                response.Dispose();
                await RetryOrThrowAsync(new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.RETRYABLE, "PROVIDER_UNAVAILABLE", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED)), attempt, null);
                continue;
            }

            string? body = null;
            if (readBody is not null && response.IsSuccessStatusCode)
            {
                try { body = await readBody(response, linked.Token); }
                catch (MarketplaceProviderCallException tooLarge)
                {
                    // e.g. RESPONSE_TOO_LARGE: headers were received, the body was never safely read — final, never retried.
                    LogAttempt(status, tooLarge.Failure.SafeCode, "SENT_AND_RESPONSE_RECEIVED", attempt, outcome.Duration.TotalMilliseconds);
                    response.Dispose();
                    throw;
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException or ObjectDisposedException)
                {
                    // Window expiry, stall, truncation or reset after the headers: an incomplete response (§F.6) — RETRYABLE, SENT_OR_UNKNOWN.
                    LogAttempt(status, "RETRYABLE", "SENT_OR_UNKNOWN", attempt, outcome.Duration.TotalMilliseconds);
                    response.Dispose();
                    await RetryOrThrowAsync(new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.RETRYABLE, safeUnreachableCode, MarketplaceSendCertainty.SENT_OR_UNKNOWN)), attempt, null);
                    continue;
                }
            }

            // ITEMS_BULK needs the parsed envelope counters and its final provider classification.
            // Its connector emits the one final event for this attempt after body processing.
            if (bulkTelemetry is null)
                LogAttempt(status, "NONE", "SENT_AND_RESPONSE_RECEIVED", attempt, outcome.Duration.TotalMilliseconds);
            return new MarketplaceSafeReadResult(response, body); // 2xx or a non-retryable 4xx: the caller classifies it.
        }
        throw new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.RETRYABLE, safeUnreachableCode, MarketplaceSendCertainty.SENT_OR_UNKNOWN));

        // Either the retry wait (strictly shorter than the remaining budget; it consumes it) or the attempt's own failure, thrown.
        async Task RetryOrThrowAsync(MarketplaceProviderCallException failure, int attemptNumber, DateTimeOffset? retryAfter)
        {
            lastFailure = failure;
            // ADR-0025 §F.6 / R2: a caller cancellation is honored before the final attempt (no further attempt, no dependence on the delay); during the final
            // attempt the frozen "any exception after SendAsync" row applies.
            if (cancellationToken.IsCancellationRequested && attemptNumber < MaxAttempts) throw new OperationCanceledException(cancellationToken);
            if (attemptNumber == MaxAttempts) throw failure;
            var baseDelay = BaseDelays[Math.Min(attemptNumber - 1, BaseDelays.Length - 1)];
            var floor = retryAfter is { } until ? until - _clock.UtcNow : TimeSpan.Zero;
            // ADR-0030 §4: d = max(base + jitter, Retry-After floor). The floor is a lower bound, never a base to add jitter to: with a dominating floor the wait is exactly the floor.
            var policy = baseDelay + _jitter.Next();
            var wait = floor > policy ? floor : policy;
            if (wait >= Remaining()) throw failure;                 // never sleep past (or up to) the budget: return the last classification
            await _delay.DelayAsync(wait, cancellationToken);
        }

        void LogAttempt(int? status, string classification, string certainty, int currentAttempt, double durationMs)
        {
            (int RequestedIdCount, int EnvelopeCount, int UsableEnvelopeCount, string Histogram)? evidence = null;
            if (bulkTelemetry is not null)
            {
                var histogram = string.Join(",", Enum.GetValues<MarketplaceListingReadOutcome>().Select(category => $"{category}=0"));
                evidence = (bulkTelemetry.RequestedIdCount, 0, 0, histogram);
            }
            _sender.LogCall(provider, operation, status, classification, certainty, currentAttempt, durationMs, accountId, correlationId, evidence);
        }
    }
}
