using System.Net;
using System.Net.Sockets;
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
/// A-10: the real-adapter refresh matrix. Every test here drives the ACTUAL
/// <see cref="MercadoLivreConnector"/> (never <see cref="FakeMarketplaceConnector"/>) through the
/// real <see cref="MarketplaceAuthorizationWorkflow"/>, against real Testcontainers PostgreSQL and a
/// real <c>LocalProtectedCredentialStore</c>. The only substitution is the two named HTTP clients'
/// transport (<see cref="RealAdapterHttpHandler"/>, in-process, no sockets) — so
/// <see cref="RealAdapterHttpHandler.SendAsyncInvocationCount"/> is genuine proof of how many times
/// <c>HttpClient.SendAsync</c> was actually invoked, never inferred from a fake connector's own
/// bookkeeping. No live network, no real ML credentials, no sleeps (a <see cref="TestClock"/>
/// substitutes for wall-clock expiry).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MercadoLivreRealAdapterRefreshMatrixTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;
    private MercadoLivreRealAdapterHarness _harness = null!;
    private TestClock _clock = null!;

    // H-01: this test tracks the EXACT identities it creates (never a blanket "everything with
    // this provider code") — "MERCADO_LIVRE" is real, permanent seed data, and other rows bearing
    // that same provider code may exist independent of this test/class. Cleanup below deletes
    // ONLY these tracked rows.
    private readonly List<Guid> _ownedAccountIds = [];
    private readonly List<Guid> _ownedSessionIds = [];
    private readonly List<Guid> _ownedChannelIds = [];

    public MercadoLivreRealAdapterRefreshMatrixTests(PostgresFixture fixture) => _fixture = fixture;

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

        // H-01: delete ONLY the rows this instance itself created — tracked by exact ID as they
        // were created (never a blanket "every row with this provider code / this prefix", which
        // would also delete rows belonging to other tests/classes running against the same shared
        // Postgres fixture, and would silently threaten the permanent MERCADO_LIVRE seed data too).
        if (_ownedAccountIds.Count > 0)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE commerce.marketplace_account_connection SET confirmed_operation_id = NULL
                WHERE marketplace_account_id = ANY({_ownedAccountIds.ToArray()})
                """);
            await db.SaveChangesAsync();
        }
        // MarketplaceAccountOperation rows for a CONNECT_NEW carry MarketplaceAccountId == null
        // (the account does not exist yet) and are identified instead by AuthorizationSessionId —
        // so both owned-account and owned-session membership must be checked, or CONNECT_NEW
        // operation rows would leak past cleanup.
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
        // Unlike the S8C.1 fake's own test-only "FAKE" provider code, "MERCADO_LIVRE" is REAL,
        // permanent S8C.0 seed data (its structural capability evidence already exists independent
        // of this test) — this class must never delete that provider/capability row, and no query
        // above references ProviderCode/Code-prefix at all anymore.
    }

    // ================= A-10 traceability: coordinator row name -> proving test =================
    //
    // D01-N1 (connect/reconnect failure preserves AccessExpiresAt)  -> D01N1_reconnect_failure_preserves_the_previously_confirmed_AccessExpiresAt
    // D01-N2 (refresh failure preserves AccessExpiresAt)             -> D01N2_refresh_failure_preserves_the_previously_confirmed_AccessExpiresAt
    // D01-N3 (SENT_OR_UNKNOWN never publishes a candidate expiry)    -> D01N3_SENT_OR_UNKNOWN_refresh_never_publishes_a_candidate_AccessExpiresAt
    // D01-N4 (candidate version not executable before confirmation) -> D01N4N5_a_persisted_candidate_is_not_executable_or_authoritative_before_confirmation
    // D01-N5 (candidate expiry not authoritative before confirmation) -> (same test as D01-N4)
    // PS-A  (real validation failure before SendAsync)               -> RowPSA_a_malformed_stored_identity_is_a_real_pre_send_validation_failure_never_REFRESH_TOKEN_UNAVAILABLE
    //         (distinct from REFRESH_TOKEN_UNAVAILABLE — that remains its own separate, already-
    //         covered terminal condition, exercised implicitly whenever ConnectedAccountAsync's own
    //         setup needs a usable secret; it is never counted as this row.)
    // PS-B  (real request-materialization failure before SendAsync)  -> RowPSB_a_real_request_materialization_failure_is_proven_NOT_SENT_before_SendAsync
    // PS-C  (real caller-token cancellation during the credential read) -> Row03_a_real_caller_token_cancellation_during_the_credential_read_never_invokes_SendAsync
    // PA-01..08 (post-send ambiguous, 8 fault shapes)                -> Row04to10_post_invocation_ambiguous_failure_is_never_replayed_and_forces_reauthorization (Theory)
    // PA-09 (headers-received-then-truncated-body)                  -> Row10b_headers_received_then_body_truncated_is_treated_as_sent_or_unknown
    // CR-01 (valid 2xx + valid R2)                                   -> Row11_valid_2xx_replacement_confirms_V_plus_1_with_new_expiry
    // CR-02 (malformed/incomplete 2xx)                               -> Row12_malformed_2xx_is_never_replayed_and_forces_reauthorization
    // CR-03 (invalid_grant)                                          -> Row13_invalid_grant_maps_to_reauthorize
    // CR-04 (invalid client/application credentials)                -> Row14_invalid_client_maps_to_contact_admin
    // CR-05 (429 WITH Retry-After)                                   -> Row15_rate_limited_with_retry_after_persists_the_real_parsed_window_and_is_honored_by_probe_across_a_fresh_scope
    // CR-06 (429 WITHOUT Retry-After — NEW row)                      -> Row16_rate_limited_without_retry_after_uses_the_frozen_fallback_never_a_manufactured_window
    // CR-07 (5xx)                                                    -> Row17_5xx_maps_to_reauthorize
    // CAS-01 (CAS1 success)                                          -> Row18a_R2_CAS_succeeds_on_the_first_attempt_with_a_genuinely_intact_fingerprint
    // CAS-02 (CAS1-fail -> CAS2-success)                             -> Row18_R2_CAS_fails_once_then_succeeds_with_the_byte_identical_R2_and_no_second_provider_call
    // CAS-03 (CAS1-fail -> CAS2-fail)                                -> Row19_R2_CAS_persistently_fails_and_forces_reauthorization_without_a_second_provider_call

    // ================= D-01 negatives =================

    [Fact]
    public async Task D01N1_reconnect_failure_preserves_the_previously_confirmed_AccessExpiresAt()
    {
        var accountId = await ConnectedAccountAsync();
        DateTimeOffset? expiryBefore;
        long versionBefore;
        await using (var db = _fixture.CreateContext())
        {
            var before = await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            expiryBefore = before.Connection.AccessExpiresAt;
            versionBefore = before.Connection.ConfirmedCredentialVersion!.Value;
        }
        expiryBefore.Should().NotBeNull();

        await ReconnectWithFailureAsync(accountId);

        await using var db2 = _fixture.CreateContext();
        var after = await db2.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        // D01-N1's own point: the reconnect's OWN failure must never touch the PREVIOUSLY-confirmed
        // credential/expiry — regardless of what a failed reconnect legitimately does to
        // AuthorizationState itself (a real reconnect failure correctly forces REAUTHORIZATION_REQUIRED
        // — the whole point of a reconnect attempt is superseding old material, so its own failure
        // cannot simply pretend the old CONNECTED state is still trustworthy; that is a genuine,
        // separate, already-covered invariant, not what this row is proving).
        after.Connection.AccessExpiresAt.Should().Be(expiryBefore, "a failed reconnect must never touch the previously-confirmed AccessExpiresAt");
        after.Connection.ConfirmedCredentialVersion.Should().Be(versionBefore, "a failed reconnect must never advance the confirmed credential version either");
    }

    [Fact]
    public async Task D01N2_refresh_failure_preserves_the_previously_confirmed_AccessExpiresAt()
    {
        var accountId = await ConnectedAccountAsync();
        DateTimeOffset? expiryBefore;
        await using (var db = _fixture.CreateContext())
            expiryBefore = (await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId)).Connection.AccessExpiresAt;
        expiryBefore.Should().NotBeNull();

        _clock.UtcNow += TimeSpan.FromHours(7);
        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"access_token":"a"}""")); // malformed — missing required fields

        using (var scope = _factory.Services.CreateScope())
        {
            var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            (await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None)).Succeeded.Should().BeFalse();
        }

        await using var db2 = _fixture.CreateContext();
        var after = await db2.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        after.Connection.AccessExpiresAt.Should().Be(expiryBefore, "a failed refresh must never touch the previously-confirmed AccessExpiresAt — it never even reaches CAS");
    }

    [Fact]
    public async Task D01N3_SENT_OR_UNKNOWN_refresh_never_publishes_a_candidate_AccessExpiresAt()
    {
        var accountId = await ConnectedAccountAsync();
        DateTimeOffset? expiryBefore;
        await using (var db = _fixture.CreateContext())
            expiryBefore = (await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId)).Connection.AccessExpiresAt;
        expiryBefore.Should().NotBeNull();

        _clock.UtcNow += TimeSpan.FromHours(7);
        _harness.Handler.EnqueueFault(_ => new HttpRequestException(HttpRequestError.ConnectionError, "simulated SENT_OR_UNKNOWN connection reset"));

        using (var scope = _factory.Services.CreateScope())
        {
            var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            (await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None)).Succeeded.Should().BeFalse();
        }
        _harness.Handler.SendAsyncInvocationCount.Should().Be(1);

        await using var db2 = _fixture.CreateContext();
        var after = await db2.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        after.Connection.AccessExpiresAt.Should().Be(expiryBefore, "SENT_OR_UNKNOWN must never publish ANY candidate expiry — the provider's true outcome is genuinely unknown, so nothing new can be authoritative");
        after.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED);
    }

    [Fact]
    public async Task D01N4N5_a_persisted_candidate_is_not_executable_or_authoritative_before_confirmation()
    {
        // Simulates the exact real shape of "CAS (R2 persistence) succeeded, but the process crashed
        // before the SAME transaction's final DB confirm (RecordPersistedCandidate + account.Install-
        // ConfirmedCredential + operation.Confirm) ever committed" — using ONLY real, public aggregate
        // methods and the REAL credential store, never reflection.
        var accountId = await ConnectedAccountAsync();
        await using var setupDb = _fixture.CreateContext();
        var account = await setupDb.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        var oldVersion = account.Connection.ConfirmedCredentialVersion!.Value;
        var oldExpiry = account.Connection.AccessExpiresAt;
        var oldOperationId = account.Connection.ConfirmedOperationId!.Value;
        var reference = account.CredentialReference!;
        var accountVersionBeforeOperation = account.Version;

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IProtectedCredentialStore>();
        // The REAL store now genuinely holds a NEWER candidate (V+1) — exactly what a real CAS
        // success produces — while the account/domain row still says V (old).
        var candidateSecret = System.Text.Encoding.UTF8.GetBytes("""{"v":1,"access_token":"candidate-access","refresh_token":"candidate-refresh","user_id":"555555555"}""");
        var cas = await store.CompareAndSwapReplaceAsync(new ProtectedCredentialWrite(oldOperationId, null, accountId, MercadoLivreConnector.Code, reference, candidateSecret), oldVersion, CancellationToken.None);
        cas.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
        var candidateVersion = cas.Receipt!.CredentialVersion;
        candidateVersion.Should().Be(oldVersion + 1);

        var operation = new MarketplaceAccountOperation(MarketplaceAccountOperationKind.REFRESH, MercadoLivreConnector.Code, accountId, null, accountVersionBeforeOperation, reference, oldVersion, _clock.UtcNow);
        operation.MarkExternalInFlight(_clock.UtcNow);
        operation.RecordPersistedCandidate(reference, candidateVersion, _clock.UtcNow);
        await using (var opDb = _fixture.CreateContext())
        {
            opDb.Add(operation);
            await opDb.SaveChangesAsync();
        }

        // D01-N4: the candidate version is NOT executable before confirmation — the domain's own
        // confirmed pointer is untouched.
        await using (var checkDb = _fixture.CreateContext())
        {
            var stillOld = await checkDb.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            stillOld.Connection.ConfirmedCredentialVersion.Should().Be(oldVersion, "the candidate must never become the domain's confirmed version on its own — only an explicit confirmation transaction may do that");
            // D01-N5: the candidate's implied expiry is not authoritative before confirmation either —
            // AccessExpiresAt is completely untouched by the mere existence of a persisted candidate.
            stillOld.Connection.AccessExpiresAt.Should().Be(oldExpiry, "a persisted-but-unconfirmed candidate must never make its own (unknown, in this shape) expiry authoritative");
        }

        // Reinforcing proof: even the STORE itself now refuses the account's own stale confirmed
        // pointer (the physical file has already moved to V+1) — nothing is safely usable via the
        // normal confirmed-version pointer until reconciliation actually runs.
        var staleRead = await store.UseConfirmedAsync(new ProtectedCredentialRead(accountId, MercadoLivreConnector.Code, reference, oldVersion, oldOperationId), CancellationToken.None);
        staleRead.Outcome.Should().Be(ProtectedCredentialStoreOutcome.VERSION_CONFLICT, "the OLD confirmed version pointer the domain still holds is now itself stale against the physical store");

        // The REAL recovery path (ResolvePendingAsync) is what may legitimately promote this
        // candidate — proving the reconciliation contract without needing a real crash/restart.
        using (var workflowScope = _factory.Services.CreateScope())
        {
            var workflow = workflowScope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            await workflow.ResolvePendingAsync(operation.Id, cancellationToken: CancellationToken.None);
        }
        await using var finalDb = _fixture.CreateContext();
        var resolved = await finalDb.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        resolved.Connection.ConfirmedCredentialVersion.Should().Be(candidateVersion, "only the explicit reconciliation path may ever promote a persisted candidate to confirmed");
    }

    // ================= pre-invocation (proven NOT_SENT — zero real SendAsync calls) =================
    //
    // The connector's own pre-invocation validation (e.g. a secret with no refresh_token) is
    // already directly proven at the unit level in
    // MercadoLivreConnectorContractTests.RefreshAsync_missing_refresh_token_in_secret_is_proven_NOT_SENT
    // (zero HTTP calls, deterministic). This class instead proves the WORKFLOW's own pre-invocation
    // classified path (A-01) end to end against the real store/connector.

    [Fact]
    public async Task RowPSA_a_malformed_stored_identity_is_a_real_pre_send_validation_failure_never_REFRESH_TOKEN_UNAVAILABLE()
    {
        // PS-A (Sol re-gate): a GENUINELY DIFFERENT validation condition from REFRESH_TOKEN_UNAVAILABLE
        // (a distinct terminal condition, per Sol's own objection) — the stored identity itself is
        // not even a well-formed Mercado Livre numeric user id (MercadoLivreConnector.RefreshAsync's
        // OWN new UserIdPattern.IsMatch(userId) guard, added specifically for this row), proven by
        // actually writing that exact malformed-but-otherwise-valid shape through the real store's
        // own public CAS API — never by mocking the connector.
        var accountId = await ConnectedAccountAsync();
        long oldVersion; string reference; Guid operationId; DateTimeOffset? oldExpiry;
        await using (var db = _fixture.CreateContext())
        {
            var account = await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            oldVersion = account.Connection.ConfirmedCredentialVersion!.Value;
            reference = account.CredentialReference!;
            operationId = account.Connection.ConfirmedOperationId!.Value;
            oldExpiry = account.Connection.AccessExpiresAt;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IProtectedCredentialStore>();
            // Refresh token IS present (unlike REFRESH_TOKEN_UNAVAILABLE's own row) — only the
            // identity itself is malformed (leading zero, never a legitimate ML user id).
            var malformedIdentity = System.Text.Encoding.UTF8.GetBytes("""{"v":1,"access_token":"a","refresh_token":"still-present","user_id":"0123abc"}""");
            var cas = await store.CompareAndSwapReplaceAsync(new ProtectedCredentialWrite(operationId, null, accountId, MercadoLivreConnector.Code, reference, malformedIdentity), oldVersion, CancellationToken.None);
            cas.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
            await using var db2 = _fixture.CreateContext();
            await db2.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account_connection SET confirmed_credential_version = {cas.Receipt!.CredentialVersion} WHERE marketplace_account_id = {accountId}");
            // This CAS write IS the test's own setup, not the refresh attempt under test — the
            // "previously-confirmed version" this row must prove stays UNCHANGED is the version AS
            // OF right after setup (2), never the version before setup even ran (1).
            oldVersion = cas.Receipt!.CredentialVersion;
        }

        _clock.UtcNow += TimeSpan.FromHours(7);
        using (var scope = _factory.Services.CreateScope())
        {
            var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
            result.Succeeded.Should().BeFalse();
            result.Code.Should().Be("STORED_IDENTITY_INVALID", "a genuinely different validation condition from REFRESH_TOKEN_UNAVAILABLE — the general NOT_SENT validation row preserves CONNECTED instead of forcing reauthorization");
        }
        _harness.Handler.SendAsyncInvocationCount.Should().Be(0, "the connector's own pre-send validation must reject this before ever reaching HttpClient.SendAsync");

        await using var finalDb = _fixture.CreateContext();
        var finalAccount = await finalDb.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        finalAccount.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED, "the general NOT_SENT validation row preserves CONNECTED — never forces reauthorization for a request that never reached the provider");
        finalAccount.Connection.RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.UNAVAILABLE);
        finalAccount.Connection.SafeFailureCode.Should().Be("STORED_IDENTITY_INVALID");
        finalAccount.Connection.ConfirmedCredentialVersion.Should().Be(oldVersion, "unchanged — nothing was ever sent");
        finalAccount.Connection.AccessExpiresAt.Should().Be(oldExpiry, "a proven pre-send failure must never touch the previously-confirmed expiry");
        finalAccount.Connection.RuntimeRetryAfterUntil.Should().BeNull("not applicable to this row — no rate-limit signal was ever involved");
        var operation = await finalDb.Set<MarketplaceAccountOperation>().AsNoTracking()
            .Where(x => x.MarketplaceAccountId == accountId && x.Kind == MarketplaceAccountOperationKind.REFRESH).OrderByDescending(x => x.CreatedAt).FirstAsync();
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.FAIL_CLOSED, "no operation may be left dangling PENDING/EXTERNAL_IN_FLIGHT");
        operation.SafeResultCode.Should().Be("STORED_IDENTITY_INVALID");
        operation.CleanupState.Should().Be(MarketplaceAccountOperationCleanupState.PENDING);

        var owner = await LoggedInOwnerAsync();
        var status = await owner.GetAsync($"/api/commerce/marketplace-accounts/{accountId}/connection");
        using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("requiredAction").GetString().Should().Be("CHECK_PROVIDER_ACCOUNT", "CONNECTED + UNAVAILABLE maps to CHECK_PROVIDER_ACCOUNT, never REAUTHORIZE");
    }

    [Fact]
    public async Task RowPSB_a_real_request_materialization_failure_is_proven_NOT_SENT_before_SendAsync()
    {
        // PS-B (Sol re-gate): a GENUINE request-materialization failure — MercadoLivreConnector's
        // own TestOnlyRequestMaterializationFault seam makes TokenFormRequest itself throw, which is
        // caught by the REAL production path (MarketplaceHttpSender.SendAsync's own `try { request =
        // requestFactory(); } catch (Exception ex) { return new MarketplaceSendOutcome(false, ...); }`)
        // — never a credential-store exception (that would prove store failure, not request
        // construction failure). The fault is armed for exactly one call and always cleared in
        // `finally`, so it can never leak into another test even if this one fails.
        var accountId = await ConnectedAccountAsync();
        DateTimeOffset? oldExpiry;
        long oldVersion;
        await using (var db = _fixture.CreateContext())
        {
            var account = await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            oldExpiry = account.Connection.AccessExpiresAt;
            oldVersion = account.Connection.ConfirmedCredentialVersion!.Value;
        }

        _clock.UtcNow += TimeSpan.FromHours(7);
        MercadoLivreConnector.TestOnlyRequestMaterializationFault = () => new InvalidOperationException("simulated real request-materialization failure (form-body construction)");
        try
        {
            using var scope = _factory.Services.CreateScope();
            var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
            result.Succeeded.Should().BeFalse();
            result.Code.Should().Be("REFRESH_NOT_SENT");
        }
        finally
        {
            MercadoLivreConnector.TestOnlyRequestMaterializationFault = null;
        }
        _harness.Handler.SendAsyncInvocationCount.Should().Be(0, "the request never even finished materializing — HttpClient.SendAsync was never reached, proven by the real MarketplaceHttpSender.SendAsync try/catch around requestFactory()");

        await using var finalDb = _fixture.CreateContext();
        var finalAccount = await finalDb.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        finalAccount.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED, "a proven pre-send failure — of ANY exception shape — preserves current authorization");
        finalAccount.Connection.RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.UNAVAILABLE);
        finalAccount.Connection.SafeFailureCode.Should().Be("REFRESH_NOT_SENT");
        finalAccount.Connection.ConfirmedCredentialVersion.Should().Be(oldVersion);
        finalAccount.Connection.AccessExpiresAt.Should().Be(oldExpiry);
        finalAccount.Connection.RuntimeRetryAfterUntil.Should().BeNull();
        var operation = await finalDb.Set<MarketplaceAccountOperation>().AsNoTracking()
            .Where(x => x.MarketplaceAccountId == accountId && x.Kind == MarketplaceAccountOperationKind.REFRESH).OrderByDescending(x => x.CreatedAt).FirstAsync();
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.FAIL_CLOSED, "no operation may be left dangling PENDING/EXTERNAL_IN_FLIGHT");
        operation.SafeResultCode.Should().Be("REFRESH_NOT_SENT");
        operation.CleanupState.Should().Be(MarketplaceAccountOperationCleanupState.PENDING);

        var owner = await LoggedInOwnerAsync();
        var status = await owner.GetAsync($"/api/commerce/marketplace-accounts/{accountId}/connection");
        using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("requiredAction").GetString().Should().Be("CHECK_PROVIDER_ACCOUNT");
    }

    [Fact]
    public async Task Row03_a_real_caller_token_cancellation_during_the_credential_read_never_invokes_SendAsync()
    {
        // PS-C (Sol re-gate): a GENUINE caller-token cancellation — RefreshAsync receives an ACTUAL
        // cancellable CancellationToken (never CancellationToken.None); FaultInjectingCredentialStore's
        // barrier signals the moment the credential read has genuinely begun, then observes THAT
        // SAME token via a real `Task.Delay(Timeout.Infinite, cancellationToken)` — never a
        // pre-thrown exception. Only once the test has that proof does it cancel the token, and the
        // credential read exits because of the REAL cancellation, not a simulated fault.
        var accountId = await ConnectedAccountAsync();
        DateTimeOffset? oldExpiry;
        long oldVersion;
        await using (var preDb = _fixture.CreateContext())
        {
            var pre = await preDb.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            oldExpiry = pre.Connection.AccessExpiresAt;
            oldVersion = pre.Connection.ConfirmedCredentialVersion!.Value;
        }
        _clock.UtcNow += TimeSpan.FromHours(7);
        _harness.FaultStore.Should().NotBeNull();
        var entered = _harness.FaultStore!.WaitForNextUseConfirmedEnteredAsync();

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        using var cts = new CancellationTokenSource();
        // The REAL caller token — never CancellationToken.None — propagated all the way down to
        // FaultInjectingCredentialStore.UseConfirmedAsync's own cancellationToken parameter.
        var refreshTask = workflow.RefreshAsync(accountId, cancellationToken: cts.Token);

        // Proof — not inference — that the credential read has genuinely begun before anything is
        // canceled.
        await entered.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        var result = await refreshTask;
        result.Succeeded.Should().BeFalse();
        result.Code.Should().Be("REFRESH_NOT_SENT");
        _harness.Handler.SendAsyncInvocationCount.Should().Be(0, "the credential read was genuinely still in flight when cancellation fired — HttpMessageHandler.SendAsync was never reached");

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED, "a proven pre-send failure preserves current authorization");
        account.Connection.RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.UNAVAILABLE);
        account.Connection.SafeFailureCode.Should().Be("REFRESH_NOT_SENT");
        account.Connection.ConfirmedCredentialVersion.Should().Be(oldVersion, "unchanged — nothing was ever sent, so nothing could have been superseded");
        account.Connection.AccessExpiresAt.Should().Be(oldExpiry, "unchanged for the same reason");
        account.Connection.RuntimeRetryAfterUntil.Should().BeNull("not applicable — no rate-limit signal was ever involved in this cancellation");
        var operation = await db.Set<MarketplaceAccountOperation>().AsNoTracking()
            .Where(x => x.MarketplaceAccountId == accountId && x.Kind == MarketplaceAccountOperationKind.REFRESH).OrderByDescending(x => x.CreatedAt).FirstAsync();
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.FAIL_CLOSED, "no operation may be left dangling PENDING/EXTERNAL_IN_FLIGHT after a proven pre-send cancellation");
        operation.SafeResultCode.Should().Be("REFRESH_NOT_SENT");
        operation.CleanupState.Should().Be(MarketplaceAccountOperationCleanupState.PENDING);

        var owner = await LoggedInOwnerAsync();
        var status = await owner.GetAsync($"/api/commerce/marketplace-accounts/{accountId}/connection");
        using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("requiredAction").GetString().Should().Be("CHECK_PROVIDER_ACCOUNT");
    }

    // ================= post-invocation (proven SENT_OR_UNKNOWN — exactly one real SendAsync call) =================

    public static IEnumerable<object[]> PostInvocationFaults()
    {
        yield return new object[] { "OperationCanceledException", (Func<Exception>)(() => new OperationCanceledException("simulated post-send cancellation")) };
        yield return new object[] { "TimeoutException-shaped", (Func<Exception>)(() => new TaskCanceledException("simulated timeout", new TimeoutException())) };
        yield return new object[] { "HttpRequestException-ConnectionError", (Func<Exception>)(() => new HttpRequestException(HttpRequestError.ConnectionError, "simulated connection error")) };
        yield return new object[] { "DNS-shaped", (Func<Exception>)(() => new HttpRequestException(HttpRequestError.NameResolutionError, "simulated DNS failure", new SocketException((int)SocketError.HostNotFound))) };
        yield return new object[] { "TCP-shaped", (Func<Exception>)(() => new HttpRequestException(HttpRequestError.ConnectionError, "simulated TCP connect failure", new SocketException((int)SocketError.ConnectionRefused))) };
        yield return new object[] { "TLS-shaped", (Func<Exception>)(() => new HttpRequestException(HttpRequestError.SecureConnectionError, "simulated TLS failure", new System.Security.Authentication.AuthenticationException("simulated"))) };
        yield return new object[] { "connection-reset", (Func<Exception>)(() => new HttpRequestException(HttpRequestError.ConnectionError, "simulated reset", new SocketException((int)SocketError.ConnectionReset))) };
        yield return new object[] { "generic-IOException", (Func<Exception>)(() => new IOException("simulated generic IO failure")) };
    }

    [Theory]
    [MemberData(nameof(PostInvocationFaults))]
    public async Task Row04to10_post_invocation_ambiguous_failure_is_never_replayed_and_forces_reauthorization(string label, Func<Exception> makeFault)
    {
        var accountId = await ConnectedAccountAsync();
        long oldVersion; DateTimeOffset? oldExpiry;
        await using (var preDb = _fixture.CreateContext())
        {
            var pre = await preDb.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            oldVersion = pre.Connection.ConfirmedCredentialVersion!.Value;
            oldExpiry = pre.Connection.AccessExpiresAt;
        }
        _clock.UtcNow += TimeSpan.FromHours(7);
        _harness.Handler.EnqueueFault(_ => makeFault());

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
        result.Succeeded.Should().BeFalse(label);
        result.Code.Should().Be("REAUTHORIZATION_REQUIRED", label);
        _harness.Handler.SendAsyncInvocationCount.Should().Be(1, $"{label}: exactly one real SendAsync — never replayed after an ambiguous outcome");
        _harness.FaultStore!.UseConfirmedCallCount.Should().Be(1, $"{label}: R1 must be read exactly once — never replayed alongside the ambiguous send");

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED, label);
        account.Connection.ConfirmedCredentialVersion.Should().Be(oldVersion, $"{label}: SENT_OR_UNKNOWN must never publish a candidate version");
        account.Connection.AccessExpiresAt.Should().Be(oldExpiry, $"{label}: SENT_OR_UNKNOWN must never publish a candidate expiry");
        var operation = await db.Set<MarketplaceAccountOperation>().AsNoTracking()
            .Where(x => x.MarketplaceAccountId == accountId && x.Kind == MarketplaceAccountOperationKind.REFRESH).OrderByDescending(x => x.CreatedAt).FirstAsync();
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.FAIL_CLOSED, label);
        operation.SafeResultCode.Should().Be("REFRESH_OUTCOME_UNKNOWN", label);

        var owner = await LoggedInOwnerAsync();
        var status = await owner.GetAsync($"/api/commerce/marketplace-accounts/{accountId}/connection");
        using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("requiredAction").GetString().Should().Be("REAUTHORIZE", label);
    }

    [Fact]
    public async Task Row10b_headers_received_then_body_truncated_is_treated_as_sent_or_unknown()
    {
        var accountId = await ConnectedAccountAsync();
        long oldVersion; DateTimeOffset? oldExpiry;
        await using (var preDb = _fixture.CreateContext())
        {
            var pre = await preDb.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            oldVersion = pre.Connection.ConfirmedCredentialVersion!.Value;
            oldExpiry = pre.Connection.AccessExpiresAt;
        }
        _clock.UtcNow += TimeSpan.FromHours(7);
        _harness.Handler.EnqueueResponse(_ => RealAdapterHttpHandler.TruncatedBodyResponse(10_000, "{\"access_token\""u8.ToArray()));

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
        result.Succeeded.Should().BeFalse();
        result.Code.Should().Be("REAUTHORIZATION_REQUIRED");
        _harness.Handler.SendAsyncInvocationCount.Should().Be(1);
        _harness.FaultStore!.UseConfirmedCallCount.Should().Be(1, "R1 must never be replayed after a truncated-body outcome either");

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED);
        account.Connection.ConfirmedCredentialVersion.Should().Be(oldVersion);
        account.Connection.AccessExpiresAt.Should().Be(oldExpiry);
        var operation = await db.Set<MarketplaceAccountOperation>().AsNoTracking()
            .Where(x => x.MarketplaceAccountId == accountId && x.Kind == MarketplaceAccountOperationKind.REFRESH).OrderByDescending(x => x.CreatedAt).FirstAsync();
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.FAIL_CLOSED);

        var owner = await LoggedInOwnerAsync();
        var status = await owner.GetAsync($"/api/commerce/marketplace-accounts/{accountId}/connection");
        using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("requiredAction").GetString().Should().Be("REAUTHORIZE");
    }

    // ================= complete responses (SENT_AND_RESPONSE_RECEIVED) =================

    [Fact]
    public async Task Row11_valid_2xx_replacement_confirms_V_plus_1_with_new_expiry()
    {
        var accountId = await ConnectedAccountAsync();
        long previousVersion;
        await using (var db = _fixture.CreateContext())
            previousVersion = (await db.Set<MarketplaceAccount>().Include(x => x.Connection).AsNoTracking().SingleAsync(x => x.Id == accountId)).Connection.ConfirmedCredentialVersion!.Value;

        _clock.UtcNow += TimeSpan.FromHours(7);
        var refreshedAt = _clock.UtcNow;
        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"access_token":"new-access","token_type":"bearer","expires_in":21600,"user_id":555555555,"refresh_token":"new-refresh"}"""));

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.Code);
        _harness.Handler.SendAsyncInvocationCount.Should().Be(1);

        await using var verify = _fixture.CreateContext();
        var account = await verify.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.ConfirmedCredentialVersion.Should().Be(previousVersion + 1);
        account.Connection.AccessExpiresAt.Should().BeCloseTo(refreshedAt.AddSeconds(21600), TimeSpan.FromSeconds(2));
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED);
    }

    [Fact]
    public async Task Row12_malformed_2xx_is_never_replayed_and_forces_reauthorization()
    {
        var accountId = await ConnectedAccountAsync();
        _clock.UtcNow += TimeSpan.FromHours(7);
        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"access_token":"a"}""")); // missing required fields

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
        result.Succeeded.Should().BeFalse();
        _harness.Handler.SendAsyncInvocationCount.Should().Be(1);
        await using var db = _fixture.CreateContext();
        (await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId))
            .Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED);
    }

    [Fact]
    public async Task Row13_invalid_grant_maps_to_reauthorize()
    {
        var accountId = await ConnectedAccountAsync();
        _clock.UtcNow += TimeSpan.FromHours(7);
        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
        result.Succeeded.Should().BeFalse();
        _harness.Handler.SendAsyncInvocationCount.Should().Be(1);
        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED);
        account.Connection.SafeFailureCode.Should().Be("REFRESH_REJECTED");
    }

    [Fact]
    public async Task Row14_invalid_client_maps_to_contact_admin()
    {
        var accountId = await ConnectedAccountAsync();
        _clock.UtcNow += TimeSpan.FromHours(7);
        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.BadRequest, """{"error":"invalid_client"}"""));

        using (var scope = _factory.Services.CreateScope())
        {
            var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            (await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None)).Succeeded.Should().BeFalse();
        }
        _harness.Handler.SendAsyncInvocationCount.Should().Be(1);

        var owner = await LoggedInOwnerAsync();
        var status = await owner.GetAsync($"/api/commerce/marketplace-accounts/{accountId}/connection");
        using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("requiredAction").GetString().Should().Be("CONTACT_ADMIN");
    }

    [Fact]
    public async Task Row15_rate_limited_with_retry_after_persists_the_real_parsed_window_and_is_honored_by_probe_across_a_fresh_scope()
    {
        // A-03: the REAL adapter's own Retry-After parser (never fake-injected failure metadata).
        var accountId = await ConnectedAccountAsync();
        _clock.UtcNow += TimeSpan.FromHours(7);
        var retryAfterSeconds = 120;
        _harness.Handler.EnqueueResponse(_ =>
        {
            var response = JsonResponse((HttpStatusCode)429, """{"error":"local_rate_limited"}""");
            response.Headers.Add("Retry-After", retryAfterSeconds.ToString());
            return response;
        });
        var sentAt = _clock.UtcNow;

        using (var scope = _factory.Services.CreateScope())
        {
            var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            (await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None)).Succeeded.Should().BeFalse();
        }

        long version;
        await using (var db = _fixture.CreateContext())
        {
            var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            account.Connection.RuntimeRetryAfterUntil.Should().BeCloseTo(sentAt.AddSeconds(retryAfterSeconds), TimeSpan.FromSeconds(2),
                "the real HTTP normalization layer must actually parse and persist Retry-After, not lose it");
            version = account.Version;
        }

        // The guard persists across the REAUTHORIZATION_REQUIRED transition the failed refresh
        // itself caused (RequireReauthorization never touches RuntimeRetryAfterUntil) — Probe's own
        // guard check runs BEFORE its authorization-usable check, so this proves the persisted
        // window is honored WITHOUT needing a reconnect (which would itself clear the guard as part
        // of a fresh CONNECTED confirmation — a different, legitimate reset, not this bug).
        using (var scope = _factory.Services.CreateScope())
        {
            var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            var blocked = await workflow.ProbeAsync(accountId, version, CancellationToken.None);
            blocked.Succeeded.Should().BeFalse();
            blocked.Code.Should().Be("PROBE_RATE_LIMITED", "the real parsed window is still active");
        }

        _clock.UtcNow += TimeSpan.FromSeconds(retryAfterSeconds + 5);
        using (var scope = _factory.Services.CreateScope())
        {
            var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            var afterExpiry = await workflow.ProbeAsync(accountId, version, CancellationToken.None);
            afterExpiry.Code.Should().NotBe("PROBE_RATE_LIMITED", "once the real parsed window has passed the guard itself no longer blocks (the account's own REAUTHORIZATION_REQUIRED state is a separate, expected reason probe cannot fully succeed here)");
        }
    }

    [Fact]
    public async Task Row16_rate_limited_without_retry_after_uses_the_frozen_fallback_never_a_manufactured_window()
    {
        // CR-06 (NEW row): the frozen fallback per RetryAfterParser/ADR-0025 §F.4 — an ABSENT
        // Retry-After header must never invent a value. RuntimeRetryAfterUntil must stay exactly
        // whatever it was before this call (null here — never set), not some guessed/default window.
        var accountId = await ConnectedAccountAsync();
        DateTimeOffset? oldExpiry;
        long oldVersion;
        await using (var preDb = _fixture.CreateContext())
        {
            var pre = await preDb.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            oldExpiry = pre.Connection.AccessExpiresAt;
            oldVersion = pre.Connection.ConfirmedCredentialVersion!.Value;
            pre.Connection.RuntimeRetryAfterUntil.Should().BeNull("baseline: no guard has ever been set for this fresh account");
        }
        _clock.UtcNow += TimeSpan.FromHours(7);
        _harness.Handler.EnqueueResponse(_ => JsonResponse((HttpStatusCode)429, """{"error":"local_rate_limited"}""")); // no Retry-After header at all

        using (var scope = _factory.Services.CreateScope())
        {
            var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
            result.Succeeded.Should().BeFalse();
            result.Code.Should().Be("REAUTHORIZATION_REQUIRED");
        }
        _harness.Handler.SendAsyncInvocationCount.Should().Be(1);

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.RuntimeRetryAfterUntil.Should().BeNull("no Retry-After header means NO guard is ever persisted — never a manufactured/default window");
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED);
        account.Connection.SafeFailureCode.Should().Be("REFRESH_RATE_LIMITED");
        account.Connection.ConfirmedCredentialVersion.Should().Be(oldVersion);
        account.Connection.AccessExpiresAt.Should().Be(oldExpiry);

        var operation = await db.Set<MarketplaceAccountOperation>().AsNoTracking()
            .Where(x => x.MarketplaceAccountId == accountId && x.Kind == MarketplaceAccountOperationKind.REFRESH).OrderByDescending(x => x.CreatedAt).FirstAsync();
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.FAIL_CLOSED);
        operation.SafeResultCode.Should().Be("REFRESH_RATE_LIMITED");

        var owner = await LoggedInOwnerAsync();
        var status = await owner.GetAsync($"/api/commerce/marketplace-accounts/{accountId}/connection");
        using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("requiredAction").GetString().Should().Be("REAUTHORIZE");
    }

    [Fact]
    public async Task Row17_5xx_maps_to_reauthorize()
    {
        var accountId = await ConnectedAccountAsync();
        long oldVersion; DateTimeOffset? oldExpiry;
        await using (var preDb = _fixture.CreateContext())
        {
            var pre = await preDb.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            oldVersion = pre.Connection.ConfirmedCredentialVersion!.Value;
            oldExpiry = pre.Connection.AccessExpiresAt;
        }
        _clock.UtcNow += TimeSpan.FromHours(7);
        _harness.Handler.EnqueueResponse(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("") });

        using (var scope = _factory.Services.CreateScope())
        {
            var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
            result.Succeeded.Should().BeFalse();
            result.Code.Should().Be("REAUTHORIZATION_REQUIRED");
        }
        _harness.Handler.SendAsyncInvocationCount.Should().Be(1);
        _harness.FaultStore!.UseConfirmedCallCount.Should().Be(1, "R1 must never be replayed after a 5xx outcome");

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED);
        account.Connection.SafeFailureCode.Should().Be("REFRESH_OUTCOME_UNKNOWN", "PROVIDER_UNAVAILABLE has no dedicated refresh-specific mapping — it conservatively falls to the generic UNKNOWN code, never assumed safe to retry");
        account.Connection.ConfirmedCredentialVersion.Should().Be(oldVersion);
        account.Connection.AccessExpiresAt.Should().Be(oldExpiry);
        account.Connection.RuntimeRetryAfterUntil.Should().BeNull("a plain 5xx (RETRYABLE, not RATE_LIMITED) never persists a Retry-After guard");

        var operation = await db.Set<MarketplaceAccountOperation>().AsNoTracking()
            .Where(x => x.MarketplaceAccountId == accountId && x.Kind == MarketplaceAccountOperationKind.REFRESH).OrderByDescending(x => x.CreatedAt).FirstAsync();
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.FAIL_CLOSED);

        var owner = await LoggedInOwnerAsync();
        var status = await owner.GetAsync($"/api/commerce/marketplace-accounts/{accountId}/connection");
        using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("requiredAction").GetString().Should().Be("REAUTHORIZE");
    }

    // ================= A-02 / A-10 rows 18a/18/19: CAS retry with a REAL fault-injected store =================
    //
    // Every fingerprint assertion below uses ObservedCasSecretFingerprints — a SHA-256 computed
    // SYNCHRONOUSLY at the moment of each CAS call, before any outcome/fault/zeroing — never a
    // post-return comparison of ObservedCasSecretMaterial's own arrays (which, after RefreshAsync
    // has already returned, the workflow's own `finally` has already zeroed — comparing two
    // ALL-ZERO arrays for equality trivially "passes" without proving anything about what was
    // really sent).

    [Fact]
    public async Task Row18a_R2_CAS_succeeds_on_the_first_attempt_with_a_genuinely_intact_fingerprint()
    {
        var accountId = await ConnectedAccountAsync();
        _clock.UtcNow += TimeSpan.FromHours(7);
        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"access_token":"new-access","token_type":"bearer","expires_in":21600,"user_id":555555555,"refresh_token":"new-refresh"}"""));

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.Code);

        _harness.FaultStore!.CasAttemptCount.Should().Be(1);
        var observedArray = RecoverArray(_harness.FaultStore.ObservedCasSecretMaterial[0]);
        observedArray.Should().OnlyContain(b => b == 0, "the workflow must zero the SAME array it handed to CompareAndSwapReplaceAsync after use");

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        var store = scope.ServiceProvider.GetRequiredService<IProtectedCredentialStore>();
        var reread = await store.UseConfirmedAsync(new ProtectedCredentialRead(accountId, MercadoLivreConnector.Code, account.CredentialReference!, account.Connection.ConfirmedCredentialVersion!.Value, account.Connection.ConfirmedOperationId!.Value), CancellationToken.None);
        reread.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
        var actuallyPersistedFingerprint = Convert.ToHexString(SHA256.HashData(reread.SecretMaterial.Span));
        _harness.FaultStore.ObservedCasSecretFingerprints[0].Should().Be(actuallyPersistedFingerprint,
            "the fingerprint captured AT CAS-call time (before any zeroing) must match what was genuinely persisted");
    }

    [Fact]
    public async Task Row18_R2_CAS_fails_once_then_succeeds_with_the_byte_identical_R2_and_no_second_provider_call()
    {
        var accountId = await ConnectedAccountAsync();
        _clock.UtcNow += TimeSpan.FromHours(7);
        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"access_token":"new-access","token_type":"bearer","expires_in":21600,"user_id":555555555,"refresh_token":"new-refresh"}"""));
        _harness.FaultStore.Should().NotBeNull();
        _harness.FaultStore!.EnqueueCasOutcome(ProtectedCredentialStoreOutcome.WRITE_FAILED); // first CAS attempt fails
        _harness.FaultStore!.EnqueueCasOutcome(null); // second attempt: let the real store actually persist it

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.Code);

        _harness.Handler.SendAsyncInvocationCount.Should().Be(1, "never a second provider POST regardless of local persistence retries");
        _harness.FaultStore.CasAttemptCount.Should().Be(2);
        _harness.FaultStore.ObservedCasSecretFingerprints.Should().HaveCount(2);
        _harness.FaultStore.ObservedCasSecretFingerprints[0].Should().Be(_harness.FaultStore.ObservedCasSecretFingerprints[1],
            "both CAS attempts must carry the byte-identical R2 — captured BEFORE any zeroing, never inferred from two already-zeroed arrays matching trivially");
        var array1 = RecoverArray(_harness.FaultStore.ObservedCasSecretMaterial[0]);
        var array2 = RecoverArray(_harness.FaultStore.ObservedCasSecretMaterial[1]);
        ReferenceEquals(array1, array2).Should().BeTrue("CAS1 and CAS2 must submit the literal same array instance — never a freshly re-derived R2");
        array2.Should().OnlyContain(b => b == 0, "zeroization must still happen after the eventually-successful retry");

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED);
        var operation = await db.Set<MarketplaceAccountOperation>().AsNoTracking()
            .Where(x => x.MarketplaceAccountId == accountId && x.Kind == MarketplaceAccountOperationKind.REFRESH).OrderByDescending(x => x.CreatedAt).FirstAsync();
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.CONFIRMED);
        operation.SafeResultCode.Should().Be("REFRESHED");
    }

    [Fact]
    public async Task Row19_R2_CAS_persistently_fails_and_forces_reauthorization_without_a_second_provider_call()
    {
        var accountId = await ConnectedAccountAsync();
        long oldVersion; DateTimeOffset? oldExpiry;
        await using (var preDb = _fixture.CreateContext())
        {
            var pre = await preDb.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            oldVersion = pre.Connection.ConfirmedCredentialVersion!.Value;
            oldExpiry = pre.Connection.AccessExpiresAt;
        }
        _clock.UtcNow += TimeSpan.FromHours(7);
        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"access_token":"new-access","token_type":"bearer","expires_in":21600,"user_id":555555555,"refresh_token":"new-refresh"}"""));
        _harness.FaultStore!.EnqueueCasOutcome(ProtectedCredentialStoreOutcome.WRITE_FAILED);
        _harness.FaultStore!.EnqueueCasOutcome(ProtectedCredentialStoreOutcome.WRITE_FAILED);

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var result = await workflow.RefreshAsync(accountId, cancellationToken: CancellationToken.None);
        result.Succeeded.Should().BeFalse();
        result.Code.Should().Be("REAUTHORIZATION_REQUIRED");

        _harness.Handler.SendAsyncInvocationCount.Should().Be(1, "never a second provider POST after a local persistence failure");
        _harness.FaultStore.CasAttemptCount.Should().Be(2, "exactly one additional retry, never more");
        _harness.FaultStore.ObservedCasSecretFingerprints[0].Should().Be(_harness.FaultStore.ObservedCasSecretFingerprints[1],
            "both attempts still carry the byte-identical R2 even though both fail");
        var array = RecoverArray(_harness.FaultStore.ObservedCasSecretMaterial[1]);
        array.Should().OnlyContain(b => b == 0, "the terminal FAIL_CLOSED path must still zero the buffer — a persistence failure is never an excuse to leave plaintext behind");

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED);
        account.Connection.SafeFailureCode.Should().Be("REFRESH_PERSISTENCE_FAILED");
        account.Connection.ConfirmedCredentialVersion.Should().Be(oldVersion, "a failed persistence attempt must never advance the confirmed version");
        account.Connection.AccessExpiresAt.Should().Be(oldExpiry, "nor the confirmed expiry");
        var operation = await db.Set<MarketplaceAccountOperation>().AsNoTracking()
            .Where(x => x.MarketplaceAccountId == accountId && x.Kind == MarketplaceAccountOperationKind.REFRESH).OrderByDescending(x => x.CreatedAt).FirstAsync();
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.FAIL_CLOSED);
        operation.SafeResultCode.Should().Be("REFRESH_PERSISTENCE_FAILED");

        var owner = await LoggedInOwnerAsync();
        var status = await owner.GetAsync($"/api/commerce/marketplace-accounts/{accountId}/connection");
        using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("requiredAction").GetString().Should().Be("REAUTHORIZE");
    }

    private static byte[] RecoverArray(ReadOnlyMemory<byte> memory)
    {
        MemoryMarshal.TryGetArray(memory, out var segment).Should().BeTrue("every buffer this test observes is expected to be a whole, directly-owned array — never an unexpected slice");
        return segment.Array!;
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
        // H-01: this instance now owns this account row — track it so DisposeAsync deletes ONLY it.
        _ownedAccountIds.Add(account.Id);
        // Every test measures ITS OWN SendAsync invocation count from here — never inflated by
        // this setup's own 3 real calls (token exchange, /users/me, grant probe).
        _harness.Handler.ResetInvocationCount();
        return account.Id;
    }

    private async Task ReconnectAsync(Guid accountId)
    {
        long version;
        await using (var db = _fixture.CreateContext())
            version = (await db.Set<MarketplaceAccount>().AsNoTracking().SingleAsync(x => x.Id == accountId)).Version;
        var owner = await LoggedInOwnerAsync();
        var begin = await owner.PostAsync($"/api/commerce/marketplace-accounts/{accountId}/reauthorize", new ReauthorizeMarketplaceAccountRequest(version));
        begin.StatusCode.Should().Be(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
        var sessionId = document.RootElement.GetProperty("sessionId").GetGuid();
        // H-01: reauthorize creates a NEW session server-side (distinct from the original connect
        // session) — track it too, or it would leak past a scoped-by-owned-ids cleanup.
        _ownedSessionIds.Add(sessionId);
        var authorizationUri = document.RootElement.GetProperty("authorizationUri").GetString()!;
        var state = HttpUtility.ParseQueryString(new Uri(authorizationUri).Query)["state"]!;
        var callbackUrl = $"/api/commerce/marketplace-authorizations/{MercadoLivreConnector.Code}/callback?state={Uri.EscapeDataString(state)}&code=fake-reconnect-code";

        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"access_token":"reconnect-access","token_type":"bearer","expires_in":21600,"user_id":555555555,"refresh_token":"reconnect-refresh"}"""));
        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"id":555555555,"site_id":"MLB"}"""));
        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.OK, """{"results":["MLB1"]}"""));
        (await owner.GetAsync(callbackUrl)).StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    /// <summary>D01-N1 support: drives the SAME reconnect flow as <see cref="ReconnectAsync"/> but
    /// scripts a FAILING token-exchange response (invalid_grant) instead of a successful one — the
    /// reconnect's own CompleteOnceAsync must fail closed before ever installing a new credential.</summary>
    private async Task ReconnectWithFailureAsync(Guid accountId)
    {
        long version;
        await using (var db = _fixture.CreateContext())
            version = (await db.Set<MarketplaceAccount>().AsNoTracking().SingleAsync(x => x.Id == accountId)).Version;
        var owner = await LoggedInOwnerAsync();
        var begin = await owner.PostAsync($"/api/commerce/marketplace-accounts/{accountId}/reauthorize", new ReauthorizeMarketplaceAccountRequest(version));
        begin.StatusCode.Should().Be(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
        var sessionId = document.RootElement.GetProperty("sessionId").GetGuid();
        _ownedSessionIds.Add(sessionId);
        var authorizationUri = document.RootElement.GetProperty("authorizationUri").GetString()!;
        var state = HttpUtility.ParseQueryString(new Uri(authorizationUri).Query)["state"]!;
        var callbackUrl = $"/api/commerce/marketplace-authorizations/{MercadoLivreConnector.Code}/callback?state={Uri.EscapeDataString(state)}&code=fake-reconnect-code-fails";

        _harness.Handler.EnqueueResponse(_ => JsonResponse(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
        (await owner.GetAsync(callbackUrl)).StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    private async Task<(Guid SessionId, string CallbackUrl)> BeginAuthorizationAsync(AuthTestClient owner, SalesChannel channel)
    {
        var begin = await owner.PostAsync("/api/commerce/marketplace-authorizations", new BeginMarketplaceAuthorizationRequest(MercadoLivreConnector.Code, channel.Id, "Conta ML Real " + Guid.NewGuid().ToString("N")[..6]));
        begin.StatusCode.Should().Be(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
        var sessionId = document.RootElement.GetProperty("sessionId").GetGuid();
        // H-01: track the session this call created so cleanup can delete it by exact ID.
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
        var channel = new SalesChannel("MKT-ML-" + Guid.NewGuid().ToString("N")[..8], "Canal ML Real", SalesChannelKind.Marketplace, null, null);
        if (provider is null) db.Add(new MarketplaceProvider(MercadoLivreConnector.Code, "Mercado Livre"));
        db.Add(channel);
        await db.SaveChangesAsync();
        // H-01: track the channel this call created — the MERCADO_LIVRE provider row above is
        // real, permanent S8C.0 seed data (created at most once, never by this test when it
        // already exists) and is deliberately NEVER tracked/deleted.
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
            Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "Owner ML Real",
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
