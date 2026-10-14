using System.Net;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verce.Api.Auth;
using Verce.Api.Commerce;
using Verce.Infrastructure.Marketplaces;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Commerce;
using Verce.Modules.Pricing;
using Verce.Platform.Identity;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// S8C.2 Phase A1-A3: real-PostgreSQL (Testcontainers) proof of the three S8C.1 implementation
/// defects fixed for this delivery — D-01 (access-token expiry never persisted), D-02 (refresh
/// send-certainty per ADR-0025 §F.2/§F.3, Astra G01) and D-03 (probe grant provenance +
/// persisted Retry-After backoff guard). Exercises the real <see cref="MarketplaceAuthorizationWorkflow"/>
/// against the real <see cref="LocalProtectedCredentialStore"/>; only the connector itself is the
/// S8C.1 fake (never the store/workflow/operation-arbiter), which is exactly what ADR-0024 G-06
/// mandates a test host may substitute.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MarketplaceRefreshAndProbeTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;
    private MarketplaceTestHarness _harness = null!;
    private TestClock _clock = null!;

    public MarketplaceRefreshAndProbeTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _harness = new MarketplaceTestHarness();
        _clock = new TestClock(DateTimeOffset.UtcNow);
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, _harness.ExtraConfiguration, clockOverride: _clock, configureTestServices: _harness.ConfigureServices);
        return Task.CompletedTask;
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
        db.RemoveRange(await db.Set<SalesChannel>().Where(x => x.Code.StartsWith("MKT-")).ToListAsync());
        var provider = await db.Set<MarketplaceProvider>().SingleOrDefaultAsync(x => x.Code == FakeMarketplaceConnector.Code);
        if (provider is not null) db.Remove(provider);
        await db.SaveChangesAsync();
    }

    // ================= D-01: access-token expiry persistence =================

    [Fact]
    public async Task D01_initial_connect_persists_the_provider_declared_access_expiry()
    {
        var (accountId, externalId) = ("", "");
        var channel = await SeedProviderAndChannelAsync();
        var owner = await LoggedInOwnerAsync();
        externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        var expectedExpiry = _clock.UtcNow.AddHours(6);

        var (sessionId, callbackUrl) = await BeginAuthorizationAsync(owner, channel);
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId, AccessExpiresAt: expectedExpiry));
        (await owner.GetAsync(callbackUrl)).StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.OK, HttpStatusCode.NotFound);

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.ExternalAccountId == externalId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED);
        account.Connection.AccessExpiresAt.Should().BeCloseTo(expectedExpiry, TimeSpan.FromSeconds(1),
            "D-01: the provider-declared expiry from CompleteOnce must actually be persisted, not silently dropped");
    }

    [Fact]
    public async Task D01_successful_refresh_persists_the_new_expiry_and_stamps_last_refresh_at()
    {
        var (accountId, externalId) = await ConnectedAccountAsync();
        var newExpiry = _clock.UtcNow.AddHours(6);
        _harness.ControlPlane.ConfigureRefresh(accountId, new FakeRefreshScenario(AccessExpiresAt: newExpiry));

        // Force the refresh to actually run regardless of the fresh-expiry short-circuit.
        await using (var pre = _fixture.CreateContext())
        {
            var account = await pre.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            account.Connection.AccessExpiresAt.Should().NotBeNull("D-01: the initial connect must already have persisted an expiry");
        }

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        _clock.UtcNow += TimeSpan.FromHours(6).Add(TimeSpan.FromMinutes(1)); // ensure the old expiry has actually passed
        var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.Code);
        result.Code.Should().Be("REFRESHED");

        await using var db = _fixture.CreateContext();
        var refreshed = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        refreshed.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED);
        refreshed.Connection.ConfirmedCredentialVersion.Should().Be(2);
        refreshed.Connection.AccessExpiresAt.Should().BeCloseTo(newExpiry, TimeSpan.FromSeconds(1));
        refreshed.Connection.LastRefreshAt.Should().NotBeNull();
        _harness.ControlPlane.RefreshCallCount(accountId).Should().Be(1);
    }

    // ================= D-02: refresh send-certainty (ADR-0025 §F.2/§F.3) =================

    [Fact]
    public async Task D02_pre_send_guard_failure_preserves_connected_authorization_and_only_marks_runtime_unavailable()
    {
        var (accountId, _) = await ConnectedAccountAsync();
        _harness.ControlPlane.ConfigureRefresh(accountId, new FakeRefreshScenario(FakeRefreshOutcome.NotSentPreSendGuard));
        _clock.UtcNow += TimeSpan.FromHours(7);

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
        result.Succeeded.Should().BeFalse();
        result.Code.Should().Be("FAKE_REFRESH_NOT_SENT");

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED,
            "D-02 F.3 rows 1-3: a proven pre-send failure never disturbs existing authorization");
        account.Connection.RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.UNAVAILABLE);
        var operation = await db.Set<MarketplaceAccountOperation>().SingleAsync(x => x.Id != account.Connection.ConfirmedOperationId && x.MarketplaceAccountId == accountId);
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.FAIL_CLOSED);
        operation.SafeResultCode.Should().Be("FAKE_REFRESH_NOT_SENT");
        _harness.ControlPlane.RefreshCallCount(accountId).Should().Be(1, "never replayed");
    }

    [Fact]
    public async Task D02_pre_send_token_unavailable_requires_reauthorization_even_though_nothing_was_sent()
    {
        var (accountId, _) = await ConnectedAccountAsync();
        _harness.ControlPlane.ConfigureRefresh(accountId, new FakeRefreshScenario(FakeRefreshOutcome.NotSentTokenUnavailable));
        _clock.UtcNow += TimeSpan.FromHours(7);

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
        result.Succeeded.Should().BeFalse();
        result.Code.Should().Be("REAUTHORIZATION_REQUIRED");

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED,
            "unrenewable material means authorization cannot be preserved even though NOT_SENT was proven");
        account.Connection.SafeFailureCode.Should().Be("REFRESH_TOKEN_UNAVAILABLE");
        _harness.ControlPlane.RefreshCallCount(accountId).Should().Be(1);
    }

    [Fact]
    public async Task D02_provider_app_credentials_invalid_requires_reauthorization_and_surfaces_contact_admin()
    {
        var (accountId, _) = await ConnectedAccountAsync();
        var failure = new MarketplaceProviderFailure(MarketplaceFailureClassification.NON_RETRYABLE, "PROVIDER_APP_CREDENTIALS_INVALID", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED);
        _harness.ControlPlane.ConfigureRefresh(accountId, new FakeRefreshScenario(FakeRefreshOutcome.ProviderFailure, failure));
        _clock.UtcNow += TimeSpan.FromHours(7);

        using (var scope = _factory.Services.CreateScope())
        {
            var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
            result.Succeeded.Should().BeFalse();
        }

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED);
        account.Connection.SafeFailureCode.Should().Be("PROVIDER_APP_CREDENTIALS_INVALID");

        var owner = await LoggedInOwnerAsync();
        var statusResponse = await owner.GetAsync($"/api/commerce/marketplace-accounts/{accountId}/connection");
        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("requiredAction").GetString().Should().Be("CONTACT_ADMIN",
            "D-02: an application-credential failure is never the seller's fault to fix by reauthorizing");
    }

    [Fact]
    public async Task D02_rate_limited_refresh_persists_retry_after_diagnostic_and_requires_reauthorization()
    {
        var (accountId, _) = await ConnectedAccountAsync();
        var retryAfter = _clock.UtcNow.AddMinutes(5);
        var failure = new MarketplaceProviderFailure(MarketplaceFailureClassification.RATE_LIMITED, "PROVIDER_RATE_LIMITED", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED, RetryAfterUntil: retryAfter);
        _harness.ControlPlane.ConfigureRefresh(accountId, new FakeRefreshScenario(FakeRefreshOutcome.ProviderFailure, failure));
        _clock.UtcNow += TimeSpan.FromHours(7);

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
        result.Succeeded.Should().BeFalse();

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED);
        account.Connection.SafeFailureCode.Should().Be("REFRESH_RATE_LIMITED");
        account.Connection.RuntimeRetryAfterUntil.Should().BeCloseTo(retryAfter, TimeSpan.FromSeconds(1),
            "Retry-After is diagnostic only for a rejected refresh, but must still be persisted (D-03 infra)");
    }

    [Fact]
    public async Task D02_generic_sent_or_unknown_exception_is_never_replayed_and_requires_reauthorization()
    {
        var (accountId, _) = await ConnectedAccountAsync();
        _harness.ControlPlane.ConfigureRefresh(accountId, new FakeRefreshScenario(FakeRefreshOutcome.GenericSentOrUnknownException));
        _clock.UtcNow += TimeSpan.FromHours(7);

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
        result.Succeeded.Should().BeFalse();

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED);
        account.Connection.SafeFailureCode.Should().Be("REFRESH_OUTCOME_UNKNOWN",
            "an unrecognized exception is never inferred to be safely retryable/NOT_SENT");
        _harness.ControlPlane.RefreshCallCount(accountId).Should().Be(1, "never replayed after an ambiguous outcome");
    }

    // ================= D-03: probe grant provenance + persisted backoff guard =================

    [Fact]
    public async Task D03_probe_writes_grant_source_PROBE_INSPECTION()
    {
        var grants = new Dictionary<string, AccountCapabilityState> { ["LISTINGS_READ"] = AccountCapabilityState.GRANTED };
        var (accountId, externalId) = await ConnectedAccountAsync(grants: grants);
        // The fake connector's InspectAsync (unlike CompleteOnceAsync) resolves grants from this
        // bound identity map, not from the original connect scenario — mirrors a real provider
        // being asked again independently at probe time.
        _harness.ControlPlane.BindAccountIdentity(accountId, externalId, grants);

        long version;
        await using (var db0 = _fixture.CreateContext())
            version = (await db0.Set<MarketplaceAccount>().AsNoTracking().SingleAsync(x => x.Id == accountId)).Version;

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var result = await workflow.ProbeAsync(accountId, version, CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.Code);

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).Include(x => x.Capabilities).SingleAsync(x => x.Id == accountId);
        account.Connection.RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.AVAILABLE);
        var capability = account.Capabilities.Single(x => x.CapabilityCode == "LISTINGS_READ");
        capability.Source.Should().Be("PROBE_INSPECTION", "D-03: not the previous generic \"PROBE\" literal");
    }

    // ================= A-04: probe identity must check token, /users/me AND the account =================

    [Fact]
    public async Task A04_probe_fails_closed_when_the_returned_identity_belongs_to_a_different_account()
    {
        // Account was originally connected/reconnected as seller B (its own ExternalAccountId).
        var (accountId, sellerB) = await ConnectedAccountAsync();
        // But the credential now in use (per the fake's InspectAsync, standing in for a real
        // token/`/users/me` identity that is internally self-consistent) actually belongs to a
        // DIFFERENT seller A. The connector alone cannot catch this — it never receives
        // ExternalAccountId — only the workflow comparing against the account's own DB row can.
        var sellerA = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        sellerA.Should().NotBe(sellerB);
        _harness.ControlPlane.BindAccountIdentity(accountId, sellerA, new Dictionary<string, AccountCapabilityState> { ["LISTINGS_READ"] = AccountCapabilityState.GRANTED });

        long version;
        await using (var db0 = _fixture.CreateContext())
            version = (await db0.Set<MarketplaceAccount>().AsNoTracking().SingleAsync(x => x.Id == accountId)).Version;

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var result = await workflow.ProbeAsync(accountId, version, CancellationToken.None);
        result.Succeeded.Should().BeFalse();
        result.Code.Should().Be("PROBE_IDENTITY_MISMATCH");

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).Include(x => x.Capabilities).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED, "the wrong account must never be left CONNECTED");
        account.Connection.RuntimeAvailability.Should().NotBe(MarketplaceRuntimeAvailability.AVAILABLE, "no runtime-available promotion for a mismatched identity");
        account.Capabilities.Should().BeEmpty("no grant promotion (LISTINGS_READ=GRANTED from the wrong seller must never be written)");
    }

    [Fact]
    public async Task D03_probe_is_blocked_while_a_persisted_retry_after_guard_is_active_and_allowed_once_it_expires()
    {
        var (accountId, externalId) = await ConnectedAccountAsync();
        // A-04: the fake connector's InspectAsync reports whatever identity was explicitly bound
        // for this account — production's real connector always self-consistently reports the
        // identity embedded in the secret it was handed, so tests that expect probe to actually
        // succeed must bind the SAME identity the account itself was created with.
        _harness.ControlPlane.BindAccountIdentity(accountId, externalId, new Dictionary<string, AccountCapabilityState>());
        var retryAfter = _clock.UtcNow.AddMinutes(10);
        await using (var db = _fixture.CreateContext())
        {
            var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            account.RecordRetryAfter(retryAfter, _clock.UtcNow);
            await db.SaveChangesAsync();
        }

        long version;
        await using (var db = _fixture.CreateContext())
            version = (await db.Set<MarketplaceAccount>().AsNoTracking().SingleAsync(x => x.Id == accountId)).Version;

        using (var scope = _factory.Services.CreateScope())
        {
            var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            var blocked = await workflow.ProbeAsync(accountId, version, CancellationToken.None);
            blocked.Succeeded.Should().BeFalse();
            blocked.Code.Should().Be("PROBE_RATE_LIMITED", "D-03: an active persisted guard is honored, never silently bypassed, even by the Owner's own explicit probe");
        }

        // Once the guard has expired, the very next probe is permitted again.
        _clock.UtcNow += TimeSpan.FromMinutes(11);
        using (var scope = _factory.Services.CreateScope())
        {
            var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            var allowed = await workflow.ProbeAsync(accountId, version, CancellationToken.None);
            allowed.Succeeded.Should().BeTrue(allowed.Code);
        }
    }

    [Fact]
    public async Task D03_http_probe_endpoint_returns_503_with_retry_after_while_the_guard_is_active()
    {
        var (accountId, _) = await ConnectedAccountAsync();
        var retryAfter = _clock.UtcNow.AddMinutes(3);
        long version;
        await using (var db = _fixture.CreateContext())
        {
            var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            account.RecordRetryAfter(retryAfter, _clock.UtcNow);
            version = account.Version;
            await db.SaveChangesAsync();
        }

        var owner = await LoggedInOwnerAsync();
        var response = await owner.PostAsync($"/api/commerce/marketplace-accounts/{accountId}/probe", new ReauthorizeMarketplaceAccountRequest(version));
        response.StatusCode.Should().Be((HttpStatusCode)503);
        response.Headers.RetryAfter.Should().NotBeNull();
    }

    // ================= shared helpers (mirrors MarketplaceAuthorizationRt01Tests's own copies) =================

    private async Task<(Guid AccountId, string ExternalAccountId)> ConnectedAccountAsync(IReadOnlyDictionary<string, AccountCapabilityState>? grants = null)
    {
        var channel = await SeedProviderAndChannelAsync();
        var owner = await LoggedInOwnerAsync();
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        var (sessionId, callbackUrl) = await BeginAuthorizationAsync(owner, channel);
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId, grants, AccessExpiresAt: _clock.UtcNow.AddHours(6)));
        (await owner.GetAsync(callbackUrl)).StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.OK, HttpStatusCode.NotFound);
        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().AsNoTracking().SingleAsync(x => x.ExternalAccountId == externalId);
        return (account.Id, externalId);
    }

    private async Task<(Guid SessionId, string CallbackUrl)> BeginAuthorizationAsync(AuthTestClient owner, SalesChannel channel)
    {
        var begin = await owner.PostAsync("/api/commerce/marketplace-authorizations",
            new BeginMarketplaceAuthorizationRequest(FakeMarketplaceConnector.Code, channel.Id, "Conta Fake " + Guid.NewGuid().ToString("N")[..6]));
        begin.StatusCode.Should().Be(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
        var sessionId = document.RootElement.GetProperty("sessionId").GetGuid();
        var authorizationUri = document.RootElement.GetProperty("authorizationUri").GetString()!;
        var state = HttpUtility.ParseQueryString(new Uri(authorizationUri).Query)["state"]!;
        var callbackUrl = $"/api/commerce/marketplace-authorizations/{FakeMarketplaceConnector.Code}/callback?state={Uri.EscapeDataString(state)}&code=fake-code";
        return (sessionId, callbackUrl);
    }

    private async Task<SalesChannel> SeedProviderAndChannelAsync()
    {
        await using var db = _fixture.CreateContext();
        var provider = await db.Set<MarketplaceProvider>().SingleOrDefaultAsync(x => x.Code == FakeMarketplaceConnector.Code);
        var channel = new SalesChannel("MKT-" + Guid.NewGuid().ToString("N")[..10], "Canal Fake", SalesChannelKind.Marketplace, null, null);
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
            Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "Owner Marketplace",
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
