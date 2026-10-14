using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Verce.Infrastructure.Marketplaces;
using Verce.Modules.Commerce;

namespace Verce.IntegrationTests.S8C2;

/// <summary>
/// M060 (Correct + Validate) — validation of the M059 fixes F02/F03 at the persistence/orchestration level, plus the characterization the open
/// architectural decisions need. Same hermetic harness as the B2 suite: disposable Testcontainers PostgreSQL, real
/// <see cref="Verce.Infrastructure.Marketplaces.MercadoLivre.MercadoLivreConnector"/> over a scripted wire, no operational database.
/// </summary>
public sealed partial class MarketplaceListingSyncExecutionIntegrationTests
{
    // ======================================================================== F03 — fingerprint length: migration matrix

    private static string Hex64(char c) => new(c, 64);

    [Fact]
    public async Task M060_F03_every_legacy_fingerprint_shape_upgrades_and_only_a_64_character_latest_value_is_carried_over()
    {
        var account = await SeedAccountAsync("777"); var t = _clock.UtcNow;
        await MigrateToAsync(MigrationS8C1Head);

        var shapes = new (string Id, string Fingerprint, string? Expected)[]
        {
            ("MLB-FP-64", Hex64('a'), Hex64('a')),                              // V2 shape: copied
            ("MLB-FP-64NONHEX", new string('z', 64), new string('z', 64)),      // exactly 64, not hex: copied (harmless: can never equal a V2 hash)
            ("MLB-FP-65", new string('b', 65), null),
            ("MLB-FP-128", new string('c', 128), null),
            ("MLB-FP-63", new string('d', 63), null),                           // shorter would be blank-padded by char(64) and never equal the original
            ("MLB-FP-1", "x", null),
            ("MLB-FP-EMPTY", "", null),
            ("MLB-FP-SPACES", new string(' ', 64), new string(' ', 64)),        // exactly 64 characters, copied verbatim (degenerate but not an error)
        };
        foreach (var (id, fingerprint, _) in shapes)
        {
            var listing = new LegacyListing(Guid.NewGuid(), id, "UNLINKED", "NEVER_SYNCED", null, null, id, 1.00m, "ACTIVE", null); await InsertLegacyListingAsync(account, listing);
            await InsertLegacyObservationAsync(listing.Id, "o-" + id, fingerprint, t.AddDays(-1), id, 1.00m, "ACTIVE");
        }
        // latest is long, an OLDER one is 64: the latest decides, so NULL (never silently fall back to a stale value)
        var mixed = new LegacyListing(Guid.NewGuid(), "MLB-FP-MIXED", "UNLINKED", "NEVER_SYNCED", null, null, "mixed", 1.00m, "ACTIVE", null); await InsertLegacyListingAsync(account, mixed);
        await InsertLegacyObservationAsync(mixed.Id, "o-old", Hex64('e'), t.AddDays(-5), "mixed", 1.00m, "ACTIVE");
        await InsertLegacyObservationAsync(mixed.Id, "o-new", new string('f', 100), t.AddDays(-1), "mixed", 1.00m, "ACTIVE");
        // the reverse: latest is 64, an older one is long: the latest decides
        var reverse = new LegacyListing(Guid.NewGuid(), "MLB-FP-REVERSE", "UNLINKED", "NEVER_SYNCED", null, null, "reverse", 1.00m, "ACTIVE", null); await InsertLegacyListingAsync(account, reverse);
        await InsertLegacyObservationAsync(reverse.Id, "o-old", new string('f', 100), t.AddDays(-5), "reverse", 1.00m, "ACTIVE");
        await InsertLegacyObservationAsync(reverse.Id, "o-new", Hex64('1'), t.AddDays(-1), "reverse", 1.00m, "ACTIVE");
        // shared latest instant: identical fingerprints are authoritative, different ones are a tie => NULL
        var sameTie = new LegacyListing(Guid.NewGuid(), "MLB-FP-TIE-SAME", "UNLINKED", "NEVER_SYNCED", null, null, "tie", 1.00m, "ACTIVE", null); await InsertLegacyListingAsync(account, sameTie);
        await InsertLegacyObservationAsync(sameTie.Id, "o-1", Hex64('2'), t.AddDays(-1), "tie", 1.00m, "ACTIVE"); await InsertLegacyObservationAsync(sameTie.Id, "o-2", Hex64('2'), t.AddDays(-1), "tie", 1.00m, "ACTIVE");
        var diffTie = new LegacyListing(Guid.NewGuid(), "MLB-FP-TIE-DIFF", "UNLINKED", "NEVER_SYNCED", null, null, "tie", 1.00m, "ACTIVE", null); await InsertLegacyListingAsync(account, diffTie);
        await InsertLegacyObservationAsync(diffTie.Id, "o-1", Hex64('3'), t.AddDays(-1), "tie", 1.00m, "ACTIVE"); await InsertLegacyObservationAsync(diffTie.Id, "o-2", Hex64('4'), t.AddDays(-1), "tie", 1.00m, "ACTIVE");
        var diffTieLong = new LegacyListing(Guid.NewGuid(), "MLB-FP-TIE-LONG", "UNLINKED", "NEVER_SYNCED", null, null, "tie", 1.00m, "ACTIVE", null); await InsertLegacyListingAsync(account, diffTieLong);
        await InsertLegacyObservationAsync(diffTieLong.Id, "o-1", Hex64('3'), t.AddDays(-1), "tie", 1.00m, "ACTIVE"); await InsertLegacyObservationAsync(diffTieLong.Id, "o-2", new string('9', 128), t.AddDays(-1), "tie", 1.00m, "ACTIVE");
        var none = new LegacyListing(Guid.NewGuid(), "MLB-FP-NONE", "UNLINKED", "NEVER_SYNCED", null, null, "none", 1.00m, "ACTIVE", null); await InsertLegacyListingAsync(account, none);

        var observationsBefore = await LegacyObservationJsonAsync();
        var act = () => MigrateToHeadAsync();
        await act.Should().NotThrowAsync();
        (await LegacyObservationJsonAsync()).Should().Be(observationsBefore, "no observation row is touched by the backfill, whatever its fingerprint shape");

        await using var db = Context();
        var rows = await db.Set<MarketplaceListing>().AsNoTracking().Where(x => x.MarketplaceAccountId == account).ToDictionaryAsync(x => x.ExternalListingId);
        foreach (var (id, _, expected) in shapes) rows[id].LatestObservationFingerprint.Should().Be(expected, id);
        rows["MLB-FP-MIXED"].LatestObservationFingerprint.Should().BeNull();
        rows["MLB-FP-REVERSE"].LatestObservationFingerprint.Should().Be(Hex64('1'));
        rows["MLB-FP-TIE-SAME"].LatestObservationFingerprint.Should().Be(Hex64('2'));
        rows["MLB-FP-TIE-DIFF"].LatestObservationFingerprint.Should().BeNull();
        rows["MLB-FP-TIE-LONG"].LatestObservationFingerprint.Should().BeNull();
        rows["MLB-FP-NONE"].LatestObservationFingerprint.Should().BeNull();
        rows.Values.Should().OnlyContain(x => x.LinkageState == MarketplaceLinkageState.UNLINKED && x.ProductId == null && x.SkuMappingId == null && x.LinkageSource == null,
            "the conservative backfill never creates, changes or infers a link");

        // The first sync after the upgrade must work for every shape, then be idempotent.
        var ids = rows.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        async Task SyncAsync(int minute)
        {
            var reader = new ScriptedReader(ids, _clock);
            foreach (var id in ids) reader.Facts[id] = FactsFor(id, "SKU-" + id) with { Title = "after-" + id, ObservedPrice = 2.00m };
            await using var run = Context(); var created = await new MarketplaceListingSyncRunKernel(run).CreateFullAsync(account, Guid.NewGuid(), t.AddMinutes(minute));
            (await CreateExecution(run, reader).RunAsync(created.Id))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        }
        await SyncAsync(1);
        int observationsAfterFirst; await using (var verify = Context()) observationsAfterFirst = await verify.Set<MarketplaceListingObservation>().CountAsync();
        await using (var verify = Context())
        {
            var after = await verify.Set<MarketplaceListing>().AsNoTracking().Where(x => x.MarketplaceAccountId == account).ToListAsync();
            after.Should().OnlyContain(x => x.LinkageState == MarketplaceLinkageState.UNLINKED && x.ProductId == null, "no incorrect association is created by a conservative first sync");
            after.Should().OnlyContain(x => x.LatestObservationFingerprint != null && x.LatestObservationFingerprint.Length == 64, "after the first sync every listing carries a real V2 fingerprint");
        }
        await SyncAsync(2);
        await using (var verify = Context()) (await verify.Set<MarketplaceListingObservation>().CountAsync()).Should().Be(observationsAfterFirst, "an unchanged re-import appends nothing");
    }

    [Fact]
    public async Task M060_F03_the_replaced_backfill_statement_really_failed_with_22001_and_a_failed_migration_leaves_the_S8C1_schema_untouched()
    {
        var account = await SeedAccountAsync("777"); await MigrateToAsync(MigrationS8C1Head);
        var listing = new LegacyListing(Guid.NewGuid(), "MLB-OLDSQL", "UNLINKED", "NEVER_SYNCED", null, null, "old sql", 1.00m, "ACTIVE", null); await InsertLegacyListingAsync(account, listing);
        await InsertLegacyObservationAsync(listing.Id, "o", new string('f', 100), _clock.UtcNow.AddDays(-1), "old sql", 1.00m, "ACTIVE");

        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync(); await using var tx = await connection.BeginTransactionAsync();
            await using (var ddl = new NpgsqlCommand("ALTER TABLE commerce.marketplace_listing ADD COLUMN latest_observation_fingerprint char(64) NULL", connection, tx)) await ddl.ExecuteNonQueryAsync();
            // the M059-era statement, verbatim minus the new length guard
            await using var oldBackfill = new NpgsqlCommand("""
                WITH observed AS (SELECT marketplace_listing_id, fingerprint, ingested_at, max(ingested_at) OVER (PARTITION BY marketplace_listing_id) AS latest_ingested_at FROM commerce.marketplace_listing_observation),
                     latest AS (SELECT marketplace_listing_id, CASE WHEN count(DISTINCT fingerprint) FILTER (WHERE ingested_at = latest_ingested_at) = 1 THEN min(fingerprint) FILTER (WHERE ingested_at = latest_ingested_at) ELSE NULL END AS fingerprint FROM observed GROUP BY marketplace_listing_id)
                UPDATE commerce.marketplace_listing AS listing SET latest_observation_fingerprint = latest.fingerprint FROM latest WHERE listing.id = latest.marketplace_listing_id;
                """, connection, tx);
            var failure = await Record.ExceptionAsync(() => oldBackfill.ExecuteNonQueryAsync());
            failure.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("22001", "the pre-fix statement aborts on a 100-character legacy fingerprint");
            await tx.RollbackAsync();
        }
        (await JsonAsync("SELECT count(*)::text FROM information_schema.columns WHERE table_schema='commerce' AND table_name='marketplace_listing' AND column_name='latest_observation_fingerprint'")).Should().Be("0", "a failed attempt leaves the S8C.1 schema exactly as it was");
        (await LegacyColumnsJsonAsync()).Should().Contain("MLB-OLDSQL");
        await MigrateToHeadAsync();                                         // and the corrected migration then applies cleanly to that very database
        await using var db = Context(); (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task M060_F03_post_upgrade_the_S8B_manual_observation_contract_still_accepts_a_fingerprint_of_up_to_128_characters()
    {
        // S8B's observation contract (Fingerprint 1..128) is unchanged. After S8C.2 the listing carries a char(64) shortcut to the latest fingerprint; a legitimate
        // 100-character client fingerprint must neither fail the save with 22001 nor be stored truncated/padded.
        var account = await SeedAccountAsync("777");
        Guid listingId;
        await using (var db = Context()) { var listing = new MarketplaceListing(account, "MLB-LONGFP-RT", null, MarketplaceListingStatus.ACTIVE); db.Add(listing); await db.SaveChangesAsync(); listingId = listing.Id; }
        var longFingerprint = new string('f', 100);
        await using (var db = Context())
        {
            var listing = await db.Set<MarketplaceListing>().Include(x => x.Observations).SingleAsync(x => x.Id == listingId);
            listing.ApplyObservation("manual-long-1", longFingerprint, ObservationProvenance.MANUAL, _clock.UtcNow, _clock.UtcNow, MarketplaceListingStatus.PAUSED, 9.00m);
            var save = () => db.SaveChangesAsync();
            await save.Should().NotThrowAsync("a 100-character S8B fingerprint is valid contract data");
        }
        await using (var db = Context())
        {
            var listing = await db.Set<MarketplaceListing>().AsNoTracking().Include(x => x.Observations).SingleAsync(x => x.Id == listingId);
            listing.Observations.Should().Contain(o => o.ObservationKey == "manual-long-1" && o.Fingerprint == longFingerprint, "the observation keeps the full fingerprint");
            listing.ObservedStatus.Should().Be(MarketplaceListingStatus.PAUSED);
            listing.LatestObservationFingerprint.Should().BeNull("a non-V2-shaped fingerprint cannot be the char(64) latest marker (unknown, not truncated)");
        }
        // a following provider sync still behaves: it appends once (nothing authoritative to compare with) and then is idempotent
        var reader = new ScriptedReader(["MLB-LONGFP-RT"], _clock) { Facts = { ["MLB-LONGFP-RT"] = FactsFor("MLB-LONGFP-RT", "SKU-LONG") } };
        for (var minute = 1; minute <= 2; minute++)
        {
            await using var db = Context(); var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow.AddMinutes(minute));
            (await CreateExecution(db, reader).RunAsync(run.Id))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        }
        await using var verify = Context(); (await verify.Set<MarketplaceListingObservation>().CountAsync(x => x.MarketplaceListingId == listingId && x.Provenance == ObservationProvenance.PROVIDER_SYNC)).Should().Be(1);
    }

    // ======================================================================== F02 — chunk-level classification through the real adapter, persisted

    private static HttpResponseMessage Text(HttpStatusCode status, string body, string? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    [Theory]
    [InlineData("empty200")]
    [InlineData("truncated200")]
    [InlineData("object200")]
    [InlineData("null200")]
    [InlineData("status400")]
    public async Task M060_F02_a_whole_chunk_that_is_malformed_or_rejected_is_PERMANENT_LISTING_READ_REJECTED_without_retry_and_without_stopping_the_run(string variant)
    {
        var account = await SeedAccountAsync("777"); var ids = Enumerable.Range(1, 45).Select(i => $"MLB{9700000000L + i}").ToArray();
        await using var boot = NewBoot(ids, p => p.WholeResponse = call => call != 1 ? null : variant switch
        {
            "empty200" => Text(HttpStatusCode.OK, ""),
            "truncated200" => Text(HttpStatusCode.OK, "[{\"id\":\"MLB97"),
            "object200" => Text(HttpStatusCode.OK, "{}"),
            "null200" => Text(HttpStatusCode.OK, "null"),
            _ => Text(HttpStatusCode.BadRequest, "{\"message\":\"CANARY-BODY-5521\"}"),
        });
        var runId = (await boot.Service.StartFullAsync(account, Guid.NewGuid())).RunId!.Value;
        var run = (await boot.Execution.RunAsync(runId))!;

        boot.Wire.BulkRequests.Select(r => r.Length).Should().Equal(20, 20, 5);               // chunk 1 consumed exactly ONE request: a permanent failure is never re-requested
        run.Status.Should().Be(MarketplaceListingSyncStatus.PARTIAL, "chunk 1's NON_RETRYABLE failure does not stop the run; chunks 2 and 3 persisted"); run.SafeResultCode.Should().Be("LISTING_SYNC_PARTIAL");
        var detail = await boot.Service.GetRunAsync(runId);
        detail.Counters.Found.Should().Be(25); detail.Counters.BlockingNonRetryable.Should().Be(20); detail.Counters.BlockingRetryable.Should().Be(0); detail.Counters.Pending.Should().Be(0);
        var rejected = (await boot.Service.GetItemsAsync(runId, MarketplaceListingSyncItemOutcome.PERMANENT_ERROR, 1, 100)).Items;
        rejected.Should().HaveCount(20).And.OnlyContain(x => x.SafeErrorCode == "LISTING_READ_REJECTED" && !x.Retryable && x.MarketplaceListingId == null);
        (await ListingCountAsync(account)).Should().Be(25, "the rejected chunk created no listing row");
        var status = await boot.Service.GetStatusAsync(account);
        status.Summary.Health.Should().Be(MarketplaceListingSyncHealth.ATTENTION_REQUIRED); status.Summary.OpenBlockingItemCount.Should().Be(20);
        (await boot.Service.RetryAsync(runId, Guid.NewGuid())).Code.Should().Be("LISTING_SYNC_NOTHING_TO_RETRY", "PERMANENT items are never copied by a retry");
        await using var db = Context();
        (await db.Set<MarketplaceAccountConnection>().AsNoTracking().SingleAsync(x => x.MarketplaceAccountId == account)).RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.AVAILABLE, "a rejected/malformed chunk is not provider unavailability");
        string.Join("\n", await JsonAsync("SELECT coalesce(string_agg(to_jsonb(r)::text, ''),'') FROM commerce.marketplace_listing_sync_run r") + await RunItemsAsync(runId)).Should().NotContain("CANARY-BODY-5521");
    }

    [Fact]
    public async Task M060_F02_a_429_on_the_first_chunk_stops_the_run_with_the_persisted_retry_after_and_makes_exactly_one_request()
    {
        var account = await SeedAccountAsync("777"); var ids = Enumerable.Range(1, 25).Select(i => $"MLB{9800000000L + i}").ToArray();
        await using var boot = NewBoot(ids, p => p.WholeResponse = _ => Text((HttpStatusCode)429, "{}", "3600"));
        var runId = (await boot.Service.StartFullAsync(account, Guid.NewGuid())).RunId!.Value;
        var run = (await boot.Execution.RunAsync(runId))!;
        boot.Wire.BulkRequests.Should().HaveCount(1, "Retry-After beyond the 30s budget is returned immediately, never slept on");
        run.Status.Should().Be(MarketplaceListingSyncStatus.FAILED); run.SafeResultCode.Should().Be("PROVIDER_RATE_LIMITED");
        var items = (await boot.Service.GetItemsAsync(runId, null, 1, 100)).Items;
        items.Should().HaveCount(25).And.OnlyContain(x => x.Outcome == MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR && x.SafeErrorCode == "PROVIDER_RATE_LIMITED");
        await using var db = Context(); var connection = await db.Set<MarketplaceAccountConnection>().AsNoTracking().SingleAsync(x => x.MarketplaceAccountId == account);
        connection.RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.UNAVAILABLE); connection.RuntimeRetryAfterUntil.Should().Be(_clock.UtcNow.AddSeconds(3600));
        (await ListingCountAsync(account)).Should().Be(0);
    }
}
