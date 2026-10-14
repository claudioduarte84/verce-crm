using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Verce.Modules.Commerce;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.S8C2;

public sealed class S8C2LeaseFencingIntegrationTests : IAsyncLifetime
{
    private PostgreSqlContainer _container=null!; private string _cs=string.Empty;
    private static readonly Guid[] Accounts=Enumerable.Range(1,8).Select(i=>Guid.Parse($"33333333-3333-3333-3333-{i:000000000000}")).ToArray();
    private static readonly Guid[] Operations=Enumerable.Range(1,8).Select(i=>Guid.Parse($"44444444-4444-4444-4444-{i:000000000000}")).ToArray();
    private int _nextAccount;

    public async Task InitializeAsync()
    {
        VerceDbContext.ConfigureModuleAssemblies(Verce.Api.ModuleAssemblyCatalog.All);
        _container=new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("verce_s8c2_b1c").WithUsername("verce").WithPassword("verce_test_only").Build();
        await _container.StartAsync(); _cs=_container.GetConnectionString();
        await using var db=Context(); await db.Database.MigrateAsync(); await SeedAsync();
    }
    public async Task DisposeAsync()=>await _container.DisposeAsync();
    private VerceDbContext Context()=>new(new DbContextOptionsBuilder<VerceDbContext>().UseNpgsql(_cs).UseSnakeCaseNamingConvention().Options);
    private async Task SeedAsync()
    {
        var n=DateTimeOffset.UtcNow; var channel=Guid.NewGuid(); await using var c=new NpgsqlConnection(_cs); await c.OpenAsync();
        await using var cmd=new NpgsqlCommand("""INSERT INTO pricing.sales_channel (id,code,name,kind,active,created_at,version) VALUES (@c,'B1C','B1C','Marketplace',true,@n,1); INSERT INTO commerce.marketplace_provider (code,name) VALUES ('MERCADO_LIVRE','Mercado Livre') ON CONFLICT DO NOTHING;""",c);
        cmd.Parameters.AddWithValue("c",channel);cmd.Parameters.AddWithValue("n",n);await cmd.ExecuteNonQueryAsync();
        for(var i=0;i<Accounts.Length;i++){var account=Accounts[i];cmd.Parameters.Clear();cmd.CommandText="INSERT INTO commerce.marketplace_account (id,provider_code,external_account_id,sales_channel_id,display_name,active,sync_state,created_at,version) VALUES (@a,'MERCADO_LIVRE',@e,@c,@e,true,'NEVER_SYNCED',@n,1); INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,created_at,updated_at,cleanup_attempt_count,version) VALUES (@o,'REFRESH','SECRET_PERSISTED','CONFIRMED','DONE',@a,'MERCADO_LIVRE',@n,@n,0,1); INSERT INTO commerce.marketplace_account_connection (marketplace_account_id,authorization_state,runtime_availability,confirmed_operation_id,created_at,updated_at) VALUES (@a,'CONNECTED','AVAILABLE',@o,@n,@n)";cmd.Parameters.AddWithValue("a",account);cmd.Parameters.AddWithValue("o",Operations[i]);cmd.Parameters.AddWithValue("e",$"b1c-{account:N}");cmd.Parameters.AddWithValue("c",channel);cmd.Parameters.AddWithValue("n",n);await cmd.ExecuteNonQueryAsync();}
    }
    private async Task<Guid> CreateRunAsync(DateTimeOffset now)
    {
        var account=Accounts[Interlocked.Increment(ref _nextAccount)-1];await using var db=Context();return (await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account,Guid.NewGuid(),now)).Id;
    }

    private async Task<Guid> AddItemWithLeaseAsync(Guid runId, Guid token, DateTimeOffset now)
    { await using var db=Context();var kernel=new MarketplaceListingSyncRunKernel(db);await kernel.RegisterTargetsWithLeaseAsync(runId,token,["MLB-B1C"],MarketplaceListingSyncItemOrigin.ENUMERATED,now,TimeSpan.FromMinutes(1));return await db.Set<MarketplaceListingSyncRunItem>().Where(x=>x.SyncRunId==runId).Select(x=>x.Id).SingleAsync(); }

    [Fact]
    public async Task Atomic_claim_heartbeat_reclaim_and_fenced_terminalization_are_deterministic()
    {
        var now=DateTimeOffset.Parse("2026-09-27T12:00:00Z");var runId=await CreateRunAsync(now);var lease=TimeSpan.FromMinutes(1);
        MarketplaceListingSyncLease? t1;await using(var db=Context()){var authority=new MarketplaceListingSyncLeaseAuthority(db);t1=await authority.ClaimAsync(runId,now,lease);(await authority.HeartbeatAsync(runId,t1!.LeaseToken,now.AddSeconds(5),lease)).Should().BeTrue();}
        var itemId=await AddItemWithLeaseAsync(runId,t1!.LeaseToken,now);await using(var db=Context()){await new MarketplaceListingSyncRunKernel(db).MarkEnumerationCompletedWithLeaseAsync(runId,t1.LeaseToken,now,lease);}
        MarketplaceListingSyncLease? t2;await using(var db=Context()){var authority=new MarketplaceListingSyncLeaseAuthority(db);t2=await authority.ReclaimAsync(runId,now.AddMinutes(2),lease);}
        t2.Should().NotBeNull();t2!.LeaseToken.Should().NotBe(t1!.LeaseToken);
        await using(var db=Context()){var authority=new MarketplaceListingSyncLeaseAuthority(db);(await authority.HeartbeatAsync(runId,t1.LeaseToken,now.AddMinutes(2),lease)).Should().BeFalse();(await authority.HeartbeatAsync(runId,t2.LeaseToken,now.AddMinutes(2),lease)).Should().BeTrue();var kernel=new MarketplaceListingSyncRunKernel(db);await kernel.RecordOutcomeWithLeaseAsync(runId,t2.LeaseToken,itemId,MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR,"LISTING_READ_FAILED",null,false,false,now.AddMinutes(2),lease);await kernel.FinalizeWithLeaseAsync(runId,t2.LeaseToken,"RUN_STOPPED",now.AddMinutes(2),lease);}
        await using(var db=Context()){var run=await db.Set<MarketplaceListingSyncRun>().SingleAsync(x=>x.Id==runId);run.Status.Should().Be(MarketplaceListingSyncStatus.FAILED);run.LeaseToken.Should().BeNull();run.AttemptCount.Should().Be(2);run.ConfirmedOperationIdAtStart.Should().Be(Operations[0]);var authority=new MarketplaceListingSyncLeaseAuthority(db);(await authority.HeartbeatAsync(runId,t2.LeaseToken,now.AddMinutes(3),lease)).Should().BeFalse();}
    }

    [Fact]
    public async Task Parallel_claim_and_parallel_reclaim_have_exactly_one_winner()
    {
        var now=DateTimeOffset.Parse("2026-09-27T13:00:00Z");var claimRun=await CreateRunAsync(now);using var claimGate=new Barrier(2);
        var claims=Enumerable.Range(0,2).Select(_=>Task.Run(async()=>{await using var db=Context();claimGate.SignalAndWait();return await new MarketplaceListingSyncLeaseAuthority(db).ClaimAsync(claimRun,now,TimeSpan.FromMinutes(1));})).ToArray();
        var claimResults=await Task.WhenAll(claims);claimResults.Count(x=>x is not null).Should().Be(1);var claimWinner=claimResults.Single(x=>x is not null)!;
        await using(var db=Context()){var run=await db.Set<MarketplaceListingSyncRun>().SingleAsync(x=>x.Id==claimRun);run.LeaseToken.Should().Be(claimWinner.LeaseToken);run.LeaseUntil.Should().Be(claimWinner.LeaseUntil);run.HeartbeatAt.Should().Be(now);run.UpdatedAt.Should().Be(now);run.AttemptCount.Should().Be(1);run.Version.Should().Be(2);run.ConfirmedOperationIdAtStart.Should().Be(Operations[0]);}
        var reclaimRun=await CreateRunAsync(now);await using(var db=Context()){(await new MarketplaceListingSyncLeaseAuthority(db).ClaimAsync(reclaimRun,now,TimeSpan.FromMinutes(1))).Should().NotBeNull();}
        using var reclaimGate=new Barrier(2);var reclaims=Enumerable.Range(0,2).Select(_=>Task.Run(async()=>{await using var db=Context();reclaimGate.SignalAndWait();return await new MarketplaceListingSyncLeaseAuthority(db).ReclaimAsync(reclaimRun,now.AddMinutes(2),TimeSpan.FromMinutes(1));})).ToArray();
        var reclaimResults=await Task.WhenAll(reclaims);reclaimResults.Count(x=>x is not null).Should().Be(1);var reclaimWinner=reclaimResults.Single(x=>x is not null)!;
        await using(var db=Context()){var run=await db.Set<MarketplaceListingSyncRun>().SingleAsync(x=>x.Id==reclaimRun);run.LeaseToken.Should().Be(reclaimWinner.LeaseToken);run.LeaseUntil.Should().Be(reclaimWinner.LeaseUntil);run.HeartbeatAt.Should().Be(now.AddMinutes(2));run.UpdatedAt.Should().Be(now.AddMinutes(2));run.AttemptCount.Should().Be(2);run.Version.Should().Be(3);run.ConfirmedOperationIdAtStart.Should().Be(Operations[1]);}
    }

    [Fact]
    public async Task Stale_worker_cannot_write_or_finalize_and_a_guarded_fault_rolls_back()
    {
        var now=DateTimeOffset.Parse("2026-09-27T14:00:00Z");var lease=TimeSpan.FromMinutes(1);var runId=await CreateRunAsync(now);MarketplaceListingSyncLease t1;
        await using(var db=Context()){t1=(await new MarketplaceListingSyncLeaseAuthority(db).ClaimAsync(runId,now,lease))!;}
        var itemId=await AddItemWithLeaseAsync(runId,t1.LeaseToken,now);
        var paused=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var resume=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale=Task.Run(async()=>{await using var db=Context();paused.SetResult();await resume.Task;var kernel=new MarketplaceListingSyncRunKernel(db);var enumeration=await Record.ExceptionAsync(()=>kernel.MarkEnumerationCompletedWithLeaseAsync(runId,t1.LeaseToken,now.AddMinutes(2),lease));var record=await Record.ExceptionAsync(()=>kernel.RecordOutcomeWithLeaseAsync(runId,t1.LeaseToken,itemId,MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR,"STALE",null,false,false,now.AddMinutes(2),lease));return(enumeration,record);});
        await paused.Task;MarketplaceListingSyncLease t2; (MarketplaceListingSyncStatus Status,Guid? LeaseToken,DateTimeOffset? LeaseUntil,DateTimeOffset? HeartbeatAt,DateTimeOffset UpdatedAt,int AttemptCount,long Version,Guid? ConfirmedOperationIdAtStart,bool EnumerationCompleted) t2Snapshot;await using(var db=Context()){t2=(await new MarketplaceListingSyncLeaseAuthority(db).ReclaimAsync(runId,now.AddMinutes(2),lease))!;var kernel=new MarketplaceListingSyncRunKernel(db);await kernel.MarkEnumerationCompletedWithLeaseAsync(runId,t2.LeaseToken,now.AddMinutes(2),lease);await kernel.RecordOutcomeWithLeaseAsync(runId,t2.LeaseToken,itemId,MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR,"LISTING_READ_FAILED",null,false,false,now.AddMinutes(2),lease);var run=await db.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x=>x.Id==runId);t2Snapshot=(run.Status,run.LeaseToken,run.LeaseUntil,run.HeartbeatAt,run.UpdatedAt,run.AttemptCount,run.Version,run.ConfirmedOperationIdAtStart,run.EnumerationCompleted);}
        resume.SetResult();var staleResults=await stale;staleResults.enumeration.Should().BeOfType<MarketplaceListingSyncLeaseLostException>();staleResults.record.Should().BeOfType<MarketplaceListingSyncLeaseLostException>();
        await using(var db=Context()){var kernel=new MarketplaceListingSyncRunKernel(db);var staleFinalize=await Record.ExceptionAsync(()=>kernel.FinalizeWithLeaseAsync(runId,t1.LeaseToken,"RUN_STOPPED",now.AddMinutes(2),lease));staleFinalize.Should().BeOfType<MarketplaceListingSyncLeaseLostException>();var item=await db.Set<MarketplaceListingSyncRunItem>().SingleAsync(x=>x.Id==itemId);item.SafeErrorCode.Should().Be("LISTING_READ_FAILED");item.AttemptCount.Should().Be(1);var run=await db.Set<MarketplaceListingSyncRun>().SingleAsync(x=>x.Id==runId);run.Status.Should().Be(t2Snapshot.Status);run.LeaseToken.Should().Be(t2Snapshot.LeaseToken);run.LeaseUntil.Should().Be(t2Snapshot.LeaseUntil);run.HeartbeatAt.Should().Be(t2Snapshot.HeartbeatAt);run.UpdatedAt.Should().Be(t2Snapshot.UpdatedAt);run.AttemptCount.Should().Be(t2Snapshot.AttemptCount);run.Version.Should().Be(t2Snapshot.Version);run.EnumerationCompleted.Should().Be(t2Snapshot.EnumerationCompleted);run.ConfirmedOperationIdAtStart.Should().Be(t2Snapshot.ConfirmedOperationIdAtStart);run.LeaseToken.Should().Be(t2.LeaseToken);run.LeaseUntil.Should().Be(t2.LeaseUntil);run.HeartbeatAt.Should().Be(now.AddMinutes(2));run.UpdatedAt.Should().Be(now.AddMinutes(2));run.AttemptCount.Should().Be(2);run.Version.Should().Be(4);run.EnumerationCompleted.Should().BeTrue();run.ConfirmedOperationIdAtStart.Should().Be(Operations[0]);}
        var faultRun=await CreateRunAsync(now);MarketplaceListingSyncLease faultToken;Guid faultItem;await using(var db=Context()){faultToken=(await new MarketplaceListingSyncLeaseAuthority(db).ClaimAsync(faultRun,now,lease))!;faultItem=await AddItemWithLeaseAsync(faultRun,faultToken.LeaseToken,now);var authority=new MarketplaceListingSyncLeaseAuthority(db);var fault=await Record.ExceptionAsync(()=>authority.ExecuteWithLeaseAsync(faultRun,faultToken.LeaseToken,now,lease,async ct=>{await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_listing_sync_run_item SET outcome = {"TRANSIENT_ERROR"}, safe_error_code = {"FAULT"}, completed_at = {now}, updated_at = {now}, attempt_count = 1 WHERE id = {faultItem}",ct);throw new InvalidOperationException("FAULT_AFTER_WRITE");}));fault.Should().BeOfType<InvalidOperationException>();}
        await using(var db=Context()){var item=await db.Set<MarketplaceListingSyncRunItem>().SingleAsync(x=>x.Id==faultItem);item.Outcome.Should().Be(MarketplaceListingSyncItemOutcome.PENDING);item.CompletedAt.Should().BeNull();}
    }
}
