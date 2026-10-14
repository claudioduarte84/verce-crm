using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Options;
using Verce.Infrastructure.Marketplaces;
using Verce.Infrastructure.Marketplaces.MercadoLivre;
using Verce.Modules.Commerce;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// M068 — adversarial complements to the M067 material suite, written AFTER reading the M067 code and tests. They close the gaps the review found: (1) the contract's character
/// classes that no M067 fixture used (tab, DEL, C1 control, NBSP, a VALID surrogate pair, an empty / numeric user_id, BOM, trailing data, comments, depth bomb), (2) zero TCP
/// connections on the REAL HTTP stack for bulk, probe AND refresh (M067 proved it for the scan only), (3) an INDEPENDENT oracle (JsonNode + own predicate, not the code under test)
/// over a structured generator, and (4) the duplicate-key rule.
/// </summary>
public sealed class MercadoLivreConnectorM068AdversarialTests
{
    private const string Canary = MercadoLivreConnectorM067MaterialTests.Canary;
    private enum Op { Scan, Bulk, Inspect }

    private static byte[] Doc(string accessRaw, string refreshRaw = "\"r\"", string userRaw = "\"777\"") =>
        Encoding.UTF8.GetBytes("{\"v\":1,\"access_token\":" + accessRaw + ",\"refresh_token\":" + refreshRaw + ",\"user_id\":" + userRaw + "}");

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Wire(HttpRequestMessage request, List<string?> auth)
    {
        lock (auth) auth.Add(request.Headers.Authorization?.ToString());
        return request.RequestUri!.AbsolutePath switch { "/users/me" => Json(HttpStatusCode.OK, """{"id":777,"site_id":"MLB"}"""), "/items/bulk" => Json(HttpStatusCode.OK, "[]"), _ => Json(HttpStatusCode.OK, """{"results":[]}""") };
    }

    private static Task Invoke(AdapterRig rig, Op op, byte[] secret) => op switch
    {
        Op.Scan => rig.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", secret, null), CancellationToken.None),
        Op.Bulk => rig.Connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", secret, ["MLB1"]), CancellationToken.None),
        _ => rig.Connector.InspectAsync(new MarketplaceAccountInspectionRequest(Guid.NewGuid(), MercadoLivreConnector.Code, secret), CancellationToken.None),
    };

    private static string Nested(int depth) => new string('[', depth) + new string(']', depth);

    /// <summary>Material the contract (ADR-0030 §1) must refuse — classes the M067 matrix did not contain.</summary>
    private static readonly (string Id, string What, byte[] Material)[] Unusable =
    [
        ("T-01", "tab in access_token", Doc("\"" + Canary + "\\t\"")),
        ("T-02", "DEL (U+007F) in access_token", Doc("\"" + Canary + "\\u007f\"")),
        ("T-03", "C1 control U+0080 in access_token", Doc("\"" + Canary + "\\u0080\"")),
        ("T-04", "no-break space U+00A0 in access_token", Doc("\"" + Canary + "\\u00a0\"")),
        ("T-05", "a VALID surrogate pair (U+1F600) in access_token", Doc("\"" + Canary + "\\ud83d\\ude00\"")),
        ("T-06", "user_id empty string", Doc("\"" + Canary + "\"", "\"r\"", "\"\"")),
        ("T-07", "user_id is a number", Doc("\"" + Canary + "\"", "\"r\"", "777")),
        ("T-08", "UTF-8 BOM before the document", [0xEF, 0xBB, 0xBF, .. Doc("\"" + Canary + "\"")]),
        ("T-09", "nesting deeper than the parser limit", Encoding.UTF8.GetBytes("{\"v\":1,\"access_token\":\"" + Canary + "\",\"refresh_token\":\"r\",\"user_id\":\"777\",\"extra\":" + Nested(70) + "}")),
        ("T-10", "trailing garbage after the document", [.. Doc("\"" + Canary + "\""), (byte)'x']),
        ("T-11", "two documents", [.. Doc("\"" + Canary + "\""), .. Doc("\"" + Canary + "\"")]),
        ("T-12", "access_token is JSON null", Doc("null")),
        ("T-13", "access_token is an array", Doc("[\"" + Canary + "\"]")),
        ("T-14", "comment inside the document", Encoding.UTF8.GetBytes("{\"v\":1,/* " + Canary + " */\"access_token\":\"tok\",\"refresh_token\":\"r\",\"user_id\":\"777\"}")),
        ("T-15", "trailing comma", Encoding.UTF8.GetBytes("{\"v\":1,\"access_token\":\"" + Canary + "\",\"refresh_token\":\"r\",\"user_id\":\"777\",}")),
        ("T-16", "user_id with a lone LOW surrogate", Doc("\"" + Canary + "\"", "\"r\"", "\"7\\udc00\"")),
        ("T-17", "access_token of exactly 4097 characters from the middle of the range", Doc("\"" + new string('~', 4097) + "\"")),
    ];

    private static readonly (string Id, string What, byte[] Material, string Token)[] Usable =
    [
        ("V-01", "escaped letters decode to the real token", Doc("\"\\u0041BC\\u007e\""), "ABC~"),
        ("V-02", "a 1 MiB unrelated field next to a valid token", Encoding.UTF8.GetBytes("{\"v\":1,\"access_token\":\"big-doc-token\",\"refresh_token\":\"r\",\"user_id\":\"777\",\"blob\":\"" + new string('x', 1 << 20) + "\"}"), "big-doc-token"),
        ("V-03", "escaped slash", Doc("\"a\\/b\""), "a/b"),
        ("V-04", "pretty-printed document, keys reordered", Encoding.UTF8.GetBytes("{\n  \"user_id\": \"777\",\n  \"refresh_token\": \"r\",\n  \"access_token\": \"pretty-token\",\n  \"v\": 1\n}\n"), "pretty-token"),
        ("V-05", "unknown fields of every JSON type", Encoding.UTF8.GetBytes("{\"a\":1,\"b\":true,\"c\":null,\"d\":[1,{\"e\":2}],\"access_token\":\"mixed-token\",\"refresh_token\":\"r\",\"user_id\":\"777\"}"), "mixed-token"),
        ("V-06", "exactly 4095 characters", Doc("\"" + new string('Z', 4095) + "\""), new string('Z', 4095)),
        ("V-07", "escaped quote and backslash (visible ASCII)", Doc("\"a\\\"b\\\\c\""), "a\"b\\c"),
        ("V-08", "unrelated field with a lone surrogate escape is never read", Encoding.UTF8.GetBytes("{\"note\":\"\\ud800\",\"access_token\":\"note-token\",\"refresh_token\":\"r\",\"user_id\":\"777\"}"), "note-token"),
    ];

    // ============================================================ 1. the character classes the M067 matrix left out

    [Fact]
    public async Task ADV_1_the_contract_classes_missing_from_the_M067_matrix_are_refused_before_any_request_and_valid_look_alikes_are_sent()
    {
        var rows = new List<object>();
        using (new AssertionScope())
        {
            foreach (var (id, what, material) in Unusable)
                foreach (var op in Enum.GetValues<Op>())
                {
                    var auth = new List<string?>(); var rig = AdapterRig.Create((r, _) => Wire(r, auth));
                    var thrown = await Record.ExceptionAsync(() => Invoke(rig, op, material)); var label = $"{id} ({what}) via {op}";
                    thrown.Should().BeOfType<MarketplaceStoredCredentialUnusableException>(label);
                    rig.Requests.Should().BeEmpty(label); auth.Should().BeEmpty(label);
                    if (thrown is not null) (thrown.ToString() + string.Join("\n", rig.Logger.Messages)).Should().NotContain(Canary, label);
                    rows.Add(new { fixture = id, what, operation = op.ToString(), exception = thrown?.GetType().Name, httpRequests = rig.Requests.Count });
                }
            foreach (var (id, what, material, token) in Usable)
                foreach (var op in Enum.GetValues<Op>())
                {
                    var auth = new List<string?>(); var rig = AdapterRig.Create((r, _) => Wire(r, auth));
                    var thrown = await Record.ExceptionAsync(() => Invoke(rig, op, material)); var label = $"{id} ({what}) via {op}";
                    thrown.Should().BeNull(label); auth.Should().OnlyContain(a => a == "Bearer " + token, label);
                    rows.Add(new { fixture = id, what, operation = op.ToString(), exception = (string?)null, httpRequests = rig.Requests.Count });
                }
        }
        Evidence.Record("adv1-matrix", new { unusable = Unusable.Length, usable = Usable.Length, operations = 3, cases = rows.Count, results = rows });
    }

    [Fact]
    public async Task ADV_2_refresh_needs_only_the_three_decodable_fields_a_non_ASCII_or_pair_access_token_still_refreshes_but_user_id_and_JSON_defects_do_not()
    {
        var expectations = new (string Id, bool Posts)[]
        {
            ("T-01", true), ("T-02", true), ("T-03", true), ("T-04", true), ("T-05", true),        // the access token's characters are irrelevant to a refresh
            ("T-06", false), ("T-07", false), ("T-08", false), ("T-09", false), ("T-10", false), ("T-11", false), ("T-12", false), ("T-13", false), ("T-14", false), ("T-15", false), ("T-16", false),
            ("T-17", true),
        };
        using (new AssertionScope())
            foreach (var (id, posts) in expectations)
            {
                var material = Unusable.Single(f => f.Id == id).Material;
                var rig = AdapterRig.Create((_, _) => Json(HttpStatusCode.OK, """{"access_token":"new-access","token_type":"bearer","expires_in":60,"user_id":777,"refresh_token":"new-refresh"}"""));
                var thrown = await Record.ExceptionAsync(() => rig.Connector.RefreshAsync(new MarketplaceRefreshRequest(Guid.NewGuid(), MercadoLivreConnector.Code, material), CancellationToken.None));
                if (posts) { thrown.Should().BeNull(id); rig.Requests.Should().ContainSingle(id); }
                else { thrown.Should().BeOfType<MarketplaceRequestNotSentException>(id); (thrown?.Message).Should().Be("REFRESH_TOKEN_UNAVAILABLE", id); rig.Requests.Should().BeEmpty(id); }
            }
    }

    [Fact]
    public async Task ADV_3_duplicate_property_names_never_put_a_non_transmissible_bearer_on_the_wire()
    {
        // whichever occurrence the parser exposes, the bearer on the wire must be transmissible; a refusal is equally fine.
        byte[] FirstGoodSecondBad = Encoding.UTF8.GetBytes("{\"access_token\":\"good-token\",\"access_token\":\"bad\\r\\nX: " + Canary + "\",\"refresh_token\":\"r\",\"user_id\":\"777\"}");
        byte[] FirstBadSecondGood = Encoding.UTF8.GetBytes("{\"access_token\":\"bad\\r\\nX: " + Canary + "\",\"access_token\":\"good-token\",\"refresh_token\":\"r\",\"user_id\":\"777\"}");
        var outcomes = new List<object>();
        foreach (var (name, material) in new[] { ("first good, second bad", FirstGoodSecondBad), ("first bad, second good", FirstBadSecondGood) })
        {
            var auth = new List<string?>(); var rig = AdapterRig.Create((r, _) => Wire(r, auth));
            var thrown = await Record.ExceptionAsync(() => Invoke(rig, Op.Scan, material));
            if (thrown is null) auth.Should().ContainSingle().Which.Should().MatchRegex("^Bearer [\\x21-\\x7E]{1,4096}$", name); else thrown.Should().BeOfType<MarketplaceStoredCredentialUnusableException>(name);
            auth.Should().NotContain(a => a != null && a.Contains(Canary));
            outcomes.Add(new { name, sent = thrown is null, bearer = auth.FirstOrDefault() });
        }
        Evidence.Record("adv3-duplicate-keys", new { outcomes });
    }

    // ============================================================ 2. zero TCP connections on the REAL stack for bulk, probe and refresh

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0); private readonly CancellationTokenSource _stop = new(); private readonly Task _loop; private int _connections;
        public int Connections => Volatile.Read(ref _connections); public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public LoopbackServer() { _listener.Start(); _loop = Task.Run(AcceptAsync); }
        private async Task AcceptAsync()
        {
            try { while (!_stop.IsCancellationRequested) { var client = await _listener.AcceptTcpClientAsync(_stop.Token); Interlocked.Increment(ref _connections); _ = Task.Run(() => ServeAsync(client)); } }
            catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
        }
        private async Task ServeAsync(TcpClient client)
        {
            using (client)
                try
                {
                    var stream = client.GetStream(); var buffer = new byte[8192]; var seen = new StringBuilder();
                    while (!seen.ToString().Contains("\r\n\r\n")) { var n = await stream.ReadAsync(buffer, _stop.Token); if (n == 0) return; seen.Append(Encoding.Latin1.GetString(buffer, 0, n)); }
                    const string body = "{\"results\":[]}";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n" + body), _stop.Token);
                }
                catch (Exception) { }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); try { await _loop; } catch { } _stop.Dispose(); }
    }

    private sealed class RewriteToLoopback(int port, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new UriBuilder(request.RequestUri!) { Scheme = Uri.UriSchemeHttp, Host = "127.0.0.1", Port = port }.Uri;
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class OneClient(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }

    [Fact]
    public async Task ADV_4_real_stack_unusable_material_opens_zero_TCP_connections_for_scan_bulk_probe_and_refresh_while_usable_material_does()
    {
        await using var server = new LoopbackServer();
        var options = Options.Create(new MercadoLivreOptions { Enabled = true, ClientId = "900000001", RedirectUri = "https://host.example/api/commerce/marketplace-authorizations/MERCADO_LIVRE/callback", ResolvedClientSecret = "m068-client-secret" });
        var clock = new TestClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z")); var sender = new MarketplaceHttpSender(Microsoft.Extensions.Logging.Abstractions.NullLogger<MarketplaceHttpSender>.Instance);
        var reader = new MarketplaceSafeReadExecutor(sender, new SystemMarketplaceDelay(), new FixedJitter(TimeSpan.Zero), clock);
        using var client = new HttpClient(new RewriteToLoopback(server.Port, new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })) { Timeout = Timeout.InfiniteTimeSpan };
        var connector = new MercadoLivreConnector(new OneClient(client), sender, reader, options, clock);
        var rows = new List<object>();
        using (new AssertionScope())
        {
            foreach (var (id, what, material) in Unusable)
            {
                var before = server.Connections;
                foreach (var thrown in new[]
                {
                    await Record.ExceptionAsync(() => connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", material, null), CancellationToken.None)),
                    await Record.ExceptionAsync(() => connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", material, ["MLB1"]), CancellationToken.None)),
                    await Record.ExceptionAsync(() => connector.InspectAsync(new MarketplaceAccountInspectionRequest(Guid.NewGuid(), MercadoLivreConnector.Code, material), CancellationToken.None)),
                })
                    thrown.Should().BeOfType<MarketplaceStoredCredentialUnusableException>($"{id} ({what})");
                await Task.Delay(30); server.Connections.Should().Be(before, $"{id} ({what}): scan, bulk and probe together opened no TCP connection");
                rows.Add(new { fixture = id, operations = "scan+bulk+inspect", tcpConnectionsOpened = server.Connections - before });
            }
            // refresh: the unparseable ones open nothing; those with a usable refresh token open exactly one (the POST)
            foreach (var id in new[] { "T-06", "T-07", "T-08", "T-09", "T-10", "T-11", "T-12", "T-13", "T-14", "T-15", "T-16" })
            {
                var before = server.Connections;
                var thrown = await Record.ExceptionAsync(() => connector.RefreshAsync(new MarketplaceRefreshRequest(Guid.NewGuid(), MercadoLivreConnector.Code, Unusable.Single(f => f.Id == id).Material), CancellationToken.None));
                thrown.Should().BeOfType<MarketplaceRequestNotSentException>(id); await Task.Delay(30); server.Connections.Should().Be(before, $"{id}: refresh sent nothing");
                rows.Add(new { fixture = id, operations = "refresh", tcpConnectionsOpened = server.Connections - before });
            }
            var baseline = server.Connections;
            foreach (var (id, _, material, _) in Usable)
            {
                var before = server.Connections;
                await connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", material, null), CancellationToken.None);
                server.Connections.Should().Be(before + 1, $"{id}: control — usable material makes exactly one real request");
            }
            server.Connections.Should().Be(baseline + Usable.Length);
        }
        Evidence.Record("adv4-real-stack-tcp", new { fixtures = Unusable.Length, results = rows, controlConnections = Usable.Length });
    }

    // ============================================================ 3. an independent oracle over a structured generator

    private sealed record Verdict(bool Bearer, bool Refresh, bool IdentityOk);

    /// <summary>The ADR-0030 §1 predicate re-implemented with JsonNode and plain C# (not the code under test). A field that is present as a string must decode; anything else counts as absent.</summary>
    private static Verdict? Oracle(byte[] material)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(material); } catch (JsonException) { return new Verdict(false, false, false); } catch (ArgumentException) { return null; /* duplicate keys: judged by ADV_3 */ }
        if (node is not JsonObject root) return new Verdict(false, false, false);
        string? Field(string name)
        {
            if (!root.TryGetPropertyValue(name, out var value) || value is not JsonValue jv) return null;
            try { return jv.TryGetValue<string>(out var s) ? s : null; } catch (InvalidOperationException) { throw new FormatException(); }
        }
        string? access, refresh, user;
        try { access = Field("access_token"); refresh = Field("refresh_token"); user = Field("user_id"); } catch (FormatException) { return new Verdict(false, false, false); }
        var userText = user ?? string.Empty; var accessText = access ?? string.Empty;
        var bearer = userText.Length > 0 && accessText.Length is >= 1 and <= 4096 && accessText.All(c => c >= 0x21 && c <= 0x7E);
        var refreshOk = accessText.Length > 0 && userText.Length > 0 && !string.IsNullOrEmpty(refresh);
        return new Verdict(bearer, refreshOk, Regex.IsMatch(userText, "^[1-9][0-9]{0,19}$"));
    }

    private static readonly string[] AccessPool =
    [
        "\"\"", "\"tok\"", "\"APP_USR-1234-abcd\"", "\"a b\"", "\"a\\tb\"", "\"a\\r\\nb\"", "\"a\\u0000b\"", "\"a\\u007fb\"", "\"a\\u0080b\"", "\"a\\u00a0b\"", "\"ação\"", "\"a\\ud800b\"", "\"a\\udc00b\"",
        "\"a\\ud83d\\ude00b\"", "\"~\"", "\"!\"", "\"\\u0041\"", "null", "123", "[]", "{}", "true", "\"" + "A" + "\"", "\"a\\\"b\"", "\"a\\\\b\"",
    ];
    private static readonly string[] RefreshPool = ["\"r\"", "\"\"", "null", "42", "\"r\\ud800\"", "\"r\\ud83d\\ude00\"", "\"long-refresh-token-value\""];
    private static readonly string[] UserPool = ["\"777\"", "\"\"", "\"abc\"", "\"0777\"", "\"1\"", "\"99999999999999999999\"", "\"123456789012345678901\"", "777", "null", "\"7\\ud800\"", "\"7 7\"", "\"\\u0037\""];

    [Fact]
    public async Task ADV_5_the_connector_agrees_with_an_independent_oracle_on_30000_structured_materials_for_bearer_refresh_and_identity()
    {
        var random = new Random(068); var agree = 0; var skipped = 0; var bearerSent = 0; var bearerRefused = 0; var refreshPosted = 0; var refreshRefused = 0; var identityRefused = 0; var disagreements = new List<string>();
        for (var i = 0; i < 30_000; i++)
        {
            string access = AccessPool[random.Next(AccessPool.Length)];
            if (random.Next(12) == 0) access = "\"" + new string('A', random.Next(4090, 4100)) + "\"";      // around the length bound
            var parts = new List<string> { "\"v\":1" };
            if (random.Next(10) != 0) parts.Add("\"access_token\":" + access);
            if (random.Next(8) != 0) parts.Add("\"refresh_token\":" + RefreshPool[random.Next(RefreshPool.Length)]);
            if (random.Next(10) != 0) parts.Add("\"user_id\":" + UserPool[random.Next(UserPool.Length)]);
            if (random.Next(4) == 0) parts.Add("\"extra\":" + (random.Next(2) == 0 ? "{\"n\":[1,2,{\"z\":null}]}" : "\"\\ud800\""));
            for (var k = parts.Count - 1; k > 0; k--) { var j = random.Next(k + 1); (parts[k], parts[j]) = (parts[j], parts[k]); }
            var text = (random.Next(6) == 0 ? "  " : "") + "{" + string.Join(random.Next(5) == 0 ? " , " : ",", parts) + "}" + (random.Next(15) == 0 ? "x" : "");
            var material = Encoding.UTF8.GetBytes(text);
            var oracle = Oracle(material); if (oracle is null) { skipped++; continue; }

            var auth = new List<string?>(); var rig = AdapterRig.Create((r, _) => Wire(r, auth));
            var scan = await Record.ExceptionAsync(() => Invoke(rig, Op.Scan, material)); var sent = scan is null;
            if (!sent && scan is not MarketplaceStoredCredentialUnusableException) disagreements.Add($"#{i} scan threw {scan!.GetType().Name}");
            if (sent != oracle.Bearer) disagreements.Add($"#{i} bearer: connector sent={sent} oracle={oracle.Bearer}: {Describe(text)}");
            if (sent) { bearerSent++; auth.Should().ContainSingle().Which.Should().MatchRegex("^Bearer [\\x21-\\x7E]{1,4096}$"); } else bearerRefused++;

            var posts = AdapterRig.Create((_, _) => Json(HttpStatusCode.OK, """{"access_token":"new-access","token_type":"bearer","expires_in":60,"user_id":777,"refresh_token":"new-refresh"}"""));
            var refresh = await Record.ExceptionAsync(() => posts.Connector.RefreshAsync(new MarketplaceRefreshRequest(Guid.NewGuid(), MercadoLivreConnector.Code, material), CancellationToken.None));
            var expected = !oracle.Refresh ? "REFRESH_TOKEN_UNAVAILABLE" : !oracle.IdentityOk ? "STORED_IDENTITY_INVALID" : null;
            // a POST that was sent may still end in a provider-shaped failure (the scripted token's user_id differs from the stored one): what matters here is whether the request left
            var actual = refresh is MarketplaceRequestNotSentException ns ? ns.Message : posts.Requests.Count == 1 && (refresh is null || refresh is MarketplaceProviderCallException) ? null : "other:" + (refresh?.GetType().Name ?? "no-exception-no-request");
            if (actual != expected) disagreements.Add($"#{i} refresh: connector={actual ?? "POST"} oracle={expected ?? "POST"}: {Describe(text)}");
            if (expected is null) { refreshPosted++; posts.Requests.Should().ContainSingle(); } else { refreshRefused++; posts.Requests.Should().BeEmpty(); if (expected == "STORED_IDENTITY_INVALID") identityRefused++; }
            if (disagreements.Count == 0) agree++;
        }
        Evidence.Record("adv5-oracle", new { seed = 68, inputs = 30_000, skippedDuplicateKeys = skipped, bearerSent, bearerRefused, refreshPosted, refreshRefused, identityRefused, disagreements = disagreements.Take(20) });
        disagreements.Should().BeEmpty("the connector and the independent oracle must agree on every input");
        bearerSent.Should().BeGreaterThan(500); bearerRefused.Should().BeGreaterThan(5_000); refreshPosted.Should().BeGreaterThan(500); identityRefused.Should().BeGreaterThan(100);
    }

    private static string Describe(string text) => text.Length <= 220 ? text : text[..200] + $"…({text.Length} chars)";
}
