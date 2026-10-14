using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verce.Infrastructure.Marketplaces.MercadoLivre;
using Verce.Modules.Commerce;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// M059 / Gate 058 E01-6 — completes the D8 evidence (<c>MercadoLivreConnectorContractTests</c> remains the
/// primary D8 class). Every test here proves ONE property the Gate 058 matrix found without an executed
/// assertion: normalization of every ADR-0025 §B.3 field and its bounds, the exact request shapes
/// (first scan / latest scroll_id / bulk attributes), the §F.4 3-attempt budget with recorded (never slept)
/// backoff, Retry-After handling, cancellation, /users/me identity+site+PII, and the grant probe table.
/// No test touches the network, a real seller or a real credential: the HTTP handler is scripted and the delay is
/// recorded rather than awaited.
/// </summary>
public sealed class MercadoLivreConnectorD8CompletionTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"d8-access-token-secret","refresh_token":"d8-refresh-token-secret","user_id":"777"}""");
    private static readonly string[] FrozenBulkAttributes =
    [
        "body.id", "body.seller_id", "body.site_id", "body.title", "body.price", "body.currency_id",
        "body.status", "body.sub_status", "body.permalink", "body.last_updated", "body.attributes", "body.variations",
    ];

    // ---------------------------------------------------------------- harness

    private sealed class Harness
    {
        public required MercadoLivreConnector Connector { get; init; }
        public required RecordingHandler Handler { get; init; }
        public required RecordingDelay Delay { get; init; }
        public required TestClock Clock { get; init; }
        public required CapturingLoggerProvider Logger { get; init; }
    }

    private static Harness Create(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
    {
        var options = Options.Create(new MercadoLivreOptions { Enabled = true, ClientId = "900000001", RedirectUri = "https://host.example/api/commerce/marketplace-authorizations/MERCADO_LIVRE/callback", ResolvedClientSecret = "d8-client-secret-value" });
        var clock = new TestClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var handler = new RecordingHandler(respond);
        var logger = new CapturingLoggerProvider();
        var sender = new MarketplaceHttpSender(new Microsoft.Extensions.Logging.Logger<MarketplaceHttpSender>(new Microsoft.Extensions.Logging.LoggerFactory([logger])));
        var delay = new RecordingDelay();
        var reader = new MarketplaceSafeReadExecutor(sender, delay, new ZeroJitter(), clock);
        return new Harness { Connector = new MercadoLivreConnector(new SingleClientFactory(handler), sender, reader, options, clock), Handler = handler, Delay = delay, Clock = clock, Logger = logger };
    }

    private static Harness Create(Func<HttpRequestMessage, HttpResponseMessage> respond) => Create((r, _) => respond(r));

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Authorization)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString()));
            return Task.FromResult(respond(request, Requests.Count));
        }
    }

    private sealed class RecordingDelay : IMarketplaceDelay
    {
        public List<TimeSpan> Delays { get; } = [];
        public Action? OnDelay { get; set; }
        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            Delays.Add(duration);
            OnDelay?.Invoke();
            cancellationToken.ThrowIfCancellationRequested(); // a real delay is cancellable; the recorded one must prove the same contract
            return Task.CompletedTask;
        }
    }

    private sealed class ZeroJitter : IMarketplaceJitter { public TimeSpan Next() => TimeSpan.Zero; }
    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler);
        public HttpClient CreateClient(string name) => _client;
    }

    // ---------------------------------------------------------------- bulk normalization harness

    private static JsonObject BaseBody(string id = "MLB1") => new()
    {
        ["id"] = id, ["seller_id"] = 777, ["site_id"] = "MLB", ["status"] = "active", ["price"] = 10.5m, ["currency_id"] = "BRL",
    };

    private static async Task<MarketplaceListingDetailResult> ReadOneAsync(Action<JsonObject> mutate)
    {
        var body = BaseBody(); mutate(body);
        var element = new JsonObject { ["id"] = "MLB1", ["status_code"] = 200, ["body"] = body };
        var harness = Create(_ => Json(HttpStatusCode.OK, new JsonArray(element).ToJsonString()));
        return (await harness.Connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", Secret, ["MLB1"]), CancellationToken.None)).Single();
    }

    private static JsonArray Attrs(params (string Id, string? Value)[] attributes)
    {
        var array = new JsonArray();
        foreach (var (id, value) in attributes) array.Add(new JsonObject { ["id"] = id, ["value_name"] = value is null ? null : JsonValue.Create(value) });
        return array;
    }

    // ---------------------------------------------------------------- SKU (§B.3: NFC, trimmed, controls removed, 1..200, ambiguity)

    [Fact]
    public async Task Sku_is_NFC_normalized_trimmed_and_stripped_of_control_characters()
    {
        var result = await ReadOneAsync(b => b["attributes"] = Attrs(("SELLER_SKU", "  \tSKU-é\u0007-1\n  ")));
        result.Facts!.ExternalSku.Should().Be("SKU-é-1", "decomposed e + combining acute composes to é (NFC); BEL and edge whitespace never survive");
        result.Facts.NormalizationWarnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Sku_length_bounds_are_inclusive_1_to_200_and_an_out_of_bounds_value_is_unknown_never_truncated()
    {
        (await ReadOneAsync(b => b["attributes"] = Attrs(("SELLER_SKU", "S")))).Facts!.ExternalSku.Should().Be("S");
        (await ReadOneAsync(b => b["attributes"] = Attrs(("SELLER_SKU", new string('A', 200))))).Facts!.ExternalSku.Should().Be(new string('A', 200));
        var tooLong = await ReadOneAsync(b => b["attributes"] = Attrs(("SELLER_SKU", new string('A', 201))));
        tooLong.Facts!.ExternalSku.Should().BeNull("a 201-char SKU is not authoritative and must not be silently cut to 200");
        tooLong.Facts.NormalizationWarnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u0007\u0008")]
    public async Task Sku_empty_blank_or_control_only_is_unknown(string value)
    {
        (await ReadOneAsync(b => b["attributes"] = Attrs(("SELLER_SKU", value)))).Facts!.ExternalSku.Should().BeNull();
    }

    [Fact]
    public async Task Sku_several_different_values_are_ambiguous_null_with_SKU_AMBIGUOUS()
    {
        var result = await ReadOneAsync(b => b["attributes"] = Attrs(("SELLER_SKU", "SKU-A"), ("SELLER_SKU", "SKU-B")));
        result.Facts!.ExternalSku.Should().BeNull();
        result.Facts.NormalizationWarnings.Should().Equal("SKU_AMBIGUOUS");
    }

    [Fact]
    public async Task Sku_repeated_values_that_normalize_to_the_same_string_are_one_value_not_ambiguous()
    {
        var result = await ReadOneAsync(b => b["attributes"] = Attrs(("SELLER_SKU", "SKU-A"), ("SELLER_SKU", "  SKU-A "), ("SELLER_SKU", "SKU-A\u0007")));
        result.Facts!.ExternalSku.Should().Be("SKU-A");
        result.Facts.NormalizationWarnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Sku_ignores_other_attributes_null_value_names_and_non_string_value_names()
    {
        var attributes = new JsonArray(
            new JsonObject { ["id"] = "BRAND", ["value_name"] = "ACME" },
            new JsonObject { ["id"] = "SELLER_SKU", ["value_name"] = null },
            new JsonObject { ["id"] = "SELLER_SKU", ["value_name"] = 12345 },
            new JsonObject { ["id"] = "SELLER_SKU" },
            JsonValue.Create("stray"), null, new JsonObject { ["id"] = "SELLER_SKU", ["value_name"] = "REAL-SKU" });
        var result = await ReadOneAsync(b => b["attributes"] = attributes);
        result.Facts!.ExternalSku.Should().Be("REAL-SKU");
        result.Facts.NormalizationWarnings.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- Title (§B.3: trimmed, controls removed, <=500 UTF-16 surrogate-safe)

    [Fact]
    public async Task Title_is_trimmed_and_stripped_of_control_characters()
    {
        var result = await ReadOneAsync(b => b["title"] = "  Ti\u0000tle\tX\u007f  ");
        result.Facts!.Title.Should().Be("TitleX");
    }

    [Fact]
    public async Task Title_of_exactly_500_units_is_untouched_and_501_is_cut_with_TITLE_TRUNCATED()
    {
        var exact = await ReadOneAsync(b => b["title"] = new string('t', 500));
        exact.Facts!.Title.Should().HaveLength(500); exact.Facts.NormalizationWarnings.Should().BeEmpty();
        var over = await ReadOneAsync(b => b["title"] = new string('t', 501));
        over.Facts!.Title.Should().HaveLength(500); over.Facts.NormalizationWarnings.Should().Equal("TITLE_TRUNCATED");
    }

    [Fact]
    public async Task Title_cut_never_splits_a_surrogate_pair()
    {
        var title = new string('a', 499) + "\U0001F600" + "b"; // the emoji occupies UTF-16 units 499 and 500
        var result = await ReadOneAsync(b => b["title"] = title);
        result.Facts!.Title.Should().HaveLength(499, "keeping unit 499 alone would leave a lone high surrogate");
        char.IsHighSurrogate(result.Facts.Title![^1]).Should().BeFalse();
        result.Facts.NormalizationWarnings.Should().Equal("TITLE_TRUNCATED");
    }

    [Fact]
    public async Task Title_a_surrogate_pair_ending_exactly_at_unit_500_is_kept_whole()
    {
        var title = new string('a', 498) + "\U0001F600" + "b";   // pair = units 498..499, then b = unit 500 -> cut after the pair
        var result = await ReadOneAsync(b => b["title"] = title);
        result.Facts!.Title.Should().HaveLength(500).And.EndWith("\U0001F600");
        result.Facts.NormalizationWarnings.Should().Equal("TITLE_TRUNCATED");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u0001\u0002")]
    public async Task Title_empty_blank_or_control_only_is_absent_not_a_warning(string title)
    {
        var result = await ReadOneAsync(b => b["title"] = title);
        result.Facts!.Title.Should().BeNull(); result.Facts.NormalizationWarnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Title_absent_or_not_a_string_is_absent()
    {
        (await ReadOneAsync(_ => { })).Facts!.Title.Should().BeNull();
        (await ReadOneAsync(b => b["title"] = 42)).Facts!.Title.Should().BeNull();
        (await ReadOneAsync(b => b["title"] = new JsonObject())).Facts!.Title.Should().BeNull();
    }

    // ---------------------------------------------------------------- native status (§B.3: lowercase [a-z0-9_] tokens, status or status:sub1,sub2, distinct, ordinal-sorted, <=200)

    [Fact]
    public async Task Native_status_without_sub_status_is_just_the_status_token()
    {
        (await ReadOneAsync(_ => { })).Facts!.ProviderNativeStatus.Should().Be("active");
    }

    [Fact]
    public async Task Native_status_lists_distinct_ordinal_sorted_lowercase_sub_status_tokens()
    {
        var result = await ReadOneAsync(b => { b["status"] = "paused"; b["sub_status"] = new JsonArray("Out_Of_Stock", "deactivated", "out_of_stock", "Out-Of Stock!", "---", 7, null); });
        result.Facts!.ObservedStatus.Should().Be(MarketplaceListingStatus.PAUSED);
        result.Facts.ProviderNativeStatus.Should().Be("paused:deactivated,out_of_stock,outofstock",
            "tokens are lowercased and filtered to [a-z0-9_]; duplicates collapse; '---' filters to empty and non-strings are ignored; order is ordinal");
    }

    [Fact]
    public async Task Native_status_never_exceeds_200_characters()
    {
        var subs = new JsonArray(); for (var i = 0; i < 60; i++) subs.Add($"sub_status_token_{i:00}");
        var result = await ReadOneAsync(b => { b["status"] = "paused"; b["sub_status"] = subs; });
        result.Facts!.ProviderNativeStatus.Should().HaveLength(200).And.StartWith("paused:sub_status_token_00,");
    }

    // ---------------------------------------------------------------- variations, site, seller, status presence

    [Fact]
    public async Task VariationCount_is_the_array_length_and_zero_when_absent_or_not_an_array()
    {
        (await ReadOneAsync(b => b["variations"] = new JsonArray(new JsonObject(), new JsonObject(), new JsonObject()))).Facts!.VariationCount.Should().Be(3);
        (await ReadOneAsync(_ => { })).Facts!.VariationCount.Should().Be(0);
        (await ReadOneAsync(b => b["variations"] = "x")).Facts!.VariationCount.Should().Be(0);
        (await ReadOneAsync(b => b["variations"] = null)).Facts!.VariationCount.Should().Be(0);
    }

    [Fact]
    public async Task Site_id_absent_is_accepted_and_present_but_not_MLB_is_INVALID_RESPONSE()
    {
        (await ReadOneAsync(b => b.Remove("site_id"))).Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
        (await ReadOneAsync(b => b["site_id"] = "MLA")).Outcome.Should().Be(MarketplaceListingReadOutcome.INVALID_RESPONSE);
        (await ReadOneAsync(b => b["site_id"] = "mlb")).Outcome.Should().Be(MarketplaceListingReadOutcome.INVALID_RESPONSE, "the site comparison is exact");
    }

    [Fact]
    public async Task Seller_id_number_and_string_forms_both_match_and_mismatch_discards_the_facts()
    {
        (await ReadOneAsync(b => b["seller_id"] = "777")).Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
        var mismatch = await ReadOneAsync(b => b["seller_id"] = 778);
        mismatch.Outcome.Should().Be(MarketplaceListingReadOutcome.SELLER_MISMATCH);
        mismatch.Facts.Should().BeNull("§B.3: SELLER_MISMATCH discards facts");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("777.5")]                       // not an integer
    [InlineData("99999999999999999999")]        // beyond Int64: unrepresentable, so missing — never "mismatch"
    [InlineData("true")]
    [InlineData("[777]")]
    public async Task Seller_id_unusable_values_are_INVALID_RESPONSE_not_a_mismatch(string rawSeller)
    {
        var body = """[{"id":"MLB1","status_code":200,"body":{"id":"MLB1","seller_id":""" + rawSeller + ""","site_id":"MLB","status":"active"}}]""";
        var harness = Create(_ => Json(HttpStatusCode.OK, body));
        var result = (await harness.Connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", Secret, ["MLB1"]), CancellationToken.None)).Single();
        result.Outcome.Should().Be(MarketplaceListingReadOutcome.INVALID_RESPONSE);
        result.Facts.Should().BeNull();
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("number")]
    public async Task Status_absent_empty_null_or_not_a_string_is_UNMAPPED_STATUS_never_ACTIVE(string variant)
    {
        var result = await ReadOneAsync(b =>
        {
            switch (variant)
            {
                case "absent": b.Remove("status"); break;
                case "empty": b["status"] = ""; break;
                case "null": b["status"] = null; break;
                default: b["status"] = 1; break;
            }
        });
        result.Outcome.Should().Be(MarketplaceListingReadOutcome.UNMAPPED_STATUS);
        result.Facts.Should().BeNull();
    }

    // ---------------------------------------------------------------- permalink allow-list (§B.3 ListingUrl)

    [Theory]
    [InlineData("https://produto.mercadolivre.com.br/MLB-123-item-_JM")]
    [InlineData("https://www.mercadolivre.com.br/")]
    [InlineData("http://mercadolivre.com.br/a")]
    [InlineData("https://MERCADOLIVRE.COM.BR/upper")]
    public async Task Permalink_on_the_mercadolivre_com_br_family_over_http_or_https_is_kept_verbatim(string url)
    {
        var result = await ReadOneAsync(b => b["permalink"] = url);
        result.Facts!.ListingUrl.Should().Be(url); result.Facts.NormalizationWarnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://mercadolivre.com.br.evil.example/x")]       // suffix trick
    [InlineData("https://notmercadolivre.com.br/x")]                 // missing dot boundary
    [InlineData("https://evil.example/?h=mercadolivre.com.br")]      // allow-listed text in the query only
    [InlineData("https://mercadolivre.com.br@evil.example/x")]       // user-info trick: the real host is evil.example
    [InlineData("https://mercadolibre.com/x")]                       // sibling brand domain
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://mercadolivre.com.br/x")]
    [InlineData("file://mercadolivre.com.br/x")]
    [InlineData("/relative/path")]
    [InlineData("mercadolivre.com.br/no-scheme")]
    [InlineData("")]
    public async Task Permalink_outside_the_allow_list_is_rejected_with_URL_REJECTED_and_never_stored(string url)
    {
        var result = await ReadOneAsync(b => b["permalink"] = url);
        result.Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND, "a bad URL is a per-field rejection, not an element failure");
        result.Facts!.ListingUrl.Should().BeNull(); result.Facts.NormalizationWarnings.Should().Equal("URL_REJECTED");
    }

    [Fact]
    public async Task Permalink_length_limit_is_2000_inclusive()
    {
        var prefix = "https://mercadolivre.com.br/";
        (await ReadOneAsync(b => b["permalink"] = prefix + new string('a', 2000 - prefix.Length))).Facts!.ListingUrl.Should().HaveLength(2000);
        var over = await ReadOneAsync(b => b["permalink"] = prefix + new string('a', 2001 - prefix.Length));
        over.Facts!.ListingUrl.Should().BeNull(); over.Facts.NormalizationWarnings.Should().Equal("URL_REJECTED");
    }

    [Fact]
    public async Task Permalink_absent_or_not_a_string_is_simply_null_without_a_warning()
    {
        foreach (var mutate in new Action<JsonObject>[] { _ => { }, b => b["permalink"] = 5, b => b["permalink"] = null })
        {
            var result = await ReadOneAsync(mutate);
            result.Facts!.ListingUrl.Should().BeNull(); result.Facts.NormalizationWarnings.Should().BeEmpty();
        }
    }

    // ---------------------------------------------------------------- last_updated (§B.3: ISO-8601 with offset -> UTC; unparseable -> null; never now)

    [Theory]
    [InlineData("2026-03-04T05:06:07.000-03:00", "2026-03-04T08:06:07Z")]
    [InlineData("2026-03-04T05:06:07Z", "2026-03-04T05:06:07Z")]
    [InlineData("2026-03-04T05:06:07+05:30", "2026-03-03T23:36:07Z")]
    public async Task LastUpdated_with_an_offset_is_converted_to_the_exact_UTC_instant(string raw, string expectedUtc)
    {
        var result = await ReadOneAsync(b => b["last_updated"] = raw);
        result.Facts!.ProviderObservedAt.Should().Be(DateTimeOffset.Parse(expectedUtc));
        result.Facts.ProviderObservedAt!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task LastUpdated_absent_unparseable_or_not_a_string_is_null_and_never_the_current_time()
    {
        foreach (var mutate in new Action<JsonObject>[] { _ => { }, b => b["last_updated"] = "garbage", b => b["last_updated"] = "", b => b["last_updated"] = 1738000000, b => b["last_updated"] = null })
            (await ReadOneAsync(mutate)).Facts!.ProviderObservedAt.Should().BeNull("unknown — never now");
    }

    // ---------------------------------------------------------------- exact request shapes

    [Fact]
    public async Task Bulk_request_is_a_single_authenticated_GET_to_items_bulk_with_the_exact_ids_and_the_frozen_attributes_and_never_the_deprecated_items_endpoint()
    {
        var harness = Create(_ => Json(HttpStatusCode.OK, "[]"));
        await harness.Connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", Secret, ["MLB1", "MLB22", "MLB333"]), CancellationToken.None);
        var request = harness.Handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.Host.Should().Be("api.mercadolibre.com"); request.Uri.AbsolutePath.Should().Be("/items/bulk");
        var query = HttpUtility.ParseQueryString(request.Uri.Query);
        query.AllKeys.OrderBy(k => k, StringComparer.Ordinal).Should().Equal(["attributes", "ids"], "no other query parameter may exist");
        query["ids"].Should().Be("MLB1,MLB22,MLB333");
        query["attributes"].Should().Be(string.Join(",", FrozenBulkAttributes));
        request.Authorization.Should().Be("Bearer d8-access-token-secret");
    }

    [Fact]
    public async Task First_scan_request_is_exact_and_each_continuation_carries_only_the_latest_scroll_id()
    {
        var harness = Create((_, n) => n switch
        {
            1 => Json(HttpStatusCode.OK, """{"results":["MLB1","MLB2"],"scroll_id":"SCROLL-ONE"}"""),
            2 => Json(HttpStatusCode.OK, """{"results":["MLB3"],"scroll_id":"SCROLL-TWO"}"""),
            _ => Json(HttpStatusCode.OK, """{"results":[],"scroll_id":"SCROLL-THREE"}"""),
        });
        MarketplaceListingCursor? cursor = null; var pages = new List<MarketplaceListingPage>();
        for (var i = 0; i < 3; i++)
        {
            var page = await harness.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, cursor), CancellationToken.None);
            pages.Add(page); cursor = page.NextCursor;
        }
        var requests = harness.Handler.Requests;
        requests.Should().HaveCount(3).And.OnlyContain(r => r.Method == HttpMethod.Get && r.Uri.AbsolutePath == "/users/777/items/search");
        requests[0].Uri.Query.Should().Be("?search_type=scan&limit=100", "first call: no scroll_id, no status filter, no offset, no sort");
        HttpUtility.ParseQueryString(requests[1].Uri.Query)["scroll_id"].Should().Be("SCROLL-ONE");
        HttpUtility.ParseQueryString(requests[2].Uri.Query)["scroll_id"].Should().Be("SCROLL-TWO", "the LATEST scroll_id replaces the earlier one — never the first");
        requests[2].Uri.Query.Should().NotContain("SCROLL-ONE");
        foreach (var r in requests.Skip(1)) HttpUtility.ParseQueryString(r.Uri.Query).AllKeys.Should().BeEquivalentTo("search_type", "limit", "scroll_id");
        pages[0].ExternalListingIds.Should().Equal("MLB1", "MLB2"); pages[0].EndOfEnumeration.Should().BeFalse();
        pages[2].EndOfEnumeration.Should().BeTrue("an empty raw `results` array terminates even when a scroll_id is still present");
        pages[2].NextCursor.Should().BeNull();
        harness.Logger.Messages.Should().NotContain(m => m.Contains("SCROLL-ONE") || m.Contains("SCROLL-TWO") || m.Contains("SCROLL-THREE"), "the scroll_id lives only in the in-memory cursor and is never logged");
    }

    [Theory]
    [InlineData("""{"results":["MLB1"]}""")]                          // scroll_id absent
    [InlineData("""{"results":["MLB1"],"scroll_id":null}""")]
    [InlineData("""{"results":["MLB1"],"scroll_id":""}""")]
    [InlineData("""{"results":["MLB1"],"scroll_id":7}""")]            // not a string: no usable continuation
    public async Task Scan_terminates_when_the_continuation_is_absent_null_empty_or_not_a_string(string body)
    {
        var harness = Create(_ => Json(HttpStatusCode.OK, body));
        var page = await harness.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, null), CancellationToken.None);
        page.EndOfEnumeration.Should().BeTrue(); page.NextCursor.Should().BeNull(); page.ExternalListingIds.Should().Equal("MLB1");
    }

    [Fact]
    public async Task Scan_url_encodes_the_external_account_id_and_the_scroll_id()
    {
        var harness = Create(_ => Json(HttpStatusCode.OK, """{"results":[],"scroll_id":"a b&c=d/e"}"""));
        var first = await harness.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "7/7?x#y", Secret, null), CancellationToken.None);
        harness.Handler.Requests[0].Uri.AbsolutePath.Should().Be("/users/7%2F7%3Fx%23y/items/search");
        var second = await harness.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, new MercadoLivreScrollCursor("a b&c=d/e")), CancellationToken.None);
        HttpUtility.ParseQueryString(harness.Handler.Requests[1].Uri.Query)["scroll_id"].Should().Be("a b&c=d/e");
        first.Should().NotBeNull(); second.Should().NotBeNull();
    }

    // ---------------------------------------------------------------- scan failure classification (§B.2)

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(422)]
    public async Task Continuation_answered_with_a_4xx_other_than_401_403_429_is_LISTING_ENUMERATION_CURSOR_INVALID(int status)
    {
        var harness = Create(_ => Json((HttpStatusCode)status, "{}"));
        var act = () => harness.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, new MercadoLivreScrollCursor("S1")), CancellationToken.None);
        var failure = (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.NON_RETRYABLE); failure.SafeCode.Should().Be("LISTING_ENUMERATION_CURSOR_INVALID");
        harness.Handler.Requests.Should().HaveCount(1, "a non-retryable 4xx is never retried");
    }

    [Theory]
    [InlineData(401, MarketplaceFailureClassification.AUTH_RENEWAL_REQUIRED, "PROVIDER_TOKEN_REJECTED")]
    [InlineData(403, MarketplaceFailureClassification.USER_ACTION_REQUIRED, "PROVIDER_ACCESS_FORBIDDEN")]
    public async Task Continuation_401_and_403_keep_their_ordinary_read_error_mapping_never_cursor_invalid(int status, MarketplaceFailureClassification classification, string code)
    {
        var harness = Create(_ => Json((HttpStatusCode)status, "{}"));
        var act = () => harness.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, new MercadoLivreScrollCursor("S1")), CancellationToken.None);
        var failure = (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(classification); failure.SafeCode.Should().Be(code);
    }

    [Fact]
    public async Task Continuation_429_is_RATE_LIMITED_never_cursor_invalid()
    {
        var harness = Create(_ => Json((HttpStatusCode)429, "{}"));
        var act = () => harness.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, new MercadoLivreScrollCursor("S1")), CancellationToken.None);
        var failure = (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.RATE_LIMITED); failure.SafeCode.Should().Be("PROVIDER_RATE_LIMITED");
    }

    [Fact]
    public async Task First_page_4xx_is_a_plain_request_rejection_never_a_cursor_problem()
    {
        var harness = Create(_ => Json(HttpStatusCode.BadRequest, "{}"));
        var act = () => harness.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, null), CancellationToken.None);
        var failure = (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.NON_RETRYABLE); failure.SafeCode.Should().Be("PROVIDER_REQUEST_REJECTED");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("{\"results\":[\"MLB1\"")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public async Task Scan_200_with_an_unparseable_or_non_object_payload_is_a_typed_NON_RETRYABLE_PROVIDER_RESPONSE_INVALID_never_a_raw_exception(string body)
    {
        var harness = Create(_ => Json(HttpStatusCode.OK, body));
        var act = () => harness.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, null), CancellationToken.None);
        var failure = (await act.Should().ThrowAsync<MarketplaceProviderCallException>("an untyped JsonException/InvalidOperationException would be indistinguishable from a transient local fault")).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.NON_RETRYABLE); failure.SafeCode.Should().Be("PROVIDER_RESPONSE_INVALID");
        failure.Certainty.Should().Be(MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED);
        harness.Handler.Requests.Should().HaveCount(1);
    }

    // ---------------------------------------------------------------- §F.4 budget / Retry-After / cancellation

    [Fact]
    public async Task Persistent_503_makes_exactly_three_attempts_with_the_250ms_then_500ms_backoff_then_RETRYABLE()
    {
        var harness = Create(_ => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var act = () => harness.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, null), CancellationToken.None);
        var failure = (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.RETRYABLE); failure.SafeCode.Should().Be("PROVIDER_UNAVAILABLE");
        harness.Handler.Requests.Should().HaveCount(3);
        harness.Delay.Delays.Should().Equal(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task A_transient_failure_followed_by_success_stops_retrying_at_the_success()
    {
        var harness = Create((_, n) => n < 3 ? Json(HttpStatusCode.BadGateway, "{}") : Json(HttpStatusCode.OK, """{"results":["MLB1"]}"""));
        var page = await harness.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, null), CancellationToken.None);
        page.ExternalListingIds.Should().Equal("MLB1");
        harness.Handler.Requests.Should().HaveCount(3); harness.Delay.Delays.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(400)]
    public async Task Non_transient_statuses_are_never_retried(int status)
    {
        var harness = Create(_ => Json((HttpStatusCode)status, "{}"));
        var act = () => harness.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, null), CancellationToken.None);
        await act.Should().ThrowAsync<MarketplaceProviderCallException>();
        harness.Handler.Requests.Should().HaveCount(1); harness.Delay.Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task Transport_exceptions_are_retried_to_the_three_attempt_budget_and_end_as_RETRYABLE_with_sent_or_unknown_certainty()
    {
        var harness = Create(_ => throw new HttpRequestException("synthetic transport failure"));
        var act = () => harness.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, null), CancellationToken.None);
        var failure = (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.RETRYABLE); failure.SafeCode.Should().Be("PROVIDER_UNREACHABLE"); failure.Certainty.Should().Be(MarketplaceSendCertainty.SENT_OR_UNKNOWN);
        harness.Handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task RetryAfter_in_seconds_is_a_floor_for_the_next_delay_and_the_retry_then_succeeds()
    {
        var harness = Create((_, n) => n == 1 ? Json((HttpStatusCode)429, "{}", "2") : Json(HttpStatusCode.OK, """{"results":["MLB1"]}"""));
        var page = await harness.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, null), CancellationToken.None);
        page.ExternalListingIds.Should().Equal("MLB1");
        harness.Delay.Delays.Should().Equal(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RetryAfter_beyond_the_remaining_30_second_budget_fails_immediately_with_the_parsed_window_and_never_sleeps()
    {
        var harness = Create(_ => Json((HttpStatusCode)429, "{}", "3600"));
        var act = () => harness.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, null), CancellationToken.None);
        var failure = (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.RATE_LIMITED); failure.SafeCode.Should().Be("PROVIDER_RATE_LIMITED");
        failure.RetryAfterUntil.Should().Be(harness.Clock.UtcNow.AddSeconds(3600));
        harness.Handler.Requests.Should().HaveCount(1); harness.Delay.Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task RetryAfter_as_an_HTTP_date_is_honored_exactly()
    {
        var harness = Create(_ => Json((HttpStatusCode)429, "{}", "Thu, 01 Jan 2026 00:10:00 GMT"));
        var act = () => harness.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, null), CancellationToken.None);
        var failure = (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.RetryAfterUntil.Should().Be(DateTimeOffset.Parse("2026-01-01T00:10:00Z"));
    }

    [Fact]
    public async Task Rate_limit_without_or_with_an_invalid_RetryAfter_never_invents_a_window_and_exhausts_the_three_attempts()
    {
        foreach (var header in new string?[] { null, "not-a-number", "-5", "" })
        {
            var harness = Create(_ => Json((HttpStatusCode)429, "{}", header));
            var act = () => harness.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, null), CancellationToken.None);
            var failure = (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
            failure.Classification.Should().Be(MarketplaceFailureClassification.RATE_LIMITED);
            failure.RetryAfterUntil.Should().BeNull($"header '{header}' yields no value");
            harness.Handler.Requests.Should().HaveCount(3);
        }
    }

    [Fact]
    public async Task Cancellation_during_the_retry_backoff_propagates_as_OperationCanceled_not_a_provider_failure_and_stops_further_attempts()
    {
        using var cts = new CancellationTokenSource();
        var harness = Create(_ => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        harness.Delay.OnDelay = cts.Cancel;
        var act = () => harness.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, null), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        harness.Handler.Requests.Should().HaveCount(1, "no attempt may start after cancellation");
    }

    [Fact]
    public async Task A_token_cancelled_before_the_call_never_produces_a_provider_failure_classification()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var harness = Create(_ => Json(HttpStatusCode.OK, """{"results":[]}"""));
        var act = () => harness.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Secret, null), cts.Token);
        var thrown = await Record.ExceptionAsync(act);
        thrown.Should().NotBeNull().And.NotBeOfType<MarketplaceProviderCallException>("cancellation is the caller's decision, never a provider outcome");
        harness.Handler.Requests.Count.Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task The_token_endpoint_POST_is_never_retried_after_a_5xx_for_exchange_or_refresh()
    {
        var exchange = Create(_ => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var exchangeAct = () => exchange.Connector.CompleteOnceAsync(new MarketplaceAuthorizationCompletionRequest(Guid.NewGuid(), "code"), CancellationToken.None);
        (await exchangeAct.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure.SafeCode.Should().Be("PROVIDER_UNAVAILABLE");
        exchange.Handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Post);
        exchange.Delay.Delays.Should().BeEmpty();

        var refresh = Create(_ => Json(HttpStatusCode.BadGateway, "{}"));
        var refreshAct = () => refresh.Connector.RefreshAsync(new MarketplaceRefreshRequest(Guid.NewGuid(), MercadoLivreConnector.Code, Secret), CancellationToken.None);
        await refreshAct.Should().ThrowAsync<MarketplaceProviderCallException>();
        refresh.Handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Post);
    }

    // ---------------------------------------------------------------- /users/me identity, site, PII and the grant probe

    private static Harness ProbeHarness(string usersMeBody, Func<int, HttpResponseMessage> probeResponse, HttpStatusCode usersMeStatus = HttpStatusCode.OK)
    {
        var probeCalls = 0;
        return Create(request => request.RequestUri!.AbsolutePath == "/users/me"
            ? Json(usersMeStatus, usersMeBody)
            : probeResponse(++probeCalls));
    }

    private static Task<MarketplaceAccountInspection> InspectAsync(Harness harness) =>
        harness.Connector.InspectAsync(new MarketplaceAccountInspectionRequest(Guid.NewGuid(), MercadoLivreConnector.Code, Secret), CancellationToken.None);

    [Fact]
    public async Task Probe_200_grants_LISTINGS_READ_with_the_exact_limit_one_probe_url()
    {
        var harness = ProbeHarness("""{"id":777,"site_id":"MLB"}""", _ => Json(HttpStatusCode.OK, """{"results":[]}"""));
        var inspection = await InspectAsync(harness);
        inspection.ExternalAccountId.Should().Be("777");
        inspection.Grants.Should().Equal(new Dictionary<string, AccountCapabilityState> { ["LISTINGS_READ"] = AccountCapabilityState.GRANTED });
        harness.Handler.Requests.Select(r => r.Uri.PathAndQuery).Should().Equal("/users/me", "/users/777/items/search?limit=1");
    }

    [Theory]
    [InlineData(403)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(429)]
    public async Task Probe_non_success_is_UNKNOWN_and_never_DENIED(int status)
    {
        var harness = ProbeHarness("""{"id":777,"site_id":"MLB"}""", _ => Json((HttpStatusCode)status, "{}"));
        var inspection = await InspectAsync(harness);
        inspection.Grants["LISTINGS_READ"].Should().Be(AccountCapabilityState.UNKNOWN, "§A.5.2: 403 is ambiguous, so the ML adapter never reports DENIED");
        Enum.GetValues<AccountCapabilityState>().Should().Contain(AccountCapabilityState.DENIED, "the DENIED state exists in the vocabulary; the adapter simply must not choose it");
        inspection.Grants.Values.Should().NotContain(AccountCapabilityState.DENIED);
    }

    [Fact]
    public async Task Probe_5xx_uses_the_full_three_attempt_budget_before_settling_on_UNKNOWN()
    {
        var harness = ProbeHarness("""{"id":777,"site_id":"MLB"}""", _ => Json(HttpStatusCode.InternalServerError, "{}"));
        (await InspectAsync(harness)).Grants["LISTINGS_READ"].Should().Be(AccountCapabilityState.UNKNOWN);
        harness.Handler.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/items/search")).Should().Be(3);
    }

    [Fact]
    public async Task Probe_transport_failure_is_UNKNOWN_not_an_exception()
    {
        var harness = Create(request => request.RequestUri!.AbsolutePath == "/users/me" ? Json(HttpStatusCode.OK, """{"id":777,"site_id":"MLB"}""") : throw new HttpRequestException("probe transport"));
        (await InspectAsync(harness)).Grants["LISTINGS_READ"].Should().Be(AccountCapabilityState.UNKNOWN);
    }

    [Theory]
    [InlineData("""{"id":777,"site_id":"MLA"}""")]       // wrong site
    [InlineData("""{"id":777,"site_id":"mlb"}""")]       // exact comparison
    [InlineData("""{"id":777}""")]                       // site absent: identity cannot be proven MLB
    [InlineData("""{"id":888,"site_id":"MLB"}""")]       // different identity than the stored one
    [InlineData("""{"id":"888","site_id":"MLB"}""")]
    public async Task Inspect_identity_or_site_mismatch_is_PROBE_IDENTITY_MISMATCH_requiring_user_action_and_never_probes_grants(string usersMeBody)
    {
        var harness = ProbeHarness(usersMeBody, _ => Json(HttpStatusCode.OK, "{}"));
        var act = () => InspectAsync(harness);
        var failure = (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.USER_ACTION_REQUIRED); failure.SafeCode.Should().Be("PROBE_IDENTITY_MISMATCH");
        harness.Handler.Requests.Should().ContainSingle("no grant probe may run for a mismatching identity");
    }

    [Fact]
    public async Task Inspect_accepts_the_identity_as_a_JSON_string_as_well_as_a_number()
    {
        var harness = ProbeHarness("""{"id":"777","site_id":"MLB"}""", _ => Json(HttpStatusCode.OK, "{}"));
        (await InspectAsync(harness)).ExternalAccountId.Should().Be("777");
    }

    [Theory]
    [InlineData("""{"site_id":"MLB"}""")]                 // id missing
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task Users_me_without_a_usable_identity_or_with_a_malformed_body_is_a_typed_NON_RETRYABLE_PROVIDER_RESPONSE_INVALID(string usersMeBody)
    {
        var harness = ProbeHarness(usersMeBody, _ => Json(HttpStatusCode.OK, "{}"));
        var act = () => InspectAsync(harness);
        var failure = (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.NON_RETRYABLE); failure.SafeCode.Should().Be("PROVIDER_RESPONSE_INVALID");
    }

    [Theory]
    [InlineData(401, MarketplaceFailureClassification.AUTH_RENEWAL_REQUIRED, "PROVIDER_TOKEN_REJECTED")]
    [InlineData(403, MarketplaceFailureClassification.USER_ACTION_REQUIRED, "PROVIDER_ACCESS_FORBIDDEN")]
    [InlineData(503, MarketplaceFailureClassification.RETRYABLE, "PROVIDER_UNAVAILABLE")]
    public async Task Users_me_error_statuses_follow_the_read_error_mapping(int status, MarketplaceFailureClassification classification, string code)
    {
        var harness = ProbeHarness("{}", _ => Json(HttpStatusCode.OK, "{}"), (HttpStatusCode)status);
        var act = () => InspectAsync(harness);
        var failure = (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(classification); failure.SafeCode.Should().Be(code);
    }

    [Fact]
    public async Task Users_me_PII_is_discarded_from_the_result_and_never_logged()
    {
        const string email = "pii.person@example.test"; const string name = "Fulano Detalhe"; const string phone = "+55 11 99999-0000"; const string street = "Rua Sintetica 123";
        var usersMe = """{"id":777,"site_id":"MLB","email":""" + $"\"{email}\"" + ""","first_name":""" + $"\"{name}\"" + ""","phone":{"number":""" + $"\"{phone}\"" + """},"address":{"street":""" + $"\"{street}\"" + """}}""";
        var harness = ProbeHarness(usersMe, _ => Json(HttpStatusCode.OK, "{}"));
        var inspection = await InspectAsync(harness);
        var serialized = JsonSerializer.Serialize(inspection);
        foreach (var pii in new[] { email, name, phone, street }) serialized.Should().NotContain(pii);
        var logged = string.Join("\n", harness.Logger.Messages) + string.Join("\n", harness.Logger.Entries.SelectMany(e => e.State.Values.Select(v => v?.ToString())));
        foreach (var secret in new[] { email, name, phone, street, "d8-access-token-secret", "d8-refresh-token-secret" }) logged.Should().NotContain(secret);
    }

    [Fact]
    public async Task Complete_exchange_rejects_a_users_me_site_other_than_MLB_after_the_token_was_accepted()
    {
        var harness = Create(request => request.RequestUri!.AbsolutePath == "/oauth/token"
            ? Json(HttpStatusCode.OK, """{"access_token":"a","token_type":"bearer","expires_in":21600,"user_id":123,"refresh_token":"r"}""")
            : Json(HttpStatusCode.OK, """{"id":123,"site_id":"MLA"}"""));
        var act = () => harness.Connector.CompleteOnceAsync(new MarketplaceAuthorizationCompletionRequest(Guid.NewGuid(), "code"), CancellationToken.None);
        var failure = (await act.Should().ThrowAsync<MarketplaceProviderCallException>()).Which.Failure;
        failure.Classification.Should().Be(MarketplaceFailureClassification.NON_RETRYABLE); failure.SafeCode.Should().Be("PROVIDER_RESPONSE_INVALID");
        failure.Certainty.Should().Be(MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED, "the token WAS issued: this must never read as replayable");
    }
}
