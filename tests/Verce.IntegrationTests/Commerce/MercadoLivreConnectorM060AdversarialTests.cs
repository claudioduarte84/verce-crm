using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Verce.Infrastructure.Marketplaces.MercadoLivre;
using Verce.Modules.Commerce;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// M060 (Correct + Validate) — adversarial inputs for the Gate 058 fixes, written AFTER a critical review of the M059 diff. Each test targets a path
/// the M059 suites did not execute: the numeric edge space of F01, string-level hostile JSON (lone surrogates, BOM, nesting) that could still
/// abort a bulk batch through an untyped exception, and cancellation/timeout classification of the safe reader (F02). The wire is scripted; no
/// network, seller or credential exists.
/// </summary>
public sealed class MercadoLivreConnectorM060AdversarialTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"m060-access-secret","refresh_token":"m060-refresh-secret","user_id":"777"}""");

    private sealed class Rig
    {
        public required MercadoLivreConnector Connector { get; init; }
        public required List<string> Requests { get; init; }
        public required RecordingDelay Delay { get; init; }
        public required CapturingLoggerProvider Logger { get; init; }
        public required TestClock Clock { get; init; }
    }

    private static Rig Create(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
    {
        var options = Options.Create(new MercadoLivreOptions { Enabled = true, ClientId = "900000001", RedirectUri = "https://host.example/api/commerce/marketplace-authorizations/MERCADO_LIVRE/callback", ResolvedClientSecret = "m060-client-secret" });
        var clock = new TestClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z")); var requests = new List<string>(); var logger = new CapturingLoggerProvider();
        var handler = new Handler(request => { requests.Add(request.RequestUri!.PathAndQuery); return respond(request, requests.Count); });
        var sender = new MarketplaceHttpSender(new Microsoft.Extensions.Logging.Logger<MarketplaceHttpSender>(new Microsoft.Extensions.Logging.LoggerFactory([logger])));
        var delay = new RecordingDelay();
        var reader = new MarketplaceSafeReadExecutor(sender, delay, new ZeroJitter(), clock);
        return new Rig { Connector = new MercadoLivreConnector(new Factory(handler), sender, reader, options, clock), Requests = requests, Delay = delay, Logger = logger, Clock = clock };
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request)); }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory { private readonly HttpClient _client = new(handler); public HttpClient CreateClient(string name) => _client; }
    private sealed class ZeroJitter : IMarketplaceJitter { public TimeSpan Next() => TimeSpan.Zero; }
    private sealed class RecordingDelay : IMarketplaceDelay
    {
        public List<TimeSpan> Delays { get; } = [];
        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) { Delays.Add(duration); cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string Peer = """{"id":"MLB-PEER","status_code":200,"body":{"id":"MLB-PEER","seller_id":777,"site_id":"MLB","status":"active","price":10.00,"currency_id":"BRL","title":"Peer"}}""";

    private static async Task<IReadOnlyList<MarketplaceListingDetailResult>> ReadAsync(string json, params string[] ids)
    {
        var rig = Create((_, _) => Json(HttpStatusCode.OK, json));
        return await rig.Connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", Secret, ids), CancellationToken.None);
    }

    /// <summary>The fixture must reach the parser as the 6-character JSON escape <c>\ud800</c>: a decoded lone surrogate char would be replaced by U+FFFD on encoding and prove nothing.</summary>
    private static void AssertEscapedNotDecoded(string json)
    {
        System.Text.RegularExpressions.Regex.IsMatch(json, @"\\u[dD][89a-fA-F][0-9a-fA-F]{2}").Should().BeTrue("the fixture carries a surrogate as a 6-character JSON escape"); json.Any(char.IsSurrogate).Should().BeFalse("never a decoded surrogate char");
        Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(json)).Should().Be(json, "the text survives UTF-8 transport unchanged");
    }

    private static string Body(string fields) => "{\"id\":\"MLB1\",\"seller_id\":777,\"site_id\":\"MLB\",\"status\":\"active\"," + fields + "}";
    private static string Element(string body) => "{\"id\":\"MLB1\",\"status_code\":200,\"body\":" + body + "}";

    // ============================================================ F01 — numeric edge space

    [Theory]
    [InlineData("1E-400", "PRICE_PRECISION_UNSUPPORTED")]                          // underflows decimal
    [InlineData("1E-30", "PRICE_PRECISION_UNSUPPORTED")]
    [InlineData("4.9E-324", "PRICE_PRECISION_UNSUPPORTED")]
    [InlineData("123456789012345678901234567890", "PRICE_PRECISION_UNSUPPORTED")]  // 30 digits, beyond decimal.MaxValue (7.9e28)
    [InlineData("1E28", "PRICE_PRECISION_UNSUPPORTED")]                            // representable decimal, far beyond numeric(18,2)
    [InlineData("99999999999999999999.99", "PRICE_PRECISION_UNSUPPORTED")]
    [InlineData("-0", "PRICE_NOT_POSITIVE")]
    [InlineData("-0.0", "PRICE_NOT_POSITIVE")]
    [InlineData("0E0", "PRICE_NOT_POSITIVE")]
    [InlineData("0.000", "PRICE_NOT_POSITIVE")]
    public async Task F01_hostile_numeric_literals_are_always_unknown_with_exactly_the_ADR_0029_warning_and_never_throw(string literal, string warning)
    {
        var result = (await ReadAsync("[" + Element(Body("\"price\":" + literal + ",\"currency_id\":\"BRL\"")) + "]", "MLB1")).Single();
        result.Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
        result.Facts!.ObservedPrice.Should().BeNull();
        result.Facts.NormalizationWarnings.Should().Equal(warning);
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("1.0E+1", "10")]
    [InlineData("1E0", "1")]
    [InlineData("0.01", "0.01")]
    [InlineData("9999999999999999.99", "9999999999999999.99")]
    [InlineData("0.10", "0.10")]
    public async Task F01_edge_valid_literals_are_kept_exactly(string literal, string expected)
    {
        var result = (await ReadAsync("[" + Element(Body("\"price\":" + literal + ",\"currency_id\":\"BRL\"")) + "]", "MLB1")).Single();
        result.Facts!.ObservedPrice.Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        result.Facts.NormalizationWarnings.Should().BeEmpty();
    }

    [Fact]
    public async Task F01_a_500_digit_number_inside_the_1MiB_cap_is_unknown_and_does_not_abort_the_batch()
    {
        var huge = "1" + new string('0', 500);
        var results = await ReadAsync("[" + Element(Body("\"price\":" + huge + ",\"currency_id\":\"BRL\"")) + "," + Peer + "]", "MLB1", "MLB-PEER");
        results[0].Facts!.ObservedPrice.Should().BeNull(); results[0].Facts!.NormalizationWarnings.Should().Equal("PRICE_PRECISION_UNSUPPORTED");
        results[1].Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
        var tiny = "0." + new string('0', 500) + "1";
        var tinyResult = (await ReadAsync("[" + Element(Body("\"price\":" + tiny + ",\"currency_id\":\"BRL\"")) + "]", "MLB1")).Single();
        tinyResult.Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND); tinyResult.Facts!.ObservedPrice.Should().BeNull();
    }

    [Fact]
    public async Task F01_a_full_page_of_twenty_extreme_prices_plus_valid_peers_never_throws()
    {
        var extremes = Enumerable.Range(1, 20).Select(i => "{\"id\":\"MLB-X" + i + "\",\"status_code\":200,\"body\":{\"id\":\"MLB-X" + i + "\",\"seller_id\":777,\"status\":\"active\",\"price\":79228162514264337593543950335,\"currency_id\":\"BRL\"}}");
        var ids = Enumerable.Range(1, 20).Select(i => "MLB-X" + i).Append("MLB-PEER").ToArray();
        var results = await ReadAsync("[" + string.Join(",", extremes) + "," + Peer + "]", ids);
        results.Should().HaveCount(21).And.OnlyContain(r => r.Outcome == MarketplaceListingReadOutcome.FOUND);
        results.Count(r => r.Facts!.ObservedPrice == null).Should().Be(20); results.Single(r => r.ExternalListingId == "MLB-PEER").Facts!.ObservedPrice.Should().Be(10.00m);
    }

    // ============================================================ element isolation against hostile strings (F01/F02 neighbourhood)

    [Theory]
    [InlineData("\"title\":\"\\ud800\"")]
    [InlineData("\"title\":\"ok\\udc00ok\"")]
    [InlineData("\"attributes\":[{\"id\":\"SELLER_SKU\",\"value_name\":\"\\ud800\"}]")]
    [InlineData("\"permalink\":\"https://mercadolivre.com.br/\\ud800\"")]
    [InlineData("\"sub_status\":[\"\\ud800\"]")]
    [InlineData("\"last_updated\":\"\\ud800\"")]
    [InlineData("\"currency_id\":\"\\ud800\",\"price\":10")]
    public async Task Hostile_lone_surrogate_in_one_field_never_aborts_the_batch_and_never_corrupts_the_peer(string hostileFields)
    {
        AssertEscapedNotDecoded(hostileFields);
        var results = await ReadAsync("[" + Element(Body(hostileFields)) + "," + Peer + "]", "MLB1", "MLB-PEER");
        results.Single(r => r.ExternalListingId == "MLB-PEER").Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND, "a hostile string in one element must not touch its siblings");
        var hostile = results.Single(r => r.ExternalListingId == "MLB1");
        hostile.Outcome.Should().Be(MarketplaceListingReadOutcome.INVALID_RESPONSE, "text that is not valid UTF-16 makes the element's body unparseable (ADR-0025 §B.3)");
        hostile.Facts.Should().BeNull();
    }

    [Theory]
    [InlineData("{\"id\":\"MLB1\",\"seller_id\":777,\"site_id\":\"MLB\",\"status\":\"\\ud800\"}")]
    [InlineData("{\"id\":\"MLB1\",\"seller_id\":777,\"site_id\":\"\\ud800\",\"status\":\"active\"}")]
    [InlineData("{\"id\":\"MLB1\",\"seller_id\":\"\\ud800\",\"site_id\":\"MLB\",\"status\":\"active\"}")]
    [InlineData("{\"id\":\"MLB1\",\"seller_id\":777,\"site_id\":\"MLB\",\"status\":\"active\",\"title\":\"\\ud800\"}")]
    public async Task U_2_hostile_lone_surrogate_in_a_structural_field_that_appears_once_is_exactly_INVALID_RESPONSE_and_the_peer_is_FOUND(string body)
    {
        AssertEscapedNotDecoded(body);
        // Each fixture contains the hostile property exactly once: no later valid duplicate can mask it (GetProperty reads the LAST duplicate).
        using (var doc = System.Text.Json.JsonDocument.Parse(body)) doc.RootElement.EnumerateObject().Select(p => p.Name).Should().OnlyHaveUniqueItems();
        var results = await ReadAsync("[" + Element(body) + "," + Peer + "]", "MLB1", "MLB-PEER");
        results.Single(r => r.ExternalListingId == "MLB-PEER").Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
        var hostile = results.Single(r => r.ExternalListingId == "MLB1");
        hostile.Outcome.Should().Be(MarketplaceListingReadOutcome.INVALID_RESPONSE); hostile.Facts.Should().BeNull();
    }

    [Theory]
    [InlineData("{\"id\":\"\\ud800\",\"status_code\":200,\"body\":{\"id\":\"\\ud800\",\"seller_id\":777,\"status\":\"active\"}}")]
    [InlineData("{\"id\":\"MLB1\",\"status_code\":200,\"body\":{\"id\":\"\\ud800\",\"seller_id\":777,\"status\":\"active\"}}")]
    public async Task Hostile_lone_surrogate_in_an_identity_field_never_aborts_the_batch(string hostileElement)
    {
        AssertEscapedNotDecoded(hostileElement);
        var results = await ReadAsync("[" + hostileElement + "," + Peer + "]", "MLB1", "MLB-PEER");
        results.Single(r => r.ExternalListingId == "MLB-PEER").Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
        results.Single(r => r.ExternalListingId == "MLB1").Outcome.Should().NotBe(MarketplaceListingReadOutcome.FOUND);
    }

    [Fact]
    public async Task A_UTF8_BOM_or_deep_nesting_in_a_200_body_is_a_typed_NON_RETRYABLE_PROVIDER_RESPONSE_INVALID()
    {
        foreach (var body in new[] { "\uFEFF[]", new string('[', 300) + new string(']', 300) })
        {
            var rig = Create((_, _) => Json(HttpStatusCode.OK, body));
            var act = () => rig.Connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", Secret, ["MLB1"]), CancellationToken.None);
            var failure = (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
            failure.Classification.Should().Be(MarketplaceFailureClassification.NON_RETRYABLE); failure.SafeCode.Should().Be("PROVIDER_RESPONSE_INVALID");
            rig.Requests.Should().HaveCount(1);
        }
    }

    // ============================================================ hostile UTF-16 in the other adapter reads (scan, users/me, token error body)

    [Fact]
    public async Task Scan_a_result_id_that_is_not_valid_UTF16_is_a_rejected_identifier_not_an_exception()
    {
        var rig = Create((_, _) => Json(HttpStatusCode.OK, "{\"results\":[\"MLB1\",\"\\ud800\",\"MLB2\"],\"scroll_id\":\"S\"}"));
        var page = await rig.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, null), CancellationToken.None);
        page.ExternalListingIds.Should().Equal("MLB1", "MLB2"); page.RejectedIdentifierCount.Should().Be(1, "the orchestrator turns a rejected identifier into LISTING_ENUMERATION_INCOMPLETE");
    }

    [Fact]
    public async Task Scan_an_unreadable_scroll_id_never_reads_as_no_continuation_which_would_end_the_enumeration_as_complete()
    {
        var rig = Create((_, _) => Json(HttpStatusCode.OK, "{\"results\":[\"MLB1\"],\"scroll_id\":\"\\ud800\"}"));
        var act = () => rig.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, null), CancellationToken.None);
        var failure = (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.NON_RETRYABLE); failure.SafeCode.Should().Be("PROVIDER_RESPONSE_INVALID");
    }

    [Theory]
    [InlineData("{\"id\":\"\\ud800\",\"site_id\":\"MLB\"}")]
    [InlineData("{\"id\":777,\"site_id\":\"\\ud800\"}")]
    public async Task Users_me_with_text_that_is_not_valid_UTF16_is_a_typed_NON_RETRYABLE_failure(string usersMe)
    {
        var rig = Create((request, _) => request.RequestUri!.AbsolutePath == "/users/me" ? Json(HttpStatusCode.OK, usersMe) : Json(HttpStatusCode.OK, "{}"));
        var act = () => rig.Connector.InspectAsync(new MarketplaceAccountInspectionRequest(Guid.NewGuid(), MercadoLivreConnector.Code, Secret), CancellationToken.None);
        var failure = (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.NON_RETRYABLE); failure.SafeCode.Should().Be("PROVIDER_RESPONSE_INVALID");
    }

    [Fact]
    public async Task Token_endpoint_bodies_with_text_that_is_not_valid_UTF16_stay_typed_failures_and_never_leak_an_untyped_exception()
    {
        var refresh = Create((_, _) => Json(HttpStatusCode.OK, "{\"access_token\":\"\\ud800\",\"token_type\":\"bearer\",\"expires_in\":60,\"user_id\":777,\"refresh_token\":\"r\"}"));
        var refreshAct = () => refresh.Connector.RefreshAsync(new MarketplaceRefreshRequest(Guid.NewGuid(), MercadoLivreConnector.Code, Secret), CancellationToken.None);
        (await refreshAct.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure.SafeCode.Should().Be("PROVIDER_RESPONSE_INVALID");
        var error = Create((_, _) => Json(HttpStatusCode.BadRequest, "{\"error\":\"\\ud800\"}"));
        var errorAct = () => error.Connector.RefreshAsync(new MarketplaceRefreshRequest(Guid.NewGuid(), MercadoLivreConnector.Code, Secret), CancellationToken.None);
        (await errorAct.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure.SafeCode.Should().Be("PROVIDER_REQUEST_REJECTED");
    }

    // ============================================================ F02 — cancellation and timeout classification of the safe reader

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Caller_cancellation_while_a_non_final_attempt_is_in_flight_is_honored_as_OperationCanceled_and_no_further_attempt_starts(int cancelledAttempt)
    {
        using var cts = new CancellationTokenSource();
        var rig = Create((_, n) =>
        {
            if (n == cancelledAttempt) { cts.Cancel(); throw new TaskCanceledException("caller cancelled in flight", null, cts.Token); }
            return Json(HttpStatusCode.ServiceUnavailable, "{}");
        });
        var act = () => rig.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, null), cts.Token);
        (await Record.ExceptionAsync(act)).Should().BeAssignableTo<OperationCanceledException>("ADR-0025 §F.4: cancellation honored");
        rig.Requests.Should().HaveCount(cancelledAttempt);
    }

    [Fact]
    public async Task Caller_cancellation_while_the_FINAL_attempt_is_in_flight_follows_the_F6_row_any_exception_after_SendAsync_is_RETRYABLE_SENT_OR_UNKNOWN()
    {
        // Characterization of the frozen contract, not an endorsement: ADR-0025 §F.6 maps "any exception after SendAsync invoked" to RETRYABLE /
        // SENT_OR_UNKNOWN, and MarketplaceHttpSender documents cancellation as included. A host stop also cancels the finish transaction, so the run stays
        // RUNNING and reclaimable; nothing is persisted from this classification. Recorded in the M060 report as a non-blocking observation.
        using var cts = new CancellationTokenSource();
        var rig = Create((_, n) =>
        {
            if (n == 3) { cts.Cancel(); throw new TaskCanceledException("caller cancelled in flight", null, cts.Token); }
            return Json(HttpStatusCode.ServiceUnavailable, "{}");
        });
        var act = () => rig.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, null), cts.Token);
        var failure = (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.RETRYABLE); failure.Certainty.Should().Be(MarketplaceSendCertainty.SENT_OR_UNKNOWN);
        rig.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_per_attempt_timeout_is_retried_inside_the_budget_and_ends_as_the_retryable_unreachable_classification()
    {
        var rig = Create((_, _) => throw new TaskCanceledException("timeout", new TimeoutException()));
        var act = () => rig.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, null), CancellationToken.None);
        var failure = (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.RETRYABLE); failure.Certainty.Should().Be(MarketplaceSendCertainty.SENT_OR_UNKNOWN);
        rig.Requests.Should().HaveCount(3);
    }

    // ============================================================ F02 — no payload leaks into logs

    [Fact]
    public async Task A_malformed_element_payload_never_reaches_any_log_entry()
    {
        const string canary = "CANARY-PAYLOAD-7c1e";
        var rig = Create((_, _) => Json(HttpStatusCode.OK, "[{\"id\":\"MLB1\",\"status_code\":200,\"body\":\"" + canary + "\"},{\"id\":\"MLB2\",\"status_code\":200,\"body\":null,\"note\":\"" + canary + "\"}]"));
        var results = await rig.Connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", Secret, ["MLB1", "MLB2"]), CancellationToken.None);
        results.Should().OnlyContain(r => r.Outcome == MarketplaceListingReadOutcome.INVALID_RESPONSE || r.Outcome == MarketplaceListingReadOutcome.FAILED);
        string.Join("\n", rig.Logger.Messages).Should().NotContain(canary);
        foreach (var entry in rig.Logger.Entries) entry.State.Values.Select(v => v?.ToString() ?? "").Should().NotContain(v => v.Contains(canary) || v.Contains("m060-access-secret"));
        var malformedBody = "{not-json " + canary;
        var failing = Create((_, _) => Json(HttpStatusCode.OK, malformedBody));
        var act = () => failing.Connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", Secret, ["MLB1"]), CancellationToken.None);
        await act.Should().ThrowAsync<MarketplaceProviderCallException>();
        string.Join("\n", failing.Logger.Messages).Should().NotContain(canary);
    }
}
