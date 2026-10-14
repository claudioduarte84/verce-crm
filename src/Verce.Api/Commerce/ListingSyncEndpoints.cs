using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Verce.Api.Authorization;
using Verce.Infrastructure.Marketplaces;
using Verce.Modules.Commerce;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Time;

namespace Verce.Api.Commerce;

// ---- C2 API DTOs (explicit, allowlisted, provider-neutral; no lease/fencing/credential/provider fields) ----

/// <summary>The frozen run DTO (ADR-0025 J). Counters are always present - on the run/start/retry responses and on the runs embedded in the status summary - and derive from run items.</summary>
public sealed record ListingSyncRunResponse(Guid RunId, Guid AccountId,
    [property: JsonConverter(typeof(JsonStringEnumConverter<MarketplaceListingSyncKind>))] MarketplaceListingSyncKind Kind,
    [property: JsonConverter(typeof(JsonStringEnumConverter<MarketplaceListingSyncStatus>))] MarketplaceListingSyncStatus Status,
    [property: JsonConverter(typeof(JsonStringEnumConverter<MarketplaceListingSyncPhase>))] MarketplaceListingSyncPhase? Phase,
    Guid RootRunId, Guid? RetryOfRunId, DateTimeOffset RequestedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, int AttemptCount, bool EnumerationCompleted,
    ListingSyncCountersResponse Counters, string? SafeResultCode, string RequiredAction);
public sealed record ListingSyncCountersResponse(int Total, int Found, int BlockingRetryable, int BlockingNonRetryable, int Pending,
    IReadOnlyDictionary<string, int> ByOutcome, IReadOnlyDictionary<string, int> ByOrigin, int ListingsCreated, int ObservationsAppended);
public sealed record ListingSyncAvailabilityResponse(bool CanStart, string? BlockedReason, string RequiredAction, DateTimeOffset? RetryAfterUntil, Guid? ActiveRunId);
public sealed record ListingSyncSummaryResponse(ListingSyncRunResponse? ActiveRun, ListingSyncRunResponse? LatestRun, ListingSyncRunResponse? LatestFullRun,
    DateTimeOffset? LastSucceededFullRunAt, int OpenBlockingItemCount,
    [property: JsonConverter(typeof(JsonStringEnumConverter<MarketplaceListingSyncHealth>))] MarketplaceListingSyncHealth Health);
public sealed record ListingSyncStatusResponse(ListingSyncAvailabilityResponse Availability, ListingSyncSummaryResponse Summary);
public sealed record ListingSyncRunItemResponse(string ExternalListingId,
    [property: JsonConverter(typeof(JsonStringEnumConverter<MarketplaceListingSyncItemOrigin>))] MarketplaceListingSyncItemOrigin Origin,
    [property: JsonConverter(typeof(JsonStringEnumConverter<MarketplaceListingSyncItemOutcome>))] MarketplaceListingSyncItemOutcome Outcome,
    string? SafeErrorCode, Guid? MarketplaceListingId, bool Retryable);

/// <summary>
/// ADR-0027 catalogs. Every current code has an explicit classification; the tests pin these sets in both directions, so a new safe code cannot ship
/// without a deliberate decision here.
/// </summary>
public static class ListingSyncRequiredActionRules
{
    public enum RunCodeKind { Fixed, Supersedable, Credential }
    public sealed record RunCodeRule(RunCodeKind Kind, string Action);

    /// <summary>The frozen §J blockedReason catalog and its ADR-0027 §3 mapping (AUTHORIZATION_NOT_USABLE is resolved from the current connection).</summary>
    public static readonly IReadOnlyDictionary<string, string?> BlockedReasonActions = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["ACCOUNT_INACTIVE"] = MarketplaceRequiredActions.None,
        ["CHANNEL_INVALID"] = MarketplaceRequiredActions.None,
        ["PROVIDER_NOT_CONFIGURED"] = MarketplaceRequiredActions.ContactAdmin,
        ["CAPABILITY_UNSUPPORTED"] = MarketplaceRequiredActions.None,
        ["GRANT_UNKNOWN"] = MarketplaceRequiredActions.None,
        ["GRANT_DENIED"] = MarketplaceRequiredActions.CheckProviderAccount,
        ["AUTHORIZATION_NOT_USABLE"] = null, // current connection RequiredAction
        ["RATE_LIMITED"] = MarketplaceRequiredActions.None,
        ["RUNTIME_UNAVAILABLE"] = MarketplaceRequiredActions.CheckProviderAccount,
        ["SYNC_ACTIVE"] = MarketplaceRequiredActions.None,
    };

    private static RunCodeRule Fixed(string action = MarketplaceRequiredActions.None) => new(RunCodeKind.Fixed, action);

    /// <summary>The complete run safe-result / stop-code catalog and its ADR-0027 §4 classification.</summary>
    public static readonly IReadOnlyDictionary<string, RunCodeRule> RunCodes = new Dictionary<string, RunCodeRule>(StringComparer.Ordinal)
    {
        ["LISTING_SYNC_COMPLETED"] = Fixed(), ["LISTING_SYNC_PARTIAL"] = Fixed(), ["LISTING_SYNC_NO_SUCCESSFUL_ITEMS"] = Fixed(),
        ["LISTING_ENUMERATION_INCOMPLETE"] = Fixed(), ["RUN_EXECUTION_ATTEMPTS_EXHAUSTED"] = Fixed(),
        ["PROVIDER_UNAVAILABLE"] = Fixed(), ["PROVIDER_RATE_LIMITED"] = Fixed(),
        // Guard stops are re-evaluated by admission and reported on the availability surface only (ADR-0027 §4).
        ["ACCOUNT_INACTIVE"] = Fixed(), ["CHANNEL_INVALID"] = Fixed(), ["PROVIDER_NOT_CONFIGURED"] = Fixed(), ["MARKETPLACE_PROVIDER_NOT_SUPPORTED"] = Fixed(),
        ["CAPABILITY_UNSUPPORTED"] = Fixed(), ["GRANT_UNKNOWN"] = Fixed(), ["GRANT_DENIED"] = Fixed(), ["AUTHORIZATION_NOT_USABLE"] = Fixed(),
        ["RUNTIME_UNAVAILABLE"] = Fixed(), ["RATE_LIMITED"] = Fixed(),
        ["PROVIDER_ACCESS_FORBIDDEN"] = new(RunCodeKind.Supersedable, MarketplaceRequiredActions.CheckProviderAccount),
        ["CREDENTIAL_STORE_UNAVAILABLE"] = new(RunCodeKind.Supersedable, MarketplaceRequiredActions.ContactAdmin),
        ["LISTING_VOLUME_EXCEEDS_LIMIT"] = new(RunCodeKind.Supersedable, MarketplaceRequiredActions.ContactAdmin),
        ["CAPABILITY_CODE_REQUIRED"] = new(RunCodeKind.Supersedable, MarketplaceRequiredActions.ContactAdmin),
        ["PROVIDER_CREDENTIAL_REJECTED"] = new(RunCodeKind.Credential, MarketplaceRequiredActions.Reauthorize),
        ["CREDENTIAL_CONFIRMED_LOST"] = new(RunCodeKind.Credential, MarketplaceRequiredActions.ReauthorizeAfterStoreLoss),
    };

    /// <summary>TRANSIENT_ERROR item codes that describe one item, not why a run stopped. They are not stop codes and are ignored when deriving a PARTIAL run's action.</summary>
    public static readonly IReadOnlySet<string> ItemLevelTransientCodes = new HashSet<string>(StringComparer.Ordinal) { "LISTING_READ_FAILED", "LISTING_WRITE_CONFLICT", "RUN_ENDED_BEFORE_ITEM_PROCESSED" };
}

/// <summary>
/// ADR-0027 derivation for both listing-sync surfaces. Read-only: EF queries over existing state, no provider I/O, no secret access. One instance per request;
/// an unmapped future code yields NONE and exactly one Warning carrying only the safe code.
/// </summary>
public sealed class ListingSyncRequiredActionResolver(VerceDbContext db, ILogger logger)
{
    private sealed record AccountAction(string ConnectionAction, MarketplaceAccountOperationKind? ConfirmedKind, DateTimeOffset? ConfirmedDecidedAt);
    private readonly Dictionary<Guid, AccountAction> _accounts = [];
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    private async Task<AccountAction> AccountAsync(Guid accountId, CancellationToken ct)
    {
        if (_accounts.TryGetValue(accountId, out var cached)) return cached;
        var account = await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).SingleOrDefaultAsync(x => x.Id == accountId, ct);
        AccountAction value;
        if (account is null) value = new(MarketplaceRequiredActions.None, null, null);
        else
        {
            var inProgress = await MarketplaceRequiredActions.AuthorizationInProgressAsync(db, accountId, ct);
            var confirmed = account.Connection.ConfirmedOperationId is { } operationId
                ? await db.Set<MarketplaceAccountOperation>().AsNoTracking().Where(x => x.Id == operationId).Select(x => new { x.Kind, x.DecidedAt }).SingleOrDefaultAsync(ct) : null;
            value = new(MarketplaceRequiredActions.ForConnection(account.Connection, inProgress), confirmed?.Kind, confirmed?.DecidedAt);
        }
        return _accounts[accountId] = value;
    }

    /// <summary>Surface A: <c>availability.requiredAction</c> from the current blockedReason (ADR-0027 §3).</summary>
    public async Task<string> ForBlockedReasonAsync(Guid accountId, string? blockedReason, CancellationToken ct)
    {
        if (blockedReason is null) return MarketplaceRequiredActions.None;
        if (!ListingSyncRequiredActionRules.BlockedReasonActions.TryGetValue(blockedReason, out var fixedAction)) return Unmapped(blockedReason);
        return fixedAction ?? (await AccountAsync(accountId, ct)).ConnectionAction;
    }

    /// <summary>Surface B: <c>run.requiredAction</c> (ADR-0027 §4/§6): only run-observed evidence, current state first, highest precedence wins.</summary>
    public async Task<string> ForRunAsync(MarketplaceListingSyncRunView run, CancellationToken ct)
    {
        if (run.Status is MarketplaceListingSyncStatus.QUEUED or MarketplaceListingSyncStatus.RUNNING or MarketplaceListingSyncStatus.SUCCEEDED) return MarketplaceRequiredActions.None;
        IReadOnlyCollection<string> stops;
        if (run.Status == MarketplaceListingSyncStatus.FAILED) stops = run.SafeResultCode is null ? [] : [run.SafeResultCode];
        else
            stops = (await db.Set<MarketplaceListingSyncRunItem>().AsNoTracking().Where(x => x.SyncRunId == run.RunId && x.Outcome == MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR && x.SafeErrorCode != null)
                .Select(x => x.SafeErrorCode!).Distinct().ToListAsync(ct)).Where(code => !ListingSyncRequiredActionRules.ItemLevelTransientCodes.Contains(code)).ToList();
        var actions = new List<string>();
        foreach (var code in stops.OrderBy(x => x, StringComparer.Ordinal)) actions.Add(await ActionForAsync(code, run, ct));
        return MarketplaceRequiredActions.Highest(actions);
    }

    private async Task<string> ActionForAsync(string code, MarketplaceListingSyncRunView run, CancellationToken ct)
    {
        if (!ListingSyncRequiredActionRules.RunCodes.TryGetValue(code, out var rule)) return Unmapped(code);
        switch (rule.Kind)
        {
            case ListingSyncRequiredActionRules.RunCodeKind.Fixed: return rule.Action;
            case ListingSyncRequiredActionRules.RunCodeKind.Supersedable: return await SupersededAsync(run, ct) ? MarketplaceRequiredActions.None : rule.Action;
            default:
                var account = await AccountAsync(run.AccountId, ct);
                if (account.ConnectionAction != MarketplaceRequiredActions.None) return account.ConnectionAction;
                // Re-consent (CONNECT_NEW/RECONNECT decided after the run finished) clears it; a later REFRESH is the same authorization (ADR-0026).
                if (account.ConfirmedKind is MarketplaceAccountOperationKind.CONNECT_NEW or MarketplaceAccountOperationKind.RECONNECT
                    && account.ConfirmedDecidedAt is { } decided && run.FinishedAt is { } finished && decided > finished) return MarketplaceRequiredActions.None;
                return await SupersededAsync(run, ct) ? MarketplaceRequiredActions.None : rule.Action;
        }
    }

    private Task<bool> SupersededAsync(MarketplaceListingSyncRunView run, CancellationToken ct) =>
        db.Set<MarketplaceListingSyncRun>().AsNoTracking().AnyAsync(x => x.MarketplaceAccountId == run.AccountId && x.RequestedAt > run.RequestedAt
            && (x.Status == MarketplaceListingSyncStatus.SUCCEEDED || x.Status == MarketplaceListingSyncStatus.PARTIAL || x.Status == MarketplaceListingSyncStatus.FAILED), ct);

    private string Unmapped(string code)
    {
        if (_warned.Add(code)) logger.LogWarning("Listing sync requiredAction has no classification for safe code {SafeCode}", code);
        return MarketplaceRequiredActions.None;
    }
}

public static class ListingSyncEndpoints
{
    private const string RunRoute = "/api/commerce/marketplace-listing-syncs/";

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/marketplace-accounts/{id:guid}/listing-syncs", StartAsync).RequireAuthorization(Permissions.CommerceManage)
            .Produces<ListingSyncRunResponse>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity).ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapPost("/marketplace-listing-syncs/{runId:guid}/retry", RetryAsync).RequireAuthorization(Permissions.CommerceManage)
            .Produces<ListingSyncRunResponse>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity).ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapGet("/marketplace-accounts/{id:guid}/listing-sync", StatusAsync).RequireAuthorization(Permissions.CommerceRead)
            .Produces<ListingSyncStatusResponse>().Produces(StatusCodes.Status404NotFound);
        group.MapGet("/marketplace-listing-syncs/{runId:guid}", RunAsync).RequireAuthorization(Permissions.CommerceRead)
            .Produces<ListingSyncRunResponse>().Produces(StatusCodes.Status404NotFound);
        group.MapGet("/marketplace-listing-syncs/{runId:guid}/items", ItemsAsync).RequireAuthorization(Permissions.CommerceRead)
            .Produces<PagedResponse<ListingSyncRunItemResponse>>().Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<bool> Csrf(HttpContext h, IAntiforgery a) { try { await a.ValidateRequestAsync(h); return true; } catch (AntiforgeryValidationException) { return false; } }

    /// <summary>Start and retry take NO request body (ADR-0025 §J): any payload, including <c>{}</c>, <c>[]</c>, <c>null</c> or <c>""</c>, is rejected so a client can never name
    /// retry targets. A positive Content-Length is a payload and a zero Content-Length is none; when the length is unknown (chunked, HTTP/2) a one-byte probe of the body stream
    /// decides, so an empty chunked request stays valid and a real payload is never parsed.</summary>
    private static async Task<bool> HasRequestBodyAsync(HttpContext h, CancellationToken ct)
    {
        var request = h.Request;
        if (request.ContentLength is { } length) return length > 0;
        return await request.Body.ReadAsync(new byte[1], ct) > 0;
    }

    private static async Task<IResult> StartAsync(Guid id, HttpContext h, IAntiforgery csrf, UserManager<ApplicationUser> users, AmbientOperationContext ambient, MarketplaceListingSyncService service, VerceDbContext db, IClock clock, ILoggerFactory logs, CancellationToken ct)
    {
        if (!await Csrf(h, csrf)) return Results.BadRequest();
        if (await HasRequestBodyAsync(h, ct)) return Results.BadRequest();
        var actor = await users.GetUserAsync(h.User); if (actor is null) return Results.Unauthorized();
        ambient.SetActor(actor.Id, actor.DisplayName);
        var resolver = new ListingSyncRequiredActionResolver(db, logs.CreateLogger<ListingSyncRequiredActionResolver>());
        try { return await RespondAsync(await service.StartFullAsync(id, actor.Id, ct), id, h, service, resolver, clock, ct); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> RetryAsync(Guid runId, HttpContext h, IAntiforgery csrf, UserManager<ApplicationUser> users, AmbientOperationContext ambient, MarketplaceListingSyncService service, VerceDbContext db, IClock clock, ILoggerFactory logs, CancellationToken ct)
    {
        if (!await Csrf(h, csrf)) return Results.BadRequest();
        if (await HasRequestBodyAsync(h, ct)) return Results.BadRequest();
        var actor = await users.GetUserAsync(h.User); if (actor is null) return Results.Unauthorized();
        ambient.SetActor(actor.Id, actor.DisplayName);
        var resolver = new ListingSyncRequiredActionResolver(db, logs.CreateLogger<ListingSyncRequiredActionResolver>());
        try
        {
            var result = await service.RetryAsync(runId, actor.Id, ct);
            // The account is only needed to resolve a connection RequiredAction for a refusal; the run exists (otherwise KeyNotFound above).
            var accountId = result.Succeeded ? Guid.Empty : await db.Set<MarketplaceListingSyncRun>().AsNoTracking().Where(x => x.Id == runId).Select(x => x.MarketplaceAccountId).SingleOrDefaultAsync(ct);
            return await RespondAsync(result, accountId, h, service, resolver, clock, ct);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    /// <summary>The single deterministic mapping from an application result to HTTP, shared by start and retry so they cannot drift (ADR-0025 §G.2 / STATE-MACHINES §6).</summary>
    private static async Task<IResult> RespondAsync(MarketplaceListingSyncRequestResult result, Guid accountId, HttpContext h, MarketplaceListingSyncService service, ListingSyncRequiredActionResolver resolver, IClock clock, CancellationToken ct)
    {
        if (result.Succeeded)
        {
            var detail = await service.GetRunAsync(result.RunId!.Value, ct);
            return Results.Accepted(RunRoute + detail.Run.RunId, await RunResponseAsync(detail.Run, detail.Counters, resolver, ct));
        }
        var status = result.Code switch
        {
            "MARKETPLACE_ACCOUNT_INACTIVE" => StatusCodes.Status409Conflict,
            "MARKETPLACE_ACCOUNT_CHANNEL_INVALID" => StatusCodes.Status422UnprocessableEntity,
            "MARKETPLACE_PROVIDER_NOT_SUPPORTED" => StatusCodes.Status409Conflict,
            "COMMERCE_CAPABILITY_UNAVAILABLE" => StatusCodes.Status409Conflict,
            "MARKETPLACE_AUTHORIZATION_NOT_USABLE" => StatusCodes.Status409Conflict,
            "COMMERCE_OPERATION_UNAVAILABLE" => StatusCodes.Status503ServiceUnavailable,
            "LISTING_SYNC_ALREADY_ACTIVE" => StatusCodes.Status409Conflict,
            "LISTING_SYNC_NOTHING_TO_RETRY" => StatusCodes.Status409Conflict,
            _ => throw new InvalidOperationException("LISTING_SYNC_RESULT_UNMAPPED"),
        };
        if (status == StatusCodes.Status503ServiceUnavailable && result.RetryAfterUntil is { } until)
            h.Response.Headers.RetryAfter = Math.Max(1, (long)Math.Ceiling((until - clock.UtcNow).TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var extensions = new Dictionary<string, object?> { ["code"] = result.Code };
        if (result.Reason is not null) { extensions["blockedReason"] = result.Reason; extensions["requiredAction"] = await resolver.ForBlockedReasonAsync(accountId, result.Reason, ct); }
        if (result.ActiveRunId is { } active) extensions["activeRunId"] = active;
        if (result.RetryAfterUntil is { } retryAfter) extensions["retryAfterUntil"] = retryAfter;
        return Results.Problem(statusCode: status, title: result.Code, extensions: extensions);
    }

    private static async Task<ListingSyncRunResponse> RunResponseAsync(MarketplaceListingSyncRunView run, MarketplaceListingSyncCountersView counters, ListingSyncRequiredActionResolver resolver, CancellationToken ct) =>
        new(run.RunId, run.AccountId, run.Kind, run.Status, run.Phase, run.RootRunId, run.RetryOfRunId, run.RequestedAt, run.StartedAt, run.FinishedAt, run.AttemptCount, run.EnumerationCompleted,
            new ListingSyncCountersResponse(counters.Total, counters.Found, counters.BlockingRetryable, counters.BlockingNonRetryable, counters.Pending, counters.ByOutcome, counters.ByOrigin, counters.ListingsCreated, counters.ObservationsAppended),
            run.SafeResultCode, await resolver.ForRunAsync(run, ct));

    private static async Task<IResult> StatusAsync(Guid id, MarketplaceListingSyncService service, VerceDbContext db, ILoggerFactory logs, CancellationToken ct)
    {
        try
        {
            var status = await service.GetStatusAsync(id, ct); var resolver = new ListingSyncRequiredActionResolver(db, logs.CreateLogger<ListingSyncRequiredActionResolver>());
            var availability = status.Availability;
            // Embedded runs carry the same counters as GET run: each DISTINCT run id is read once through the certified GetRunAsync.
            var counters = new Dictionary<Guid, MarketplaceListingSyncCountersView>();
            async Task<ListingSyncRunResponse?> Embedded(MarketplaceListingSyncRunView? run)
            {
                if (run is null) return null;
                if (!counters.TryGetValue(run.RunId, out var runCounters)) counters[run.RunId] = runCounters = (await service.GetRunAsync(run.RunId, ct)).Counters;
                return await RunResponseAsync(run, runCounters, resolver, ct);
            }
            return Results.Ok(new ListingSyncStatusResponse(
                new ListingSyncAvailabilityResponse(availability.CanStart, availability.BlockedReason, await resolver.ForBlockedReasonAsync(id, availability.BlockedReason, ct), availability.RetryAfterUntil, availability.ActiveRunId),
                new ListingSyncSummaryResponse(await Embedded(status.Summary.ActiveRun), await Embedded(status.Summary.LatestRun), await Embedded(status.Summary.LatestFullRun), status.Summary.LastSucceededFullRunAt, status.Summary.OpenBlockingItemCount, status.Summary.Health)));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> RunAsync(Guid runId, MarketplaceListingSyncService service, VerceDbContext db, ILoggerFactory logs, CancellationToken ct)
    {
        try
        {
            var detail = await service.GetRunAsync(runId, ct);
            return Results.Ok(await RunResponseAsync(detail.Run, detail.Counters, new ListingSyncRequiredActionResolver(db, logs.CreateLogger<ListingSyncRequiredActionResolver>()), ct));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> ItemsAsync(Guid runId, MarketplaceListingSyncService service, CancellationToken ct, MarketplaceListingSyncItemOutcome? outcome = null, int page = 1, int pageSize = 25)
    {
        try
        {
            var result = await service.GetItemsAsync(runId, outcome, page, pageSize, ct);
            return Results.Ok(new PagedResponse<ListingSyncRunItemResponse>(result.Items.Select(x => new ListingSyncRunItemResponse(x.ExternalListingId, x.Origin, x.Outcome, x.SafeErrorCode, x.MarketplaceListingId, x.Retryable)).ToList(), result.Page, result.PageSize, result.Total));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }
}
