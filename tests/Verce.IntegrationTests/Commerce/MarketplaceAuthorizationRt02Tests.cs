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

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// ADR-0024 RT-02: duplicate/ambiguous authorization. Real PostgreSQL (Testcontainers), the fake
/// connector's deterministic scenarios, and — where the ADR's own text says the single-instance
/// provider gate serializes callback exchanges — real concurrent HTTP calls proving the gate's
/// serialized outcome ("exactly one DB winner") rather than attempting to defeat the gate at the
/// raw-SQL level (which the ADR does not ask for: "Serialize callback exchanges for a provider in
/// the single-instance coordinator... Existing sends complete before the callback can start its
/// exchange").
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MarketplaceAuthorizationRt02Tests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;
    private MarketplaceTestHarness _harness = null!;
    private AuthTestClient _owner = null!;
    private SalesChannel _channel = null!;

    public MarketplaceAuthorizationRt02Tests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _harness = new MarketplaceTestHarness();
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, _harness.ExtraConfiguration, configureTestServices: _harness.ConfigureServices);
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
    public async Task Connected_X_then_CONNECT_NEW_to_X_forces_reauthorization_and_fails_new_operation()
    {
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        var accountId = await ConnectNewAsync(externalId);

        var (sessionId, callbackUrl) = await BeginAuthorizationAsync();
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId));
        var callback = await _owner.GetAsync(callbackUrl);
        callback.Should().NotBeNull();

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED);
        (await db.Set<MarketplaceAccount>().CountAsync(x => x.ExternalAccountId == externalId)).Should().Be(1, "no second account may be created for the same identity");
        var newOperation = await db.Set<MarketplaceAccountOperation>().SingleAsync(x => x.AuthorizationSessionId == sessionId);
        newOperation.Decision.Should().Be(MarketplaceAccountOperationDecision.FAIL_CLOSED);
        newOperation.SafeResultCode.Should().Be("ACCOUNT_ALREADY_EXISTS_RECONNECT_REQUIRED");
    }

    [Fact]
    public async Task Inactive_X_then_CONNECT_NEW_to_X_preserves_inactive_flag_and_forces_reauthorization()
    {
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        var accountId = await ConnectNewAsync(externalId);
        await using (var db = _fixture.CreateContext())
        {
            var account = await db.Set<MarketplaceAccount>().SingleAsync(x => x.Id == accountId);
            account.Deactivate();
            await db.SaveChangesAsync();
        }

        var (sessionId, callbackUrl) = await BeginAuthorizationAsync();
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId));
        await _owner.GetAsync(callbackUrl);

        await using var verify = _fixture.CreateContext();
        var account2 = await verify.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account2.Active.Should().BeFalse("inactive flag/history must be preserved, never silently reactivated");
        account2.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED);
    }

    [Fact]
    public async Task Two_concurrent_CONNECT_NEW_sessions_resolve_to_exactly_one_account()
    {
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        var (session1, callback1, cookie1) = await BeginAuthorizationRawAsync();
        var (session2, callback2, cookie2) = await BeginAuthorizationRawAsync();
        _harness.ControlPlane.Configure(session1, new FakeMarketplaceScenario(externalId,
            new Dictionary<string, AccountCapabilityState> { ["ORDERS_READ"] = AccountCapabilityState.GRANTED }));
        _harness.ControlPlane.Configure(session2, new FakeMarketplaceScenario(externalId,
            new Dictionary<string, AccountCapabilityState> { ["ORDERS_READ"] = AccountCapabilityState.GRANTED }));

        // The single-instance provider gate (ADR-0024 §2) serializes callback exchanges for one
        // provider — two real concurrent HTTP calls still yield exactly one DB winner, proving
        // the mandated outcome even though the actual mechanism is gate ordering rather than a
        // raw unique-constraint race (which the ADR's own text says should never be reachable
        // under normal single-instance operation). The callback route is AllowAnonymous — only
        // each session's own browser-binding cookie is required, set manually here on two
        // independent HttpClients so AuthTestClient's own (non-thread-safe) cookie jar is never
        // shared across the two concurrent calls.
        using var client1 = _factory.CreateHttpsClient();
        using var client2 = _factory.CreateHttpsClient();
        using var request1 = new HttpRequestMessage(HttpMethod.Get, callback1);
        request1.Headers.Add("Cookie", cookie1);
        using var request2 = new HttpRequestMessage(HttpMethod.Get, callback2);
        request2.Headers.Add("Cookie", cookie2);
        var results = await Task.WhenAll(client1.SendAsync(request1), client2.SendAsync(request2));
        results.Should().AllSatisfy(r => r.Should().NotBeNull());

        await using var db = _fixture.CreateContext();
        var accounts = await db.Set<MarketplaceAccount>().Where(x => x.ExternalAccountId == externalId).ToListAsync();
        accounts.Should().HaveCount(1, "exactly one account may ever exist for one external identity");
        var operations = await db.Set<MarketplaceAccountOperation>().Where(x => x.AuthorizationSessionId == session1 || x.AuthorizationSessionId == session2).ToListAsync();
        operations.Should().HaveCount(2);
        operations.Count(x => x.Decision == MarketplaceAccountOperationDecision.CONFIRMED).Should().Be(1);
        operations.Count(x => x.Decision == MarketplaceAccountOperationDecision.FAIL_CLOSED).Should().Be(1);
    }

    [Fact]
    public async Task Reconnect_X_returns_X_confirms_normally()
    {
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        var accountId = await ConnectNewAsync(externalId);
        long versionBefore;
        await using (var db = _fixture.CreateContext())
            versionBefore = (await db.Set<MarketplaceAccount>().SingleAsync(x => x.Id == accountId)).Version;

        var (sessionId, callbackUrl) = await BeginReauthorizeAsync(accountId, versionBefore);
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId));
        await _owner.GetAsync(callbackUrl);

        await using var verify = _fixture.CreateContext();
        var account = await verify.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED);
        (await verify.Set<MarketplaceAccount>().CountAsync(x => x.ExternalAccountId == externalId)).Should().Be(1);
    }

    [Fact]
    public async Task Reconnect_X_returns_identity_owned_by_Y_fails_both_closed_without_rebinding()
    {
        var externalX = "EXT-X-" + Guid.NewGuid().ToString("N")[..10];
        var externalY = "EXT-Y-" + Guid.NewGuid().ToString("N")[..10];
        var accountX = await ConnectNewAsync(externalX);
        var accountY = await ConnectNewAsync(externalY);
        long versionX;
        await using (var db = _fixture.CreateContext())
            versionX = (await db.Set<MarketplaceAccount>().SingleAsync(x => x.Id == accountX)).Version;

        var (sessionId, callbackUrl) = await BeginReauthorizeAsync(accountX, versionX);
        // The provider returns Y's identity for an explicit reconnect of X.
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalY));
        await _owner.GetAsync(callbackUrl);

        await using var verify = _fixture.CreateContext();
        var x = await verify.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(a => a.Id == accountX);
        var y = await verify.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(a => a.Id == accountY);
        x.ExternalAccountId.Should().Be(externalX, "X must never be silently rebound to Y's identity");
        x.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED);
        y.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED, "Y's own authorization is no longer provably valid either, under the default UNKNOWN/MAY_SUPERSEDE_EXISTING impact");
        var operation = await verify.Set<MarketplaceAccountOperation>().SingleAsync(o => o.AuthorizationSessionId == sessionId);
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.FAIL_CLOSED);
    }

    [Fact]
    public async Task Duplicate_CONNECT_NEW_under_proven_PRESERVES_EXISTING_leaves_existing_account_connected()
    {
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        var accountId = await ConnectNewAsync(externalId);

        var (sessionId, callbackUrl) = await BeginAuthorizationAsync();
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId, Impact: CredentialImpact.PRESERVES_EXISTING));
        await _owner.GetAsync(callbackUrl);

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED,
            "officially proven PRESERVES_EXISTING impact lets the existing account remain connected");
        var operation = await db.Set<MarketplaceAccountOperation>().SingleAsync(o => o.AuthorizationSessionId == sessionId);
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.FAIL_CLOSED, "the duplicate session itself still never installs a new candidate on the existing account");
    }

    [Fact]
    public async Task Ambiguous_after_send_identity_unknown_makes_every_connected_provider_account_non_executable()
    {
        var externalA = "EXT-A-" + Guid.NewGuid().ToString("N")[..10];
        var externalB = "EXT-B-" + Guid.NewGuid().ToString("N")[..10];
        var accountA = await ConnectNewAsync(externalA);
        var accountB = await ConnectNewAsync(externalB);

        var (sessionId, callbackUrl) = await BeginAuthorizationAsync();
        // Outcome=FailAfterSend: the fake proves nothing about whether/who the exchange reached —
        // a real SENT_OR_UNKNOWN ambiguity, never attributable to a specific existing account.
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario("EXT-UNKNOWN-" + Guid.NewGuid().ToString("N")[..8], Outcome: FakeMarketplaceOutcome.FailAfterSend));
        await _owner.GetAsync(callbackUrl);

        await using var db = _fixture.CreateContext();
        var a = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountA);
        var b = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountB);
        a.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED);
        b.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED);
    }

    [Fact]
    public async Task Proven_not_sent_denial_preserves_existing_authorization_for_other_accounts()
    {
        var externalA = "EXT-A-" + Guid.NewGuid().ToString("N")[..10];
        var accountA = await ConnectNewAsync(externalA);

        var (sessionId, callbackUrl) = await BeginAuthorizationAsync();
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario("EXT-DENIED-" + Guid.NewGuid().ToString("N")[..8], Outcome: FakeMarketplaceOutcome.DeniedBeforeSend));
        await _owner.GetAsync(callbackUrl);

        await using var db = _fixture.CreateContext();
        var a = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountA);
        a.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED,
            "a proven pre-send denial must never force-reauthorize unrelated already-connected accounts");
    }

    private async Task<Guid> ConnectNewAsync(string externalId)
    {
        var (sessionId, callbackUrl) = await BeginAuthorizationAsync();
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId));
        var callback = await _owner.GetAsync(callbackUrl);
        callback.Should().NotBeNull();
        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.ExternalAccountId == externalId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED, "test setup must itself succeed");
        return account.Id;
    }

    /// <summary>Same as <see cref="BeginAuthorizationAsync"/> but also returns the raw
    /// browser-binding <c>Set-Cookie</c> value, so the callback can be driven from an independent
    /// HttpClient/cookie jar — needed to run two sessions' callbacks genuinely concurrently
    /// without racing on AuthTestClient's shared, non-thread-safe cookie dictionary.</summary>
    private async Task<(Guid SessionId, string CallbackUrl, string CookieHeader)> BeginAuthorizationRawAsync()
    {
        var begin = await _owner.PostAsync("/api/commerce/marketplace-authorizations",
            new BeginMarketplaceAuthorizationRequest(FakeMarketplaceConnector.Code, _channel.Id, "Conta Fake " + Guid.NewGuid().ToString("N")[..6]));
        begin.StatusCode.Should().Be(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync());
        var (sessionId, callbackUrl) = ParseBegin(await begin.Content.ReadAsStringAsync());
        var setCookie = begin.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith($"__Host-verce.marketplace.{sessionId}=", StringComparison.Ordinal))
            : null;
        setCookie.Should().NotBeNull("the begin response must set the browser-binding cookie");
        var cookiePair = setCookie!.Split(';', 2)[0];
        return (sessionId, callbackUrl, cookiePair);
    }

    private async Task<(Guid SessionId, string CallbackUrl)> BeginAuthorizationAsync()
    {
        var begin = await _owner.PostAsync("/api/commerce/marketplace-authorizations",
            new BeginMarketplaceAuthorizationRequest(FakeMarketplaceConnector.Code, _channel.Id, "Conta Fake " + Guid.NewGuid().ToString("N")[..6]));
        begin.StatusCode.Should().Be(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync());
        return ParseBegin(await begin.Content.ReadAsStringAsync());
    }

    private async Task<(Guid SessionId, string CallbackUrl)> BeginReauthorizeAsync(Guid accountId, long expectedVersion)
    {
        var begin = await _owner.PostAsync($"/api/commerce/marketplace-accounts/{accountId}/reauthorize", new ReauthorizeMarketplaceAccountRequest(expectedVersion));
        begin.StatusCode.Should().Be(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync());
        return ParseBegin(await begin.Content.ReadAsStringAsync());
    }

    private static (Guid SessionId, string CallbackUrl) ParseBegin(string json)
    {
        using var document = JsonDocument.Parse(json);
        var sessionId = document.RootElement.GetProperty("sessionId").GetGuid();
        var authorizationUri = document.RootElement.GetProperty("authorizationUri").GetString()!;
        var state = HttpUtility.ParseQueryString(new Uri(authorizationUri).Query)["state"]!;
        return (sessionId, $"/api/commerce/marketplace-authorizations/{FakeMarketplaceConnector.Code}/callback?state={Uri.EscapeDataString(state)}&code=fake-code");
    }

    private async Task<SalesChannel> SeedProviderAndChannelAsync()
    {
        await using var db = _fixture.CreateContext();
        var provider = await db.Set<MarketplaceProvider>().SingleOrDefaultAsync(x => x.Code == FakeMarketplaceConnector.Code);
        var channel = new SalesChannel("MKT-" + Guid.NewGuid().ToString("N")[..10], "Canal Fake RT02", SalesChannelKind.Marketplace, null, null);
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
            Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "Owner RT02",
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
