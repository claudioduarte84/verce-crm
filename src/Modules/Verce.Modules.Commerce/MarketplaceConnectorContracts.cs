namespace Verce.Modules.Commerce;

/// <summary>Only provider-neutral authorization and account-inspection semantics belong in Commerce.</summary>
public interface IMarketplaceAuthorizationConnector
{
    string ProviderCode { get; }
    Task<MarketplaceAuthorizationStart> BeginAsync(MarketplaceAuthorizationBeginRequest request, CancellationToken cancellationToken);
    Task<MarketplaceAuthorizationCompletion> CompleteOnceAsync(MarketplaceAuthorizationCompletionRequest request, CancellationToken cancellationToken);
    Task<MarketplaceRefreshCompletion> RefreshAsync(MarketplaceRefreshRequest request, CancellationToken cancellationToken);
    Task<MarketplaceAccountInspection> InspectAsync(MarketplaceAccountInspectionRequest request, CancellationToken cancellationToken);
}

public interface IMarketplaceConnectorRegistry
{
    bool TryGet(string providerCode, out IMarketplaceAuthorizationConnector connector);

    /// <summary>ADR-0025 §B.1: "a reader without a same-code authorization connector fails
    /// startup" — enforced by the concrete registry's constructor, not here.</summary>
    bool TryGetListingReader(string providerCode, out IMarketplaceListingReader reader);
}

public interface IProtectedCredentialStore
{
    Task<ProtectedCredentialStoreResult> CreateStagedAsync(ProtectedCredentialWrite write, CancellationToken cancellationToken);
    Task<ProtectedCredentialStoreResult> BindCandidateAsync(ProtectedCredentialBinding binding, CancellationToken cancellationToken);
    Task<ProtectedCredentialStoreResult> PromoteCandidateAsync(Guid operationId, string credentialReference, long credentialVersion, CancellationToken cancellationToken);
    Task<ProtectedCredentialReadResult> UseConfirmedAsync(ProtectedCredentialRead read, CancellationToken cancellationToken);
    Task<ProtectedCredentialStoreResult> CompareAndSwapReplaceAsync(ProtectedCredentialWrite write, long expectedVersion, CancellationToken cancellationToken);
    Task<ProtectedCredentialStoreResult> DeleteCandidateAsync(Guid operationId, string credentialReference, long credentialVersion, CancellationToken cancellationToken);
}

public enum ProtectedCredentialStoreOutcome { SUCCESS, NOT_FOUND, VERSION_CONFLICT, STORE_UNAVAILABLE, CORRUPTED_OR_UNDECRYPTABLE, WRITE_FAILED }

/// <summary>
/// G-08: "Send certainty is separate typed metadata NOT_SENT|SENT_OR_UNKNOWN; an error
/// classification alone cannot establish it." A connector throws this ONLY when it can positively
/// prove the request never reached the provider (e.g. local validation failure, DNS/connect
/// refused before any bytes were sent, provider-declared idempotent rejection with an explicit
/// not-received signal). Every other failure — including a plain exception — is SENT_OR_UNKNOWN
/// and must be treated conservatively (RT-02: the provider-wide fence may need to close every
/// account for that provider, not just this one session).
/// </summary>
public sealed class MarketplaceRequestNotSentException : Exception
{
    public MarketplaceRequestNotSentException(string message) : base(message) { }
}

public sealed record MarketplaceAuthorizationBeginRequest(Guid SessionId, string State, string RedirectUri);
public sealed record MarketplaceAuthorizationStart(string AuthorizationUri);
public sealed record MarketplaceAuthorizationCompletionRequest(Guid SessionId, string AuthorizationCode);
// D-01: trailing AccessExpiresAt (default null so every existing/fake connector keeps compiling)
// carries the provider-declared access-token lifetime so it can actually be persisted on
// confirmation instead of being silently dropped.
public sealed record MarketplaceAuthorizationCompletion(string ExternalAccountId, IReadOnlyDictionary<string, AccountCapabilityState> Grants, ReadOnlyMemory<byte> SecretMaterial, CredentialImpact CredentialImpact, DateTimeOffset? AccessExpiresAt = null);
public sealed record MarketplaceRefreshRequest(Guid AccountId, string ProviderCode, ReadOnlyMemory<byte> SecretMaterial, bool Force = false);
public sealed record MarketplaceRefreshCompletion(ReadOnlyMemory<byte> SecretMaterial, DateTimeOffset? AccessExpiresAt);
public sealed record MarketplaceAccountInspectionRequest(Guid AccountId, string ProviderCode, ReadOnlyMemory<byte> SecretMaterial);
public sealed record MarketplaceAccountInspection(string ExternalAccountId, IReadOnlyDictionary<string, AccountCapabilityState> Grants);

// ---- ADR-0025 §F.1 (D-02 / G01 Astra): normalized provider failure vocabulary. Provider-neutral;
// no ML type/string appears here. A connector throws MarketplaceProviderCallException only for a
// COMPLETE provider response (success or error) that maps to a known failure; anything without a
// complete usable response must surface as an ordinary exception (never this type), so the
// workflow's catch-all branch is what classifies it SENT_OR_UNKNOWN. MarketplaceRequestNotSentException
// remains the only NOT_SENT signal, thrown only before the connector's SendAsync-equivalent begins.
public enum MarketplaceFailureClassification { NON_RETRYABLE, RETRYABLE, RATE_LIMITED, AUTH_RENEWAL_REQUIRED, USER_ACTION_REQUIRED, UNKNOWN }
public enum MarketplaceSendCertainty { NOT_SENT, SENT_AND_RESPONSE_RECEIVED, SENT_OR_UNKNOWN }
public sealed record MarketplaceProviderFailure(MarketplaceFailureClassification Classification, string SafeCode, MarketplaceSendCertainty Certainty, string? ProviderErrorCode = null, DateTimeOffset? RetryAfterUntil = null);
public sealed class MarketplaceProviderCallException : Exception
{
    public MarketplaceProviderFailure Failure { get; }
    public MarketplaceProviderCallException(MarketplaceProviderFailure failure) : base(failure.SafeCode) => Failure = failure;
}
/// <summary>
/// <paramref name="ExpectedPreviousVersion"/> is the CAS guard for <c>CreateStagedAsync</c>:
/// null means "this reference has never been bound before" (CONNECT_NEW, or a fresh K2
/// generation) and requires no prior material to exist; a non-null value means the caller
/// (Commerce, from its own DB truth) expects that EXACT version to currently be the confirmed
/// material for this reference. RT-03: physically missing/corrupt/mismatched material at that
/// expectation is the store-loss signal that forces a fresh K2 reference — it must never be
/// silently treated as "start over at version 1 under the same name," because that would let an
/// old K1 backup that reappears later collide with the new material at the same path.
/// </summary>
public sealed record ProtectedCredentialWrite(Guid OperationId, Guid? SessionId, Guid? AccountId, string ProviderCode, string CredentialReference, ReadOnlyMemory<byte> SecretMaterial, long? ExpectedPreviousVersion = null);
public sealed record ProtectedCredentialBinding(Guid OperationId, Guid SessionId, Guid AccountId, string ProviderCode, string CredentialReference);
public sealed record ProtectedCredentialRead(Guid AccountId, string ProviderCode, string CredentialReference, long CredentialVersion, Guid ConfirmedOperationId);
public sealed record ProtectedCredentialStoreResult(ProtectedCredentialStoreOutcome Outcome, ProtectedCredentialReceipt? Receipt = null);
public sealed record ProtectedCredentialReadResult(ProtectedCredentialStoreOutcome Outcome, ReadOnlyMemory<byte> SecretMaterial, ProtectedCredentialReceipt? Receipt = null);
