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
using Verce.Infrastructure.Marketplaces.MercadoLivre;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Commerce;
using Verce.Modules.Pricing;
using Verce.Platform.Identity;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// A-01: two GENUINE cancellation seams for <see cref="MarketplaceAuthorizationWorkflow.RefreshAsync"/>
/// — never a heuristic, never a pre-set fault standing in for a real race.
///
/// (1) Pre-send: <see cref="FaultInjectingCredentialStore.EnqueueUseConfirmedFault"/> makes the
/// REAL <c>IProtectedCredentialStore.UseConfirmedAsync</c> call (the credential read that always
/// happens strictly BEFORE the connector could ever invoke <c>HttpClient.SendAsync</c>) throw a
/// real <see cref="OperationCanceledException"/> — proving NOT_SENT with
/// <see cref="RealAdapterHttpHandler.SendAsyncInvocationCount"/> at exactly 0, never inferred.
///
/// (2) Post-send: <see cref="RealAdapterHttpHandler.WaitForNextSendEnteredAsync"/> lets this test
/// know, via a real signal (never a delay/poll), the exact instant the real adapter's HTTP call
/// has been counted — then blocks that same call on a second, cancellation-observing gate. Only
/// once the test has that proof does it cancel the SAME <see cref="CancellationToken"/> the
/// workflow call itself was given, producing a genuine mid-flight cancellation indistinguishable
/// (from the workflow's point of view) from a real slow socket being torn down.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MarketplaceRefreshCancellationSeamTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;
    private MercadoLivreRealAdapterHarness _harness = null!;
    private TestClock _clock = null!;

    private readonly List<Guid> _ownedAccountIds = [];
    private readonly List<Guid> _ownedSessionIds = [];
    private readonly List<Guid> _ownedChannelIds = [];

    public MarketplaceRefreshCancellationSeamTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _harness = new MercadoLivreRealAdapterHarness { InjectCasFaults = true };
        _clock = new TestClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, _harness.ExtraConfiguration, clockOverride: _clock, configureTestServices: _harness.ConfigureServices);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        _harness.Dispose();
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
    }

    [Fact]
    public async Task Pre_send_cancellation_during_the_credential_read_is_proven_NOT_SENT_with_zero_real_HTTP_calls()
    {
        var accountId = await ConnectedAccountAsync();
        _clock.UtcNow += TimeSpan.FromHours(7); // past the 6h fake expiry — RefreshAsync will not short-circuit
        long preVersion; DateTimeOffset? preExpiry;
        await using (var db0 = _fixture.CreateContext())
        {
            var pre = await db0.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            preVersion = pre.Connection.ConfirmedCredentialVersion!.Value;
            preExpiry = pre.Connection.AccessExpiresAt;
        }

        // A REAL cancellation observed inside the REAL store's own read call — strictly before the
        // connector could ever touch HttpClient.SendAsync.
        _harness.FaultStore!.EnqueueUseConfirmedFault(new OperationCanceledException("TEST_ONLY_GENUINE_PRE_SEND_CANCELLATION"));

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Code.Should().Be("REFRESH_NOT_SENT");
        _harness.Handler.SendAsyncInvocationCount.Should().Be(0, "the real adapter's HttpMessageHandler must NEVER be reached — this is what makes NOT_SENT genuine, not inferred from the exception's type");

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED, "a proven-NOT_SENT failure preserves existing authorization exactly — never forces reauthorization for a request that never reached the provider");
        account.Connection.RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.UNAVAILABLE);
        account.Connection.SafeFailureCode.Should().Be("REFRESH_NOT_SENT");
        account.Connection.ConfirmedCredentialVersion.Should().Be(preVersion, "the confirmed credential pointer must be byte-for-byte unchanged — nothing was ever sent, so nothing could have been superseded");
        account.Connection.AccessExpiresAt.Should().Be(preExpiry, "the access-token expiry must also be untouched by a proven pre-send failure");

        var operation = await db.Set<MarketplaceAccountOperation>().AsNoTracking()
            .Where(x => x.MarketplaceAccountId == accountId && x.Kind == MarketplaceAccountOperationKind.REFRESH)
            .OrderByDescending(x => x.CreatedAt).FirstAsync();
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.FAIL_CLOSED, "no operation may be left dangling PENDING after a proven pre-send failure");
        // The operation row is marked EXTERNAL_IN_FLIGHT at creation time (before the credential
        // read even runs) — that phase describes "a credential operation is underway for this
        // account", not "bytes are known to be in flight to the provider". What matters here is the
        // DECISION reaching a terminal FAIL_CLOSED — never a row left PENDING/dangling.
        operation.Phase.Should().Be(MarketplaceAccountOperationPhase.EXTERNAL_IN_FLIGHT);
        operation.SafeResultCode.Should().Be("REFRESH_NOT_SENT");
        operation.CleanupState.Should().Be(MarketplaceAccountOperationCleanupState.PENDING, "cleanup must be explicitly requested for this terminal row, never left NOT_REQUIRED by accident");
    }

    [Fact]
    public async Task Post_send_cancellation_with_a_genuine_in_flight_HTTP_call_is_SENT_OR_UNKNOWN_with_exactly_one_real_send()
    {
        var accountId = await ConnectedAccountAsync();
        _clock.UtcNow += TimeSpan.FromHours(7);
        long preVersion;
        await using (var db0 = _fixture.CreateContext())
            preVersion = (await db0.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId)).Connection.ConfirmedCredentialVersion!.Value;

        var entered = _harness.Handler.WaitForNextSendEnteredAsync();
        using var cts = new CancellationTokenSource();

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var refreshTask = workflow.RefreshAsync(accountId, cancellationToken: cts.Token);

        // Real proof — not a delay, not a poll — that SendAsync has genuinely been entered (and
        // therefore genuinely counted) before this test does anything else.
        await entered.WaitAsync(TimeSpan.FromSeconds(10));
        _harness.Handler.SendAsyncInvocationCount.Should().Be(1, "SendAsync must already be counted at this exact instant — proven, not assumed");

        // NOW cancel — a real mid-flight cancellation of the exact token the workflow call is using.
        await cts.CancelAsync();

        var result = await refreshTask;
        result.Succeeded.Should().BeFalse();
        result.Code.Should().Be("REAUTHORIZATION_REQUIRED", "SENT_OR_UNKNOWN can never be treated as safe to silently retry/ignore — it fails closed toward reauthorization");
        _harness.Handler.SendAsyncInvocationCount.Should().Be(1, "exactly one real send — cancellation must never trigger a hidden retry");

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED, "SENT_OR_UNKNOWN after a refresh must fail closed toward reauthorization, never silently preserve CONNECTED");
        account.Connection.SafeFailureCode.Should().Be("REFRESH_OUTCOME_UNKNOWN");
        account.Connection.ConfirmedCredentialVersion.Should().Be(preVersion, "nothing was ever actually persisted for this attempt — the confirmed pointer must be untouched");

        var operation = await db.Set<MarketplaceAccountOperation>().AsNoTracking()
            .Where(x => x.MarketplaceAccountId == accountId && x.Kind == MarketplaceAccountOperationKind.REFRESH)
            .OrderByDescending(x => x.CreatedAt).FirstAsync();
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.FAIL_CLOSED, "no dangling PENDING/EXTERNAL_IN_FLIGHT operation may survive this — the terminal arbiter must resolve it even though the provider's own outcome is genuinely unknown");
        operation.SafeResultCode.Should().Be("REFRESH_OUTCOME_UNKNOWN");

        var owner = await LoggedInOwnerAsync();
        var probe = await owner.PostAsync($"/api/commerce/marketplace-accounts/{accountId}/probe", new ReauthorizeMarketplaceAccountRequest(account.Version));
        probe.StatusCode.Should().Be(HttpStatusCode.Conflict, "CONNECTED->REAUTHORIZATION_REQUIRED is a genuine state change probe must now respect — never a bare 200 as if nothing happened");
    }

    [Fact]
    public async Task Terminalization_cleanup_budget_is_created_fresh_after_the_provider_flight_completes_never_at_flight_start()
    {
        // A-01, final proof: a long external provider call must never be able to consume the LATER
        // terminalization/cleanup budget. RefreshAsync's own `_cleanupBudgetFactory` seam (default
        // behavior unchanged for every other caller) lets THIS test record, on the SAME deterministic
        // TestClock the flight itself experiences, the exact instant each fresh budget is
        // constructed — no real sleep, no broad new time-abstraction, no altered production wiring
        // for anyone who does not supply this optional constructor parameter.
        var accountId = await ConnectedAccountAsync();
        _clock.UtcNow += TimeSpan.FromHours(7); // past the 6h fake expiry — forces a real refresh attempt
        var flightStartedAt = _clock.UtcNow;

        using var scope = _factory.Services.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var registry = scope.ServiceProvider.GetRequiredService<IMarketplaceConnectorRegistry>();
        var store = scope.ServiceProvider.GetRequiredService<IProtectedCredentialStore>();
        var gate = scope.ServiceProvider.GetRequiredService<ProviderExecutionGate>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>(); // the SAME _clock instance (VerceWebApplicationFactory replaces IClock with it)
        var executor = scope.ServiceProvider.GetRequiredService<MarketplaceCredentialExecutor>();

        ReferenceEquals(clock, _clock).Should().BeTrue("VerceWebApplicationFactory must resolve IClock to the exact TestClock instance this test controls, or every clock-based assertion below is meaningless");
        var recordedBudgetCreations = new List<DateTimeOffset>();
        var workflow = new MarketplaceAuthorizationWorkflow(uow, registry, store, gate, clock, executor,
            cleanupBudgetFactory: budget => { recordedBudgetCreations.Add(_clock.UtcNow); return new CancellationTokenSource(budget); });

        // No real barrier/real wait here deliberately: an earlier version of this test held the
        // REAL RealAdapterHttpHandler.SendAsync call open on a TaskCompletionSource — which raced
        // MarketplaceHttpSender's own internal REAL 20-second HTTP timeout (a genuine
        // CancellationTokenSource tied to the system clock, unrelated to this test's fake IClock)
        // and occasionally lost, corrupting the very outcome being tested. Advancing the fake clock
        // FROM INSIDE the response-producing callback instead achieves the identical proof — "time
        // genuinely passes, on the SAME clock the flight experiences, while SendAsync is still
        // executing and has not yet returned" — with zero real waiting and zero risk of a real
        // per-attempt HTTP timeout ever firing.
        var flightOutlivedCleanupBudgetAt = flightStartedAt + TimeSpan.FromSeconds(30);
        _harness.Handler.EnqueueResponse(_ =>
        {
            // Still "inside" SendAsync's own call frame (the handler has not returned yet) — this
            // is genuinely BEFORE the flight completes, exactly like the barrier-based design, just
            // without a real await.
            _clock.UtcNow = flightOutlivedCleanupBudgetAt;
            return JsonResponse(HttpStatusCode.OK, """{"access_token":"slow-flight-access","token_type":"bearer","expires_in":21600,"user_id":555555555,"refresh_token":"slow-flight-refresh"}""");
        });

        var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.Code);
        result.Code.Should().Be("REFRESHED");
        _harness.Handler.SendAsyncInvocationCount.Should().Be(1, "exactly one real send for the whole operation — the long flight was slow, never retried");

        recordedBudgetCreations.Should().ContainSingle(
            "the success path creates exactly one fresh terminalization budget (the `terminal` CTS just before CAS) — never the separate pre-send/ambiguous-failure budgets, which this path never reaches");
        recordedBudgetCreations.Single().Should().BeOnOrAfter(flightOutlivedCleanupBudgetAt,
            "the terminalization budget must be constructed AFTER the flight had already run 30s past its own nominal 10s cleanup duration on this SAME clock — proving the budget derives from T1 (flight completion), never from T0 (flight start), which would already be expired by the time terminalization needed it");

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED);
        account.Connection.ConfirmedCredentialVersion.Should().Be(2, "terminalization completed successfully inside the freshly-created budget — the refresh actually persisted");
        account.Connection.AccessExpiresAt.Should().NotBeNull();

        var operation = await db.Set<MarketplaceAccountOperation>().AsNoTracking()
            .Where(x => x.MarketplaceAccountId == accountId && x.Kind == MarketplaceAccountOperationKind.REFRESH)
            .OrderByDescending(x => x.CreatedAt).FirstAsync();
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.CONFIRMED);
        operation.SafeResultCode.Should().Be("REFRESHED");

        // RequiredAction proof: fetch the CURRENT version immediately before the call (never the
        // one captured earlier in this method) — the account's own optimistic-concurrency version
        // is irrelevant to what this test is proving and must never make an otherwise-unrelated
        // assertion flaky.
        var owner = await LoggedInOwnerAsync();
        long currentVersion;
        await using (var versionDb = _fixture.CreateContext())
            currentVersion = (await versionDb.Set<MarketplaceAccount>().AsNoTracking().SingleAsync(x => x.Id == accountId)).Version;
        // Probe itself makes two REAL HTTP calls (InspectAsync: /users/me + the LISTINGS_READ grant
        // probe) through the credential executor — script both, or the handler's own "no scripted
        // response queued" InvalidOperationException would surface as an unrelated 409 here.
        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"id":555555555,"site_id":"MLB"}"""));
        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"results":["MLB1"]}"""));
        var probe = await owner.PostAsync($"/api/commerce/marketplace-accounts/{accountId}/probe", new ReauthorizeMarketplaceAccountRequest(currentVersion));
        probe.StatusCode.Should().NotBe(HttpStatusCode.Conflict, "CONNECTED with a genuinely refreshed credential must not report a stale/blocked RequiredAction");
    }

    // ================= helpers =================

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private async Task<Guid> ConnectedAccountAsync()
    {
        var channel = await SeedProviderAndChannelAsync();
        var owner = await LoggedInOwnerAsync();
        var (sessionId, callbackUrl) = await BeginAuthorizationAsync(owner, channel);

        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"access_token":"initial-access","token_type":"bearer","expires_in":21600,"user_id":555555555,"refresh_token":"initial-refresh"}"""));
        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"id":555555555,"site_id":"MLB"}"""));
        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"results":["MLB1"]}"""));

        var callback = await owner.GetAsync(callbackUrl);
        callback.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.OK, HttpStatusCode.NotFound);

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.ProviderCode == MercadoLivreConnector.Code && x.SalesChannelId == channel.Id);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED, "the real adapter's own initial exchange must succeed for this harness to be usable");
        _ownedAccountIds.Add(account.Id);
        // Every test measures ITS OWN SendAsync invocation count from here. Deliberately NOT
        // pre-enqueuing a "next" response here: the pre-send and post-send cancellation tests never
        // reach a dequeue at all (they fail before/during send), and a test that DOES need a real
        // refresh response (e.g. the cleanup-budget proof) enqueues it explicitly, at the exact
        // moment it wants — a response pre-queued here would silently be consumed FIRST by such a
        // test instead of the one it explicitly enqueues, corrupting the very proof being built.
        _harness.Handler.ResetInvocationCount();
        return account.Id;
    }

    private async Task<(Guid SessionId, string CallbackUrl)> BeginAuthorizationAsync(AuthTestClient owner, SalesChannel channel)
    {
        var begin = await owner.PostAsync("/api/commerce/marketplace-authorizations", new BeginMarketplaceAuthorizationRequest(MercadoLivreConnector.Code, channel.Id, "Conta ML Cancel " + Guid.NewGuid().ToString("N")[..6]));
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
        var channel = new SalesChannel("MKT-MLC-" + Guid.NewGuid().ToString("N")[..8], "Canal ML Cancel", SalesChannelKind.Marketplace, null, null);
        if (provider is null) db.Add(new MarketplaceProvider(MercadoLivreConnector.Code, "Mercado Livre"));
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
            Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "Owner ML Cancel",
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
