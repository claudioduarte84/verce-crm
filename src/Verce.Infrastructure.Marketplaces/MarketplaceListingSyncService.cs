using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Verce.Modules.Commerce;
using Verce.Modules.Pricing;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Time;

namespace Verce.Infrastructure.Marketplaces;

/// <summary>Outcome of a start/retry request. Failures carry only the ADR-0025 §G.2 / STATE-MACHINES §6 codes and the §J blockedReason in
/// <see cref="Reason"/>; there is no provider text. <see cref="ActiveRunId"/> accompanies LISTING_SYNC_ALREADY_ACTIVE and
/// <see cref="RetryAfterUntil"/> accompanies RATE_LIMITED. Unknown accounts/runs are not results: they throw <see cref="KeyNotFoundException"/>.</summary>
public sealed record MarketplaceListingSyncRequestResult(bool Succeeded, Guid? RunId, string? Code = null, string? Reason = null, Guid? ActiveRunId = null, DateTimeOffset? RetryAfterUntil = null)
{
    public static MarketplaceListingSyncRequestResult Created(Guid runId) => new(true, runId);
    public static MarketplaceListingSyncRequestResult Refused(string code, string? reason = null, Guid? activeRunId = null, DateTimeOffset? retryAfterUntil = null) => new(false, null, code, reason, activeRunId, retryAfterUntil);
}

public enum MarketplaceListingSyncHealth { NEVER_RUN, IN_PROGRESS, HEALTHY, ATTENTION_REQUIRED, FAILED }

public sealed record MarketplaceListingSyncAvailability(bool CanStart, string? BlockedReason, DateTimeOffset? RetryAfterUntil, Guid? ActiveRunId);

/// <summary>Safe application projection of a run: no lease token/expiry, heartbeat, authorization snapshot, provider text or credential data.</summary>
public sealed record MarketplaceListingSyncRunView(Guid RunId, Guid AccountId, MarketplaceListingSyncKind Kind, MarketplaceListingSyncStatus Status, MarketplaceListingSyncPhase? Phase,
    Guid RootRunId, Guid? RetryOfRunId, DateTimeOffset RequestedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, int AttemptCount, bool EnumerationCompleted, string? SafeResultCode);

public sealed record MarketplaceListingSyncCountersView(int Total, int Found, int BlockingRetryable, int BlockingNonRetryable, int Pending,
    IReadOnlyDictionary<string, int> ByOutcome, IReadOnlyDictionary<string, int> ByOrigin, int ListingsCreated, int ObservationsAppended);

public sealed record MarketplaceListingSyncRunDetail(MarketplaceListingSyncRunView Run, MarketplaceListingSyncCountersView Counters);

public sealed record MarketplaceListingSyncItemView(string ExternalListingId, MarketplaceListingSyncItemOrigin Origin, MarketplaceListingSyncItemOutcome Outcome, string? SafeErrorCode, Guid? MarketplaceListingId, bool Retryable);

/// <summary>Same shape as the repository's paged envelope <c>{items, page, pageSize, total}</c>.</summary>
public sealed record MarketplaceListingSyncPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed record MarketplaceListingSyncSummary(MarketplaceListingSyncRunView? ActiveRun, MarketplaceListingSyncRunView? LatestRun, MarketplaceListingSyncRunView? LatestFullRun,
    DateTimeOffset? LastSucceededFullRunAt, int OpenBlockingItemCount, MarketplaceListingSyncHealth Health);

public sealed record MarketplaceListingSyncStatusView(MarketplaceListingSyncAvailability Availability, MarketplaceListingSyncSummary Summary);

/// <summary>
/// Handoff B5 service. Admission (the §G.2 guards, no provider call) is one shared evaluator used by start, retry and the status availability; the run
/// is staged through the one authoritative kernel implementation and its explicit creation audit joins the SAME unit of work, so a committed run always
/// has exactly its creation audit and a rolled-back one has none. The authorization snapshot is captured at claim (ADR-0025 §E.2), not here. Only after
/// the commit is the immediate dispatch trigger attempted (best effort, never able to undo the run). The read methods are application projections; HTTP
/// exposure and permissions belong to Stream C.
/// </summary>
public sealed class MarketplaceListingSyncService(IUnitOfWork uow, IClock clock, IMarketplaceConnectorRegistry connectors, AmbientOperationContext ambient,
    IServiceScopeFactory scopes, MarketplaceListingSyncDispatchTrigger trigger, IOptions<MarketplaceListingSyncOptions> options)
{
    private const string Capability = "LISTINGS_READ";
    /// <summary>The partial unique index that allows one QUEUED/RUNNING run per account (migration AddS8C2MarketplaceListingSync).</summary>
    public const string ActiveRunIndexName = "ix_marketplace_listing_sync_run_marketplace_account_id";

    public Task<MarketplaceListingSyncRequestResult> StartFullAsync(Guid accountId, Guid actorId, CancellationToken cancellationToken = default)
        => RequestAsync(accountId, null, actorId, cancellationToken);

    /// <summary>Retry targets are never supplied by the caller: the kernel derives them once from the source run's durable retryable items.</summary>
    public Task<MarketplaceListingSyncRequestResult> RetryAsync(Guid runId, Guid actorId, CancellationToken cancellationToken = default)
        => RequestAsync(null, runId, actorId, cancellationToken);

    private async Task<MarketplaceListingSyncRequestResult> RequestAsync(Guid? accountId, Guid? sourceRunId, Guid actorId, CancellationToken ct)
    {
        if (actorId == Guid.Empty) throw new ArgumentException("LISTING_SYNC_ACTOR_REQUIRED", nameof(actorId));
        Guid? resolvedAccount = accountId; MarketplaceListingSyncRequestResult result;
        try
        {
            result = await uow.ExecuteAsync(async (db, inner) =>
            {
                db.ChangeTracker.Clear();
                var now = clock.UtcNow; var kernel = new MarketplaceListingSyncRunKernel(db); MarketplaceListingSyncRetryPlan? plan = null;
                if (sourceRunId is { } source)
                {
                    // Frozen order: source exists -> eligibility (one derivation) -> guards -> active run. Nothing is staged before every refusal decision.
                    plan = await kernel.PlanRetryAsync(source, inner) ?? throw new KeyNotFoundException("LISTING_SYNC_RUN");
                    if (!plan.Eligible) return MarketplaceListingSyncRequestResult.Refused("LISTING_SYNC_NOTHING_TO_RETRY");
                    resolvedAccount = plan.Source.MarketplaceAccountId;
                }
                var admission = await AdmitAsync(db, resolvedAccount!.Value, now, inner);
                if (!admission.Allowed) return MarketplaceListingSyncRequestResult.Refused(admission.Code!, admission.Reason, admission.ActiveRunId, admission.RetryAfterUntil);
                var run = plan is null ? await kernel.StageFullAsync(resolvedAccount.Value, actorId, now, inner, options.Value.MaxAttempts) : kernel.StageRetry(plan, actorId, now, options.Value.MaxAttempts);
                db.Add(MarketplaceListingSyncRunKernel.RequestedAudit(run, actorId, ambient.ActorDisplayName, now, ambient.OperationStartedAtUtc, ambient.CorrelationId, ambient.RequestId, ambient.Source));
                return MarketplaceListingSyncRequestResult.Created(run.Id);
            }, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: ActiveRunIndexName } && resolvedAccount is { } account)
        {
            // Two requests passed the pre-check together and the partial unique index let one win. The failed context is discarded (rule 19): a fresh
            // scope/context reads the winner. If it already terminalized there is nothing honest to return, so the original failure propagates.
            await using var scope = scopes.CreateAsyncScope();
            var winner = await ActiveRunAsync(scope.ServiceProvider.GetRequiredService<VerceDbContext>(), account, ct);
            if (winner is null) throw;
            return MarketplaceListingSyncRequestResult.Refused("LISTING_SYNC_ALREADY_ACTIVE", "SYNC_ACTIVE", activeRunId: winner);
        }
        // Best effort and strictly after the commit: the trigger can never undo, alter or re-audit the committed run.
        if (result.Succeeded) await trigger.TryTriggerAsync(result.RunId!.Value, ct);
        return result;
    }

    // ---- reads (application projections; no provider call, no permission checks) ----

    public async Task<MarketplaceListingSyncRunDetail> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
        => await uow.ExecuteAsync(async (db, ct) =>
        {
            db.ChangeTracker.Clear();
            var run = await db.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == runId, ct) ?? throw new KeyNotFoundException("LISTING_SYNC_RUN");
            return new MarketplaceListingSyncRunDetail(ToView(run), await CountersAsync(db, runId, ct));
        }, cancellationToken);

    public async Task<MarketplaceListingSyncPage<MarketplaceListingSyncItemView>> GetItemsAsync(Guid runId, MarketplaceListingSyncItemOutcome? outcome, int page, int pageSize, CancellationToken cancellationToken = default)
        => await uow.ExecuteAsync(async (db, ct) =>
        {
            db.ChangeTracker.Clear();
            if (!await db.Set<MarketplaceListingSyncRun>().AsNoTracking().AnyAsync(x => x.Id == runId, ct)) throw new KeyNotFoundException("LISTING_SYNC_RUN");
            page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, 100);
            var query = db.Set<MarketplaceListingSyncRunItem>().AsNoTracking().Where(x => x.SyncRunId == runId);
            if (outcome is { } filter) query = query.Where(x => x.Outcome == filter);
            var total = await query.CountAsync(ct);
            var rows = await query.OrderBy(x => x.ExternalListingId).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            return new MarketplaceListingSyncPage<MarketplaceListingSyncItemView>(rows.Select(x => new MarketplaceListingSyncItemView(x.ExternalListingId, x.Origin, x.Outcome, x.SafeErrorCode, x.MarketplaceListingId, MarketplaceListingSyncKernel.IsRetryable(x.Outcome))).ToList(), page, pageSize, total);
        }, cancellationToken);

    /// <summary>Availability comes from the same admission evaluator as StartFullAsync; the summary is ADR-0025 §E.8, derived from runs and items only
    /// (never from connection runtime).</summary>
    public async Task<MarketplaceListingSyncStatusView> GetStatusAsync(Guid accountId, CancellationToken cancellationToken = default)
        => await uow.ExecuteAsync(async (db, ct) =>
        {
            db.ChangeTracker.Clear();
            var admission = await AdmitAsync(db, accountId, clock.UtcNow, ct);
            var runs = db.Set<MarketplaceListingSyncRun>().AsNoTracking().Where(x => x.MarketplaceAccountId == accountId);
            var active = await runs.Where(x => x.Status == MarketplaceListingSyncStatus.QUEUED || x.Status == MarketplaceListingSyncStatus.RUNNING).OrderByDescending(x => x.RequestedAt).FirstOrDefaultAsync(ct);
            var latest = await runs.OrderByDescending(x => x.RequestedAt).FirstOrDefaultAsync(ct);
            var latestFull = await runs.Where(x => x.Kind == MarketplaceListingSyncKind.FULL).OrderByDescending(x => x.RequestedAt).FirstOrDefaultAsync(ct);
            var lastSucceeded = await runs.Where(x => x.Kind == MarketplaceListingSyncKind.FULL && x.Status == MarketplaceListingSyncStatus.SUCCEEDED).OrderByDescending(x => x.RequestedAt).Select(x => x.FinishedAt).FirstOrDefaultAsync(ct);
            var open = 0;
            if (latestFull is not null)
            {
                var laterRuns = db.Set<MarketplaceListingSyncRun>().AsNoTracking().Where(r => r.RootRunId == latestFull.RootRunId && r.RequestedAt > latestFull.RequestedAt).Select(r => r.Id);
                var items = db.Set<MarketplaceListingSyncRunItem>().AsNoTracking();
                open = await items.Where(i => i.SyncRunId == latestFull.Id && i.Outcome != MarketplaceListingSyncItemOutcome.PENDING && i.Outcome != MarketplaceListingSyncItemOutcome.FOUND
                    && !items.Any(j => laterRuns.Contains(j.SyncRunId) && j.ExternalListingId == i.ExternalListingId && j.Outcome == MarketplaceListingSyncItemOutcome.FOUND)).CountAsync(ct);
            }
            var health = active is not null ? MarketplaceListingSyncHealth.IN_PROGRESS
                : latestFull is null ? MarketplaceListingSyncHealth.NEVER_RUN
                : latestFull.Status == MarketplaceListingSyncStatus.SUCCEEDED ? MarketplaceListingSyncHealth.HEALTHY
                : latestFull.Status == MarketplaceListingSyncStatus.PARTIAL ? (open == 0 ? MarketplaceListingSyncHealth.HEALTHY : MarketplaceListingSyncHealth.ATTENTION_REQUIRED)
                : MarketplaceListingSyncHealth.FAILED;
            return new MarketplaceListingSyncStatusView(new(admission.Allowed, admission.Reason, admission.RetryAfterUntil, admission.ActiveRunId),
                new(active is null ? null : ToView(active), latest is null ? null : ToView(latest), latestFull is null ? null : ToView(latestFull), lastSucceeded, open, health));
        }, cancellationToken);

    private static MarketplaceListingSyncRunView ToView(MarketplaceListingSyncRun x) => new(x.Id, x.MarketplaceAccountId, x.Kind, x.Status, x.Phase, x.RootRunId, x.RetryOfRunId, x.RequestedAt, x.StartedAt, x.FinishedAt, x.AttemptCount, x.EnumerationCompleted, x.SafeResultCode);

    private static async Task<MarketplaceListingSyncCountersView> CountersAsync(VerceDbContext db, Guid runId, CancellationToken ct)
    {
        var groups = await db.Set<MarketplaceListingSyncRunItem>().AsNoTracking().Where(x => x.SyncRunId == runId)
            .GroupBy(x => new { x.Outcome, x.Origin }).Select(g => new { g.Key.Outcome, g.Key.Origin, Count = g.Count(), Created = g.Count(x => x.ListingCreated), Appended = g.Count(x => x.ObservationAppended) }).ToListAsync(ct);
        var byOutcome = groups.GroupBy(g => g.Outcome).ToDictionary(g => g.Key.ToString(), g => g.Sum(x => x.Count));
        var byOrigin = groups.GroupBy(g => g.Origin).ToDictionary(g => g.Key.ToString(), g => g.Sum(x => x.Count));
        int Of(Func<MarketplaceListingSyncItemOutcome, bool> predicate) => groups.Where(g => predicate(g.Outcome)).Sum(g => g.Count);
        return new(groups.Sum(g => g.Count), Of(o => o == MarketplaceListingSyncItemOutcome.FOUND), Of(MarketplaceListingSyncKernel.IsRetryable),
            Of(o => o is MarketplaceListingSyncItemOutcome.SELLER_MISMATCH or MarketplaceListingSyncItemOutcome.PERMANENT_ERROR), Of(o => o == MarketplaceListingSyncItemOutcome.PENDING),
            byOutcome, byOrigin, groups.Sum(g => g.Created), groups.Sum(g => g.Appended));
    }

    // ---- shared §G.2 admission evaluator ----

    private sealed record Admission(string? Code, string? Reason = null, Guid? ActiveRunId = null, DateTimeOffset? RetryAfterUntil = null)
    {
        public bool Allowed => Code is null;
        public static Admission Ok => new((string?)null);
    }

    private static Task<Guid?> ActiveRunAsync(VerceDbContext db, Guid accountId, CancellationToken ct) =>
        db.Set<MarketplaceListingSyncRun>().AsNoTracking()
            .Where(x => x.MarketplaceAccountId == accountId && (x.Status == MarketplaceListingSyncStatus.QUEUED || x.Status == MarketplaceListingSyncStatus.RUNNING))
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);

    /// <summary>ADR-0025 §G.2 in the frozen order, no provider I/O. An unknown account throws KeyNotFoundException (not a refusal code).</summary>
    private async Task<Admission> AdmitAsync(VerceDbContext db, Guid accountId, DateTimeOffset now, CancellationToken ct)
    {
        var account = await db.Set<MarketplaceAccount>().AsNoTracking().Include(x => x.Connection).Include(x => x.Capabilities).SingleOrDefaultAsync(x => x.Id == accountId, ct)
            ?? throw new KeyNotFoundException("MARKETPLACE_ACCOUNT");
        if (!account.Active) return new("MARKETPLACE_ACCOUNT_INACTIVE", "ACCOUNT_INACTIVE");
        if (!await db.Set<SalesChannel>().AsNoTracking().AnyAsync(x => x.Id == account.SalesChannelId && x.Active && x.Kind == SalesChannelKind.Marketplace, ct)) return new("MARKETPLACE_ACCOUNT_CHANNEL_INVALID", "CHANNEL_INVALID");
        if (!connectors.TryGetListingReader(account.ProviderCode, out _)) return new("MARKETPLACE_PROVIDER_NOT_SUPPORTED", "PROVIDER_NOT_CONFIGURED");
        var structural = await db.Set<MarketplaceProviderCapability>().AsNoTracking().Where(x => x.ProviderCode == account.ProviderCode && x.CapabilityCode == Capability).Select(x => (ProviderCapabilityState?)x.State).SingleOrDefaultAsync(ct);
        if (structural != ProviderCapabilityState.SUPPORTED) return new("COMMERCE_CAPABILITY_UNAVAILABLE", "CAPABILITY_UNSUPPORTED");
        var grant = account.Capabilities.Where(x => x.CapabilityCode == Capability).Select(x => (AccountCapabilityState?)x.State).SingleOrDefault();
        if (grant != AccountCapabilityState.GRANTED) return new("COMMERCE_CAPABILITY_UNAVAILABLE", grant == AccountCapabilityState.DENIED ? "GRANT_DENIED" : "GRANT_UNKNOWN");
        var connection = account.Connection;
        if (connection.AuthorizationState != MarketplaceAuthorizationState.CONNECTED || account.CredentialReference is null || connection.ConfirmedCredentialVersion is null || connection.ConfirmedOperationId is null
            || await MarketplaceOperationFences.IsBlockedAsync(db, accountId, account.ProviderCode, ct))
            return new("MARKETPLACE_AUTHORIZATION_NOT_USABLE", "AUTHORIZATION_NOT_USABLE");
        if (connection.RuntimeRetryAfterUntil is { } retryAfter && retryAfter > now) return new("COMMERCE_OPERATION_UNAVAILABLE", "RATE_LIMITED", RetryAfterUntil: retryAfter);
        if (connection.RuntimeAvailability == MarketplaceRuntimeAvailability.UNAVAILABLE) return new("COMMERCE_OPERATION_UNAVAILABLE", "RUNTIME_UNAVAILABLE");
        if (await ActiveRunAsync(db, accountId, ct) is { } active) return new("LISTING_SYNC_ALREADY_ACTIVE", "SYNC_ACTIVE", ActiveRunId: active);
        return Admission.Ok;
    }
}
