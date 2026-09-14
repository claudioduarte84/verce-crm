using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace Verce.IntegrationTests.Auth;

/// <summary>
/// A minimal, explicit cookie jar over <see cref="HttpClient"/> that mirrors exactly what the
/// real SPA does (frontend/src/api/client.ts): fetch the XSRF-TOKEN cookie once, echo it in the
/// X-XSRF-TOKEN header on state-changing requests, and carry the session cookie set by /login
/// on every subsequent call. Deliberately manual rather than relying on
/// <see cref="System.Net.CookieContainer"/>'s Secure-flag enforcement, which would fight the
/// in-memory TestServer's http/https scheme handling for no test-relevant reason.
/// </summary>
public sealed class AuthTestClient
{
    private readonly HttpClient _client;
    private readonly Dictionary<string, string> _cookies = new();

    public AuthTestClient(HttpClient client)
    {
        _client = client;
    }

    public async Task<HttpResponseMessage> GetAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        ApplyCookies(request);
        var response = await _client.SendAsync(request);
        CaptureCookies(response);
        return response;
    }

    public async Task<HttpResponseMessage> PostAsync<T>(string path, T body, bool withAntiforgery = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        ApplyCookies(request);
        if (withAntiforgery && _cookies.TryGetValue("XSRF-TOKEN", out var token))
            request.Headers.Add("X-XSRF-TOKEN", token);

        var response = await _client.SendAsync(request);
        CaptureCookies(response);
        return response;
    }

    public async Task<HttpResponseMessage> PutAsync<T>(string path, T body, bool withAntiforgery = true) =>
        await SendJsonAsync(HttpMethod.Put, path, body, withAntiforgery);

    public async Task<HttpResponseMessage> DeleteAsync(string path, bool withAntiforgery = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        ApplyCookies(request);
        if (withAntiforgery && _cookies.TryGetValue("XSRF-TOKEN", out var token)) request.Headers.Add("X-XSRF-TOKEN", token);
        var response = await _client.SendAsync(request);
        CaptureCookies(response);
        return response;
    }

    public async Task<HttpResponseMessage> PostMultipartAsync(string path, MultipartFormDataContent body, bool withAntiforgery = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body };
        ApplyCookies(request);
        if (withAntiforgery && _cookies.TryGetValue("XSRF-TOKEN", out var token)) request.Headers.Add("X-XSRF-TOKEN", token);
        var response = await _client.SendAsync(request);
        CaptureCookies(response);
        return response;
    }

    private async Task<HttpResponseMessage> SendJsonAsync<T>(HttpMethod method, string path, T body, bool withAntiforgery)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        ApplyCookies(request);
        if (withAntiforgery && _cookies.TryGetValue("XSRF-TOKEN", out var token)) request.Headers.Add("X-XSRF-TOKEN", token);
        var response = await _client.SendAsync(request);
        CaptureCookies(response);
        return response;
    }

    public async Task EnsureCsrfCookieAsync()
    {
        var response = await GetAsync("/api/auth/csrf");
        response.EnsureSuccessStatusCode();
    }

    private void ApplyCookies(HttpRequestMessage request)
    {
        if (_cookies.Count == 0) return;
        request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    private void CaptureCookies(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders)) return;

        foreach (var header in setCookieHeaders)
        {
            var match = Regex.Match(header, @"^([^=]+)=([^;]*)");
            if (!match.Success) continue;
            _cookies[match.Groups[1].Value] = match.Groups[2].Value;
        }
    }
}
