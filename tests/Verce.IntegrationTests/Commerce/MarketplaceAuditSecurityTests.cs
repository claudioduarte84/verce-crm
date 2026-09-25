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
using Verce.Platform.Audit;
using Verce.Platform.Identity;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// ADR-0024 G-08 audit security. Persists REAL connect/reconnect/disconnect actions through the
/// real HTTP routes, then inspects the actual persisted <see cref="AuditLogEntry"/> rows (never a
/// DTO) for absence of CredentialReference, StateHash, BrowserBindingHash, ProtectedTransientReference,
/// the authorization code, and the raw state value — regression coverage for the plaintext
/// CredentialReference leak fixed earlier in <c>AuditSaveChangesInterceptor</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MarketplaceAuditSecurityTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;
    private MarketplaceTestHarness _harness = null!;
    private AuthTestClient _owner = null!;
    private SalesChannel _channel = null!;

    public MarketplaceAuditSecurityTests(PostgresFixture fixture) => _fixture = fixture;

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
        db.RemoveRange(await db.Set<AuditLogEntry>().Where(x => x.EntityTable == "marketplace_account" || x.EntityTable == "marketplace_account_connection").ToListAsync());
        await db.SaveChangesAsync();
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
    public async Task Persisted_audit_rows_never_contain_credential_reference_hashes_code_or_state()
    {
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        var sentinelCode = "SENTINEL-AUDIT-CODE-" + Guid.NewGuid().ToString("N");

        // 1. Connect (writes MarketplaceAccount + MarketplaceAccountConnection [Auditable] rows,
        //    a session claim, and an operation confirmation).
        var (session1, callback1State, callback1Url) = await BeginAuthorizationAsync();
        _harness.ControlPlane.Configure(session1, new FakeMarketplaceScenario(externalId));
        var taggedCallback1 = callback1Url.Replace("code=fake-code", "code=" + Uri.EscapeDataString(sentinelCode));
        await _owner.GetAsync(taggedCallback1);

        Guid accountId;
        string credentialReference;
        long version;
        await using (var db = _fixture.CreateContext())
        {
            var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.ExternalAccountId == externalId);
            account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED, "test setup must itself succeed");
            accountId = account.Id;
            credentialReference = account.CredentialReference!;
            version = account.Version;
        }

        // 2. Reconnect (a second credential-changing action, another distinct sentinel code).
        var sentinelCode2 = "SENTINEL-AUDIT-CODE-2-" + Guid.NewGuid().ToString("N");
        var (session2, callback2State, callback2Url) = await BeginReauthorizeAsync(accountId, version);
        _harness.ControlPlane.Configure(session2, new FakeMarketplaceScenario(externalId));
        var taggedCallback2 = callback2Url.Replace("code=fake-code", "code=" + Uri.EscapeDataString(sentinelCode2));
        await _owner.GetAsync(taggedCallback2);

        long versionAfterReconnect;
        string k1Reference;
        await using (var db = _fixture.CreateContext())
        {
            var account = await db.Set<MarketplaceAccount>().SingleAsync(x => x.Id == accountId);
            versionAfterReconnect = account.Version;
            k1Reference = account.CredentialReference!;
        }

        // 3. Disconnect (writes REVOKED + a CONFIRMED DISCONNECT operation).
        var disconnect = await _owner.PostAsync($"/api/commerce/marketplace-accounts/{accountId}/disconnect", new ReauthorizeMarketplaceAccountRequest(versionAfterReconnect));
        disconnect.IsSuccessStatusCode.Should().BeTrue();

        // Inspect the ACTUAL persisted AuditLog rows for this account/connection — not any DTO.
        await using var auditDb = _fixture.CreateContext();
        var auditRows = await auditDb.Set<AuditLogEntry>()
            .Where(x => x.EntityTable == "marketplace_account" || x.EntityTable == "marketplace_account_connection")
            .Where(x => x.EntityId == accountId)
            .ToListAsync();
        auditRows.Should().NotBeEmpty("connect/reconnect/disconnect must all produce audit rows for the account/connection");

        foreach (var row in auditRows)
        {
            var payload = (row.OldValuesJson ?? "") + (row.NewValuesJson ?? "") + string.Join(",", row.ChangedColumns ?? Array.Empty<string>());
            payload.Should().NotContain(credentialReference, "the CONNECT-time credential reference must never appear in audit JSON");
            payload.Should().NotContain(k1Reference, "the reconnected credential reference must never appear in audit JSON");
            payload.Should().NotContain(sentinelCode, "the raw authorization code must never appear in audit JSON");
            payload.Should().NotContain(sentinelCode2);
            payload.Should().NotContain(callback1State, "the raw state value must never appear in audit JSON");
            payload.Should().NotContain(callback2State);
        }

        // The changed-columns payload itself must never NAME the suppressed field either.
        auditRows.SelectMany(x => x.ChangedColumns ?? Array.Empty<string>())
            .Should().NotContain(c => c.Equals("CredentialReference", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Persisted_authorization_session_and_operation_rows_are_never_generically_audited()
    {
        // Sessions/operations are TechnicalEntity, deliberately NOT [Auditable] — ADR-0024 G-08:
        // "do not mark authorization sessions or credential-operation rows [Auditable] and rely
        // on that generic scalar snapshot." Proves no AuditLog row exists for either table at all
        // (their own explicit safe-audit events, if any, are a separate concern from this generic
        // interceptor's scope).
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        var (sessionId, _, callbackUrl) = await BeginAuthorizationAsync();
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId));
        await _owner.GetAsync(callbackUrl);

        await using var db = _fixture.CreateContext();
        var sessionAuditRows = await db.Set<AuditLogEntry>().Where(x => x.EntityTable == "marketplace_authorization_session").ToListAsync();
        var operationAuditRows = await db.Set<AuditLogEntry>().Where(x => x.EntityTable == "marketplace_account_operation").ToListAsync();
        sessionAuditRows.Should().BeEmpty();
        operationAuditRows.Should().BeEmpty();
    }

    private async Task<(Guid SessionId, string State, string CallbackUrl)> BeginAuthorizationAsync()
    {
        var begin = await _owner.PostAsync("/api/commerce/marketplace-authorizations",
            new BeginMarketplaceAuthorizationRequest(FakeMarketplaceConnector.Code, _channel.Id, "Conta Fake Audit " + Guid.NewGuid().ToString("N")[..6]));
        begin.StatusCode.Should().Be(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync());
        return ParseBegin(await begin.Content.ReadAsStringAsync());
    }

    private async Task<(Guid SessionId, string State, string CallbackUrl)> BeginReauthorizeAsync(Guid accountId, long expectedVersion)
    {
        var begin = await _owner.PostAsync($"/api/commerce/marketplace-accounts/{accountId}/reauthorize", new ReauthorizeMarketplaceAccountRequest(expectedVersion));
        begin.StatusCode.Should().Be(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync());
        return ParseBegin(await begin.Content.ReadAsStringAsync());
    }

    private static (Guid SessionId, string State, string CallbackUrl) ParseBegin(string json)
    {
        using var document = JsonDocument.Parse(json);
        var sessionId = document.RootElement.GetProperty("sessionId").GetGuid();
        var authorizationUri = document.RootElement.GetProperty("authorizationUri").GetString()!;
        var state = HttpUtility.ParseQueryString(new Uri(authorizationUri).Query)["state"]!;
        return (sessionId, state, $"/api/commerce/marketplace-authorizations/{FakeMarketplaceConnector.Code}/callback?state={Uri.EscapeDataString(state)}&code=fake-code");
    }

    private async Task<SalesChannel> SeedProviderAndChannelAsync()
    {
        await using var db = _fixture.CreateContext();
        var provider = await db.Set<MarketplaceProvider>().SingleOrDefaultAsync(x => x.Code == FakeMarketplaceConnector.Code);
        var channel = new SalesChannel("MKT-" + Guid.NewGuid().ToString("N")[..10], "Canal Fake Audit", SalesChannelKind.Marketplace, null, null);
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
            Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "Owner Audit",
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
