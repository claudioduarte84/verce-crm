using Verce.SharedKernel.Domain;

namespace Verce.Modules.Commerce;

/// <summary>
/// The account-owned authorization authority. It intentionally has no independent version: all
/// mutations are owned by <see cref="MarketplaceAccount"/> and advance that root's Version.
/// </summary>
public sealed class MarketplaceAccountConnection : Entity, IOwnedBy<MarketplaceAccount>
{
    private MarketplaceAccountConnection() { }

    private MarketplaceAccountConnection(Guid accountId) : base(accountId)
    {
        MarketplaceAccountId = accountId;
        AuthorizationState = MarketplaceAuthorizationState.NOT_CONNECTED;
        RuntimeAvailability = MarketplaceRuntimeAvailability.UNKNOWN;
    }

    public Guid MarketplaceAccountId { get; private set; }
    public Guid ParentId => MarketplaceAccountId;
    public MarketplaceAuthorizationState AuthorizationState { get; private set; }
    public MarketplaceRuntimeAvailability RuntimeAvailability { get; private set; }
    public DateTimeOffset? IdentityVerifiedAt { get; private set; }
    public long? ConfirmedCredentialVersion { get; private set; }
    public Guid? ConfirmedOperationId { get; private set; }
    public DateTimeOffset? AccessExpiresAt { get; private set; }
    public DateTimeOffset? LastRefreshAt { get; private set; }
    public DateTimeOffset? LastSuccessAt { get; private set; }
    public DateTimeOffset? LastFailureAt { get; private set; }
    public string? LastFailureClassification { get; private set; }
    public string? SafeFailureCode { get; private set; }
    public string? ProviderErrorCode { get; private set; }
    public string? ProviderRequestId { get; private set; }
    public DateTimeOffset? RuntimeRetryAfterUntil { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    internal static MarketplaceAccountConnection NotConnected(Guid accountId) => new(accountId);

    internal void Confirm(Guid operationId, long credentialVersion, DateTimeOffset now)
    {
        if (operationId == Guid.Empty || credentialVersion < 1) throw new ArgumentException("MARKETPLACE_CONFIRMATION_INVALID");
        AuthorizationState = MarketplaceAuthorizationState.CONNECTED;
        RuntimeAvailability = MarketplaceRuntimeAvailability.UNKNOWN;
        IdentityVerifiedAt = now;
        ConfirmedOperationId = operationId;
        ConfirmedCredentialVersion = credentialVersion;
        SafeFailureCode = null;
        LastFailureClassification = null;
        UpdatedAt = now;
    }

    internal void RequireReauthorization(string safeCode, DateTimeOffset now)
    {
        AuthorizationState = MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED;
        RuntimeAvailability = MarketplaceRuntimeAvailability.UNKNOWN;
        SafeFailureCode = RequiredSafeCode(safeCode);
        UpdatedAt = now;
    }

    internal void Revoke(DateTimeOffset now)
    {
        AuthorizationState = MarketplaceAuthorizationState.REVOKED;
        RuntimeAvailability = MarketplaceRuntimeAvailability.UNKNOWN;
        UpdatedAt = now;
    }

    internal void ClearConfirmedCredential(DateTimeOffset now)
    {
        ConfirmedCredentialVersion = null;
        ConfirmedOperationId = null;
        AccessExpiresAt = null;
        LastRefreshAt = null;
        UpdatedAt = now;
    }

    internal void MarkAvailable(DateTimeOffset now)
    {
        if (AuthorizationState != MarketplaceAuthorizationState.CONNECTED) throw new InvalidOperationException("MARKETPLACE_AUTHORIZATION_NOT_USABLE");
        RuntimeAvailability = MarketplaceRuntimeAvailability.AVAILABLE;
        LastSuccessAt = now;
        UpdatedAt = now;
    }

    internal void MarkUnavailable(string classification, string safeCode, DateTimeOffset now, DateTimeOffset? retryAfter = null)
    {
        RuntimeAvailability = MarketplaceRuntimeAvailability.UNAVAILABLE;
        LastFailureAt = now;
        LastFailureClassification = RequiredSafeCode(classification);
        SafeFailureCode = RequiredSafeCode(safeCode);
        RuntimeRetryAfterUntil = retryAfter;
        UpdatedAt = now;
    }

    private static string RequiredSafeCode(string value)
    {
        var code = value?.Trim() ?? string.Empty;
        if (code.Length is < 1 or > 64 || !code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException("MARKETPLACE_SAFE_CODE_INVALID");
        return code;
    }
}

/// <summary>Opaque, browser-bound one-time authorization state. No raw state/code/token exists here.</summary>
public sealed class MarketplaceAuthorizationSession : TechnicalEntity, ITechnicalTable
{
    private MarketplaceAuthorizationSession() { }

    public MarketplaceAuthorizationSession(string providerCode, Guid salesChannelId, Guid initiatedByUserId, string requestedDisplayName,
        string stateHash, string browserBindingHash, Guid? reconnectMarketplaceAccountId, long? reconnectAccountVersion,
        DateTimeOffset now)
    {
        if (salesChannelId == Guid.Empty || initiatedByUserId == Guid.Empty) throw new ArgumentException("MARKETPLACE_SESSION_CONTEXT_REQUIRED");
        if ((reconnectMarketplaceAccountId is null) != (reconnectAccountVersion is null)) throw new ArgumentException("MARKETPLACE_SESSION_RECONNECT_CONTEXT_INVALID");
        ProviderCode = MarketplaceProvider.RequiredCode(providerCode);
        SalesChannelId = salesChannelId;
        InitiatedByUserId = initiatedByUserId;
        RequestedDisplayName = MarketplaceProvider.Required(requestedDisplayName, 2, 200);
        StateHash = RequiredHash(stateHash);
        BrowserBindingHash = RequiredHash(browserBindingHash);
        ReconnectMarketplaceAccountId = reconnectMarketplaceAccountId;
        ReconnectAccountVersion = reconnectAccountVersion;
        CreatedAt = now;
        ExpiresAt = now.AddMinutes(15);
        UpdatedAt = now;
        Status = MarketplaceAuthorizationSessionStatus.PENDING;
    }

    public string ProviderCode { get; private set; } = string.Empty;
    public Guid SalesChannelId { get; private set; }
    public Guid InitiatedByUserId { get; private set; }
    public string RequestedDisplayName { get; private set; } = string.Empty;
    public Guid? ReconnectMarketplaceAccountId { get; private set; }
    public long? ReconnectAccountVersion { get; private set; }
    public string StateHash { get; private set; } = string.Empty;
    public string BrowserBindingHash { get; private set; } = string.Empty;
    public MarketplaceAuthorizationSessionStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ClaimedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public string? SafeOutcomeCode { get; private set; }
    public string? ProtectedTransientReference { get; private set; }
    public DateTimeOffset? NextCleanupAt { get; private set; }
    public int CleanupAttemptCount { get; private set; }
    public long Version { get; private set; } = 1;

    public bool TryClaim(string stateHash, string browserBindingHash, Guid actorId, DateTimeOffset now)
    {
        if (Status != MarketplaceAuthorizationSessionStatus.PENDING || now >= ExpiresAt || actorId != InitiatedByUserId ||
            !FixedTimeEquals(StateHash, stateHash) || !FixedTimeEquals(BrowserBindingHash, browserBindingHash)) return false;
        Status = MarketplaceAuthorizationSessionStatus.CLAIMED;
        ClaimedAt = now;
        UpdatedAt = now;
        Version++;
        return true;
    }

    public void Complete(string outcomeCode, DateTimeOffset now) => Finish(MarketplaceAuthorizationSessionStatus.COMPLETED, outcomeCode, now);
    public void Fail(string outcomeCode, DateTimeOffset now) => Finish(MarketplaceAuthorizationSessionStatus.FAILED, outcomeCode, now);
    public void Expire(DateTimeOffset now) { if (Status == MarketplaceAuthorizationSessionStatus.PENDING && now >= ExpiresAt) Finish(MarketplaceAuthorizationSessionStatus.EXPIRED, "AUTHORIZATION_SESSION_EXPIRED", now); }
    public void Revoke(string outcomeCode, DateTimeOffset now) { if (Status == MarketplaceAuthorizationSessionStatus.PENDING) Finish(MarketplaceAuthorizationSessionStatus.REVOKED, outcomeCode, now); }

    private void Finish(MarketplaceAuthorizationSessionStatus status, string outcomeCode, DateTimeOffset now)
    {
        if (Status is MarketplaceAuthorizationSessionStatus.COMPLETED or MarketplaceAuthorizationSessionStatus.FAILED or MarketplaceAuthorizationSessionStatus.EXPIRED or MarketplaceAuthorizationSessionStatus.REVOKED)
            throw new InvalidOperationException("MARKETPLACE_SESSION_TERMINAL");
        Status = status;
        SafeOutcomeCode = RequiredSafeCode(outcomeCode);
        FinishedAt = now;
        NextCleanupAt = now;
        UpdatedAt = now;
        Version++;
    }

    /// <summary>Housekeeping has physically swept this terminal session's own retention marker.
    /// The durable operation row — not the session — governs any credential deletion.</summary>
    public void MarkSwept(DateTimeOffset now) { NextCleanupAt = null; UpdatedAt = now; Version++; }

    private static string RequiredHash(string value)
    {
        var hash = value?.Trim() ?? string.Empty;
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new ArgumentException("MARKETPLACE_SESSION_HASH_INVALID");
        return hash.ToLowerInvariant();
    }
    private static string RequiredSafeCode(string value) => value?.Length is > 0 and <= 64 ? value : throw new ArgumentException("MARKETPLACE_SAFE_CODE_INVALID");
    private static bool FixedTimeEquals(string expected, string actual) => actual?.Length == expected.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(expected), System.Text.Encoding.ASCII.GetBytes(actual));
}

/// <summary>PostgreSQL is the only terminal authority for credential-changing work.</summary>
public sealed class MarketplaceAccountOperation : TechnicalEntity, ITechnicalTable
{
    private MarketplaceAccountOperation() { }

    public MarketplaceAccountOperation(MarketplaceAccountOperationKind kind, string providerCode, Guid? marketplaceAccountId,
        Guid? authorizationSessionId, long? expectedAccountVersion, string? previousCredentialReference,
        long? previousConfirmedCredentialVersion, DateTimeOffset now)
    {
        if (kind is MarketplaceAccountOperationKind.RECONNECT or MarketplaceAccountOperationKind.REFRESH or MarketplaceAccountOperationKind.DISCONNECT && marketplaceAccountId is null)
            throw new ArgumentException("MARKETPLACE_OPERATION_ACCOUNT_REQUIRED");
        if (kind is MarketplaceAccountOperationKind.CONNECT_NEW or MarketplaceAccountOperationKind.RECONNECT && authorizationSessionId is null)
            throw new ArgumentException("MARKETPLACE_OPERATION_SESSION_REQUIRED");
        Kind = kind;
        ProviderCode = MarketplaceProvider.RequiredCode(providerCode);
        MarketplaceAccountId = marketplaceAccountId;
        AuthorizationSessionId = authorizationSessionId;
        ExpectedAccountVersion = expectedAccountVersion;
        PreviousCredentialReference = Optional(previousCredentialReference, 300);
        PreviousConfirmedCredentialVersion = previousConfirmedCredentialVersion;
        CreatedAt = UpdatedAt = now;
    }

    public MarketplaceAccountOperationKind Kind { get; private set; }
    public MarketplaceAccountOperationPhase Phase { get; private set; } = MarketplaceAccountOperationPhase.PREPARED;
    public MarketplaceAccountOperationDecision Decision { get; private set; } = MarketplaceAccountOperationDecision.PENDING;
    public MarketplaceAccountOperationCleanupState CleanupState { get; private set; } = MarketplaceAccountOperationCleanupState.NOT_REQUIRED;
    public Guid? MarketplaceAccountId { get; private set; }
    public Guid? AuthorizationSessionId { get; private set; }
    public string ProviderCode { get; private set; } = string.Empty;
    public string? ResolvedExternalAccountId { get; private set; }
    public long? ExpectedAccountVersion { get; private set; }
    public string? PreviousCredentialReference { get; private set; }
    public long? PreviousConfirmedCredentialVersion { get; private set; }
    public string? CandidateCredentialReference { get; private set; }
    public long? CandidateCredentialVersion { get; private set; }
    public string? SafeResultCode { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public DateTimeOffset? NextCleanupAt { get; private set; }
    public int CleanupAttemptCount { get; private set; }
    public long Version { get; private set; } = 1;

    public void MarkExternalInFlight(DateTimeOffset now) => AdvancePhase(MarketplaceAccountOperationPhase.EXTERNAL_IN_FLIGHT, now);
    public void RecordPersistedCandidate(string reference, long version, DateTimeOffset now)
    {
        if (version < 1) throw new ArgumentException("MARKETPLACE_CREDENTIAL_VERSION_INVALID");
        CandidateCredentialReference = Required(reference, 300);
        CandidateCredentialVersion = version;
        AdvancePhase(MarketplaceAccountOperationPhase.SECRET_PERSISTED, now);
    }
    public void ResolveIdentity(string externalAccountId) => ResolvedExternalAccountId = Required(externalAccountId, 200);
    public void BindAccount(Guid accountId) { if (accountId == Guid.Empty) throw new ArgumentException("MARKETPLACE_OPERATION_ACCOUNT_REQUIRED"); MarketplaceAccountId = accountId; }
    public void Confirm(string safeResultCode, DateTimeOffset now) => Terminal(MarketplaceAccountOperationDecision.CONFIRMED, safeResultCode, now);
    public void FailClosed(string safeResultCode, DateTimeOffset now) => Terminal(MarketplaceAccountOperationDecision.FAIL_CLOSED, safeResultCode, now);
    public void RequestCleanup(DateTimeOffset now) { if (Decision == MarketplaceAccountOperationDecision.PENDING) throw new InvalidOperationException("MARKETPLACE_OPERATION_NOT_TERMINAL"); CleanupState = MarketplaceAccountOperationCleanupState.PENDING; NextCleanupAt = now; UpdatedAt = now; Version++; }
    public void CompleteCleanup(DateTimeOffset now) { if (CleanupState != MarketplaceAccountOperationCleanupState.PENDING) throw new InvalidOperationException("MARKETPLACE_OPERATION_CLEANUP_INVALID"); CleanupState = MarketplaceAccountOperationCleanupState.DONE; NextCleanupAt = null; UpdatedAt = now; Version++; }
    /// <summary>Bounded exponential backoff for a failed deletion attempt — never blocks later due rows.</summary>
    public void ScheduleCleanupRetry(DateTimeOffset nextAttemptAt, DateTimeOffset now) { if (CleanupState != MarketplaceAccountOperationCleanupState.PENDING) throw new InvalidOperationException("MARKETPLACE_OPERATION_CLEANUP_INVALID"); CleanupAttemptCount++; NextCleanupAt = nextAttemptAt; UpdatedAt = now; Version++; }

    private void AdvancePhase(MarketplaceAccountOperationPhase phase, DateTimeOffset now)
    {
        if (Decision != MarketplaceAccountOperationDecision.PENDING || phase < Phase) throw new InvalidOperationException("MARKETPLACE_OPERATION_PHASE_INVALID");
        Phase = phase; UpdatedAt = now; Version++;
    }
    private void Terminal(MarketplaceAccountOperationDecision decision, string safeResultCode, DateTimeOffset now)
    {
        if (Decision != MarketplaceAccountOperationDecision.PENDING) throw new InvalidOperationException("MARKETPLACE_OPERATION_TERMINAL");
        Decision = decision; SafeResultCode = RequiredSafeCode(safeResultCode); DecidedAt = now; UpdatedAt = now; Version++;
    }
    private static string Required(string value, int max) { var v = value?.Trim() ?? string.Empty; if (v.Length is < 1 or > 300) throw new ArgumentException("MARKETPLACE_OPERATION_VALUE_INVALID"); return v; }
    private static string? Optional(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : Required(value, max);
    private static string RequiredSafeCode(string value) => value?.Length is > 0 and <= 64 ? value : throw new ArgumentException("MARKETPLACE_SAFE_CODE_INVALID");
}

public sealed record ProtectedCredentialReceipt(Guid OperationId, string CredentialReference, long CredentialVersion, string IntegrityEvidence, DateTimeOffset PersistedAt);
