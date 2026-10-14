using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Verce.Modules.Commerce;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.S8C2;

/// <summary>
/// M059 / Gate 058 E01-7 (D9.2): "S8C.1-head upgrade with seeded listings, observations, links" — the earlier upgrade test seeded only a
/// channel and an account. Here the database is walked DOWN to the S8C.1 head (which also exercises the S8C.2 Down on a real schema), representative
/// S8B/S8C.1-era commerce history is inserted with raw SQL at THAT schema, the S8C.2 migration is applied, and the backfill is proven row by row.
/// The disposable Testcontainers database is owned by this fixture; no operational connection string is reachable.
/// </summary>
public sealed partial class MarketplaceListingSyncExecutionIntegrationTests
{
    private const string MigrationS8C1Head = "20260925092305_AddS8C1MarketplaceAuthorizationFoundation";
    private const string MigrationS8C2 = "20260927114823_AddS8C2MarketplaceListingSync";

    private async Task MigrateToAsync(string target) { await using var db = Context(); await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(target); }
    private async Task MigrateToHeadAsync() { await using var db = Context(); await db.Database.MigrateAsync(); }

    private sealed record LegacyListing(Guid Id, string ExternalId, string LinkageState, string SyncState, string? SyncError, string? Sku, string Title, decimal? Price, string Status, Guid? ProductId);

    private async Task InsertLegacyListingAsync(Guid account, LegacyListing l)
    {
        await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO commerce.marketplace_listing (id,marketplace_account_id,external_listing_id,external_sku,product_id,channel_offer_id,title_snapshot,observed_price,listing_url,observed_status,provider_native_status,linkage_state,sync_state,provider_observed_at,last_sync_attempt_at,last_successful_sync_at,sync_error,created_at,version)
            VALUES (@id,@account,@ext,@sku,@product,NULL,@title,@price,NULL,@status,NULL,@linkage,@sync,@now,@now,@now,@error,@now,1)
            """, connection);
        command.Parameters.AddWithValue("id", l.Id); command.Parameters.AddWithValue("account", account); command.Parameters.AddWithValue("ext", l.ExternalId);
        command.Parameters.AddWithValue("sku", (object?)l.Sku ?? DBNull.Value); command.Parameters.AddWithValue("product", (object?)l.ProductId ?? DBNull.Value);
        command.Parameters.AddWithValue("title", l.Title); command.Parameters.AddWithValue("price", (object?)l.Price ?? DBNull.Value); command.Parameters.AddWithValue("status", l.Status);
        command.Parameters.AddWithValue("linkage", l.LinkageState); command.Parameters.AddWithValue("sync", l.SyncState); command.Parameters.AddWithValue("error", (object?)l.SyncError ?? DBNull.Value);
        command.Parameters.AddWithValue("now", _clock.UtcNow);
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertLegacyObservationAsync(Guid listing, string key, string fingerprint, DateTimeOffset ingestedAt, string title, decimal? price, string status)
    {
        await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO commerce.marketplace_listing_observation (id,marketplace_listing_id,external_sku,title_snapshot,observed_price,observed_status,provider_native_status,observation_key,fingerprint,provenance,provider_observed_at,ingested_at,created_at)
            VALUES (@id,@listing,NULL,@title,@price,@status,NULL,@key,@fingerprint,'MANUAL',@observed,@ingested,@ingested)
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("listing", listing); command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("price", (object?)price ?? DBNull.Value); command.Parameters.AddWithValue("status", status); command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("fingerprint", fingerprint); command.Parameters.AddWithValue("observed", ingestedAt); command.Parameters.AddWithValue("ingested", ingestedAt);
        await command.ExecuteNonQueryAsync();
    }

    private Task<string> LegacyColumnsJsonAsync() => JsonAsync("""
        SELECT coalesce(jsonb_agg(jsonb_build_object('id',l.id,'account',l.marketplace_account_id,'ext',l.external_listing_id,'sku',l.external_sku,'product',l.product_id,'offer',l.channel_offer_id,'title',l.title_snapshot,'price',l.observed_price,
               'url',l.listing_url,'status',l.observed_status,'native',l.provider_native_status,'linkage',l.linkage_state,'sync',l.sync_state,'observedAt',l.provider_observed_at,'attempt',l.last_sync_attempt_at,'success',l.last_successful_sync_at,'error',l.sync_error,'version',l.version) ORDER BY l.external_listing_id), '[]'::jsonb)::text
        FROM commerce.marketplace_listing l
        """);

    private Task<string> LegacyObservationJsonAsync() => JsonAsync("""
        SELECT coalesce(jsonb_agg(jsonb_build_object('id',o.id,'listing',o.marketplace_listing_id,'sku',o.external_sku,'title',o.title_snapshot,'price',o.observed_price,'status',o.observed_status,'key',o.observation_key,'fingerprint',o.fingerprint,
               'provenance',o.provenance,'observedAt',o.provider_observed_at,'ingested',o.ingested_at) ORDER BY o.id), '[]'::jsonb)::text
        FROM commerce.marketplace_listing_observation o
        """);

    [Fact]
    public async Task D9_2_upgrade_from_the_S8C1_head_with_representative_listings_observations_and_links_backfills_exactly_and_the_first_sync_is_conservative()
    {
        var account = await SeedAccountAsync("777"); var product = Guid.NewGuid(); var t = _clock.UtcNow;
        await MigrateToAsync(MigrationS8C1Head);
        (await JsonAsync("SELECT to_regclass('commerce.marketplace_listing_sync_run')::text")).Should().Be("null", "the database really is at the S8C.1 head before the seed");

        var manualLinked = new LegacyListing(Guid.NewGuid(), "MLB-UP-LINKED", "LINKED", "SYNCED", null, "SKU-UP-1", "Linked title", 19.90m, "ACTIVE", product);
        var unlinked = new LegacyListing(Guid.NewGuid(), "MLB-UP-UNLINKED", "UNLINKED", "NEVER_SYNCED", null, "SKU-UP-2", "Unlinked title", 5.00m, "PAUSED", null);
        var needsReview = new LegacyListing(Guid.NewGuid(), "MLB-UP-REVIEW", "NEEDS_REVIEW", "SYNCED", null, "SKU-UP-3", "Review title", null, "ACTIVE", null);
        var noHistory = new LegacyListing(Guid.NewGuid(), "MLB-UP-NOHISTORY", "UNLINKED", "ERROR", "LEGACY_FAILURE", null, "No history", 1.00m, "INACTIVE", null);
        foreach (var l in new[] { manualLinked, unlinked, needsReview, noHistory }) await InsertLegacyListingAsync(account, l);

        var latestFingerprint = MarketplaceListingFingerprint.ComputeV2("SKU-UP-1", "Linked title", 19.90m, MarketplaceListingStatus.ACTIVE, null, null);
        var tieA = new string('a', 64); var tieB = new string('b', 64);
        await InsertLegacyObservationAsync(manualLinked.Id, "obs-1", new string('1', 64), t.AddDays(-30), "Older title", 10.00m, "ACTIVE");
        await InsertLegacyObservationAsync(manualLinked.Id, "obs-2", new string('2', 64), t.AddDays(-20), "Old title", 15.00m, "ACTIVE");
        await InsertLegacyObservationAsync(manualLinked.Id, "obs-3", latestFingerprint, t.AddDays(-1), "Linked title", 19.90m, "ACTIVE");
        await InsertLegacyObservationAsync(unlinked.Id, "obs-u", MarketplaceListingFingerprint.ComputeV2("SKU-UP-2", "Unlinked title", 5.00m, MarketplaceListingStatus.PAUSED, null, null), t.AddDays(-2), "Unlinked title", 5.00m, "PAUSED");
        await InsertLegacyObservationAsync(needsReview.Id, "obs-r1", tieA, t.AddDays(-3), "Review title", null, "ACTIVE");
        await InsertLegacyObservationAsync(needsReview.Id, "obs-r2", tieB, t.AddDays(-3), "Review title", null, "ACTIVE");        // two DIFFERENT fingerprints at the same latest instant

        var listingsBefore = await LegacyColumnsJsonAsync(); var observationsBefore = await LegacyObservationJsonAsync();

        await MigrateToHeadAsync();                                         // S8C.2 Up over real data
        (await JsonAsync("SELECT to_regclass('commerce.marketplace_listing_sync_run')::text")).Should().NotBe("null");

        // ---- nothing that existed was lost or altered
        (await LegacyColumnsJsonAsync()).Should().Be(listingsBefore, "every S8C.1-era listing column is byte-identical after the upgrade");
        (await LegacyObservationJsonAsync()).Should().Be(observationsBefore, "every observation (and its non-null provider_observed_at) is preserved exactly");

        // ---- the backfill
        await using (var db = Context())
        {
            var rows = await db.Set<MarketplaceListing>().AsNoTracking().Where(x => x.MarketplaceAccountId == account).ToDictionaryAsync(x => x.ExternalListingId);
            rows["MLB-UP-LINKED"].LinkageSource.Should().Be(MarketplaceListingLinkageSource.MANUAL, "every pre-existing LINKED listing was linked by a human");
            rows["MLB-UP-LINKED"].SkuMappingId.Should().BeNull(); rows["MLB-UP-LINKED"].ProductId.Should().Be(product);
            foreach (var other in new[] { "MLB-UP-UNLINKED", "MLB-UP-REVIEW", "MLB-UP-NOHISTORY" }) { rows[other].LinkageSource.Should().BeNull(); rows[other].SkuMappingId.Should().BeNull(); }
            rows.Values.Should().OnlyContain(x => !x.AutoLinkSuppressed, "no legacy listing is retroactively suppressed");
            rows.Values.Should().OnlyContain(x => x.VariationCount == null, "unknown variation count stays unknown — never 0");
            rows["MLB-UP-LINKED"].LatestObservationFingerprint.Should().Be(latestFingerprint, "the latest observation's fingerprint");
            rows["MLB-UP-UNLINKED"].LatestObservationFingerprint.Should().NotBeNull();
            rows["MLB-UP-REVIEW"].LatestObservationFingerprint.Should().BeNull("two different fingerprints tie at the latest instant: no authoritative latest, so NULL (the first sync appends conservatively)");
            rows["MLB-UP-NOHISTORY"].LatestObservationFingerprint.Should().BeNull();
            rows["MLB-UP-REVIEW"].LinkageState.Should().Be(MarketplaceLinkageState.NEEDS_REVIEW); rows["MLB-UP-NOHISTORY"].SyncState.Should().Be(MarketplaceSyncState.ERROR); rows["MLB-UP-NOHISTORY"].SyncError.Should().Be("LEGACY_FAILURE");
            (await db.Set<MarketplaceListingObservation>().AsNoTracking().AllAsync(x => x.VariationCount == null)).Should().BeTrue();
        }
        (await JsonAsync("SELECT count(*)::text FROM commerce.marketplace_listing_sync_run")).Should().Be("0");
        (await JsonAsync("SELECT state FROM commerce.marketplace_provider_capability WHERE provider_code='MERCADO_LIVRE' AND capability_code='LISTINGS_READ'")).Should().Be("SUPPORTED");

        // ---- the first sync after the upgrade is correct on top of the backfill
        var linkedFacts = FactsFor("MLB-UP-LINKED", "SKU-UP-1") with { Title = "Linked title", ObservedPrice = 19.90m, ObservedStatus = MarketplaceListingStatus.ACTIVE, ProviderNativeStatus = null, VariationCount = 0 };
        var unlinkedFacts = FactsFor("MLB-UP-UNLINKED", "SKU-UP-2") with { Title = "Unlinked title", ObservedPrice = 5.00m, ObservedStatus = MarketplaceListingStatus.PAUSED, ProviderNativeStatus = null, VariationCount = 0 };
        var reviewFacts = FactsFor("MLB-UP-REVIEW", "SKU-UP-3") with { Title = "Review title", ObservedPrice = null, ObservedStatus = MarketplaceListingStatus.ACTIVE, ProviderNativeStatus = null, VariationCount = 0 };
        async Task SyncAsync(int minute)
        {
            var reader = new ScriptedReader(["MLB-UP-LINKED", "MLB-UP-UNLINKED", "MLB-UP-REVIEW", "MLB-UP-NOHISTORY"], _clock) { Facts = { ["MLB-UP-LINKED"] = linkedFacts, ["MLB-UP-UNLINKED"] = unlinkedFacts, ["MLB-UP-REVIEW"] = reviewFacts } };
            await using var db = Context();
            var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), t.AddMinutes(minute));
            (await CreateExecution(db, reader).RunAsync(run.Id))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        }
        await SyncAsync(1);
        async Task<Dictionary<string, MarketplaceListing>> LoadAsync() { await using var db = Context(); return await db.Set<MarketplaceListing>().AsNoTracking().Include(x => x.Observations).Where(x => x.MarketplaceAccountId == account).ToDictionaryAsync(x => x.ExternalListingId); }
        var afterFirst = await LoadAsync();
        afterFirst["MLB-UP-LINKED"].LinkageState.Should().Be(MarketplaceLinkageState.LINKED, "the legacy manual link survives the first sync"); afterFirst["MLB-UP-LINKED"].LinkageSource.Should().Be(MarketplaceListingLinkageSource.MANUAL); afterFirst["MLB-UP-LINKED"].ProductId.Should().Be(product);
        afterFirst["MLB-UP-REVIEW"].LinkageState.Should().Be(MarketplaceLinkageState.NEEDS_REVIEW, "NEEDS_REVIEW is preserved by the first sync");
        foreach (var id in new[] { "MLB-UP-LINKED", "MLB-UP-UNLINKED", "MLB-UP-REVIEW" })
        {
            afterFirst[id].SyncState.Should().Be(MarketplaceSyncState.SYNCED); afterFirst[id].SyncError.Should().BeNull();
            afterFirst[id].Observations.Count(o => o.Provenance == ObservationProvenance.PROVIDER_SYNC).Should().Be(1,
                "the legacy fingerprint was computed with an unknown variation count while the provider now states one: a genuine change of an observed fact, appended exactly once");
        }
        afterFirst["MLB-UP-LINKED"].Observations.Count(o => o.Provenance == ObservationProvenance.MANUAL).Should().Be(3, "legacy history is never rewritten");
        afterFirst["MLB-UP-NOHISTORY"].SyncState.Should().Be(MarketplaceSyncState.SYNCED, "a known listing the provider did not enumerate was directly re-read (here: enumerated) and its legacy ERROR cleared");
        var observationsAfterFirst = afterFirst.Values.Sum(x => x.Observations.Count);

        await SyncAsync(2);                                                    // idempotent re-import on the upgraded data
        (await LoadAsync()).Values.Sum(x => x.Observations.Count).Should().Be(observationsAfterFirst, "re-importing unchanged facts appends nothing");
    }

    [Fact]
    public async Task D9_2_a_legacy_observation_fingerprint_longer_than_64_characters_must_not_abort_the_upgrade()
    {
        // S8B accepted client-supplied fingerprints up to 128 characters (observation.fingerprint varchar(128)); the S8C.2 backfill copies the latest one into
        // listing.latest_observation_fingerprint char(64). A legitimate 100-character legacy value must not make the migration fail.
        var account = await SeedAccountAsync("777"); var t = _clock.UtcNow;
        await MigrateToAsync(MigrationS8C1Head);
        var listing = new LegacyListing(Guid.NewGuid(), "MLB-UP-LONGFP", "UNLINKED", "NEVER_SYNCED", null, null, "Long fingerprint", 3.00m, "ACTIVE", null);
        await InsertLegacyListingAsync(account, listing);
        await InsertLegacyObservationAsync(listing.Id, "obs-long", new string('f', 100), t.AddDays(-1), "Long fingerprint", 3.00m, "ACTIVE");
        var act = () => MigrateToHeadAsync();
        await act.Should().NotThrowAsync("a legitimate legacy fingerprint of 100 characters is valid S8B data");
        await using var db = Context();
        var migrated = await db.Set<MarketplaceListing>().AsNoTracking().SingleAsync(x => x.ExternalListingId == "MLB-UP-LONGFP");
        migrated.LatestObservationFingerprint.Should().BeNull("a fingerprint that is not a 64-character V2 hash cannot equal any future V2 fingerprint: unknown, not truncated");
    }

    [Fact]
    public async Task D9_2_down_after_provider_sync_observations_with_an_unknown_ProviderObservedAt_restores_the_not_null_column_without_losing_rows_and_re_upgrade_works()
    {
        var account = await SeedAccountAsync("777");
        var reader = new ScriptedReader(["MLB-DOWN-1"], _clock) { Facts = { ["MLB-DOWN-1"] = FactsFor("MLB-DOWN-1", "SKU-D1") with { ProviderObservedAt = null } } };
        await using (var db = Context())
        {
            var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
            (await CreateExecution(db, reader).RunAsync(run.Id))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        }
        (await JsonAsync("SELECT count(*)::text FROM commerce.marketplace_listing_observation WHERE provider_observed_at IS NULL")).Should().Be("1", "the provider-sync observation has an unknown observation instant");

        await MigrateToAsync(MigrationS8C1Head);                              // Down: must backfill the NOT NULL column from ingested_at
        (await JsonAsync("SELECT count(*)::text FROM commerce.marketplace_listing_observation")).Should().Be("1", "Down never drops observation rows");
        (await JsonAsync("SELECT (provider_observed_at = ingested_at)::text FROM commerce.marketplace_listing_observation")).Should().Be("true");
        (await JsonAsync("SELECT is_nullable FROM information_schema.columns WHERE table_schema='commerce' AND table_name='marketplace_listing_observation' AND column_name='provider_observed_at'")).Should().Be("NO");

        await MigrateToHeadAsync();                                           // Up again over the downgraded data
        (await JsonAsync("SELECT is_nullable FROM information_schema.columns WHERE table_schema='commerce' AND table_name='marketplace_listing_observation' AND column_name='provider_observed_at'")).Should().Be("YES");
        await using var verify = Context(); (await verify.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }
}
