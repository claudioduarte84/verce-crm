using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Verce.Modules.Commerce;
using Verce.Platform.Persistence;
using Verce.SharedKernel.Time;

namespace Verce.Infrastructure.Marketplaces;

/// <summary>ADR-0025 E.3-E.7 execution seam. Provider work is outside transactions; every
/// provider-derived write re-enters through the B1 lease guard and operation snapshot.</summary>
public sealed class MarketplaceListingSyncExecution(
    VerceDbContext db,
    MarketplaceCredentialExecutor credentials,
    IMarketplaceConnectorRegistry connectors,
    ICommerceProductEligibilityReader productEligibility,
    IClock clock,
    IOptions<MarketplaceListingSyncOptions> options,
    ILogger<MarketplaceListingSyncExecution> logger)
{
    public const int DefaultDetailBatchSize = 20;
    private readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(options.Value.LeaseSeconds);
    private readonly int _detailBatchSize = options.Value.DetailBatchSize;

    public async Task<MarketplaceListingSyncRun?> RunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var authority = new MarketplaceListingSyncLeaseAuthority(db);
        var lease = await authority.ClaimOrReclaimAsync(runId, clock.UtcNow, LeaseDuration, cancellationToken);
        return lease is null ? null : await RunClaimedAsync(lease, cancellationToken);
    }

    /// <summary>Executes a lease that was already claimed (by the dispatcher, or by <see cref="RunAsync"/>). All fencing, snapshot, authority and
    /// finish semantics are unchanged: this is the former body of RunAsync after the claim.</summary>
    public async Task<MarketplaceListingSyncRun?> RunClaimedAsync(MarketplaceListingSyncLease lease, CancellationToken cancellationToken = default)
    {
        var runId = lease.RunId;
        var run = await db.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId, cancellationToken);
        if (!connectors.TryGetListingReader(run.ProviderCode, out var reader)) return await FinishAsync(lease, "MARKETPLACE_PROVIDER_NOT_SUPPORTED", false, null, cancellationToken);
        var providerSucceeded = false; string? stopReason = null; DateTimeOffset? retryAfter = null;
        // Finish is attempted exactly once, after the try/catch below: an exception raised by the finish
        // transaction itself must propagate (lease loss or otherwise) and never be re-interpreted as a
        // provider stop that would trigger a second finish.
        try
        {
            if (run.Kind == MarketplaceListingSyncKind.FULL && !run.EnumerationCompleted)
                providerSucceeded |= await EnumerateAsync(run, lease, reader, cancellationToken);
            run = await db.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId, cancellationToken);
            if (run.Kind == MarketplaceListingSyncKind.FULL) await RegisterKnownNotEnumeratedAsync(run, lease, cancellationToken);
            await new MarketplaceListingSyncRunKernel(db).SetPhaseWithLeaseAsync(runId, lease.LeaseToken, MarketplaceListingSyncPhase.DETAILING, clock.UtcNow, LeaseDuration, cancellationToken, Snapshot(run));
            while (true)
            {
                var items = await GetNextPendingAsync(runId, cancellationToken); if (items.Count == 0) break;
                var ids = items.Select(x => x.ExternalListingId).ToArray(); IReadOnlyList<MarketplaceListingDetailResult> results;
                try
                {
                    results = await credentials.ExecuteAsync(run.MarketplaceAccountId, MarketplaceExecutionRequirement.BusinessCapability, (context, ct) => reader.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(context.AccountId, context.ExternalAccountId, context.Secret, ids), ct), cancellationToken, "LISTINGS_READ");
                    providerSucceeded = true;
                    var failed = results.Where(x => x.Outcome == MarketplaceListingReadOutcome.FAILED && ids.Contains(x.ExternalListingId, StringComparer.Ordinal)).Select(x => x.ExternalListingId).Distinct(StringComparer.Ordinal).ToArray();
                    if (failed.Length != 0)
                    {
                        var retried = await credentials.ExecuteAsync(run.MarketplaceAccountId, MarketplaceExecutionRequirement.BusinessCapability, (context, ct) => reader.ReadListingDetailsAsync(new MarketplaceListingDetailRequest(context.AccountId, context.ExternalAccountId, context.Secret, failed), ct), cancellationToken, "LISTINGS_READ");
                        providerSucceeded = true; results = results.Where(x => !failed.Contains(x.ExternalListingId, StringComparer.Ordinal)).Concat(retried).ToArray();
                    }
                }
                catch (MarketplaceProviderCallException ex) when (ex.Failure.Classification == MarketplaceFailureClassification.NON_RETRYABLE)
                { await RecordChunkRejectedAsync(run, lease, items, cancellationToken); continue; }
                await ApplyDetailChunkWithRetryAsync(run, lease, items, results, cancellationToken);
            }
        }
        catch (ListingEnumerationIncompleteException ex) { stopReason = ex.Code; }
        catch (MarketplaceListingSyncTargetLimitException) { stopReason = "LISTING_VOLUME_EXCEEDS_LIMIT"; }
        catch (MarketplaceListingSyncLeaseLostException) { throw; }
        catch (MarketplaceListingSyncTestRollbackException) { throw; }
        catch (MarketplaceListingSyncAuthorizationSnapshotException) { stopReason = "AUTHORIZATION_NOT_USABLE"; }
        catch (MarketplaceExecutionBlockedException ex) { stopReason = ex.Reason; }
        catch (MarketplaceProviderCallException ex)
        {
            stopReason = ex.Failure.Classification switch { MarketplaceFailureClassification.RATE_LIMITED => "PROVIDER_RATE_LIMITED", MarketplaceFailureClassification.AUTH_RENEWAL_REQUIRED => "PROVIDER_CREDENTIAL_REJECTED", MarketplaceFailureClassification.USER_ACTION_REQUIRED => "PROVIDER_ACCESS_FORBIDDEN", MarketplaceFailureClassification.NON_RETRYABLE => "PROVIDER_UNAVAILABLE", _ => "PROVIDER_UNAVAILABLE" };
            retryAfter = ex.Failure.RetryAfterUntil;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; } // host stop: never a provider stop; the run stays RUNNING and reclaimable
        catch (Exception)
        { logger.LogWarning("Listing synchronization stopped for run {RunId} with safe local classification", runId); stopReason = "PROVIDER_UNAVAILABLE"; }
        // Deterministic seam: all detail work is done (no PENDING item left, no stop), outside any transaction.
        if (stopReason is null && MarketplaceListingSyncExecutionTestHooks.BeforeFinishAsync is { } beforeFinish) { MarketplaceListingSyncExecutionTestHooks.BeforeFinishAsync = null; await beforeFinish(cancellationToken); }
        return await FinishAsync(lease, stopReason, providerSucceeded, retryAfter, cancellationToken);
    }

    /// <summary>The run's authorization snapshot as the shared authority predicate expects it; a run without one is
    /// represented by <see cref="MarketplaceListingSyncRunKernel.NoSnapshot"/> so it can never pass the lease-only seam.</summary>
    private static Guid Snapshot(MarketplaceListingSyncRun run) => run.ConfirmedOperationIdAtStart ?? MarketplaceListingSyncRunKernel.NoSnapshot;

    private async Task<bool> EnumerateAsync(MarketplaceListingSyncRun run, MarketplaceListingSyncLease lease, IMarketplaceListingReader reader, CancellationToken ct)
    {
        var kernel = new MarketplaceListingSyncRunKernel(db); await kernel.SetPhaseWithLeaseAsync(run.Id, lease.LeaseToken, MarketplaceListingSyncPhase.ENUMERATING, clock.UtcNow, LeaseDuration, ct, Snapshot(run));
        var restarts = 0; var called = false;
        while (true)
        {
            MarketplaceListingCursor? cursor = null; var noNewPages = 0;
            try
            {
                while (true)
                {
                    var page = await credentials.ExecuteAsync(run.MarketplaceAccountId, MarketplaceExecutionRequirement.BusinessCapability, (context, inner) => ReadPageForEnumerationAsync(reader, new MarketplaceListingPageRequest(context.AccountId, context.ExternalAccountId, context.Secret, cursor), inner), ct, "LISTINGS_READ");
                    called = true;
                    if (page.RejectedIdentifierCount != 0) throw new ListingEnumerationIncompleteException("LISTING_ENUMERATION_INCOMPLETE");
                    var pageIds = page.ExternalListingIds.Distinct(StringComparer.Ordinal).ToArray();
                    var added = await kernel.RegisterTargetsWithLeaseAsync(run.Id, lease.LeaseToken, pageIds, MarketplaceListingSyncItemOrigin.ENUMERATED, clock.UtcNow, LeaseDuration, ct, Snapshot(run));
                    noNewPages = added == 0 ? noNewPages + 1 : 0;
                    if (noNewPages >= 3) throw new ListingEnumerationIncompleteException("LISTING_ENUMERATION_INCOMPLETE");
                    if (page.EndOfEnumeration) { await kernel.MarkEnumerationCompletedWithLeaseAsync(run.Id, lease.LeaseToken, clock.UtcNow, LeaseDuration, ct, Snapshot(run)); return called; }
                    if (page.NextCursor is null) throw new ListingEnumerationCursorException();
                    cursor = page.NextCursor;
                }
            }
            catch (MarketplaceProviderCallException ex) when (ex.Failure.SafeCode == "LISTING_ENUMERATION_CURSOR_INVALID") { if (++restarts > 2) throw new ListingEnumerationIncompleteException("LISTING_ENUMERATION_INCOMPLETE"); }
            catch (ListingEnumerationCursorException) { if (++restarts > 2) throw new ListingEnumerationIncompleteException("LISTING_ENUMERATION_INCOMPLETE"); }
        }
    }

    /// <summary>ADR-0029 §2 (complementing ADR-0025 §E.3): a whole-call NON_RETRYABLE provider failure of a scan page — 400, an unreadable/invalid 2xx, an oversized body — means the
    /// complete ID set could not be obtained: the run stops as <c>LISTING_ENUMERATION_INCOMPLETE</c> (runtime untouched, requiredAction NONE), never as provider unavailability.
    /// <c>LISTING_ENUMERATION_CURSOR_INVALID</c> keeps its own restart rule. Only the READER's failure is mapped here: credential and guard failures keep their own stops.</summary>
    private static async Task<MarketplaceListingPage> ReadPageForEnumerationAsync(IMarketplaceListingReader reader, MarketplaceListingPageRequest request, CancellationToken ct)
    {
        try { return await reader.ReadListingPageAsync(request, ct); }
        catch (MarketplaceProviderCallException ex) when (ex.Failure.Classification == MarketplaceFailureClassification.NON_RETRYABLE && ex.Failure.SafeCode != "LISTING_ENUMERATION_CURSOR_INVALID")
        { throw new ListingEnumerationIncompleteException("LISTING_ENUMERATION_INCOMPLETE"); }
    }

    private async Task RegisterKnownNotEnumeratedAsync(MarketplaceListingSyncRun run, MarketplaceListingSyncLease lease, CancellationToken ct)
    {
        var kernel = new MarketplaceListingSyncRunKernel(db); await kernel.SetPhaseWithLeaseAsync(run.Id, lease.LeaseToken, MarketplaceListingSyncPhase.RECONCILING_MISSING, clock.UtcNow, LeaseDuration, ct, Snapshot(run));
        while (await kernel.RegisterKnownNotEnumeratedWithLeaseAsync(run.Id, lease.LeaseToken, clock.UtcNow, LeaseDuration, Snapshot(run), ct) != 0) { }
    }

    private Task<List<MarketplaceListingSyncRunItem>> GetNextPendingAsync(Guid runId, CancellationToken ct) => db.Set<MarketplaceListingSyncRunItem>().AsNoTracking().Where(x => x.SyncRunId == runId && x.Outcome == MarketplaceListingSyncItemOutcome.PENDING).OrderBy(x => x.Origin == MarketplaceListingSyncItemOrigin.ENUMERATED ? 0 : x.Origin == MarketplaceListingSyncItemOrigin.RETRY_TARGET ? 1 : 2).ThenBy(x => x.ExternalListingId).Take(_detailBatchSize).ToListAsync(ct);

    private async Task ApplyDetailChunkWithRetryAsync(MarketplaceListingSyncRun run, MarketplaceListingSyncLease lease, IReadOnlyList<MarketplaceListingSyncRunItem> items, IReadOnlyList<MarketplaceListingDetailResult> results, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try { await ApplyDetailChunkAsync(run, lease, items, results, ct); return; }
            catch (Exception ex) when (IsWriteConflict(ex) && attempt < 3) { db.ChangeTracker.Clear(); if (MarketplaceListingSyncExecutionTestHooks.BeforeWriteRetryAsync is { } retry) await retry(attempt, ct); }
            catch (Exception ex) when (IsWriteConflict(ex)) { db.ChangeTracker.Clear(); await RecordWriteConflictAsync(run, lease, items, ct); return; }
        }
    }
    private static bool IsWriteConflict(Exception ex) => ex is DbUpdateConcurrencyException || ex is DbUpdateException { InnerException: PostgresException { SqlState: "23505" } };

    private async Task ApplyDetailChunkAsync(MarketplaceListingSyncRun run, MarketplaceListingSyncLease lease, IReadOnlyList<MarketplaceListingSyncRunItem> items, IReadOnlyList<MarketplaceListingDetailResult> results, CancellationToken ct)
    {
        if (MarketplaceListingSyncExecutionTestHooks.BeforeFencedDetailPersistenceAsync is { } before) { MarketplaceListingSyncExecutionTestHooks.BeforeFencedDetailPersistenceAsync = null; await before(ct); }
        var byId = results.Where(x => !string.IsNullOrWhiteSpace(x.ExternalListingId)).GroupBy(x => x.ExternalListingId, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count() == 1 ? x.Single() : new MarketplaceListingDetailResult(x.Key, MarketplaceListingReadOutcome.INVALID_RESPONSE, null, null), StringComparer.Ordinal);
        var authority = new MarketplaceListingSyncLeaseAuthority(db);
        await authority.ExecuteWithLeaseAsync(run.Id, lease.LeaseToken, clock.UtcNow, LeaseDuration, async inner =>
        {
            var trackedRun = await db.Set<MarketplaceListingSyncRun>().SingleAsync(x => x.Id == run.Id, inner);
            await MarketplaceListingSyncRunKernel.VerifySnapshotAsync(db,trackedRun,Snapshot(run),inner);
            if (MarketplaceListingSyncExecutionTestHooks.AfterAuthorizationValidationAsync is { } afterAuthority)
            {
                MarketplaceListingSyncExecutionTestHooks.AfterAuthorizationValidationAsync = null;
                await afterAuthority(inner);
            }
            var trackedItems = await db.Set<MarketplaceListingSyncRunItem>().Where(x => items.Select(i => i.Id).Contains(x.Id)).ToDictionaryAsync(x => x.Id, inner);
            var listings = await db.Set<MarketplaceListing>().Include(x => x.Observations).Where(x => x.MarketplaceAccountId == trackedRun.MarketplaceAccountId && items.Select(i => i.ExternalListingId).Contains(x.ExternalListingId)).ToDictionaryAsync(x => x.ExternalListingId, inner);
            var skus = byId.Values.Where(x => x.Outcome == MarketplaceListingReadOutcome.FOUND && x.Facts?.ExternalSku is not null).Select(x => MarketplaceSkuNormalizer.Normalize(x.Facts!.ExternalSku!)).Distinct(StringComparer.Ordinal).ToArray();
            var mappings = await db.Set<MarketplaceAccountSkuMapping>().Where(x => x.MarketplaceAccountId == trackedRun.MarketplaceAccountId && x.Active && skus.Contains(x.ExternalSku)).ToListAsync(inner);
            var products = await productEligibility.GetActiveAsync(mappings.Select(x => x.ProductId).Distinct().ToArray(), inner);
            foreach (var source in items)
            {
                var item = trackedItems[source.Id]; if (item.Outcome != MarketplaceListingSyncItemOutcome.PENDING) continue;
                var result = byId.TryGetValue(item.ExternalListingId, out var found) ? found : new MarketplaceListingDetailResult(item.ExternalListingId, MarketplaceListingReadOutcome.FAILED, null, null);
                if (result.Outcome == MarketplaceListingReadOutcome.FOUND && result.Facts is not null)
                {
                    var created = false; if (!listings.TryGetValue(item.ExternalListingId, out var listing)) { listing = MarketplaceListing.ImportFromProvider(trackedRun.MarketplaceAccountId, result.Facts, run.Id, DecideLinkage(trackedRun.MarketplaceAccountId, result.Facts, mappings, products), clock.UtcNow); db.Add(listing); listings.Add(item.ExternalListingId, listing); created = true; }
                    else { listing.ApplyLinkageDecision(DecideLinkage(trackedRun.MarketplaceAccountId, result.Facts, mappings, products)); var appendedExisting = listing.ApplyProviderFacts(result.Facts, run.Id, clock.UtcNow); item.Record(MarketplaceListingSyncItemOutcome.FOUND, null, listing.Id, false, appendedExisting, clock.UtcNow); continue; }
                    item.Record(MarketplaceListingSyncItemOutcome.FOUND, null, listing.Id, created, true, clock.UtcNow);
                }
                else { var mapped = Map(result.Outcome); if (listings.TryGetValue(item.ExternalListingId, out var listing)) listing.RecordProviderReadFailure(mapped.Code, clock.UtcNow); item.Record(mapped.Outcome, mapped.Code, null, false, false, clock.UtcNow); }
            }
            await db.SaveChangesAsync(inner);
            if (MarketplaceListingSyncExecutionTestHooks.AfterDetailSaveBeforeCommitAsync is { } after) { MarketplaceListingSyncExecutionTestHooks.AfterDetailSaveBeforeCommitAsync = null; await after(inner); }
        }, ct);
    }
    private async Task RecordWriteConflictAsync(MarketplaceListingSyncRun run, MarketplaceListingSyncLease lease, IReadOnlyList<MarketplaceListingSyncRunItem> items, CancellationToken ct)
    {
        var kernel = new MarketplaceListingSyncRunKernel(db);
        foreach (var item in items)
            await kernel.RecordOutcomeWithLeaseAsync(run.Id, lease.LeaseToken, item.Id, MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR, "LISTING_WRITE_CONFLICT", null, false, false, clock.UtcNow, LeaseDuration, ct, Snapshot(run));
    }
    private async Task RecordChunkRejectedAsync(MarketplaceListingSyncRun run, MarketplaceListingSyncLease lease, IReadOnlyList<MarketplaceListingSyncRunItem> items, CancellationToken ct) { foreach (var item in items) await new MarketplaceListingSyncRunKernel(db).RecordOutcomeWithLeaseAsync(run.Id, lease.LeaseToken, item.Id, MarketplaceListingSyncItemOutcome.PERMANENT_ERROR, "LISTING_READ_REJECTED", null, false, false, clock.UtcNow, LeaseDuration, ct, Snapshot(run)); }
    private static MarketplaceListingLinkageDecision DecideLinkage(Guid accountId, NormalizedListingFacts facts, IReadOnlyList<MarketplaceAccountSkuMapping> mappings, IReadOnlyDictionary<Guid,bool> products) { if (facts.ExternalSku is null) return new(MarketplaceLinkageState.UNLINKED,null,null); var matches=mappings.Where(x=>x.MarketplaceAccountId==accountId&&x.ExternalSku==MarketplaceSkuNormalizer.Normalize(facts.ExternalSku)).ToArray(); if(matches.Length==0)return new(MarketplaceLinkageState.UNLINKED,null,null); if(matches.Length!=1||facts.VariationCount>1||!products.TryGetValue(matches[0].ProductId,out var active)||!active)return new(MarketplaceLinkageState.NEEDS_REVIEW,null,null);return new(MarketplaceLinkageState.LINKED,matches[0].ProductId,matches[0].Id); }
    private static (MarketplaceListingSyncItemOutcome Outcome,string Code) Map(MarketplaceListingReadOutcome outcome)=>outcome switch { MarketplaceListingReadOutcome.NOT_FOUND=>(MarketplaceListingSyncItemOutcome.NOT_FOUND,"LISTING_NOT_FOUND_AT_PROVIDER"),MarketplaceListingReadOutcome.ACCESS_DENIED=>(MarketplaceListingSyncItemOutcome.ACCESS_DENIED,"LISTING_ACCESS_DENIED"),MarketplaceListingReadOutcome.SELLER_MISMATCH=>(MarketplaceListingSyncItemOutcome.SELLER_MISMATCH,"LISTING_SELLER_MISMATCH"),MarketplaceListingReadOutcome.UNMAPPED_STATUS=>(MarketplaceListingSyncItemOutcome.PERMANENT_ERROR,"LISTING_STATUS_UNMAPPED"),MarketplaceListingReadOutcome.INVALID_RESPONSE=>(MarketplaceListingSyncItemOutcome.PERMANENT_ERROR,"LISTING_RESPONSE_INVALID"),_=>(MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR,"LISTING_READ_FAILED")};
    private Task<MarketplaceListingSyncRun> FinishAsync(MarketplaceListingSyncLease lease,string? reason,bool providerSucceeded,DateTimeOffset? retryAfter,CancellationToken ct)
    {
        var effect = reason switch
        {
            "PROVIDER_RATE_LIMITED" => MarketplaceListingSyncRuntimeEffect.ProviderRateLimited,
            "PROVIDER_UNAVAILABLE" => MarketplaceListingSyncRuntimeEffect.ProviderUnavailable,
            null when providerSucceeded => MarketplaceListingSyncRuntimeEffect.Available,
            _ => MarketplaceListingSyncRuntimeEffect.None
        };
        return new MarketplaceListingSyncRunKernel(db).FinalizeWithLeaseAsync(lease.RunId,lease.LeaseToken,reason,clock.UtcNow,LeaseDuration,ct,new(effect,effect==MarketplaceListingSyncRuntimeEffect.ProviderRateLimited?"RATE_LIMITED":"TRANSIENT",retryAfter));
    }
    private sealed class ListingEnumerationCursorException : Exception;
    private sealed class ListingEnumerationIncompleteException(string code) : Exception { public string Code { get; } = code; }
}
