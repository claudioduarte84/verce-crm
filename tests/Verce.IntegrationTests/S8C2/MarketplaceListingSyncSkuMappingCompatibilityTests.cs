using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Verce.Api.Commerce;
using Verce.Modules.Commerce;
using Verce.Platform.Audit;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;
using Verce.Infrastructure.Marketplaces;

namespace Verce.IntegrationTests.S8C2;

/// <summary>Slice 1: a mapping created or removed through the B7 service has exactly the representation the certified SKU-LINK-1 consumer reads (Stream B is unchanged).</summary>
public sealed partial class MarketplaceListingSyncExecutionIntegrationTests
{
    private MarketplaceSkuMappingService CreateMappingService(VerceDbContext db)
    {
        var ambient = new AmbientOperationContext(Guid.NewGuid(), AuditSource.Api, _clock.UtcNow); ambient.SetActor(null, "tester");
        return new MarketplaceSkuMappingService(new Verce.Platform.UnitOfWork.UnitOfWork(db, ambient, new NoDispatcher(), _clock), _clock, new CommerceProductEligibilityReader(db));
    }

    [Fact]
    public async Task B7_confirmed_mapping_feeds_SKU_LINK_1_only_in_its_account_and_stops_after_deactivation_without_relinking_history()
    {
        var accountA = await SeedAccountAsync(); var accountB = await SeedAccountAsync(); var product = await SeedProductAsync(true); var actor = Guid.NewGuid();
        MarketplaceSkuMappingResult confirmed;
        await using (var db = Context()) confirmed = await CreateMappingService(db).ConfirmAsync(accountA, "  SKU-B7  ", product, null, null, actor);
        confirmed.Succeeded.Should().BeTrue();
        var mappingId = confirmed.MappingId!.Value;

        async Task RunAsync(Guid account, string listingId, int minute)
        {
            var reader = new ScriptedReader([listingId], _clock) { Facts = { [listingId] = FactsFor(listingId, "SKU-B7") } };
            await using var db = Context();
            var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow.AddMinutes(minute));
            (await CreateExecution(db, reader, productEligibility: new CommerceProductEligibilityReader(db)).RunAsync(run.Id))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        }

        await RunAsync(accountA, "MLB-B7-A1", 0);
        await RunAsync(accountB, "MLB-B7-B1", 1);
        await using (var db = Context())
        {
            var linked = await db.Set<MarketplaceListing>().SingleAsync(x => x.MarketplaceAccountId == accountA && x.ExternalListingId == "MLB-B7-A1");
            linked.LinkageState.Should().Be(MarketplaceLinkageState.LINKED); linked.ProductId.Should().Be(product);
            linked.LinkageSource.Should().Be(MarketplaceListingLinkageSource.DETERMINISTIC_SKU); linked.SkuMappingId.Should().Be(mappingId); linked.ChannelOfferId.Should().BeNull();
            var other = await db.Set<MarketplaceListing>().SingleAsync(x => x.MarketplaceAccountId == accountB && x.ExternalListingId == "MLB-B7-B1");
            other.LinkageState.Should().Be(MarketplaceLinkageState.UNLINKED, "a mapping of account A never authorizes account B"); other.ProductId.Should().BeNull();
        }

        long version; await using (var db = Context()) version = (await db.Set<MarketplaceAccountSkuMapping>().AsNoTracking().SingleAsync(x => x.Id == mappingId)).Version;
        await using (var db = Context()) (await CreateMappingService(db).DeactivateAsync(mappingId, version, actor)).Succeeded.Should().BeTrue();

        await RunAsync(accountA, "MLB-B7-A2", 2);
        await using (var db = Context())
        {
            var after = await db.Set<MarketplaceListing>().SingleAsync(x => x.MarketplaceAccountId == accountA && x.ExternalListingId == "MLB-B7-A2");
            after.LinkageState.Should().Be(MarketplaceLinkageState.UNLINKED, "an inactive mapping is never consumed"); after.ProductId.Should().BeNull();
            var history = await db.Set<MarketplaceListing>().SingleAsync(x => x.MarketplaceAccountId == accountA && x.ExternalListingId == "MLB-B7-A1");
            history.LinkageState.Should().Be(MarketplaceLinkageState.LINKED, "deactivation never unlinks history"); history.ProductId.Should().Be(product); history.SkuMappingId.Should().Be(mappingId);
        }
    }
}
