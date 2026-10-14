using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// A-02: proves the REDESIGNED R2 (and R1) buffer-ownership model — a single, workflow-owned array
/// recovered directly from the adapter/store's own returned <c>ReadOnlyMemory&lt;byte&gt;</c> (see
/// <c>MarketplaceAuthorizationWorkflow.RecoverOwnedArrayOrCopy</c>), never a separate `.ToArray()`
/// copy that would leave the adapter-side array as a leftover, un-zeroed plaintext secret.
///
/// The proof mechanism: <see cref="FaultInjectingCredentialStore.ObservedCasSecretFingerprints"/>
/// captures a SHA-256 fingerprint of the EXACT bytes passed to
/// <c>CompareAndSwapReplaceAsync</c>, computed synchronously at call time — i.e. the one instant
/// those bytes are guaranteed to still be genuine plaintext, never a post-hoc read that could
/// already observe a zeroed buffer. Separately, <see cref="FaultInjectingCredentialStore.ObservedCasSecretMaterial"/>
/// retains the SAME <see cref="ReadOnlyMemory{T}"/> (never a copy of it), so
/// <see cref="MemoryMarshal.TryGetArray{T}"/> recovers the literal backing array the workflow
/// itself owns and zeroes in its <c>finally</c> block — this test reads THAT array's state after
/// <c>RefreshAsync</c> returns, proving the SAME buffer used for real persistence is what actually
/// gets zeroed, not an unrelated copy.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MarketplaceRefreshBufferZeroizationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;
    private MercadoLivreRealAdapterHarness _harness = null!;
    private TestClock _clock = null!;

    private readonly List<Guid> _ownedAccountIds = [];
    private readonly List<Guid> _ownedSessionIds = [];
    private readonly List<Guid> _ownedChannelIds = [];

    public MarketplaceRefreshBufferZeroizationTests(PostgresFixture fixture) => _fixture = fixture;

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
    public async Task Successful_refresh_zeroes_the_exact_array_the_real_store_persisted_never_a_copy_of_it()
    {
        var accountId = await ConnectedAccountAsync();
        _clock.UtcNow += TimeSpan.FromHours(7); // past the 6h fake expiry — RefreshAsync will not short-circuit
        // The real connector's own RefreshAsync makes exactly one real HTTP call (the OAuth token
        // endpoint) before ever handing R2 to the workflow — this scripts that one real response.
        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"access_token":"refreshed-access","token_type":"bearer","expires_in":21600,"user_id":555555555,"refresh_token":"refreshed-refresh"}"""));

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.Code);
        result.Code.Should().Be("REFRESHED");

        _harness.FaultStore!.CasAttemptCount.Should().Be(1);
        var observedArray = RecoverArray(_harness.FaultStore.ObservedCasSecretMaterial[0]);
        observedArray.Should().OnlyContain(b => b == 0, "the workflow must zero the SAME array it handed to CompareAndSwapReplaceAsync — this IS that array, recovered by reference, not a copy");

        // Independent proof the fingerprint captured AT CAS-CALL TIME was genuine plaintext: it must
        // match the SHA-256 of what was actually decrypted back out of the real store afterward
        // (the on-disk envelope is a SEPARATE encrypted copy, unaffected by zeroing the in-memory
        // buffer — reading it back is a legitimate, independent oracle for "what was really CAS'd").
        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        var store = scope.ServiceProvider.GetRequiredService<IProtectedCredentialStore>();
        var reread = await store.UseConfirmedAsync(new ProtectedCredentialRead(accountId, MercadoLivreConnector.Code, account.CredentialReference!, account.Connection.ConfirmedCredentialVersion!.Value, account.Connection.ConfirmedOperationId!.Value), CancellationToken.None);
        reread.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
        var actuallyPersistedFingerprint = Convert.ToHexString(SHA256.HashData(reread.SecretMaterial.Span));
        _harness.FaultStore.ObservedCasSecretFingerprints[0].Should().Be(actuallyPersistedFingerprint,
            "the fingerprint captured synchronously at the CAS call must match what was genuinely persisted — proving it was real plaintext, not already-zeroed garbage, at the moment of use");
    }

    [Fact]
    public async Task Cas1_transient_failure_then_Cas2_success_still_zeroes_the_one_reused_R2_buffer()
    {
        var accountId = await ConnectedAccountAsync();
        _clock.UtcNow += TimeSpan.FromHours(7);
        // The real connector's own RefreshAsync makes exactly one real HTTP call (the OAuth token
        // endpoint) before ever handing R2 to the workflow — this scripts that one real response.
        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"access_token":"refreshed-access","token_type":"bearer","expires_in":21600,"user_id":555555555,"refresh_token":"refreshed-refresh"}"""));
        _harness.FaultStore!.EnqueueCasOutcome(ProtectedCredentialStoreOutcome.STORE_UNAVAILABLE); // CAS1 fails
        // CAS2: no override queued -> falls through to the real store and succeeds.

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.Code);

        _harness.FaultStore.CasAttemptCount.Should().Be(2, "exactly one additional persistence attempt of the identical R2 — never a third");
        _harness.FaultStore.ObservedCasSecretFingerprints[0].Should().Be(_harness.FaultStore.ObservedCasSecretFingerprints[1],
            "both attempts must carry byte-identical R2 — this is the SAME reused write, never a freshly re-derived buffer");
        var array1 = RecoverArray(_harness.FaultStore.ObservedCasSecretMaterial[0]);
        var array2 = RecoverArray(_harness.FaultStore.ObservedCasSecretMaterial[1]);
        ReferenceEquals(array1, array2).Should().BeTrue("CAS1 and CAS2 must submit the literal same array instance, not two independent copies of the same bytes");
        array2.Should().OnlyContain(b => b == 0, "zeroization must still happen after the eventually-successful retry");
    }

    [Fact]
    public async Task Cas1_and_Cas2_both_failing_still_zeroes_the_buffer_even_though_nothing_was_ever_persisted()
    {
        var accountId = await ConnectedAccountAsync();
        _clock.UtcNow += TimeSpan.FromHours(7);
        // The real connector's own RefreshAsync makes exactly one real HTTP call (the OAuth token
        // endpoint) before ever handing R2 to the workflow — this scripts that one real response.
        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"access_token":"refreshed-access","token_type":"bearer","expires_in":21600,"user_id":555555555,"refresh_token":"refreshed-refresh"}"""));
        _harness.FaultStore!.EnqueueCasOutcome(ProtectedCredentialStoreOutcome.STORE_UNAVAILABLE); // CAS1 fails
        _harness.FaultStore.EnqueueCasOutcome(ProtectedCredentialStoreOutcome.STORE_UNAVAILABLE); // CAS2 (the one permitted retry) also fails

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
        result.Succeeded.Should().BeFalse();
        result.Code.Should().Be("REAUTHORIZATION_REQUIRED");

        _harness.FaultStore.CasAttemptCount.Should().Be(2, "never a THIRD provider-equivalent persistence attempt after two failures");
        var array = RecoverArray(_harness.FaultStore.ObservedCasSecretMaterial[1]);
        array.Should().OnlyContain(b => b == 0, "the terminal FAIL_CLOSED path must still zero the buffer — a persistence failure is never an excuse to leave plaintext behind");

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED);
        account.Connection.SafeFailureCode.Should().Be("REFRESH_PERSISTENCE_FAILED");
    }

    [Fact]
    public async Task An_exception_thrown_during_CAS_after_the_provider_call_already_succeeded_still_zeroes_the_buffer()
    {
        // "Provider-success-then-workflow-exception": the connector's own RefreshAsync has ALREADY
        // returned a genuine R2 by this point — the failure happens entirely on the workflow's own
        // side, persisting it. finally must still run.
        var accountId = await ConnectedAccountAsync();
        _clock.UtcNow += TimeSpan.FromHours(7);
        // The real connector's own RefreshAsync makes exactly one real HTTP call (the OAuth token
        // endpoint) before ever handing R2 to the workflow — this scripts that one real response.
        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"access_token":"refreshed-access","token_type":"bearer","expires_in":21600,"user_id":555555555,"refresh_token":"refreshed-refresh"}"""));
        _harness.FaultStore!.EnqueueCasFault(new InvalidOperationException("TEST_ONLY_SIMULATED_WORKFLOW_SIDE_FAILURE_DURING_CAS"));

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var act = () => workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>("this exact exception is NOT one of RefreshAsync's own recognized failure shapes — it is not caught, it propagates, and the buffer must STILL be zeroed by the method's `finally`");

        _harness.FaultStore.CasAttemptCount.Should().Be(1);
        var array = RecoverArray(_harness.FaultStore.ObservedCasSecretMaterial[0]);
        array.Should().OnlyContain(b => b == 0, "even a totally uncaught exception during CAS must still zero the buffer via `finally` — memory hygiene must not depend on the exception being one of the method's own recognized shapes");
    }

    [Fact]
    public async Task A_cancellation_observed_right_as_the_response_is_being_persisted_still_zeroes_the_buffer()
    {
        var accountId = await ConnectedAccountAsync();
        _clock.UtcNow += TimeSpan.FromHours(7);
        // The real connector's own RefreshAsync makes exactly one real HTTP call (the OAuth token
        // endpoint) before ever handing R2 to the workflow — this scripts that one real response.
        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"access_token":"refreshed-access","token_type":"bearer","expires_in":21600,"user_id":555555555,"refresh_token":"refreshed-refresh"}"""));
        _harness.FaultStore!.EnqueueCasFault(new OperationCanceledException("TEST_ONLY_SIMULATED_CANCELLATION_DURING_CAS"));

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var act = () => workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>("a cancellation observed exactly as the response is being persisted is not one of RefreshAsync's own recognized failure shapes either — it propagates the same way, and `finally` must still run");

        _harness.FaultStore.CasAttemptCount.Should().Be(1);
        var array = RecoverArray(_harness.FaultStore.ObservedCasSecretMaterial[0]);
        array.Should().OnlyContain(b => b == 0, "cancellation must never be an excuse to leave plaintext R2 un-zeroed");
    }

    private static byte[] RecoverArray(ReadOnlyMemory<byte> memory)
    {
        MemoryMarshal.TryGetArray(memory, out var segment).Should().BeTrue("every buffer this test observes is expected to be a whole, directly-owned array — never an unexpected slice");
        return segment.Array!;
    }

    // ================= helpers (mirrors the sibling real-adapter test files' own copies) =================

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
        _harness.Handler.ResetInvocationCount();
        return account.Id;
    }

    private async Task<(Guid SessionId, string CallbackUrl)> BeginAuthorizationAsync(AuthTestClient owner, SalesChannel channel)
    {
        var begin = await owner.PostAsync("/api/commerce/marketplace-authorizations", new BeginMarketplaceAuthorizationRequest(MercadoLivreConnector.Code, channel.Id, "Conta ML Zero " + Guid.NewGuid().ToString("N")[..6]));
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
        var channel = new SalesChannel("MKT-MLZ-" + Guid.NewGuid().ToString("N")[..8], "Canal ML Zero", SalesChannelKind.Marketplace, null, null);
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
            Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "Owner ML Zero",
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
