using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Verce.Infrastructure.Marketplaces.MercadoLivre;
using Verce.Modules.Commerce;
using Verce.Modules.Pricing;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Time;

namespace Verce.Infrastructure.Marketplaces;

/// <summary>ADR-0025 §G.3: distinguishes the Owner's explicit availability/grant probe (which needs
/// no unrelated business capability) from a future business-capability read (which additionally
/// needs structural SUPPORTED and account GRANTED).</summary>
public enum MarketplaceExecutionRequirement { Probe, BusinessCapability }

/// <summary>The short-lived, decrypted material an operation may use exactly once. Never persisted,
/// never returned outside the executor's own callback.</summary>
public sealed record MarketplaceCredentialContext(Guid AccountId, string ExternalAccountId, ReadOnlyMemory<byte> Secret, Guid ConfirmedOperationId);

/// <summary>ADR-0030 §2: the adapter that owns the secret format found the (authentic, confirmed) stored material unusable before any request. Internal, constant message, no inner
/// exception and no content — the executor translates it into the existing <c>CREDENTIAL_CONFIRMED_LOST</c> path; it is never a provider failure.</summary>
public sealed class MarketplaceStoredCredentialUnusableException : Exception
{
    public MarketplaceStoredCredentialUnusableException() : base("STORED_CREDENTIAL_UNUSABLE") { }
}

/// <summary>A guard refused execution before any credential was ever touched. <see cref="Reason"/>
/// is a safe, bounded local catalog code (never a provider message).</summary>
public sealed class MarketplaceExecutionBlockedException : Exception
{
    public string Reason { get; }
    public MarketplaceExecutionBlockedException(string reason) : base(reason) => Reason = reason;
}

/// <summary>
/// A-05 (ADR-0025 §G.3, Stream A8): the single component every credentialed provider operation must
/// go through — today, <see cref="MarketplaceAuthorizationWorkflow.ProbeAsync"/>; in a future
/// Stream B, listing-sync reads keyed by a required capability code. It centralizes: account
/// active; SalesChannel active; structural capability SUPPORTED (business-capability requirement
/// only); account grant GRANTED (business-capability requirement only); authorization usable
/// (CONNECTED + a confirmed credential pointer); runtime not UNAVAILABLE (business-capability
/// requirement only — a probe's whole point is recovering from UNAVAILABLE); no blocking pending
/// credential operation; the persisted Retry-After/backoff guard; and proactive refresh-margin
/// coordination.
///
/// Refresh coordination reuses the EXISTING single-flight
/// <see cref="MarketplaceAuthorizationWorkflow.RefreshAsync"/> — this class never reimplements a
/// refresh decision or issues a competing provider call. It resolves the workflow lazily through
/// <see cref="IServiceProvider"/> rather than taking a constructor dependency on it, because the
/// workflow (for probe) depends on THIS class — a direct two-way constructor dependency would be an
/// unresolvable DI cycle; resolving the already-constructed scoped instance at call time has no
/// such problem.
///
/// This class never grants probe (or any future caller) interactive credential-management
/// authority: it only ever proves a credential is safe to use for the duration of one call and
/// hands over the bytes. Grant-writing/runtime-promotion logic (e.g. probe's own
/// "PROBE_INSPECTION" capability rows and MarkAvailable) stays entirely in the caller.
/// </summary>
public sealed class MarketplaceCredentialExecutor
{
    private readonly IUnitOfWork _uow;
    private readonly IMarketplaceConnectorRegistry _registry;
    private readonly IProtectedCredentialStore _store;
    private readonly ProviderExecutionGate _gate;
    private readonly IClock _clock;
    private readonly IServiceProvider _services;
    private readonly TimeSpan _refreshMargin;
    private readonly MarketplaceProviderInFlightLimiter? _limiter;

    public MarketplaceCredentialExecutor(IUnitOfWork uow, IMarketplaceConnectorRegistry registry, IProtectedCredentialStore store, ProviderExecutionGate gate, IClock clock, IServiceProvider services, IOptions<MercadoLivreOptions> mercadoLivreOptions, MarketplaceProviderInFlightLimiter? limiter = null)
    {
        _limiter = limiter; _uow = uow; _registry = registry; _store = store; _gate = gate; _clock = clock; _services = services;
        _refreshMargin = TimeSpan.FromSeconds(Math.Clamp(mercadoLivreOptions.Value.AccessTokenRefreshMarginSeconds, 0, 3600));
    }

    public async Task<T> ExecuteAsync<T>(Guid accountId, MarketplaceExecutionRequirement requirement, Func<MarketplaceCredentialContext, CancellationToken, Task<T>> send, CancellationToken cancellationToken, string? requiredCapabilityCode = null)
    {
        var now = _clock.UtcNow;
        var snapshot = await LoadSnapshotAsync(accountId, cancellationToken) ?? throw new KeyNotFoundException();

        if (!_registry.TryGet(snapshot.ProviderCode, out _)) throw new MarketplaceExecutionBlockedException("PROVIDER_NOT_CONFIGURED");
        // Handoff B5 / G.3 step 2: the per-provider in-flight slot is taken BEFORE the process-local provider gate and released when this call ends.
        using var inFlightSlot = _limiter is null ? null : await _limiter.AcquireAsync(snapshot.ProviderCode, cancellationToken);
        await using var providerLease = await _gate.EnterSharedAsync(snapshot.ProviderCode, cancellationToken);

        ValidateGuards(snapshot, requirement, requiredCapabilityCode, now);
        if (await HasBlockingOperationAsync(accountId, snapshot.ProviderCode, cancellationToken)) throw new MarketplaceExecutionBlockedException("AUTHORIZATION_NOT_USABLE");

        // Proactive refresh margin (A-05): an unknown or soon-to-expire access token triggers a
        // coordinated refresh through the existing single-flight BEFORE this operation ever reads a
        // secret. Concurrent callers for the same account share the one in-flight refresh via the
        // workflow's own account lock; every waiter reloads the authoritative confirmed state below
        // rather than assuming its own stale snapshot is still current.
        if (snapshot.AccessExpiresAt is null || snapshot.AccessExpiresAt <= now + _refreshMargin)
        {
            var workflow = _services.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            // A-05 bug 2: the token may be inside the configured margin but NOT yet technically
            // expired (AccessExpiresAt > now) — RefreshAsync's own freshness short-circuit would
            // otherwise silently return "REFRESH_NOT_REQUIRED" without ever calling the connector,
            // making the whole margin concept a no-op. `force: true` bypasses that short-circuit,
            // but ONLY for the exact confirmed operation THIS guard-checked snapshot just observed
            // (`rejectedConfirmedOperationId`) — never an unrestricted force any other caller could
            // invoke; a concurrent refresh that already replaced this operation is never forced again.
            var refreshed = await workflow.RefreshAsync(accountId, force: true, rejectedConfirmedOperationId: snapshot.ConfirmedOperationId, cancellationToken: cancellationToken);
            if (!refreshed.Succeeded && refreshed.Code != "REFRESH_NOT_REQUIRED")
                throw new MarketplaceExecutionBlockedException("AUTHORIZATION_NOT_USABLE");
            // Every waiter — whether it triggered the refresh or queued behind the same account
            // single-flight lock — reloads the authoritative confirmed state here rather than
            // trusting its own stale snapshot; this is what makes concurrent margin-triggered
            // callers converge on one provider refresh with no R1 replay.
            snapshot = await LoadSnapshotAsync(accountId, cancellationToken) ?? throw new KeyNotFoundException();
            ValidateAuthorizationUsable(snapshot);
        }

        try { return await SendWithConfirmedCredentialAsync(accountId, snapshot, send, cancellationToken); }
        catch (MarketplaceProviderCallException ex) when (ex.Failure.Classification == MarketplaceFailureClassification.AUTH_RENEWAL_REQUIRED)
        {
            // G.3: a 401 gets exactly one forced refresh and exactly one resend. This remains in
            // the shared executor, so listing sync does not create a competing retry policy.
            var workflow = _services.GetRequiredService<MarketplaceAuthorizationWorkflow>();
            var refreshed = await workflow.RefreshAsync(accountId, force: true, rejectedConfirmedOperationId: snapshot.ConfirmedOperationId, cancellationToken: cancellationToken);
            if (!refreshed.Succeeded) throw new MarketplaceExecutionBlockedException("AUTHORIZATION_NOT_USABLE");
            snapshot = await LoadSnapshotAsync(accountId, cancellationToken) ?? throw new KeyNotFoundException();
            ValidateGuards(snapshot, requirement, requiredCapabilityCode, _clock.UtcNow);
            return await SendWithConfirmedCredentialAsync(accountId, snapshot, send, cancellationToken);
        }
    }

    private async Task<T> SendWithConfirmedCredentialAsync<T>(Guid accountId, ExecutionSnapshot snapshot, Func<MarketplaceCredentialContext, CancellationToken, Task<T>> send, CancellationToken cancellationToken)
    {
        var read = await _store.UseConfirmedAsync(new ProtectedCredentialRead(accountId, snapshot.ProviderCode, snapshot.CredentialReference!, snapshot.ConfirmedCredentialVersion!.Value, snapshot.ConfirmedOperationId!.Value), cancellationToken);
        if (read.Outcome != ProtectedCredentialStoreOutcome.SUCCESS)
            throw new MarketplaceExecutionBlockedException(read.Outcome is ProtectedCredentialStoreOutcome.NOT_FOUND or ProtectedCredentialStoreOutcome.CORRUPTED_OR_UNDECRYPTABLE ? "CREDENTIAL_CONFIRMED_LOST" : "CREDENTIAL_STORE_UNAVAILABLE");
        // ADR-0030 §2: a store SUCCESS proves authenticity and version binding, not usability. When the adapter that owns the format refuses the material the confirmed version is lost
        // for good (deterministic: only a new consent replaces it) — the very path of NOT_FOUND / CORRUPTED_OR_UNDECRYPTABLE above, never a provider or enumeration failure.
        try { return await send(new MarketplaceCredentialContext(accountId, snapshot.ExternalAccountId, read.SecretMaterial, snapshot.ConfirmedOperationId!.Value), cancellationToken); }
        catch (MarketplaceStoredCredentialUnusableException) { throw new MarketplaceExecutionBlockedException("CREDENTIAL_CONFIRMED_LOST"); }
    }

    private static void ValidateGuards(ExecutionSnapshot snapshot, MarketplaceExecutionRequirement requirement, string? requiredCapabilityCode, DateTimeOffset now)
    {
        if (!snapshot.AccountActive) throw new MarketplaceExecutionBlockedException("ACCOUNT_INACTIVE");
        if (!snapshot.ChannelActive) throw new MarketplaceExecutionBlockedException("CHANNEL_INVALID");
        if (requirement == MarketplaceExecutionRequirement.BusinessCapability)
        {
            // A-05 bug 1: a business-capability caller supplying no capability code must NEVER
            // silently bypass the structural/grant guards — that is a programmer/configuration
            // error, not "no capability to check," and must fail closed exactly like any other
            // guard. Only Probe (a DIFFERENT requirement value entirely) legitimately needs no code.
            if (requiredCapabilityCode is null) throw new MarketplaceExecutionBlockedException("CAPABILITY_CODE_REQUIRED");
            if (snapshot.StructuralState(requiredCapabilityCode) != ProviderCapabilityState.SUPPORTED) throw new MarketplaceExecutionBlockedException("CAPABILITY_UNSUPPORTED");
            var grant = snapshot.GrantState(requiredCapabilityCode);
            if (grant != AccountCapabilityState.GRANTED) throw new MarketplaceExecutionBlockedException(grant == AccountCapabilityState.DENIED ? "GRANT_DENIED" : "GRANT_UNKNOWN");
        }
        ValidateAuthorizationUsable(snapshot);
        // A probe's whole purpose is recovering from UNAVAILABLE (ADR-0024 §5 "Owner probe is a
        // recovery exception to runtime UNAVAILABLE"); a business-capability read is not.
        if (requirement == MarketplaceExecutionRequirement.BusinessCapability && snapshot.RuntimeAvailability == MarketplaceRuntimeAvailability.UNAVAILABLE)
            throw new MarketplaceExecutionBlockedException("RUNTIME_UNAVAILABLE");
        if (snapshot.RuntimeRetryAfterUntil is { } retryAfter && retryAfter > now)
            throw new MarketplaceExecutionBlockedException("RATE_LIMITED");
    }

    private static void ValidateAuthorizationUsable(ExecutionSnapshot snapshot)
    {
        if (snapshot.AuthorizationState != MarketplaceAuthorizationState.CONNECTED || snapshot.CredentialReference is null
            || snapshot.ConfirmedCredentialVersion is null || snapshot.ConfirmedOperationId is null)
            throw new MarketplaceExecutionBlockedException("AUTHORIZATION_NOT_USABLE");
    }

    private async Task<bool> HasBlockingOperationAsync(Guid accountId, string providerCode, CancellationToken cancellationToken) =>
        await _uow.ExecuteAsync(async (db, ct) =>
            await MarketplaceOperationFences.IsBlockedAsync(db, accountId, providerCode, ct), cancellationToken);

    private async Task<ExecutionSnapshot?> LoadSnapshotAsync(Guid accountId, CancellationToken cancellationToken) =>
        await _uow.ExecuteAsync(async (db, ct) =>
        {
            var account = await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).Include(x => x.Capabilities).SingleOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null) return null;
            var channelActive = await db.Set<SalesChannel>().AsNoTracking().AnyAsync(x => x.Id == account.SalesChannelId && x.Active && x.Kind == SalesChannelKind.Marketplace, ct);
            var structural = await db.Set<MarketplaceProviderCapability>().AsNoTracking().Where(x => x.ProviderCode == account.ProviderCode)
                .ToDictionaryAsync(x => x.CapabilityCode, x => x.State, ct);
            var grants = account.Capabilities.ToDictionary(x => x.CapabilityCode, x => x.State);
            return new ExecutionSnapshot(account.ProviderCode, account.ExternalAccountId, account.Active, channelActive,
                account.Connection.AuthorizationState, account.Connection.RuntimeAvailability, account.CredentialReference,
                account.Connection.ConfirmedCredentialVersion, account.Connection.ConfirmedOperationId,
                account.Connection.AccessExpiresAt, account.Connection.RuntimeRetryAfterUntil, structural, grants);
        }, cancellationToken);

    private sealed record ExecutionSnapshot(
        string ProviderCode, string ExternalAccountId, bool AccountActive, bool ChannelActive,
        MarketplaceAuthorizationState AuthorizationState, MarketplaceRuntimeAvailability RuntimeAvailability,
        string? CredentialReference, long? ConfirmedCredentialVersion, Guid? ConfirmedOperationId,
        DateTimeOffset? AccessExpiresAt, DateTimeOffset? RuntimeRetryAfterUntil,
        IReadOnlyDictionary<string, ProviderCapabilityState> Structural, IReadOnlyDictionary<string, AccountCapabilityState> Grants)
    {
        public ProviderCapabilityState StructuralState(string code) => Structural.TryGetValue(code, out var s) ? s : ProviderCapabilityState.UNKNOWN;
        public AccountCapabilityState GrantState(string code) => Grants.TryGetValue(code, out var g) ? g : AccountCapabilityState.UNKNOWN;
    }
}
