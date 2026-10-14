using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Options;
using Verce.Infrastructure.Marketplaces;
using Verce.Infrastructure.Marketplaces.MercadoLivre;
using Verce.Modules.Commerce;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// M067 / C067-01 + C067-02 — ADR-0030 §1/§2/§3. A protected-store SUCCESS proves authenticity, not usability: the adapter that owns the v1 secret format must refuse unusable material
/// BEFORE it builds any request, with the typed message-less <see cref="MarketplaceStoredCredentialUnusableException"/> (never a provider failure), and zero requests / zero connections.
/// MAT-1 refusal matrix (18 fixtures × scan, bulk, owner probe) · MAT-2 usable material really sends the exact bearer · MAT-3/4/5 refresh (ADR-0025 §F.3 rows unchanged) ·
/// MAT-6 total-reader fuzz · plus the real-stack proof (SocketsHttpHandler over loopback) that an unusable bearer never opens a TCP connection.
/// </summary>
public sealed class MercadoLivreConnectorM067MaterialTests
{
    internal const string Canary = "CANARY-M067";

    private static byte[] Doc(string accessRaw, string refreshRaw = "\"r\"", string userRaw = "\"777\"") =>
        Encoding.UTF8.GetBytes("{\"v\":1,\"access_token\":" + accessRaw + ",\"refresh_token\":" + refreshRaw + ",\"user_id\":" + userRaw + "}");

    private static byte[] Concat(string head, byte[] middle, string tail) => [.. Encoding.UTF8.GetBytes(head), .. middle, .. Encoding.UTF8.GetBytes(tail)];

    /// <summary>The 18 fixtures of the 066 matrix (M-01..M-18) plus M-19 (no refresh_token). Every one is unusable as a bearer; each carries the canary wherever the format allows it.</summary>
    internal static IReadOnlyList<(string Id, string What, byte[] Material)> Unusable { get; } =
    [
        ("M-01", "empty (0 bytes)", []),
        ("M-02", "not JSON", Encoding.UTF8.GetBytes("{not-json " + Canary)),
        ("M-03", "root is an array", Encoding.UTF8.GetBytes("[\"" + Canary + "\"]")),
        ("M-04", "root is a string", Encoding.UTF8.GetBytes("\"" + Canary + "\"")),
        ("M-05", "no access_token", Encoding.UTF8.GetBytes("{\"v\":1,\"note\":\"" + Canary + "\",\"refresh_token\":\"r\",\"user_id\":\"777\"}")),
        ("M-06", "no user_id", Encoding.UTF8.GetBytes("{\"v\":1,\"access_token\":\"" + Canary + "\",\"refresh_token\":\"r\"}")),
        ("M-07", "access_token is a number", Encoding.UTF8.GetBytes("{\"v\":1,\"access_token\":12345,\"note\":\"" + Canary + "\",\"refresh_token\":\"r\",\"user_id\":\"777\"}")),
        ("M-08", "access_token with a lone HIGH surrogate escape", Doc("\"" + Canary + "\\ud800\"")),
        ("M-09", "access_token with a lone LOW surrogate escape", Doc("\"" + Canary + "\\udc00\"")),
        ("M-10", "refresh_token with a lone high surrogate escape", Doc("\"" + Canary + "\"", "\"r\\ud800\"")),
        ("M-11", "user_id with a lone high surrogate escape", Doc("\"" + Canary + "\"", "\"r\"", "\"777\\udbff\"")),
        ("M-12", "access_token with raw invalid UTF-8 bytes", Concat("{\"v\":1,\"access_token\":\"" + Canary, [0xC3, 0x28], "\",\"refresh_token\":\"r\",\"user_id\":\"777\"}")),
        ("M-13", "access_token with CR LF", Doc("\"" + Canary + "\\r\\nX-Injected: 1\"")),
        ("M-14", "access_token with NUL", Doc("\"" + Canary + "\\u0000\"")),
        ("M-15", "access_token with a non-ASCII character", Doc("\"" + Canary + "-ação\"")),
        ("M-16", "access_token with a space", Doc("\"" + Canary + " M067\"")),
        ("M-17", "access_token of 4097 visible characters", Doc("\"" + Canary + new string('A', 4097 - Canary.Length) + "\"")),
        ("M-18", "access_token empty", Doc("\"\"", "\"" + Canary + "\"")),
        ("M-19", "no refresh_token and no usable access_token", Encoding.UTF8.GetBytes("{\"v\":1,\"access_token\":\"\",\"user_id\":\"777\",\"note\":\"" + Canary + "\"}")),
    ];

    /// <summary>P-1..P-5: usable material, including both ends of the character range, the length limits and tolerated shape variations.</summary>
    internal static IReadOnlyList<(string Id, string Token, byte[] Material)> Usable { get; } =
    [
        ("P-1", "APP_USR-123456-abcdef-777", Doc("\"APP_USR-123456-abcdef-777\"")),
        ("P-2", new string('B', 4096), Doc("\"" + new string('B', 4096) + "\"")),
        ("P-3a", "!", Doc("\"!\"")),
        ("P-3b", "~", Doc("\"~\"")),
        ("P-3c", "!~", Doc("\"!~\"")),
        ("P-4", "no-version-field", Encoding.UTF8.GetBytes("{\"access_token\":\"no-version-field\",\"refresh_token\":\"r\",\"user_id\":\"777\"}")),
        ("P-5", "extra-field-token", Encoding.UTF8.GetBytes("{\"v\":1,\"access_token\":\"extra-field-token\",\"refresh_token\":\"r\",\"user_id\":\"777\",\"extra\":{\"a\":[1,2,{\"b\":null}]}}")),
    ];

    private enum Op { Scan, Bulk, Inspect }

    private static Task Invoke(AdapterRig rig, Op op, byte[] secret) => op switch
    {
        Op.Scan => rig.Connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", secret, null), CancellationToken.None),
        Op.Bulk => rig.Connector.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(Guid.NewGuid(), "777", secret, ["MLB1"]), CancellationToken.None),
        _ => rig.Connector.InspectAsync(new MarketplaceAccountInspectionRequest(Guid.NewGuid(), MercadoLivreConnector.Code, secret), CancellationToken.None),
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Wire(HttpRequestMessage request, List<string?> authorization)
    {
        lock (authorization) authorization.Add(request.Headers.Authorization?.ToString());
        return request.RequestUri!.AbsolutePath switch
        {
            "/users/me" => Json(HttpStatusCode.OK, """{"id":777,"site_id":"MLB"}"""),
            "/items/bulk" => Json(HttpStatusCode.OK, "[]"),
            _ => Json(HttpStatusCode.OK, """{"results":[]}"""),
        };
    }

    private static void AssertNothingLeaked(AdapterRig rig, Exception exception, byte[] material)
    {
        var all = exception.ToString() + "\n" + string.Join("\n", rig.Logger.Messages);
        all.Should().NotContain(Canary);
        var text = Encoding.UTF8.GetString(material);
        foreach (var token in new[] { "ação", "X-Injected" }) if (text.Contains(token)) all.Should().NotContain(token);
    }

    // ============================================================ MAT-1 — the refusal matrix (zero requests, typed, no leak)

    [Fact]
    public async Task MAT_1_unusable_material_is_refused_before_any_request_by_scan_bulk_and_probe_with_a_typed_message_less_exception()
    {
        var matrix = new List<object>();
        using (new AssertionScope())
            foreach (var (id, what, material) in Unusable)
                foreach (var op in Enum.GetValues<Op>())
                {
                    var auth = new List<string?>();
                    var rig = AdapterRig.Create((r, _) => Wire(r, auth));
                    var thrown = await Record.ExceptionAsync(() => Invoke(rig, op, material));
                    var label = $"{id} ({what}) via {op}";
                    thrown.Should().BeOfType<MarketplaceStoredCredentialUnusableException>(label);
                    if (thrown is MarketplaceStoredCredentialUnusableException typed)
                    {
                        typed.Message.Should().Be("STORED_CREDENTIAL_UNUSABLE", label); typed.InnerException.Should().BeNull(label);
                        AssertNothingLeaked(rig, typed, material);
                    }
                    rig.Requests.Should().BeEmpty(label + ": the handler must never be reached"); auth.Should().BeEmpty(label);
                    rig.Logger.Messages.Should().NotContain(m => m.Contains("MarketplaceProviderCall"), label + ": no attempt, so no provider-call event");
                    matrix.Add(new { fixture = id, what, operation = op.ToString(), exception = thrown?.GetType().Name, message = thrown?.Message, httpRequests = rig.Requests.Count, providerCallEvents = rig.Logger.Messages.Count(m => m.Contains("MarketplaceProviderCall")), retries = 0 });
                }
        Evidence.Record("mat1-matrix", new { fixtures = Unusable.Count, operations = 3, cases = matrix.Count, results = matrix });
    }

    // ============================================================ MAT-2 — usable material sends exactly the stored bearer

    [Fact]
    public async Task MAT_2_usable_material_is_sent_with_the_exact_bearer_by_scan_bulk_and_probe()
    {
        var matrix = new List<object>();
        using (new AssertionScope())
            foreach (var (id, token, material) in Usable)
                foreach (var op in Enum.GetValues<Op>())
                {
                    var auth = new List<string?>();
                    var rig = AdapterRig.Create((r, _) => Wire(r, auth));
                    var thrown = await Record.ExceptionAsync(() => Invoke(rig, op, material));
                    thrown.Should().BeNull($"{id} via {op}");
                    rig.Requests.Should().NotBeEmpty($"{id} via {op}");
                    auth.Should().OnlyContain(a => a == "Bearer " + token, $"{id} via {op}");
                    matrix.Add(new { fixture = id, operation = op.ToString(), httpRequests = rig.Requests.Count, tokenLength = token.Length });
                }
        Evidence.Record("mat2-matrix", new { cases = matrix.Count, results = matrix });
    }

    [Fact]
    public async Task MAT_2b_every_single_printable_ASCII_character_is_a_usable_bearer_the_HTTP_stack_accepts()
    {
        using (new AssertionScope())
            for (var c = 0x21; c <= 0x7E; c++)
            {
                var token = "t" + (char)c + "t"; var json = new System.Text.Json.Nodes.JsonObject { ["v"] = 1, ["access_token"] = token, ["refresh_token"] = "r", ["user_id"] = "777" }.ToJsonString();
                var auth = new List<string?>(); var rig = AdapterRig.Create((r, _) => Wire(r, auth));
                var thrown = await Record.ExceptionAsync(() => Invoke(rig, Op.Scan, Encoding.UTF8.GetBytes(json)));
                thrown.Should().BeNull($"U+{c:X4} is inside the contract range and must not break the request factory");
                auth.Should().ContainSingle().Which.Should().Be("Bearer " + token);
            }
        await Task.CompletedTask;
    }

    // ============================================================ MAT-3 / MAT-4 / MAT-5 — refresh keeps its ADR-0025 §F.3 rows

    private static HttpResponseMessage TokenOk() => Json(HttpStatusCode.OK, """{"access_token":"new-access","token_type":"bearer","expires_in":60,"user_id":777,"refresh_token":"new-refresh"}""");

    [Fact]
    public async Task MAT_3_refresh_with_unparseable_or_refresh_less_material_is_proven_NOT_SENT_REFRESH_TOKEN_UNAVAILABLE_with_zero_POST()
    {
        var matrix = new List<object>();
        var fixtures = Unusable.Where(f => f.Id is not ("M-13" or "M-14" or "M-15" or "M-16" or "M-17")).ToList();   // M-13..M-17 keep a valid refresh_token: MAT-4
        using (new AssertionScope())
            foreach (var (id, what, material) in fixtures)
            {
                var rig = AdapterRig.Create((_, _) => TokenOk());
                var thrown = await Record.ExceptionAsync(() => rig.Connector.RefreshAsync(new MarketplaceRefreshRequest(Guid.NewGuid(), MercadoLivreConnector.Code, material), CancellationToken.None));
                var label = $"{id} ({what})";
                thrown.Should().BeOfType<MarketplaceRequestNotSentException>(label);
                (thrown?.Message).Should().Be("REFRESH_TOKEN_UNAVAILABLE", label);
                rig.Requests.Should().BeEmpty(label + ": zero POST");
                if (thrown is not null) (thrown.ToString() + string.Join("\n", rig.Logger.Messages)).Should().NotContain(Canary, label);
                matrix.Add(new { fixture = id, what, exception = thrown?.GetType().Name, message = thrown?.Message, httpRequests = rig.Requests.Count });
            }
        // a surrogate pair is VALID text: a refresh token that is a pair is a legitimate refresh credential
        var pair = AdapterRig.Create((_, _) => TokenOk());
        (await Record.ExceptionAsync(() => pair.Connector.RefreshAsync(new MarketplaceRefreshRequest(Guid.NewGuid(), MercadoLivreConnector.Code, Doc("\"a\"", "\"\\ud83d\\ude00\"")), CancellationToken.None))).Should().BeNull();
        pair.Requests.Should().ContainSingle();
        Evidence.Record("mat3-matrix", new { cases = matrix.Count, results = matrix });
    }

    [Fact]
    public async Task MAT_4_refresh_does_not_need_a_transmissible_access_token_so_a_valid_refresh_token_still_makes_exactly_one_POST()
    {
        var matrix = new List<object>();
        using (new AssertionScope())
            foreach (var id in new[] { "M-13", "M-14", "M-15", "M-16", "M-17" })
            {
                var material = Unusable.Single(f => f.Id == id).Material;
                var rig = AdapterRig.Create((_, _) => TokenOk());
                var result = await rig.Connector.RefreshAsync(new MarketplaceRefreshRequest(Guid.NewGuid(), MercadoLivreConnector.Code, material), CancellationToken.None);
                rig.Requests.Should().ContainSingle(id); rig.Requests[0].Method.Should().Be(HttpMethod.Post);
                result.Should().NotBeNull();
                matrix.Add(new { fixture = id, httpPosts = rig.Requests.Count });
            }
        Evidence.Record("mat4-matrix", new { cases = matrix.Count, results = matrix });
    }

    [Fact]
    public async Task MAT_5_a_malformed_stored_identity_is_still_the_PS_A_pre_send_validation_STORED_IDENTITY_INVALID()
    {
        var rig = AdapterRig.Create((_, _) => TokenOk());
        var thrown = await Record.ExceptionAsync(() => rig.Connector.RefreshAsync(new MarketplaceRefreshRequest(Guid.NewGuid(), MercadoLivreConnector.Code, Doc("\"a\"", "\"r\"", "\"abc\"")), CancellationToken.None));
        thrown.Should().BeOfType<MarketplaceRequestNotSentException>().Which.Message.Should().Be("STORED_IDENTITY_INVALID");
        rig.Requests.Should().BeEmpty();
    }

    // ============================================================ MAT-6 — the reader is total (fuzz): only the typed refusal or a normal send, never a raw exception

    [Fact]
    public async Task MAT_6_the_stored_material_reader_is_total_over_24000_seeded_random_and_mutated_inputs()
    {
        var random = new Random(067); var refused = 0; var sent = 0; var typeCounts = new Dictionary<string, int>();
        byte[] valid = Doc("\"APP_USR-fuzz-token\"");
        string[] splices = ["\\ud800", "\\udc00", "\\ud83d\\ude00", "\\u0000", "\\r\\n", "\\\"", "\\\\", "\u00e7", "\ud83d\ude00", " ", "\t", "\u007f", "\u0080", "\ufffd", "\\ufffd", "\\u0041", "A", "~", "!", "\"", "}", "{", ",", ":"];
        for (var i = 0; i < 24_000; i++)
        {
            byte[] material;
            switch (random.Next(4))
            {
                case 0: material = new byte[random.Next(0, 200)]; random.NextBytes(material); break;
                case 1:
                    material = (byte[])valid.Clone();
                    for (var k = random.Next(1, 4); k > 0; k--) material[random.Next(material.Length)] = (byte)random.Next(256);
                    break;
                case 2:
                {
                    var text = Encoding.UTF8.GetString(valid); var at = random.Next(text.Length);
                    material = Encoding.UTF8.GetBytes(text.Insert(at, splices[random.Next(splices.Length)])); break;
                }
                default:
                {
                    var bytes = valid.ToList(); bytes.RemoveAt(random.Next(bytes.Count)); bytes.InsertRange(random.Next(bytes.Count + 1), Encoding.UTF8.GetBytes(splices[random.Next(splices.Length)])); material = [.. bytes]; break;
                }
            }
            var auth = new List<string?>(); var rig = AdapterRig.Create((r, _) => Wire(r, auth));
            var thrown = await Record.ExceptionAsync(() => Invoke(rig, Op.Scan, material));
            var name = thrown?.GetType().Name ?? "sent"; typeCounts[name] = typeCounts.GetValueOrDefault(name) + 1;
            if (thrown is null)
            {
                sent++;
                rig.Requests.Should().ContainSingle($"input {i}"); auth.Should().ContainSingle($"input {i}").Which.Should().MatchRegex("^Bearer [\\x21-\\x7E]{1,4096}$", $"input {i}: a sent bearer is always transmissible");
            }
            else
            {
                thrown.Should().BeOfType<MarketplaceStoredCredentialUnusableException>($"input {i}: the only permitted failure of material validation");
                rig.Requests.Should().BeEmpty($"input {i}"); refused++;
            }
        }
        refused.Should().BeGreaterThan(1_000); sent.Should().BeGreaterThan(1_000);
        Evidence.Record("mat6-fuzz", new { seed = 67, inputs = 24_000, refused, sent, outcomes = typeCounts });
    }

    // ============================================================ real stack — an unusable bearer never opens a TCP connection (the Gate 065 regression)

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0); private readonly CancellationTokenSource _stop = new(); private readonly Task _loop; private int _connections;
        public int Connections => Volatile.Read(ref _connections);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
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
                    var body = "{\"results\":[]}";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n" + body), _stop.Token);
                }
                catch (Exception) { }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); try { await _loop; } catch { } _stop.Dispose(); }
    }

    /// <summary>Sends every request through a REAL SocketsHttpHandler to the loopback server, whatever host the connector addressed.</summary>
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
    public async Task Real_stack_an_unusable_bearer_opens_zero_TCP_connections_while_usable_material_opens_exactly_one()
    {
        await using var server = new LoopbackServer();
        var options = Options.Create(new MercadoLivreOptions { Enabled = true, ClientId = "900000001", RedirectUri = "https://host.example/api/commerce/marketplace-authorizations/MERCADO_LIVRE/callback", ResolvedClientSecret = "m067-client-secret" });
        var clock = new TestClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z")); var sender = new MarketplaceHttpSender(Microsoft.Extensions.Logging.Abstractions.NullLogger<MarketplaceHttpSender>.Instance);
        var reader = new MarketplaceSafeReadExecutor(sender, new SystemMarketplaceDelay(), new FixedJitter(TimeSpan.Zero), clock);
        using var client = new HttpClient(new RewriteToLoopback(server.Port, new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })) { Timeout = Timeout.InfiniteTimeSpan };
        var connector = new MercadoLivreConnector(new OneClient(client), sender, reader, options, clock);

        var rows = new List<object>();
        foreach (var id in new[] { "M-08", "M-09", "M-12", "M-13", "M-14", "M-15", "M-16", "M-17", "M-18" })
        {
            var before = server.Connections; var material = Unusable.Single(f => f.Id == id).Material;
            var thrown = await Record.ExceptionAsync(() => connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", material, null), CancellationToken.None));
            thrown.Should().BeOfType<MarketplaceStoredCredentialUnusableException>(id);
            await Task.Delay(40); server.Connections.Should().Be(before, $"{id}: an unusable bearer must never reach the network (Gate 065: non-ASCII opened a connection on each of 3 attempts)");
            rows.Add(new { fixture = id, tcpConnectionsOpened = server.Connections - before });
        }
        var baseline = server.Connections;
        (await connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Usable.Single(f => f.Id == "P-1").Material, null), CancellationToken.None)).EndOfEnumeration.Should().BeTrue();
        server.Connections.Should().Be(baseline + 1, "control: usable material makes exactly one real request");
        for (var c = 0x21; c <= 0x7E; c++)
        {
            var token = "t" + (char)c + "t"; var json = new System.Text.Json.Nodes.JsonObject { ["v"] = 1, ["access_token"] = token, ["refresh_token"] = "r", ["user_id"] = "777" }.ToJsonString();
            var before = server.Connections;
            await connector.ReadListingPageAsync(new MarketplaceListingPageRequest(Guid.NewGuid(), "777", Encoding.UTF8.GetBytes(json), null), CancellationToken.None);
            server.Connections.Should().Be(before + 1, $"U+{c:X4} must travel on the real stack");
        }
        Evidence.Record("mat-real-stack-tcp", new { unusable = rows, controlConnections = 1, printableAsciiCharactersSent = 0x7E - 0x21 + 1 });
    }
}

