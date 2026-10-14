using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Verce.Modules.Commerce;
using Verce.SharedKernel.Time;

namespace Verce.Infrastructure.Marketplaces.MercadoLivre;

/// <summary>Opaque, in-memory-only scan cursor (ADR-0025 §B.2). Never serialized/persisted/logged.</summary>
public sealed class MercadoLivreScrollCursor : MarketplaceListingCursor
{
    public string ScrollId { get; }
    public MercadoLivreScrollCursor(string scrollId) => ScrollId = scrollId;
}

/// <summary>
/// ADR-0025 real Mercado Livre (MLB) adapter for the frozen S8C.2 LISTINGS_READ scope only. All
/// credential flow (session, operation arbiter, protected store, single-flight refresh, provider
/// fence, K1/K2 recovery) stays exactly the S8C.1 machinery in <see cref="MarketplaceAuthorizationWorkflow"/>
/// — this class only ever sees a decrypted secret handed to it for the duration of one call. It
/// implements both Commerce ports as ONE instance (§A.1) and never leaks an ML type/URL/DTO outside
/// this namespace.
/// </summary>
public sealed class MercadoLivreConnector : IMarketplaceAuthorizationConnector, IMarketplaceListingReader
{
    public const string Code = "MERCADO_LIVRE";
    private const string SiteId = "MLB";
    private const string FixedCallbackPath = "/api/commerce/marketplace-authorizations/MERCADO_LIVRE/callback";
    private const string TokenUrl = "https://api.mercadolibre.com/oauth/token";
    private static readonly Regex ListingIdPattern = new("^MLB[0-9]{1,20}$", RegexOptions.Compiled);
    private static readonly Regex UserIdPattern = new("^[1-9][0-9]{0,19}$", RegexOptions.Compiled);
    private static readonly string[] BulkAttributes =
    [
        "body.id", "body.seller_id", "body.site_id", "body.title", "body.price", "body.currency_id",
        "body.status", "body.sub_status", "body.permalink", "body.last_updated", "body.attributes", "body.variations",
    ];

    public string ProviderCode => Code;

    /// <summary>ADR-0025 §F.4 / ADR-0029 §4: the token POST's single window (send + headers + body); the POST is never retried.</summary>
    public static readonly TimeSpan TokenWindow = TimeSpan.FromSeconds(20);

    private readonly IHttpClientFactory _httpFactory;
    private readonly MarketplaceHttpSender _sender;
    private readonly MarketplaceSafeReadExecutor _reader;
    private readonly IOptions<MercadoLivreOptions> _options;
    private readonly IClock _clock;

    public MercadoLivreConnector(IHttpClientFactory httpFactory, MarketplaceHttpSender sender, MarketplaceSafeReadExecutor reader, IOptions<MercadoLivreOptions> options, IClock clock)
    {
        _httpFactory = httpFactory;
        _sender = sender;
        _reader = reader;
        _options = options;
        _clock = clock;
    }

    // ================= §A.3 Begin =================

    public Task<MarketplaceAuthorizationStart> BeginAsync(MarketplaceAuthorizationBeginRequest request, CancellationToken cancellationToken)
    {
        if (request.RedirectUri != FixedCallbackPath) throw new MarketplaceRequestNotSentException("REDIRECT_PATH_INVALID");
        var options = _options.Value;
        var uri = "https://auth.mercadolivre.com.br/authorization?response_type=code" +
                  "&client_id=" + Uri.EscapeDataString(options.ClientId!) +
                  "&redirect_uri=" + Uri.EscapeDataString(options.RedirectUri!) +
                  "&state=" + Uri.EscapeDataString(request.State);
        return Task.FromResult(new MarketplaceAuthorizationStart(uri));
    }

    // ================= §A.5 CompleteOnce =================

    public async Task<MarketplaceAuthorizationCompletion> CompleteOnceAsync(MarketplaceAuthorizationCompletionRequest request, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var client = _httpFactory.CreateClient("MercadoLivre.OAuth");
        var sendInvokedAt = _clock.UtcNow;
        // ADR-0029 §4: the token POST's single (never retried) 20 s window covers send, headers AND the body read.
        using var tokenWindow = new CancellationTokenSource(TokenWindow, _sender.TimeProvider);
        using var tokenLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, tokenWindow.Token);
        var outcome = await _sender.SendWithinAsync(client, () =>
        {
            sendInvokedAt = _clock.UtcNow; // read immediately before SendAsync, per §A.5
            return TokenFormRequest(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = options.ClientId!,
                ["client_secret"] = options.ResolvedClientSecret!,
                ["code"] = request.AuthorizationCode,
                ["redirect_uri"] = options.RedirectUri!,
            });
        }, tokenLinked.Token, cancellationToken);

        // A-08-LOG: exactly one structured event per HTTP attempt, emitted ONLY once this attempt's
        // FINAL classification is known — never a premature placeholder (the previous bug: a log
        // line already written here, before the body/identity checks below ever ran, permanently
        // recording a meaningless classification="NONE"/"UNKNOWN" no matter what this attempt's
        // TRUE outcome later turned out to be). Every exit path below — success or any thrown
        // failure — logs exactly once, with the real classification, via the catch clauses.
        string accessToken, refreshToken, userId; long expiresIn;
        try
        {
            if (!outcome.SendInvoked) throw new MarketplaceRequestNotSentException("OAUTH_EXCHANGE_NOT_SENT");
            if (outcome.Response is null)
                throw new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.UNKNOWN, "PROVIDER_UNREACHABLE", MarketplaceSendCertainty.SENT_OR_UNKNOWN));

            using var response = outcome.Response;
            var body = await ReadBoundedBodyAsync(response, tokenLinked.Token);
            if (!response.IsSuccessStatusCode) throw MapTokenErrorResponse(response, body);
            if (!TryParseTokenResponse(body, out accessToken, out refreshToken, out userId, out expiresIn))
                throw new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.NON_RETRYABLE, "PROVIDER_RESPONSE_INVALID", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED));

            LogOauthAttempt("OAUTH_EXCHANGE", outcome, "SUCCESS");
        }
        catch (MarketplaceRequestNotSentException) { LogOauthAttempt("OAUTH_EXCHANGE", outcome, "NOT_SENT"); throw; }
        catch (MarketplaceProviderCallException ex) { LogOauthAttempt("OAUTH_EXCHANGE", outcome, ex.Failure.SafeCode); throw; }

        var accessExpiresAt = sendInvokedAt.AddSeconds(expiresIn);

        var (meId, siteId) = await GetUsersMeAsync(accessToken, null, cancellationToken);
        if (meId != userId || siteId != SiteId)
            throw new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.NON_RETRYABLE, "PROVIDER_RESPONSE_INVALID", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED));

        var listingsReadGrant = await ProbeListingsReadGrantAsync(accessToken, userId, null, cancellationToken);
        var grants = new Dictionary<string, AccountCapabilityState>
        {
            ["LISTINGS_READ"] = listingsReadGrant,
            ["LISTINGS_WRITE"] = AccountCapabilityState.UNKNOWN,
            ["ORDERS_READ"] = AccountCapabilityState.UNKNOWN,
            ["FEES_QUOTE"] = AccountCapabilityState.UNKNOWN,
            ["ANALYTICS_READ"] = AccountCapabilityState.UNKNOWN,
            ["ADS_READ"] = AccountCapabilityState.UNKNOWN,
            ["SHIPPING_READ"] = AccountCapabilityState.UNKNOWN,
            ["INVENTORY_SYNC"] = AccountCapabilityState.UNKNOWN,
        };

        return new MarketplaceAuthorizationCompletion(userId, grants, BuildSecretJson(accessToken, refreshToken, userId), CredentialImpact.MAY_SUPERSEDE_EXISTING, accessExpiresAt);
    }

    // ================= §A.7 Refresh =================

    public async Task<MarketplaceRefreshCompletion> RefreshAsync(MarketplaceRefreshRequest request, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var stored = ReadStoredSecret(request.SecretMaterial);
        if (!stored.UsableForRefresh)
            throw new MarketplaceRequestNotSentException("REFRESH_TOKEN_UNAVAILABLE");
        var refreshToken = stored.RefreshToken!; var userId = stored.UserId;
        // A-10 PRE-VALIDATION row: a genuinely DIFFERENT pre-send validation condition from
        // REFRESH_TOKEN_UNAVAILABLE above — that one is a decrypted-secret-shape failure (no usable
        // refresh token at all, which forces REAUTHORIZATION_REQUIRED); this one is "the stored
        // identity itself is not even a well-formed Mercado Livre numeric user id", a request/value
        // validation belonging to the general NOT_SENT row (preserves CONNECTED, only moves runtime
        // to UNAVAILABLE — see FailRefreshNotSentAsync). Reuses the SAME UserIdPattern already
        // enforced on token RESPONSES, applied here to the stored identity before ANY request is
        // materialized or sent.
        if (!UserIdPattern.IsMatch(userId))
            throw new MarketplaceRequestNotSentException("STORED_IDENTITY_INVALID");

        var client = _httpFactory.CreateClient("MercadoLivre.OAuth");
        var sendInvokedAt = _clock.UtcNow;
        using var tokenWindow = new CancellationTokenSource(TokenWindow, _sender.TimeProvider);   // ADR-0029 §4: one 20 s window, body included, never retried
        using var tokenLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, tokenWindow.Token);
        var outcome = await _sender.SendWithinAsync(client, () =>
        {
            sendInvokedAt = _clock.UtcNow;
            return TokenFormRequest(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = options.ClientId!,
                ["client_secret"] = options.ResolvedClientSecret!,
                ["refresh_token"] = refreshToken,
            });
        }, tokenLinked.Token, cancellationToken);

        // A-08-LOG: see CompleteOnceAsync's own note — exactly one event per HTTP attempt, only
        // once the FINAL classification for THIS attempt is known, never a premature placeholder.
        string newAccessToken, newRefreshToken; long expiresIn;
        try
        {
            if (!outcome.SendInvoked) throw new MarketplaceRequestNotSentException("REFRESH_NOT_SENT");
            if (outcome.Response is null)
                throw new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.UNKNOWN, "PROVIDER_UNREACHABLE", MarketplaceSendCertainty.SENT_OR_UNKNOWN));

            using var response = outcome.Response;
            var body = await ReadBoundedBodyAsync(response, tokenLinked.Token);
            if (!response.IsSuccessStatusCode) throw MapTokenErrorResponse(response, body);
            if (!TryParseTokenResponse(body, out newAccessToken, out newRefreshToken, out var newUserId, out expiresIn) || newUserId != userId)
                throw new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.NON_RETRYABLE, "PROVIDER_RESPONSE_INVALID", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED));

            LogOauthAttempt("OAUTH_REFRESH", outcome, "SUCCESS", request.AccountId);
        }
        catch (MarketplaceRequestNotSentException) { LogOauthAttempt("OAUTH_REFRESH", outcome, "NOT_SENT", request.AccountId); throw; }
        catch (MarketplaceProviderCallException ex) { LogOauthAttempt("OAUTH_REFRESH", outcome, ex.Failure.SafeCode, request.AccountId); throw; }

        var accessExpiresAt = sendInvokedAt.AddSeconds(expiresIn);
        return new MarketplaceRefreshCompletion(BuildSecretJson(newAccessToken, newRefreshToken, userId), accessExpiresAt);
    }

    /// <summary>A-08-LOG: the ONE place both OAuth attempts (exchange and refresh) emit their single
    /// mandatory structured log event — always called exactly once per real HTTP attempt, always
    /// with the FINAL classification for that attempt (never a value logged before it is known).
    /// Never carries Authorization/Bearer/tokens/a code/a secret/a raw body/a sensitive URL — only
    /// the same safe scalar fields every other provider-call log line carries.</summary>
    private void LogOauthAttempt(string operation, MarketplaceSendOutcome outcome, string classification, Guid? accountId = null)
    {
        var certainty = outcome.SendInvoked ? (outcome.Response is null ? "SENT_OR_UNKNOWN" : "SENT_AND_RESPONSE_RECEIVED") : "NOT_SENT";
        var httpStatus = outcome.Response is null ? (int?)null : (int)outcome.Response.StatusCode;
        _sender.LogCall("MERCADO_LIVRE", operation, httpStatus, classification, certainty, 1, outcome.Duration.TotalMilliseconds, accountId, Guid.NewGuid().ToString("N"), null);
    }

    // ================= §A.9 Inspect (probe) =================

    public async Task<MarketplaceAccountInspection> InspectAsync(MarketplaceAccountInspectionRequest request, CancellationToken cancellationToken)
    {
        var stored = RequireBearerUsable(request.SecretMaterial);   // ADR-0030 §1/§2: before ANY request; never a provider failure
        var accessToken = stored.AccessToken; var storedUserId = stored.UserId;

        var (meId, siteId) = await GetUsersMeAsync(accessToken, request.AccountId, cancellationToken);
        if (meId != storedUserId || siteId != SiteId)
            throw new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.USER_ACTION_REQUIRED, "PROBE_IDENTITY_MISMATCH", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED));

        var grant = await ProbeListingsReadGrantAsync(accessToken, storedUserId, request.AccountId, cancellationToken);
        return new MarketplaceAccountInspection(meId, new Dictionary<string, AccountCapabilityState> { ["LISTINGS_READ"] = grant });
    }

    // ================= §B.2 enumeration =================

    public async Task<MarketplaceListingPage> ReadListingPageAsync(MarketplaceListingPageRequest request, CancellationToken cancellationToken)
    {
        var accessToken = RequireBearerUsable(request.SecretMaterial).AccessToken;   // ADR-0030 §1/§2: before ANY request; never a provider failure

        var scrollId = (request.Cursor as MercadoLivreScrollCursor)?.ScrollId;
        var url = scrollId is null
            ? $"https://api.mercadolibre.com/users/{Uri.EscapeDataString(request.ExternalAccountId)}/items/search?search_type=scan&limit=100"
            : $"https://api.mercadolibre.com/users/{Uri.EscapeDataString(request.ExternalAccountId)}/items/search?search_type=scan&limit=100&scroll_id={Uri.EscapeDataString(scrollId)}";

        var client = _httpFactory.CreateClient("MercadoLivre.Api");
        var read = await _reader.ExecuteReadAsync(client, () => BearerGetRequest(url, accessToken), MarketplaceSafeReadExecutor.AttemptWindow, "PROVIDER_UNREACHABLE", "MERCADO_LIVRE", "ITEMS_SCAN", request.AccountId, null, MarketplaceBoundedBodyReader.ReadAsync, cancellationToken);
        var response = read.Response;
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // §B.2: "A continuation answered with a 4xx other than 401/403/429 throws
                // LISTING_ENUMERATION_CURSOR_INVALID." 401/403/429 fall through to the ordinary
                // read-error mapping (already excluded here since the safe-read executor itself
                // throws typed exceptions for 429/5xx before returning a response).
                if (scrollId is not null && response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                    throw new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.NON_RETRYABLE, "LISTING_ENUMERATION_CURSOR_INVALID", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED));
                throw MapReadErrorResponse(response.StatusCode);
            }

            var body = read.Body!;                       // read inside the attempt window (ADR-0029 §4)
            using var doc = ParseProviderJson(body);
            var root = doc.RootElement;
            // A-06: `results` missing, or present with the wrong JSON type, is a MALFORMED provider
            // response — never silently treated as a successfully-terminated (empty) scan. Only a
            // genuinely present, empty JSON array is a legitimate "nothing here" page.
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var resultsEl) || resultsEl.ValueKind != JsonValueKind.Array)
                throw new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.NON_RETRYABLE, "PROVIDER_RESPONSE_INVALID", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED));

            var ids = new List<string>();
            var rejected = 0;
            var rawResultCount = 0;
            foreach (var item in resultsEl.EnumerateArray())
            {
                rawResultCount++;
                // Provider text that is not valid UTF-16 (a lone surrogate escape) makes GetString throw: such an id is simply a rejected identifier.
                string? id; try { id = item.ValueKind == JsonValueKind.String ? item.GetString() : null; } catch (InvalidOperationException) { id = null; }
                if (id is not null && ListingIdPattern.IsMatch(id)) ids.Add(id);
                else rejected++;
            }
            // An unreadable scroll_id must NOT read as "no continuation": that would end the enumeration as if it were complete.
            string? nextScrollId;
            try { nextScrollId = root.TryGetProperty("scroll_id", out var scrollEl) && scrollEl.ValueKind == JsonValueKind.String ? scrollEl.GetString() : null; }
            catch (InvalidOperationException) { throw new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.NON_RETRYABLE, "PROVIDER_RESPONSE_INVALID", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED)); }
            // A-06: termination uses the PROVIDER's OWN envelope semantics — a genuinely empty raw
            // `results` array, or an absent/null continuation — never "every returned ID happened to
            // fail our own ID-shape validation." A non-empty raw page whose IDs were all rejected but
            // which still carries a scroll_id must continue enumeration, not silently stop.
            var endOfEnumeration = rawResultCount == 0 || string.IsNullOrEmpty(nextScrollId);
            return new MarketplaceListingPage(ids, endOfEnumeration ? null : new MercadoLivreScrollCursor(nextScrollId!), endOfEnumeration, rejected);
        }
    }

    // ================= §B.3/§C.2 detail + status mapping =================

    public async Task<IReadOnlyList<MarketplaceListingDetailResult>> ReadListingDetailsAsync(MarketplaceListingDetailRequest request, CancellationToken cancellationToken)
    {
        var accessToken = RequireBearerUsable(request.SecretMaterial).AccessToken;   // ADR-0030 §1/§2: before ANY request; never a provider failure

        var idsParam = string.Join(",", request.ExternalListingIds);
        var attrParam = string.Join(",", BulkAttributes);
        var url = $"https://api.mercadolibre.com/items/bulk?ids={Uri.EscapeDataString(idsParam)}&attributes={Uri.EscapeDataString(attrParam)}";
        var requestedIdCount = request.ExternalListingIds.Count;
        var telemetry = new MarketplaceBulkTelemetry(requestedIdCount, Guid.NewGuid().ToString("N"));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var client = _httpFactory.CreateClient("MercadoLivre.Api");
        // The body is read INSIDE the attempt window; an oversized body is logged by the executor as this attempt's one bulk-evidence event (zero counts).
        var read = await _reader.ExecuteReadAsync(client, () => BearerGetRequest(url, accessToken), MarketplaceSafeReadExecutor.AttemptWindow, "PROVIDER_UNREACHABLE", "MERCADO_LIVRE", "ITEMS_BULK", request.AccountId, telemetry, MarketplaceBoundedBodyReader.ReadAsync, cancellationToken);
        var response = read.Response;
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                LogBulkAttempt(request.AccountId, telemetry, 0, 0, [], (int)response.StatusCode, "PROVIDER_ERROR", stopwatch.Elapsed.TotalMilliseconds);
                throw MapReadErrorResponse(response.StatusCode);
            }

            var body = read.Body!;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (JsonException)
            {
                LogBulkAttempt(request.AccountId, telemetry, 0, 0, [], (int)response.StatusCode, "PROVIDER_RESPONSE_INVALID", stopwatch.Elapsed.TotalMilliseconds);
                throw new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.NON_RETRYABLE, "PROVIDER_RESPONSE_INVALID", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED));
            }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    // A malformed TOP-LEVEL payload (not even an array) genuinely yields zero
                    // enumerable envelope elements — 0 is the honest count here, never "unknown".
                    LogBulkAttempt(request.AccountId, telemetry, 0, 0, [], (int)response.StatusCode, "PROVIDER_RESPONSE_INVALID", stopwatch.Elapsed.TotalMilliseconds);
                    throw new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.NON_RETRYABLE, "PROVIDER_RESPONSE_INVALID", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED));
                }

                var envelopeCount = doc.RootElement.GetArrayLength();
                var byId = new Dictionary<string, MarketplaceListingDetailResult>(StringComparer.Ordinal);
                // Every element's own outcome — INCLUDING a structurally-invalid element's synthetic
                // FAILED result (id == null, so it can never be matched to a request and never enters
                // `byId`) — still belongs in the histogram: the histogram counts what the RESPONSE
                // actually contained, never only the subset this call could resolve an identity for.
                var allOutcomes = new List<MarketplaceListingReadOutcome>(envelopeCount);
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    var (id, result) = ParseBulkElementIsolated(element, request.ExternalAccountId);
                    allOutcomes.Add(result.Outcome);
                    if (id is not null) byId[id] = result;
                }
                // Exactly one bulk-evidence event, emitted here — after parsing enough of the
                // response to compute all four required counts, never before (no early misleading
                // event followed by a correction).
                LogBulkAttempt(request.AccountId, telemetry, envelopeCount, byId.Count, allOutcomes, (int)response.StatusCode, "SUCCESS", stopwatch.Elapsed.TotalMilliseconds);

                var results = new List<MarketplaceListingDetailResult>(request.ExternalListingIds.Count);
                foreach (var requestedId in request.ExternalListingIds)
                    results.Add(byId.TryGetValue(requestedId, out var r) ? r : new MarketplaceListingDetailResult(requestedId, MarketplaceListingReadOutcome.FAILED, null, null));
                return results;
            }
        }
    }

    /// <summary>A-08-LOG: the ONE place `/items/bulk` emits its mandatory bulk-evidence structured
    /// event — counts and a bounded histogram ONLY (never a listing id, seller item id, SKU, title,
    /// the raw envelope, or the raw provider body). <paramref name="outcomes"/> is enumerated over
    /// the REAL, already-frozen <see cref="MarketplaceListingReadOutcome"/> vocabulary (A-07) —
    /// never a newly-invented category — and the histogram always lists all 7 known categories
    /// (zero-count ones included) so its shape is bounded and stable regardless of the data.</summary>
    private void LogBulkAttempt(Guid? accountId, MarketplaceBulkTelemetry telemetry, int envelopeCount, int usableEnvelopeCount, IEnumerable<MarketplaceListingReadOutcome> outcomes, int? httpStatus, string classification, double durationMs)
    {
        var materialized = outcomes as ICollection<MarketplaceListingReadOutcome> ?? outcomes.ToList();
        var histogram = string.Join(",", Enum.GetValues<MarketplaceListingReadOutcome>().Select(category => $"{category}={materialized.Count(o => o == category)}"));
        _sender.LogCall("MERCADO_LIVRE", "ITEMS_BULK", httpStatus, classification, httpStatus is null ? "SENT_OR_UNKNOWN" : "SENT_AND_RESPONSE_RECEIVED", telemetry.CurrentAttempt, durationMs, accountId, telemetry.CorrelationId,
            (telemetry.RequestedIdCount, envelopeCount, usableEnvelopeCount, histogram));
    }

    /// <summary>ADR-0025 §B.3 / A-07: one hostile element must never abort its batch. Provider strings that are not valid UTF-16 (an unpaired
    /// surrogate escape) make <c>JsonElement.GetString</c> / <c>string.Normalize</c> throw; that is an "unparseable body" ⇒
    /// <see cref="MarketplaceListingReadOutcome.INVALID_RESPONSE"/> for THAT element only, attributed to its root id when that id is itself readable.</summary>
    private static (string? Id, MarketplaceListingDetailResult Result) ParseBulkElementIsolated(JsonElement element, string expectedSellerId)
    {
        try { return ParseBulkElement(element, expectedSellerId); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            string? id = null;
            try { id = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null; }
            catch (InvalidOperationException) { id = null; }
            return (id, new MarketplaceListingDetailResult(id ?? string.Empty, MarketplaceListingReadOutcome.INVALID_RESPONSE, null, null));
        }
    }

    private static (string? Id, MarketplaceListingDetailResult Result) ParseBulkElement(JsonElement element, string expectedSellerId)
    {
        // A-07: one structurally invalid element (JSON null, a nested array, a bare scalar/string —
        // anything that is not itself a JSON object) must never throw and abort the whole batch.
        // `JsonElement.TryGetProperty` throws InvalidOperationException when the element's own Kind
        // isn't Object, so this check MUST happen before any property access on `element` itself.
        // No identity is recoverable from a non-object element; its slot is left unmatched and the
        // caller's own "requested ID missing from the response ⇒ FAILED" fallback covers it —
        // valid peer elements in the same response are completely unaffected.
        if (element.ValueKind != JsonValueKind.Object) return (null, new MarketplaceListingDetailResult(string.Empty, MarketplaceListingReadOutcome.FAILED, null, null));

        // ML-ITEMS documents the per-element envelope as "status_code, root id, body"; "code" is
        // accepted defensively (an alternate shape seen in some integrations — the exact envelope
        // is one of the ADR-0025 official-evidence unknowns, proven only by live validation L-07).
        int? statusCode = element.TryGetProperty("status_code", out var scEl) && scEl.ValueKind == JsonValueKind.Number && scEl.TryGetInt32(out var sc) ? sc
            : element.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number && codeEl.TryGetInt32(out var c) ? c
            : null;
        var requestedId = element.TryGetProperty("id", out var reqIdEl) && reqIdEl.ValueKind == JsonValueKind.String ? reqIdEl.GetString() : null;
        // The root "id" is the documented correlation key; a nested body.id is used only as a
        // defensive fallback for identifying WHICH request this element answers (never to decide
        // FOUND/NOT_FOUND/etc, which still comes only from status_code/body below).
        requestedId ??= element.TryGetProperty("body", out var probeBody) && probeBody.ValueKind == JsonValueKind.Object
            && probeBody.TryGetProperty("id", out var probeId) && probeId.ValueKind == JsonValueKind.String ? probeId.GetString() : null;

        if (statusCode == 404) return (requestedId, new MarketplaceListingDetailResult(requestedId ?? "", MarketplaceListingReadOutcome.NOT_FOUND, null, "not_found"));
        if (statusCode == 403) return (requestedId, new MarketplaceListingDetailResult(requestedId ?? "", MarketplaceListingReadOutcome.ACCESS_DENIED, null, "forbidden"));
        if (statusCode != 200)
            return (requestedId, new MarketplaceListingDetailResult(requestedId ?? "", MarketplaceListingReadOutcome.FAILED, null, null));

        // ADR-0025 §B.3: an element that CLAIMS success (status_code 200) but whose body is absent,
        // null or not an object is an "unparseable body" ⇒ INVALID_RESPONSE (PERMANENT_ERROR /
        // LISTING_RESPONSE_INVALID downstream, never retryable) — NOT the generic FAILED that §B.3
        // reserves for "any other element code or a requested ID missing from the response", which
        // downstream is a retryable TRANSIENT_ERROR. A malformed success payload will not heal on retry.
        if (!element.TryGetProperty("body", out var bodyEl) || bodyEl.ValueKind != JsonValueKind.Object)
            return (requestedId, new MarketplaceListingDetailResult(requestedId ?? "", MarketplaceListingReadOutcome.INVALID_RESPONSE, null, null));

        var bodyId = bodyEl.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
        if (bodyId is null || (requestedId is not null && bodyId != requestedId))
            return (requestedId ?? bodyId, new MarketplaceListingDetailResult(requestedId ?? bodyId ?? "", MarketplaceListingReadOutcome.INVALID_RESPONSE, null, null));

        var sellerId = bodyEl.TryGetProperty("seller_id", out var sellerEl) ? RenderNumberOrString(sellerEl) : null;
        if (sellerId is null) return (bodyId, new MarketplaceListingDetailResult(bodyId, MarketplaceListingReadOutcome.INVALID_RESPONSE, null, null));
        if (bodyEl.TryGetProperty("site_id", out var siteEl) && siteEl.ValueKind == JsonValueKind.String && siteEl.GetString() != SiteId)
            return (bodyId, new MarketplaceListingDetailResult(bodyId, MarketplaceListingReadOutcome.INVALID_RESPONSE, null, null));
        if (sellerId != expectedSellerId)
            return (bodyId, new MarketplaceListingDetailResult(bodyId, MarketplaceListingReadOutcome.SELLER_MISMATCH, null, null));

        var warnings = new List<string>();
        var (status, native) = MapStatus(bodyEl);
        if (status is null) return (bodyId, new MarketplaceListingDetailResult(bodyId, MarketplaceListingReadOutcome.UNMAPPED_STATUS, null, null));

        var sku = ExtractSku(bodyEl, warnings);
        var title = ExtractTitle(bodyEl, warnings);
        var price = ExtractPrice(bodyEl, warnings);
        var variationCount = bodyEl.TryGetProperty("variations", out var varsEl) && varsEl.ValueKind == JsonValueKind.Array ? varsEl.GetArrayLength() : 0;
        var listingUrl = ExtractUrl(bodyEl, warnings);
        var observedAt = bodyEl.TryGetProperty("last_updated", out var luEl) && luEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(luEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed : (DateTimeOffset?)null;

        var facts = new NormalizedListingFacts(bodyId, sku, title, price, status.Value, native, variationCount, listingUrl, observedAt, warnings);
        return (bodyId, new MarketplaceListingDetailResult(bodyId, MarketplaceListingReadOutcome.FOUND, facts, null));
    }

    private static (MarketplaceListingStatus? Status, string? Native) MapStatus(JsonElement body)
    {
        var status = body.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        if (string.IsNullOrEmpty(status)) return (null, null);

        var subTokens = new List<string>();
        if (body.TryGetProperty("sub_status", out var subEl) && subEl.ValueKind == JsonValueKind.Array)
            foreach (var sub in subEl.EnumerateArray())
                if (sub.ValueKind == JsonValueKind.String && NormalizeToken(sub.GetString()) is { } token) subTokens.Add(token);

        var statusToken = NormalizeToken(status) ?? string.Empty;
        var native = subTokens.Count == 0 ? statusToken : statusToken + ":" + string.Join(",", subTokens.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));
        if (native.Length > 200) native = native[..200];

        MarketplaceListingStatus? normalized = status switch
        {
            "active" => MarketplaceListingStatus.ACTIVE,
            "paused" => MarketplaceListingStatus.PAUSED,
            "closed" or "inactive" => MarketplaceListingStatus.INACTIVE,
            "not_yet_active" or "programmed" => MarketplaceListingStatus.DRAFT,
            "under_review" or "payment_required" or "pending" => MarketplaceListingStatus.ERROR,
            _ => null,
        };
        return (normalized, native);
    }

    private static string? NormalizeToken(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var filtered = new string(raw.ToLowerInvariant().Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_').ToArray());
        return filtered.Length == 0 ? null : filtered;
    }

    private static string? ExtractSku(JsonElement body, List<string> warnings)
    {
        if (!body.TryGetProperty("attributes", out var attrsEl) || attrsEl.ValueKind != JsonValueKind.Array) return null;
        var values = new List<string>();
        foreach (var attr in attrsEl.EnumerateArray())
        {
            if (attr.ValueKind == JsonValueKind.Object
                && attr.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && idEl.GetString() == "SELLER_SKU"
                && attr.TryGetProperty("value_name", out var valEl) && valEl.ValueKind == JsonValueKind.String
                && NormalizeSku(valEl.GetString()) is { } normalized)
                values.Add(normalized);
        }
        var distinct = values.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0) return null;
        if (distinct.Length > 1) { warnings.Add("SKU_AMBIGUOUS"); return null; }
        return distinct[0];
    }

    private static string? NormalizeSku(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var normalized = new string(raw.Normalize(NormalizationForm.FormC).Trim().Where(c => !char.IsControl(c)).ToArray());
        return normalized.Length is < 1 or > 200 ? null : normalized;
    }

    private static string? ExtractTitle(JsonElement body, List<string> warnings)
    {
        if (!body.TryGetProperty("title", out var titleEl) || titleEl.ValueKind != JsonValueKind.String) return null;
        var raw = titleEl.GetString();
        if (string.IsNullOrEmpty(raw)) return null;
        var trimmed = new string(raw.Trim().Where(c => !char.IsControl(c)).ToArray());
        if (trimmed.Length == 0) return null;
        if (trimmed.Length > 500)
        {
            var cut = 500;
            if (char.IsHighSurrogate(trimmed[cut - 1])) cut--;
            trimmed = trimmed[..cut];
            warnings.Add("TITLE_TRUNCATED");
        }
        return trimmed;
    }

    /// <summary>ADR-0029 §1 (complementing ADR-0025 §B.3): ObservedPrice is validated on the EXACT JSON number lexeme — never on a <c>decimal</c> that already
    /// rounded it. BRL only; zero/negative ⇒ <c>PRICE_NOT_POSITIVE</c>; positive but not exactly representable in <c>numeric(18,2)</c> (more than two significant
    /// fractional digits, more than 16 integer digits, or any magnitude beyond a finite decimal) ⇒ <c>PRICE_PRECISION_UNSUPPORTED</c>; otherwise the exact value.
    /// Never rounds, truncates, saturates or throws; exactly one warning per rejected price. Currency is checked first.</summary>
    private static decimal? ExtractPrice(JsonElement body, List<string> warnings)
    {
        if (!body.TryGetProperty("price", out var priceEl) || priceEl.ValueKind != JsonValueKind.Number) return null;
        var currency = body.TryGetProperty("currency_id", out var curEl) && curEl.ValueKind == JsonValueKind.String ? curEl.GetString() : null;
        if (!string.Equals(currency, "BRL", StringComparison.Ordinal)) { warnings.Add("PRICE_CURRENCY_UNSUPPORTED"); return null; }
        switch (ExactPrice.Parse(priceEl.GetRawText(), out var price))
        {
            case ExactPriceResult.Valid: return price;
            case ExactPriceResult.NotPositive: warnings.Add("PRICE_NOT_POSITIVE"); return null;
            default: warnings.Add("PRICE_PRECISION_UNSUPPORTED"); return null;
        }
    }

    internal enum ExactPriceResult { Valid, NotPositive, PrecisionUnsupported }

    /// <summary>Lexeme-level price classification (ADR-0029 §1). Input is a JSON number as produced by <see cref="JsonElement.GetRawText"/> (RFC 8259 grammar is
    /// already guaranteed by the parser). The work is linear in the lexeme length, uses no floating point and no arithmetic that can overflow on provider input:
    /// the exponent is read saturating, digits are counted as strings, and a <see cref="decimal"/> is built only once the value is proven to need at most 18
    /// significant digits and scale ≤ 2.</summary>
    internal static class ExactPrice
    {
        private const long ExponentSaturation = 1_000_000_000;
        private const int MaxIntegerDigits = 16;   // numeric(18,2)
        private const int MaxScale = 2;

        public static ExactPriceResult Parse(string lexeme, out decimal value)
        {
            value = 0m;
            var span = lexeme.AsSpan();
            var i = 0;
            var negative = span.Length > 0 && span[0] == '-';
            if (negative) i++;

            var intStart = i;
            while (i < span.Length && span[i] is >= '0' and <= '9') i++;
            var intDigits = span[intStart..i];

            ReadOnlySpan<char> fracDigits = default;
            if (i < span.Length && span[i] == '.')
            {
                i++;
                var fracStart = i;
                while (i < span.Length && span[i] is >= '0' and <= '9') i++;
                fracDigits = span[fracStart..i];
            }

            long exponent = 0;
            if (i < span.Length && span[i] is 'e' or 'E')
            {
                i++;
                var expNegative = false;
                if (i < span.Length && (span[i] == '+' || span[i] == '-')) { expNegative = span[i] == '-'; i++; }
                while (i < span.Length && span[i] == '0') i++;                       // leading zeros carry no information
                long magnitude = 0; var significant = 0;
                while (i < span.Length && span[i] is >= '0' and <= '9')
                {
                    significant++;
                    if (significant > 9) magnitude = ExponentSaturation;               // saturating: with a non-zero significand this already decides the outcome
                    else magnitude = magnitude * 10 + (span[i] - '0');
                    i++;
                }
                if (magnitude > ExponentSaturation) magnitude = ExponentSaturation;
                exponent = expNegative ? -magnitude : magnitude;
            }

            // value = 0.<digits> × 10^position, with <digits> = int + frac stripped of leading and trailing zeros. The digit string is never materialized: it is
            // addressed through the two spans, so the work is linear in the lexeme and the allocation is constant.
            var total = intDigits.Length + fracDigits.Length;
            var lead = 0; while (lead < total && Digit(intDigits, fracDigits, lead) == '0') lead++;
            var end = total; while (end > lead && Digit(intDigits, fracDigits, end - 1) == '0') end--;
            var length = end - lead;
            if (length == 0 || negative) return ExactPriceResult.NotPositive;       // zero (any spelling) or a negative: "not positive"

            var position = (long)intDigits.Length + exponent - lead;                 // digits before the decimal point
            var scale = length - position;
            if (scale > MaxScale || position > MaxIntegerDigits) return ExactPriceResult.PrecisionUnsupported;

            // Here: length ≤ position + 2 ≤ 18 significant digits, so the mantissa fits ulong and the value fits decimal and numeric(18,2).
            ulong mantissa = 0;
            for (var k = lead; k < end; k++) mantissa = mantissa * 10 + (ulong)(Digit(intDigits, fracDigits, k) - '0');
            var exp10 = (int)(position - length);                                    // ≥ −2
            decimal result = mantissa;
            if (exp10 > 0) for (var k = 0; k < exp10; k++) result *= 10m;
            else if (exp10 < 0) for (var k = 0; k < -exp10; k++) result /= 10m;
            value = result + 0.00m;                                                  // exactly two decimal places: 10.100 ⇒ 10.10, 100 ⇒ 100.00
            return ExactPriceResult.Valid;
        }

        private static char Digit(ReadOnlySpan<char> integer, ReadOnlySpan<char> fraction, int index) => index < integer.Length ? integer[index] : fraction[index - integer.Length];
    }
    private static string? ExtractUrl(JsonElement body, List<string> warnings)
    {
        if (!body.TryGetProperty("permalink", out var el) || el.ValueKind != JsonValueKind.String) return null;
        var raw = el.GetString();
        if (string.IsNullOrEmpty(raw) || raw.Length > 2000 || !Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !(uri.Host.Equals("mercadolivre.com.br", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".mercadolivre.com.br", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add("URL_REJECTED");
            return null;
        }
        return raw;
    }

    private static string? RenderNumberOrString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number when element.TryGetInt64(out var n) => n.ToString(CultureInfo.InvariantCulture),
        JsonValueKind.String => element.GetString(),
        _ => null,
    };

    // ================= shared HTTP/JSON helpers =================

    private async Task<(string Id, string SiteId)> GetUsersMeAsync(string accessToken, Guid? accountId, CancellationToken cancellationToken)
    {
        var client = _httpFactory.CreateClient("MercadoLivre.Api");
        var read = await _reader.ExecuteReadAsync(client, () => BearerGetRequest("https://api.mercadolibre.com/users/me", accessToken), MarketplaceSafeReadExecutor.AttemptWindow, "PROVIDER_UNREACHABLE", "MERCADO_LIVRE", "USERS_ME", accountId, null, MarketplaceBoundedBodyReader.ReadAsync, cancellationToken);
        var response = read.Response;
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw MapReadErrorResponse(response.StatusCode);
            var body = read.Body!;
            using var doc = ParseProviderJson(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var idEl))
                throw new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.NON_RETRYABLE, "PROVIDER_RESPONSE_INVALID", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED));
            try
            {
                var id = RenderNumberOrString(idEl) ?? "";
                var siteId = root.TryGetProperty("site_id", out var siteEl) && siteEl.ValueKind == JsonValueKind.String ? siteEl.GetString() ?? "" : "";
                return (id, siteId);
            }
            catch (InvalidOperationException) // provider text that is not valid UTF-16
            {
                throw new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.NON_RETRYABLE, "PROVIDER_RESPONSE_INVALID", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED));
            }
        }
    }

    private async Task<AccountCapabilityState> ProbeListingsReadGrantAsync(string accessToken, string userId, Guid? accountId, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpFactory.CreateClient("MercadoLivre.Api");
            var response = await _reader.ExecuteAsync(client, () => BearerGetRequest($"https://api.mercadolibre.com/users/{Uri.EscapeDataString(userId)}/items/search?limit=1", accessToken), MarketplaceSafeReadExecutor.AttemptWindow, "PROVIDER_UNREACHABLE", "MERCADO_LIVRE", "ITEMS_SEARCH_PROBE", accountId, cancellationToken);
            using (response)
                return response.IsSuccessStatusCode ? AccountCapabilityState.GRANTED : AccountCapabilityState.UNKNOWN;
        }
        catch
        {
            // §A.5.2: "anything else ⇒ UNKNOWN (403 is ambiguous ... the ML adapter never reports
            // DENIED)." Includes a proven-unreachable/rate-limited grant probe.
            return AccountCapabilityState.UNKNOWN;
        }
    }

    /// <summary>A 2xx whose payload is not even syntactically JSON (empty, truncated, not JSON) is a
    /// malformed PROVIDER response: a typed, NON_RETRYABLE <c>PROVIDER_RESPONSE_INVALID</c> at the adapter
    /// boundary — never a raw <see cref="JsonException"/> that a caller could only mistake for a transient
    /// local failure.</summary>
    private static JsonDocument ParseProviderJson(string body)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException)
        {
            throw new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.NON_RETRYABLE, "PROVIDER_RESPONSE_INVALID", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED));
        }
    }

    private static HttpRequestMessage BearerGetRequest(string url, string accessToken)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return message;
    }

    /// <summary>A-10 PRE-MATERIALIZATION row test-only seam: when set, <see cref="TokenFormRequest"/>
    /// throws this instead of building the request — proving the REAL production
    /// request-factory-exception path (<c>MarketplaceHttpSender.SendAsync</c>'s own <c>try/catch</c>
    /// around calling <c>requestFactory()</c>, which converts any exception there into a proven
    /// NOT_SENT <see cref="MarketplaceSendOutcome"/> — never <c>HttpClient.SendAsync</c> itself)
    /// without inventing a broad new production abstraction. Always <c>null</c> in production;
    /// visible only via <c>InternalsVisibleTo</c> to the integration test project, and only ever
    /// set for the direct duration of one scripted test call.</summary>
    internal static Func<Exception>? TestOnlyRequestMaterializationFault;

    private static HttpRequestMessage TokenFormRequest(Dictionary<string, string> form)
    {
        if (TestOnlyRequestMaterializationFault is { } fault) throw fault();
        var message = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(form) };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return message;
    }

    /// <summary>A-03: the token endpoint's own 429 must propagate Retry-After exactly like the
    /// safe-read executor already does for GET reads — parsed by the SAME <see cref="RetryAfterParser"/>
    /// (delta-seconds or HTTP-date, bounded/clamped by that parser; a malformed/missing header
    /// yields no invented value, never a crash or an unbounded block).</summary>
    private MarketplaceProviderCallException MapTokenErrorResponse(HttpResponseMessage response, string body)
    {
        var status = response.StatusCode;
        var errorCode = TryGetJsonStringField(body, "error");
        if ((int)status == 429)
        {
            var retryAfter = RetryAfterParser.Parse(response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null, _clock.UtcNow);
            return new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.RATE_LIMITED, "PROVIDER_RATE_LIMITED", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED, SafeProviderErrorCode(errorCode), retryAfter));
        }
        if ((int)status is >= 500 and <= 599)
            return new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.RETRYABLE, "PROVIDER_UNAVAILABLE", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED, SafeProviderErrorCode(errorCode)));
        if (errorCode is "invalid_grant" or "unauthorized_client" or "invalid_operator_user_id")
            return new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.USER_ACTION_REQUIRED, "PROVIDER_GRANT_REJECTED", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED, SafeProviderErrorCode(errorCode)));
        if (errorCode is "invalid_client" or "unauthorized_application")
            return new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.NON_RETRYABLE, "PROVIDER_APP_CREDENTIALS_INVALID", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED, SafeProviderErrorCode(errorCode)));
        return new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.NON_RETRYABLE, "PROVIDER_REQUEST_REJECTED", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED, SafeProviderErrorCode(errorCode)));
    }

    private static MarketplaceProviderCallException MapReadErrorResponse(HttpStatusCode status) => (int)status switch
    {
        401 => new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.AUTH_RENEWAL_REQUIRED, "PROVIDER_TOKEN_REJECTED", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED)),
        403 => new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.USER_ACTION_REQUIRED, "PROVIDER_ACCESS_FORBIDDEN", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED)),
        >= 500 and <= 599 => new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.RETRYABLE, "PROVIDER_UNAVAILABLE", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED)),
        _ => new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.NON_RETRYABLE, "PROVIDER_REQUEST_REJECTED", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED)),
    };

    /// <summary>ADR-0024 §9 policy: only an allow-listed, bounded provider error code is retained.</summary>
    private static string? SafeProviderErrorCode(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length is < 1 or > 64) return null;
        return code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '-') ? code : null;
    }

    private static string? TryGetJsonStringField(string body, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(field, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>A-08 / ADR-0029 §4: the shared bounded streaming reader (1 MiB); callers pass the attempt/token window token so the read is part of the deadline.</summary>
    private static Task<string> ReadBoundedBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken) => MarketplaceBoundedBodyReader.ReadAsync(response, cancellationToken);

    // ================= §A.6 secret material (adapter-owned JSON v1) =================

    private static ReadOnlyMemory<byte> BuildSecretJson(string accessToken, string refreshToken, string userId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("access_token", accessToken);
            writer.WriteString("refresh_token", refreshToken);
            writer.WriteString("user_id", userId);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>ADR-0030 §1: the longest bearer the adapter will put on the wire (RFC 6750 tokens are far shorter).</summary>
    private const int MaxBearerLength = 4096;

    /// <summary>The adapter's verdict on the decrypted secret material (ADR-0030 §1). The secret strings stay in memory only and are never logged, thrown or persisted.</summary>
    private sealed record StoredSecret(string AccessToken, string? RefreshToken, string UserId, bool UsableForBearer, bool UsableForRefresh)
    {
        public static readonly StoredSecret Unusable = new(string.Empty, null, string.Empty, false, false);
    }

    /// <summary>ADR-0030 §1: a pure, total reader of the adapter-owned v1 secret JSON. It never throws: material that is not JSON, whose root is not an object, or in which a present string
    /// value of access_token / refresh_token / user_id cannot be decoded to valid text (a lone surrogate escape, invalid UTF-8) is simply unusable. Bearer usability additionally
    /// needs 1..4096 characters of U+0021..U+007E in access_token (nothing the HTTP stack could refuse, truncate or smuggle) and a non-empty user_id; refresh usability is the unchanged
    /// ADR-0025 §F.3 predicate (access_token, refresh_token and user_id all non-empty).</summary>
    private static StoredSecret ReadStoredSecret(ReadOnlyMemory<byte> material)
    {
        try
        {
            using var doc = JsonDocument.Parse(material);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return StoredSecret.Unusable;
            var accessToken = ReadStoredString(root, "access_token") ?? string.Empty;
            var refreshToken = ReadStoredString(root, "refresh_token");
            var userId = ReadStoredString(root, "user_id") ?? string.Empty;
            var bearer = userId.Length > 0 && IsTransmissibleBearer(accessToken);
            var refresh = accessToken.Length > 0 && userId.Length > 0 && !string.IsNullOrEmpty(refreshToken);
            return new StoredSecret(accessToken, refreshToken, userId, bearer, refresh);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return StoredSecret.Unusable; }
    }

    private static string? ReadStoredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static bool IsTransmissibleBearer(string token)
    {
        if (token.Length is < 1 or > MaxBearerLength) return false;
        foreach (var ch in token) if (ch is < (char)0x21 or > (char)0x7E) return false;
        return true;
    }

    /// <summary>ADR-0030 §2: unusable stored material is signalled BEFORE any request with a typed, message-less internal exception — never a provider failure — which the credential executor
    /// translates into the existing confirmed-loss path.</summary>
    private static StoredSecret RequireBearerUsable(ReadOnlyMemory<byte> material)
    {
        var stored = ReadStoredSecret(material);
        if (!stored.UsableForBearer) throw new MarketplaceStoredCredentialUnusableException();
        return stored;
    }

    private static bool TryParseTokenResponse(string body, out string accessToken, out string refreshToken, out string userId, out long expiresIn)
    {
        accessToken = string.Empty; refreshToken = string.Empty; userId = string.Empty; expiresIn = 0;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (!root.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String) return false;
            accessToken = at.GetString() ?? string.Empty;
            if (accessToken.Length is < 1 or > 4096) return false;

            if (!root.TryGetProperty("token_type", out var tt) || tt.ValueKind != JsonValueKind.String
                || !string.Equals(tt.GetString(), "bearer", StringComparison.OrdinalIgnoreCase)) return false;

            if (!root.TryGetProperty("user_id", out var uidEl) || uidEl.ValueKind != JsonValueKind.Number || !uidEl.TryGetInt64(out var uidNum) || uidNum <= 0) return false;
            var uid = uidNum.ToString(CultureInfo.InvariantCulture);
            if (!UserIdPattern.IsMatch(uid)) return false;
            userId = uid;

            if (!root.TryGetProperty("refresh_token", out var rt) || rt.ValueKind != JsonValueKind.String) return false;
            refreshToken = rt.GetString() ?? string.Empty;
            if (refreshToken.Length is < 1 or > 4096) return false;

            if (!root.TryGetProperty("expires_in", out var ei) || ei.ValueKind != JsonValueKind.Number || !ei.TryGetInt64(out expiresIn) || expiresIn <= 0) return false;

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }
}
