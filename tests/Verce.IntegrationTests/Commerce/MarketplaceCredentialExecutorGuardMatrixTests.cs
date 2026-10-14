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
/// A-05 (S8C.2 Phase A final consolidation): <see cref="MarketplaceCredentialExecutor"/> is the
/// single shared component every credentialed provider operation must go through — today only
/// <see cref="MarketplaceAuthorizationWorkflow.ProbeAsync"/> (a <see
/// cref="MarketplaceExecutionRequirement.Probe"/> caller); a real <see
/// cref="MarketplaceExecutionRequirement.BusinessCapability"/> caller does not exist yet (Stream B
/// listing-sync reads are explicitly out of scope for this delivery). Since Stream B business code
/// must NOT be written to reach this class, every test here calls <see
/// cref="MarketplaceCredentialExecutor.ExecuteAsync{T}"/> DIRECTLY with a trivial, test-only
/// send delegate that performs no real provider I/O and no business logic whatsoever — it only
/// returns a sentinel string and counts its own invocations, so this class's actual guard/refresh
/// coordination logic is proven through its real public entry point without inventing a Stream B
/// feature to do it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MarketplaceCredentialExecutorGuardMatrixTests : IAsyncLifetime
{
    private const string Capability = "LISTINGS_READ";

    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;
    private MarketplaceTestHarness _harness = null!;
    private TestClock _clock = null!;

    private readonly List<Guid> _ownedAccountIds = [];
    private readonly List<Guid> _ownedSessionIds = [];
    private readonly List<Guid> _ownedChannelIds = [];

    public MarketplaceCredentialExecutorGuardMatrixTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _harness = new MarketplaceTestHarness();
        _clock = new TestClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, _harness.ExtraConfiguration, clockOverride: _clock, configureTestServices: _harness.ConfigureServices);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        _harness.Dispose();
        await using var db = _fixture.CreateContext();
        // H-01-clean from the start: this class tracks exactly the rows it creates and deletes
        // ONLY those — never a blanket delete by provider code (see the sibling
        // MercadoLivreRealAdapterRefreshMatrixTests fix for why that pattern is unsafe).
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
        // This exact composite key (FAKE + LISTINGS_READ) is written ONLY by
        // SeedStructuralCapabilitySupportedAsync in THIS file — unlike an account/session/channel
        // row it has no per-instance ID to track, but its identity is narrow and fully known, so
        // deleting it by that exact composite key is scoped, not a blanket-by-provider-code delete.
        // Without this, the row survives across test METHODS in this same class (it is provider-
        // level, not account-scoped) and silently makes "no structural row" tests order-dependent.
        db.RemoveRange(await db.Set<MarketplaceProviderCapability>().Where(x => x.ProviderCode == FakeMarketplaceConnector.Code && x.CapabilityCode == Capability).ToListAsync());
        await db.SaveChangesAsync();
        // "FAKE" is itself test-only provider identity (never real seed data) but is left alone
        // here too — only ever created once, opportunistically, exactly like every other file that
        // shares this same fake connector across the whole Commerce collection.
    }

    // ================= structural / static guards =================

    [Fact]
    public async Task Inactive_account_is_blocked_before_any_credential_is_touched()
    {
        var accountId = await ConnectedAccountAsync();
        await using (var db = _fixture.CreateContext())
        {
            var account = await db.Set<MarketplaceAccount>().SingleAsync(x => x.Id == accountId);
            account.Deactivate();
            await db.SaveChangesAsync();
        }

        var calls = new int[1];
        var act = () => ExecuteProbeAsync(accountId, calls);
        (await act.Should().ThrowAsync<MarketplaceExecutionBlockedException>()).Which.Reason.Should().Be("ACCOUNT_INACTIVE");
        calls[0].Should().Be(0, "a guard failure must never invoke the test-only send delegate");
    }

    [Fact]
    public async Task Inactive_channel_is_blocked_before_any_credential_is_touched()
    {
        Guid channelId;
        Guid accountId;
        (accountId, channelId) = await ConnectedAccountWithChannelAsync();
        await using (var db = _fixture.CreateContext())
        {
            var channel = await db.Set<SalesChannel>().SingleAsync(x => x.Id == channelId);
            channel.Deactivate();
            await db.SaveChangesAsync();
        }

        var calls = new int[1];
        var act = () => ExecuteProbeAsync(accountId, calls);
        (await act.Should().ThrowAsync<MarketplaceExecutionBlockedException>()).Which.Reason.Should().Be("CHANNEL_INVALID");
        calls[0].Should().Be(0);
    }

    [Fact]
    public async Task BusinessCapability_requirement_with_no_capability_code_fails_closed_never_silently_bypassing_guards()
    {
        var accountId = await ConnectedAccountAsync();
        var calls = new int[1];
        var act = () => ExecuteBusinessAsync(accountId, requiredCapabilityCode: null, calls);
        (await act.Should().ThrowAsync<MarketplaceExecutionBlockedException>()).Which.Reason.Should().Be("CAPABILITY_CODE_REQUIRED");
        calls[0].Should().Be(0);
    }

    [Fact]
    public async Task BusinessCapability_requirement_with_no_structural_row_is_unsupported()
    {
        var accountId = await ConnectedAccountAsync();
        var calls = new int[1];
        // No MarketplaceProviderCapability row at all for this capability code -> StructuralState
        // resolves to UNKNOWN, never SUPPORTED.
        var act = () => ExecuteBusinessAsync(accountId, Capability, calls);
        (await act.Should().ThrowAsync<MarketplaceExecutionBlockedException>()).Which.Reason.Should().Be("CAPABILITY_UNSUPPORTED");
        calls[0].Should().Be(0);
    }

    [Fact]
    public async Task BusinessCapability_requirement_with_structural_support_but_no_grant_is_unknown()
    {
        var accountId = await ConnectedAccountAsync();
        await SeedStructuralCapabilitySupportedAsync();
        var calls = new int[1];
        var act = () => ExecuteBusinessAsync(accountId, Capability, calls);
        (await act.Should().ThrowAsync<MarketplaceExecutionBlockedException>()).Which.Reason.Should().Be("GRANT_UNKNOWN");
        calls[0].Should().Be(0);
    }

    [Fact]
    public async Task BusinessCapability_requirement_with_denied_grant_fails_closed()
    {
        var accountId = await ConnectedAccountAsync();
        await SeedStructuralCapabilitySupportedAsync();
        await SetAccountGrantAsync(accountId, AccountCapabilityState.DENIED);
        var calls = new int[1];
        var act = () => ExecuteBusinessAsync(accountId, Capability, calls);
        (await act.Should().ThrowAsync<MarketplaceExecutionBlockedException>()).Which.Reason.Should().Be("GRANT_DENIED");
        calls[0].Should().Be(0);
    }

    [Fact]
    public async Task Authorization_not_CONNECTED_blocks_both_probe_and_business_requirements()
    {
        var accountId = await ConnectedAccountAsync();
        await SeedStructuralCapabilitySupportedAsync();
        await SetAccountGrantAsync(accountId, AccountCapabilityState.GRANTED);
        await using (var db = _fixture.CreateContext())
        {
            var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            account.RequireReauthorization("TEST_FORCED_REAUTH", _clock.UtcNow);
            await db.SaveChangesAsync();
        }

        var calls = new int[1];
        var probeAct = () => ExecuteProbeAsync(accountId, calls);
        (await probeAct.Should().ThrowAsync<MarketplaceExecutionBlockedException>()).Which.Reason.Should().Be("AUTHORIZATION_NOT_USABLE");
        var businessAct = () => ExecuteBusinessAsync(accountId, Capability, calls);
        (await businessAct.Should().ThrowAsync<MarketplaceExecutionBlockedException>()).Which.Reason.Should().Be("AUTHORIZATION_NOT_USABLE");
        calls[0].Should().Be(0);
    }

    [Fact]
    public async Task Runtime_UNAVAILABLE_blocks_business_capability_but_not_probe()
    {
        var accountId = await ConnectedAccountAsync();
        await SeedStructuralCapabilitySupportedAsync();
        await SetAccountGrantAsync(accountId, AccountCapabilityState.GRANTED);
        await using (var db = _fixture.CreateContext())
        {
            var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            account.RecordRuntimeFailure("TRANSIENT", "TEST_FORCED_UNAVAILABLE", _clock.UtcNow);
            await db.SaveChangesAsync();
        }

        var calls = new int[1];
        var businessAct = () => ExecuteBusinessAsync(accountId, Capability, calls);
        (await businessAct.Should().ThrowAsync<MarketplaceExecutionBlockedException>()).Which.Reason.Should().Be("RUNTIME_UNAVAILABLE");
        calls[0].Should().Be(0, "the blocked business call must never reach the test-only send delegate");

        // ADR-0024 §5: "Owner probe is a recovery exception to runtime UNAVAILABLE" — the SAME
        // UNAVAILABLE state must NOT block a Probe requirement.
        var probeResult = await ExecuteProbeAsync(accountId, calls);
        probeResult.Should().Be("TEST_ONLY_OPERATION_EXECUTED");
        calls[0].Should().Be(1, "probe must actually reach the send delegate despite runtime UNAVAILABLE");
    }

    [Fact]
    public async Task Active_retry_after_guard_blocks_both_probe_and_business_requirements_until_it_expires()
    {
        var accountId = await ConnectedAccountAsync();
        await SeedStructuralCapabilitySupportedAsync();
        await SetAccountGrantAsync(accountId, AccountCapabilityState.GRANTED);
        var retryUntil = _clock.UtcNow.AddMinutes(5);
        await using (var db = _fixture.CreateContext())
        {
            var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            account.RecordRetryAfter(retryUntil, _clock.UtcNow);
            await db.SaveChangesAsync();
        }

        var calls = new int[1];
        var probeAct = () => ExecuteProbeAsync(accountId, calls);
        (await probeAct.Should().ThrowAsync<MarketplaceExecutionBlockedException>()).Which.Reason.Should().Be("RATE_LIMITED");
        calls[0].Should().Be(0);

        _clock.UtcNow = retryUntil.AddSeconds(1);
        var result = await ExecuteProbeAsync(accountId, calls);
        result.Should().Be("TEST_ONLY_OPERATION_EXECUTED");
        calls[0].Should().Be(1, "once the persisted guard has genuinely expired the call must actually execute");
    }

    [Fact]
    public async Task Pending_blocking_operation_prevents_execution_even_though_every_static_guard_passes()
    {
        var accountId = await ConnectedAccountAsync();
        await using (var db = _fixture.CreateContext())
        {
            var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).AsNoTracking().SingleAsync(x => x.Id == accountId);
            db.Add(new MarketplaceAccountOperation(MarketplaceAccountOperationKind.REFRESH, account.ProviderCode, accountId, null,
                account.Version, account.CredentialReference, account.Connection.ConfirmedCredentialVersion, _clock.UtcNow));
            await db.SaveChangesAsync();
        }

        var calls = new int[1];
        var act = () => ExecuteProbeAsync(accountId, calls);
        (await act.Should().ThrowAsync<MarketplaceExecutionBlockedException>()).Which.Reason.Should().Be("AUTHORIZATION_NOT_USABLE");
        calls[0].Should().Be(0);
    }

    [Fact]
    public async Task Confirmed_credential_version_mismatch_against_the_store_fails_closed_as_store_unavailable()
    {
        var accountId = await ConnectedAccountAsync();
        // Corrupt ONLY the aggregate's own bookkeeping of the confirmed version, never the store
        // envelope itself — proves the executor genuinely cross-checks the two rather than trusting
        // its own row.
        await using (var db = _fixture.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE commerce.marketplace_account_connection SET confirmed_credential_version = 999
                WHERE marketplace_account_id = {accountId}
                """);
        }

        var calls = new int[1];
        var act = () => ExecuteProbeAsync(accountId, calls);
        (await act.Should().ThrowAsync<MarketplaceExecutionBlockedException>()).Which.Reason.Should().Be("CREDENTIAL_STORE_UNAVAILABLE");
        calls[0].Should().Be(0);
    }

    [Fact]
    public async Task Missing_confirmed_credential_pointer_while_nominally_CONNECTED_fails_closed()
    {
        var accountId = await ConnectedAccountAsync();
        // This exact combination (CONNECTED + no confirmed credential pointer) is unrepresentable
        // through the aggregate's own public API (S8C.1/ADR-0024 invariants keep them in lock-step)
        // — it is exercised here only as defense-in-depth against a data anomaly, via a direct
        // column write that violates no DB CHECK constraint (there is none tying the two together).
        await using (var db = _fixture.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE commerce.marketplace_account_connection SET confirmed_credential_version = NULL, confirmed_operation_id = NULL
                WHERE marketplace_account_id = {accountId}
                """);
        }

        var calls = new int[1];
        var act = () => ExecuteProbeAsync(accountId, calls);
        (await act.Should().ThrowAsync<MarketplaceExecutionBlockedException>()).Which.Reason.Should().Be("AUTHORIZATION_NOT_USABLE");
        calls[0].Should().Be(0);
    }

    // ================= A-05 bug 2: proactive refresh margin actually forces a refresh =================

    [Fact]
    public async Task Token_already_expired_forces_a_real_refresh_before_the_send_delegate_runs()
    {
        var accountId = await ConnectedAccountAsync();
        await using (var db = _fixture.CreateContext())
        {
            var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            account.Connection.AccessExpiresAt.Should().NotBeNull();
        }
        _clock.UtcNow += TimeSpan.FromHours(7); // well past the 6h fake expiry

        var calls = new int[1];
        var result = await ExecuteProbeAsync(accountId, calls);
        result.Should().Be("TEST_ONLY_OPERATION_EXECUTED");
        calls[0].Should().Be(1);
        _harness.ControlPlane.RefreshCallCount(accountId).Should().Be(1, "an expired token must force exactly one real connector refresh before the operation runs");

        await using var db2 = _fixture.CreateContext();
        var refreshed = await db2.Set<MarketplaceAccount>().Include(x => x.Connection).AsNoTracking().SingleAsync(x => x.Id == accountId);
        refreshed.Connection.ConfirmedCredentialVersion.Should().Be(2, "the executor must use the POST-refresh confirmed version, never the stale pre-refresh one");
    }

    [Fact]
    public async Task Token_inside_the_refresh_margin_but_not_yet_technically_expired_still_forces_a_real_refresh()
    {
        // A-05 bug 2 regression: before the fix, RefreshAsync's own freshness short-circuit
        // (AccessExpiresAt > now => return "REFRESH_NOT_REQUIRED" with NO connector call) silently
        // no-opped this exact case, making the whole margin concept dead code.
        var accountId = await ConnectedAccountAsync();
        DateTimeOffset expiry;
        await using (var db = _fixture.CreateContext())
        {
            var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).AsNoTracking().SingleAsync(x => x.Id == accountId);
            expiry = account.Connection.AccessExpiresAt!.Value;
        }
        // MercadoLivreOptions.AccessTokenRefreshMarginSeconds defaults to 300s (5 min) — move the
        // clock to 2 minutes before the real expiry: strictly INSIDE that margin, yet
        // AccessExpiresAt is still (barely) in the future, which is exactly the case the bug 2 fix
        // targets (RefreshAsync's own freshness short-circuit must be bypassed by `force: true`).
        _clock.UtcNow = expiry - TimeSpan.FromMinutes(2);
        (expiry > _clock.UtcNow).Should().BeTrue("this case only exists when the token has NOT yet technically expired");

        var calls = new int[1];
        var result = await ExecuteProbeAsync(accountId, calls);
        result.Should().Be("TEST_ONLY_OPERATION_EXECUTED");
        calls[0].Should().Be(1);
        _harness.ControlPlane.RefreshCallCount(accountId).Should().Be(1,
            "AccessExpiresAt > now but inside the configured margin must still force exactly one real refresh, never silently no-op");
    }

    [Fact]
    public async Task Successful_margin_triggered_refresh_is_immediately_followed_by_the_business_capability_execution()
    {
        var accountId = await ConnectedAccountAsync();
        await SeedStructuralCapabilitySupportedAsync();
        await SetAccountGrantAsync(accountId, AccountCapabilityState.GRANTED);
        _clock.UtcNow += TimeSpan.FromHours(7);

        var calls = new int[1];
        var result = await ExecuteBusinessAsync(accountId, Capability, calls);
        result.Should().Be("TEST_ONLY_OPERATION_EXECUTED");
        calls[0].Should().Be(1);
        _harness.ControlPlane.RefreshCallCount(accountId).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_callers_inside_the_margin_converge_on_exactly_one_real_refresh()
    {
        // The per-account single-flight semaphore inside MarketplaceAuthorizationWorkflow.RefreshAsync
        // guarantees only ONE real provider refresh ever happens — it does NOT guarantee every
        // concurrent caller silently succeeds: a caller whose own HasBlockingOperationAsync check
        // lands while the winning caller's refresh operation is still PENDING (not yet CONFIRMED)
        // is CORRECTLY, legitimately blocked with AUTHORIZATION_NOT_USABLE (ADR-0024's own
        // "an in-flight credential operation blocks other concurrent use" arbiter rule) rather than
        // silently queuing past it. This test proves the one invariant that actually matters: no
        // matter how many callers race in, the connector is invoked at most once, and at least one
        // caller genuinely completes end-to-end.
        var accountId = await ConnectedAccountAsync();
        _clock.UtcNow += TimeSpan.FromHours(7);

        const int concurrency = 6;
        var results = await Task.WhenAll(BuildConcurrentProbeTasks(accountId, concurrency));

        results.Should().OnlyContain(r => r == "TEST_ONLY_OPERATION_EXECUTED" || r == "BLOCKED:AUTHORIZATION_NOT_USABLE",
            "every concurrent caller must either genuinely execute or be legitimately blocked by the in-flight refresh operation — never any other outcome");
        results.Should().Contain("TEST_ONLY_OPERATION_EXECUTED", "at least one concurrent caller must actually complete the margin-triggered refresh and then execute");
        _harness.ControlPlane.RefreshCallCount(accountId).Should().Be(1,
            "no matter how many callers raced in, exactly one real provider refresh must ever happen — the core single-flight safety invariant, never a double POST");
    }

    private IEnumerable<Task<string>> BuildConcurrentProbeTasks(Guid accountId, int concurrency) =>
        Enumerable.Range(0, concurrency).Select(_ => ExecuteConcurrentProbeAsync(accountId));

    private async Task<string> ExecuteConcurrentProbeAsync(Guid accountId)
    {
        using var scope = _factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<MarketplaceCredentialExecutor>();
        try
        {
            return await executor.ExecuteAsync(accountId, MarketplaceExecutionRequirement.Probe,
                (_, _) => Task.FromResult("TEST_ONLY_OPERATION_EXECUTED"), CancellationToken.None);
        }
        catch (MarketplaceExecutionBlockedException ex)
        {
            return "BLOCKED:" + ex.Reason;
        }
    }

    // ================= execution helpers (test-only send delegate; no business logic) =================

    // `calls` is a single-element counter array (never a `ref int` — async methods cannot declare
    // ref/out parameters) so the count survives being read by the caller even when ExecuteAsync
    // throws before ever reaching the send delegate: the array is the same heap object on both
    // sides of the call, updated in place.
    private async Task<string> ExecuteProbeAsync(Guid accountId, int[] calls)
    {
        using var scope = _factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<MarketplaceCredentialExecutor>();
        return await executor.ExecuteAsync(accountId, MarketplaceExecutionRequirement.Probe,
            (_, _) => { calls[0]++; return Task.FromResult("TEST_ONLY_OPERATION_EXECUTED"); }, CancellationToken.None);
    }

    private async Task<string> ExecuteBusinessAsync(Guid accountId, string? requiredCapabilityCode, int[] calls)
    {
        using var scope = _factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<MarketplaceCredentialExecutor>();
        return await executor.ExecuteAsync(accountId, MarketplaceExecutionRequirement.BusinessCapability,
            (_, _) => { calls[0]++; return Task.FromResult("TEST_ONLY_OPERATION_EXECUTED"); }, CancellationToken.None, requiredCapabilityCode);
    }

    // ================= seeding helpers =================

    private async Task SeedStructuralCapabilitySupportedAsync()
    {
        await using var db = _fixture.CreateContext();
        var exists = await db.Set<MarketplaceProviderCapability>().AnyAsync(x => x.ProviderCode == FakeMarketplaceConnector.Code && x.CapabilityCode == Capability);
        if (!exists) db.Add(new MarketplaceProviderCapability(FakeMarketplaceConnector.Code, Capability, ProviderCapabilityState.SUPPORTED));
        await db.SaveChangesAsync();
    }

    private async Task SetAccountGrantAsync(Guid accountId, AccountCapabilityState state)
    {
        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Capabilities).SingleAsync(x => x.Id == accountId);
        account.SetCapability(Capability, state, "TEST_ONLY", null, _clock.UtcNow);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> ConnectedAccountAsync()
    {
        var (accountId, _) = await ConnectedAccountWithChannelAsync();
        return accountId;
    }

    private async Task<(Guid AccountId, Guid ChannelId)> ConnectedAccountWithChannelAsync()
    {
        var channel = await SeedProviderAndChannelAsync();
        var owner = await LoggedInOwnerAsync();
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        var (sessionId, callbackUrl) = await BeginAuthorizationAsync(owner, channel);
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId, AccessExpiresAt: _clock.UtcNow.AddHours(6)));
        (await owner.GetAsync(callbackUrl)).StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.OK, HttpStatusCode.NotFound);

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().AsNoTracking().SingleAsync(x => x.ExternalAccountId == externalId);
        _ownedAccountIds.Add(account.Id);
        // The real refresh connector call needs to resolve an identity for InspectAsync-style
        // callers; Probe/BusinessCapability guard tests here never call InspectAsync, but binding
        // costs nothing and keeps this harness consistent with the sibling refresh/probe tests.
        _harness.ControlPlane.BindAccountIdentity(account.Id, externalId, new Dictionary<string, AccountCapabilityState>());
        return (account.Id, channel.Id);
    }

    private async Task<(Guid SessionId, string CallbackUrl)> BeginAuthorizationAsync(AuthTestClient owner, SalesChannel channel)
    {
        var begin = await owner.PostAsync("/api/commerce/marketplace-authorizations",
            new BeginMarketplaceAuthorizationRequest(FakeMarketplaceConnector.Code, channel.Id, "Conta Fake " + Guid.NewGuid().ToString("N")[..6]));
        begin.StatusCode.Should().Be(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
        var sessionId = document.RootElement.GetProperty("sessionId").GetGuid();
        _ownedSessionIds.Add(sessionId);
        var authorizationUri = document.RootElement.GetProperty("authorizationUri").GetString()!;
        var state = HttpUtility.ParseQueryString(new Uri(authorizationUri).Query)["state"]!;
        var callbackUrl = $"/api/commerce/marketplace-authorizations/{FakeMarketplaceConnector.Code}/callback?state={Uri.EscapeDataString(state)}&code=fake-code";
        return (sessionId, callbackUrl);
    }

    private async Task<SalesChannel> SeedProviderAndChannelAsync()
    {
        await using var db = _fixture.CreateContext();
        var provider = await db.Set<MarketplaceProvider>().SingleOrDefaultAsync(x => x.Code == FakeMarketplaceConnector.Code);
        var channel = new SalesChannel("MKT-CEX-" + Guid.NewGuid().ToString("N")[..8], "Canal Fake Executor", SalesChannelKind.Marketplace, null, null);
        if (provider is null) db.Add(new MarketplaceProvider(FakeMarketplaceConnector.Code, "Fake Marketplace"));
        db.Add(channel);
        await db.SaveChangesAsync();
        _ownedChannelIds.Add(channel.Id);
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
            Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "Owner Executor Matrix",
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
