using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Verce.Modules.Commerce;
using Verce.Modules.Pricing;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Time;

namespace Verce.Infrastructure.Marketplaces;

public sealed record BeginMarketplaceAuthorization(string ProviderCode, Guid SalesChannelId, string DisplayName, Guid ActorId, Guid? ReconnectAccountId, long? ExpectedAccountVersion);
public sealed record BegunMarketplaceAuthorization(Guid SessionId, string State, string BrowserBinding, DateTimeOffset ExpiresAt, string AuthorizationUri);
public sealed record MarketplaceWorkflowResult(bool Succeeded, string Code, Guid? AccountId = null);
public sealed record MarketplaceCallbackInput(string ProviderCode, string State, string BrowserBinding, string AuthorizationCode);

/// <summary>
/// The only workflow that transitions credential operations to a terminal decision. The store
/// may acknowledge bytes, but this class makes them executable only in the matching DB commit.
/// </summary>
public sealed class MarketplaceAuthorizationWorkflow
{
    private readonly IUnitOfWork _uow;
    private readonly IMarketplaceConnectorRegistry _registry;
    private readonly IProtectedCredentialStore _store;
    private readonly ProviderExecutionGate _gate;
    private readonly IClock _clock;

    // G-03: "a keyed async single-flight by immutable account ID is shared by refresh, reconnect
    // installation, generation replacement and disconnect". This process-wide dictionary is that
    // single-flight; it is intentionally process-local (S8C.1 assumes one application instance).
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> AccountLocks = new();

    public MarketplaceAuthorizationWorkflow(IUnitOfWork uow, IMarketplaceConnectorRegistry registry, IProtectedCredentialStore store, ProviderExecutionGate gate, IClock clock)
    { _uow = uow; _registry = registry; _store = store; _gate = gate; _clock = clock; }

    public async Task<BegunMarketplaceAuthorization> BeginAsync(BeginMarketplaceAuthorization input, CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(input.ProviderCode, out var connector)) throw new InvalidOperationException("MARKETPLACE_PROVIDER_NOT_SUPPORTED");
        var state = RandomValue();
        var browser = RandomValue();
        var now = _clock.UtcNow;
        MarketplaceAuthorizationSession? session = null;
        await _uow.ExecuteAsync(async (db, ct) =>
        {
            var actor = await db.Users.SingleOrDefaultAsync(x => x.Id == input.ActorId && x.IsActive, ct) ?? throw new UnauthorizedAccessException();
            var channel = await db.Set<SalesChannel>().SingleOrDefaultAsync(x => x.Id == input.SalesChannelId && x.Active && x.Kind == SalesChannelKind.Marketplace && x.Code != SalesChannel.DirectChannelCode, ct)
                ?? throw new ArgumentException("MARKETPLACE_ACCOUNT_CHANNEL_INVALID");
            if (!await db.Set<MarketplaceProvider>().AnyAsync(x => x.Code == input.ProviderCode, ct)) throw new ArgumentException("MARKETPLACE_PROVIDER_NOT_FOUND");
            if ((input.ReconnectAccountId is null) != (input.ExpectedAccountVersion is null)) throw new ArgumentException("MARKETPLACE_RECONNECT_CONTEXT_INVALID");
            if (input.ReconnectAccountId is { } accountId)
            {
                var account = await db.Set<MarketplaceAccount>().SingleOrDefaultAsync(x => x.Id == accountId, ct) ?? throw new ArgumentException("MARKETPLACE_ACCOUNT_NOT_FOUND");
                if (account.Version != input.ExpectedAccountVersion || account.ProviderCode != input.ProviderCode || account.SalesChannelId != input.SalesChannelId || !account.Active)
                    throw new ArgumentException("MARKETPLACE_RECONNECT_CONTEXT_INVALID");
            }
            session = new MarketplaceAuthorizationSession(input.ProviderCode, input.SalesChannelId, actor.Id, input.DisplayName, Hash(state), Hash(browser), input.ReconnectAccountId, input.ExpectedAccountVersion, now);
            db.Add(session);
        }, cancellationToken);
        var start = await connector.BeginAsync(new MarketplaceAuthorizationBeginRequest(session!.Id, state, "/api/commerce/marketplace-authorizations/" + input.ProviderCode + "/callback"), cancellationToken);
        return new(session.Id, state, browser, session.ExpiresAt, start.AuthorizationUri);
    }

    public async Task<MarketplaceWorkflowResult> DisconnectAsync(Guid accountId, long expectedVersion, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        await _uow.ExecuteAsync(async (db, ct) =>
        {
            var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleOrDefaultAsync(x => x.Id == accountId, ct) ?? throw new KeyNotFoundException();
            if (account.Version != expectedVersion) throw new InvalidOperationException("CONCURRENCY_CONFLICT");
            var operation = new MarketplaceAccountOperation(MarketplaceAccountOperationKind.DISCONNECT, account.ProviderCode, account.Id, null, account.Version, account.CredentialReference, account.Connection.ConfirmedCredentialVersion, now);
            account.Revoke(now);
            operation.Confirm("DISCONNECTED", now);
            operation.RequestCleanup(now);
            db.Add(operation);
        }, cancellationToken);
        return new(true, "DISCONNECTED", accountId);
    }

    /// <summary>
    /// G-03: refresh is a keyed single-flight per account, the provider gate is acquired before
    /// the account lock, and a durable REFRESH operation row is committed and advanced to
    /// EXTERNAL_IN_FLIGHT before any refresh call can be sent. A waiter that queues behind the
    /// lock reloads current state instead of blindly refreshing again.
    /// </summary>
    public async Task<MarketplaceWorkflowResult> RefreshAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var providerCode = await _uow.ExecuteAsync(async (db, ct) =>
            (await db.Set<MarketplaceAccount>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == accountId, ct))?.ProviderCode, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (!_registry.TryGet(providerCode, out var connector)) return new(false, "MARKETPLACE_PROVIDER_NOT_SUPPORTED");

        await using var providerLease = await _gate.EnterSharedAsync(providerCode, cancellationToken);
        var accountLock = AccountLocks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await accountLock.WaitAsync(cancellationToken);
        try
        {
            var now = _clock.UtcNow;
            RefreshClaim? claim = null;
            await _uow.ExecuteAsync(async (db, ct) =>
            {
                var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId, ct);
                if (account.Connection.AuthorizationState != MarketplaceAuthorizationState.CONNECTED || account.CredentialReference is null || account.Connection.ConfirmedCredentialVersion is not { } confirmedVersion || account.Connection.ConfirmedOperationId is not { } confirmedOperationId)
                    return; // not usable: nothing to refresh, waiters simply see current state
                // Waiters that queued behind the lock re-evaluate expiry here instead of refreshing again.
                if (account.Connection.AccessExpiresAt is { } expiresAt && expiresAt > now) return;
                var operation = new MarketplaceAccountOperation(MarketplaceAccountOperationKind.REFRESH, account.ProviderCode, account.Id, null, account.Version, account.CredentialReference, confirmedVersion, now);
                operation.MarkExternalInFlight(now);
                db.Add(operation);
                claim = new RefreshClaim(operation.Id, account.CredentialReference, confirmedVersion, confirmedOperationId, account.ProviderCode);
            }, cancellationToken);

            if (claim is null) return new(true, "REFRESH_NOT_REQUIRED", accountId);

            var read = await _store.UseConfirmedAsync(new ProtectedCredentialRead(accountId, claim.ProviderCode, claim.Reference, claim.PreviousVersion, claim.PreviousOperationId), cancellationToken);
            if (read.Outcome != ProtectedCredentialStoreOutcome.SUCCESS)
            {
                await ResolvePendingAsync(claim.OperationId, cancellationToken);
                return new(false, "REAUTHORIZATION_REQUIRED", accountId);
            }

            MarketplaceRefreshCompletion completion;
            try
            {
                completion = await connector.RefreshAsync(new MarketplaceRefreshRequest(accountId, claim.ProviderCode, read.SecretMaterial), cancellationToken);
            }
            catch
            {
                // No send-certainty channel exists yet on the port; a failed/ambiguous exchange is
                // treated conservatively (fail closed) rather than ever risking a blind replay.
                await ResolvePendingAsync(claim.OperationId, cancellationToken);
                return new(false, "REAUTHORIZATION_REQUIRED", accountId);
            }

            var cas = await _store.CompareAndSwapReplaceAsync(new ProtectedCredentialWrite(claim.OperationId, null, accountId, claim.ProviderCode, claim.Reference, completion.SecretMaterial), claim.PreviousVersion, cancellationToken);
            if (cas.Outcome != ProtectedCredentialStoreOutcome.SUCCESS || cas.Receipt is null)
            {
                // A foreign CAS conflict or write failure after a possibly-consumed single-use
                // refresh token must never be silently retried against the provider.
                await ResolvePendingAsync(claim.OperationId, cancellationToken);
                return new(false, "REAUTHORIZATION_REQUIRED", accountId);
            }

            try
            {
                await _uow.ExecuteAsync(async (db, ct) =>
                {
                    var operation = await db.Set<MarketplaceAccountOperation>().FromSqlInterpolated($"SELECT * FROM commerce.marketplace_account_operation WHERE id = {claim.OperationId} FOR UPDATE").SingleAsync(ct);
                    if (operation.Decision != MarketplaceAccountOperationDecision.PENDING) throw new InvalidOperationException("MARKETPLACE_OPERATION_TERMINAL");
                    var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId, ct);
                    operation.RecordPersistedCandidate(claim.Reference, cas.Receipt.CredentialVersion, _clock.UtcNow);
                    account.InstallConfirmedCredential(claim.Reference, cas.Receipt.CredentialVersion, operation.Id, _clock.UtcNow);
                    operation.Confirm("REFRESHED", _clock.UtcNow);
                }, cancellationToken);
                return new(true, "REFRESHED", accountId);
            }
            catch (DbUpdateException)
            {
                await ResolvePendingAsync(claim.OperationId, cancellationToken);
                return new(false, "REAUTHORIZATION_REQUIRED", accountId);
            }
        }
        finally { accountLock.Release(); }
    }

    private sealed record RefreshClaim(Guid OperationId, string Reference, long PreviousVersion, Guid PreviousOperationId, string ProviderCode);

    /// <summary>
    /// G-04: the mandatory explicit availability/grant-refresh operation. It requires the account
    /// to be usable and active, reads the current confirmed secret and calls the registered
    /// inspector; success advances runtime to AVAILABLE and refreshes grant observations, failure
    /// records a safe classification and moves runtime to UNAVAILABLE. It never installs a new
    /// credential generation.
    /// </summary>
    public async Task<MarketplaceWorkflowResult> ProbeAsync(Guid accountId, long expectedVersion, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var snapshot = await _uow.ExecuteAsync(async (db, ct) =>
        {
            var account = await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null) return null;
            var channelActive = await db.Set<SalesChannel>().AnyAsync(x => x.Id == account.SalesChannelId && x.Active && x.Kind == SalesChannelKind.Marketplace, ct);
            return new ProbeSnapshot(account.Version, account.Active, channelActive, account.ProviderCode, account.CredentialReference, account.Connection.AuthorizationState, account.Connection.ConfirmedCredentialVersion, account.Connection.ConfirmedOperationId);
        }, cancellationToken) ?? throw new KeyNotFoundException();

        if (snapshot.Version != expectedVersion) throw new InvalidOperationException("CONCURRENCY_CONFLICT");
        if (!snapshot.Active || !snapshot.ChannelActive || snapshot.AuthorizationState != MarketplaceAuthorizationState.CONNECTED
            || snapshot.CredentialReference is null || snapshot.ConfirmedCredentialVersion is not { } version || snapshot.ConfirmedOperationId is not { } operationId)
            return new(false, "MARKETPLACE_AUTHORIZATION_NOT_USABLE", accountId);
        if (!_registry.TryGet(snapshot.ProviderCode, out var connector)) return new(false, "MARKETPLACE_PROVIDER_NOT_SUPPORTED", accountId);

        var read = await _store.UseConfirmedAsync(new ProtectedCredentialRead(accountId, snapshot.ProviderCode, snapshot.CredentialReference, version, operationId), cancellationToken);
        if (read.Outcome != ProtectedCredentialStoreOutcome.SUCCESS)
        {
            // ADR-0024 §5/G-05: NOT_FOUND/CORRUPTED_OR_UNDECRYPTABLE mean the confirmed credential
            // is CONFIRMED lost, not merely transiently unreachable — CommerceEndpoints.RequiredAction
            // only ever surfaces "REAUTHORIZE_AFTER_STORE_LOSS" (and the frontend's dedicated
            // recovery button/copy) when SafeFailureCode="PROBE_CREDENTIAL_UNREADABLE" is paired
            // with AuthorizationState=REAUTHORIZATION_REQUIRED — CONNECTED+UNAVAILABLE always takes
            // priority as "CHECK_PROVIDER_ACCOUNT" regardless of SafeFailureCode (a real defect
            // this fixes: a probe against a confirmed-lost local credential used to stay CONNECTED
            // and tell the Owner to "check the provider account" instead of routing them to
            // reauthorize). A genuinely transient outcome (STORE_UNAVAILABLE/VERSION_CONFLICT) is
            // NOT a confirmed loss and keeps the milder runtime-failure treatment.
            if (read.Outcome is ProtectedCredentialStoreOutcome.NOT_FOUND or ProtectedCredentialStoreOutcome.CORRUPTED_OR_UNDECRYPTABLE)
                await _uow.ExecuteAsync(async (db, ct) =>
                {
                    var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId, ct);
                    account.RequireReauthorization("PROBE_CREDENTIAL_UNREADABLE", _clock.UtcNow);
                }, cancellationToken);
            else
                await MarkUnavailableAsync(accountId, "CREDENTIAL_UNREADABLE", "PROBE_CREDENTIAL_UNREADABLE", cancellationToken);
            return new(false, "PROBE_CREDENTIAL_UNREADABLE", accountId);
        }

        try
        {
            var inspection = await connector.InspectAsync(new MarketplaceAccountInspectionRequest(accountId, snapshot.ProviderCode, read.SecretMaterial), cancellationToken);
            await _uow.ExecuteAsync(async (db, ct) =>
            {
                var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).Include(x => x.Capabilities).SingleAsync(x => x.Id == accountId, ct);
                foreach (var grant in inspection.Grants) account.SetCapability(grant.Key, grant.Value, "PROBE", null, _clock.UtcNow);
                account.MarkAvailable(_clock.UtcNow);
            }, cancellationToken);
            return new(true, "PROBE_AVAILABLE", accountId);
        }
        catch
        {
            await MarkUnavailableAsync(accountId, "TRANSIENT", "PROBE_FAILED", cancellationToken);
            return new(false, "PROBE_FAILED", accountId);
        }
    }

    private async Task MarkUnavailableAsync(Guid accountId, string classification, string safeCode, CancellationToken cancellationToken) =>
        await _uow.ExecuteAsync(async (db, ct) =>
        {
            var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId, ct);
            account.RecordRuntimeFailure(classification, safeCode, _clock.UtcNow);
        }, cancellationToken);

    private sealed record ProbeSnapshot(long Version, bool Active, bool ChannelActive, string ProviderCode, string? CredentialReference, MarketplaceAuthorizationState AuthorizationState, long? ConfirmedCredentialVersion, Guid? ConfirmedOperationId);

    /// <summary>PENDING -> REVOKED by the initiating Owner before any exchange has happened.</summary>
    public async Task<MarketplaceWorkflowResult> CancelSessionAsync(Guid sessionId, Guid actorId, long expectedVersion, CancellationToken cancellationToken)
    {
        await _uow.ExecuteAsync(async (db, ct) =>
        {
            var session = await db.Set<MarketplaceAuthorizationSession>().SingleOrDefaultAsync(x => x.Id == sessionId, ct) ?? throw new KeyNotFoundException();
            if (session.InitiatedByUserId != actorId) throw new UnauthorizedAccessException();
            if (session.Version != expectedVersion) throw new InvalidOperationException("CONCURRENCY_CONFLICT");
            session.Revoke("CANCELLED_BY_OWNER", _clock.UtcNow);
        }, cancellationToken);
        return new(true, "CANCELLED", null);
    }

    public async Task<MarketplaceWorkflowResult> CompleteCallbackAsync(MarketplaceCallbackInput input, CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(input.ProviderCode, out var connector)) return new(false, "MARKETPLACE_PROVIDER_NOT_SUPPORTED");
        CallbackClaim claim;
        await using (await _gate.EnterExclusiveAsync(input.ProviderCode, cancellationToken))
        {
            try
            {
                claim = await ClaimForCallbackAsync(input, cancellationToken);
            }
            catch (InvalidOperationException ex) { return new(false, ex.Message); }

            MarketplaceAuthorizationCompletion completion;
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(TimeSpan.FromSeconds(60));
                completion = await connector.CompleteOnceAsync(new MarketplaceAuthorizationCompletionRequest(claim.SessionId, input.AuthorizationCode), budget.Token);
            }
            catch (MarketplaceRequestNotSentException)
            {
                // Proven NOT_SENT (G-08): a normal denial/local failure before any provider bytes
                // went out. This affects only this session's own target — never every account for
                // the provider. Preserves existing authorization exactly (RT-01: "proven pre-send
                // failures preserve previously valid authorization").
                await FailSingleOperationAsync(claim.OperationId, cancellationToken);
                return new(false, "AUTHORIZATION_EXCHANGE_FAILED");
            }
            catch
            {
                // SENT_OR_UNKNOWN (the default — G-08: "an error classification alone cannot
                // establish [NOT_SENT]"): ResolvePendingAsync's CONNECT_NEW branch conservatively
                // fails every currently CONNECTED account for this provider closed, since we
                // cannot prove which, if any, existing identity this ambiguous exchange affected.
                await ResolvePendingAsync(claim.OperationId, cancellationToken);
                return new(false, "AUTHORIZATION_EXCHANGE_FAILED");
            }

            if (claim.ReconnectAccountId is { } reconnectId &&
                (!string.Equals(claim.ExpectedExternalAccountId, completion.ExternalAccountId, StringComparison.Ordinal) || !string.Equals(claim.ProviderCode, input.ProviderCode, StringComparison.OrdinalIgnoreCase)))
            {
                // RT-02 "Explicit RECONNECT X returns identity owned by Y": X always requires
                // reauthorization (its own sent exchange is now unaccounted for). Y — whichever
                // account currently owns the RETURNED identity, if any — is also forced closed
                // under UNKNOWN/MAY_SUPERSEDE_EXISTING impact; only officially proven
                // PRESERVES_EXISTING evidence leaves Y untouched. Never rebind X to Y.
                await FailReconnectMismatchAsync(claim.OperationId, reconnectId, input.ProviderCode, completion.ExternalAccountId, completion.CredentialImpact, "AUTHORIZATION_IDENTITY_MISMATCH", cancellationToken);
                return new(false, "AUTHORIZATION_IDENTITY_MISMATCH");
            }

            // A new account id and opaque reference are server-generated before staging. They are
            // still non-executable: only the later operation CONFIRMED transaction can use them.
            var candidateAccountId = claim.ReconnectAccountId ?? Guid.CreateVersion7();
            var reusingPreviousReference = claim.PreviousCredentialReference is not null;
            var candidateReference = claim.PreviousCredentialReference ?? NewCredentialReference();
            var staged = await _store.CreateStagedAsync(new ProtectedCredentialWrite(claim.OperationId, claim.SessionId, candidateAccountId, input.ProviderCode, candidateReference, completion.SecretMaterial, reusingPreviousReference ? claim.PreviousConfirmedVersion : null), cancellationToken);

            // RT-03: the previous reference's material is confirmed missing/corrupt/stale
            // (NOT_FOUND/CORRUPTED/VERSION_CONFLICT against our own DB expectation). K1 is
            // retired for execution; a fresh K2 reference starts its own version sequence at 1.
            // This never happens for CONNECT_NEW (no previous reference to lose).
            var recoveredFromStoreLoss = false;
            if (reusingPreviousReference && staged.Outcome is ProtectedCredentialStoreOutcome.NOT_FOUND or ProtectedCredentialStoreOutcome.CORRUPTED_OR_UNDECRYPTABLE or ProtectedCredentialStoreOutcome.VERSION_CONFLICT)
            {
                candidateReference = NewCredentialReference();
                recoveredFromStoreLoss = true;
                staged = await _store.CreateStagedAsync(new ProtectedCredentialWrite(claim.OperationId, claim.SessionId, candidateAccountId, input.ProviderCode, candidateReference, completion.SecretMaterial, null), cancellationToken);
            }
            if (staged.Outcome != ProtectedCredentialStoreOutcome.SUCCESS || staged.Receipt is null)
            {
                await ResolvePendingAsync(claim.OperationId, cancellationToken);
                return new(false, "CREDENTIAL_PERSISTENCE_FAILED");
            }
            var bound = await _store.BindCandidateAsync(new ProtectedCredentialBinding(claim.OperationId, claim.SessionId, candidateAccountId, input.ProviderCode, candidateReference), cancellationToken);
            if (bound.Outcome != ProtectedCredentialStoreOutcome.SUCCESS)
            {
                await ResolvePendingAsync(claim.OperationId, cancellationToken);
                return new(false, "CREDENTIAL_BINDING_FAILED");
            }
            try
            {
                // A concurrent resolver (startup/cleanup/abandonment) may have already won the
                // operation row's terminal decision (RT-01 "resolver wins terminal row lock
                // before callback commit") while this exchange was in flight. RecordReceiptAsync
                // observes that under its own FOR UPDATE lock and throws — this callback must
                // never let that escape as an unhandled exception, and must never leave the
                // just-persisted candidate material orphaned and undeleted.
                await RecordReceiptAsync(claim.OperationId, candidateReference, staged.Receipt.CredentialVersion, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // ResolvePendingAsync is idempotent (no-op once the row is already terminal) —
                // calling it here guarantees the bound account (if any) is actually marked
                // REAUTHORIZATION_REQUIRED even in the rare case this callback is the FIRST to
                // observe the terminal state, not merely a late arrival behind an external resolver.
                await ResolvePendingAsync(claim.OperationId, cancellationToken);
                await _store.DeleteCandidateAsync(claim.OperationId, candidateReference, staged.Receipt.CredentialVersion, cancellationToken);
                return new(false, "OPERATION_RECOVERY_FAIL_CLOSED");
            }
            var promoted = await _store.PromoteCandidateAsync(claim.OperationId, candidateReference, staged.Receipt.CredentialVersion, cancellationToken);
            if (promoted.Outcome != ProtectedCredentialStoreOutcome.SUCCESS)
            {
                await ResolvePendingAsync(claim.OperationId, cancellationToken);
                await _store.DeleteCandidateAsync(claim.OperationId, candidateReference, staged.Receipt.CredentialVersion, cancellationToken);
                return new(false, "CREDENTIAL_CONFIRMATION_FAILED");
            }
            try
            {
                return await ConfirmCallbackAsync(claim, completion, candidateAccountId, candidateReference, staged.Receipt.CredentialVersion, recoveredFromStoreLoss, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // ConfirmCallbackAsync's own FOR UPDATE observed the row already terminal
                // (resolver won between promotion and this final commit attempt) OR its own
                // actor-permission recheck just failed. Either way ResolvePendingAsync is the
                // single place that fails the operation closed AND marks the bound account (for
                // RECONNECT) or every provider account (for an unbound CONNECT_NEW) — it must run
                // here, not only be assumed to have already run.
                await ResolvePendingAsync(claim.OperationId, cancellationToken);
                await _store.DeleteCandidateAsync(claim.OperationId, candidateReference, staged.Receipt.CredentialVersion, cancellationToken);
                return new(false, "OPERATION_RECOVERY_FAIL_CLOSED");
            }
        }
    }

    private async Task<CallbackClaim> ClaimForCallbackAsync(MarketplaceCallbackInput input, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        return await _uow.ExecuteAsync(async (db, ct) =>
        {
            var session = await db.Set<MarketplaceAuthorizationSession>().SingleOrDefaultAsync(x => x.StateHash == Hash(input.State), ct) ?? throw new InvalidOperationException("AUTHORIZATION_SESSION_INVALID");
            if (!string.Equals(session.ProviderCode, input.ProviderCode, StringComparison.OrdinalIgnoreCase) || !session.TryClaim(Hash(input.State), Hash(input.BrowserBinding), session.InitiatedByUserId, now))
                throw new InvalidOperationException("AUTHORIZATION_SESSION_INVALID");
            var actor = await db.Users.SingleOrDefaultAsync(x => x.Id == session.InitiatedByUserId && x.IsActive, ct) ?? throw new InvalidOperationException("AUTHORIZATION_ACTOR_INVALID");
            if (!await HasCommerceAccountsManagePermissionAsync(db, actor.Id, ct)) throw new InvalidOperationException("AUTHORIZATION_ACTOR_INVALID");
            // "Recheck actor, channel activity and expected account Version before installing
            // credentials" (ADR-0024 §2): the channel may have been deactivated between Begin and
            // this callback. Throwing here rolls back the whole transaction (including the CLAIMED
            // transition just made) since no provider exchange has happened yet — safe to require
            // a fresh session rather than silently install a credential against a channel that
            // is no longer a valid Marketplace destination.
            if (!await db.Set<SalesChannel>().AnyAsync(x => x.Id == session.SalesChannelId && x.Active && x.Kind == SalesChannelKind.Marketplace, ct))
                throw new InvalidOperationException("MARKETPLACE_ACCOUNT_CHANNEL_INVALID");
            var operation = new MarketplaceAccountOperation(session.ReconnectMarketplaceAccountId is null ? MarketplaceAccountOperationKind.CONNECT_NEW : MarketplaceAccountOperationKind.RECONNECT,
                session.ProviderCode, session.ReconnectMarketplaceAccountId, session.Id, session.ReconnectAccountVersion, null, null, now);
            string? externalId = null;
            if (session.ReconnectMarketplaceAccountId is { } accountId)
            {
                var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleOrDefaultAsync(x => x.Id == accountId, ct) ?? throw new InvalidOperationException("MARKETPLACE_ACCOUNT_NOT_FOUND");
                if (account.Version != session.ReconnectAccountVersion || !account.Active) throw new InvalidOperationException("MARKETPLACE_RECONNECT_CONTEXT_INVALID");
                externalId = account.ExternalAccountId;
                operation = new MarketplaceAccountOperation(MarketplaceAccountOperationKind.RECONNECT, session.ProviderCode, account.Id, session.Id, account.Version, account.CredentialReference, account.Connection.ConfirmedCredentialVersion, now);
            }
            operation.MarkExternalInFlight(now);
            db.Add(operation);
            return new CallbackClaim(session.Id, operation.Id, session.ProviderCode, session.SalesChannelId, session.RequestedDisplayName, session.ReconnectMarketplaceAccountId, externalId, operation.PreviousCredentialReference, operation.PreviousConfirmedCredentialVersion);
        }, cancellationToken);
    }

    private async Task RecordReceiptAsync(Guid operationId, string reference, long version, CancellationToken cancellationToken)
    {
        await _uow.ExecuteAsync(async (db, ct) =>
        {
            var operation = await db.Set<MarketplaceAccountOperation>().FromSqlInterpolated($"SELECT * FROM commerce.marketplace_account_operation WHERE id = {operationId} FOR UPDATE").SingleAsync(ct);
            if (operation.Decision != MarketplaceAccountOperationDecision.PENDING) throw new InvalidOperationException("MARKETPLACE_OPERATION_TERMINAL");
            operation.RecordPersistedCandidate(reference, version, _clock.UtcNow);
        }, cancellationToken);
    }

    private async Task<MarketplaceWorkflowResult> ConfirmCallbackAsync(CallbackClaim claim, MarketplaceAuthorizationCompletion completion, Guid candidateAccountId, string reference, long version, bool recoveredFromStoreLoss, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        try
        {
            var result = await _uow.ExecuteAsync(async (db, ct) =>
            {
                var operation = await db.Set<MarketplaceAccountOperation>().FromSqlInterpolated($"SELECT * FROM commerce.marketplace_account_operation WHERE id = {claim.OperationId} FOR UPDATE").SingleAsync(ct);
                var session = await db.Set<MarketplaceAuthorizationSession>().SingleAsync(x => x.Id == claim.SessionId, ct);
                if (operation.Decision != MarketplaceAccountOperationDecision.PENDING || session.Status != MarketplaceAuthorizationSessionStatus.CLAIMED) throw new InvalidOperationException("MARKETPLACE_OPERATION_TERMINAL");
                // RT-01 "Actor loses permission before callback confirmation": the final commit
                // rechecks the SAME actor/permission guard as claim time, not only at claim.
                if (!await HasCommerceAccountsManagePermissionAsync(db, session.InitiatedByUserId, ct)) throw new InvalidOperationException("AUTHORIZATION_ACTOR_INVALID");
                MarketplaceAccount account;
                if (claim.ReconnectAccountId is { } existingId)
                {
                    account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).Include(x => x.Capabilities).SingleAsync(x => x.Id == existingId, ct);
                }
                else
                {
                    var existing = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleOrDefaultAsync(x => x.ProviderCode == claim.ProviderCode && x.ExternalAccountId == completion.ExternalAccountId, ct);
                    if (existing is not null)
                    {
                        // RT-02: a duplicate new-session exchange never silently becomes an
                        // unbound reconnect and never installs a candidate on the existing
                        // account. Only officially proven PRESERVES_EXISTING impact evidence
                        // lets the existing account remain CONNECTED (still no new candidate
                        // installed — the owner must still explicitly reconnect for that);
                        // UNKNOWN/MAY_SUPERSEDE_EXISTING conservatively forces reauthorization,
                        // including when existing is inactive (history/inactive flag preserved).
                        if (completion.CredentialImpact != CredentialImpact.PRESERVES_EXISTING)
                            existing.RequireReauthorization("RECONNECT_EXISTING_ACCOUNT", now);
                        operation.BindAccount(existing.Id);
                        operation.FailClosed("ACCOUNT_ALREADY_EXISTS_RECONNECT_REQUIRED", now);
                        operation.RequestCleanup(now);
                        session.Fail("ACCOUNT_ALREADY_EXISTS_RECONNECT_REQUIRED", now);
                        return new MarketplaceWorkflowResult(false, "RECONNECT_EXISTING_ACCOUNT", existing.Id);
                    }
                    account = new MarketplaceAccount(candidateAccountId, claim.ProviderCode, completion.ExternalAccountId, claim.SalesChannelId, claim.DisplayName);
                    db.Add(account);
                }
                operation.BindAccount(account.Id);
                account.InstallConfirmedCredential(reference, version, operation.Id, now);
                foreach (var grant in completion.Grants) account.SetCapability(grant.Key, grant.Value, "AUTH_INSPECTION", null, now);
                var safeCode = recoveredFromStoreLoss ? "AUTHORIZED_AFTER_STORE_LOSS" : "AUTHORIZED";
                operation.Confirm(safeCode, now);
                session.Complete(safeCode, now);
                return new MarketplaceWorkflowResult(true, safeCode, account.Id);
            }, cancellationToken);
            if (!result.Succeeded)
                await _store.DeleteCandidateAsync(claim.OperationId, reference, version, cancellationToken);
            return result;
        }
        catch (DbUpdateException)
        {
            // RT-02 "Two CONNECT_NEW sessions resolve to new X": the unique (ProviderCode,
            // ExternalAccountId) index is the final identity authority — this transaction lost.
            // "Its successful exchange may have superseded the winner's credential": under
            // UNKNOWN/MAY_SUPERSEDE_EXISTING impact the winner must not remain falsely CONNECTED
            // on an unconfirmed assumption that its own exchange is still the valid one.
            await ResolvePendingAsync(claim.OperationId, cancellationToken);
            await _store.DeleteCandidateAsync(claim.OperationId, reference, version, cancellationToken);
            if (completion.CredentialImpact != CredentialImpact.PRESERVES_EXISTING)
            {
                await _uow.ExecuteAsync(async (db, ct) =>
                {
                    var winner = await db.Set<MarketplaceAccount>().Include(x => x.Connection)
                        .SingleOrDefaultAsync(x => x.ProviderCode == claim.ProviderCode && x.ExternalAccountId == completion.ExternalAccountId, ct);
                    if (winner is not null && winner.Connection.AuthorizationState == MarketplaceAuthorizationState.CONNECTED)
                        winner.RequireReauthorization("CONCURRENT_AUTHORIZATION_CONFLICT", _clock.UtcNow);
                }, cancellationToken);
            }
            return new(false, "RECONNECT_EXISTING_ACCOUNT");
        }
    }

    private async Task FailReconnectMismatchAsync(Guid operationId, Guid reconnectAccountId, string providerCode, string returnedExternalAccountId, CredentialImpact impact, string code, CancellationToken cancellationToken)
    {
        await _uow.ExecuteAsync(async (db, ct) =>
        {
            var operation = await db.Set<MarketplaceAccountOperation>().FromSqlInterpolated($"SELECT * FROM commerce.marketplace_account_operation WHERE id = {operationId} FOR UPDATE").SingleAsync(ct);
            var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == reconnectAccountId, ct);
            operation.FailClosed(code, _clock.UtcNow);
            operation.RequestCleanup(_clock.UtcNow);
            // X: its own sent exchange is now unaccounted for — always requires reauthorization,
            // independent of impact evidence (ADR-0024 §2: "X requires reauthorization after the
            // sent exchange" is unconditional).
            account.RequireReauthorization(code, _clock.UtcNow);

            // Y: whichever account currently owns the RETURNED identity, if any. Never rebind X to
            // Y; only officially proven PRESERVES_EXISTING evidence leaves Y's own authorization
            // untouched (RT-02).
            if (impact != CredentialImpact.PRESERVES_EXISTING)
            {
                var owner = await db.Set<MarketplaceAccount>().Include(x => x.Connection)
                    .SingleOrDefaultAsync(x => x.ProviderCode == providerCode && x.ExternalAccountId == returnedExternalAccountId && x.Id != reconnectAccountId, ct);
                owner?.RequireReauthorization("RECONNECT_TARGET_IDENTITY_MISMATCH", _clock.UtcNow);
            }
        }, cancellationToken);
    }

    private sealed record CallbackClaim(Guid SessionId, Guid OperationId, string ProviderCode, Guid SalesChannelId, string DisplayName, Guid? ReconnectAccountId, string? ExpectedExternalAccountId, string? PreviousCredentialReference, long? PreviousConfirmedVersion);

    /// <summary>Proven NOT_SENT resolution: fails only this operation and (for a bound reconnect
    /// target only) that one account — never other accounts for the provider, since no exchange
    /// reached the provider at all.</summary>
    private async Task FailSingleOperationAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        await _uow.ExecuteAsync(async (db, ct) =>
        {
            var operation = await db.Set<MarketplaceAccountOperation>().FromSqlInterpolated($"SELECT * FROM commerce.marketplace_account_operation WHERE id = {operationId} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (operation is null || operation.Decision != MarketplaceAccountOperationDecision.PENDING) return;
            operation.FailClosed("AUTHORIZATION_NOT_SENT", now);
            operation.RequestCleanup(now);
            if (operation.MarketplaceAccountId is { } accountId)
            {
                // Reconnect target only: its prior credential remains valid (nothing was sent),
                // so it is NOT force-reauthorized — the ADR explicitly preserves it here. We still
                // clear the operation as failed so a retry starts a fresh session.
            }
            if (operation.AuthorizationSessionId is { } sessionId)
            {
                var session = await db.Set<MarketplaceAuthorizationSession>().SingleOrDefaultAsync(x => x.Id == sessionId, ct);
                if (session is { Status: MarketplaceAuthorizationSessionStatus.CLAIMED }) session.Fail("AUTHORIZATION_NOT_SENT", now);
            }
        }, cancellationToken);
    }

    public async Task<MarketplaceWorkflowResult> ResolvePendingAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        await _uow.ExecuteAsync(async (db, ct) =>
        {
            // PostgreSQL obtains the same terminal-arbiter row lock for all startup/cleanup
            // resolution; an unlocked negative receipt is never treated as a rollback proof.
            var operation = await db.Set<MarketplaceAccountOperation>().FromSqlInterpolated($"SELECT * FROM commerce.marketplace_account_operation WHERE id = {operationId} FOR UPDATE").SingleOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException();
            if (operation.Decision != MarketplaceAccountOperationDecision.PENDING) return;

            // ADR-0024 §2/G-03: "REFRESH may confirm an exact same-operation store receipt when
            // all refresh guards in section 2 still hold." CONNECT_NEW/RECONNECT NEVER take this
            // path — their full committed callback transaction is the only thing that can confirm
            // them; a receipt alone can never complete either after restart. Every guard below
            // must match EXACTLY, or this falls through to the ordinary fail-closed path.
            if (operation.Kind == MarketplaceAccountOperationKind.REFRESH
                && operation.Phase == MarketplaceAccountOperationPhase.SECRET_PERSISTED
                && operation.MarketplaceAccountId is { } refreshAccountId
                && operation.CandidateCredentialReference is { } candidateReference
                && operation.CandidateCredentialVersion is { } candidateVersion)
            {
                var refreshAccount = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == refreshAccountId, ct);
                var guardsHold = refreshAccount.Version == operation.ExpectedAccountVersion
                    && refreshAccount.CredentialReference == operation.PreviousCredentialReference
                    && refreshAccount.Connection.ConfirmedCredentialVersion == operation.PreviousConfirmedCredentialVersion
                    && candidateReference == operation.PreviousCredentialReference
                    && candidateVersion == (operation.PreviousConfirmedCredentialVersion ?? 0) + 1;
                if (guardsHold)
                {
                    refreshAccount.InstallConfirmedCredential(candidateReference, candidateVersion, operation.Id, now);
                    operation.Confirm("REFRESHED_FROM_RECEIPT", now);
                    return;
                }
            }

            operation.FailClosed("OPERATION_RECOVERY_FAIL_CLOSED", now);
            operation.RequestCleanup(now);
            if (operation.MarketplaceAccountId is { } accountId)
            {
                var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId, ct);
                account.RequireReauthorization("OPERATION_RECOVERY_FAIL_CLOSED", now);
            }
            else if (operation.Kind == MarketplaceAccountOperationKind.CONNECT_NEW)
            {
                // RT-02 "Crash after exchange before identity is known": a CONNECT_NEW operation
                // being force-resolved with NO bound account means the exchange's returned
                // identity (if any) was never learned — we cannot prove which, if any, existing
                // account it might have superseded. The provider-wide fence's own safe default
                // (deny) is preserved here by marking every currently CONNECTED account for this
                // provider REAUTHORIZATION_REQUIRED before this resolution — never a targeted
                // guess, never silently trusting old credentials to still be valid.
                var affected = await db.Set<MarketplaceAccount>().Include(x => x.Connection)
                    .Where(x => x.ProviderCode == operation.ProviderCode && x.Connection.AuthorizationState == MarketplaceAuthorizationState.CONNECTED)
                    .ToListAsync(ct);
                foreach (var account in affected) account.RequireReauthorization("UNKNOWN_IDENTITY_AFTER_EXCHANGE", now);
            }
            if (operation.AuthorizationSessionId is { } sessionId)
            {
                var session = await db.Set<MarketplaceAuthorizationSession>().SingleAsync(x => x.Id == sessionId, ct);
                if (session.Status == MarketplaceAuthorizationSessionStatus.CLAIMED) session.Fail("OPERATION_RECOVERY_FAIL_CLOSED", now);
            }
        }, cancellationToken);
        return new(true, "OPERATION_RECOVERY_FAIL_CLOSED");
    }

    // ---- Housekeeping (ADR-0024 "Cleanup"): shared by the Quartz job and the startup pass. ----
    // Every method here is bounded (Take(batchSize)) and keyset-ordered so a permanently stuck
    // old row can never starve later healthy rows in the same or a later sweep.

    /// <summary>PENDING sessions whose 15-minute deadline has already passed. Immediate logical
    /// invalidity: no callback can ever claim them again once expired.</summary>
    public async Task<int> ExpireDueSessionsAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var dueIds = await _uow.ExecuteAsync(async (db, ct) =>
            await db.Set<MarketplaceAuthorizationSession>().AsNoTracking()
                .Where(x => x.Status == MarketplaceAuthorizationSessionStatus.PENDING && x.ExpiresAt <= now)
                .OrderBy(x => x.ExpiresAt).Take(batchSize).Select(x => x.Id).ToListAsync(ct), cancellationToken);
        var count = 0;
        foreach (var id in dueIds)
        {
            try
            {
                await _uow.ExecuteAsync(async (db, ct) =>
                {
                    var session = await db.Set<MarketplaceAuthorizationSession>().SingleAsync(x => x.Id == id, ct);
                    session.Expire(_clock.UtcNow);
                }, cancellationToken);
                count++;
            }
            catch (InvalidOperationException) { /* already left PENDING concurrently; not this sweep's concern */ }
        }
        return count;
    }

    /// <summary>Sessions CLAIMED more than two minutes ago (ADR: "resolves CLAIMED sessions older
    /// than two minutes through the same operation-row arbiter") and durable operations left
    /// PENDING that long — both mean a crash or abandonment, never a still-running callback
    /// (whose 60-second budget is well below this threshold).</summary>
    public async Task<int> ResolveAbandonedAsync(int batchSize, CancellationToken cancellationToken)
    {
        var threshold = _clock.UtcNow.AddMinutes(-2);
        var operationIds = await _uow.ExecuteAsync(async (db, ct) =>
            await db.Set<MarketplaceAccountOperation>().AsNoTracking()
                .Where(x => x.Decision == MarketplaceAccountOperationDecision.PENDING && x.UpdatedAt <= threshold)
                .OrderBy(x => x.CreatedAt).Take(batchSize).Select(x => x.Id).ToListAsync(ct), cancellationToken);
        var resolved = 0;
        foreach (var id in operationIds)
        {
            try { await ResolvePendingAsync(id, cancellationToken); resolved++; }
            catch (KeyNotFoundException) { }
        }

        // A CLAIMED session whose pre-call operation row never got committed (crash between
        // TryClaim and the operation insert) has no operation to lock — a direct conditional
        // session transition to FAILED is sufficient, per ADR §"Cleanup".
        var orphanSessionIds = await _uow.ExecuteAsync(async (db, ct) =>
            await db.Set<MarketplaceAuthorizationSession>().AsNoTracking()
                .Where(x => x.Status == MarketplaceAuthorizationSessionStatus.CLAIMED && x.ClaimedAt <= threshold)
                .Where(x => !db.Set<MarketplaceAccountOperation>().Any(o => o.AuthorizationSessionId == x.Id))
                .OrderBy(x => x.ClaimedAt).Take(batchSize).Select(x => x.Id).ToListAsync(ct), cancellationToken);
        foreach (var id in orphanSessionIds)
        {
            try
            {
                await _uow.ExecuteAsync(async (db, ct) =>
                {
                    var session = await db.Set<MarketplaceAuthorizationSession>().SingleAsync(x => x.Id == id, ct);
                    if (session.Status == MarketplaceAuthorizationSessionStatus.CLAIMED) session.Fail("ABANDONED_BEFORE_OPERATION", _clock.UtcNow);
                }, cancellationToken);
                resolved++;
            }
            catch (InvalidOperationException) { }
        }
        return resolved;
    }

    /// <summary>Physically removes candidate material for terminal operations, and marks terminal
    /// sessions/operations whose cleanup is due as swept. Never touches a current CONNECTED
    /// reference/version: only a FAIL_CLOSED operation's own staged/candidate material, or a
    /// CONFIRMED DISCONNECT's previously-confirmed reference, is ever deleted here.</summary>
    public async Task<int> CleanupTerminalAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var dueOperations = await _uow.ExecuteAsync(async (db, ct) =>
            await db.Set<MarketplaceAccountOperation>().AsNoTracking()
                .Where(x => x.CleanupState == MarketplaceAccountOperationCleanupState.PENDING && x.NextCleanupAt != null && x.NextCleanupAt <= now)
                .OrderBy(x => x.NextCleanupAt).ThenBy(x => x.CreatedAt).Take(batchSize)
                .Select(x => new { x.Id, x.Decision, x.CandidateCredentialReference, x.CandidateCredentialVersion, x.PreviousCredentialReference, x.PreviousConfirmedCredentialVersion, x.Kind, x.CleanupAttemptCount })
                .ToListAsync(ct), cancellationToken);

        var swept = 0;
        foreach (var op in dueOperations)
        {
            try
            {
                // FAIL_CLOSED: remove its own never-executable candidate, if one was persisted.
                if (op.Decision == MarketplaceAccountOperationDecision.FAIL_CLOSED && op.CandidateCredentialReference is { } candidateRef && op.CandidateCredentialVersion is { } candidateVersion)
                {
                    var result = await _store.DeleteCandidateAsync(op.Id, candidateRef, candidateVersion, cancellationToken);
                    if (result.Outcome is not (ProtectedCredentialStoreOutcome.SUCCESS or ProtectedCredentialStoreOutcome.NOT_FOUND))
                    { await AdvanceCleanupRetryAsync(op.Id, cancellationToken); continue; }
                }
                // CONFIRMED DISCONNECT: only now may the previously-confirmed reference be deleted.
                if (op.Kind == MarketplaceAccountOperationKind.DISCONNECT && op.Decision == MarketplaceAccountOperationDecision.CONFIRMED && op.PreviousCredentialReference is { } previousRef && op.PreviousConfirmedCredentialVersion is { } previousVersion)
                {
                    var result = await _store.DeleteCandidateAsync(op.Id, previousRef, previousVersion, cancellationToken);
                    if (result.Outcome is not (ProtectedCredentialStoreOutcome.SUCCESS or ProtectedCredentialStoreOutcome.NOT_FOUND))
                    { await AdvanceCleanupRetryAsync(op.Id, cancellationToken); continue; }
                }
                await _uow.ExecuteAsync(async (db, ct) =>
                {
                    var operation = await db.Set<MarketplaceAccountOperation>().SingleAsync(x => x.Id == op.Id, ct);
                    if (operation.CleanupState == MarketplaceAccountOperationCleanupState.PENDING) operation.CompleteCleanup(_clock.UtcNow);
                }, cancellationToken);
                swept++;
            }
            catch { await AdvanceCleanupRetryAsync(op.Id, cancellationToken); }
        }

        var dueSessions = await _uow.ExecuteAsync(async (db, ct) =>
            await db.Set<MarketplaceAuthorizationSession>().AsNoTracking()
                .Where(x => x.NextCleanupAt != null && x.NextCleanupAt <= now && x.Status != MarketplaceAuthorizationSessionStatus.PENDING && x.Status != MarketplaceAuthorizationSessionStatus.CLAIMED)
                .OrderBy(x => x.NextCleanupAt).ThenBy(x => x.CreatedAt).Take(batchSize).Select(x => x.Id).ToListAsync(ct), cancellationToken);
        foreach (var id in dueSessions)
        {
            await _uow.ExecuteAsync(async (db, ct) =>
            {
                var session = await db.Set<MarketplaceAuthorizationSession>().SingleAsync(x => x.Id == id, ct);
                // A terminal session's own retention is release of its NextCleanupAt marker; the
                // durable operation row (not the session) is what governs credential deletion.
                if (session.NextCleanupAt is not null) session.MarkSwept(_clock.UtcNow);
            }, cancellationToken);
            swept++;
        }
        return swept;
    }

    /// <summary>
    /// ADR-0024 G-02: "At startup, before enabling provider execution, validate each CONNECTED
    /// account's current reference/version and decryptability in bounded batches. Mark confirmed
    /// missing/corrupt/key-lost accounts REAUTHORIZATION_REQUIRED/runtime UNKNOWN independently
    /// with safe audit." Also run periodically (not only at startup) so a key ring or credential
    /// root lost while the host stays up is self-detected rather than only surfacing on the next
    /// probe/refresh attempt. Never reads/logs/copies the actual secret bytes.
    /// </summary>
    public async Task<int> ValidateConnectedCredentialsAsync(int batchSize, CancellationToken cancellationToken)
    {
        var candidates = await _uow.ExecuteAsync(async (db, ct) =>
            await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection)
                .Where(x => x.Connection.AuthorizationState == MarketplaceAuthorizationState.CONNECTED)
                .OrderBy(x => x.Connection.UpdatedAt).Take(batchSize)
                .Select(x => new { x.Id, x.ProviderCode, x.CredentialReference, x.Connection.ConfirmedCredentialVersion, x.Connection.ConfirmedOperationId })
                .ToListAsync(ct), cancellationToken);

        var invalidated = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.CredentialReference is null || candidate.ConfirmedCredentialVersion is not { } version || candidate.ConfirmedOperationId is not { } operationId)
                continue; // structurally inconsistent CONNECTED row without a full pointer — nothing to validate against
            var read = await _store.UseConfirmedAsync(new ProtectedCredentialRead(candidate.Id, candidate.ProviderCode, candidate.CredentialReference, version, operationId), cancellationToken);
            if (read.Outcome == ProtectedCredentialStoreOutcome.SUCCESS) continue;
            if (read.Outcome == ProtectedCredentialStoreOutcome.STORE_UNAVAILABLE) continue; // transient — does not assert permanent loss

            try
            {
                await _uow.ExecuteAsync(async (db, ct) =>
                {
                    var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == candidate.Id, ct);
                    if (account.Connection.AuthorizationState == MarketplaceAuthorizationState.CONNECTED)
                        account.RequireReauthorization("STARTUP_CREDENTIAL_VALIDATION_FAILED", _clock.UtcNow);
                }, cancellationToken);
                invalidated++;
            }
            catch (DbUpdateConcurrencyException) { /* account changed concurrently; a later sweep re-checks it */ }
        }
        return invalidated;
    }

    /// <summary>Bounded exponential backoff (1, 2, 4... minutes, capped at one hour) so one
    /// permanently-failing deletion can never block later healthy due rows from progressing.</summary>
    private async Task AdvanceCleanupRetryAsync(Guid operationId, CancellationToken cancellationToken) =>
        await _uow.ExecuteAsync(async (db, ct) =>
        {
            var operation = await db.Set<MarketplaceAccountOperation>().SingleOrDefaultAsync(x => x.Id == operationId, ct);
            if (operation is null || operation.CleanupState != MarketplaceAccountOperationCleanupState.PENDING) return;
            var delayMinutes = Math.Min(60, (int)Math.Pow(2, Math.Min(6, operation.CleanupAttemptCount)));
            operation.ScheduleCleanupRetry(_clock.UtcNow.AddMinutes(delayMinutes), _clock.UtcNow);
        }, cancellationToken);

    /// <summary>ADR-0024 §2: "the user must still exist, be active and have
    /// commerce:accounts:manage" — rechecked at claim AND at final commit, not only at Begin.
    /// The permission is granted to the Owner role only (matches the existing
    /// <c>Permissions.CommerceAccountsManage</c> policy at the API layer); this workflow cannot
    /// reference that Api-layer constant (Infrastructure must not depend on Api), so the literal
    /// role name is the same one <see cref="Verce.Platform.Identity.Roles.Owner"/> defines.</summary>
    private static async Task<bool> HasCommerceAccountsManagePermissionAsync(VerceDbContext db, Guid userId, CancellationToken cancellationToken) =>
        await (from userRole in db.UserRoles
               join role in db.Roles on userRole.RoleId equals role.Id
               where userRole.UserId == userId && role.Name == Roles.Owner
               select 1).AnyAsync(cancellationToken);

    private static string RandomValue() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string NewCredentialReference() => RandomValue();
}
