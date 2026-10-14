using Microsoft.EntityFrameworkCore;
using Verce.Modules.Commerce;
using Verce.Platform.Persistence;

namespace Verce.Api.Commerce;

/// <summary>
/// The ADR-0024 connection RequiredAction (vocabulary, precedence and inputs), extracted unchanged from <c>CommerceEndpoints</c> so the connection
/// status route and the listing-sync surfaces (ADR-0027) share ONE implementation. API/presentation concern only: no Stream B or domain code.
/// </summary>
public static class MarketplaceRequiredActions
{
    public const string None = "NONE";
    public const string Authorize = "AUTHORIZE";
    public const string Reauthorize = "REAUTHORIZE";
    public const string ReauthorizeAfterStoreLoss = "REAUTHORIZE_AFTER_STORE_LOSS";
    public const string ReconnectExistingAccount = "RECONNECT_EXISTING_ACCOUNT";
    public const string ContactAdmin = "CONTACT_ADMIN";
    public const string CheckProviderAccount = "CHECK_PROVIDER_ACCOUNT";

    /// <summary>The closed ADR-0024 vocabulary, in ADR-0027 §6 precedence order (highest first).</summary>
    public static readonly IReadOnlyList<string> PrecedenceHighestFirst = [ContactAdmin, ReauthorizeAfterStoreLoss, ReconnectExistingAccount, Reauthorize, Authorize, CheckProviderAccount, None];

    /// <summary>ADR-0024 G-05 local catalog RequiredAction (D-02: PROVIDER_APP_CREDENTIALS_INVALID is a VERCE application failure and maps to CONTACT_ADMIN first).</summary>
    public static string ForConnection(MarketplaceAccountConnection connection, bool authorizationInProgress)
    {
        if (authorizationInProgress) return None;
        return connection.AuthorizationState switch
        {
            MarketplaceAuthorizationState.NOT_CONNECTED or MarketplaceAuthorizationState.REVOKED => Authorize,
            MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED => connection.SafeFailureCode switch
            {
                "PROVIDER_APP_CREDENTIALS_INVALID" => ContactAdmin,
                "RECONNECT_EXISTING_ACCOUNT" or "ACCOUNT_ALREADY_EXISTS_RECONNECT_REQUIRED" => ReconnectExistingAccount,
                "PROBE_CREDENTIAL_UNREADABLE" or "OPERATION_RECOVERY_FAIL_CLOSED" => ReauthorizeAfterStoreLoss,
                _ => Reauthorize,
            },
            MarketplaceAuthorizationState.CONNECTED when connection.RuntimeAvailability == MarketplaceRuntimeAvailability.UNAVAILABLE => CheckProviderAccount,
            _ => None,
        };
    }

    /// <summary>A PENDING or CLAIMED reauthorization session exists for the account.</summary>
    public static Task<bool> AuthorizationInProgressAsync(VerceDbContext db, Guid accountId, CancellationToken ct) =>
        db.Set<MarketplaceAuthorizationSession>().AsNoTracking().AnyAsync(x => x.ReconnectMarketplaceAccountId == accountId
            && (x.Status == MarketplaceAuthorizationSessionStatus.PENDING || x.Status == MarketplaceAuthorizationSessionStatus.CLAIMED), ct);

    /// <summary>Highest-precedence action of the candidates (ADR-0027 §6); NONE when empty. Never depends on candidate order.</summary>
    public static string Highest(IEnumerable<string> actions)
    {
        var set = actions.ToHashSet(StringComparer.Ordinal);
        foreach (var action in PrecedenceHighestFirst) if (set.Contains(action)) return action;
        return None;
    }
}
