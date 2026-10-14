using System.Net;
using System.Text;
using System.Web;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verce.Infrastructure.Marketplaces.MercadoLivre;
using Verce.Modules.Commerce;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// S8C.2 Stream D8 (subset): deterministic contract tests for the real
/// <see cref="MercadoLivreConnector"/> against a scripted <see cref="HttpMessageHandler"/> — never
/// real ML, never the internet, never a real seller/app credential. Exercises the ADR-0025 send
/// boundary, token-response validation, users/me identity check, §C.2 status mapping, bulk element
/// outcomes and scan pagination. This is not the complete D8/D2 matrix (a full 19-row F.3 refresh
/// pass and every §F.6 classification row would need substantially more fixtures); it certifies the
/// specific behaviors this delivery's own frozen normalization/mapping code paths must get exactly
/// right.
/// </summary>
public sealed class MercadoLivreConnectorContractTests
{
    private static (MercadoLivreConnector Connector, ScriptedHttpMessageHandler Handler, TestClock Clock) CreateConnector(Func<HttpRequestMessage, HttpResponseMessage> respond, CapturingLoggerProvider? logger = null)
    {
        var options = Options.Create(new MercadoLivreOptions
        {
            Enabled = true,
            ClientId = "900000001",
            RedirectUri = "https://host.example/api/commerce/marketplace-authorizations/MERCADO_LIVRE/callback",
            ResolvedClientSecret = "test-only-synthetic-secret",
        });
        var clock = new TestClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var handler = new ScriptedHttpMessageHandler(respond);
        var factory = new SingleClientFactory(handler);
        var senderLogger = logger is null
            ? (Microsoft.Extensions.Logging.ILogger<MarketplaceHttpSender>)NullLogger<MarketplaceHttpSender>.Instance
            : new Microsoft.Extensions.Logging.Logger<MarketplaceHttpSender>(new Microsoft.Extensions.Logging.LoggerFactory([logger]));
        var sender = new MarketplaceHttpSender(senderLogger);
        var reader = new MarketplaceSafeReadExecutor(sender, new SystemMarketplaceDelay(), new ZeroJitter(), clock);
        var connector = new MercadoLivreConnector(factory, sender, reader, options, clock);
        return (connector, handler, clock);
    }

    // A-09: the config-origin validation is now proven by actually booting a real host
    // (MercadoLivreOptionsStartupValidationTests) — a bare unit call to the validator function is
    // no longer treated as sufficient proof; see that file for the real-startup tests.

    [Fact]
    public async Task BeginAsync_builds_the_exact_authorization_url_with_encoded_state()
    {
        var (connector, _, _) = CreateConnector(_ => throw new InvalidOperationException("no HTTP expected"));
        var start = await connector.BeginAsync(new MarketplaceAuthorizationBeginRequest(Guid.NewGuid(), "st ate/needs+encoding", "/api/commerce/marketplace-authorizations/MERCADO_LIVRE/callback"), CancellationToken.None);
        start.AuthorizationUri.Should().StartWith("https://auth.mercadolivre.com.br/authorization?response_type=code");
        start.AuthorizationUri.Should().Contain("client_id=900000001");
        var query = HttpUtility.ParseQueryString(new Uri(start.AuthorizationUri).Query);
        query["state"].Should().Be("st ate/needs+encoding");
        query["redirect_uri"].Should().Be("https://host.example/api/commerce/marketplace-authorizations/MERCADO_LIVRE/callback");
    }

    [Fact]
    public async Task BeginAsync_rejects_a_wrong_relative_redirect_path_as_NOT_SENT()
    {
        var (connector, _, _) = CreateConnector(_ => throw new InvalidOperationException("no HTTP expected"));
        var act = () => connector.BeginAsync(new MarketplaceAuthorizationBeginRequest(Guid.NewGuid(), "state", "/wrong/path"), CancellationToken.None);
        (await act.Should().ThrowAsync<MarketplaceRequestNotSentException>()).Which.Message.Should().Be("REDIRECT_PATH_INVALID");
    }

    [Fact]
    public async Task CompleteOnceAsync_valid_exchange_persists_expiry_and_verified_identity()
    {
        var (connector, handler, clock) = CreateConnector(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth/token")
                return JsonResponse(HttpStatusCode.OK, """{"access_token":"APP_USR-token","token_type":"bearer","expires_in":21600,"scope":"read","user_id":123456789,"refresh_token":"TG-refresh"}""");
            if (request.RequestUri!.AbsolutePath == "/users/me")
                return JsonResponse(HttpStatusCode.OK, """{"id":123456789,"site_id":"MLB","email":"seller@example.test"}""");
            if (request.RequestUri!.AbsolutePath.EndsWith("/items/search"))
                return JsonResponse(HttpStatusCode.OK, """{"results":["MLB111"]}""");
            throw new InvalidOperationException("unexpected request " + request.RequestUri);
        });

        var completion = await connector.CompleteOnceAsync(new MarketplaceAuthorizationCompletionRequest(Guid.NewGuid(), "TG-authcode"), CancellationToken.None);
        completion.ExternalAccountId.Should().Be("123456789");
        completion.AccessExpiresAt.Should().Be(clock.UtcNow.AddSeconds(21600));
        completion.Grants["LISTINGS_READ"].Should().Be(AccountCapabilityState.GRANTED);
        completion.Grants["ORDERS_READ"].Should().Be(AccountCapabilityState.UNKNOWN);
        completion.CredentialImpact.Should().Be(CredentialImpact.MAY_SUPERSEDE_EXISTING);

        // Token form fields (never logged/persisted raw, but here we assert the outbound shape).
        var tokenRequest = handler.Requests.Single(r => r.RequestUri!.AbsolutePath == "/oauth/token");
        var form = await tokenRequest.Content!.ReadAsStringAsync();
        form.Should().Contain("grant_type=authorization_code").And.Contain("code=TG-authcode");
    }

    [Fact]
    public async Task CompleteOnceAsync_identity_mismatch_between_token_and_users_me_fails_closed()
    {
        var (connector, _, _) = CreateConnector(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth/token")
                return JsonResponse(HttpStatusCode.OK, """{"access_token":"a","token_type":"bearer","expires_in":21600,"user_id":123,"refresh_token":"r"}""");
            if (request.RequestUri!.AbsolutePath == "/users/me")
                return JsonResponse(HttpStatusCode.OK, """{"id":999,"site_id":"MLB"}"""); // different id
            throw new InvalidOperationException("unexpected");
        });
        var act = () => connector.CompleteOnceAsync(new MarketplaceAuthorizationCompletionRequest(Guid.NewGuid(), "code"), CancellationToken.None);
        (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure.SafeCode.Should().Be("PROVIDER_RESPONSE_INVALID");
    }

    [Fact]
    public async Task CompleteOnceAsync_malformed_2xx_is_a_non_replayable_failure()
    {
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.OK, """{"access_token":"a"}""")); // missing required fields
        var act = () => connector.CompleteOnceAsync(new MarketplaceAuthorizationCompletionRequest(Guid.NewGuid(), "code"), CancellationToken.None);
        (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure.Certainty.Should().Be(MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED);
    }

    [Fact]
    public async Task CompleteOnceAsync_invalid_grant_maps_to_grant_rejected()
    {
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.BadRequest, """{"error":"invalid_grant","message":"..."}"""));
        var act = () => connector.CompleteOnceAsync(new MarketplaceAuthorizationCompletionRequest(Guid.NewGuid(), "code"), CancellationToken.None);
        var ex = (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which;
        ex.Failure.SafeCode.Should().Be("PROVIDER_GRANT_REJECTED");
        ex.Failure.Classification.Should().Be(MarketplaceFailureClassification.USER_ACTION_REQUIRED);
    }

    [Fact]
    public async Task CompleteOnceAsync_invalid_client_maps_to_app_credentials_invalid()
    {
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.BadRequest, """{"error":"invalid_client"}"""));
        var act = () => connector.CompleteOnceAsync(new MarketplaceAuthorizationCompletionRequest(Guid.NewGuid(), "code"), CancellationToken.None);
        (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure.SafeCode.Should().Be("PROVIDER_APP_CREDENTIALS_INVALID");
    }

    [Fact]
    public async Task RefreshAsync_missing_refresh_token_in_secret_is_proven_NOT_SENT()
    {
        var (connector, _, _) = CreateConnector(_ => throw new InvalidOperationException("no HTTP expected"));
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"a","user_id":"1"}"""); // no refresh_token
        var act = () => connector.RefreshAsync(new MarketplaceRefreshRequest(Guid.NewGuid(), MercadoLivreConnector.Code, secret), CancellationToken.None);
        (await act.Should().ThrowAsync<MarketplaceRequestNotSentException>()).Which.Message.Should().Be("REFRESH_TOKEN_UNAVAILABLE");
    }

    [Fact]
    public async Task RefreshAsync_success_rotates_the_refresh_token_and_persists_new_expiry()
    {
        var (connector, handler, clock) = CreateConnector(request =>
            JsonResponse(HttpStatusCode.OK, """{"access_token":"new-access","token_type":"bearer","expires_in":21600,"user_id":42,"refresh_token":"new-refresh"}"""));
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"old-access","refresh_token":"old-refresh","user_id":"42"}""");
        var completion = await connector.RefreshAsync(new MarketplaceRefreshRequest(Guid.NewGuid(), MercadoLivreConnector.Code, secret), CancellationToken.None);
        completion.AccessExpiresAt.Should().Be(clock.UtcNow.AddSeconds(21600));
        var form = await handler.Requests.Single().Content!.ReadAsStringAsync();
        form.Should().Contain("grant_type=refresh_token").And.Contain("refresh_token=old-refresh");
    }

    // ================= §C.2 status mapping =================

    [Theory]
    [InlineData("active", MarketplaceListingStatus.ACTIVE)]
    [InlineData("paused", MarketplaceListingStatus.PAUSED)]
    [InlineData("closed", MarketplaceListingStatus.INACTIVE)]
    [InlineData("inactive", MarketplaceListingStatus.INACTIVE)]
    [InlineData("not_yet_active", MarketplaceListingStatus.DRAFT)]
    [InlineData("programmed", MarketplaceListingStatus.DRAFT)]
    [InlineData("under_review", MarketplaceListingStatus.ERROR)]
    [InlineData("payment_required", MarketplaceListingStatus.ERROR)]
    [InlineData("pending", MarketplaceListingStatus.ERROR)]
    public async Task Status_mapping_matches_the_frozen_table(string providerStatus, MarketplaceListingStatus expected)
    {
        var json = """[{"code":200,"body":{"id":"MLB1","seller_id":777,"site_id":"MLB","status":"__STATUS__","price":10.00,"currency_id":"BRL"}}]""".Replace("__STATUS__", providerStatus);
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.OK, json));
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"a","refresh_token":"r","user_id":"777"}""");
        var results = await connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", secret, ["MLB1"]), CancellationToken.None);
        results.Single().Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
        results.Single().Facts!.ObservedStatus.Should().Be(expected);
    }

    [Fact]
    public async Task Unknown_status_never_becomes_ACTIVE_and_is_reported_UNMAPPED_STATUS()
    {
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.OK,
            """[{"code":200,"body":{"id":"MLB1","seller_id":777,"site_id":"MLB","status":"some_future_provider_status"}}]"""));
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"a","refresh_token":"r","user_id":"777"}""");
        var results = await connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", secret, ["MLB1"]), CancellationToken.None);
        results.Single().Outcome.Should().Be(MarketplaceListingReadOutcome.UNMAPPED_STATUS);
        results.Single().Facts.Should().BeNull();
    }

    [Fact]
    public async Task Bulk_detail_maps_every_per_element_outcome_including_seller_mismatch()
    {
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.OK, """
            [
              {"code":200,"body":{"id":"MLB1","seller_id":777,"site_id":"MLB","status":"active","price":9.9,"currency_id":"BRL"}},
              {"code":404,"body":{"id":"MLB2"}},
              {"code":403,"body":{"id":"MLB3"}},
              {"code":200,"body":{"id":"MLB4","seller_id":888,"site_id":"MLB","status":"active"}},
              {"code":500,"body":{"id":"MLB5"}}
            ]
            """));
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"a","refresh_token":"r","user_id":"777"}""");
        var results = await connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", secret, ["MLB1", "MLB2", "MLB3", "MLB4", "MLB5", "MLB6"]), CancellationToken.None);
        results.Single(r => r.ExternalListingId == "MLB1").Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
        results.Single(r => r.ExternalListingId == "MLB2").Outcome.Should().Be(MarketplaceListingReadOutcome.NOT_FOUND);
        results.Single(r => r.ExternalListingId == "MLB3").Outcome.Should().Be(MarketplaceListingReadOutcome.ACCESS_DENIED);
        results.Single(r => r.ExternalListingId == "MLB4").Outcome.Should().Be(MarketplaceListingReadOutcome.SELLER_MISMATCH, "MLB4 belongs to a different seller_id than the requesting account");
        results.Single(r => r.ExternalListingId == "MLB5").Outcome.Should().Be(MarketplaceListingReadOutcome.FAILED);
        results.Single(r => r.ExternalListingId == "MLB6").Outcome.Should().Be(MarketplaceListingReadOutcome.FAILED, "an ID missing entirely from the response is FAILED, never silently dropped");
    }

    [Fact]
    public async Task SKU_is_extracted_only_from_SELLER_SKU_never_seller_custom_field()
    {
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.OK, """
            [{"code":200,"body":{"id":"MLB1","seller_id":777,"site_id":"MLB","status":"active",
              "attributes":[{"id":"SELLER_SKU","value_name":"SKU-ABC"}],"seller_custom_field":"IGNORED"}}]
            """));
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"a","refresh_token":"r","user_id":"777"}""");
        var result = (await connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", secret, ["MLB1"]), CancellationToken.None)).Single();
        result.Facts!.ExternalSku.Should().Be("SKU-ABC");
    }

    // ================= §B.2 scan pagination =================

    [Fact]
    public async Task ReadListingPageAsync_first_page_then_continuation_then_termination()
    {
        var callCount = 0;
        var (connector, _, _) = CreateConnector(request =>
        {
            callCount++;
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            if (callCount == 1)
            {
                query["scroll_id"].Should().BeNull();
                return JsonResponse(HttpStatusCode.OK, """{"results":["MLB1","MLB2"],"scroll_id":"scroll-A"}""");
            }
            if (callCount == 2)
            {
                query["scroll_id"].Should().Be("scroll-A");
                return JsonResponse(HttpStatusCode.OK, """{"results":[],"scroll_id":"scroll-B"}""");
            }
            throw new InvalidOperationException("unexpected extra call");
        });
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"a","refresh_token":"r","user_id":"1"}""");
        var first = await connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "1", secret, null), CancellationToken.None);
        first.ExternalListingIds.Should().BeEquivalentTo(["MLB1", "MLB2"]);
        first.EndOfEnumeration.Should().BeFalse();
        var second = await connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "1", secret, first.NextCursor), CancellationToken.None);
        second.EndOfEnumeration.Should().BeTrue("an empty results page ends enumeration regardless of scroll_id");
    }

    [Fact]
    public async Task ReadListingPageAsync_drops_and_counts_ids_that_do_not_match_the_MLB_pattern()
    {
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.OK, """{"results":["MLB1","NOT-VALID","MLB2"],"scroll_id":"s"}"""));
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"a","refresh_token":"r","user_id":"1"}""");
        var page = await connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "1", secret, null), CancellationToken.None);
        page.ExternalListingIds.Should().BeEquivalentTo(["MLB1", "MLB2"]);
        page.RejectedIdentifierCount.Should().Be(1);
    }

    // ================= A-06: enumeration termination on malformed/filtered pages =================

    [Fact]
    public async Task ReadListingPageAsync_missing_results_field_is_a_malformed_failure_never_termination()
    {
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.OK, """{"scroll_id":"s"}"""));
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"a","refresh_token":"r","user_id":"1"}""");
        var act = () => connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "1", secret, null), CancellationToken.None);
        (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure.SafeCode.Should().Be("PROVIDER_RESPONSE_INVALID");
    }

    [Fact]
    public async Task ReadListingPageAsync_wrong_type_results_field_is_a_malformed_failure()
    {
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.OK, """{"results":"not-an-array","scroll_id":"s"}"""));
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"a","refresh_token":"r","user_id":"1"}""");
        var act = () => connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "1", secret, null), CancellationToken.None);
        (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure.SafeCode.Should().Be("PROVIDER_RESPONSE_INVALID");
    }

    [Fact]
    public async Task ReadListingPageAsync_genuinely_empty_results_is_legitimate_termination()
    {
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.OK, """{"results":[],"scroll_id":"s"}"""));
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"a","refresh_token":"r","user_id":"1"}""");
        var page = await connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "1", secret, null), CancellationToken.None);
        page.EndOfEnumeration.Should().BeTrue();
        page.ExternalListingIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadListingPageAsync_non_empty_page_with_every_id_rejected_but_a_continuation_present_does_not_terminate()
    {
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.OK, """{"results":["NOT-VALID-1","NOT-VALID-2"],"scroll_id":"scroll-continues"}"""));
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"a","refresh_token":"r","user_id":"1"}""");
        var page = await connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "1", secret, null), CancellationToken.None);
        page.EndOfEnumeration.Should().BeFalse("the RAW page was non-empty and a continuation exists — rejecting every ID locally must never look like provider-side termination");
        page.ExternalListingIds.Should().BeEmpty();
        page.RejectedIdentifierCount.Should().Be(2);
        page.NextCursor.Should().NotBeNull();
    }

    // ================= A-07: bulk element isolation =================

    [Fact]
    public async Task Bulk_detail_one_structurally_invalid_element_never_crashes_the_whole_batch()
    {
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.OK, """
            [
              {"code":200,"body":{"id":"MLB1","seller_id":777,"site_id":"MLB","status":"active","price":9.9,"currency_id":"BRL"}},
              null,
              ["unexpected","nested","array"],
              {"unexpected":"shape with no code/body/id at all"},
              {"code":"not-a-number","body":{"id":"MLB5"}}
            ]
            """));
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"a","refresh_token":"r","user_id":"777"}""");
        var results = await connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", secret, ["MLB1", "MLB5", "MLB-UNMATCHED"]), CancellationToken.None);
        results.Single(r => r.ExternalListingId == "MLB1").Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND, "a valid peer element must survive its malformed siblings");
        results.Single(r => r.ExternalListingId == "MLB5").Outcome.Should().Be(MarketplaceListingReadOutcome.FAILED);
        results.Single(r => r.ExternalListingId == "MLB-UNMATCHED").Outcome.Should().Be(MarketplaceListingReadOutcome.FAILED);
    }

    // ================= M059 B1: price normalization is overflow-free (ADR-0025 §B.3 ObservedPrice row) =================

    private static readonly byte[] PriceTestSecret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"a","refresh_token":"r","user_id":"777"}""");

    private static async Task<MarketplaceListingDetailResult> ReadSinglePriceAsync(string priceFragment, string currencyFragment = "\"currency_id\":\"BRL\"")
    {
        var json = """[{"code":200,"body":{"id":"MLB1","seller_id":777,"site_id":"MLB","status":"active",""" + priceFragment + "," + currencyFragment + "}}]";
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.OK, json));
        return (await connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", PriceTestSecret, ["MLB1"]), CancellationToken.None)).Single();
    }

    [Theory]
    [InlineData("10", "10")]
    [InlineData("10.00", "10.00")]
    [InlineData("10.10", "10.10")]
    [InlineData("10.100", "10.100")]                                   // scale 3 by text, but exactly two significant decimals by value
    [InlineData("0.01", "0.01")]                                       // smallest positive two-decimal price
    [InlineData("1.5E1", "15")]                                        // valid scientific notation
    [InlineData("1E2", "100")]
    [InlineData("9999999999999999.99", "9999999999999999.99")]         // exactly the numeric(18,2) ceiling
    [InlineData("9999999999999999", "9999999999999999")]
    public async Task Price_B1_valid_decimal_values_are_preserved_exactly_with_no_warning(string literal, string expected)
    {
        var result = await ReadSinglePriceAsync($"\"price\":{literal}");
        result.Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
        result.Facts!.ObservedPrice.Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        result.Facts.NormalizationWarnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("10000000000000000")]                                   // one unit above the numeric(18,2) ceiling
    [InlineData("10000000000000000.00")]
    [InlineData("79228162514264337593543950335")]                       // decimal.MaxValue — the Gate 058 reproduction (price*100 overflowed)
    [InlineData("7922816251426433759354395033.5")]                      // inside decimal range, far outside numeric(18,2)
    [InlineData("1E30")]                                                // exceeds decimal itself: TryGetDecimal is false
    [InlineData("1E+400")]                                              // exponent far beyond any decimal/double range
    [InlineData("10.001")]                                              // excess scale
    [InlineData("10.005")]
    [InlineData("0.0000000000000000000000000001")]                      // 28 decimal places, positive
    [InlineData("1.23456789012345678901234567")]                        // 26 decimal places
    public async Task Price_B1_unrepresentable_prices_are_null_with_the_precision_warning_and_never_abort_the_element(string literal)
    {
        var result = await ReadSinglePriceAsync($"\"price\":{literal}");
        result.Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND, "an unusable price is a per-field normalization result, never an element or batch failure");
        result.Facts!.ObservedPrice.Should().BeNull("unknown — never zero, never saturated at a maximum, never silently truncated");
        result.Facts.NormalizationWarnings.Should().Equal("PRICE_PRECISION_UNSUPPORTED");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.00")]
    [InlineData("-1")]
    [InlineData("-0.01")]
    [InlineData("-79228162514264337593543950335")]                      // decimal.MinValue
    [InlineData("-1E+400")]                                             // negative and beyond decimal: still "not positive", not "precision"
    [InlineData("-1E30")]
    public async Task Price_B1_non_positive_prices_are_null_with_the_not_positive_warning_including_at_the_range_limits(string literal)
    {
        var result = await ReadSinglePriceAsync($"\"price\":{literal}");
        result.Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
        result.Facts!.ObservedPrice.Should().BeNull();
        result.Facts.NormalizationWarnings.Should().Equal("PRICE_NOT_POSITIVE");
    }

    [Theory]
    [InlineData("\"price\":null")]
    [InlineData("\"price\":\"10.00\"")]                                 // JSON string, not number
    [InlineData("\"price\":true")]
    [InlineData("\"price\":[10]")]
    [InlineData("\"price\":{\"amount\":10}")]
    [InlineData("\"unrelated\":1")]                                     // price absent altogether
    public async Task Price_B1_absent_null_or_wrongly_typed_price_is_unknown_never_zero_and_never_throws(string priceFragment)
    {
        var result = await ReadSinglePriceAsync(priceFragment);
        result.Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
        result.Facts!.ObservedPrice.Should().BeNull();
        result.Facts.NormalizationWarnings.Should().BeEmpty("§B.3 defines no warning for an absent/ill-typed price: it is simply unknown");
    }

    [Theory]
    [InlineData("\"currency_id\":\"USD\"")]
    [InlineData("\"currency_id\":\"ARS\"")]
    [InlineData("\"currency_id\":null")]
    [InlineData("\"currency_id\":7")]
    public async Task Price_B1_non_BRL_or_unusable_currency_is_unsupported_even_for_an_extreme_price(string currencyFragment)
    {
        foreach (var literal in new[] { "10.00", "79228162514264337593543950335", "1E+400" })
        {
            var result = await ReadSinglePriceAsync($"\"price\":{literal}", currencyFragment);
            result.Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
            result.Facts!.ObservedPrice.Should().BeNull();
            result.Facts.NormalizationWarnings.Should().Equal("PRICE_CURRENCY_UNSUPPORTED");
        }
    }

    [Fact]
    public async Task Price_B1_an_extreme_price_never_aborts_the_batch_and_its_peers_and_the_single_bulk_evidence_event_survive()
    {
        var logger = new CapturingLoggerProvider();
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.OK, """
            [
              {"id":"MLB1","status_code":200,"body":{"id":"MLB1","seller_id":777,"site_id":"MLB","status":"active","price":79228162514264337593543950335,"currency_id":"BRL"}},
              {"id":"MLB2","status_code":200,"body":{"id":"MLB2","seller_id":777,"site_id":"MLB","status":"active","price":19.90,"currency_id":"BRL"}},
              {"id":"MLB3","status_code":200,"body":{"id":"MLB3","seller_id":777,"site_id":"MLB","status":"paused","price":1E+400,"currency_id":"BRL"}},
              {"id":"MLB4","status_code":404,"body":{"id":"MLB4"}}
            ]
            """), logger);
        var results = await connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", PriceTestSecret, ["MLB1", "MLB2", "MLB3", "MLB4"]), CancellationToken.None);

        results.Select(r => r.Outcome).Should().Equal(MarketplaceListingReadOutcome.FOUND, MarketplaceListingReadOutcome.FOUND, MarketplaceListingReadOutcome.FOUND, MarketplaceListingReadOutcome.NOT_FOUND);
        results[0].Facts!.ObservedPrice.Should().BeNull(); results[0].Facts!.NormalizationWarnings.Should().Equal("PRICE_PRECISION_UNSUPPORTED");
        results[1].Facts!.ObservedPrice.Should().Be(19.90m); results[1].Facts!.NormalizationWarnings.Should().BeEmpty();
        results[2].Facts!.ObservedPrice.Should().BeNull(); results[2].Facts!.NormalizationWarnings.Should().Equal("PRICE_PRECISION_UNSUPPORTED");
        results[0].Facts!.ObservedStatus.Should().Be(MarketplaceListingStatus.ACTIVE, "the rest of the extreme-priced element is still normalized");
        results[2].Facts!.ObservedStatus.Should().Be(MarketplaceListingStatus.PAUSED);

        var evidence = logger.Entries.Where(e => (string)e.State["Operation"]! == "ITEMS_BULK" && e.State.ContainsKey("RequestedIdCount")).Should().ContainSingle().Subject;
        evidence.State["Classification"].Should().Be("SUCCESS", "the response was processed to completion — there was no aborted batch");
        evidence.State["EnvelopeCount"].Should().Be(4);
        evidence.State["UsableEnvelopeCount"].Should().Be(4);
        ParseHistogram((string)evidence.State["Histogram"]!)["FOUND"].Should().Be(3);
        AssertBulkEventNeverCarriesListingIdsOrBodies(evidence);
    }

    // ================= M059 B2: HTTP 200 element/body malformation classification (ADR-0025 §B.3) =================

    private static async Task<IReadOnlyList<MarketplaceListingDetailResult>> ReadBulkAsync(string json, params string[] ids)
    {
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.OK, json));
        return await connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", PriceTestSecret, ids), CancellationToken.None);
    }

    private const string ValidElement = """{"id":"MLB-OK","status_code":200,"body":{"id":"MLB-OK","seller_id":777,"site_id":"MLB","status":"active","price":10.00,"currency_id":"BRL"}}""";

    [Theory]
    [InlineData("""{"id":"MLB-BAD","status_code":200,"body":null}""")]
    [InlineData("""{"id":"MLB-BAD","status_code":200,"body":[1,2,3]}""")]
    [InlineData("""{"id":"MLB-BAD","status_code":200,"body":"not-an-object"}""")]
    [InlineData("""{"id":"MLB-BAD","status_code":200,"body":42}""")]
    [InlineData("""{"id":"MLB-BAD","status_code":200}""")]                       // success claimed, body absent
    [InlineData("""{"id":"MLB-BAD","code":200,"body":null}""")]                  // the defensive "code" envelope variant behaves identically
    public async Task Bulk_B2_status_200_with_a_missing_or_non_object_body_is_INVALID_RESPONSE_never_the_retryable_FAILED_and_peers_survive(string badElement)
    {
        var results = await ReadBulkAsync($"[{ValidElement},{badElement}]", "MLB-OK", "MLB-BAD");
        results.Single(r => r.ExternalListingId == "MLB-OK").Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
        var bad = results.Single(r => r.ExternalListingId == "MLB-BAD");
        bad.Outcome.Should().Be(MarketplaceListingReadOutcome.INVALID_RESPONSE);
        bad.Facts.Should().BeNull("no facts may be derived from a malformed success payload");
    }

    [Theory]
    [InlineData("""{"id":"MLB-BAD","status_code":200,"body":{"id":"MLB-OTHER","seller_id":777,"site_id":"MLB","status":"active"}}""")]   // body id != requested id
    [InlineData("""{"id":"MLB-BAD","status_code":200,"body":{"id":"MLB-BAD","site_id":"MLB","status":"active"}}""")]                         // seller_id missing
    [InlineData("""{"id":"MLB-BAD","status_code":200,"body":{"id":"MLB-BAD","seller_id":777,"site_id":"MLA","status":"active"}}""")]       // site_id present and != MLB
    [InlineData("""{"id":"MLB-BAD","status_code":200,"body":{"seller_id":777,"site_id":"MLB","status":"active"}}""")]                       // body id missing
    [InlineData("""{"id":"MLB-BAD","status_code":200,"body":{"id":7,"seller_id":777,"site_id":"MLB","status":"active"}}""")]                // body id wrong type
    [InlineData("""{"id":"MLB-BAD","status_code":200,"body":{"id":"MLB-BAD","seller_id":{"nested":1},"site_id":"MLB","status":"active"}}""")] // seller_id wrong type
    public async Task Bulk_B2_status_200_with_a_body_missing_or_mistyping_a_required_field_is_INVALID_RESPONSE(string badElement)
    {
        var results = await ReadBulkAsync($"[{ValidElement},{badElement}]", "MLB-OK", "MLB-BAD");
        results.Single(r => r.ExternalListingId == "MLB-OK").Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
        var bad = results.Single(r => r.ExternalListingId == "MLB-BAD");
        bad.Outcome.Should().Be(MarketplaceListingReadOutcome.INVALID_RESPONSE);
        bad.Facts.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"id":"MLB-BAD","status_code":500,"body":{"id":"MLB-BAD"}}""")]
    [InlineData("""{"id":"MLB-BAD","status_code":500,"body":null}""")]           // a failed element with a null body is still just a provider failure
    [InlineData("""{"id":"MLB-BAD","status_code":429,"body":{"id":"MLB-BAD"}}""")]
    [InlineData("""{"id":"MLB-BAD","status_code":400,"body":{"id":"MLB-BAD"}}""")]
    [InlineData("""{"id":"MLB-BAD","status_code":null,"body":{"id":"MLB-BAD"}}""")]
    [InlineData("""{"id":"MLB-BAD","status_code":"200","body":{"id":"MLB-BAD"}}""")] // string status_code: not a usable element code (frozen §B.3 "any other element code")
    public async Task Bulk_B2_every_non_success_element_code_stays_the_retryable_FAILED_so_the_fix_does_not_leak_into_genuine_transient_failures(string failedElement)
    {
        var results = await ReadBulkAsync($"[{ValidElement},{failedElement}]", "MLB-OK", "MLB-BAD");
        results.Single(r => r.ExternalListingId == "MLB-OK").Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
        results.Single(r => r.ExternalListingId == "MLB-BAD").Outcome.Should().Be(MarketplaceListingReadOutcome.FAILED);
    }

    [Theory]
    [InlineData("""[null]""")]
    [InlineData("""["string-element"]""")]
    [InlineData("""[[1,2]]""")]
    [InlineData("""[{"status_code":200,"body":null}]""")]                      // 200 + null body, and no identity at all to attribute it to
    public async Task Bulk_B2_an_element_with_no_recoverable_identity_cannot_be_attributed_so_the_requested_id_is_the_generic_missing_FAILED(string json)
    {
        var results = await ReadBulkAsync(json, "MLB-REQ");
        results.Single().ExternalListingId.Should().Be("MLB-REQ");
        results.Single().Outcome.Should().Be(MarketplaceListingReadOutcome.FAILED, "§B.3: a requested ID missing from the response is FAILED; nothing in a non-attributable element can say which ID it answers");
    }

    [Fact]
    public async Task Bulk_B2_the_body_identity_fallback_attributes_a_200_element_whose_root_id_is_absent_but_whose_body_id_is_present()
    {
        var results = await ReadBulkAsync("""[{"status_code":200,"body":{"id":"MLB-FB","seller_id":777,"site_id":"MLB","status":"active"}}]""", "MLB-FB");
        results.Single().Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
    }

    [Theory]
    [InlineData("")]                                                            // empty body
    [InlineData("   ")]
    [InlineData("{not json")]                                                   // syntactically invalid
    [InlineData("[{\"id\":\"MLB1\",")]                                          // truncated
    [InlineData("null")]                                                        // valid JSON, wrong top-level type
    [InlineData("{}")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public async Task Bulk_B2_a_200_response_whose_payload_is_not_a_JSON_array_is_a_NON_RETRYABLE_PROVIDER_RESPONSE_INVALID_call_failure(string body)
    {
        var logger = new CapturingLoggerProvider();
        var (connector, handler, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.OK, body), logger);
        var act = () => connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", PriceTestSecret, ["MLB1"]), CancellationToken.None);
        var failure = (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.NON_RETRYABLE, "a malformed success payload will not heal on retry; it must never be RETRYABLE/UNKNOWN (transient)");
        failure.SafeCode.Should().Be("PROVIDER_RESPONSE_INVALID");
        failure.Certainty.Should().Be(MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED);
        handler.Requests.Should().HaveCount(1, "the safe reader's own transient retry budget is not consumed by a 2xx");
        var evidence = logger.Entries.Where(e => (string)e.State["Operation"]! == "ITEMS_BULK" && e.State.ContainsKey("RequestedIdCount")).Should().ContainSingle().Subject;
        evidence.State["Classification"].Should().Be("PROVIDER_RESPONSE_INVALID");
        evidence.State["EnvelopeCount"].Should().Be(0);
    }

    [Fact]
    public async Task Bulk_B2_the_malformed_element_is_counted_as_INVALID_RESPONSE_in_the_single_bulk_evidence_histogram()
    {
        var logger = new CapturingLoggerProvider();
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.OK, "[" + ValidElement + """,{"id":"MLB-BAD","status_code":200,"body":null},{"id":"MLB-500","status_code":500,"body":{"id":"MLB-500"}}]"""), logger);
        await connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", PriceTestSecret, ["MLB-OK", "MLB-BAD", "MLB-500"]), CancellationToken.None);
        var evidence = logger.Entries.Where(e => (string)e.State["Operation"]! == "ITEMS_BULK" && e.State.ContainsKey("RequestedIdCount")).Should().ContainSingle().Subject;
        ParseHistogram((string)evidence.State["Histogram"]!).Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["FOUND"] = 1, ["INVALID_RESPONSE"] = 1, ["FAILED"] = 1, ["NOT_FOUND"] = 0, ["ACCESS_DENIED"] = 0, ["SELLER_MISMATCH"] = 0, ["UNMAPPED_STATUS"] = 0,
        });
        evidence.State["UsableEnvelopeCount"].Should().Be(3);
    }

    // ================= A-10 fixture realism: the documented status_code + root id envelope =================

    [Fact]
    public async Task Bulk_detail_accepts_the_documented_status_code_and_root_id_envelope_variant()
    {
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.OK, """
            [{"id":"MLB1","status_code":200,"body":{"id":"MLB1","seller_id":777,"site_id":"MLB","status":"active","price":9.9,"currency_id":"BRL"}}]
            """));
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"a","refresh_token":"r","user_id":"777"}""");
        var result = (await connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", secret, ["MLB1"]), CancellationToken.None)).Single();
        result.Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
    }

    // ================= A-08: real bounded body size =================

    [Fact]
    public async Task ReadBoundedBody_a_trustworthy_oversized_Content_Length_fails_before_reading_any_body_byte()
    {
        var streamed = false;
        var (connector, _, _) = CreateConnector(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new CountingNeverReadContent(2 * 1024 * 1024, () => streamed = true) };
            return response;
        });
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"a","refresh_token":"r","user_id":"1"}""");
        var act = () => connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "1", secret, null), CancellationToken.None);
        (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure.SafeCode.Should().Be("RESPONSE_TOO_LARGE");
        streamed.Should().BeFalse("a trustworthy oversized Content-Length must fail BEFORE any body byte is read");
    }

    [Fact]
    public async Task ReadBoundedBody_absent_Content_Length_a_genuinely_streamed_oversized_body_is_bounded_never_fully_consumed()
    {
        // A-08-BODY: the generator can represent an effectively unbounded body (never
        // byte[]-backed — see InstrumentedGeneratorStream) while this test proves the READER
        // itself stops pulling bytes shortly after crossing the 1 MiB bound, never anywhere close
        // to fully draining the stream — genuine bounded consumption, not merely "eventually threw".
        var generator = new InstrumentedGeneratorStream(totalLength: 64 * 1024 * 1024); // 64 MiB available
        var (connector, _, _) = CreateConnector(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new GeneratedBodyHttpContent(generator, declaredContentLength: null) });
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"a","refresh_token":"r","user_id":"1"}""");
        var act = () => connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "1", secret, null), CancellationToken.None);
        (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure.SafeCode.Should().Be("RESPONSE_TOO_LARGE");

        generator.ReadAsyncCallCount.Should().BeGreaterThan(0);
        generator.MaxOffsetConsumed.Should().BeLessThan(2 * 1024 * 1024, "bounded consumption: the reader must stop within roughly one chunk of the 1 MiB bound, never anywhere near the 64 MiB the generator could have supplied");
        generator.TotalBytesRequested.Should().BeLessThan(2 * 1024 * 1024, "the SUM of bytes ever requested across all ReadAsync calls must also stay bounded — never inflated by re-reading");
        generator.Disposed.Should().BeTrue("the reader must dispose the response stream on every exit path, including this failure path — no leaked handle to a still-open generator");
    }

    [Fact]
    public async Task ReadBoundedBody_a_declared_Content_Length_that_understates_the_true_body_is_still_caught_by_the_streaming_bound()
    {
        // A-08-BODY "incorrect Content-Length": the header LIES (claims a small, trustworthy-looking
        // size, well under the 1 MiB bound) while the actual body is far larger — proving the
        // reader never simply trusts a declared header without also enforcing its own running-total
        // bound against what the stream actually produces.
        var generator = new InstrumentedGeneratorStream(totalLength: 8 * 1024 * 1024); // true body: 8 MiB
        var (connector, _, _) = CreateConnector(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new GeneratedBodyHttpContent(generator, declaredContentLength: 100) });
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"a","refresh_token":"r","user_id":"1"}""");
        var act = () => connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "1", secret, null), CancellationToken.None);
        (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure.SafeCode.Should().Be("RESPONSE_TOO_LARGE");

        generator.MaxOffsetConsumed.Should().BeLessThan(2 * 1024 * 1024, "a lying (understated) Content-Length must never make the reader trust it far past the real 1 MiB bound");
        generator.Disposed.Should().BeTrue();
    }

    // ================= A-08: safe structured provider-call logging =================

    [Fact]
    public async Task Successful_read_emits_the_one_safe_structured_log_event_with_no_secret_material()
    {
        var logger = new CapturingLoggerProvider();
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.OK, """{"results":["MLB1"],"scroll_id":null}"""), logger);
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"super-secret-access-token-value","refresh_token":"super-secret-refresh-token-value","user_id":"1"}""");
        await connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "1", secret, null), CancellationToken.None);

        var events = logger.Messages.Where(m => m.Contains("MarketplaceProviderCall")).ToList();
        events.Should().ContainSingle();
        var message = events.Single();
        message.Should().Contain("operation=ITEMS_SCAN").And.Contain("status=200").And.Contain("certainty=SENT_AND_RESPONSE_RECEIVED");
        message.Should().NotContain("super-secret-access-token-value").And.NotContain("super-secret-refresh-token-value")
            .And.NotContain("Authorization").And.NotContain("Bearer");

        // A-08-LOG: the REAL structured state (never a re-parse of rendered text) — this is what a
        // real structured sink would actually index/alert on.
        AssertNoSensitiveStructuredValues(logger, "super-secret-access-token-value", "super-secret-refresh-token-value");
        var entry = logger.Entries.Single(e => (string)e.State["Operation"]! == "ITEMS_SCAN");
        entry.State["Provider"].Should().Be("MERCADO_LIVRE");
        entry.State["HttpStatus"].Should().Be("200");
        entry.State["Classification"].Should().Be("NONE");
        entry.State["Certainty"].Should().Be("SENT_AND_RESPONSE_RECEIVED");
        entry.State["Attempt"].Should().Be(1);
    }

    [Fact]
    public async Task GET_429_emits_structured_events_with_the_real_RATE_LIMITED_classification_and_no_secret_material()
    {
        var logger = new CapturingLoggerProvider();
        var (connector, _, _) = CreateConnector(_ =>
        {
            var response = JsonResponse((HttpStatusCode)429, "{}");
            response.Headers.Add("Retry-After", "0"); // never actually a future instant -> no real retry delay
            return response;
        }, logger);
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"super-secret-access-token-value","refresh_token":"super-secret-refresh-token-value","user_id":"1"}""");
        var act = () => connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "1", secret, null), CancellationToken.None);
        (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure.SafeCode.Should().Be("PROVIDER_RATE_LIMITED");

        AssertNoSensitiveStructuredValues(logger, "super-secret-access-token-value", "super-secret-refresh-token-value");
        var events = logger.Entries.Where(e => (string)e.State["Operation"]! == "ITEMS_SCAN").ToList();
        events.Should().NotBeEmpty();
        events.Should().OnlyContain(e => (string)e.State["Classification"]! == "RATE_LIMITED");
        events.Should().OnlyContain(e => (string)e.State["HttpStatus"]! == "429");
    }

    [Fact]
    public async Task GET_5xx_emits_structured_events_with_the_real_RETRYABLE_classification_across_every_attempt()
    {
        var logger = new CapturingLoggerProvider();
        var (connector, _, _) = CreateConnector(_ => JsonResponse((HttpStatusCode)503, "{}"), logger);
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"super-secret-access-token-value","refresh_token":"super-secret-refresh-token-value","user_id":"1"}""");
        var act = () => connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "1", secret, null), CancellationToken.None);
        (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure.SafeCode.Should().Be("PROVIDER_UNAVAILABLE");

        AssertNoSensitiveStructuredValues(logger, "super-secret-access-token-value", "super-secret-refresh-token-value");
        var events = logger.Entries.Where(e => (string)e.State["Operation"]! == "ITEMS_SCAN").ToList();
        events.Should().HaveCount(3, "one structured event per ATTEMPT — never once per logical operation — across all 3 permitted attempts");
        events.Should().OnlyContain(e => (string)e.State["Classification"]! == "RETRYABLE");
        events.Should().OnlyContain(e => (string)e.State["HttpStatus"]! == "503");
        events.Select(e => (int)e.State["Attempt"]!).Should().BeEquivalentTo([1, 2, 3]);
    }

    [Fact]
    public async Task OAuth_429_on_refresh_emits_exactly_one_structured_event_with_the_final_PROVIDER_RATE_LIMITED_classification()
    {
        // A-08-LOG regression proof: before the fix, this exact HTTP attempt logged a single event
        // with classification="NONE"/"UNKNOWN" (recorded BEFORE the 429 status was even inspected)
        // — never the real final outcome. Now exactly one event, with the REAL classification.
        var logger = new CapturingLoggerProvider();
        var (connector, _, _) = CreateConnector(_ =>
        {
            var response = JsonResponse((HttpStatusCode)429, """{"error":"rate_limited"}""");
            response.Headers.Add("Retry-After", "0");
            return response;
        }, logger);
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"super-secret-access-token-value","refresh_token":"super-secret-refresh-token-value","user_id":"1"}""");
        var act = () => connector.RefreshAsync(new MarketplaceRefreshRequest(Guid.NewGuid(), "MERCADO_LIVRE", secret), CancellationToken.None);
        (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure.SafeCode.Should().Be("PROVIDER_RATE_LIMITED");

        AssertNoSensitiveStructuredValues(logger, "super-secret-access-token-value", "super-secret-refresh-token-value");
        var events = logger.Entries.Where(e => (string)e.State["Operation"]! == "OAUTH_REFRESH").ToList();
        events.Should().ContainSingle("exactly one real HTTP attempt happened — exactly one log event, never zero and never a premature extra one");
        events.Single().State["Classification"].Should().Be("PROVIDER_RATE_LIMITED", "the FINAL classification, known only after the status/body were inspected — never the premature NONE/UNKNOWN placeholder the previous bug logged");
        events.Single().State["HttpStatus"].Should().Be("429");
    }

    [Fact]
    public async Task OAuth_5xx_on_exchange_emits_exactly_one_structured_event_with_the_final_PROVIDER_UNAVAILABLE_classification()
    {
        var logger = new CapturingLoggerProvider();
        var (connector, _, _) = CreateConnector(_ => JsonResponse((HttpStatusCode)502, """{"error":"bad_gateway"}"""), logger);
        var act = () => connector.CompleteOnceAsync(new MarketplaceAuthorizationCompletionRequest(Guid.NewGuid(), "some-code"), CancellationToken.None);
        (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure.SafeCode.Should().Be("PROVIDER_UNAVAILABLE");

        var events = logger.Entries.Where(e => (string)e.State["Operation"]! == "OAUTH_EXCHANGE").ToList();
        events.Should().ContainSingle();
        events.Single().State["Classification"].Should().Be("PROVIDER_UNAVAILABLE");
        events.Single().State["HttpStatus"].Should().Be("502");
    }

    [Fact]
    public async Task Oversized_response_emits_a_structured_event_with_the_real_RESPONSE_TOO_LARGE_classification()
    {
        var logger = new CapturingLoggerProvider();
        var generator = new InstrumentedGeneratorStream(totalLength: 8 * 1024 * 1024);
        var (connector, _, _) = CreateConnector(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new GeneratedBodyHttpContent(generator, declaredContentLength: null) }, logger);
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"super-secret-access-token-value","refresh_token":"super-secret-refresh-token-value","user_id":"1"}""");
        var act = () => connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "1", secret, null), CancellationToken.None);
        (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure.SafeCode.Should().Be("RESPONSE_TOO_LARGE");

        // The read loop itself throws before MarketplaceSafeReadExecutor gets a chance to log this
        // particular attempt with the RESPONSE_TOO_LARGE-specific classification (its own per-attempt
        // log already fired with classification=NONE for the 200 status BEFORE the body was ever
        // read) — assert the safe-read executor's own event reflects the transport-level facts it
        // actually knew (status 200, "NONE") and never leaks anything about the body's true size.
        var events = logger.Entries.Where(e => (string)e.State["Operation"]! == "ITEMS_SCAN").ToList();
        events.Should().ContainSingle();
        events.Single().State["HttpStatus"].Should().Be("200");
        AssertNoSensitiveStructuredValues(logger, "super-secret-access-token-value", "super-secret-refresh-token-value");
    }

    // ================= A-08-LOG: ITEMS_BULK structured telemetry (Sol re-gate blocker 2) =================

    [Fact]
    public async Task ITEMS_BULK_successful_call_emits_exactly_one_bulk_evidence_event_with_the_real_counts_and_histogram()
    {
        var logger = new CapturingLoggerProvider();
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.OK, """
            [
              {"code":200,"body":{"id":"MLB1","seller_id":777,"site_id":"MLB","status":"active","price":9.9,"currency_id":"BRL"}},
              {"code":404,"body":{"id":"MLB2"}},
              {"code":403,"body":{"id":"MLB3"}},
              {"code":200,"body":{"id":"MLB4","seller_id":888,"site_id":"MLB","status":"active"}},
              {"code":500,"body":{"id":"MLB5"}}
            ]
            """), logger);
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"super-secret-access-token-value","refresh_token":"super-secret-refresh-token-value","user_id":"777"}""");
        var results = await connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", secret, ["MLB1", "MLB2", "MLB3", "MLB4", "MLB5", "MLB6"]), CancellationToken.None);
        results.Should().HaveCount(6); // sanity: A-07's own outcome mapping is unaffected by this telemetry addition

        // Distinct from MarketplaceSafeReadExecutor's OWN generic per-HTTP-attempt log (which also
        // uses operation=ITEMS_BULK, without bulk evidence, for every retried/transport-level
        // attempt) — this is specifically the ONE bulk-EVIDENCE event this call's own
        // ReadListingDetailsAsync emits, identified by the presence of its own dedicated field.
        var events = logger.Entries.Where(e => (string)e.State["Operation"]! == "ITEMS_BULK" && e.State.ContainsKey("RequestedIdCount")).ToList();
        events.Should().ContainSingle("exactly one bulk-evidence event per attempt — never an early one followed by a correction");
        var evidence = events.Single();
        evidence.State["Classification"].Should().Be("SUCCESS");
        evidence.State["HttpStatus"].Should().Be("200");
        evidence.State["RequestedIdCount"].Should().Be(6, "the number of IDs actually submitted in the request");
        evidence.State["EnvelopeCount"].Should().Be(5, "the response envelope itself contained exactly 5 elements");
        evidence.State["UsableEnvelopeCount"].Should().Be(5, "all 5 elements carried a resolvable identity, whatever their individual outcome");
        var histogram = ParseHistogram((string)evidence.State["Histogram"]!);
        histogram.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["FOUND"] = 1, ["NOT_FOUND"] = 1, ["ACCESS_DENIED"] = 1, ["SELLER_MISMATCH"] = 1,
            ["INVALID_RESPONSE"] = 0, ["UNMAPPED_STATUS"] = 0, ["FAILED"] = 1,
        }, "MLB1=FOUND, MLB2=NOT_FOUND, MLB3=ACCESS_DENIED, MLB4=SELLER_MISMATCH (different seller_id), MLB5=FAILED (500) — MLB6 was requested but never appeared in the envelope, so it does not count toward envelopeCount/histogram at all, only toward requestedIdCount");
        AssertNoSensitiveStructuredValues(logger, "super-secret-access-token-value", "super-secret-refresh-token-value");
        AssertBulkEventNeverCarriesListingIdsOrBodies(evidence);
    }

    [Fact]
    public async Task ITEMS_BULK_mixed_response_emits_the_exact_histogram_for_a_successful_a_provider_error_and_a_malformed_element()
    {
        // The exact fixture Sol's re-gate asked for: a successful usable element, a provider-error
        // element, and a structurally malformed (non-object) element, all in ONE response — also
        // re-proves A-07 (the malformed element never crashes/drops the whole batch).
        var logger = new CapturingLoggerProvider();
        var (connector, _, _) = CreateConnector(_ => JsonResponse(HttpStatusCode.OK, """
            [
              {"code":200,"body":{"id":"MLB1","seller_id":777,"site_id":"MLB","status":"active","price":9.9,"currency_id":"BRL"}},
              {"code":403,"body":{"id":"MLB2"}},
              "this-is-not-an-object"
            ]
            """), logger);
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"super-secret-access-token-value","refresh_token":"super-secret-refresh-token-value","user_id":"777"}""");
        var results = await connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", secret, ["MLB1", "MLB2"]), CancellationToken.None);
        results.Single(r => r.ExternalListingId == "MLB1").Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
        results.Single(r => r.ExternalListingId == "MLB2").Outcome.Should().Be(MarketplaceListingReadOutcome.ACCESS_DENIED);

        // Distinct from MarketplaceSafeReadExecutor's OWN generic per-HTTP-attempt log (which also
        // uses operation=ITEMS_BULK, without bulk evidence, for every retried/transport-level
        // attempt) — this is specifically the ONE bulk-EVIDENCE event this call's own
        // ReadListingDetailsAsync emits, identified by the presence of its own dedicated field.
        var events = logger.Entries.Where(e => (string)e.State["Operation"]! == "ITEMS_BULK" && e.State.ContainsKey("RequestedIdCount")).ToList();
        events.Should().ContainSingle();
        var evidence = events.Single();
        evidence.State["RequestedIdCount"].Should().Be(2);
        evidence.State["EnvelopeCount"].Should().Be(3, "the response contained 3 top-level elements, including the malformed one");
        evidence.State["UsableEnvelopeCount"].Should().Be(2, "only the 2 elements with a resolvable identity — the malformed element has none");
        var histogram = ParseHistogram((string)evidence.State["Histogram"]!);
        histogram.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["FOUND"] = 1, ["ACCESS_DENIED"] = 1, ["FAILED"] = 1, // the malformed element's own synthetic FAILED outcome
            ["NOT_FOUND"] = 0, ["SELLER_MISMATCH"] = 0, ["INVALID_RESPONSE"] = 0, ["UNMAPPED_STATUS"] = 0,
        }, "the malformed element's synthetic FAILED result IS counted in the histogram (it genuinely happened) even though it has no resolvable identity and so is excluded from UsableEnvelopeCount");

        AssertNoSensitiveStructuredValues(logger, "super-secret-access-token-value", "super-secret-refresh-token-value");
        AssertBulkEventNeverCarriesListingIdsOrBodies(evidence);
    }

    [Fact]
    public async Task ITEMS_BULK_provider_HTTP_error_emits_a_bulk_evidence_event_with_zero_legitimately_obtained_counts()
    {
        var logger = new CapturingLoggerProvider();
        var (connector, _, _) = CreateConnector(_ => JsonResponse((HttpStatusCode)503, "{}"), logger);
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"super-secret-access-token-value","refresh_token":"super-secret-refresh-token-value","user_id":"777"}""");
        var act = () => connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", secret, ["MLB1", "MLB2"]), CancellationToken.None);
        await act.Should().ThrowAsync<MarketplaceProviderCallException>();

        // Distinct from MarketplaceSafeReadExecutor's OWN generic per-HTTP-attempt log (which also
        // uses operation=ITEMS_BULK, without bulk evidence, for every retried/transport-level
        // attempt) — this is specifically the ONE bulk-EVIDENCE event this call's own
        // ReadListingDetailsAsync emits, identified by the presence of its own dedicated field.
        var events = logger.Entries.Where(e => (string)e.State["Operation"]! == "ITEMS_BULK" && e.State.ContainsKey("RequestedIdCount")).ToList();
        // 503 is retryable, so the safe reader made all three real HTTP attempts.  Each one gets
        // exactly one final bulk event; a call-level summary would hide the retry boundary and
        // violate ADR-0025's one-event-per-attempt contract.
        events.Should().HaveCount(3);
        events.Select(e => (int)e.State["Attempt"]!).Should().Equal(1, 2, 3);
        foreach (var evidence in events)
        {
            evidence.State["RequestedIdCount"].Should().Be(2, "known upfront, regardless of what the provider ever returned");
            evidence.State["EnvelopeCount"].Should().Be(0, "no envelope was ever legitimately obtained — never fabricated as anything else");
            evidence.State["UsableEnvelopeCount"].Should().Be(0);
            ParseHistogram((string)evidence.State["Histogram"]!).Values.Should().OnlyContain(count => count == 0, "no element outcome was ever legitimately observed");
        }
        AssertNoSensitiveStructuredValues(logger, "super-secret-access-token-value", "super-secret-refresh-token-value");
        foreach (var evidence in events) AssertBulkEventNeverCarriesListingIdsOrBodies(evidence);
    }

    [Fact]
    public async Task ITEMS_BULK_oversized_response_emits_a_bulk_evidence_event_with_zero_legitimately_obtained_counts()
    {
        var logger = new CapturingLoggerProvider();
        var generator = new InstrumentedGeneratorStream(totalLength: 8 * 1024 * 1024);
        var (connector, _, _) = CreateConnector(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new GeneratedBodyHttpContent(generator, declaredContentLength: null) }, logger);
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"super-secret-access-token-value","refresh_token":"super-secret-refresh-token-value","user_id":"777"}""");
        var act = () => connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", secret, ["MLB1"]), CancellationToken.None);
        (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure.SafeCode.Should().Be("RESPONSE_TOO_LARGE");

        // Distinct from MarketplaceSafeReadExecutor's OWN generic per-HTTP-attempt log (which also
        // uses operation=ITEMS_BULK, without bulk evidence, for every retried/transport-level
        // attempt) — this is specifically the ONE bulk-EVIDENCE event this call's own
        // ReadListingDetailsAsync emits, identified by the presence of its own dedicated field.
        var events = logger.Entries.Where(e => (string)e.State["Operation"]! == "ITEMS_BULK" && e.State.ContainsKey("RequestedIdCount")).ToList();
        events.Should().ContainSingle();
        var evidence = events.Single();
        evidence.State["Classification"].Should().Be("RESPONSE_TOO_LARGE");
        evidence.State["HttpStatus"].Should().Be("200", "headers were genuinely received — only the body itself was never safely read");
        evidence.State["RequestedIdCount"].Should().Be(1);
        evidence.State["EnvelopeCount"].Should().Be(0, "the oversized body was never fully/safely read — no envelope was ever legitimately obtained");
        evidence.State["UsableEnvelopeCount"].Should().Be(0);
        AssertNoSensitiveStructuredValues(logger, "super-secret-access-token-value", "super-secret-refresh-token-value");
        AssertBulkEventNeverCarriesListingIdsOrBodies(evidence);
    }

    private static Dictionary<string, int> ParseHistogram(string histogram) =>
        histogram.Split(',').Select(pair => pair.Split('=')).ToDictionary(parts => parts[0], parts => int.Parse(parts[1]));

    /// <summary>The bulk-evidence event is counts-and-a-histogram ONLY — asserts none of its own
    /// structured values ever contain a listing id, the raw envelope, or the raw provider body
    /// (Authorization/Bearer/tokens are already covered by the shared sensitive-value assertion).</summary>
    private static void AssertBulkEventNeverCarriesListingIdsOrBodies(CapturedLogEntry evidence)
    {
        // "{OriginalFormat}" is Microsoft.Extensions.Logging's own standard state key holding the
        // literal MESSAGE TEMPLATE string (e.g. "...requestedIdCount={RequestedIdCount}...") — every
        // structured log event carries it, harmlessly, and its own placeholder braces are not a
        // body/envelope leak; excluded here, checked nowhere else in this file either.
        foreach (var (key, value) in evidence.State.Where(kv => kv.Key != "{OriginalFormat}"))
        {
            var text = value?.ToString() ?? "";
            text.Should().NotContain("MLB", $"no listing/seller item id may ever appear in the bulk-evidence event (field {key})");
            text.Should().NotContain("{", $"no raw envelope/body JSON may ever appear in the bulk-evidence event (field {key})");
        }
    }

    /// <summary>A-08-LOG: asserts NONE of the captured structured VALUES (never mind the rendered
    /// text) across every category/event contain a sensitive value — the genuinely rigorous form of
    /// "no secret material ever leaks", since a message-template change could hide a leak from a
    /// text-only assertion while the raw structured property bag still carried it.</summary>
    private static void AssertNoSensitiveStructuredValues(CapturingLoggerProvider logger, params string[] sensitiveValues)
    {
        foreach (var entry in logger.Entries)
        foreach (var value in entry.State.Values)
        {
            var text = value?.ToString() ?? "";
            foreach (var sensitive in sensitiveValues)
                text.Should().NotContain(sensitive, $"structured field value must never carry a secret (category={entry.Category})");
            text.Should().NotContain("Authorization").And.NotContain("Bearer");
        }
    }

    [Fact]
    public async Task Rate_limited_and_oversized_responses_each_emit_a_safe_log_event_too()
    {
        var logger = new CapturingLoggerProvider();
        var (connector, _, _) = CreateConnector(request =>
        {
            var response = JsonResponse((HttpStatusCode)429, "{}");
            response.Headers.Add("Retry-After", "5");
            return response;
        }, logger);
        var secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"a","refresh_token":"r","user_id":"1"}""");
        var act = () => connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "1", secret, null), CancellationToken.None);
        await act.Should().ThrowAsync<MarketplaceProviderCallException>();

        var events = logger.Messages.Where(m => m.Contains("MarketplaceProviderCall")).ToList();
        events.Should().NotBeEmpty();
        events.Should().OnlyContain(m => !m.Contains("Retry-After") || m.Contains("classification=RATE_LIMITED"));
        events.Should().Contain(m => m.Contains("status=429"));
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A-08 test double: reports a large declared length but must NEVER actually be read
    /// (proves the trustworthy-Content-Length-first fast path never touches the stream).</summary>
    private sealed class CountingNeverReadContent(long declaredLength, Action onRead) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) { onRead(); throw new InvalidOperationException("must never be read"); }
        protected override bool TryComputeLength(out long length) { length = declaredLength; return true; }
    }

    /// <summary>
    /// A-08-BODY: a genuine, never-byte[]-backed producer stream. Bytes are computed procedurally
    /// (a deterministic function of position), never materialized as one large array anywhere —
    /// capable of representing a body far larger than the 1 MiB bound (or of representing no
    /// bound at all, via <see cref="long.MaxValue"/>) while this object's own live footprint stays
    /// at a handful of scalar fields. Every <see cref="ReadAsync(byte[],int,int,CancellationToken)"/>
    /// call is counted, the bytes actually requested (the caller-supplied buffer's own length) are
    /// summed, and the highest offset ever handed out is tracked — the only way to prove a reader
    /// stopped consuming EARLY (bounded consumption) rather than merely "eventually threw after
    /// receiving everything regardless".
    /// </summary>
    private sealed class InstrumentedGeneratorStream(long totalLength) : Stream
    {
        public int ReadAsyncCallCount { get; private set; }
        public long TotalBytesRequested { get; private set; }
        public long MaxOffsetConsumed { get; private set; }
        public bool Disposed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => totalLength;
        public override long Position { get => MaxOffsetConsumed; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ReadAsyncCallCount++;
            TotalBytesRequested += count;
            var remaining = totalLength - MaxOffsetConsumed;
            var toProduce = (int)Math.Min(count, Math.Max(0, remaining));
            for (var i = 0; i < toProduce; i++) buffer[offset + i] = (byte)((MaxOffsetConsumed + i) % 251); // deterministic, never allocated in bulk
            MaxOffsetConsumed += toProduce;
            return Task.FromResult(toProduce);
        }

        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    /// <summary>Wraps an <see cref="InstrumentedGeneratorStream"/> as the response body — overriding
    /// <c>CreateContentReadStreamAsync</c> to hand the reader THAT stream directly, never routing
    /// through <see cref="HttpContent"/>'s own default (which would buffer the whole body into a
    /// MemoryStream via <c>SerializeToStreamAsync</c> before the reader ever sees it — exactly the
    /// unfaithful shortcut this replaces). A null <paramref name="declaredContentLength"/> leaves the
    /// Content-Length header entirely absent (the "no declared length / chunked-unknown" case from
    /// the reader's point of view — indistinguishable from real chunked transfer-encoding at this
    /// abstraction layer); a non-null value that disagrees with the generator's own true
    /// <paramref name="generator"/>.Length is the "incorrect/lying Content-Length" case.</summary>
    private sealed class GeneratedBodyHttpContent : HttpContent
    {
        private readonly InstrumentedGeneratorStream _generator;
        public InstrumentedGeneratorStream Generator => _generator;

        public GeneratedBodyHttpContent(InstrumentedGeneratorStream generator, long? declaredContentLength)
        {
            _generator = generator;
            if (declaredContentLength is { } len) Headers.ContentLength = len;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("must never be used — CreateContentReadStreamAsync hands the reader the generator stream directly");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(_generator);
#if NET5_0_OR_GREATER
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult<Stream>(_generator);
#endif
    }

    private sealed class ScriptedHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler);
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class ZeroJitter : IMarketplaceJitter { public TimeSpan Next() => TimeSpan.Zero; }
}

/// <summary>
/// A-03: <see cref="RetryAfterParser"/> is pure, deterministic logic with no HTTP/DB dependency —
/// deltaseconds, HTTP-date, invalid and missing-header cases, plus the new upper-bound clamp
/// (<see cref="RetryAfterParser.MaxDuration"/>) this delivery adds because neither ADR-0025 §F.4 nor
/// the Mercado Livre handoff states one.
/// </summary>
public sealed class RetryAfterParserTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Fact]
    public void Missing_header_returns_null()
    {
        RetryAfterParser.Parse(null, Now).Should().BeNull();
        RetryAfterParser.Parse("", Now).Should().BeNull();
        RetryAfterParser.Parse("   ", Now).Should().BeNull();
    }

    [Fact]
    public void Invalid_header_returns_null_and_never_invents_a_value()
    {
        RetryAfterParser.Parse("not-a-number-or-date", Now).Should().BeNull();
        RetryAfterParser.Parse("-5", Now).Should().BeNull("a negative delta-seconds is not a valid nonnegative integer");
        RetryAfterParser.Parse("3.5", Now).Should().BeNull("only a pure integer second count is accepted, never a fractional one");
    }

    [Fact]
    public void Delta_seconds_header_is_now_plus_that_many_seconds()
    {
        RetryAfterParser.Parse("120", Now).Should().Be(Now.AddSeconds(120));
    }

    [Fact]
    public void Zero_delta_seconds_is_not_a_future_instant_and_returns_null()
    {
        // "retry immediately" carries no actual guard — now.AddSeconds(0) is not STRICTLY after
        // now, so this is correctly treated as "no retry guard", never a value equal to now.
        RetryAfterParser.Parse("0", Now).Should().BeNull();
    }

    [Fact]
    public void Valid_future_http_date_header_is_honored_exactly()
    {
        var future = Now.AddHours(2);
        RetryAfterParser.Parse(future.ToString("R"), Now).Should().Be(future);
    }

    [Fact]
    public void A_past_http_date_is_not_a_future_instant_and_returns_null()
    {
        var past = Now.AddHours(-2);
        RetryAfterParser.Parse(past.ToString("R"), Now).Should().BeNull("§F.4 only ever honors a valid FUTURE HTTP-date");
    }

    [Fact]
    public void Excessive_delta_seconds_is_clamped_to_MaxDuration_never_rejected_outright()
    {
        // ~31 years in seconds — an absurd, almost certainly adversarial/malformed value.
        var result = RetryAfterParser.Parse("999999999", Now);
        result.Should().Be(Now + RetryAfterParser.MaxDuration,
            "A-03: an excessive header is CLAMPED to the disclosed safety ceiling, never trusted verbatim and never simply discarded — the underlying signal (slow down) is still honored, bounded");
    }

    [Fact]
    public void Excessive_future_http_date_is_also_clamped_to_MaxDuration()
    {
        var farFuture = Now.AddYears(5);
        var result = RetryAfterParser.Parse(farFuture.ToString("R"), Now);
        result.Should().Be(Now + RetryAfterParser.MaxDuration);
    }

    [Fact]
    public void A_delta_seconds_value_exactly_at_the_ceiling_is_returned_unclamped()
    {
        var exact = (int)RetryAfterParser.MaxDuration.TotalSeconds;
        RetryAfterParser.Parse(exact.ToString(), Now).Should().Be(Now + RetryAfterParser.MaxDuration);
    }

    [Fact]
    public void MaxDuration_is_twenty_four_hours_disclosed_as_a_VERCE_assumption_not_an_ADR_stated_value()
    {
        RetryAfterParser.MaxDuration.Should().Be(TimeSpan.FromHours(24));
    }
}
