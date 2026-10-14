using Microsoft.EntityFrameworkCore;
using Verce.Platform.Persistence;

namespace Verce.Modules.Commerce;

public sealed record MarketplaceListingSyncLease(Guid RunId, Guid LeaseToken, DateTimeOffset LeaseUntil);
public sealed class MarketplaceListingSyncLeaseLostException : InvalidOperationException
{ public MarketplaceListingSyncLeaseLostException() : base("LISTING_SYNC_LEASE_LOST") { } }

/// <summary>G02's sole persistence authority for execution-owned writes. Its SQL guard and the
/// caller's write share one PostgreSQL transaction, so a superseded lease cannot pass a prior
/// check and commit afterwards.</summary>
public sealed class MarketplaceListingSyncLeaseAuthority(VerceDbContext db)
{
    public async Task<MarketplaceListingSyncLease?> ClaimOrReclaimAsync(Guid runId,DateTimeOffset now,TimeSpan lease,CancellationToken ct=default)
        => await AcquireAsync(runId,now,lease,AcquireMode.QueuedOrExpired,ct);

    public async Task<MarketplaceListingSyncLease?> ClaimAsync(Guid runId,DateTimeOffset now,TimeSpan lease,CancellationToken ct=default)
        => await AcquireAsync(runId,now,lease,AcquireMode.Queued,ct);

    public async Task<MarketplaceListingSyncLease?> ReclaimAsync(Guid runId,DateTimeOffset now,TimeSpan lease,CancellationToken ct=default)
        => await AcquireAsync(runId,now,lease,AcquireMode.Expired,ct);

    private enum AcquireMode { Queued, Expired, QueuedOrExpired }

    private async Task<MarketplaceListingSyncLease?> AcquireAsync(Guid runId,DateTimeOffset now,TimeSpan lease,AcquireMode mode,CancellationToken ct)
    {
        if(lease<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(lease));
        var token=Guid.CreateVersion7();var until=now.Add(lease);
        var affected=mode switch
        {
            AcquireMode.Queued => await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE commerce.marketplace_listing_sync_run
                   SET status = {"RUNNING"}, lease_token = {token}, lease_until = {until}, heartbeat_at = {now}, attempt_count = attempt_count + 1, started_at = COALESCE(started_at, {now}), confirmed_operation_id_at_start = (SELECT confirmed_operation_id FROM commerce.marketplace_account_connection WHERE marketplace_account_id = commerce.marketplace_listing_sync_run.marketplace_account_id), version = version + 1, updated_at = {now}
                 WHERE id = {runId} AND status = {"QUEUED"} AND attempt_count < max_attempts
                """,ct),
            AcquireMode.Expired => await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE commerce.marketplace_listing_sync_run
                   SET status = {"RUNNING"}, lease_token = {token}, lease_until = {until}, heartbeat_at = {now}, attempt_count = attempt_count + 1, started_at = COALESCE(started_at, {now}), confirmed_operation_id_at_start = (SELECT confirmed_operation_id FROM commerce.marketplace_account_connection WHERE marketplace_account_id = commerce.marketplace_listing_sync_run.marketplace_account_id), version = version + 1, updated_at = {now}
                 WHERE id = {runId} AND status = {"RUNNING"} AND lease_until < {now} AND attempt_count < max_attempts
                """,ct),
            _ => await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE commerce.marketplace_listing_sync_run
                   SET status = {"RUNNING"}, lease_token = {token}, lease_until = {until}, heartbeat_at = {now}, attempt_count = attempt_count + 1, started_at = COALESCE(started_at, {now}), confirmed_operation_id_at_start = (SELECT confirmed_operation_id FROM commerce.marketplace_account_connection WHERE marketplace_account_id = commerce.marketplace_listing_sync_run.marketplace_account_id), version = version + 1, updated_at = {now}
                 WHERE id = {runId} AND (status = {"QUEUED"} OR (status = {"RUNNING"} AND lease_until < {now})) AND attempt_count < max_attempts
                """,ct)
        };
        return affected==1?new(runId,token,until):null;
    }

    public async Task<bool> HeartbeatAsync(Guid runId,Guid leaseToken,DateTimeOffset now,TimeSpan lease,CancellationToken ct=default)
    {if(lease<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(lease));var affected=await db.Database.ExecuteSqlInterpolatedAsync($"""
        UPDATE commerce.marketplace_listing_sync_run
           SET heartbeat_at = {now}, lease_until = {now.Add(lease)}, updated_at = {now}
         WHERE id = {runId} AND lease_token = {leaseToken} AND status = {"RUNNING"}
        """,ct);return affected==1;}

    public async Task ExecuteWithLeaseAsync(Guid runId,Guid leaseToken,DateTimeOffset now,TimeSpan lease,Func<CancellationToken,Task> write,CancellationToken ct=default)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        try
        {
            var guarded=await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE commerce.marketplace_listing_sync_run
                   SET heartbeat_at = {now}, lease_until = {now.Add(lease)}, updated_at = {now}
                 WHERE id = {runId} AND lease_token = {leaseToken} AND status = {"RUNNING"}
                """,ct);
            if(guarded!=1)throw new MarketplaceListingSyncLeaseLostException();
            // The guard is raw SQL. Discard any pre-guard tracked snapshot so the execution
            // callback can only observe the lease-bearing RUNNING row from this transaction.
            db.ChangeTracker.Clear();
            await write(ct);
            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }
}
