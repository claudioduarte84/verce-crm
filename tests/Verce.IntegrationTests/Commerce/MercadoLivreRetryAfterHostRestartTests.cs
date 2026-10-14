using System.Net;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verce.Api.Auth;
using Verce.Api.Commerce;
using Verce.Infrastructure.Marketplaces.MercadoLivre;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Commerce;
using Verce.Modules.Pricing;
using Verce.Platform.Identity;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// A-03: a persisted <c>RuntimeRetryAfterUntil</c> guard must survive a REAL application restart —
/// not merely "the same process keeps running" but a brand new <see cref="VerceWebApplicationFactory"/>
/// (a fresh DI container, a fresh in-process credential-store singleton, a fresh
/// <see cref="RealAdapterHttpHandler"/> that has never itself sent a single request) reading the
/// SAME Testcontainers PostgreSQL database and the SAME on-disk credential-store root Host A wrote
/// to. This is the genuine two-host proof the coordinator asked for: "blocked-before" is proven by
/// the BRAND NEW host's own handler never once being invoked (SendAsyncInvocationCount == 0), and
/// "actually-executes-after" is proven by that SAME handler genuinely receiving real HTTP calls once
/// the persisted guard has (deterministically, via a shared <see cref="TestClock"/>, never a sleep)
/// expired.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MercadoLivreRetryAfterHostRestartTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private TestClock _clock = null!;
    private string? _sharedCredentialStoreRoot;
    private string? _sharedDataProtectionKeyDirectory;

    private readonly List<Guid> _ownedAccountIds = [];
    private readonly List<Guid> _ownedSessionIds = [];
    private readonly List<Guid> _ownedChannelIds = [];

    public MercadoLivreRetryAfterHostRestartTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _clock = new TestClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await using var db = _fixture.CreateContext();
        if (_ownedAccountIds.Count > 0)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE commerce.marketplace_account_connection SET confirmed_operation_id = NULL
                WHERE marketplace_account_id = ANY({_ownedAccountIds.ToArray()})
                """);
            await db.SaveChangesAsync();
        }
        db.RemoveRange(await db.Set<MarketplaceAccountOperation>()
            .Where(x => (x.MarketplaceAccountId != null && _ownedAccountIds.Contains(x.MarketplaceAccountId.Value))
                     || (x.AuthorizationSessionId != null && _ownedSessionIds.Contains(x.AuthorizationSessionId.Value)))
            .ToListAsync());
        await db.SaveChangesAsync();
        db.RemoveRange(await db.Set<MarketplaceAuthorizationSession>().Where(x => _ownedSessionIds.Contains(x.Id)).ToListAsync());
        await db.SaveChangesAsync();
        db.RemoveRange(await db.Set<MarketplaceAccount>().Where(x => _ownedAccountIds.Contains(x.Id)).ToListAsync());
        await db.SaveChangesAsync();
        db.RemoveRange(await db.Set<SalesChannel>().Where(x => _ownedChannelIds.Contains(x.Id)).ToListAsync());
        await db.SaveChangesAsync();
        // MERCADO_LIVRE provider/capability rows are real, permanent S8C.0 seed data — never deleted.
        if (_sharedCredentialStoreRoot is not null)
        {
            try { if (Directory.Exists(_sharedCredentialStoreRoot)) Directory.Delete(_sharedCredentialStoreRoot, recursive: true); }
            catch { /* best-effort cleanup; the OS temp directory is reclaimed anyway */ }
        }
        if (_sharedDataProtectionKeyDirectory is not null)
        {
            try { if (Directory.Exists(_sharedDataProtectionKeyDirectory)) Directory.Delete(_sharedDataProtectionKeyDirectory, recursive: true); }
            catch { /* best-effort cleanup; the OS temp directory is reclaimed anyway */ }
        }
    }

    [Fact]
    public async Task Persisted_retry_after_guard_survives_a_real_host_restart_and_is_honored_by_a_brand_new_host()
    {
        Guid accountId;
        DateTimeOffset retryUntil;
        // Both the credential-store root AND the Data Protection key ring must be shared across
        // Host A and Host B: VerceWebApplicationFactory otherwise stamps EVERY factory instance
        // with its own random "DataProtection:DevKeyDirectory" (test isolation between UNRELATED
        // tests) — which, left unoverridden here, would make Host B generate brand-new keys unable
        // to decrypt anything Host A encrypted, silently turning "a restarted host" into "a
        // different application with a different key ring" (never true in real production, where
        // ADR-0008/OPERATIONS persist the SAME key ring in Postgres itself). Overriding both to the
        // same directories is what makes this genuinely "the same application, restarted".
        _sharedDataProtectionKeyDirectory = Path.Combine(Path.GetTempPath(), "verce-ml-restart-dp-" + Guid.NewGuid().ToString("N"));

        // ================= Host A: connects the account and persists the guard =================
        // NOTE: harnessA is deliberately NEVER wrapped in `using`/disposed. Its ONLY IDisposable
        // behavior is deleting its own CredentialStoreRoot directory (see
        // MercadoLivreRealAdapterHarness.Dispose) — exactly right for a harness that owns an
        // isolated, single-host test, but exactly wrong here: disposing it before Host B ever
        // starts would delete the shared credential envelope Host B must still read, defeating the
        // entire "restart" premise. This test's own DisposeAsync deletes `_sharedCredentialStoreRoot`
        // once, after BOTH hosts are done with it.
        var harnessA = new MercadoLivreRealAdapterHarness();
        _sharedCredentialStoreRoot = harnessA.CredentialStoreRoot;
        var configA = new Dictionary<string, string?>(harnessA.ExtraConfiguration)
        {
            ["DataProtection:DevKeyDirectory"] = _sharedDataProtectionKeyDirectory,
        };
        await using (var factoryA = new VerceWebApplicationFactory(_fixture.ConnectionString, configA, clockOverride: _clock, configureTestServices: harnessA.ConfigureServices))
        {
            accountId = await ConnectedAccountAsync(factoryA, harnessA);

            retryUntil = _clock.UtcNow.AddMinutes(5);
            await using var db = _fixture.CreateContext();
            var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            account.RecordRetryAfter(retryUntil, _clock.UtcNow);
            await db.SaveChangesAsync();
            // `factoryA` is disposed at the end of this block — a REAL host teardown (its own DI
            // container, its own in-process singletons, its own HttpMessageHandler instance all
            // torn down), never merely "the same objects kept running". `harnessA` itself (and its
            // credential-store directory) deliberately outlives this block — see the note above.
        }

        // ================= Host B: a BRAND NEW host, same Postgres, same credential-store root =================
        using var harnessB = new MercadoLivreRealAdapterHarness();
        var sharedConfig = new Dictionary<string, string?>(harnessB.ExtraConfiguration)
        {
            // The credential envelope Host A's CompleteOnce wrote lives on disk under Host A's own
            // root — Host B must read the SAME root, never a fresh empty one, to genuinely be "the
            // same application, restarted" rather than a different application entirely.
            ["Marketplaces:CredentialStoreRoot"] = _sharedCredentialStoreRoot,
            ["DataProtection:DevKeyDirectory"] = _sharedDataProtectionKeyDirectory,
        };
        await using var factoryB = new VerceWebApplicationFactory(_fixture.ConnectionString, sharedConfig, clockOverride: _clock, configureTestServices: harnessB.ConfigureServices);
        // Accessing .Services forces Host B's host to actually build and start — INCLUDING its own
        // real MarketplaceStartupRecoveryScopedHostedService sweep — before this test reads the
        // account's version, so the version used below is Host B's own authoritative post-startup
        // value, never a value captured before this second host even existed.
        var owner = await LoggedInOwnerAsync(factoryB);

        async Task<long> CurrentVersionAsync()
        {
            await using var db = _fixture.CreateContext();
            return (await db.Set<MarketplaceAccount>().AsNoTracking().SingleAsync(x => x.Id == accountId)).Version;
        }

        // ---- BEFORE the guard expires: Host B must block WITHOUT ever touching the real adapter ----
        var blocked = await owner.PostAsync($"/api/commerce/marketplace-accounts/{accountId}/probe", new ReauthorizeMarketplaceAccountRequest(await CurrentVersionAsync()));
        blocked.StatusCode.Should().Be((HttpStatusCode)503);
        blocked.Headers.RetryAfter.Should().NotBeNull();
        harnessB.Handler.SendAsyncInvocationCount.Should().Be(0,
            "the persisted guard must block this operation BEFORE any real HTTP call ever reaches the adapter — proven at the actual HttpMessageHandler boundary of a BRAND NEW host that never itself created this guard, never merely by an error-code check");

        // ---- advance the SHARED clock past retryUntil — no sleep, deterministic ----
        _clock.UtcNow = retryUntil.AddSeconds(1);

        // ---- AFTER expiry: the SAME brand-new host must actually reach the real adapter ----
        harnessB.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"id":555555555,"site_id":"MLB"}"""));
        harnessB.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"results":["MLB1"]}"""));
        var allowed = await owner.PostAsync($"/api/commerce/marketplace-accounts/{accountId}/probe", new ReauthorizeMarketplaceAccountRequest(await CurrentVersionAsync()));
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
        harnessB.Handler.SendAsyncInvocationCount.Should().Be(2,
            "once the persisted guard has genuinely expired, this SAME brand-new host must actually reach the real adapter — proven by real SendAsync invocations at the handler boundary, never inferred from a status code alone");
    }

    // ================= helpers =================

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private async Task<Guid> ConnectedAccountAsync(VerceWebApplicationFactory factory, MercadoLivreRealAdapterHarness harness)
    {
        var channel = await SeedProviderAndChannelAsync();
        var owner = await LoggedInOwnerAsync(factory);
        var (sessionId, callbackUrl) = await BeginAuthorizationAsync(owner, channel);

        harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"access_token":"initial-access","token_type":"bearer","expires_in":21600,"user_id":555555555,"refresh_token":"initial-refresh"}"""));
        harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"id":555555555,"site_id":"MLB"}"""));
        harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"results":["MLB1"]}"""));

        var callback = await owner.GetAsync(callbackUrl);
        callback.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.OK, HttpStatusCode.NotFound);

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.ProviderCode == MercadoLivreConnector.Code && x.SalesChannelId == channel.Id);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED, "the real adapter's own initial exchange must succeed for this harness to be usable");
        _ownedAccountIds.Add(account.Id);
        harness.Handler.ResetInvocationCount();
        return account.Id;
    }

    private async Task<(Guid SessionId, string CallbackUrl)> BeginAuthorizationAsync(AuthTestClient owner, SalesChannel channel)
    {
        var begin = await owner.PostAsync("/api/commerce/marketplace-authorizations", new BeginMarketplaceAuthorizationRequest(MercadoLivreConnector.Code, channel.Id, "Conta ML Restart " + Guid.NewGuid().ToString("N")[..6]));
        begin.StatusCode.Should().Be(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
        var sessionId = document.RootElement.GetProperty("sessionId").GetGuid();
        _ownedSessionIds.Add(sessionId);
        var authorizationUri = document.RootElement.GetProperty("authorizationUri").GetString()!;
        var state = HttpUtility.ParseQueryString(new Uri(authorizationUri).Query)["state"]!;
        var callbackUrl = $"/api/commerce/marketplace-authorizations/{MercadoLivreConnector.Code}/callback?state={Uri.EscapeDataString(state)}&code=fake-connect-code";
        return (sessionId, callbackUrl);
    }

    private async Task<SalesChannel> SeedProviderAndChannelAsync()
    {
        await using var db = _fixture.CreateContext();
        var provider = await db.Set<MarketplaceProvider>().SingleOrDefaultAsync(x => x.Code == MercadoLivreConnector.Code);
        var channel = new SalesChannel("MKT-MLR-" + Guid.NewGuid().ToString("N")[..8], "Canal ML Restart", SalesChannelKind.Marketplace, null, null);
        if (provider is null) db.Add(new MarketplaceProvider(MercadoLivreConnector.Code, "Mercado Livre"));
        db.Add(channel);
        await db.SaveChangesAsync();
        _ownedChannelIds.Add(channel.Id);
        return channel;
    }

    private async Task<AuthTestClient> LoggedInOwnerAsync(VerceWebApplicationFactory factory)
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        const string password = "a-perfectly-fine-12char-password";
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!await roles.RoleExistsAsync(Roles.Owner))
            (await roles.CreateAsync(new ApplicationRole(Roles.Owner))).Succeeded.Should().BeTrue();
        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "Owner ML Restart",
            IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow
        };
        (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
        (await users.AddToRoleAsync(user, Roles.Owner)).Succeeded.Should().BeTrue();

        var client = new AuthTestClient(factory.CreateHttpsClient());
        await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new LoginRequest(email, password))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await client.EnsureCsrfCookieAsync();
        return client;
    }
}
