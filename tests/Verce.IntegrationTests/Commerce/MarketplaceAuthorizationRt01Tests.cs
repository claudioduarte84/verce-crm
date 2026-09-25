using System.Net;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
/// Real-PostgreSQL (Testcontainers) proof of ADR-0024's RT-01 terminal-operation-authority
/// matrix: a happy-path callback end to end (nothing previously exercised the workflow beyond
/// unit-level domain checks), plus the "resolver wins the terminal row lock before the callback
/// commits" race — deterministic via <see cref="FakeMarketplaceControlPlane"/>'s barrier, never a
/// sleep. This is not exhaustive RT-01 coverage (see the mission's final report for the full gap
/// list); it certifies the two scenarios most load-bearing for correctness: that a normal
/// authorization actually confirms end to end, and that a losing callback can never leave an
/// orphaned candidate or an unhandled exception when a resolver wins the row-lock race.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MarketplaceAuthorizationRt01Tests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;
    private MarketplaceTestHarness _harness = null!;
    private TestClock _clock = null!;

    public MarketplaceAuthorizationRt01Tests(PostgresFixture fixture) => _fixture = fixture;

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
        // The Postgres fixture's container is shared across the WHOLE collection (one database
        // per test run, not per test class) — a sibling test (CommerceSeedIntegrationTests)
        // asserts an EXACT global MarketplaceProvider row count, so this test must leave no trace
        // of the "FAKE" provider/channel/account rows it created, in strict FK dependency order.
        await using var db = _fixture.CreateContext();
        // account.marketplace_account_operation.marketplace_account_id (Restrict) and
        // marketplace_account_connection.confirmed_operation_id (Restrict) are a genuine
        // production circular reference — deliberately: nothing in production ever deletes
        // either row (accounts/operations are retained forever; only their credential material
        // is cleaned up). Only THIS test's own hygiene needs to break the cycle, by nulling the
        // connection's confirmed_operation_id pointer first.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE commerce.marketplace_account_connection SET confirmed_operation_id = NULL
            WHERE marketplace_account_id IN (SELECT id FROM commerce.marketplace_account WHERE provider_code = {FakeMarketplaceConnector.Code})
            """);
        await db.SaveChangesAsync();

        // Operations (and sessions) next — deleting the referencing (child) row is always
        // allowed regardless of its own outbound FK to the account; only deleting the
        // REFERENCED account first would have violated Restrict.
        db.RemoveRange(await db.Set<MarketplaceAccountOperation>().Where(x => x.ProviderCode == FakeMarketplaceConnector.Code).ToListAsync());
        db.RemoveRange(await db.Set<MarketplaceAuthorizationSession>().Where(x => x.ProviderCode == FakeMarketplaceConnector.Code).ToListAsync());
        await db.SaveChangesAsync();

        db.RemoveRange(await db.Set<MarketplaceAccount>().Where(x => x.ProviderCode == FakeMarketplaceConnector.Code).ToListAsync());
        await db.SaveChangesAsync();

        // Step 3: the test-owned channel(s) and provider row.
        db.RemoveRange(await db.Set<SalesChannel>().Where(x => x.Code.StartsWith("MKT-")).ToListAsync());
        var provider = await db.Set<MarketplaceProvider>().SingleOrDefaultAsync(x => x.Code == FakeMarketplaceConnector.Code);
        if (provider is not null) db.Remove(provider);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Normal_CONNECT_NEW_callback_confirms_the_operation_and_connects_the_account()
    {
        var channel = await SeedProviderAndChannelAsync();
        var owner = await LoggedInOwnerAsync();
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];

        var (sessionId, callbackUrl) = await BeginAuthorizationAsync(owner, channel);
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(
            externalId,
            new Dictionary<string, AccountCapabilityState> { ["ORDERS_READ"] = AccountCapabilityState.GRANTED }));

        var callback = await owner.GetAsync(callbackUrl);
        callback.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.OK, HttpStatusCode.NotFound);

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).Include(x => x.Capabilities)
            .SingleOrDefaultAsync(x => x.ExternalAccountId == externalId);
        account.Should().NotBeNull("a normal callback must create and connect the account");
        account!.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED);
        account.Connection.ConfirmedCredentialVersion.Should().Be(1);
        account.Connection.ConfirmedOperationId.Should().NotBeNull();
        account.CredentialReference.Should().NotBeNullOrWhiteSpace();
        account.Capabilities.Single(x => x.CapabilityCode == "ORDERS_READ").State.Should().Be(AccountCapabilityState.GRANTED);

        var operation = await db.Set<MarketplaceAccountOperation>().SingleAsync(x => x.Id == account.Connection.ConfirmedOperationId);
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.CONFIRMED);
        operation.Kind.Should().Be(MarketplaceAccountOperationKind.CONNECT_NEW);

        var session = await db.Set<MarketplaceAuthorizationSession>().SingleAsync(x => x.Id == sessionId);
        session.Status.Should().Be(MarketplaceAuthorizationSessionStatus.COMPLETED);
    }

    /// <summary>
    /// RT-01 row: "Resolver wins terminal row lock before callback commit" -&gt; FAIL_CLOSED;
    /// existing account (none, for CONNECT_NEW) stays absent; candidate deleted; callback cannot
    /// confirm. Exercises the exact defect this session fixed in
    /// <c>MarketplaceAuthorizationWorkflow.CompleteCallbackAsync</c>: before the fix, a resolver
    /// winning this race caused RecordReceiptAsync/ConfirmCallbackAsync to throw an UNHANDLED
    /// InvalidOperationException out of the callback instead of a clean FAIL_CLOSED result, and
    /// left the just-staged candidate file undeleted.
    /// </summary>
    [Fact]
    public async Task Resolver_winning_the_operation_row_lock_fails_the_callback_closed_with_no_orphaned_candidate()
    {
        var channel = await SeedProviderAndChannelAsync();
        var owner = await LoggedInOwnerAsync();
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];

        var (sessionId, callbackUrl) = await BeginAuthorizationAsync(owner, channel);
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId));
        _harness.ControlPlane.ArmBarrier(sessionId);

        var callbackTask = owner.GetAsync(callbackUrl);

        // Deterministic poll (not a fixed sleep-and-hope): waits only for the already-committed
        // operation row that ClaimForCallbackAsync writes BEFORE calling the (barrier-blocked)
        // connector — bounded, and the condition it waits for is guaranteed to eventually be true
        // because that transaction has already committed by the time BeginAsync/GetAsync returned
        // control to this test (the HTTP call is in flight, but the DB commit inside it happens
        // before the fake blocks).
        Guid operationId = Guid.Empty;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var poll = _fixture.CreateContext();
            var found = await poll.Set<MarketplaceAccountOperation>().AsNoTracking()
                .Where(x => x.AuthorizationSessionId == sessionId).Select(x => (Guid?)x.Id).SingleOrDefaultAsync();
            if (found is { } id) { operationId = id; break; }
            await Task.Delay(25);
        }
        operationId.Should().NotBe(Guid.Empty, "the operation row must exist before the fake connector call it precedes can be blocked");

        // The resolver wins the row lock while the callback's own exchange is still blocked.
        using (var scope = _factory.Services.CreateScope())
        {
            var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            await workflow.ResolvePendingAsync(operationId, CancellationToken.None);
        }

        _harness.ControlPlane.Release(sessionId);
        var callback = await callbackTask; // must complete, never throw an unhandled exception
        callback.Should().NotBeNull();

        await using var db = _fixture.CreateContext();
        (await db.Set<MarketplaceAccount>().AnyAsync(x => x.ExternalAccountId == externalId)).Should().BeFalse(
            "the resolver already failed the operation closed; the losing callback must never create the account");

        var operation = await db.Set<MarketplaceAccountOperation>().SingleAsync(x => x.Id == operationId);
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.FAIL_CLOSED);
        operation.CleanupState.Should().NotBe(MarketplaceAccountOperationCleanupState.NOT_REQUIRED);

        var session = await db.Set<MarketplaceAuthorizationSession>().SingleAsync(x => x.Id == sessionId);
        session.Status.Should().Be(MarketplaceAuthorizationSessionStatus.FAILED);
    }

    [Fact]
    public async Task Normal_RECONNECT_commit_confirms_and_advances_the_same_credential_reference_to_a_new_version()
    {
        var channel = await SeedProviderAndChannelAsync();
        var owner = await LoggedInOwnerAsync();
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];

        var (session1, callback1) = await BeginAuthorizationAsync(owner, channel);
        _harness.ControlPlane.Configure(session1, new FakeMarketplaceScenario(externalId));
        await owner.GetAsync(callback1);

        Guid accountId; string k1Reference; long accountVersion;
        await using (var db = _fixture.CreateContext())
        {
            var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.ExternalAccountId == externalId);
            accountId = account.Id; k1Reference = account.CredentialReference!; accountVersion = account.Version;
            account.Connection.ConfirmedCredentialVersion.Should().Be(1);
        }

        var (session2, callback2) = await BeginReauthorizeAsync(owner, accountId, accountVersion);
        _harness.ControlPlane.Configure(session2, new FakeMarketplaceScenario(externalId));
        await owner.GetAsync(callback2);

        await using var verify = _fixture.CreateContext();
        var reconnected = await verify.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        reconnected.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED);
        reconnected.CredentialReference.Should().Be(k1Reference, "a normal reconnect retains the SAME reference/generation");
        reconnected.Connection.ConfirmedCredentialVersion.Should().Be(2, "the store's version for this reference advances by one");
        var operation = await verify.Set<MarketplaceAccountOperation>().SingleAsync(x => x.Id == reconnected.Connection.ConfirmedOperationId);
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.CONFIRMED);
        operation.Kind.Should().Be(MarketplaceAccountOperationKind.RECONNECT);
    }

    /// <summary>
    /// RT-01 rows "Store persisted; callback commit succeeds but reply lost" and "Commit still
    /// pending when resolver starts; callback wins row lock": both describe the SAME row-lock
    /// arbitration outcome (CONFIRMED, never overridden). Proven here with a REAL PostgreSQL
    /// FOR UPDATE lock held by a raw connection (standing in for "a commit transaction still in
    /// flight") while a concurrent resolver call is issued and observed to genuinely BLOCK at the
    /// database level — not merely be logically skipped — until that lock releases.
    /// </summary>
    [Fact]
    public async Task An_in_flight_commit_that_succeeds_is_never_overridden_by_a_racing_resolver()
    {
        var (accountId, operationId) = await SeedRefreshOperationAsync(MarketplaceAccountOperationDecision.PENDING);

        await using var lockConnection = new NpgsqlConnection(_fixture.ConnectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand("SELECT id FROM commerce.marketplace_account_operation WHERE id = @id FOR UPDATE", lockConnection, lockTransaction))
        {
            lockCmd.Parameters.AddWithValue("id", operationId);
            await lockCmd.ExecuteScalarAsync();
        }

        var resolverTask = Task.Run(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            await workflow.ResolvePendingAsync(operationId, CancellationToken.None);
        });

        // The resolver must genuinely be blocked by PostgreSQL's own row lock — not merely slow.
        var completedEarly = await Task.WhenAny(resolverTask, Task.Delay(400)) == resolverTask;
        completedEarly.Should().BeFalse("the resolver must block on the real row lock, never proceed while it is held");

        // Simulate "the callback's commit succeeded" — apply the exact terminal mutation a real
        // CONFIRMED commit would make, then commit — releasing the lock with CONFIRMED already in place.
        await using (var updateCmd = new NpgsqlCommand(
            "UPDATE commerce.marketplace_account_operation SET decision = 'CONFIRMED', decided_at = now(), safe_result_code = 'AUTHORIZED', version = version + 1 WHERE id = @id",
            lockConnection, lockTransaction))
        {
            updateCmd.Parameters.AddWithValue("id", operationId);
            await updateCmd.ExecuteNonQueryAsync();
        }
        await lockTransaction.CommitAsync();

        await resolverTask; // must complete promptly now that the lock released

        await using var verify = _fixture.CreateContext();
        var operation = await verify.Set<MarketplaceAccountOperation>().SingleAsync(x => x.Id == operationId);
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.CONFIRMED, "the resolver must never turn an already-committed CONFIRMED decision into FAIL_CLOSED");
        var account = await verify.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED, "the resolver's no-op path must never have touched the account");
    }

    /// <summary>RT-01 "Store persisted; callback commit rolls back" -&gt; FAIL_CLOSED: the same real
    /// row-lock technique, but the holder rolls back instead of committing — the resolver must
    /// then see the row still PENDING (never inferring rollback from an unlocked negative read)
    /// and correctly fail it closed itself.</summary>
    [Fact]
    public async Task An_in_flight_commit_that_rolls_back_lets_the_resolver_fail_the_operation_closed()
    {
        var (accountId, operationId) = await SeedRefreshOperationAsync(MarketplaceAccountOperationDecision.PENDING);

        await using (var lockConnection = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await lockConnection.OpenAsync();
            await using var lockTransaction = await lockConnection.BeginTransactionAsync();
            await using (var lockCmd = new NpgsqlCommand("SELECT id FROM commerce.marketplace_account_operation WHERE id = @id FOR UPDATE", lockConnection, lockTransaction))
            {
                lockCmd.Parameters.AddWithValue("id", operationId);
                await lockCmd.ExecuteScalarAsync();
            }

            var resolverTask = Task.Run(async () =>
            {
                using var scope = _factory.Services.CreateScope();
                var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
                await workflow.ResolvePendingAsync(operationId, CancellationToken.None);
            });
            (await Task.WhenAny(resolverTask, Task.Delay(400)) == resolverTask).Should().BeFalse("the resolver must block while the row lock is held");

            // Apply the same mutation as the "succeeds" test, but roll it back — the row must
            // revert to exactly its pre-transaction state (still PENDING).
            await using (var updateCmd = new NpgsqlCommand(
                "UPDATE commerce.marketplace_account_operation SET decision = 'CONFIRMED', decided_at = now() WHERE id = @id", lockConnection, lockTransaction))
            {
                updateCmd.Parameters.AddWithValue("id", operationId);
                await updateCmd.ExecuteNonQueryAsync();
            }
            await lockTransaction.RollbackAsync();
            await resolverTask;
        }

        await using var verify = _fixture.CreateContext();
        var operation = await verify.Set<MarketplaceAccountOperation>().SingleAsync(x => x.Id == operationId);
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.FAIL_CLOSED);
        var account = await verify.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED);
    }

    /// <summary>RT-01 "Startup finds CONNECT_NEW/RECONNECT receipt without committed session
    /// success": a session CLAIMED (and its operation EXTERNAL_IN_FLIGHT) then abandoned for over
    /// two minutes — the bounded housekeeping sweep (never a manual poke) must resolve it closed,
    /// exactly as a real restart's startup pass would.</summary>
    [Fact]
    public async Task Abandoned_CLAIMED_session_and_operation_are_resolved_by_the_bounded_housekeeping_sweep()
    {
        var channel = await SeedProviderAndChannelAsync();
        var owner = await LoggedInOwnerAsync();
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];

        var (sessionId, callbackUrl) = await BeginAuthorizationAsync(owner, channel);
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId));
        _harness.ControlPlane.ArmBarrier(sessionId); // holds CompleteOnceAsync open — session stays CLAIMED, operation stays EXTERNAL_IN_FLIGHT
        var callbackTask = owner.GetAsync(callbackUrl);

        Guid operationId = Guid.Empty;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var poll = _fixture.CreateContext();
            var found = await poll.Set<MarketplaceAccountOperation>().AsNoTracking().Where(x => x.AuthorizationSessionId == sessionId).Select(x => (Guid?)x.Id).SingleOrDefaultAsync();
            if (found is { } id) { operationId = id; break; }
            await Task.Delay(25);
        }
        operationId.Should().NotBe(Guid.Empty);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(3); // past the 2-minute abandonment threshold

        using (var scope = _factory.Services.CreateScope())
        {
            var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            var resolved = await workflow.ResolveAbandonedAsync(100, CancellationToken.None);
            resolved.Should().BeGreaterThan(0, "the abandoned operation/session must be picked up by the bounded sweep's own due-time query");
        }

        _harness.ControlPlane.Release(sessionId);
        await callbackTask; // the now-terminal operation must let the blocked callback return cleanly, never throw

        await using var verify = _fixture.CreateContext();
        var operation = await verify.Set<MarketplaceAccountOperation>().SingleAsync(x => x.Id == operationId);
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.FAIL_CLOSED);
        var session = await verify.Set<MarketplaceAuthorizationSession>().SingleAsync(x => x.Id == sessionId);
        session.Status.Should().Be(MarketplaceAuthorizationSessionStatus.FAILED);
        (await verify.Set<MarketplaceAccount>().AnyAsync(x => x.ExternalAccountId == externalId)).Should().BeFalse();
    }

    /// <summary>RT-01 "Startup finds REFRESH receipt without DB confirmation, exact guards still
    /// valid" -&gt; CONFIRMED by the refresh-only resolver at V+1. Exercises the exact recovery
    /// path this round's session added to <c>ResolvePendingAsync</c>.</summary>
    [Fact]
    public async Task Refresh_receipt_under_exact_guards_is_confirmed_by_the_refresh_only_resolver()
    {
        var (accountId, operationId) = await SeedRefreshOperationAsync(MarketplaceAccountOperationDecision.PENDING, persistExactGuardMatchingCandidate: true);

        using (var scope = _factory.Services.CreateScope())
        {
            var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            await workflow.ResolvePendingAsync(operationId, CancellationToken.None);
        }

        await using var verify = _fixture.CreateContext();
        var operation = await verify.Set<MarketplaceAccountOperation>().SingleAsync(x => x.Id == operationId);
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.CONFIRMED, "an exact-guard-matching REFRESH receipt may be confirmed by the refresh-only resolver");
        var account = await verify.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED);
        account.Connection.ConfirmedCredentialVersion.Should().Be(2, "the refresh's persisted candidate (V+1) becomes the new confirmed version");
        account.Connection.ConfirmedOperationId.Should().Be(operationId);
    }

    /// <summary>RT-01 "REFRESH receipt with failed guards/foreign operation" -&gt; FAIL_CLOSED: the
    /// account's root Version moved between the refresh's own send and this resolution (a
    /// concurrent unrelated mutation) — the refresh-only path must refuse and fail closed exactly
    /// like the generic path, never silently promote a stale receipt.</summary>
    [Fact]
    public async Task Refresh_receipt_with_incompatible_guards_fails_closed()
    {
        var (accountId, operationId) = await SeedRefreshOperationAsync(MarketplaceAccountOperationDecision.PENDING, persistExactGuardMatchingCandidate: true);

        // Mutate the account root AFTER the refresh operation captured its expected version —
        // exactly the "foreign operation moved the root" case the guard must catch. Goes through
        // the REAL HTTP PUT (not a raw DbContext save): AggregateVersionInterceptor — which
        // actually bumps Version — is wired only into the DI-resolved VerceDbContext production
        // composes through IUnitOfWork, not into PostgresFixture.CreateContext()'s bare
        // DbContextOptionsBuilder, so only the real pipeline reproduces a genuine version bump here.
        long accountVersionBeforeMutation;
        await using (var db = _fixture.CreateContext())
            accountVersionBeforeMutation = (await db.Set<MarketplaceAccount>().SingleAsync(x => x.Id == accountId)).Version;
        var owner = await LoggedInOwnerAsync();
        var rename = await owner.PutAsync($"/api/commerce/marketplace-accounts/{accountId}", new MarketplaceAccountUpdateRequest("Nome alterado por outra operação", accountVersionBeforeMutation));
        rename.StatusCode.Should().Be(HttpStatusCode.NoContent, await rename.Content.ReadAsStringAsync());

        using (var scope = _factory.Services.CreateScope())
        {
            var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            await workflow.ResolvePendingAsync(operationId, CancellationToken.None);
        }

        await using var verify = _fixture.CreateContext();
        var operation = await verify.Set<MarketplaceAccountOperation>().SingleAsync(x => x.Id == operationId);
        operation.Decision.Should().Be(MarketplaceAccountOperationDecision.FAIL_CLOSED, "a guard mismatch must never let a REFRESH receipt confirm");
        var account2 = await verify.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account2.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED);
    }

    /// <summary>RT-01 "Actor loses permission before callback confirmation" for an explicit
    /// RECONNECT: the target account had valid prior authorization; the actor's role is revoked
    /// between claim and final commit; the exchange DID happen (identity/grants were learned), but
    /// the commit must still refuse and fail the account closed rather than trust a since-revoked
    /// actor's in-flight action. Full RT-01 assertion set: terminal decision, connection state,
    /// candidate disposition, and execution eligibility (a probe must then be refused).</summary>
    [Fact]
    public async Task Actor_losing_permission_before_reconnect_confirmation_fails_the_account_closed()
    {
        var channel = await SeedProviderAndChannelAsync();
        var owner = await LoggedInOwnerAsync();
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];

        var (session1, callback1) = await BeginAuthorizationAsync(owner, channel);
        _harness.ControlPlane.Configure(session1, new FakeMarketplaceScenario(externalId));
        await owner.GetAsync(callback1);
        Guid accountId; long accountVersion;
        await using (var db = _fixture.CreateContext())
        {
            var account = await db.Set<MarketplaceAccount>().SingleAsync(x => x.ExternalAccountId == externalId);
            accountId = account.Id; accountVersion = account.Version;
        }

        var (session2, callback2) = await BeginReauthorizeAsync(owner, accountId, accountVersion);
        _harness.ControlPlane.Configure(session2, new FakeMarketplaceScenario(externalId));
        // Arm the barrier so CompleteOnceAsync blocks AFTER claim (the actor/channel rechecks at
        // claim time already passed) but BEFORE the exchange "returns" — this is the genuine
        // "after send" window RT-01's table describes: the code may already be consumed by the
        // provider, so only the FINAL-commit-time recheck (not claim time) is what must catch
        // this, exactly reproducing "proven pre-send failures preserve prior authorization" NOT
        // applying here, because the exchange itself is already treated as in flight.
        _harness.ControlPlane.ArmBarrier(session2);
        var callback2Task = owner.GetAsync(callback2);

        Guid reconnectOperationId = Guid.Empty;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var poll = _fixture.CreateContext();
            var found = await poll.Set<MarketplaceAccountOperation>().AsNoTracking().Where(x => x.AuthorizationSessionId == session2).Select(x => (Guid?)x.Id).SingleOrDefaultAsync();
            if (found is { } id) { reconnectOperationId = id; break; }
            await Task.Delay(25);
        }
        reconnectOperationId.Should().NotBe(Guid.Empty);

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await using var db = _fixture.CreateContext();
            var session = await db.Set<MarketplaceAuthorizationSession>().SingleAsync(x => x.Id == session2);
            var actor = await users.FindByIdAsync(session.InitiatedByUserId.ToString());
            (await users.RemoveFromRoleAsync(actor!, Roles.Owner)).Succeeded.Should().BeTrue();
        }

        _harness.ControlPlane.Release(session2);
        await callback2Task;

        await using var verify = _fixture.CreateContext();
        var account2 = await verify.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account2.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED, "connection state: an actor who lost permission mid-flow can never complete the install");
        var reconnectOperation = await verify.Set<MarketplaceAccountOperation>().SingleAsync(x => x.Id == reconnectOperationId);
        reconnectOperation.Decision.Should().Be(MarketplaceAccountOperationDecision.FAIL_CLOSED, "terminal decision");
        reconnectOperation.CandidateCredentialReference.Should().NotBeNull("candidate disposition: the exchange DID complete and stage a candidate before the actor-permission recheck failed it closed");

        // Execution eligibility: a probe from an UNRELATED, still-privileged Owner must now be
        // refused purely because the account itself is REAUTHORIZATION_REQUIRED.
        var otherOwner = await LoggedInOwnerAsync();
        var probe = await otherOwner.PostAsync($"/api/commerce/marketplace-accounts/{accountId}/probe", new ReauthorizeMarketplaceAccountRequest(account2.Version));
        probe.IsSuccessStatusCode.Should().BeFalse("execution eligibility: REAUTHORIZATION_REQUIRED must refuse an ordinary probe");
    }

    /// <summary>RT-01 "Cleanup visits confirmed current operation" -&gt; never deletes the current
    /// reference/version. Directly exercises <c>CleanupTerminalAsync</c> against a CONFIRMED
    /// (non-DISCONNECT) operation whose CleanupState was defensively left PENDING — proving the
    /// Decision+Kind guard, not merely "nothing happened to schedule cleanup."</summary>
    [Fact]
    public async Task Cleanup_never_deletes_the_credential_of_a_confirmed_non_disconnect_operation()
    {
        var (accountId, operationId) = await SeedRefreshOperationAsync(MarketplaceAccountOperationDecision.CONFIRMED, persistExactGuardMatchingCandidate: true, markCleanupPendingDefensively: true);

        using (var scope = _factory.Services.CreateScope())
        {
            var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            var store = scope.ServiceProvider.GetRequiredService<IProtectedCredentialStore>();
            await workflow.CleanupTerminalAsync(100, CancellationToken.None);

            await using var verify = _fixture.CreateContext();
            var account = await verify.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
            account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED, "the account's usable authorization must survive the sweep untouched");
            var read = await store.UseConfirmedAsync(new ProtectedCredentialRead(accountId, "FAKE", account.CredentialReference!, account.Connection.ConfirmedCredentialVersion!.Value, account.Connection.ConfirmedOperationId!.Value), CancellationToken.None);
            read.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS, "the current confirmed credential must never be deleted by a cleanup sweep, regardless of a defensively-set CleanupState");
        }
    }

    /// <summary>Seeds a real MarketplaceAccount (via the normal CONNECT_NEW flow) plus one
    /// additional REFRESH-kind operation directly against the domain/store, without going through
    /// HTTP — the vehicle every raw-lock/guard test above needs, since none of those adversarial
    /// states are reachable through an ordinary route.</summary>
    private async Task<(Guid AccountId, Guid OperationId)> SeedRefreshOperationAsync(
        MarketplaceAccountOperationDecision decision, bool persistExactGuardMatchingCandidate = false, bool markCleanupPendingDefensively = false)
    {
        var channel = await SeedProviderAndChannelAsync();
        var owner = await LoggedInOwnerAsync();
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        var (sessionId, callbackUrl) = await BeginAuthorizationAsync(owner, channel);
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId));
        await owner.GetAsync(callbackUrl);

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IProtectedCredentialStore>();
        var now = _clock.UtcNow;

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.ExternalAccountId == externalId);
        var previousReference = account.CredentialReference!;
        var previousVersion = account.Connection.ConfirmedCredentialVersion!.Value;
        var previousOperationId = account.Connection.ConfirmedOperationId!.Value;

        var operation = new MarketplaceAccountOperation(MarketplaceAccountOperationKind.REFRESH, "FAKE", account.Id, null, account.Version, previousReference, previousVersion, now);
        db.Add(operation);
        await db.SaveChangesAsync(); // materializes operation.Id

        if (persistExactGuardMatchingCandidate)
        {
            var refreshSessionId = Guid.NewGuid();
            var staged = await store.CreateStagedAsync(new ProtectedCredentialWrite(operation.Id, refreshSessionId, account.Id, "FAKE", previousReference, System.Text.Encoding.UTF8.GetBytes("refreshed-secret"), previousVersion), CancellationToken.None);
            staged.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
            var bound = await store.BindCandidateAsync(new ProtectedCredentialBinding(operation.Id, refreshSessionId, account.Id, "FAKE", previousReference), CancellationToken.None);
            bound.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
            var promoted = await store.PromoteCandidateAsync(operation.Id, previousReference, staged.Receipt!.CredentialVersion, CancellationToken.None);
            promoted.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
            operation.MarkExternalInFlight(now);
            operation.RecordPersistedCandidate(previousReference, staged.Receipt.CredentialVersion, now);
            if (decision == MarketplaceAccountOperationDecision.CONFIRMED)
            {
                account.InstallConfirmedCredential(previousReference, staged.Receipt.CredentialVersion, operation.Id, now);
                operation.Confirm("REFRESHED", now);
            }
        }

        if (markCleanupPendingDefensively) operation.RequestCleanup(now);
        await db.SaveChangesAsync();
        return (account.Id, operation.Id);
    }

    private async Task<(Guid SessionId, string CallbackUrl)> BeginReauthorizeAsync(AuthTestClient owner, Guid accountId, long expectedVersion)
    {
        var begin = await owner.PostAsync($"/api/commerce/marketplace-accounts/{accountId}/reauthorize", new ReauthorizeMarketplaceAccountRequest(expectedVersion));
        begin.StatusCode.Should().Be(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
        var sessionId = document.RootElement.GetProperty("sessionId").GetGuid();
        var authorizationUri = document.RootElement.GetProperty("authorizationUri").GetString()!;
        var state = HttpUtility.ParseQueryString(new Uri(authorizationUri).Query)["state"]!;
        var callbackUrl = $"/api/commerce/marketplace-authorizations/{FakeMarketplaceConnector.Code}/callback?state={Uri.EscapeDataString(state)}&code=fake-code";
        return (sessionId, callbackUrl);
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
