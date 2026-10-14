using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using Verce.Modules.Commerce;
using Verce.Platform.Persistence;
using Verce.SharedKernel.Time;

namespace Verce.Infrastructure.Marketplaces;

/// <summary>The single owner of the listing-sync dispatch job identity (handoff B5).</summary>
public static class MarketplaceListingSyncDispatchJobKeys
{
    public static readonly JobKey Job = new("MarketplaceListingSyncDispatchJob", "marketplaces");
}

/// <summary>
/// Best-effort immediate dispatch after a run was committed (handoff B5). It never throws, never touches the run or its audit and never retries:
/// the recurring dispatch job is the fallback. It is a no-op when scheduling is disabled, no scheduler factory exists or the job is not registered.
/// </summary>
public sealed class MarketplaceListingSyncDispatchTrigger(IServiceProvider services, IOptions<MarketplaceListingSyncOptions> options, ILogger<MarketplaceListingSyncDispatchTrigger> logger)
{
    public async Task TryTriggerAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (!options.Value.SchedulingEnabled) return;
        try
        {
            var factory = services.GetService<ISchedulerFactory>(); if (factory is null) return;
            var scheduler = await factory.GetScheduler(cancellationToken);
            if (!await scheduler.CheckExists(MarketplaceListingSyncDispatchJobKeys.Job, cancellationToken)) return;
            await scheduler.TriggerJob(MarketplaceListingSyncDispatchJobKeys.Job, cancellationToken);
        }
        catch (OperationCanceledException) { /* the run is already committed; the recurring dispatcher will pick it up */ }
        catch (Exception ex) { logger.LogWarning("Listing sync dispatch trigger failed for run {RunId} ({ExceptionType}); the recurring dispatcher will pick it up", runId, ex.GetType().Name); }
    }
}

/// <summary>
/// Singleton host for claimed executions (handoff B5): one Task and one DI scope per run. A failed, cancelled or lease-lost execution never crashes the
/// host; host stop cancels executions and leaves their runs RUNNING so lease expiry makes them reclaimable (no synthetic finish).
/// </summary>
public sealed class MarketplaceListingSyncExecutor(IServiceScopeFactory scopes, ILogger<MarketplaceListingSyncExecutor> logger) : IHostedService
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    /// <summary>Number of executions currently in flight in this process.</summary>
    public int Executing => _running.Count;

    public bool TryStart(MarketplaceListingSyncLease lease)
    {
        if (_stopping.IsCancellationRequested) return false;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_running.TryAdd(lease.RunId, done.Task)) return false;
        _ = Task.Run(async () =>
        {
            try { await ExecuteAsync(lease); }
            finally { _running.TryRemove(lease.RunId, out _); done.SetResult(); }
        });
        return true;
    }

    private async Task ExecuteAsync(MarketplaceListingSyncLease lease)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<MarketplaceListingSyncExecution>().RunClaimedAsync(lease, _stopping.Token);
        }
        catch (MarketplaceListingSyncLeaseLostException) { logger.LogInformation("LISTING_SYNC_LEASE_LOST run {RunId}", lease.RunId); }
        catch (OperationCanceledException) { logger.LogInformation("Listing sync execution cancelled for run {RunId}; it stays reclaimable", lease.RunId); }
        catch (Exception ex) { logger.LogWarning("Listing sync execution failed for run {RunId} ({ExceptionType})", lease.RunId, ex.GetType().Name); }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        await Task.WhenAll(_running.Values.ToArray()).WaitAsync(cancellationToken);
    }
}

/// <summary>
/// One dispatch pass (handoff B4/B5, ADR-0025 §E.1/§E.2): terminalize exhausted lease-expired runs with the stale-token compare-and-swap, then claim due
/// runs by requested_at up to the free capacity and hand each lease to the executor host. It makes no provider call, touches no authorization/runtime and
/// writes audit only for the exhausted-run terminalization. Failures are isolated per run.
/// </summary>
public sealed class MarketplaceListingSyncDispatcher(VerceDbContext db, IClock clock, IOptions<MarketplaceListingSyncOptions> options, MarketplaceListingSyncExecutor executor, ILogger<MarketplaceListingSyncDispatcher> logger)
{
    private const int ExhaustedBatch = 100;
    public const string ExhaustedCode = "RUN_EXECUTION_ATTEMPTS_EXHAUSTED";

    /// <summary>Returns the number of executions started.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await TerminalizeExhaustedAsync(cancellationToken);
        return await ClaimDueAsync(cancellationToken);
    }

    private async Task TerminalizeExhaustedAsync(CancellationToken ct)
    {
        db.ChangeTracker.Clear(); var now = clock.UtcNow;
        var stale = await db.Set<MarketplaceListingSyncRun>().AsNoTracking()
            .Where(x => x.Status == MarketplaceListingSyncStatus.RUNNING && x.LeaseUntil < now && x.AttemptCount >= x.MaxAttempts && x.LeaseToken != null)
            .OrderBy(x => x.RequestedAt).Take(ExhaustedBatch).Select(x => new { x.Id, x.LeaseToken }).ToListAsync(ct);
        foreach (var candidate in stale)
        {
            try { await TerminalizeAsync(candidate.Id, candidate.LeaseToken!.Value, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning("Listing sync exhausted-run terminalization failed for run {RunId} ({ExceptionType})", candidate.Id, ex.GetType().Name); }
        }
    }

    /// <summary>Compare-and-swap on the stale token the dispatcher read; zero rows means someone reclaimed or finished the run, so nothing else is written.</summary>
    private async Task<bool> TerminalizeAsync(Guid runId, Guid observedToken, CancellationToken ct)
    {
        if (MarketplaceListingSyncExecutionTestHooks.BeforeExhaustedTerminalizationAsync is { } before) { MarketplaceListingSyncExecutionTestHooks.BeforeExhaustedTerminalizationAsync = null; await before(runId, ct); }
        db.ChangeTracker.Clear(); var now = clock.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var swapped = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE commerce.marketplace_listing_sync_run
                   SET status = {"FAILED"}, safe_result_code = {ExhaustedCode}, lease_token = NULL, lease_until = NULL, heartbeat_at = NULL, phase = NULL,
                       finished_at = {now}, updated_at = {now}, version = version + 1
                 WHERE id = {runId} AND lease_token = {observedToken} AND status = {"RUNNING"} AND lease_until < {now}
                """, ct);
            if (swapped != 1) { await tx.RollbackAsync(ct); return false; }
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE commerce.marketplace_listing_sync_run_item
                   SET outcome = {"TRANSIENT_ERROR"}, safe_error_code = {"RUN_ENDED_BEFORE_ITEM_PROCESSED"}, completed_at = {now}, updated_at = {now}, attempt_count = attempt_count + 1
                 WHERE sync_run_id = {runId} AND outcome = {"PENDING"}
                """, ct);
            var run = await db.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId, ct);
            var counters = MarketplaceListingSyncKernel.Count(await db.Set<MarketplaceListingSyncRunItem>().AsNoTracking().Where(x => x.SyncRunId == runId).ToListAsync(ct));
            db.Add(MarketplaceListingSyncRunKernel.FinishedAudit(run, MarketplaceListingSyncStatus.FAILED, ExhaustedCode, counters, now));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }
        catch { await tx.RollbackAsync(CancellationToken.None); throw; }
    }

    private async Task<int> ClaimDueAsync(CancellationToken ct)
    {
        db.ChangeTracker.Clear(); var settings = options.Value;
        var available = settings.MaxConcurrentRuns - executor.Executing; if (available <= 0) return 0;
        var now = clock.UtcNow;
        var due = await db.Set<MarketplaceListingSyncRun>().AsNoTracking()
            .Where(x => (x.Status == MarketplaceListingSyncStatus.QUEUED || (x.Status == MarketplaceListingSyncStatus.RUNNING && x.LeaseUntil < now)) && x.AttemptCount < x.MaxAttempts)
            .OrderBy(x => x.RequestedAt).Take(available).Select(x => x.Id).ToListAsync(ct);
        var started = 0; var authority = new MarketplaceListingSyncLeaseAuthority(db);
        foreach (var runId in due)
        {
            try
            {
                if (await authority.ClaimOrReclaimAsync(runId, clock.UtcNow, TimeSpan.FromSeconds(settings.LeaseSeconds), ct) is not { } lease) continue;
                if (executor.TryStart(lease)) started++; else logger.LogWarning("Listing sync run {RunId} was claimed but is already executing in this process", runId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning("Listing sync claim failed for run {RunId} ({ExceptionType})", runId, ex.GetType().Name); }
        }
        return started;
    }
}

[DisallowConcurrentExecution]
public sealed class MarketplaceListingSyncDispatchJob(MarketplaceListingSyncDispatcher dispatcher) : IJob
{
    public Task Execute(IJobExecutionContext context) => dispatcher.RunOnceAsync(context.CancellationToken);
}

public static class MarketplaceListingSyncSchedulingExtensions
{
    /// <summary>Registers the durable dispatch job and its recurring trigger only when <c>Marketplaces:ListingSync:SchedulingEnabled</c> is true. The executor
    /// host, dispatcher and service stay resolvable either way (tests drive the dispatcher directly). The job only reads VERCE's run table (no provider polling).</summary>
    public static IServiceCollection AddVerceMarketplaceListingSyncScheduling(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = new MarketplaceListingSyncOptions();
        configuration.GetSection(MarketplaceListingSyncOptions.SectionName).Bind(settings);
        if (!settings.SchedulingEnabled) return services;
        services.AddQuartz(q =>
        {
            q.AddJob<MarketplaceListingSyncDispatchJob>(j => j.WithIdentity(MarketplaceListingSyncDispatchJobKeys.Job).StoreDurably());
            q.AddTrigger(t => t
                .ForJob(MarketplaceListingSyncDispatchJobKeys.Job)
                .WithIdentity("MarketplaceListingSyncDispatchTrigger", "marketplaces")
                .WithSimpleSchedule(s => s.WithIntervalInSeconds(settings.DispatchIntervalSeconds).RepeatForever())
                .StartNow());
        });
        return services;
    }
}
