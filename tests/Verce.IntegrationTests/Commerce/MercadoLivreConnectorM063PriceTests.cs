using System.Diagnostics;
using System.Net;
using System.Text;
using FluentAssertions;
using Verce.Infrastructure.Marketplaces.MercadoLivre;
using Verce.Modules.Commerce;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// M063 / C063-01 — ADR-0029 §1: ObservedPrice is validated on the EXACT JSON number lexeme, never on a rounded decimal. Every case goes through the public
/// <see cref="MercadoLivreConnector.ReadListingDetailsAsync"/> of the real connector over a scripted wire (no network). PR-1..PR-4 and PR-6 live here; PR-5 (PostgreSQL) is in
/// the execution suite.
/// </summary>
public sealed class MercadoLivreConnectorM063PriceTests
{
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Element(string id, string pricePart, string currencyPart = "\"currency_id\":\"BRL\"") =>
        "{\"id\":\"" + id + "\",\"status_code\":200,\"body\":{\"id\":\"" + id + "\",\"seller_id\":777,\"site_id\":\"MLB\",\"status\":\"active\"," + (pricePart.Length == 0 ? "" : pricePart + ",") + currencyPart + "}}";

    private static async Task<IReadOnlyList<MarketplaceListingDetailResult>> ReadAsync(string json, params string[] ids)
    {
        var rig = AdapterRig.Create((_, _) => Json(json));
        return await rig.Connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", AdapterRig.Secret, ids), CancellationToken.None);
    }

    private static async Task<MarketplaceListingDetailResult> ReadOneAsync(string pricePart, string currencyPart = "\"currency_id\":\"BRL\"") =>
        (await ReadAsync("[" + Element("MLB1", pricePart, currencyPart) + "]", "MLB1")).Single();

    // ============================================================ PR-1 — the frozen table

    [Theory]
    [InlineData("10", "10.00")]
    [InlineData("10.100", "10.10")]
    [InlineData("1.0E+1", "10.00")]
    [InlineData("1E+0000000002", "100.00")]
    [InlineData("0.01", "0.01")]
    [InlineData("9999999999999999.99", "9999999999999999.99")]
    [InlineData("9999999999999999.990000", "9999999999999999.99")]
    [InlineData("1e0", "1.00")]
    [InlineData("0.1E1", "1.00")]
    [InlineData("100E-2", "1.00")]
    [InlineData("1E15", "1000000000000000.00")]
    [InlineData("0.10", "0.10")]
    [InlineData("12345678901234.56", "12345678901234.56")]
    [InlineData("1.5", "1.50")]
    [InlineData("999999999999999.9", "999999999999999.90")]
    public async Task PR_1_valid_prices_are_accepted_exactly_with_two_decimal_places_and_no_warning(string lexeme, string expected)
    {
        var result = await ReadOneAsync("\"price\":" + lexeme);
        result.Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
        var price = result.Facts!.ObservedPrice!.Value;
        price.Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        price.ToString(System.Globalization.CultureInfo.InvariantCulture).Should().Be(expected, "the value is normalized to exactly two decimal places");
        result.Facts.NormalizationWarnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("1.00000000000000000000000000001")]                // Gate 061 F061-01: used to come back as 1.0000000000000000000000000000 with no warning
    [InlineData("9999999999999999.9900000000000001")]              // Gate 061 F061-01: used to come back as the ceiling with no warning
    [InlineData("999999999999999999.99E-2")]                       // exact value 9999999999999999.9999 : scale 4
    [InlineData("0.001")]
    [InlineData("0.005")]
    [InlineData("10.001")]
    [InlineData("1E-400")]
    [InlineData("0.0000000000000000000000000001")]
    [InlineData("10000000000000000")]                              // 17 integer digits
    [InlineData("1E17")]
    [InlineData("1E16")]
    [InlineData("79228162514264337593543950335")]                  // decimal.MaxValue
    [InlineData("1E400")]
    [InlineData("12345678901234567.8")]
    [InlineData("0.123")]
    public async Task PR_1_positive_prices_that_are_not_exactly_representable_in_numeric_18_2_are_null_with_the_precision_warning(string lexeme)
    {
        var result = await ReadOneAsync("\"price\":" + lexeme);
        result.Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
        result.Facts!.ObservedPrice.Should().BeNull("never rounded, truncated, saturated or zeroed");
        result.Facts.NormalizationWarnings.Should().Equal("PRICE_PRECISION_UNSUPPORTED");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.00")]
    [InlineData("-0")]
    [InlineData("-0.0")]
    [InlineData("0E9")]
    [InlineData("0e-9")]
    [InlineData("-0.01")]
    [InlineData("-1")]
    [InlineData("-1E400")]
    [InlineData("-1E-400")]
    [InlineData("-79228162514264337593543950335")]
    [InlineData("-0.0000000000000000000000000001")]
    public async Task PR_1_zero_and_negative_prices_are_null_with_the_not_positive_warning_whatever_their_magnitude(string lexeme)
    {
        var result = await ReadOneAsync("\"price\":" + lexeme);
        result.Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
        result.Facts!.ObservedPrice.Should().BeNull();
        result.Facts.NormalizationWarnings.Should().Equal("PRICE_NOT_POSITIVE");
    }

    [Theory]
    [InlineData("10.00", "\"currency_id\":\"USD\"")]
    [InlineData("1E400", "\"other\":1")]                           // currency absent
    [InlineData("-1", "\"currency_id\":7")]
    [InlineData("0", "\"currency_id\":null")]
    [InlineData("79228162514264337593543950335", "\"currency_id\":\"brl\"")]   // the comparison is exact
    public async Task PR_1_a_currency_other_than_BRL_wins_over_every_numeric_judgement(string lexeme, string currencyPart)
    {
        var result = await ReadOneAsync("\"price\":" + lexeme, currencyPart);
        result.Facts!.ObservedPrice.Should().BeNull();
        result.Facts.NormalizationWarnings.Should().Equal("PRICE_CURRENCY_UNSUPPORTED");
    }

    [Theory]
    [InlineData("\"price\":\"10.00\"")]
    [InlineData("\"price\":true")]
    [InlineData("\"price\":[10]")]
    [InlineData("\"price\":{\"amount\":10}")]
    [InlineData("\"price\":null")]
    [InlineData("")]
    public async Task PR_1_an_absent_or_non_number_price_is_unknown_without_a_warning(string pricePart)
    {
        var result = await ReadOneAsync(pricePart);
        result.Facts!.ObservedPrice.Should().BeNull(); result.Facts.NormalizationWarnings.Should().BeEmpty();
    }

    // ============================================================ PR-2 — the Gate 061 probes through the public API, with a healthy peer in the same response

    [Fact]
    public async Task PR_2_the_two_Gate_061_probes_are_null_with_the_precision_warning_and_the_peer_is_untouched()
    {
        var json = "[" + Element("MLB-A", "\"price\":1.00000000000000000000000000001") + "," + Element("MLB-B", "\"price\":9999999999999999.9900000000000001") + "," + Element("MLB-PEER", "\"price\":19.90") + "]";
        var results = await ReadAsync(json, "MLB-A", "MLB-B", "MLB-PEER");
        results.Select(r => r.Outcome).Should().OnlyContain(o => o == MarketplaceListingReadOutcome.FOUND);
        foreach (var probe in results.Take(2)) { probe.Facts!.ObservedPrice.Should().BeNull(); probe.Facts.NormalizationWarnings.Should().Equal("PRICE_PRECISION_UNSUPPORTED"); probe.Facts.ObservedStatus.Should().Be(MarketplaceListingStatus.ACTIVE); }
        results[2].Facts!.ObservedPrice.Should().Be(19.90m); results[2].Facts!.NormalizationWarnings.Should().BeEmpty();
    }

    // ============================================================ PR-3 — hostile lexemes: bounded work, no exception

    [Fact]
    public async Task PR_3_huge_lexemes_and_exponents_are_classified_without_exception_and_without_unbounded_work()
    {
        var cases = new (string Lexeme, string Warning)[]
        {
            ("1" + new string('0', 500), "PRICE_PRECISION_UNSUPPORTED"),
            ("0." + new string('0', 500) + "1", "PRICE_PRECISION_UNSUPPORTED"),
            ("1E+999999999999", "PRICE_PRECISION_UNSUPPORTED"),
            ("1E-999999999999", "PRICE_PRECISION_UNSUPPORTED"),
            ("1E" + new string('9', 100_000), "PRICE_PRECISION_UNSUPPORTED"),       // exponent with 100 000 digits: saturating read
            ("0E+999999999999", "PRICE_NOT_POSITIVE"),
            ("-1E+999999999999", "PRICE_NOT_POSITIVE"),
            ("-0E-999999999999", "PRICE_NOT_POSITIVE"),
            (new string('9', 900_000) + ".0", "PRICE_PRECISION_UNSUPPORTED"),        // 900 000 digits, still inside the 1 MiB cap
            ("0." + new string('0', 900_000) + "5", "PRICE_PRECISION_UNSUPPORTED"),
        };
        var stopwatch = Stopwatch.StartNew();
        foreach (var (lexeme, warning) in cases)
        {
            var result = await ReadOneAsync("\"price\":" + lexeme);
            result.Outcome.Should().Be(MarketplaceListingReadOutcome.FOUND);
            result.Facts!.ObservedPrice.Should().BeNull(); result.Facts.NormalizationWarnings.Should().Equal(warning);
        }
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the work is linear in the lexeme length");
    }

    [Theory]
    [InlineData("1" + "E+00000000000000000000000000000000000002", "100.00")]     // 38 leading zeros in the exponent are ignored
    [InlineData("1E+0000000000000000000000000000000000000", "1.00")]            // exponent 0 spelled with many zeros
    public async Task PR_3_leading_zeros_of_the_exponent_never_change_its_value(string lexeme, string expected)
    {
        var result = await ReadOneAsync("\"price\":" + lexeme);
        result.Facts!.ObservedPrice.Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture)); result.Facts.NormalizationWarnings.Should().BeEmpty();
    }

    // ============================================================ PR-4 — a batch of inexact prices never contaminates its valid peer

    [Fact]
    public async Task PR_4_twenty_inexact_prices_and_one_valid_one_give_21_FOUND_20_null_and_the_exact_peer()
    {
        var inexact = new[] { "1.00000000000000000000000000001", "9999999999999999.9900000000000001", "0.001", "10.001", "1E-400", "1E17", "79228162514264337593543950335", "1E400", "12345678901234567.8", "0.123" };
        var elements = new List<string>(); var ids = new List<string>();
        for (var i = 0; i < 20; i++) { var id = "MLB-X" + i; ids.Add(id); elements.Add(Element(id, "\"price\":" + inexact[i % inexact.Length])); }
        ids.Add("MLB-PEER"); elements.Add(Element("MLB-PEER", "\"price\":19.90"));
        var results = await ReadAsync("[" + string.Join(",", elements) + "]", ids.ToArray());
        results.Should().HaveCount(21).And.OnlyContain(r => r.Outcome == MarketplaceListingReadOutcome.FOUND);
        results.Count(r => r.Facts!.ObservedPrice is null).Should().Be(20);
        results.Where(r => r.Facts!.ObservedPrice is null).Should().OnlyContain(r => r.Facts!.NormalizationWarnings.Count == 1 && r.Facts.NormalizationWarnings[0] == "PRICE_PRECISION_UNSUPPORTED");
        results.Single(r => r.ExternalListingId == "MLB-PEER").Facts!.ObservedPrice.Should().Be(19.90m);
    }

    // ============================================================ PR-6 — the same price has the same fingerprint, however it was spelled

    [Fact]
    public async Task PR_6_equivalent_spellings_of_one_price_yield_equal_normalized_values_and_equal_observation_fingerprints()
    {
        var spellings = new[] { "10.10", "10.100", "1.01E1", "101E-1", "10.1", "10.1000000000" };
        var fingerprints = new HashSet<string>(); var values = new HashSet<decimal>();
        foreach (var spelling in spellings)
        {
            var facts = (await ReadOneAsync("\"price\":" + spelling)).Facts!;
            values.Add(facts.ObservedPrice!.Value); fingerprints.Add(MarketplaceListingFingerprint.ComputeV2(facts));
            facts.ObservedPrice!.Value.Scale.Should().Be(2, spelling);
        }
        values.Should().ContainSingle().Which.Should().Be(10.10m); fingerprints.Should().ContainSingle("equal prices must not append a spurious observation");
    }
}
