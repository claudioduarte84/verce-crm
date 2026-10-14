using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Verce.Modules.Commerce;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.S8C2;

public sealed class S8C2RunKernelIntegrationTests : IAsyncLifetime
{
    private PostgreSqlContainer _container=null!; private string _cs=string.Empty;
    public async Task InitializeAsync(){VerceDbContext.ConfigureModuleAssemblies(Verce.Api.ModuleAssemblyCatalog.All);_container=new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("verce_s8c2_b1b").WithUsername("verce").WithPassword("verce_test_only").Build();await _container.StartAsync();_cs=_container.GetConnectionString();await using var db=Context();await db.Database.MigrateAsync();await SeedAsync();}
    public async Task DisposeAsync()=>await _container.DisposeAsync();
    private VerceDbContext Context()=>new(new DbContextOptionsBuilder<VerceDbContext>().UseNpgsql(_cs).UseSnakeCaseNamingConvention().Options);
    private async Task SeedAsync(){var n=DateTimeOffset.UtcNow;var channel=Guid.NewGuid();await using var c=new NpgsqlConnection(_cs);await c.OpenAsync();await using var cmd=new NpgsqlCommand("""INSERT INTO pricing.sales_channel (id,code,name,kind,active,created_at,version) VALUES (@c,'B1B','B1B','Marketplace',true,@n,1); INSERT INTO commerce.marketplace_provider (code,name) VALUES ('MERCADO_LIVRE','Mercado Livre') ON CONFLICT DO NOTHING; INSERT INTO commerce.marketplace_account (id,provider_code,external_account_id,sales_channel_id,display_name,active,sync_state,created_at,version) VALUES (@a,'MERCADO_LIVRE','b1b-account',@c,'B1B',true,'NEVER_SYNCED',@n,1); INSERT INTO commerce.marketplace_account (id,provider_code,external_account_id,sales_channel_id,display_name,active,sync_state,created_at,version) VALUES (@b,'MERCADO_LIVRE','b1b-race',@c,'B1B race',true,'NEVER_SYNCED',@n,1);""",c);cmd.Parameters.AddWithValue("c",channel);cmd.Parameters.AddWithValue("a",Account);cmd.Parameters.AddWithValue("b",RaceAccount);cmd.Parameters.AddWithValue("n",n);await cmd.ExecuteNonQueryAsync();}
    private static readonly Guid Account=Guid.Parse("11111111-1111-1111-1111-111111111111"); private static readonly Guid RaceAccount=Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Failed_only_is_server_derived_preserves_ancestry_and_active_run_race_is_database_safe()
    {
        var now=DateTimeOffset.UtcNow; var ids=Enumerable.Range(1,100).Select(i=>$"MLB-{i:000}").ToArray(); MarketplaceListingSyncRun r1;
        await using(var db=Context()){db.AddRange(ids.Take(95).Select(id=>new MarketplaceListing(Account,id,null,MarketplaceListingStatus.ACTIVE)));await db.SaveChangesAsync();var kernel=new MarketplaceListingSyncRunKernel(db);var authority=new MarketplaceListingSyncLeaseAuthority(db);r1=await kernel.CreateFullAsync(Account,Guid.NewGuid(),now);var lease=(await authority.ClaimAsync(r1.Id,now,TimeSpan.FromMinutes(2)))!;await kernel.RegisterTargetsWithLeaseAsync(r1.Id,lease.LeaseToken,ids,MarketplaceListingSyncItemOrigin.ENUMERATED,now,TimeSpan.FromMinutes(2));await kernel.MarkEnumerationCompletedWithLeaseAsync(r1.Id,lease.LeaseToken,now,TimeSpan.FromMinutes(2));var items=await db.Set<MarketplaceListingSyncRunItem>().Where(x=>x.SyncRunId==r1.Id).ToDictionaryAsync(x=>x.ExternalListingId);var listings=await db.Set<MarketplaceListing>().Where(x=>x.MarketplaceAccountId==Account).ToDictionaryAsync(x=>x.ExternalListingId);foreach(var id in ids.Take(95))await kernel.RecordOutcomeWithLeaseAsync(r1.Id,lease.LeaseToken,items[id].Id,MarketplaceListingSyncItemOutcome.FOUND,null,listings[id].Id,true,false,now,TimeSpan.FromMinutes(2));foreach(var id in ids.Skip(95))await kernel.RecordOutcomeWithLeaseAsync(r1.Id,lease.LeaseToken,items[id].Id,MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR,"LISTING_READ_FAILED",null,false,false,now,TimeSpan.FromMinutes(2));var terminal=await kernel.FinalizeWithLeaseAsync(r1.Id,lease.LeaseToken,null,now,TimeSpan.FromMinutes(2));terminal.Status.Should().Be(MarketplaceListingSyncStatus.PARTIAL);(await kernel.GetCountersAsync(r1.Id)).Should().Be(new MarketplaceListingSyncCounters(100,95,5,0,0,95,0));}
        MarketplaceListingSyncRun r2; await using(var db=Context()){var kernel=new MarketplaceListingSyncRunKernel(db);var authority=new MarketplaceListingSyncLeaseAuthority(db);r2=await kernel.CreateFailedOnlyRetryAsync(r1.Id,Guid.NewGuid(),now);var lease=(await authority.ClaimAsync(r2.Id,now,TimeSpan.FromMinutes(2)))!;var targets=await db.Set<MarketplaceListingSyncRunItem>().Where(x=>x.SyncRunId==r2.Id).ToListAsync();targets.Should().HaveCount(5);targets.Should().OnlyContain(x=>x.Origin==MarketplaceListingSyncItemOrigin.RETRY_TARGET&&ids.Skip(95).Contains(x.ExternalListingId));r2.RetryOfRunId.Should().Be(r1.Id);r2.RootRunId.Should().Be(r1.Id);await kernel.RecordOutcomeWithLeaseAsync(r2.Id,lease.LeaseToken,targets.First().Id,MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR,"LISTING_READ_FAILED",null,false,false,now,TimeSpan.FromMinutes(2));await kernel.FinalizeWithLeaseAsync(r2.Id,lease.LeaseToken,"RUN_STOPPED",now,TimeSpan.FromMinutes(2));}
        await using(var db=Context()){var r3=await new MarketplaceListingSyncRunKernel(db).CreateFailedOnlyRetryAsync(r2.Id,Guid.NewGuid(),now);r3.RetryOfRunId.Should().Be(r2.Id);r3.RootRunId.Should().Be(r1.Id);}
        using var gate=new Barrier(2); var attempts=Enumerable.Range(0,2).Select(_=>Task.Run(async ()=>{await using var db=Context();var kernel=new MarketplaceListingSyncRunKernel(db);gate.SignalAndWait();return await Record.ExceptionAsync(()=>kernel.CreateFullAsync(RaceAccount,Guid.NewGuid(),now));})).ToArray();var results=await Task.WhenAll(attempts);results.Count(x=>x is null).Should().Be(1);results.Count(x=>x is DbUpdateException).Should().Be(1);
    }

    [Fact]
    public async Task Sku_mapping_uses_canonical_identity_and_postgresql_enforces_it()
    {
        var now=DateTimeOffset.UtcNow;
        MarketplaceSkuNormalizer.Normalize(" e\u0301\u0001 ").Should().Be("é");
        var empty=()=>MarketplaceSkuNormalizer.Normalize("\u0000");
        empty.Should().Throw<ArgumentException>().WithMessage("SKU_MAPPING_SKU_INVALID*");
        await using var db=Context();
        db.Add(new MarketplaceAccountSkuMapping(Account," e\u0301\u0001 ",Guid.NewGuid(),Guid.NewGuid(),now));
        await db.SaveChangesAsync();
        db.Add(new MarketplaceAccountSkuMapping(Account,"é",Guid.NewGuid(),Guid.NewGuid(),now));
        await Assert.ThrowsAsync<DbUpdateException>(()=>db.SaveChangesAsync());
    }
}
