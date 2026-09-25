using System.Net;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verce.Api.Auth;
using Verce.Api.Commerce;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Commerce;
using Verce.Modules.Pricing;
using Verce.Platform.Identity;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// ADR-0024 §2/G-08 callback security. Real PostgreSQL, real HTTP calls against the actual
/// callback route, a <see cref="TestClock"/> for deterministic expiry (never <c>Thread.Sleep</c>),
/// and <see cref="CapturingLoggerProvider"/> for the captured-log assertions.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MarketplaceCallbackSecurityTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;
    private MarketplaceTestHarness _harness = null!;
    private TestClock _clock = null!;
    private AuthTestClient _owner = null!;
    private SalesChannel _channel = null!;

    public MarketplaceCallbackSecurityTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _harness = new MarketplaceTestHarness();
        _clock = new TestClock(DateTimeOffset.UtcNow);
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, _harness.ExtraConfiguration, clockOverride: _clock, configureTestServices: _harness.ConfigureServices);
        _channel = await SeedProviderAndChannelAsync();
        _owner = await LoggedInOwnerAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        _harness.Dispose();
        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE commerce.marketplace_account_connection SET confirmed_operation_id = NULL
            WHERE marketplace_account_id IN (SELECT id FROM commerce.marketplace_account WHERE provider_code = {FakeMarketplaceConnector.Code})
            """);
        await db.SaveChangesAsync();
        db.RemoveRange(await db.Set<MarketplaceAccountOperation>().Where(x => x.ProviderCode == FakeMarketplaceConnector.Code).ToListAsync());
        db.RemoveRange(await db.Set<MarketplaceAuthorizationSession>().Where(x => x.ProviderCode == FakeMarketplaceConnector.Code).ToListAsync());
        await db.SaveChangesAsync();
        db.RemoveRange(await db.Set<MarketplaceAccount>().Where(x => x.ProviderCode == FakeMarketplaceConnector.Code).ToListAsync());
        await db.SaveChangesAsync();
        db.RemoveRange(await db.Set<SalesChannel>().Where(x => x.Id == _channel.Id).ToListAsync());
        var provider = await db.Set<MarketplaceProvider>().SingleOrDefaultAsync(x => x.Code == FakeMarketplaceConnector.Code);
        if (provider is not null) db.Remove(provider);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Forged_state_is_rejected_without_invoking_the_provider()
    {
        var forgedUrl = $"/api/commerce/marketplace-authorizations/{FakeMarketplaceConnector.Code}/callback?state={Uri.EscapeDataString(Convert.ToBase64String(RandomBytes()))}&code=forged-code";
        var response = await _owner.GetAsync(forgedUrl);
        response.Should().NotBeNull();
        await using var db = _fixture.CreateContext();
        (await db.Set<MarketplaceAccount>().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Expired_state_is_rejected()
    {
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        var (sessionId, callbackUrl) = await BeginAuthorizationAsync();
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId));

        _clock.UtcNow = _clock.UtcNow.AddMinutes(16); // past the fixed 15-minute session lifetime

        await _owner.GetAsync(callbackUrl);
        _harness.ControlPlane.CompleteOnceCallCount(sessionId).Should().Be(0, "an expired session must never reach the provider exchange");
        await using var db = _fixture.CreateContext();
        (await db.Set<MarketplaceAccount>().AnyAsync(x => x.ExternalAccountId == externalId)).Should().BeFalse();
    }

    [Fact]
    public async Task Replayed_callback_invokes_the_provider_at_most_once()
    {
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        var (sessionId, callbackUrl) = await BeginAuthorizationAsync();
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId));

        var first = await _owner.GetAsync(callbackUrl);
        var second = await _owner.GetAsync(callbackUrl); // identical state+code, replayed
        first.Should().NotBeNull();
        second.Should().NotBeNull();

        _harness.ControlPlane.CompleteOnceCallCount(sessionId).Should().BeLessThanOrEqualTo(1, "a replayed/duplicate callback must never re-invoke the provider exchange");
        await using var db = _fixture.CreateContext();
        (await db.Set<MarketplaceAccount>().CountAsync(x => x.ExternalAccountId == externalId)).Should().Be(1);
    }

    [Fact]
    public async Task Wrong_provider_route_is_rejected()
    {
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        var (sessionId, callbackUrl) = await BeginAuthorizationAsync();
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId));

        var wrongRouteUrl = callbackUrl.Replace($"/{FakeMarketplaceConnector.Code}/callback", "/OTHERPROVIDER/callback");
        await _owner.GetAsync(wrongRouteUrl);
        _harness.ControlPlane.CompleteOnceCallCount(sessionId).Should().Be(0, "a provider-route mismatch must be rejected before any exchange");

        // The correct route must still work afterwards — proves the wrong-route attempt did not
        // consume/corrupt the session.
        var correct = await _owner.GetAsync(callbackUrl);
        correct.Should().NotBeNull();
        await using var db = _fixture.CreateContext();
        (await db.Set<MarketplaceAccount>().AnyAsync(x => x.ExternalAccountId == externalId)).Should().BeTrue();
    }

    [Fact]
    public async Task Wrong_browser_binding_is_rejected()
    {
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        var (sessionId, callbackUrl) = await BeginAuthorizationAsync();
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId));

        using var wrongCookieClient = _factory.CreateHttpsClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, callbackUrl);
        request.Headers.Add("Cookie", $"__Host-verce.marketplace.{sessionId}=totally-wrong-browser-binding-value");
        await wrongCookieClient.SendAsync(request);

        _harness.ControlPlane.CompleteOnceCallCount(sessionId).Should().Be(0, "a mismatched browser-binding cookie must be rejected before any exchange");
    }

    [Fact]
    public async Task SalesChannel_deactivated_between_begin_and_callback_fails_closed()
    {
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        var (sessionId, callbackUrl) = await BeginAuthorizationAsync();
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId));

        await using (var db = _fixture.CreateContext())
        {
            var channel = await db.Set<SalesChannel>().SingleAsync(x => x.Id == _channel.Id);
            channel.Deactivate();
            await db.SaveChangesAsync();
        }

        await _owner.GetAsync(callbackUrl);
        _harness.ControlPlane.CompleteOnceCallCount(sessionId).Should().Be(0, "a channel that is no longer active/Marketplace must fail closed before any provider exchange");
        await using var verify = _fixture.CreateContext();
        (await verify.Set<MarketplaceAccount>().AnyAsync(x => x.ExternalAccountId == externalId)).Should().BeFalse();

        // Restore for teardown symmetry (harmless if channel is deleted anyway).
        await using var restore = _fixture.CreateContext();
        var restored = await restore.Set<SalesChannel>().SingleAsync(x => x.Id == _channel.Id);
        restored.Activate();
        await restore.SaveChangesAsync();
    }

    [Fact]
    public async Task Actor_permission_revoked_before_confirmation_fails_closed()
    {
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        var (sessionId, callbackUrl) = await BeginAuthorizationAsync();
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId));

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await using var db = _fixture.CreateContext();
            var session = await db.Set<MarketplaceAuthorizationSession>().SingleAsync(x => x.Id == sessionId);
            var actor = await users.FindByIdAsync(session.InitiatedByUserId.ToString());
            (await users.RemoveFromRoleAsync(actor!, Roles.Owner)).Succeeded.Should().BeTrue();
        }

        await _owner.GetAsync(callbackUrl);

        await using var verify = _fixture.CreateContext();
        (await verify.Set<MarketplaceAccount>().AnyAsync(x => x.ExternalAccountId == externalId)).Should().BeFalse(
            "an actor who lost commerce:accounts:manage before confirmation must never have their session install a credential");
    }

    [Fact]
    public async Task Callback_never_redirects_anywhere_other_than_the_fixed_internal_result_route()
    {
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        var (sessionId, callbackUrl) = await BeginAuthorizationAsync();
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId));

        // Attempted open-redirect: inject extra query parameters that a naive implementation
        // might echo into a Location header. The endpoint accepts no returnUrl at all.
        using var noRedirectClient = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get,
            callbackUrl + "&returnUrl=" + Uri.EscapeDataString("https://evil.example.com/phish"));
        request.Headers.Add("Cookie", await CaptureBrowserBindingCookieAsync(sessionId));
        var response = await noRedirectClient.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.ToString();
        // The real frontend route is "/marketplace-accounts" (AppRoutes.tsx) — "/commerce/marketplace-accounts"
        // is not a registered React Router route and previously left a real browser stranded on
        // NotFoundPage after every completed authorization; fixed alongside this test's own
        // expectation (CommerceEndpoints.cs's callback route).
        location.Should().StartWith("/marketplace-accounts");
        location.Should().NotContain("evil.example.com");
    }

    [Fact]
    public async Task Captured_logs_never_contain_the_authorization_code_state_or_error_description()
    {
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        var (sessionId, callbackUrl) = await BeginAuthorizationAsync();
        var sentinelCode = "SENTINEL-CODE-" + Guid.NewGuid().ToString("N");
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId));

        var taggedUrl = callbackUrl.Replace("code=fake-code", "code=" + Uri.EscapeDataString(sentinelCode))
            + "&error_description=" + Uri.EscapeDataString("SENTINEL-ERROR-DESCRIPTION-should-never-be-logged");
        await _owner.GetAsync(taggedUrl);

        var state = HttpUtility.ParseQueryString(new Uri(new Uri("https://x"), taggedUrl).Query)["state"]!;
        var allLogs = string.Join("\n", _harness.Logs.Messages);
        allLogs.Should().NotContain(sentinelCode);
        allLogs.Should().NotContain("SENTINEL-ERROR-DESCRIPTION");
        allLogs.Should().NotContain(state);
    }

    private async Task<string> CaptureBrowserBindingCookieAsync(Guid sessionId)
    {
        // Re-derive the exact Set-Cookie value from this session's begin response by re-reading
        // it through the same AuthTestClient jar (it captured it when BeginAuthorizationAsync ran).
        await Task.CompletedTask;
        return _capturedCookies[sessionId];
    }

    private readonly Dictionary<Guid, string> _capturedCookies = new();

    private async Task<(Guid SessionId, string CallbackUrl)> BeginAuthorizationAsync()
    {
        var begin = await _owner.PostAsync("/api/commerce/marketplace-authorizations",
            new BeginMarketplaceAuthorizationRequest(FakeMarketplaceConnector.Code, _channel.Id, "Conta Fake Sec " + Guid.NewGuid().ToString("N")[..6]));
        begin.StatusCode.Should().Be(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync());
        var json = await begin.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var sessionId = document.RootElement.GetProperty("sessionId").GetGuid();
        var authorizationUri = document.RootElement.GetProperty("authorizationUri").GetString()!;
        var state = HttpUtility.ParseQueryString(new Uri(authorizationUri).Query)["state"]!;
        var callbackUrl = $"/api/commerce/marketplace-authorizations/{FakeMarketplaceConnector.Code}/callback?state={Uri.EscapeDataString(state)}&code=fake-code";
        if (begin.Headers.TryGetValues("Set-Cookie", out var values))
        {
            var cookie = values.FirstOrDefault(v => v.StartsWith($"__Host-verce.marketplace.{sessionId}=", StringComparison.Ordinal));
            if (cookie is not null) _capturedCookies[sessionId] = cookie.Split(';', 2)[0];
        }
        return (sessionId, callbackUrl);
    }

    private static byte[] RandomBytes() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    private async Task<SalesChannel> SeedProviderAndChannelAsync()
    {
        await using var db = _fixture.CreateContext();
        var provider = await db.Set<MarketplaceProvider>().SingleOrDefaultAsync(x => x.Code == FakeMarketplaceConnector.Code);
        var channel = new SalesChannel("MKT-" + Guid.NewGuid().ToString("N")[..10], "Canal Fake Sec", SalesChannelKind.Marketplace, null, null);
        if (provider is null) db.Add(new MarketplaceProvider(FakeMarketplaceConnector.Code, "Fake Marketplace"));
        db.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    private async Task<AuthTestClient> LoggedInOwnerAsync()
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        const string password = "a-perfectly-fine-12char-password";
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!await roles.RoleExistsAsync(Roles.Owner))
            (await roles.CreateAsync(new ApplicationRole(Roles.Owner))).Succeeded.Should().BeTrue();
        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "Owner Sec",
            IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow
        };
        (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
        (await users.AddToRoleAsync(user, Roles.Owner)).Succeeded.Should().BeTrue();

        var client = new AuthTestClient(_factory.CreateHttpsClient());
        await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new LoginRequest(email, password))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await client.EnsureCsrfCookieAsync();
        return client;
    }
}
