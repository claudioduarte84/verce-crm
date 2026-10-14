using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Verce.Platform.Audit;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;

namespace Verce.Modules.Commerce;

public sealed class MarketplaceListingSyncAuthorizationSnapshotException : InvalidOperationException { public MarketplaceListingSyncAuthorizationSnapshotException() : base("LISTING_SYNC_AUTHORIZATION_SNAPSHOT_CHANGED") { } }
public sealed class MarketplaceListingSyncTargetLimitException : InvalidOperationException { public MarketplaceListingSyncTargetLimitException() : base("LISTING_VOLUME_EXCEEDS_LIMIT") { } }
public enum MarketplaceListingSyncRuntimeEffect { None, Available, ProviderUnavailable, ProviderRateLimited }
public sealed record MarketplaceListingSyncFinishContext(MarketplaceListingSyncRuntimeEffect RuntimeEffect, string? FailureClassification = null, DateTimeOffset? RetryAfterUntil = null) { public static readonly MarketplaceListingSyncFinishContext None = new(MarketplaceListingSyncRuntimeEffect.None); }
public sealed record MarketplaceListingSyncCounters(int Total,int Found,int BlockingRetryable,int BlockingNonRetryable,int Pending,int ListingsCreated,int ObservationsAppended) { public int Blocking => BlockingRetryable+BlockingNonRetryable; }

public static class MarketplaceListingSyncKernel
{
    private static readonly HashSet<MarketplaceListingSyncItemOutcome> Retryable=[MarketplaceListingSyncItemOutcome.NOT_FOUND,MarketplaceListingSyncItemOutcome.ACCESS_DENIED,MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR];
    public static bool IsRetryable(MarketplaceListingSyncItemOutcome outcome)=>Retryable.Contains(outcome);
    public static string RequiredSafeCode(string value){var code=value?.Trim()??string.Empty;if(code.Length is <1 or >64)throw new ArgumentException("LISTING_SYNC_SAFE_CODE_INVALID");return code;}
    public static MarketplaceListingSyncCounters Count(IEnumerable<MarketplaceListingSyncRunItem> items){var all=items.ToArray();var found=all.Count(x=>x.Outcome==MarketplaceListingSyncItemOutcome.FOUND);var retryable=all.Count(x=>IsRetryable(x.Outcome));var nonRetryable=all.Count(x=>x.Outcome is MarketplaceListingSyncItemOutcome.SELLER_MISMATCH or MarketplaceListingSyncItemOutcome.PERMANENT_ERROR);return new(all.Length,found,retryable,nonRetryable,all.Count(x=>x.Outcome==MarketplaceListingSyncItemOutcome.PENDING),all.Count(x=>x.ListingCreated),all.Count(x=>x.ObservationAppended));}
    public static (MarketplaceListingSyncStatus Status,string SafeResultCode) Compute(MarketplaceListingSyncKind kind,bool enumerationCompleted,MarketplaceListingSyncCounters counters,string? stopReason){if(!string.IsNullOrWhiteSpace(stopReason)&&kind==MarketplaceListingSyncKind.FULL&&!enumerationCompleted)return(MarketplaceListingSyncStatus.FAILED,RequiredSafeCode(stopReason));if(counters.Found==0&&(counters.Blocking>0||!string.IsNullOrWhiteSpace(stopReason)))return(MarketplaceListingSyncStatus.FAILED,RequiredSafeCode(stopReason??"LISTING_SYNC_NO_SUCCESSFUL_ITEMS"));if(counters.Blocking==0&&counters.Pending==0&&string.IsNullOrWhiteSpace(stopReason))return(MarketplaceListingSyncStatus.SUCCEEDED,"LISTING_SYNC_COMPLETED");return(MarketplaceListingSyncStatus.PARTIAL,"LISTING_SYNC_PARTIAL");}
}

public sealed class MarketplaceListingSyncRunKernel(VerceDbContext db)
{
    public const int MaxTargetsPerRun = 200_000;
    /// <summary>ADR-0025: known-not-enumerated registration is one transaction per batch of at most 500;
    /// the caller loops until a batch returns 0. Distinct from the global <see cref="MaxTargetsPerRun"/> ceiling.</summary>
    public const int KnownNotEnumeratedBatchSize = 500;
    /// <summary>Stages (does not save) the one authoritative FULL run: QUEUED, no snapshot yet (captured at claim, ADR-0025 §E.2).
    /// Callers own the transaction/save, so a creation audit can commit in the same unit of work (B5).</summary>
    public async Task<MarketplaceListingSyncRun> StageFullAsync(Guid accountId,Guid actorId,DateTimeOffset now,CancellationToken ct=default,int maxAttempts=3){var account=await db.Set<MarketplaceAccount>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==accountId,ct)??throw new ArgumentException("MARKETPLACE_ACCOUNT_NOT_FOUND");var run=new MarketplaceListingSyncRun(accountId,account.ProviderCode,actorId,now,maxAttempts);db.Add(run);return run;}
    public async Task<MarketplaceListingSyncRun> CreateFullAsync(Guid accountId,Guid actorId,DateTimeOffset now,CancellationToken ct=default){var run=await StageFullAsync(accountId,actorId,now,ct);await db.SaveChangesAsync(ct);return run;}
    public async Task<MarketplaceListingSyncCounters> GetCountersAsync(Guid runId,CancellationToken ct=default)=>MarketplaceListingSyncKernel.Count(await db.Set<MarketplaceListingSyncRunItem>().Where(x=>x.SyncRunId==runId).ToListAsync(ct));
    /// <summary>Sentinel the execution passes when the run row carries no authorization snapshot, so a missing
    /// snapshot can never be mistaken for the null lease-only seam used by the B1 fencing tests.</summary>
    public static readonly Guid NoSnapshot=Guid.Empty;
    public const string FinishedAuditEvent="LISTING_SYNC_FINISHED";
    public const string RequestedAuditEvent="LISTING_SYNC_REQUESTED";
    public const string RetryRequestedAuditEvent="LISTING_SYNC_RETRY_REQUESTED";
    /// <summary>ADR-0025 §K / handoff B5: explicit allowlisted creation audit for a newly staged run (FULL =&gt; LISTING_SYNC_REQUESTED,
    /// FAILED_ONLY =&gt; LISTING_SYNC_RETRY_REQUESTED). Same representation as the FINISHED row (entity = the run, operation MODIFIED, the frozen
    /// event name in the allowlisted JSON). A creation fact has no counters or result code, so only event, kind and status are written.
    /// The caller adds it to the same context/transaction that persists the run, so both commit or roll back together.</summary>
    public static AuditLogEntry RequestedAudit(MarketplaceListingSyncRun run,Guid actorUserId,string? actorDisplayName,DateTimeOffset now,DateTimeOffset operationStartedAt,Guid correlationId,Guid? requestId,AuditSource source)
    {
        var json=JsonSerializer.Serialize(new Dictionary<string,object>{["event"]=run.Kind==MarketplaceListingSyncKind.FULL?RequestedAuditEvent:RetryRequestedAuditEvent,["kind"]=run.Kind.ToString(),["status"]=run.Status.ToString()});
        return new AuditLogEntry(occurredAt:now,operationStartedAt:operationStartedAt,userId:actorUserId,userDisplayName:actorDisplayName,
            entitySchema:"commerce",entityTable:"marketplace_listing_sync_run",entityId:run.Id,operation:"MODIFIED",changedColumns:null,oldValuesJson:null,newValuesJson:json,
            correlationId:correlationId,requestId:requestId,waveIndex:1,source:source);
    }
    /// <summary>ADR-0025 §K: explicit allowlisted LISTING_SYNC_FINISHED audit row for the run entity. It is added to the same
    /// lease-fenced context as the terminal transition, so both commit or roll back together. platform.audit_log.operation is
    /// varchar(16), so the frozen event name travels in the allowlisted JSON (<c>event</c>) and the operation is MODIFIED
    /// (the run row's terminal transition). Only event, kind, status, counters and the safe result code are written — never
    /// provider text, exceptions, credentials, references or account identifiers.</summary>
    public static AuditLogEntry FinishedAudit(MarketplaceListingSyncRun run,MarketplaceListingSyncStatus status,string safeResultCode,MarketplaceListingSyncCounters counters,DateTimeOffset now)
    {
        var json=JsonSerializer.Serialize(new Dictionary<string,object>{
            ["event"]=FinishedAuditEvent,["kind"]=run.Kind.ToString(),["status"]=status.ToString(),["safeResultCode"]=safeResultCode,
            ["counters"]=new Dictionary<string,int>{["total"]=counters.Total,["found"]=counters.Found,["blockingRetryable"]=counters.BlockingRetryable,["blockingNonRetryable"]=counters.BlockingNonRetryable,["pending"]=counters.Pending,["listingsCreated"]=counters.ListingsCreated,["observationsAppended"]=counters.ObservationsAppended}});
        return new AuditLogEntry(occurredAt:now,operationStartedAt:run.StartedAt??now,userId:null,userDisplayName:"JOB:listing-sync",
            entitySchema:"commerce",entityTable:"marketplace_listing_sync_run",entityId:run.Id,operation:"MODIFIED",changedColumns:null,oldValuesJson:null,newValuesJson:json,
            correlationId:Guid.CreateVersion7(),requestId:null,waveIndex:1,source:AuditSource.Job);
    }
    /// <summary>
    /// The single authority predicate (B2 authority contract P1) shared by every provider-derived
    /// persistence transaction and by the finish-time runtime decision. It must run inside a
    /// lease-fenced transaction: it locks the connection row (FOR UPDATE) after the run-row guard, so
    /// an authorization change cannot become current until the protected write commits or rolls back.
    /// O1 = the run row's confirmed_operation_id_at_start; C = the connection's confirmed operation.
    /// True only when O1 exists, the connection is CONNECTED with a confirmed operation/version, C is a
    /// CONFIRMED operation of this account whose candidate credential equals the account credential
    /// reference and the connection's confirmed version, and either C == O1 or C is a direct REFRESH
    /// continuation of O1 (single hop: no chain is traversed and no other operation row is consulted).
    /// The identity branch is not a shortcut around coherence.
    /// </summary>
    public static async Task<bool> IsSnapshotCurrentOrAuthorizedRefreshAsync(VerceDbContext context,MarketplaceListingSyncRun run,Guid? expected,CancellationToken ct)
    {
        var o1=run.ConfirmedOperationIdAtStart;
        if(o1 is null||(expected is not null&&expected!=o1))return false;
        // Lock order is always SyncRun (the ExecuteWithLease UPDATE) then connection. Authorization
        // confirmation does not lock SyncRun, so this account-local lock cannot form a reverse cycle.
        var connection=await context.Set<MarketplaceAccountConnection>().FromSqlInterpolated($"SELECT * FROM commerce.marketplace_account_connection WHERE marketplace_account_id = {run.MarketplaceAccountId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if(connection is null||connection.AuthorizationState!=MarketplaceAuthorizationState.CONNECTED
            ||connection.ConfirmedOperationId is not { } current||connection.ConfirmedCredentialVersion is not { } confirmedVersion)return false;
        var ids=current==o1.Value?new[]{current}:new[]{current,o1.Value};
        var operations=await context.Set<MarketplaceAccountOperation>().AsNoTracking().Where(x=>ids.Contains(x.Id)).ToDictionaryAsync(x=>x.Id,ct);
        var accountReference=await context.Set<MarketplaceAccount>().AsNoTracking().Where(x=>x.Id==run.MarketplaceAccountId).Select(x=>x.CredentialReference).SingleOrDefaultAsync(ct);
        if(!operations.TryGetValue(current,out var currentOperation)
            ||currentOperation.MarketplaceAccountId!=run.MarketplaceAccountId
            ||currentOperation.Decision!=MarketplaceAccountOperationDecision.CONFIRMED
            ||currentOperation.CandidateCredentialReference is null||currentOperation.CandidateCredentialVersion is null
            ||currentOperation.CandidateCredentialReference!=accountReference
            ||currentOperation.CandidateCredentialVersion!=confirmedVersion)return false;
        if(current==o1.Value)return true;
        // A credential refresh is a continuation of the same authorization lineage although the S8C.1
        // arbiter records its own newly-confirmed operation. Accept exactly one direct
        // O1 -> REFRESH(O2) hop whose previous credential is O1's confirmed candidate; a reconnect, an
        // unrelated operation or a second hop stays a snapshot mismatch and fails closed.
        return currentOperation.Kind==MarketplaceAccountOperationKind.REFRESH
            &&operations.TryGetValue(o1.Value,out var prior)
            &&prior.MarketplaceAccountId==run.MarketplaceAccountId
            &&prior.Decision==MarketplaceAccountOperationDecision.CONFIRMED
            &&prior.CandidateCredentialReference is not null&&prior.CandidateCredentialVersion is not null
            &&currentOperation.PreviousCredentialReference==prior.CandidateCredentialReference
            &&currentOperation.PreviousConfirmedCredentialVersion==prior.CandidateCredentialVersion;
    }
    /// <summary>Provider-derived write guard. A null <paramref name="expected"/> is the lease-only seam exercised by the
    /// B1 fencing tests; the B2 execution never passes null (a missing snapshot is passed as <see cref="NoSnapshot"/>).</summary>
    public static async Task VerifySnapshotAsync(VerceDbContext context,MarketplaceListingSyncRun run,Guid? expected,CancellationToken ct)
    { if(expected is null)return; if(!await IsSnapshotCurrentOrAuthorizedRefreshAsync(context,run,expected,ct))throw new MarketplaceListingSyncAuthorizationSnapshotException(); }
    public async Task RecordOutcomeWithLeaseAsync(Guid runId,Guid token,Guid itemId,MarketplaceListingSyncItemOutcome outcome,string? safeCode,Guid? listingId,bool listingCreated,bool observationAppended,DateTimeOffset now,TimeSpan lease,CancellationToken ct=default,Guid? expectedOperationId=null){var authority=new MarketplaceListingSyncLeaseAuthority(db);await authority.ExecuteWithLeaseAsync(runId,token,now,lease,async inner=>{var run=await db.Set<MarketplaceListingSyncRun>().SingleAsync(x=>x.Id==runId,inner);await VerifySnapshotAsync(db,run,expectedOperationId,inner);var item=await db.Set<MarketplaceListingSyncRunItem>().SingleOrDefaultAsync(x=>x.Id==itemId&&x.SyncRunId==runId,inner)??throw new ArgumentException("LISTING_SYNC_ITEM_NOT_FOUND");item.Record(outcome,safeCode,listingId,listingCreated,observationAppended,now);await db.SaveChangesAsync(inner);},ct);}
    public async Task<MarketplaceListingSyncRun> FinalizeWithLeaseAsync(Guid runId,Guid token,string? stopReason,DateTimeOffset now,TimeSpan lease,CancellationToken ct=default,MarketplaceListingSyncFinishContext? finish=null){MarketplaceListingSyncRun? result=null;finish??=MarketplaceListingSyncFinishContext.None;var authority=new MarketplaceListingSyncLeaseAuthority(db);await authority.ExecuteWithLeaseAsync(runId,token,now,lease,async inner=>{var run=await db.Set<MarketplaceListingSyncRun>().SingleAsync(x=>x.Id==runId,inner);if(run.IsTerminal)throw new InvalidOperationException("LISTING_SYNC_RUN_ALREADY_TERMINAL");if(!string.IsNullOrWhiteSpace(stopReason))foreach(var item in await db.Set<MarketplaceListingSyncRunItem>().Where(x=>x.SyncRunId==runId&&x.Outcome==MarketplaceListingSyncItemOutcome.PENDING).ToListAsync(inner))item.Record(MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR,stopReason,null,false,false,now);var counters=MarketplaceListingSyncKernel.Count(await db.Set<MarketplaceListingSyncRunItem>().Where(x=>x.SyncRunId==runId).ToListAsync(inner));var terminal=MarketplaceListingSyncKernel.Compute(run.Kind,run.EnumerationCompleted,counters,stopReason);if(finish.RuntimeEffect!=MarketplaceListingSyncRuntimeEffect.None&&await IsSnapshotCurrentOrAuthorizedRefreshAsync(db,run,run.ConfirmedOperationIdAtStart,inner)){var account=await db.Set<MarketplaceAccount>().Include(x=>x.Connection).SingleAsync(x=>x.Id==run.MarketplaceAccountId,inner);if(finish.RuntimeEffect==MarketplaceListingSyncRuntimeEffect.Available)account.MarkAvailable(now);else account.RecordRuntimeFailure(finish.FailureClassification??"TRANSIENT",stopReason??"PROVIDER_UNAVAILABLE",now,finish.RetryAfterUntil);}run.Terminalize(terminal.Status,terminal.SafeResultCode,now);db.Add(FinishedAudit(run,terminal.Status,terminal.SafeResultCode,counters,now));await db.SaveChangesAsync(inner);result=run;},ct);return result!;}
    /// <summary>Stages (does not save) the one authoritative FAILED_ONLY retry: targets are derived here from the source run's durable
    /// retryable items, ancestry from <c>CreateRetry</c>. Callers own the transaction/save (B5).</summary>
    /// <summary>The one derivation of retry eligibility and targets (handoff B5/ADR-0025 §E.7): null when the source run does not exist; otherwise the
    /// source (detached) plus its retryable external ids. Eligible = terminal and at least one retryable item. Callers stage from the same plan.</summary>
    public async Task<MarketplaceListingSyncRetryPlan?> PlanRetryAsync(Guid sourceRunId,CancellationToken ct=default){var source=await db.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==sourceRunId,ct);if(source is null)return null;var targets=(await db.Set<MarketplaceListingSyncRunItem>().AsNoTracking().Where(x=>x.SyncRunId==sourceRunId&&x.Outcome!=MarketplaceListingSyncItemOutcome.PENDING).ToListAsync(ct)).Where(x=>MarketplaceListingSyncKernel.IsRetryable(x.Outcome)).Select(x=>x.ExternalListingId).ToArray();return new(source,targets,source.IsTerminal&&targets.Length>0);}
    public MarketplaceListingSyncRun StageRetry(MarketplaceListingSyncRetryPlan plan,Guid actorId,DateTimeOffset now,int maxAttempts=3){if(!plan.Eligible)throw new InvalidOperationException("LISTING_SYNC_NOTHING_TO_RETRY");var retry=MarketplaceListingSyncRun.CreateRetry(plan.Source,actorId,now,maxAttempts);db.Add(retry);foreach(var id in plan.Targets)db.Add(new MarketplaceListingSyncRunItem(retry.Id,id,MarketplaceListingSyncItemOrigin.RETRY_TARGET,now));return retry;}
    public async Task<MarketplaceListingSyncRun> StageFailedOnlyRetryAsync(Guid sourceRunId,Guid actorId,DateTimeOffset now,CancellationToken ct=default){var source=await db.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==sourceRunId,ct)??throw new ArgumentException("LISTING_SYNC_RUN_NOT_FOUND");var retryable=(await db.Set<MarketplaceListingSyncRunItem>().AsNoTracking().Where(x=>x.SyncRunId==sourceRunId&&x.Outcome!=MarketplaceListingSyncItemOutcome.PENDING).ToListAsync(ct)).Where(x=>MarketplaceListingSyncKernel.IsRetryable(x.Outcome)).Select(x=>x.ExternalListingId).ToArray();if(retryable.Length==0)throw new InvalidOperationException("LISTING_SYNC_NOTHING_TO_RETRY");var retry=MarketplaceListingSyncRun.CreateRetry(source,actorId,now);db.Add(retry);foreach(var id in retryable)db.Add(new MarketplaceListingSyncRunItem(retry.Id,id,MarketplaceListingSyncItemOrigin.RETRY_TARGET,now));return retry;}
    public async Task<MarketplaceListingSyncRun> CreateFailedOnlyRetryAsync(Guid sourceRunId,Guid actorId,DateTimeOffset now,CancellationToken ct=default){await using var tx=await db.Database.BeginTransactionAsync(ct);var retry=await StageFailedOnlyRetryAsync(sourceRunId,actorId,now,ct);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return retry;}
    public async Task<int> RegisterTargetsWithLeaseAsync(Guid runId,Guid token,IEnumerable<string> externalIds,MarketplaceListingSyncItemOrigin origin,DateTimeOffset now,TimeSpan lease,CancellationToken ct=default,Guid? expectedOperationId=null){var added=0;var authority=new MarketplaceListingSyncLeaseAuthority(db);await authority.ExecuteWithLeaseAsync(runId,token,now,lease,async inner=>{var run=await db.Set<MarketplaceListingSyncRun>().SingleAsync(x=>x.Id==runId,inner);await VerifySnapshotAsync(db,run,expectedOperationId,inner);if(run.IsTerminal)throw new InvalidOperationException("LISTING_SYNC_RUN_TERMINAL");var ids=externalIds.Select(x=>MarketplaceProvider.Required(x,1,200)).Distinct(StringComparer.Ordinal).ToArray();var known=(await db.Set<MarketplaceListingSyncRunItem>().Where(x=>x.SyncRunId==runId&&ids.Contains(x.ExternalListingId)).Select(x=>x.ExternalListingId).ToListAsync(inner)).ToHashSet(StringComparer.Ordinal);var newIds=ids.Where(x=>!known.Contains(x)).ToArray();var current=await db.Set<MarketplaceListingSyncRunItem>().CountAsync(x=>x.SyncRunId==runId,inner);if(current+newIds.Length>MaxTargetsPerRun)throw new MarketplaceListingSyncTargetLimitException();foreach(var id in newIds){db.Add(new MarketplaceListingSyncRunItem(runId,id,origin,now));added++;}await db.SaveChangesAsync(inner);},ct);return added;}
    public async Task<int> RegisterKnownNotEnumeratedWithLeaseAsync(Guid runId,Guid token,DateTimeOffset now,TimeSpan lease,Guid? expectedOperationId,CancellationToken ct=default){var added=0;var authority=new MarketplaceListingSyncLeaseAuthority(db);await authority.ExecuteWithLeaseAsync(runId,token,now,lease,async inner=>{var run=await db.Set<MarketplaceListingSyncRun>().SingleAsync(x=>x.Id==runId,inner);await VerifySnapshotAsync(db,run,expectedOperationId,inner);var candidates=await db.Set<MarketplaceListing>().Where(x=>x.MarketplaceAccountId==run.MarketplaceAccountId&&!db.Set<MarketplaceListingSyncRunItem>().Any(i=>i.SyncRunId==runId&&i.ExternalListingId==x.ExternalListingId)).OrderBy(x=>x.ExternalListingId).Take(KnownNotEnumeratedBatchSize).Select(x=>x.ExternalListingId).ToListAsync(inner);var current=await db.Set<MarketplaceListingSyncRunItem>().CountAsync(x=>x.SyncRunId==runId,inner);if(current+candidates.Count>MaxTargetsPerRun)throw new MarketplaceListingSyncTargetLimitException();foreach(var externalId in candidates)db.Add(new MarketplaceListingSyncRunItem(runId,externalId,MarketplaceListingSyncItemOrigin.KNOWN_NOT_ENUMERATED,now));added=candidates.Count;await db.SaveChangesAsync(inner);},ct);return added;}
    public async Task MarkEnumerationCompletedWithLeaseAsync(Guid runId,Guid token,DateTimeOffset now,TimeSpan lease,CancellationToken ct=default,Guid? expectedOperationId=null){var authority=new MarketplaceListingSyncLeaseAuthority(db);await authority.ExecuteWithLeaseAsync(runId,token,now,lease,async inner=>{var run=await db.Set<MarketplaceListingSyncRun>().SingleAsync(x=>x.Id==runId,inner);await VerifySnapshotAsync(db,run,expectedOperationId,inner);run.MarkEnumerationCompleted(now);await db.SaveChangesAsync(inner);},ct);}
    public async Task SetPhaseWithLeaseAsync(Guid runId,Guid token,MarketplaceListingSyncPhase phase,DateTimeOffset now,TimeSpan lease,CancellationToken ct=default,Guid? expectedOperationId=null){var authority=new MarketplaceListingSyncLeaseAuthority(db);await authority.ExecuteWithLeaseAsync(runId,token,now,lease,async inner=>{var run=await db.Set<MarketplaceListingSyncRun>().SingleAsync(x=>x.Id==runId,inner);await VerifySnapshotAsync(db,run,expectedOperationId,inner);run.SetPhase(phase,now);await db.SaveChangesAsync(inner);},ct);}
}

/// <summary>Result of the single retry-target derivation: the source run (detached), the retryable external ids and whether a retry may be created.</summary>
public sealed record MarketplaceListingSyncRetryPlan(MarketplaceListingSyncRun Source,IReadOnlyList<string> Targets,bool Eligible);
