using System.Net;
using System.Text;
using System.Text.Json;

namespace Verce.Marketplaces.E2EHost;

/// <summary>One synthetic listing the fake provider serves. <see cref="Fail"/> makes the bulk detail answer for this id an element-level 500 (a transient provider failure).</summary>
public sealed record FakeMercadoLivreItem(string Id, string? Sku, string Title, decimal? Price, string Status, int Variations, bool Fail, long? SellerId = null);

/// <summary>Everything the control endpoint can set. Replaces the whole scenario atomically; call counters are kept unless <see cref="ResetCalls"/> is set.</summary>
public sealed record FakeMercadoLivreScenario(long SellerId, IReadOnlyList<FakeMercadoLivreItem> Items, bool ResetCalls = false);

/// <summary>Safe call evidence: route templates and counts only. Never a token, code, secret, scroll id or query value.</summary>
public sealed record FakeMercadoLivreCalls(int TokenExchanges, int TokenRefreshes, int UsersMe, int GrantProbes, int Scans, int BulkRequests, IReadOnlyList<IReadOnlyList<string>> BulkIds, int NonGetOutsideOAuth, int StockOrWriteLike, int UnexpectedRoutes, int Rejected);

/// <summary>
/// S8C.2 D11: the deterministic, in-process stand-in for the Mercado Livre HTTP API. It replaces ONLY the transport of the two named clients
/// ("MercadoLivre.OAuth" and "MercadoLivre.Api"), so the real <c>MercadoLivreConnector</c>, send-certainty boundary, credential store, fenced runs
/// and persistence all run for real; no socket ever leaves the process and nothing here is a real credential. Shapes follow the frozen contract
/// (ADR-0025 §A/§B): token form POST, <c>/users/me</c>, scan with a scroll id, and <c>/items/bulk</c> with per-element <c>code</c>/<c>body</c>.
/// </summary>
public sealed class FakeMercadoLivreHttpHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private FakeMercadoLivreScenario _scenario = new(900000001, [], false);
    private int _tokens, _tokenExchanges, _tokenRefreshes, _usersMe, _grantProbes, _scans, _bulk, _nonGet, _writeLike, _unexpected, _rejected;
    private readonly List<IReadOnlyList<string>> _bulkIds = [];

    public void Configure(FakeMercadoLivreScenario scenario)
    {
        lock (_gate)
        {
            _scenario = scenario;
            if (scenario.ResetCalls) { _tokens = _tokenExchanges = _tokenRefreshes = _usersMe = _grantProbes = _scans = _bulk = _nonGet = _writeLike = _unexpected = _rejected = 0; _bulkIds.Clear(); }
        }
    }

    public FakeMercadoLivreCalls Calls()
    {
        lock (_gate) return new(_tokenExchanges, _tokenRefreshes, _usersMe, _grantProbes, _scans, _bulk, _bulkIds.Select(x => (IReadOnlyList<string>)x.ToArray()).ToArray(), _nonGet, _writeLike, _unexpected, _rejected);
    }

    protected override void Dispose(bool disposing) { /* shared by the HttpClientFactory handler lifetime: never disposed out from under a client */ }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("E2E_FAKE_ML_REQUEST_WITHOUT_URI");
        if (!uri.Host.Equals("api.mercadolibre.com", StringComparison.OrdinalIgnoreCase)) { lock (_gate) _unexpected++; return Json(HttpStatusCode.NotFound, "{}"); }
        var path = uri.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

        // Any write-like or stock-like route is counted and refused: S8C.2 is read-only and never touches inventory.
        if (request.Method != HttpMethod.Get && path != "/oauth/token") { lock (_gate) { _nonGet++; _rejected++; } return Json(HttpStatusCode.MethodNotAllowed, "{}"); }
        if (path.Contains("stock", StringComparison.OrdinalIgnoreCase) || path.Contains("inventor", StringComparison.OrdinalIgnoreCase) || path.Contains("available_quantity", StringComparison.OrdinalIgnoreCase))
        { lock (_gate) { _writeLike++; _rejected++; } return Json(HttpStatusCode.NotFound, "{}"); }

        if (path == "/oauth/token" && request.Method == HttpMethod.Post) return await TokenAsync(request, cancellationToken);

        var bearer = request.Headers.Authorization;
        if (bearer is not { Scheme: "Bearer" } || bearer.Parameter is null || !bearer.Parameter.StartsWith("e2e-at-", StringComparison.Ordinal))
        { lock (_gate) _rejected++; return Json(HttpStatusCode.Unauthorized, """{"message":"invalid_token"}"""); }

        if (path == "/users/me") { lock (_gate) { _usersMe++; return Json(HttpStatusCode.OK, $$"""{"id":{{_scenario.SellerId}},"site_id":"MLB"}"""); } }

        if (path.StartsWith("/users/", StringComparison.Ordinal) && path.EndsWith("/items/search", StringComparison.Ordinal)) return Search(query);

        if (path == "/items/bulk") return Bulk(query);

        lock (_gate) { _unexpected++; _rejected++; }
        return Json(HttpStatusCode.NotFound, """{"message":"not_found"}""");
    }

    private async Task<HttpResponseMessage> TokenAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var form = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var fields = System.Web.HttpUtility.ParseQueryString(form);
        var grant = fields["grant_type"];
        lock (_gate)
        {
            var credentialsOk = fields["client_id"] == "900000001" && !string.IsNullOrEmpty(fields["client_secret"]);
            var ok = credentialsOk && ((grant == "authorization_code" && !string.IsNullOrEmpty(fields["code"])) || (grant == "refresh_token" && !string.IsNullOrEmpty(fields["refresh_token"])));
            if (!ok) { _rejected++; return Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""); }
            if (grant == "authorization_code") _tokenExchanges++; else _tokenRefreshes++;
            var n = ++_tokens;
            return Json(HttpStatusCode.OK, $$"""{"access_token":"e2e-at-{{n}}","token_type":"bearer","expires_in":21600,"scope":"offline_access read","user_id":{{_scenario.SellerId}},"refresh_token":"e2e-rt-{{n}}"}""");
        }
    }

    private HttpResponseMessage Search(System.Collections.Specialized.NameValueCollection query)
    {
        lock (_gate)
        {
            if (query["search_type"] != "scan")
            {
                _grantProbes++; // the connect/probe check: GET .../items/search?limit=1
                var first = _scenario.Items.Take(1).Select(x => x.Id);
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { results = first, paging = new { total = _scenario.Items.Count } }));
            }
            _scans++;
            // First page carries every id and a continuation; the continuation answers an empty page, which ends the scan (ADR-0025 §B.2).
            if (string.IsNullOrEmpty(query["scroll_id"]))
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { results = _scenario.Items.Select(x => x.Id), scroll_id = "e2e-scroll-1", paging = new { total = _scenario.Items.Count } }));
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { results = Array.Empty<string>(), scroll_id = (string?)null, paging = new { total = _scenario.Items.Count } }));
        }
    }

    private HttpResponseMessage Bulk(System.Collections.Specialized.NameValueCollection query)
    {
        var ids = (query["ids"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lock (_gate)
        {
            _bulk++; _bulkIds.Add(ids);
            var elements = new List<object>();
            foreach (var id in ids)
            {
                var item = _scenario.Items.FirstOrDefault(x => x.Id == id);
                if (item is null) { elements.Add(new { code = 404, body = new { message = "not_found" } }); continue; }
                if (item.Fail) { elements.Add(new { code = 500, body = new { message = "internal_error" } }); continue; }
                object[] attributes = item.Sku is null ? [] : [new { id = "SELLER_SKU", value_name = item.Sku }];
                elements.Add(new
                {
                    code = 200,
                    body = new
                    {
                        id = item.Id, seller_id = item.SellerId ?? _scenario.SellerId, site_id = "MLB", title = item.Title, price = item.Price, currency_id = "BRL", status = item.Status,
                        sub_status = Array.Empty<string>(), permalink = "https://produto.mercadolivre.com.br/" + item.Id, last_updated = "2026-10-01T12:00:00.000Z",
                        attributes, variations = Enumerable.Range(0, item.Variations).Select(i => new { id = i + 1 }),
                    },
                });
            }
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(elements));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

}
