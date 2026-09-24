using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Verce.Api.Commerce;
using Verce.Modules.Commerce;
using Verce.Modules.Settings;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Commerce;

[Collection(PostgresCollection.Name)]
public sealed class CommercePersistenceIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Channel_offer_product_channel_identity_is_unique()
    {
        var product = Guid.NewGuid(); var channel = Guid.NewGuid();
        await using (var db = fixture.CreateContext())
        {
            db.Add(new ChannelOffer(product, channel, 10m, ChannelOfferPriceSource.MANUAL));
            await db.SaveChangesAsync();
        }
        await using var duplicate = fixture.CreateContext();
        duplicate.Add(new ChannelOffer(product, channel, 11m, ChannelOfferPriceSource.MANUAL));
        var act = async () => await duplicate.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Concurrent_duplicate_channel_offers_have_exactly_one_database_winner()
    {
        var product = Guid.NewGuid(); var channel = Guid.NewGuid();
        async Task<bool> TryInsertAsync(decimal price)
        {
            await using var db = fixture.CreateContext();
            db.Add(new ChannelOffer(product, channel, price, ChannelOfferPriceSource.MANUAL));
            try { await db.SaveChangesAsync(); return true; }
            catch (DbUpdateException) { return false; }
        }

        var results = await Task.WhenAll(TryInsertAsync(20m), TryInsertAsync(21m));
        results.Count(x => x).Should().Be(1);
        await using var proof = fixture.CreateContext();
        (await proof.Set<ChannelOffer>().CountAsync(x => x.ProductId == product && x.SalesChannelId == channel)).Should().Be(1);
    }

    [Fact]
    public async Task Listing_linkage_check_rejects_unlinked_row_with_product()
    {
        // The domain prevents malformed state; this directly proves the physical database
        // constraint remains authoritative for callers that bypass the aggregate.
        var account = await CreateAccountAsync();
        await using var db = fixture.CreateContext();
        var act = async () => await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO commerce.marketplace_listing
            (id, marketplace_account_id, external_listing_id, observed_status, linkage_state, sync_state, product_id, created_at, version)
            VALUES ({Guid.NewGuid()}, {account.Id}, {"invalid-" + Guid.NewGuid()}, 'ACTIVE', 'UNLINKED', 'NEVER_SYNCED', {Guid.NewGuid()}, now(), 1)
            """);
        var failure = await act.Should().ThrowAsync<PostgresException>();
        failure.Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Account_and_listing_external_identities_are_unique_per_parent()
    {
        var account = await CreateAccountAsync();
        await using (var db = fixture.CreateContext())
        {
            db.Add(new MarketplaceListing(account.Id, "same-listing", null, MarketplaceListingStatus.ACTIVE));
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            db.Add(new MarketplaceListing(account.Id, "same-listing", null, MarketplaceListingStatus.ACTIVE));
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }

        await using (var db = fixture.CreateContext())
        {
            db.Add(new MarketplaceAccount(account.ProviderCode, account.ExternalAccountId, Guid.NewGuid(), "Duplicada"));
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }
    }

    [Fact]
    public async Task Profile_image_constraints_reject_duplicate_asset_sort_and_primary()
    {
        var profile = new ProductCommercialProfile(Guid.NewGuid());
        profile.AddImage(Guid.NewGuid(), CommercialImageRole.PRIMARY, 0, "Principal");
        await using (var db = fixture.CreateContext())
        {
            db.Add(profile);
            await db.SaveChangesAsync();
        }

        async Task InsertAsync(Guid asset, string role, int sort)
        {
            await using var db = fixture.CreateContext();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO commerce.product_commercial_image
                (id, product_commercial_profile_id, brand_asset_id, role, sort_order, created_at)
                VALUES ({Guid.NewGuid()}, {profile.Id}, {asset}, {role}, {sort}, now())
                """);
        }

        var original = profile.Images.Single();
        await FluentActions.Awaiting(() => InsertAsync(original.BrandAssetId, "GALLERY", 1))
            .Should().ThrowAsync<PostgresException>().Where(x => x.SqlState == PostgresErrorCodes.UniqueViolation);
        await FluentActions.Awaiting(() => InsertAsync(Guid.NewGuid(), "GALLERY", 0))
            .Should().ThrowAsync<PostgresException>().Where(x => x.SqlState == PostgresErrorCodes.UniqueViolation);
        await FluentActions.Awaiting(() => InsertAsync(Guid.NewGuid(), "PRIMARY", 2))
            .Should().ThrowAsync<PostgresException>().Where(x => x.SqlState == PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Product_commercial_profile_is_unique_per_product()
    {
        var product = Guid.NewGuid();
        await using (var db = fixture.CreateContext())
        {
            db.Add(new ProductCommercialProfile(product));
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
        {
            db.Add(new ProductCommercialProfile(product));
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }
    }

    [Fact]
    public async Task Observation_key_and_tag_assignments_are_physically_unique()
    {
        var account = await CreateAccountAsync();
        var listing = new MarketplaceListing(account.Id, "obs-" + Guid.NewGuid(), null, MarketplaceListingStatus.ACTIVE);
        var now = DateTimeOffset.UtcNow;
        listing.AddObservation("key", "fingerprint", ObservationProvenance.MANUAL, now, now);
        var tag = new CommercialTag("TAG-" + Guid.NewGuid().ToString("N"), "Teste");
        await using (var db = fixture.CreateContext())
        {
            db.AddRange(listing, tag);
            await db.SaveChangesAsync();
            db.Add(new MarketplaceListingTag(listing.Id, tag.Id));
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var act = async () => await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO commerce.marketplace_listing_observation
                (id, marketplace_listing_id, observation_key, fingerprint, provenance, observed_status, provider_observed_at, ingested_at, created_at)
                VALUES ({Guid.NewGuid()}, {listing.Id}, 'key', 'other', 'MANUAL', 'ACTIVE', now(), now(), now())
                """);
            await act.Should().ThrowAsync<PostgresException>().Where(x => x.SqlState == PostgresErrorCodes.UniqueViolation);
        }

        await using (var db = fixture.CreateContext())
        {
            db.Add(new MarketplaceListingTag(listing.Id, tag.Id));
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }
    }

    [Fact]
    public async Task Retention_deletes_only_expired_non_latest_observations_per_listing()
    {
        var account = await CreateAccountAsync();
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var first = ListingWithObservations(account.Id, now, ("a", -300), ("b", -200));
        var second = ListingWithObservations(account.Id, now, ("a", -300));
        var third = ListingWithObservations(account.Id, now, ("a", -300), ("b", -10));

        await using (var db = fixture.CreateContext())
        {
            db.AddRange(first, second, third);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var service = new MarketplaceListingObservationRetentionService(db, new AppSettingValueReader(db), new TestClock(now));
            // The shared PostgreSQL fixture also contains observations from other integration
            // cases. This case proves its own expired non-latest observations are purged;
            // the service's global count can include older rows created by those cases.
            (await service.PurgeExpiredAsync()).Should().BeGreaterThanOrEqualTo(2);
        }

        await using (var db = fixture.CreateContext())
        {
            (await db.Set<MarketplaceListingObservation>().CountAsync(x => x.MarketplaceListingId == first.Id)).Should().Be(1);
            (await db.Set<MarketplaceListingObservation>().CountAsync(x => x.MarketplaceListingId == second.Id)).Should().Be(1);
            // Every listing begins with a durable normalized initial observation. The recent
            // observation is also retained, so this listing keeps both records.
            (await db.Set<MarketplaceListingObservation>().CountAsync(x => x.MarketplaceListingId == third.Id)).Should().Be(2);
        }
    }

    [Fact]
    public async Task Retention_setting_changes_the_cutoff()
    {
        var account = await CreateAccountAsync();
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var listing = ListingWithObservations(account.Id, now, ("old", -45), ("latest", -10));
        await using (var db = fixture.CreateContext())
        {
            var setting = await db.Set<AppSetting>().SingleOrDefaultAsync(x => x.Key == "commerce.listing_observation_retention_days");
            if (setting is null)
            {
                setting = new AppSetting("commerce.listing_observation_retention_days", "60", AppSettingValueType.Int,
                    "commerce", "Retenção de observações de listagem");
                db.Add(setting);
            }
            else setting.Update("60", Guid.NewGuid(), now);
            db.Add(listing);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var service = new MarketplaceListingObservationRetentionService(db, new AppSettingValueReader(db), new TestClock(now));
            (await service.PurgeExpiredAsync()).Should().Be(0, "45 days is inside a configured 60-day window");
            var setting = await db.Set<AppSetting>().SingleAsync(x => x.Key == "commerce.listing_observation_retention_days");
            setting.Update("30", Guid.NewGuid(), now);
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
        {
            var service = new MarketplaceListingObservationRetentionService(db, new AppSettingValueReader(db), new TestClock(now));
            (await service.PurgeExpiredAsync()).Should().Be(1, "the same row expires under a 30-day window");
            var setting = await db.Set<AppSetting>().SingleAsync(x => x.Key == "commerce.listing_observation_retention_days");
            setting.Update("180", Guid.NewGuid(), now);
            await db.SaveChangesAsync();
        }
    }

    private async Task<MarketplaceAccount> CreateAccountAsync()
    {
        var provider = new MarketplaceProvider("T" + Guid.NewGuid().ToString("N"), "Provider de teste");
        var account = new MarketplaceAccount(provider.Code, "account-" + Guid.NewGuid(), Guid.NewGuid(), "Conta de teste");
        await using var db = fixture.CreateContext();
        db.AddRange(provider, account);
        await db.SaveChangesAsync();
        return account;
    }

    private static MarketplaceListing ListingWithObservations(Guid accountId, DateTimeOffset now, params (string Key, int Days)[] facts)
    {
        var listing = new MarketplaceListing(accountId, "retention-" + Guid.NewGuid(), null, MarketplaceListingStatus.ACTIVE);
        foreach (var fact in facts)
        {
            var at = now.AddDays(fact.Days);
            listing.AddObservation(fact.Key, fact.Key, ObservationProvenance.MANUAL, at, at);
        }
        return listing;
    }
}
