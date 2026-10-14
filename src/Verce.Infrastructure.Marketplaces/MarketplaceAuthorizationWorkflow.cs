using System.Collections.Concurrent;
using System.Runtime.InteropServices;
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
    private readonly MarketplaceCredentialExecutor _credentialExecutor;
    private readonly Func<TimeSpan, CancellationTokenSource> _cleanupBudgetFactory;

    // G-03: "a keyed async single-flight by immutable account ID is shared by refresh, reconnect
    // installation, generation replacement and disconnect". This process-wide dictionary is that
    // single-flight; it is intentionally process-local (S8C.1 assumes one application instance).
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> AccountLocks = new();

    /// <summary>A-01: every terminalization/cleanup budget below is created through this ONE seam
    /// instead of calling <c>new CancellationTokenSource(...)</c> inline — production behavior is
    /// byte-for-byte identical (the default factory below), but a test can substitute a recording
    /// factory to prove WHEN each fresh budget is actually constructed (e.g. strictly after an
    /// external provider flight completes, never hoisted to flight-start) without needing a broad
    /// new time-abstraction or any production DI change: this parameter defaults to null for every
    /// existing caller (DI-resolved or otherwise) and only a test that constructs this class
    /// directly ever supplies a different one.</summary>
    public MarketplaceAuthorizationWorkflow(IUnitOfWork uow, IMarketplaceConnectorRegistry registry, IProtectedCredentialStore store, ProviderExecutionGate gate, IClock clock, MarketplaceCredentialExecutor credentialExecutor, Func<TimeSpan, CancellationTokenSource>? cleanupBudgetFactory = null)
    { _uow = uow; _registry = registry; _store = store; _gate = gate; _clock = clock; _credentialExecutor = credentialExecutor; _cleanupBudgetFactory = cleanupBudgetFactory ?? (budget => new CancellationTokenSource(budget)); }

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
    ///
    /// D-02 (ADR-0025 §F.2/§F.3, Astra G01): send certainty is never inferred from an exception
    /// subtype. <see cref="MarketplaceRequestNotSentException"/> is the ONLY proof the provider
    /// call never went out; its safe-code message distinguishes a routine pre-send guard (preserve
    /// CONNECTED, mark UNAVAILABLE only, no replay) from unrenewable material
    /// (REFRESH_TOKEN_UNAVAILABLE — REAUTHORIZATION_REQUIRED, nothing was ever sent to lose). Every
    /// other exception, of any type, is SENT_OR_UNKNOWN and is never replayed: a
    /// <see cref="MarketplaceProviderCallException"/> maps through its own safe code, anything else
    /// becomes REFRESH_OUTCOME_UNKNOWN. Once the connector call begins, all terminalization uses a
    /// bounded cleanup token independent of the caller's, so caller cancellation can never turn a
    /// real attempt back into NOT_SENT; if that bounded window itself cannot commit a terminal
    /// decision, the operation is deliberately left PENDING for the operation-row arbiter/housekeeping.
    /// </summary>
    public async Task<MarketplaceWorkflowResult> RefreshAsync(Guid accountId, bool force = false, Guid? rejectedConfirmedOperationId = null, CancellationToken cancellationToken = default)
    {
        var providerCode = await _uow.ExecuteAsync(async (db, ct) =>
            (await db.Set<MarketplaceAccount>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == accountId, ct))?.ProviderCode, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (!_registry.TryGet(providerCode, out var connector)) return new(false, "MARKETPLACE_PROVIDER_NOT_SUPPORTED");

        await using var providerLease = await _gate.EnterSharedAsync(providerCode, cancellationToken);
        var accountLock = AccountLocks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await accountLock.WaitAsync(cancellationToken);
        // A-02: R1 (the previous confirmed secret, read below) and R2 (the provider's replacement,
        // received after the call) are both copied into buffers this method owns exclusively, so
        // they can be positively zeroed on EVERY exit path — success, CAS-fail-then-success,
        // persistent CAS failure, exception, cancellation, terminal fail-closed — never only some.
        byte[]? r1Buffer = null;
        byte[]? r2Buffer = null;
        try
        {
            var now = _clock.UtcNow;
            RefreshClaim? claim = null;
            await _uow.ExecuteAsync(async (db, ct) =>
            {
                var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId, ct);
                if (account.Connection.AuthorizationState != MarketplaceAuthorizationState.CONNECTED || account.CredentialReference is null || account.Connection.ConfirmedCredentialVersion is not { } confirmedVersion || account.Connection.ConfirmedOperationId is not { } confirmedOperationId)
                    return; // not usable: nothing to refresh, waiters simply see current state
                // Waiters that queued behind the lock re-evaluate expiry here instead of refreshing
                // again. `force` (after an observed 401) bypasses the freshness check ONLY when it
                // still targets the exact confirmed operation whose token was rejected — a
                // concurrent refresh that already replaced it must never be forced again.
                var forcingThisOperation = force && confirmedOperationId == rejectedConfirmedOperationId;
                if (!forcingThisOperation && account.Connection.AccessExpiresAt is { } expiresAt && expiresAt > now) return;
                var operation = new MarketplaceAccountOperation(MarketplaceAccountOperationKind.REFRESH, account.ProviderCode, account.Id, null, account.Version, account.CredentialReference, confirmedVersion, now);
                operation.MarkExternalInFlight(now);
                db.Add(operation);
                claim = new RefreshClaim(operation.Id, account.CredentialReference, confirmedVersion, confirmedOperationId, account.ProviderCode);
            }, cancellationToken);

            if (claim is null) return new(true, "REFRESH_NOT_REQUIRED", accountId);

            // A-01: this credential/store read happens strictly BEFORE any provider bytes could be
            // sent. It is therefore its OWN classified pre-invocation path: ANY failure here —
            // including the caller's own cancellation firing mid-read — is proven NOT_SENT and must
            // terminalize accordingly, never be left to propagate and strand the operation PENDING.
            // Terminalization always uses a FRESH bounded token of its own: if the caller's token is
            // what caused this to fail, reusing it here would make the terminalizing write itself
            // fail immediately, which is exactly the "stuck PENDING" defect being fixed.
            ProtectedCredentialReadResult read;
            try
            {
                read = await _store.UseConfirmedAsync(new ProtectedCredentialRead(accountId, claim.ProviderCode, claim.Reference, claim.PreviousVersion, claim.PreviousOperationId), cancellationToken);
            }
            catch (Exception)
            {
                using var preSendCleanup = _cleanupBudgetFactory(TimeSpan.FromSeconds(10));
                await FailRefreshNotSentAsync(claim.OperationId, accountId, "REFRESH_NOT_SENT", preSendCleanup.Token);
                return new(false, "REFRESH_NOT_SENT", accountId);
            }
            if (read.Outcome != ProtectedCredentialStoreOutcome.SUCCESS)
            {
                using var preSendCleanup = _cleanupBudgetFactory(TimeSpan.FromSeconds(10));
                if (read.Outcome == ProtectedCredentialStoreOutcome.STORE_UNAVAILABLE)
                {
                    // G-04: "Transient STORE_UNAVAILABLE blocks use ... it does not assert permanent
                    // loss." Nothing was sent; authorization is fully preserved, not force-reauthorized.
                    await FailRefreshNotSentAsync(claim.OperationId, accountId, "REFRESH_NOT_SENT", preSendCleanup.Token);
                    return new(false, "REFRESH_NOT_SENT", accountId);
                }
                // NOT_FOUND/CORRUPTED_OR_UNDECRYPTABLE/VERSION_CONFLICT: confirmed-loss-shaped —
                // "Material with no refresh token or a missing/unparseable secret" (ADR-0025 §F.3).
                await ResolvePendingAsync(claim.OperationId, "REFRESH_TOKEN_UNAVAILABLE", preSendCleanup.Token);
                return new(false, "REAUTHORIZATION_REQUIRED", accountId);
            }
            // A-02: recover the CALLER's own already-exclusive array instead of allocating yet
            // another copy whenever the shape allows it (see RecoverOwnedArrayOrCopy) — one fewer
            // transient plaintext buffer this method would otherwise never get a chance to zero.
            r1Buffer = RecoverOwnedArrayOrCopy(read.SecretMaterial);

            // From here a connector call may actually be sent, using the CALLER's own token (so the
            // caller can genuinely cancel a slow network call) — but every terminalization branch
            // below creates its OWN fresh, independent bounded token at the moment IT begins, never
            // sharing a budget with however long the network call itself took (A-01).
            MarketplaceRefreshCompletion completion;
            try
            {
                completion = await connector.RefreshAsync(new MarketplaceRefreshRequest(accountId, claim.ProviderCode, r1Buffer, force), cancellationToken);
            }
            catch (MarketplaceRequestNotSentException ex)
            {
                using var cleanup = _cleanupBudgetFactory(TimeSpan.FromSeconds(10));
                if (ex.Message == "REFRESH_TOKEN_UNAVAILABLE")
                {
                    // Proven NOT_SENT, but the material itself is unrenewable (no refresh token in
                    // the decrypted secret): authorization cannot be preserved — there is nothing to
                    // renew — even though nothing was sent.
                    await ResolvePendingAsync(claim.OperationId, "REFRESH_TOKEN_UNAVAILABLE", cleanup.Token);
                    return new(false, "REAUTHORIZATION_REQUIRED", accountId);
                }
                // Every other proven-NOT_SENT case (local validation/serialization/pre-send
                // cancellation): the provider was never contacted, so prior authorization is fully
                // preserved (CONNECTED) — only runtime moves to UNAVAILABLE. Never RequireReauthorization.
                await FailRefreshNotSentAsync(claim.OperationId, accountId, ex.Message, cleanup.Token);
                return new(false, ex.Message, accountId);
            }
            catch (MarketplaceProviderCallException ex)
            {
                using var cleanup = _cleanupBudgetFactory(TimeSpan.FromSeconds(10));
                var code = MapRefreshFailureCode(ex.Failure);
                if (ex.Failure.Classification == MarketplaceFailureClassification.RATE_LIMITED && ex.Failure.RetryAfterUntil is { } retryAfter)
                    await RecordRefreshRetryAfterAsync(accountId, retryAfter, cleanup.Token);
                await ResolvePendingAsync(claim.OperationId, code, cleanup.Token);
                return new(false, "REAUTHORIZATION_REQUIRED", accountId);
            }
            catch
            {
                // SENT_OR_UNKNOWN by default (G-08/G01): an error classification alone can never
                // establish NOT_SENT. Never a blind replay.
                using var cleanup = _cleanupBudgetFactory(TimeSpan.FromSeconds(10));
                await ResolvePendingAsync(claim.OperationId, "REFRESH_OUTCOME_UNKNOWN", cleanup.Token);
                return new(false, "REAUTHORIZATION_REQUIRED", accountId);
            }

            // Terminalization (success path) begins NOW — its own fresh bounded token, never the
            // one (if any) that was ticking during the network call above.
            using var terminal = _cleanupBudgetFactory(TimeSpan.FromSeconds(10));
            var terminalToken = terminal.Token;
            // A-02: MercadoLivreConnector.BuildSecretJson returns a whole-array ReadOnlyMemory (a
            // MemoryStream.ToArray() result, never a slice) — recovering that EXACT array instance
            // (instead of `.ToArray()`-ing a fresh copy of it) means the byte[] this method zeroes
            // in `finally` IS the adapter's own buffer, not a separate copy that leaves the
            // adapter-side array as a leftover, un-zeroed plaintext secret in the managed heap.
            r2Buffer = RecoverOwnedArrayOrCopy(completion.SecretMaterial);
            var write = new ProtectedCredentialWrite(claim.OperationId, null, accountId, claim.ProviderCode, claim.Reference, r2Buffer);
            var cas = await _store.CompareAndSwapReplaceAsync(write, claim.PreviousVersion, terminalToken);
            if (cas.Outcome != ProtectedCredentialStoreOutcome.SUCCESS || cas.Receipt is null)
            {
                // "Never a second POST": exactly one additional persistence attempt of the IDENTICAL
                // R2 bytes under the same operation ID — never a repeated provider call.
                cas = await _store.CompareAndSwapReplaceAsync(write, claim.PreviousVersion, terminalToken);
                if (cas.Outcome == ProtectedCredentialStoreOutcome.VERSION_CONFLICT)
                {
                    // The first attempt may actually have succeeded server-side and only its
                    // acknowledgement was lost. A matching confirmed read at V+1 under this exact
                    // operation ID is treated as an equivalent receipt (row 18); anything else falls
                    // through to REFRESH_PERSISTENCE_FAILED.
                    var confirmedCheck = await _store.UseConfirmedAsync(new ProtectedCredentialRead(accountId, claim.ProviderCode, claim.Reference, claim.PreviousVersion + 1, claim.OperationId), terminalToken);
                    if (confirmedCheck.Outcome == ProtectedCredentialStoreOutcome.SUCCESS && confirmedCheck.Receipt is { } receipt && receipt.CredentialVersion == claim.PreviousVersion + 1)
                        cas = new ProtectedCredentialStoreResult(ProtectedCredentialStoreOutcome.SUCCESS, receipt);
                }
                if (cas.Outcome != ProtectedCredentialStoreOutcome.SUCCESS || cas.Receipt is null)
                {
                    await ResolvePendingAsync(claim.OperationId, "REFRESH_PERSISTENCE_FAILED", terminalToken);
                    return new(false, "REAUTHORIZATION_REQUIRED", accountId);
                }
            }

            try
            {
                await _uow.ExecuteAsync(async (db, ct) =>
                {
                    var operation = await db.Set<MarketplaceAccountOperation>().FromSqlInterpolated($"SELECT * FROM commerce.marketplace_account_operation WHERE id = {claim.OperationId} FOR UPDATE").SingleAsync(ct);
                    if (operation.Decision != MarketplaceAccountOperationDecision.PENDING) throw new InvalidOperationException("MARKETPLACE_OPERATION_TERMINAL");
                    var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId, ct);
                    operation.RecordPersistedCandidate(claim.Reference, cas.Receipt.CredentialVersion, _clock.UtcNow);
                    account.InstallConfirmedCredential(claim.Reference, cas.Receipt.CredentialVersion, operation.Id, _clock.UtcNow, completion.AccessExpiresAt, isRefresh: true);
                    operation.Confirm("REFRESHED", _clock.UtcNow);
                }, terminalToken);
                return new(true, "REFRESHED", accountId);
            }
            catch (DbUpdateException)
            {
                await ResolvePendingAsync(claim.OperationId, "REFRESH_OUTCOME_UNKNOWN", terminalToken);
                return new(false, "REAUTHORIZATION_REQUIRED", accountId);
            }
        }
        finally
        {
            accountLock.Release();
            // A-02: zeroed only after the FINAL persistence/receipt decision on every exit path
            // above — never before a permitted same-R2 retry has had its chance to complete.
            if (r1Buffer is not null) CryptographicOperations.ZeroMemory(r1Buffer);
            if (r2Buffer is not null) CryptographicOperations.ZeroMemory(r2Buffer);
        }
    }

    /// <summary>D-02, F.3 rows 1-3: a proven pre-send failure preserves current authorization
    /// exactly — CONNECTED is never disturbed, only runtime moves to UNAVAILABLE — and the
    /// operation is FAIL_CLOSED with the connector's own safe code. Never a blind replay, never
    /// reauthorization for a request that never reached the provider.</summary>
    private async Task FailRefreshNotSentAsync(Guid operationId, Guid accountId, string safeCode, CancellationToken cancellationToken) =>
        await _uow.ExecuteAsync(async (db, ct) =>
        {
            var operation = await db.Set<MarketplaceAccountOperation>().FromSqlInterpolated($"SELECT * FROM commerce.marketplace_account_operation WHERE id = {operationId} FOR UPDATE").SingleAsync(ct);
            if (operation.Decision != MarketplaceAccountOperationDecision.PENDING) return;
            operation.FailClosed(safeCode, _clock.UtcNow);
            operation.RequestCleanup(_clock.UtcNow);
            var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId, ct);
            account.RecordRuntimeFailure("UNKNOWN", safeCode, _clock.UtcNow);
        }, cancellationToken);

    /// <summary>F.3 row 15: Retry-After is diagnostic only for a rejected refresh (the account still
    /// goes REAUTHORIZATION_REQUIRED) but is still persisted so a later probe/business attempt can
    /// honor it (D-03).</summary>
    private async Task RecordRefreshRetryAfterAsync(Guid accountId, DateTimeOffset until, CancellationToken cancellationToken) =>
        await _uow.ExecuteAsync(async (db, ct) =>
        {
            var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId, ct);
            account.RecordRetryAfter(until, _clock.UtcNow);
        }, cancellationToken);

    /// <summary>ADR-0025 §F.3: maps a connector's normalized provider failure to the refresh-specific
    /// safe result code. Any classification/safe-code this map does not specifically recognize
    /// conservatively becomes REFRESH_OUTCOME_UNKNOWN — never assumed safe to replay.</summary>
    private static string MapRefreshFailureCode(MarketplaceProviderFailure failure) => failure.SafeCode switch
    {
        "PROVIDER_APP_CREDENTIALS_INVALID" => "PROVIDER_APP_CREDENTIALS_INVALID",
        "PROVIDER_GRANT_REJECTED" => "REFRESH_REJECTED",
        "PROVIDER_REQUEST_REJECTED" => "REFRESH_REJECTED",
        "PROVIDER_RATE_LIMITED" => "REFRESH_RATE_LIMITED",
        _ => "REFRESH_OUTCOME_UNKNOWN",
    };

    /// <summary>A-02: when <paramref name="material"/> IS a directly-owned, whole array (the shape
    /// every current caller actually produces — <c>MercadoLivreConnector.BuildSecretJson</c>'s own
    /// <c>MemoryStream.ToArray()</c> result, and <c>LocalProtectedCredentialStore</c>'s own
    /// <c>Convert.FromBase64String</c> result — both freshly allocated, exclusively owned, never a
    /// slice of anything larger), this recovers that SAME array instance so the caller zeroes the
    /// ACTUAL buffer the adapter/store handed over, never a separate copy that leaves the
    /// original as an un-zeroed, un-tracked plaintext secret sitting in the managed heap. Only when
    /// the memory is a slice — offset &gt; 0, or shorter than its backing array — does this fall
    /// back to an explicit, independently zeroable copy, so this is safe for any future connector
    /// that returns a slice instead.</summary>
    private static byte[] RecoverOwnedArrayOrCopy(ReadOnlyMemory<byte> material) =>
        MemoryMarshal.TryGetArray(material, out var segment) && segment.Offset == 0 && segment.Array is { } array && segment.Count == array.Length
            ? array
            : material.ToArray();

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
            return new ProbeSnapshot(account.Version, account.Active, channelActive, account.ProviderCode, account.ExternalAccountId, account.CredentialReference, account.Connection.AuthorizationState, account.Connection.ConfirmedCredentialVersion, account.Connection.ConfirmedOperationId, account.Connection.RuntimeRetryAfterUntil);
        }, cancellationToken) ?? throw new KeyNotFoundException();

        if (snapshot.Version != expectedVersion) throw new InvalidOperationException("CONCURRENCY_CONFLICT");
        // D-03: a persisted provider Retry-After/backoff guard is honored even by the Owner's
        // explicit probe — ADR-0024 §5 "Probe honors persisted RuntimeRetryAfterUntil; it does not
        // bypass 429, including after restart." An expired guard (now >= until) permits the probe.
        if (snapshot.RuntimeRetryAfterUntil is { } retryAfterUntil && retryAfterUntil > now)
            return new(false, "PROBE_RATE_LIMITED", accountId);
        if (!snapshot.Active || !snapshot.ChannelActive || snapshot.AuthorizationState != MarketplaceAuthorizationState.CONNECTED
            || snapshot.CredentialReference is null || snapshot.ConfirmedCredentialVersion is null || snapshot.ConfirmedOperationId is null)
            return new(false, "MARKETPLACE_AUTHORIZATION_NOT_USABLE", accountId);
        if (!_registry.TryGet(snapshot.ProviderCode, out var connector)) return new(false, "MARKETPLACE_PROVIDER_NOT_SUPPORTED", accountId);

        try
        {
            // A-05: routed through the shared credential executor (guards + refresh-margin
            // coordination + secret provisioning). Probe supplies no requiredCapabilityCode — it
            // needs no unrelated business capability (ADR-0024 §5) — and keeps ALL of its own
            // grant-writing/runtime-promotion authority entirely on this side of the call.
            var inspection = await _credentialExecutor.ExecuteAsync(accountId, MarketplaceExecutionRequirement.Probe,
                (context, ct) => connector.InspectAsync(new MarketplaceAccountInspectionRequest(context.AccountId, snapshot.ProviderCode, context.Secret), ct),
                cancellationToken);

            // A-04: the connector's own InspectAsync already proved "token user_id == /users/me.id"
            // (and site MLB). The ONE comparison only the workflow can make — because the connector
            // never receives it — is that this returned identity also matches what THIS VERCE
            // account itself claims to be. Without this check, a credential that is internally
            // self-consistent but was stored under the WRONG account row would silently pass.
            if (!string.Equals(inspection.ExternalAccountId, snapshot.ExternalAccountId, StringComparison.Ordinal))
            {
                await _uow.ExecuteAsync(async (db, ct) =>
                {
                    var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId, ct);
                    account.RequireReauthorization("PROBE_IDENTITY_MISMATCH", _clock.UtcNow);
                }, cancellationToken);
                return new(false, "PROBE_IDENTITY_MISMATCH", accountId);
            }

            await _uow.ExecuteAsync(async (db, ct) =>
            {
                var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).Include(x => x.Capabilities).SingleAsync(x => x.Id == accountId, ct);
                // D-03: grant source must be PROBE_INSPECTION, not the previous generic "PROBE".
                foreach (var grant in inspection.Grants) account.SetCapability(grant.Key, grant.Value, "PROBE_INSPECTION", null, _clock.UtcNow);
                account.MarkAvailable(_clock.UtcNow);
            }, cancellationToken);
            return new(true, "PROBE_AVAILABLE", accountId);
        }
        catch (MarketplaceExecutionBlockedException ex) when (ex.Reason == "RATE_LIMITED")
        {
            return new(false, "PROBE_RATE_LIMITED", accountId);
        }
        catch (MarketplaceExecutionBlockedException ex) when (ex.Reason == "CREDENTIAL_CONFIRMED_LOST")
        {
            // ADR-0024 §5/RT-03: a CONFIRMED loss (not merely transiently unreachable) routes to
            // REAUTHORIZATION_REQUIRED — CommerceEndpoints.RequiredAction only ever surfaces
            // "REAUTHORIZE_AFTER_STORE_LOSS" when SafeFailureCode="PROBE_CREDENTIAL_UNREADABLE" is
            // paired with AuthorizationState=REAUTHORIZATION_REQUIRED (CONNECTED+UNAVAILABLE always
            // takes priority as "CHECK_PROVIDER_ACCOUNT" regardless of SafeFailureCode).
            await _uow.ExecuteAsync(async (db, ct) =>
            {
                var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId, ct);
                account.RequireReauthorization("PROBE_CREDENTIAL_UNREADABLE", _clock.UtcNow);
            }, cancellationToken);
            return new(false, "PROBE_CREDENTIAL_UNREADABLE", accountId);
        }
        catch (MarketplaceExecutionBlockedException ex) when (ex.Reason == "CREDENTIAL_STORE_UNAVAILABLE")
        {
            // Genuinely transient — NOT a confirmed loss — keeps the milder runtime-failure treatment.
            await MarkUnavailableAsync(accountId, "CREDENTIAL_UNREADABLE", "PROBE_CREDENTIAL_UNREADABLE", cancellationToken);
            return new(false, "PROBE_CREDENTIAL_UNREADABLE", accountId);
        }
        catch (MarketplaceExecutionBlockedException)
        {
            // A race between this method's own pre-check above and the executor's fresh re-check
            // (account/channel/authorization changed concurrently) — never silently proceed.
            return new(false, "MARKETPLACE_AUTHORIZATION_NOT_USABLE", accountId);
        }
        // ADR-0025 §A.9: a proven identity mismatch during probe (the confirmed token now resolves
        // to a DIFFERENT provider identity than the one embedded at issuance) is not a transient
        // runtime failure — it must force reauthorization, never merely CHECK_PROVIDER_ACCOUNT.
        catch (MarketplaceProviderCallException ex) when (ex.Failure.SafeCode == "PROBE_IDENTITY_MISMATCH")
        {
            await _uow.ExecuteAsync(async (db, ct) =>
            {
                var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId, ct);
                account.RequireReauthorization("PROBE_IDENTITY_MISMATCH", _clock.UtcNow);
            }, cancellationToken);
            return new(false, "PROBE_IDENTITY_MISMATCH", accountId);
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

    private sealed record ProbeSnapshot(long Version, bool Active, bool ChannelActive, string ProviderCode, string ExternalAccountId, string? CredentialReference, MarketplaceAuthorizationState AuthorizationState, long? ConfirmedCredentialVersion, Guid? ConfirmedOperationId, DateTimeOffset? RuntimeRetryAfterUntil);

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
                await ResolvePendingAsync(claim.OperationId, cancellationToken: cancellationToken);
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
                await ResolvePendingAsync(claim.OperationId, cancellationToken: cancellationToken);
                return new(false, "CREDENTIAL_PERSISTENCE_FAILED");
            }
            var bound = await _store.BindCandidateAsync(new ProtectedCredentialBinding(claim.OperationId, claim.SessionId, candidateAccountId, input.ProviderCode, candidateReference), cancellationToken);
            if (bound.Outcome != ProtectedCredentialStoreOutcome.SUCCESS)
            {
                await ResolvePendingAsync(claim.OperationId, cancellationToken: cancellationToken);
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
                await ResolvePendingAsync(claim.OperationId, cancellationToken: cancellationToken);
                await _store.DeleteCandidateAsync(claim.OperationId, candidateReference, staged.Receipt.CredentialVersion, cancellationToken);
                return new(false, "OPERATION_RECOVERY_FAIL_CLOSED");
            }
            var promoted = await _store.PromoteCandidateAsync(claim.OperationId, candidateReference, staged.Receipt.CredentialVersion, cancellationToken);
            if (promoted.Outcome != ProtectedCredentialStoreOutcome.SUCCESS)
            {
                await ResolvePendingAsync(claim.OperationId, cancellationToken: cancellationToken);
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
                await ResolvePendingAsync(claim.OperationId, cancellationToken: cancellationToken);
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
                // D-01: persist the provider-declared access-token expiry on the initial
                // connect/reconnect confirmation too (not just refresh).
                account.InstallConfirmedCredential(reference, version, operation.Id, now, completion.AccessExpiresAt);
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
            await ResolvePendingAsync(claim.OperationId, cancellationToken: cancellationToken);
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

    /// <summary>D-02: <paramref name="safeCode"/> is the terminal fail-closed/reauthorization code
    /// recorded on the operation and (when an account is bound) on the account itself — defaults to
    /// the pre-existing generic recovery code so callback/startup/cleanup resolution paths that
    /// never pass a specific code keep their exact previous behavior.</summary>
    public async Task<MarketplaceWorkflowResult> ResolvePendingAsync(Guid operationId, string safeCode = "OPERATION_RECOVERY_FAIL_CLOSED", CancellationToken cancellationToken = default)
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
                    // D-01: a receipt-only refresh recovery has no way to know the provider's
                    // original expires_in — leave it null so the very next use forces a fresh
                    // refresh rather than assuming/guessing a value.
                    refreshAccount.InstallConfirmedCredential(candidateReference, candidateVersion, operation.Id, now, accessExpiresAt: null, isRefresh: true);
                    operation.Confirm("REFRESHED_FROM_RECEIPT", now);
                    return;
                }
            }

            operation.FailClosed(safeCode, now);
            operation.RequestCleanup(now);
            if (operation.MarketplaceAccountId is { } accountId)
            {
                var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId, ct);
                account.RequireReauthorization(safeCode, now);
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
                if (session.Status == MarketplaceAuthorizationSessionStatus.CLAIMED) session.Fail(safeCode, now);
            }
        }, cancellationToken);
        return new(true, safeCode);
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
            try { await ResolvePendingAsync(id, cancellationToken: cancellationToken); resolved++; }
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
