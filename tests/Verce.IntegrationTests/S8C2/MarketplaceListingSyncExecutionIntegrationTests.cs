using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Verce.Infrastructure.Marketplaces;
using Verce.Infrastructure.Marketplaces.MercadoLivre;
using Verce.Api.Commerce;
using Verce.Modules.Catalog;
using Verce.Modules.Commerce;
using Verce.Platform.Audit;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.S8C2;

/// <summary>Hermetic B2 execution proof. Its container is owned by this fixture; the fake reader
/// is in-process and no OAuth, provider endpoint, seller or operational database is reachable.</summary>
public sealed partial class MarketplaceListingSyncExecutionIntegrationTests : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private string _connectionString = string.Empty;
    private readonly TestClock _clock = new(DateTimeOffset.Parse("2026-09-28T12:00:00Z"));

    public async Task InitializeAsync()
    {
        ArmedExecutionHooks().Should().BeEmpty("M064: no static execution hook may be armed when a test starts (a previous test leaked it)");
        VerceDbContext.ConfigureModuleAssemblies(Verce.Api.ModuleAssemblyCatalog.All);
        _container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("verce_s8c2_b2").WithUsername("verce").WithPassword("verce_test_only").Build();
        await _container.StartAsync(); _connectionString = _container.GetConnectionString();
        await using var db = Context(); await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        // M064: the hooks are process-wide statics. Whatever this test left armed is reported AND cleared, so one test's leftovers can never fire inside the next one.
        var armed = ArmedExecutionHooks(); ClearExecutionHooks();
        await _container.DisposeAsync();
        armed.Should().BeEmpty("a test must consume (the production code does) or reset every static execution hook it arms");
    }
    private VerceDbContext Context() => new(new DbContextOptionsBuilder<VerceDbContext>().UseNpgsql(_connectionString).UseSnakeCaseNamingConvention().Options);
    private VerceDbContext Context(string applicationName) => new(new DbContextOptionsBuilder<VerceDbContext>().UseNpgsql(new NpgsqlConnectionStringBuilder(_connectionString) { ApplicationName = applicationName }.ConnectionString).UseSnakeCaseNamingConvention().Options);

    [Fact]
    public async Task Full_repeat_sync_is_durable_idempotent_and_uses_20_then_short_detail_batches()
    {
        var account = await SeedAccountAsync();
        var ids = Enumerable.Range(1, 22).Select(x => $"MLB-{x:000}").ToArray();
        var reader = new ScriptedReader(ids, _clock);

        await using (var db = Context())
        {
            var first = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
            var completed = await CreateExecution(db, reader).RunAsync(first.Id);
            completed!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
            completed.EnumerationCompleted.Should().BeTrue();
        }
        reader.DetailBatches.Select(x => x.Count).Should().Equal(20, 2);

        await using (var db = Context())
        {
            (await db.Set<MarketplaceListingSyncRunItem>().CountAsync()).Should().Be(22);
            (await db.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.Outcome == MarketplaceListingSyncItemOutcome.FOUND)).Should().Be(22);
            (await db.Set<MarketplaceListing>().CountAsync()).Should().Be(22);
            (await db.Set<MarketplaceListingObservation>().CountAsync()).Should().Be(22);
            var second = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow.AddMinutes(1));
            var completed = await CreateExecution(db, reader).RunAsync(second.Id);
            completed!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        }
        await using (var db = Context())
        {
            (await db.Set<MarketplaceListing>().CountAsync()).Should().Be(22);
            (await db.Set<MarketplaceListingObservation>().CountAsync()).Should().Be(22);
        }
    }

    [Fact]
    public async Task Mixed_result_derives_partial_and_failed_only_retries_only_the_frozen_eligible_items()
    {
        var account = await SeedAccountAsync();
        var ids = Enumerable.Range(1, 20).Select(x => $"MLB-M-{x:000}").ToArray();
        var reader = new ScriptedReader(ids, _clock);
        reader.Outcomes[ids[17]] = MarketplaceListingReadOutcome.NOT_FOUND;
        reader.Outcomes[ids[18]] = MarketplaceListingReadOutcome.ACCESS_DENIED;
        reader.Outcomes[ids[19]] = MarketplaceListingReadOutcome.FAILED;
        Guid source;
        await using (var db = Context())
        {
            source = (await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow)).Id;
            var result = await CreateExecution(db, reader).RunAsync(source);
            result!.Status.Should().Be(MarketplaceListingSyncStatus.PARTIAL);
            (await db.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.SyncRunId == source && x.Outcome == MarketplaceListingSyncItemOutcome.FOUND)).Should().Be(17);
            (await db.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.SyncRunId == source && x.Outcome == MarketplaceListingSyncItemOutcome.NOT_FOUND)).Should().Be(1);
            (await db.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.SyncRunId == source && x.Outcome == MarketplaceListingSyncItemOutcome.ACCESS_DENIED)).Should().Be(1);
            (await db.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.SyncRunId == source && x.Outcome == MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR)).Should().Be(1);
            var retry = await new MarketplaceListingSyncRunKernel(db).CreateFailedOnlyRetryAsync(source, Guid.NewGuid(), _clock.UtcNow.AddMinutes(1));
            (await db.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.SyncRunId == retry.Id)).Should().Be(3);
            reader.Outcomes.Clear();
            (await CreateExecution(db, reader).RunAsync(retry.Id))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        }
    }

    [Fact]
    public async Task Stale_lease_cannot_insert_listing_and_fault_after_listing_write_rolls_back()
    {
        var account = await SeedAccountAsync(); var now = _clock.UtcNow; var leaseDuration = TimeSpan.FromMinutes(1);
        Guid staleRun;
        await using (var db = Context())
        {
            staleRun = (await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), now)).Id;
            var authority = new MarketplaceListingSyncLeaseAuthority(db);
            var first = (await authority.ClaimAsync(staleRun, now, leaseDuration))!;
            var second = (await authority.ReclaimAsync(staleRun, now.AddMinutes(2), leaseDuration))!;
            var stale = await Record.ExceptionAsync(() => authority.ExecuteWithLeaseAsync(staleRun, first.LeaseToken, now.AddMinutes(2), leaseDuration, async ct =>
            {
                db.Add(new MarketplaceListing(account, "MLB-STALE", null, MarketplaceListingStatus.ACTIVE));
                await db.SaveChangesAsync(ct);
            }));
            stale.Should().BeOfType<MarketplaceListingSyncLeaseLostException>();
            await new MarketplaceListingSyncRunKernel(db).FinalizeWithLeaseAsync(staleRun, second.LeaseToken, "PROVIDER_UNAVAILABLE", now.AddMinutes(2), leaseDuration);
        }
        await using (var db = Context()) (await db.Set<MarketplaceListing>().AnyAsync(x => x.MarketplaceAccountId == account && x.ExternalListingId == "MLB-STALE")).Should().BeFalse();

        Guid rollbackRun;
        await using (var db = Context())
        {
            rollbackRun = (await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), now.AddMinutes(3))).Id;
            var token = (await new MarketplaceListingSyncLeaseAuthority(db).ClaimAsync(rollbackRun, now.AddMinutes(3), leaseDuration))!;
            var fault = await Record.ExceptionAsync(() => new MarketplaceListingSyncLeaseAuthority(db).ExecuteWithLeaseAsync(rollbackRun, token.LeaseToken, now.AddMinutes(3), leaseDuration, async ct =>
            {
                db.Add(new MarketplaceListing(account, "MLB-ROLLBACK", null, MarketplaceListingStatus.ACTIVE));
                await db.SaveChangesAsync(ct); throw new InvalidOperationException("TEST_FAULT");
            }));
            fault.Should().BeOfType<InvalidOperationException>();
        }
        await using (var db = Context()) (await db.Set<MarketplaceListing>().AnyAsync(x => x.MarketplaceAccountId == account && x.ExternalListingId == "MLB-ROLLBACK")).Should().BeFalse();
    }

    [Fact]
    public async Task Known_not_enumerated_listing_is_registered_and_directly_reconciled()
    {
        var account = await SeedAccountAsync();
        await using (var db = Context())
        {
            db.Add(new MarketplaceListing(account, "MLB-KNOWN", "SKU-KNOWN", MarketplaceListingStatus.ACTIVE, "before", 10m));
            await db.SaveChangesAsync();
            var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
            var completed = await CreateExecution(db, new ScriptedReader([], _clock)).RunAsync(run.Id);
            completed!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        }
        await using (var db = Context())
        {
            var item = await db.Set<MarketplaceListingSyncRunItem>().SingleAsync();
            item.Origin.Should().Be(MarketplaceListingSyncItemOrigin.KNOWN_NOT_ENUMERATED);
            item.Outcome.Should().Be(MarketplaceListingSyncItemOutcome.FOUND);
            var listing = await db.Set<MarketplaceListing>().SingleAsync(x => x.ExternalListingId == "MLB-KNOWN");
            listing.SyncState.Should().Be(MarketplaceSyncState.SYNCED);
        }
    }

    [Fact]
    public async Task Configured_detail_batch_size_is_used_and_invalid_values_are_rejected()
    {
        new MarketplaceListingSyncOptions().DetailBatchSize.Should().Be(20);
        new MarketplaceListingSyncOptionsValidator().Validate(null, new MarketplaceListingSyncOptions()).Succeeded.Should().BeTrue();
        new MarketplaceListingSyncOptionsValidator().Validate(null, new MarketplaceListingSyncOptions { DetailBatchSize = 1 }).Succeeded.Should().BeTrue();
        new MarketplaceListingSyncOptionsValidator().Validate(null, new MarketplaceListingSyncOptions { DetailBatchSize = 20 }).Succeeded.Should().BeTrue();
        new MarketplaceListingSyncOptionsValidator().Validate(null, new MarketplaceListingSyncOptions { DetailBatchSize = 0 }).Succeeded.Should().BeFalse();
        new MarketplaceListingSyncOptionsValidator().Validate(null, new MarketplaceListingSyncOptions { DetailBatchSize = 21 }).Succeeded.Should().BeFalse();
        var account = await SeedAccountAsync(); var reader = new ScriptedReader(["MLB-C1", "MLB-C2"], _clock);
        await using var db = Context();
        var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
        (await CreateExecution(db, reader, 1).RunAsync(run.Id))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        reader.DetailBatches.Should().OnlyContain(x => x.Count == 1);
    }

    [Fact]
    public async Task Enumeration_restarts_invalid_cursor_without_duplicate_targets_and_stops_after_three_no_progress_pages()
    {
        var account = await SeedAccountAsync(); var reader = new ScriptedReader([], _clock);
        reader.Pages.Enqueue(new MarketplaceListingPage(["MLB-R1"], new Cursor(), false, 0));
        reader.PageFailures.Enqueue(new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.NON_RETRYABLE, "LISTING_ENUMERATION_CURSOR_INVALID", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED)));
        reader.Pages.Enqueue(new MarketplaceListingPage(["MLB-R1", "MLB-R2"], null, true, 0));
        await using (var db = Context())
        {
            var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
            (await CreateExecution(db, reader).RunAsync(run.Id))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        }
        reader.PageCalls.Should().Be(3);
        await using (var db = Context()) (await db.Set<MarketplaceListingSyncRunItem>().Select(x => x.ExternalListingId).OrderBy(x => x).ToListAsync()).Should().Equal("MLB-R1", "MLB-R2");

        var stalledAccount = await SeedAccountAsync(); var stalled = new ScriptedReader([], _clock);
        stalled.Pages.Enqueue(new MarketplaceListingPage(["MLB-S1"], new Cursor(), false, 0));
        stalled.Pages.Enqueue(new MarketplaceListingPage(["MLB-S1"], new Cursor(), false, 0));
        stalled.Pages.Enqueue(new MarketplaceListingPage(["MLB-S1"], new Cursor(), false, 0));
        stalled.Pages.Enqueue(new MarketplaceListingPage(["MLB-S1"], new Cursor(), false, 0));
        await using (var db = Context())
        {
            var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(stalledAccount, Guid.NewGuid(), _clock.UtcNow);
            var completed = await CreateExecution(db, stalled).RunAsync(run.Id);
            completed!.Status.Should().Be(MarketplaceListingSyncStatus.FAILED);
            completed.SafeResultCode.Should().Be("LISTING_ENUMERATION_INCOMPLETE");
            completed.EnumerationCompleted.Should().BeFalse();
        }
        stalled.PageCalls.Should().Be(4);
    }

    [Fact]
    public async Task Pagination_over_100_and_known_missing_blocking_outcome_are_durable()
    {
        var account = await SeedAccountAsync(); var ids = Enumerable.Range(1, 105).Select(i => $"MLB-P{i:000}").ToArray(); var reader = new ScriptedReader([], _clock);
        reader.Pages.Enqueue(new MarketplaceListingPage(ids.Take(100).Reverse().ToArray(), new Cursor(), false, 0));
        reader.Pages.Enqueue(new MarketplaceListingPage(ids.Skip(100).Reverse().ToArray(), null, true, 0));
        await using (var db = Context()) { var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow); (await CreateExecution(db, reader).RunAsync(run.Id))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED); }
        await using (var db = Context()) (await db.Set<MarketplaceListingSyncRunItem>().Where(x => x.MarketplaceListingId != null).Select(x => x.ExternalListingId).OrderBy(x => x).ToListAsync()).Should().BeEquivalentTo(ids);

        var missingAccount = await SeedAccountAsync();
        await using (var db = Context())
        {
            db.Add(new MarketplaceListing(missingAccount, "MLB-MISSING", null, MarketplaceListingStatus.ACTIVE)); await db.SaveChangesAsync();
            var missingReader = new ScriptedReader([], _clock); missingReader.Outcomes["MLB-MISSING"] = MarketplaceListingReadOutcome.NOT_FOUND;
            var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(missingAccount, Guid.NewGuid(), _clock.UtcNow);
            (await CreateExecution(db, missingReader).RunAsync(run.Id))!.Status.Should().Be(MarketplaceListingSyncStatus.FAILED);
        }
        await using (var db = Context()) { var item = await db.Set<MarketplaceListingSyncRunItem>().SingleAsync(x => x.ExternalListingId == "MLB-MISSING"); item.Origin.Should().Be(MarketplaceListingSyncItemOrigin.KNOWN_NOT_ENUMERATED); item.Outcome.Should().Be(MarketplaceListingSyncItemOutcome.NOT_FOUND); (await db.Set<MarketplaceListing>().SingleAsync(x => x.ExternalListingId == "MLB-MISSING")).ObservedStatus.Should().Be(MarketplaceListingStatus.ACTIVE); }
    }

    [Fact]
    public async Task Runtime_marks_available_for_success_and_unavailable_for_rate_limit()
    {
        var success = await SeedAccountAsync();
        await using (var db = Context()) { var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(success, Guid.NewGuid(), _clock.UtcNow); await CreateExecution(db, new ScriptedReader(["MLB-OK"], _clock)).RunAsync(run.Id); }
        await using (var db = Context()) (await db.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == success)).RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.AVAILABLE);

        var limited = await SeedAccountAsync(); var limitedReader = new ScriptedReader(["MLB-429"], _clock); var retry = _clock.UtcNow.AddMinutes(7);
        limitedReader.DetailFailures.Enqueue(new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.RATE_LIMITED, "PROVIDER_RATE_LIMITED", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED, RetryAfterUntil: retry)));
        await using (var db = Context()) { var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(limited, Guid.NewGuid(), _clock.UtcNow); (await CreateExecution(db, limitedReader).RunAsync(run.Id))!.Status.Should().Be(MarketplaceListingSyncStatus.FAILED); }
        await using (var db = Context()) { var c = await db.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == limited); c.RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.UNAVAILABLE); c.RuntimeRetryAfterUntil.Should().Be(retry); c.LastFailureClassification.Should().Be("RATE_LIMITED"); }
    }

    [Fact]
    public async Task Provider_change_and_explicit_closed_create_authoritative_observations()
    {
        var account = await SeedAccountAsync(); var reader = new ScriptedReader(["MLB-OBS"], _clock);
        await using (var db = Context()) { var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow); await CreateExecution(db, reader).RunAsync(run.Id); }
        reader.Facts["MLB-OBS"] = new NormalizedListingFacts("MLB-OBS", "SKU-MLB-OBS", "Changed", 11m, MarketplaceListingStatus.INACTIVE, "closed", 0, "https://example.test/MLB-OBS", _clock.UtcNow.AddMinutes(1), []);
        await using (var db = Context()) { var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow.AddMinutes(1)); await CreateExecution(db, reader).RunAsync(run.Id); }
        await using (var db = Context()) { var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow.AddMinutes(2)); await CreateExecution(db, reader).RunAsync(run.Id); }
        await using (var db = Context()) { var listing = await db.Set<MarketplaceListing>().Include(x => x.Observations).SingleAsync(x => x.ExternalListingId == "MLB-OBS"); listing.ObservedStatus.Should().Be(MarketplaceListingStatus.INACTIVE); listing.ProviderNativeStatus.Should().Be("closed"); listing.Observations.Should().HaveCount(2); listing.Observations.Select(x => x.Fingerprint).Distinct().Should().HaveCount(2); }
    }

    [Fact]
    public async Task Explicit_account_scoped_mapping_links_only_its_account_and_manual_link_wins()
    {
        var accountA = await SeedAccountAsync(); var accountB = await SeedAccountAsync(); var productA = await SeedProductAsync(true); var productB = Guid.NewGuid(); var sku = MarketplaceSkuNormalizer.Normalize(" SKU-X ");
        await using (var db = Context()) { db.Add(new MarketplaceAccountSkuMapping(accountB, sku, productB, Guid.NewGuid(), _clock.UtcNow)); await db.SaveChangesAsync(); var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(accountA, Guid.NewGuid(), _clock.UtcNow); await CreateExecution(db, new ScriptedReader(["MLB-SKU"], _clock) { Facts = { ["MLB-SKU"] = FactsFor("MLB-SKU", sku) } }).RunAsync(run.Id); }
        await using (var db = Context()) { var listing = await db.Set<MarketplaceListing>().SingleAsync(x => x.MarketplaceAccountId == accountA && x.ExternalListingId == "MLB-SKU"); listing.ProductId.Should().BeNull(); listing.LinkageState.Should().Be(MarketplaceLinkageState.UNLINKED); db.Add(new MarketplaceAccountSkuMapping(accountA, sku, productA, Guid.NewGuid(), _clock.UtcNow)); await db.SaveChangesAsync(); }
        await using (var db = Context()) { var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(accountA, Guid.NewGuid(), _clock.UtcNow); await CreateExecution(db, new ScriptedReader(["MLB-SKU-2"], _clock) { Facts = { ["MLB-SKU-2"] = FactsFor("MLB-SKU-2", sku) } }, productEligibility: new CommerceProductEligibilityReader(db)).RunAsync(run.Id); }
        await using (var db = Context()) { var linked = await db.Set<MarketplaceListing>().SingleAsync(x => x.MarketplaceAccountId == accountA && x.ExternalListingId == "MLB-SKU-2"); linked.ProductId.Should().Be(productA); linked.LinkageSource.Should().Be(MarketplaceListingLinkageSource.DETERMINISTIC_SKU); linked.SkuMappingId.Should().NotBeNull(); }
    }

    [Fact]
    public async Task Revoked_mapping_and_newly_ineligible_product_are_reread_inside_the_fenced_decision()
    {
        var account = await SeedAccountAsync(); var sku = MarketplaceSkuNormalizer.Normalize(" SKU-CONCURRENT ");
        var mappedProduct = await SeedProductAsync(true); var ineligibleProduct = await SeedProductAsync(true);
        Guid revokedMapping;
        await using (var db = Context())
        {
            var mapping = new MarketplaceAccountSkuMapping(account, sku, mappedProduct, Guid.NewGuid(), _clock.UtcNow); revokedMapping = mapping.Id; db.Add(mapping);
            var second = new MarketplaceAccountSkuMapping(account, MarketplaceSkuNormalizer.Normalize("SKU-INELIGIBLE"), ineligibleProduct, Guid.NewGuid(), _clock.UtcNow); db.Add(second); await db.SaveChangesAsync();
            var reader = new ScriptedReader(["MLB-REVOKED", "MLB-INELIGIBLE"], _clock) { Facts = { ["MLB-REVOKED"] = FactsFor("MLB-REVOKED", sku), ["MLB-INELIGIBLE"] = FactsFor("MLB-INELIGIBLE", "SKU-INELIGIBLE") } };
            reader.BeforeDetailsAsync = async () =>
            {
                await using var concurrent = Context();
                await concurrent.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account_sku_mapping SET active = false, invalidated_at = {_clock.UtcNow}, invalidated_by_user_id = {Guid.NewGuid()}, invalidated_reason = 'OPERATOR_DEACTIVATED' WHERE id = {revokedMapping}");
                var product = await concurrent.Set<Product>().SingleAsync(x => x.Id == ineligibleProduct); product.Deactivate(); await concurrent.SaveChangesAsync();
            };
            var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
            (await CreateExecution(db, reader, productEligibility: new CommerceProductEligibilityReader(db)).RunAsync(run.Id))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        }
        await using (var db = Context())
        {
            var revoked = await db.Set<MarketplaceListing>().SingleAsync(x => x.ExternalListingId == "MLB-REVOKED");
            revoked.ProductId.Should().BeNull(); revoked.LinkageState.Should().Be(MarketplaceLinkageState.UNLINKED);
            var ineligible = await db.Set<MarketplaceListing>().SingleAsync(x => x.ExternalListingId == "MLB-INELIGIBLE");
            ineligible.ProductId.Should().BeNull(); ineligible.LinkageState.Should().Be(MarketplaceLinkageState.NEEDS_REVIEW);
        }
    }

    [Fact]
    public async Task Manual_linkage_remains_authoritative_when_an_automatic_sku_mapping_is_available()
    {
        var account = await SeedAccountAsync(); var automaticProduct = await SeedProductAsync(true); var manualProduct = Guid.NewGuid(); var sku = MarketplaceSkuNormalizer.Normalize("SKU-MANUAL");
        await using (var db = Context())
        {
            db.Add(new MarketplaceAccountSkuMapping(account, sku, automaticProduct, Guid.NewGuid(), _clock.UtcNow));
            var listing = new MarketplaceListing(account, "MLB-MANUAL", sku, MarketplaceListingStatus.ACTIVE, "old", 9m); listing.Link(manualProduct, null, null, null, Guid.Empty); db.Add(listing); await db.SaveChangesAsync();
            var reader = new ScriptedReader(["MLB-MANUAL"], _clock) { Facts = { ["MLB-MANUAL"] = FactsFor("MLB-MANUAL", sku) } };
            var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
            (await CreateExecution(db, reader, productEligibility: new CommerceProductEligibilityReader(db)).RunAsync(run.Id))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        }
        await using (var db = Context()) { var listing = await db.Set<MarketplaceListing>().Include(x => x.Observations).SingleAsync(x => x.ExternalListingId == "MLB-MANUAL"); listing.ProductId.Should().Be(manualProduct); listing.LinkageSource.Should().Be(MarketplaceListingLinkageSource.MANUAL); listing.SkuMappingId.Should().BeNull(); listing.TitleSnapshot.Should().Be("Listing MLB-MANUAL"); listing.Observations.Should().HaveCount(2); }
    }

    [Fact]
    public async Task Reconciliation_processes_more_than_one_500_row_database_batch_without_duplicates()
    {
        var account = await SeedAccountAsync(); var ids = Enumerable.Range(1, 501).Select(x => $"MLB-K{x:0000}").ToArray();
        Guid runId;
        await using (var db = Context())
        {
            db.AddRange(ids.Select(id => new MarketplaceListing(account, id, null, MarketplaceListingStatus.ACTIVE))); await db.SaveChangesAsync();
            var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
            runId = run.Id;
            (await CreateExecution(db, new ScriptedReader([], _clock)).RunAsync(run.Id))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        }
        await using (var db = Context())
        {
            var items = await db.Set<MarketplaceListingSyncRunItem>().Where(x => x.SyncRunId == runId).ToListAsync();
            items.Should().HaveCount(501); items.Select(x => x.ExternalListingId).Distinct().Should().HaveCount(501); items.Should().OnlyContain(x => x.Origin == MarketplaceListingSyncItemOrigin.KNOWN_NOT_ENUMERATED && x.Outcome == MarketplaceListingSyncItemOutcome.FOUND);
        }
    }

    [Theory]
    [InlineData(500, new[] { 500 })]
    [InlineData(501, new[] { 500, 1 })]
    [InlineData(1001, new[] { 500, 500, 1 })]
    public async Task Known_not_enumerated_registration_is_capped_at_500_rows_per_batch_and_loops_until_exhausted(int candidates, int[] expectedBatches)
    {
        var account = await SeedAccountAsync(); Guid runId; MarketplaceListingSyncLease lease;
        await using var db = Context();
        db.AddRange(Enumerable.Range(1, candidates).Select(x => new MarketplaceListing(account, $"MLB-B{x:0000}", null, MarketplaceListingStatus.ACTIVE))); await db.SaveChangesAsync();
        runId = (await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow)).Id;
        lease = (await new MarketplaceListingSyncLeaseAuthority(db).ClaimAsync(runId, _clock.UtcNow, TimeSpan.FromMinutes(2)))!;
        MarketplaceListingSyncRunKernel.KnownNotEnumeratedBatchSize.Should().Be(500);
        var kernel = new MarketplaceListingSyncRunKernel(db); var sizes = new List<int>();
        while (true)
        {
            var added = await kernel.RegisterKnownNotEnumeratedWithLeaseAsync(runId, lease.LeaseToken, _clock.UtcNow, TimeSpan.FromMinutes(2), null);
            if (added == 0) break; sizes.Add(added);
            (await db.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.SyncRunId == runId)).Should().Be(sizes.Sum());
        }
        sizes.Should().Equal(expectedBatches);
        (await db.Set<MarketplaceListingSyncRunItem>().Where(x => x.SyncRunId == runId).Select(x => x.ExternalListingId).Distinct().CountAsync()).Should().Be(candidates);
    }

    [Fact]
    public async Task Real_bulk_adapter_keeps_mixed_provider_response_isolated_through_b2_orchestration()
    {
        var account = await SeedAccountAsync("777"); var telemetry = new BoundedLogProvider();
        var connector = CreateRealConnector(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
            [
              {"code":200,"body":{"id":"MLB-GOOD","seller_id":777,"site_id":"MLB","status":"active","price":9.9,"currency_id":"BRL"}},
              {"code":403,"body":{"id":"MLB-DENIED"}},
              "malformed-element"
            ]
            """, Encoding.UTF8, "application/json") }, telemetry);
        await using (var db = Context())
        {
            var reader = new ConnectorDetailReader(new ScriptedReader(["MLB-GOOD", "MLB-DENIED", "MLB-MALFORMED"], _clock), connector);
            var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
            var completed = await CreateExecution(db, reader).RunAsync(run.Id);
            completed!.Status.Should().Be(MarketplaceListingSyncStatus.PARTIAL);
            var items = await db.Set<MarketplaceListingSyncRunItem>().Where(x => x.SyncRunId == run.Id).ToDictionaryAsync(x => x.ExternalListingId);
            items.Values.Count(x => x.Outcome == MarketplaceListingSyncItemOutcome.FOUND).Should().Be(1, "G05 derives terminal counters from durable RunItems");
            items.Values.Count(x => x.Outcome == MarketplaceListingSyncItemOutcome.ACCESS_DENIED).Should().Be(1, "G05 derives terminal counters from durable RunItems");
            items.Values.Count(x => x.Outcome == MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR).Should().Be(1, "G05 derives terminal counters from durable RunItems");
            items["MLB-GOOD"].Outcome.Should().Be(MarketplaceListingSyncItemOutcome.FOUND);
            items["MLB-DENIED"].Outcome.Should().Be(MarketplaceListingSyncItemOutcome.ACCESS_DENIED); items["MLB-DENIED"].SafeErrorCode.Should().Be("LISTING_ACCESS_DENIED");
            items["MLB-MALFORMED"].Outcome.Should().Be(MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR); items["MLB-MALFORMED"].SafeErrorCode.Should().Be("LISTING_READ_FAILED");
            (await db.Set<MarketplaceListing>().CountAsync(x => x.ExternalListingId == "MLB-GOOD")).Should().Be(1);
        }
        telemetry.Entries.Should().ContainSingle(x => x.Contains("requestedIdCount=3") && x.Contains("envelopeCount=3"));
        telemetry.Entries.Should().NotContain(x => x.Contains("test-only-access-token") || x.Contains("test-only-refresh-token") || x.Contains("malformed-element"));
    }

    [Fact]
    public async Task Operation_replacement_after_provider_read_fails_closed_without_stale_provider_effects()
    {
        var account = await SeedAccountAsync(); Guid original;
        await using (var db = Context()) original = (await db.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == account)).ConfirmedOperationId!.Value;
        var reader = new ScriptedReader(["MLB-O1"], _clock);
        reader.BeforeDetailsAsync = async () =>
        {
            var replacement = Guid.NewGuid(); await using var concurrent = Context();
            await concurrent.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,created_at,updated_at,cleanup_attempt_count,version) VALUES ({replacement},'RECONNECT','SECRET_PERSISTED','CONFIRMED','DONE',{account},'MERCADO_LIVRE',{_clock.UtcNow},{_clock.UtcNow},0,1)");
            await concurrent.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account_connection SET confirmed_operation_id={replacement} WHERE marketplace_account_id={account}");
        };
        await using (var db = Context())
        {
            var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
            var result = await CreateExecution(db, reader).RunAsync(run.Id); result!.Status.Should().Be(MarketplaceListingSyncStatus.FAILED);
        }
        await using (var db = Context())
        {
            var run = await db.Set<MarketplaceListingSyncRun>().SingleAsync(); run.ConfirmedOperationIdAtStart.Should().Be(original); run.SafeResultCode.Should().Be("AUTHORIZATION_NOT_USABLE");
            (await db.Set<MarketplaceListing>().CountAsync()).Should().Be(0); (await db.Set<MarketplaceListingObservation>().CountAsync()).Should().Be(0);
            var item = await db.Set<MarketplaceListingSyncRunItem>().SingleAsync(); item.Outcome.Should().Be(MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR); item.SafeErrorCode.Should().Be("AUTHORIZATION_NOT_USABLE");
            (await db.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == account)).ConfirmedOperationId.Should().NotBe(original);
        }
    }

    [Fact]
    public async Task Authorization_replacement_is_blocked_by_the_connection_row_lock_until_the_fenced_effect_commits()
    {
        // T1 = B2 fenced persistence (SyncRun lease fence -> connection FOR UPDATE -> O1 validation -> hook barrier).
        // T2 = independent PostgreSQL connection replacing authority with O2.  The semantic proof is the database's own
        // lock graph (pg_blocking_pids), not timing: if production dropped the connection-row FOR UPDATE, T2's UPDATE would
        // never wait on T1, so the blocked-state wait below would observe T2 completing and the test would fail there.
        var account = await SeedAccountAsync(); var t1ConnectionLockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var t2Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var releaseT1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var replacement = Guid.NewGuid(); Guid original;
        await using (var seed = Context()) original = (await seed.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == account)).ConfirmedOperationId!.Value;
        MarketplaceListingSyncExecutionTestHooks.AfterAuthorizationValidationAsync = async _ => { t1ConnectionLockAcquired.TrySetResult(); await releaseT1.Task; };
        try
        {
            Guid runId;
            await using (var db = Context()) runId = (await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow)).Id;
            var t1 = Task.Run(async () => { await using var db = Context("b2-19-t1"); return await CreateExecution(db, new ScriptedReader(["MLB-LOCKED-O1"], _clock)).RunAsync(runId); });
            await WithSafetyTimeout(t1ConnectionLockAcquired.Task, "T1 never reached the post-validation barrier");
            var t2 = Task.Run(async () =>
            {
                await using var db = Context("b2-19-t2"); await using var tx = await db.Database.BeginTransactionAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,created_at,updated_at,cleanup_attempt_count,version) VALUES ({replacement},'RECONNECT','SECRET_PERSISTED','CONFIRMED','DONE',{account},'MERCADO_LIVRE',{_clock.UtcNow},{_clock.UtcNow},0,1)");
                t2Started.TrySetResult();
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account_connection SET confirmed_operation_id={replacement}, confirmed_credential_version=2 WHERE marketplace_account_id={account}");
                await tx.CommitAsync();
            });
            await WithSafetyTimeout(t2Started.Task, "T2 never started");
            // Deterministic pre-release proof: PostgreSQL itself reports T2's connection UPDATE waiting on a lock owned by T1.
            await WaitUntilBlockedByAsync("b2-19-t2", "b2-19-t1", t2);
            t2.IsCompleted.Should().BeFalse("T2 must not have committed while T1 owns the connection-row lock");
            await using (var observer = Context())
            {
                (await observer.Set<MarketplaceAccountConnection>().AsNoTracking().SingleAsync(x => x.MarketplaceAccountId == account)).ConfirmedOperationId.Should().Be(original, "O2 is not committed before T1 releases");
                (await observer.Set<MarketplaceAccountOperation>().AsNoTracking().AnyAsync(x => x.Id == replacement)).Should().BeFalse();
                (await observer.Set<MarketplaceListing>().AsNoTracking().AnyAsync(x => x.ExternalListingId == "MLB-LOCKED-O1")).Should().BeFalse("T1 has not committed its O1 effects yet");
            }
            releaseT1.TrySetResult(); (await t1)!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED); await WithSafetyTimeout(t2, "T2 never completed after T1 committed");
            await using var verify = Context();
            (await verify.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == account)).ConfirmedOperationId.Should().Be(replacement);
            var listing = await verify.Set<MarketplaceListing>().SingleAsync(x => x.ExternalListingId == "MLB-LOCKED-O1"); listing.TitleSnapshot.Should().Be("Listing MLB-LOCKED-O1");
            var item = await verify.Set<MarketplaceListingSyncRunItem>().SingleAsync(x => x.SyncRunId == runId); item.Outcome.Should().Be(MarketplaceListingSyncItemOutcome.FOUND);
        }
        finally { MarketplaceListingSyncExecutionTestHooks.AfterAuthorizationValidationAsync = null; releaseT1.TrySetResult(); }
    }

    private static async Task WithSafetyTimeout(Task task, string message)
    {
        // Deadlock guard only: it never decides a semantic outcome, it just turns a hang into a failure.
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(60))) != task) throw new TimeoutException(message);
        await task;
    }

    /// <summary>Polls PostgreSQL's lock graph (condition-driven, not a fixed wait) until the named backend is waiting on a Lock held
    /// by the blocker's backend.  Fails fast if the waiter finished instead, which is exactly what a missing FOR UPDATE produces.</summary>
    private async Task WaitUntilBlockedByAsync(string waiterApplication, string blockerApplication, Task waiter)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        await using var observer = new NpgsqlConnection(_connectionString); await observer.OpenAsync();
        while (true)
        {
            waiter.IsCompleted.Should().BeFalse($"{waiterApplication} completed without ever waiting on {blockerApplication}: the connection-row FOR UPDATE is not serializing authority replacement");
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                  SELECT 1 FROM pg_stat_activity w JOIN pg_stat_activity b ON b.pid = ANY (pg_blocking_pids(w.pid))
                   WHERE w.application_name = @waiter AND b.application_name = @blocker
                     AND w.wait_event_type = 'Lock' AND w.query ILIKE '%marketplace_account_connection%')
                """, observer);
            command.Parameters.AddWithValue("waiter", waiterApplication); command.Parameters.AddWithValue("blocker", blockerApplication);
            if ((bool)(await command.ExecuteScalarAsync())!) return;
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"{waiterApplication} was never observed blocked by {blockerApplication}");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task Confirmed_refresh_lineage_rotates_credential_authority_without_invalidating_the_run()
    {
        var account = await SeedAccountAsync(); Guid original;
        await using (var db = Context())
        {
            var connection = await db.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == account); original = connection.ConfirmedOperationId!.Value;
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account_operation SET candidate_credential_reference='test-ref', candidate_credential_version=1 WHERE id={original}");
        }
        var reader = new ScriptedReader(["MLB-REFRESH"], _clock);
        reader.BeforeDetailsAsync = async () =>
        {
            var refresh = Guid.NewGuid(); await using var concurrent = Context();
            await concurrent.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,previous_credential_reference,previous_confirmed_credential_version,candidate_credential_reference,candidate_credential_version,created_at,updated_at,cleanup_attempt_count,version) VALUES ({refresh},'REFRESH','SECRET_PERSISTED','CONFIRMED','DONE',{account},'MERCADO_LIVRE','test-ref',1,'test-ref',2,{_clock.UtcNow},{_clock.UtcNow},0,1)");
            await concurrent.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account_connection SET confirmed_operation_id={refresh}, confirmed_credential_version=2, access_expires_at={_clock.UtcNow.AddHours(2)}, runtime_availability='UNKNOWN', last_failure_classification='TRANSIENT', safe_failure_code='PROVIDER_UNAVAILABLE', runtime_retry_after_until={_clock.UtcNow.AddMinutes(5)} WHERE marketplace_account_id={account}");
        };
        await using (var db = Context())
        {
            var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
            (await CreateExecution(db, reader).RunAsync(run.Id))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        }
        await using (var db = Context()) { (await db.Set<MarketplaceListing>().SingleAsync()).ExternalListingId.Should().Be("MLB-REFRESH"); var run = await db.Set<MarketplaceListingSyncRun>().SingleAsync(); run.ConfirmedOperationIdAtStart.Should().Be(original); var connection = await db.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == account); connection.ConfirmedCredentialVersion.Should().Be(2); connection.RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.AVAILABLE); connection.LastFailureClassification.Should().BeNull(); connection.SafeFailureCode.Should().BeNull(); connection.RuntimeRetryAfterUntil.Should().BeNull(); }
    }

    /// <summary>Valid O1 -> confirmed REFRESH(O2) lineage: O1 confirmed candidate ('test-ref', v1); the account's current reference is 'test-ref';
    /// the connection moves to v2.  Each negative case changes exactly one authority dimension of this record.</summary>
    private sealed record RefreshLineage(string PreviousReference = "test-ref", long PreviousVersion = 1, string CandidateReference = "test-ref", long CandidateVersion = 2, string Kind = "REFRESH", string? AccountReferenceAfter = null);

    private async Task<(Guid Account, MarketplaceListingSyncRun Run)> RunRefreshLineageAsync(RefreshLineage lineage)
    {
        // Malformed persisted lineage cannot be produced through the S8C.1 arbiter (it only writes coherent operations), so the
        // negative rows are written directly as test setup; the rule under test is the real shared production primitive.
        var account = await SeedAccountAsync(); Guid original;
        await using (var db = Context())
        {
            original = (await db.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == account)).ConfirmedOperationId!.Value;
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account_operation SET candidate_credential_reference='test-ref', candidate_credential_version=1 WHERE id={original}");
        }
        var reader = new ScriptedReader(["MLB-REFRESH-MATRIX"], _clock);
        reader.BeforeDetailsAsync = async () =>
        {
            var refresh = Guid.NewGuid(); await using var concurrent = Context();
            await concurrent.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,previous_credential_reference,previous_confirmed_credential_version,candidate_credential_reference,candidate_credential_version,created_at,updated_at,cleanup_attempt_count,version) VALUES ({refresh},{lineage.Kind},'SECRET_PERSISTED','CONFIRMED','DONE',{account},'MERCADO_LIVRE',{lineage.PreviousReference},{lineage.PreviousVersion},{lineage.CandidateReference},{lineage.CandidateVersion},{_clock.UtcNow},{_clock.UtcNow},0,1)");

            await concurrent.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account_connection SET confirmed_operation_id={refresh}, confirmed_credential_version=2, access_expires_at={_clock.UtcNow.AddHours(2)}, runtime_availability='UNKNOWN', last_failure_classification='TRANSIENT', safe_failure_code='PROVIDER_UNAVAILABLE', runtime_retry_after_until={_clock.UtcNow.AddMinutes(5)} WHERE marketplace_account_id={account}");
            if (lineage.AccountReferenceAfter is { } reference) await concurrent.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account SET credential_reference={reference} WHERE id={account}");
        };
        await using var run = Context();
        var created = await new MarketplaceListingSyncRunKernel(run).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
        var completed = await CreateExecution(run, reader).RunAsync(created.Id);
        return (account, completed!);
    }

    [Fact]
    public async Task Valid_refresh_lineage_control_allows_execution_and_clears_failure_state()
    {
        var (account, run) = await RunRefreshLineageAsync(new RefreshLineage());
        run.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        await using var db = Context();
        (await db.Set<MarketplaceListing>().CountAsync(x => x.MarketplaceAccountId == account)).Should().Be(1);
        var connection = await db.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == account);
        connection.RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.AVAILABLE); connection.LastFailureClassification.Should().BeNull(); connection.SafeFailureCode.Should().BeNull(); connection.RuntimeRetryAfterUntil.Should().BeNull();
    }

    public static TheoryData<string> MalformedRefreshDimensions => new()
    {
        "T-F03-A wrong previous credential reference",
        "T-F03-B wrong previous version",
        "T-F03-C wrong candidate credential reference",
        "T-F03-D wrong candidate version",
        "T-F03-E connection current reference mismatch",
        "T-F03-F confirmed operation is not a REFRESH",
    };

    [Theory]
    [MemberData(nameof(MalformedRefreshDimensions))]
    public async Task Malformed_refresh_lineage_fails_closed_when_exactly_one_authority_dimension_is_wrong(string dimension)
    {
        var lineage = dimension[..7] switch
        {
            "T-F03-A" => new RefreshLineage(PreviousReference: "wrong-previous-reference"),
            "T-F03-B" => new RefreshLineage(PreviousVersion: 7),
            "T-F03-C" => new RefreshLineage(CandidateReference: "wrong-candidate-reference"),
            "T-F03-D" => new RefreshLineage(CandidateVersion: 3),
            "T-F03-E" => new RefreshLineage(AccountReferenceAfter: "account-rotated-elsewhere-ref"),
            "T-F03-F" => new RefreshLineage(Kind: "RECONNECT"),
            _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
        };
        var (account, run) = await RunRefreshLineageAsync(lineage);
        run.Status.Should().Be(MarketplaceListingSyncStatus.FAILED, dimension); run.SafeResultCode.Should().Be("AUTHORIZATION_NOT_USABLE", dimension);
        await using var db = Context();
        (await db.Set<MarketplaceListing>().CountAsync(x => x.MarketplaceAccountId == account)).Should().Be(0, "no provider-derived effect may survive a rejected lineage");
        (await db.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == account)).RuntimeAvailability.Should().NotBe(MarketplaceRuntimeAvailability.AVAILABLE, "a rejected lineage must not terminalize runtime AVAILABLE");
    }

    private async Task<(Guid Account, MarketplaceListingSyncRun Run)> RunWithConcurrentAuthorityChangeAsync(Func<VerceDbContext, Guid, Guid, Task> change)
    {
        var account = await SeedAccountAsync(); Guid original;
        await using (var db = Context()) original = (await db.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == account)).ConfirmedOperationId!.Value;
        var reader = new ScriptedReader(["MLB-AUTH-CHANGE"], _clock);
        reader.BeforeDetailsAsync = async () => { await using var concurrent = Context(); await change(concurrent, account, original); };
        await using var run = Context();
        var created = await new MarketplaceListingSyncRunKernel(run).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
        return (account, (await CreateExecution(run, reader).RunAsync(created.Id))!);
    }

    private async Task AssertAuthorityFailClosedAsync(Guid account, MarketplaceListingSyncRun run, string because)
    {
        run.Status.Should().Be(MarketplaceListingSyncStatus.FAILED, because); run.SafeResultCode.Should().Be("AUTHORIZATION_NOT_USABLE", because);
        await using var db = Context();
        (await db.Set<MarketplaceListing>().CountAsync(x => x.MarketplaceAccountId == account)).Should().Be(0, because);
        (await db.Set<MarketplaceListingObservation>().CountAsync()).Should().Be(0, because);
        var item = await db.Set<MarketplaceListingSyncRunItem>().SingleAsync(x => x.SyncRunId == run.Id); item.Outcome.Should().Be(MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR, because); item.SafeErrorCode.Should().Be("AUTHORIZATION_NOT_USABLE", because);
        (await db.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == account)).RuntimeAvailability.Should().NotBe(MarketplaceRuntimeAvailability.AVAILABLE, because);
    }

    [Fact]
    public async Task T_F03_G_version_identity_branch_requires_the_connection_version_to_match_the_snapshot_operation()
    {
        // No O2 exists: the connection still confirms O1 but claims version 2 while O1's confirmed candidate is ('test-ref', 1).
        var (account, run) = await RunWithConcurrentAuthorityChangeAsync(async (db, acct, _) =>
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account_connection SET confirmed_credential_version=2, runtime_availability='UNKNOWN' WHERE marketplace_account_id={acct}"));
        await AssertAuthorityFailClosedAsync(account, run, "T-F03-G-VERSION");
    }

    [Fact]
    public async Task T_F03_G_reference_identity_branch_requires_the_account_reference_to_match_the_snapshot_operation()
    {
        var (account, run) = await RunWithConcurrentAuthorityChangeAsync(async (db, acct, _) =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account SET credential_reference='other-ref' WHERE id={acct}");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account_connection SET runtime_availability='UNKNOWN' WHERE marketplace_account_id={acct}");
        });
        await AssertAuthorityFailClosedAsync(account, run, "T-F03-G-REFERENCE");
    }

    [Fact]
    public async Task T_F03_H_reauthorization_required_on_the_snapshot_operation_fails_closed_and_preserves_the_known_failure_code()
    {
        var (account, run) = await RunWithConcurrentAuthorityChangeAsync(async (db, acct, _) =>
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account_connection SET authorization_state='REAUTHORIZATION_REQUIRED', runtime_availability='UNKNOWN', safe_failure_code='REFRESH_REJECTED' WHERE marketplace_account_id={acct}"));
        await AssertAuthorityFailClosedAsync(account, run, "T-F03-H");
        await using var db = Context(); var connection = await db.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == account);
        connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED); connection.SafeFailureCode.Should().Be("REFRESH_REJECTED", "sync must not convert a reauthorization failure into PROVIDER_UNAVAILABLE");
    }

    [Theory]
    [InlineData("PENDING_EXTERNAL_IN_FLIGHT")]
    [InlineData("PENDING_SECRET_PERSISTED_WITH_CANDIDATE")]
    [InlineData("FAIL_CLOSED")]
    [InlineData("CONFIRMED_UNREFERENCED")]
    public async Task CTRL_O2_an_operation_row_that_is_not_the_connection_authority_does_not_invalidate_the_coherent_snapshot_operation(string o2)
    {
        // The connection stays on coherent O1; the injected O2 happens after the executor's pre-send guard and no further provider call follows.
        var (account, run) = await RunWithConcurrentAuthorityChangeAsync(async (db, acct, _) =>
        {
            var (phase, decision, candidate) = o2 switch { "PENDING_EXTERNAL_IN_FLIGHT" => ("EXTERNAL_IN_FLIGHT", "PENDING", false), "PENDING_SECRET_PERSISTED_WITH_CANDIDATE" => ("SECRET_PERSISTED", "PENDING", true), "FAIL_CLOSED" => ("SECRET_PERSISTED", "FAIL_CLOSED", true), _ => ("SECRET_PERSISTED", "CONFIRMED", true) };
            var id = Guid.NewGuid(); var reference = candidate ? "test-ref" : null; long? version = candidate ? 2 : null;
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,candidate_credential_reference,candidate_credential_version,created_at,updated_at,cleanup_attempt_count,version) VALUES ({id},'RECONNECT',{phase},{decision},'DONE',{acct},'MERCADO_LIVRE',{reference},{version},{_clock.UtcNow},{_clock.UtcNow},0,1)");
        });
        run.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED, o2); run.SafeResultCode.Should().Be("LISTING_SYNC_COMPLETED", o2);
        await using var db = Context();
        (await db.Set<MarketplaceListing>().CountAsync(x => x.MarketplaceAccountId == account)).Should().Be(1, o2);
        (await db.Set<MarketplaceListingSyncRunItem>().SingleAsync(x => x.SyncRunId == run.Id)).Outcome.Should().Be(MarketplaceListingSyncItemOutcome.FOUND, o2);
        var connection = await db.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == account);
        connection.RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.AVAILABLE, o2); connection.LastSuccessAt.Should().Be(_clock.UtcNow, "finish ran its runtime write under the still-valid O1");
    }

    [Fact]
    public async Task Null_run_snapshot_has_no_provider_derived_persistence_authority()
    {
        var (account, run) = await RunWithConcurrentAuthorityChangeAsync(async (db, acct, _) =>
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_listing_sync_run SET confirmed_operation_id_at_start=NULL WHERE marketplace_account_id={acct}"));
        run.Status.Should().Be(MarketplaceListingSyncStatus.FAILED); run.SafeResultCode.Should().Be("AUTHORIZATION_NOT_USABLE");
        await using var db = Context();
        (await db.Set<MarketplaceListing>().CountAsync(x => x.MarketplaceAccountId == account)).Should().Be(0); (await db.Set<MarketplaceListingObservation>().CountAsync()).Should().Be(0);
        var item = await db.Set<MarketplaceListingSyncRunItem>().SingleAsync(x => x.SyncRunId == run.Id); item.Outcome.Should().Be(MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR); item.SafeErrorCode.Should().Be("AUTHORIZATION_NOT_USABLE");
        var connection = await db.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == account); connection.LastSuccessAt.Should().BeNull("a run without a snapshot must not write runtime");
    }

    private sealed record ConnectionState(MarketplaceAuthorizationState Authorization, Guid? Operation, long? Version, MarketplaceRuntimeAvailability Runtime, string? Classification, string? SafeCode, DateTimeOffset? RetryAfter, DateTimeOffset? LastSuccess, DateTimeOffset? LastFailure, DateTimeOffset? AccessExpires, DateTimeOffset UpdatedAt);

    private async Task<ConnectionState> ConnectionStateAsync(Guid account)
    {
        await using var db = Context(); var c = await db.Set<MarketplaceAccountConnection>().AsNoTracking().SingleAsync(x => x.MarketplaceAccountId == account);
        return new(c.AuthorizationState, c.ConfirmedOperationId, c.ConfirmedCredentialVersion, c.RuntimeAvailability, c.LastFailureClassification, c.SafeFailureCode, c.RuntimeRetryAfterUntil, c.LastSuccessAt, c.LastFailureAt, c.AccessExpiresAt, c.UpdatedAt);
    }

    /// <summary>T1 completes every detail item under coherent O1 and parks at the pre-finish seam (no transaction, no lock). T2 then runs on its own
    /// connection and must complete while T1 is parked; the connection row is snapshotted after T2 commits and T1 is released.</summary>
    private async Task<(Guid Account, Guid Original, Guid RunId, MarketplaceListingSyncRun Result, ConnectionState AfterT2)> RunParkedBeforeFinishAsync(Func<VerceDbContext, Guid, Task> t2)
    {
        var account = await SeedAccountAsync(); Guid original;
        await using (var db = Context()) original = (await db.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == account)).ConfirmedOperationId!.Value;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MarketplaceListingSyncExecutionTestHooks.BeforeFinishAsync = async _ => { entered.TrySetResult(); await release.Task; };
        try
        {
            Guid runId; await using (var db = Context()) runId = (await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow)).Id;
            var t1 = Task.Run(async () => { await using var db = Context(); return await CreateExecution(db, new ScriptedReader(["MLB-RACE-1", "MLB-RACE-2"], _clock), detailBatchSize: 1).RunAsync(runId); });
            if (await Task.WhenAny(entered.Task, t1, Task.Delay(TimeSpan.FromSeconds(60))) != entered.Task) { if (t1.IsCompleted) await t1; throw new TimeoutException("T1 never reached the pre-finish barrier"); }
            await using (var observer = Context())
            {
                var parked = await observer.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId);
                parked.Status.Should().Be(MarketplaceListingSyncStatus.RUNNING); parked.LeaseToken.Should().NotBeNull(); parked.ConfirmedOperationIdAtStart.Should().Be(original);
                (await observer.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.SyncRunId == runId && x.Outcome == MarketplaceListingSyncItemOutcome.FOUND)).Should().Be(2);
                (await ConnectionStateAsync(account)).Operation.Should().Be(original);
            }
            await using (var concurrent = Context("race-t2")) await WithSafetyTimeout(t2(concurrent, account), "T2 did not complete while T1 was parked: a lock is held across the barrier");
            var afterT2 = await ConnectionStateAsync(account); release.TrySetResult();
            var result = (await WithSafetyTimeout(t1))!;
            return (account, original, runId, result, afterT2);
        }
        finally { MarketplaceListingSyncExecutionTestHooks.BeforeFinishAsync = null; release.TrySetResult(); }
    }
    private static async Task<T> WithSafetyTimeout<T>(Task<T> task) { if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(60))) != task) throw new TimeoutException("task did not complete"); return await task; }

    private async Task AssertCompletedRaceAsync(Guid account, Guid original, Guid runId, MarketplaceListingSyncRun result)
    {
        result.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED); result.SafeResultCode.Should().Be("LISTING_SYNC_COMPLETED");
        await using var db = Context();
        var run = await db.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId);
        run.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED); run.SafeResultCode.Should().Be("LISTING_SYNC_COMPLETED"); run.LeaseToken.Should().BeNull(); run.ConfirmedOperationIdAtStart.Should().Be(original); run.LastFailureClassification.Should().BeNull();
        (await db.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.SyncRunId == runId && x.Outcome == MarketplaceListingSyncItemOutcome.FOUND)).Should().Be(2);
        (await db.Set<MarketplaceListing>().CountAsync(x => x.MarketplaceAccountId == account)).Should().Be(2); (await db.Set<MarketplaceListingObservation>().CountAsync()).Should().Be(2);
        await AssertSingleFinishedAuditAsync(runId, "SUCCEEDED", "LISTING_SYNC_COMPLETED");
    }

    private Task InsertOperationAsync(VerceDbContext db, Guid id, Guid account, string kind, string? previousReference, long? previousVersion, string candidateReference, long candidateVersion)
        => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,previous_credential_reference,previous_confirmed_credential_version,candidate_credential_reference,candidate_credential_version,created_at,updated_at,cleanup_attempt_count,version) VALUES ({id},{kind},'SECRET_PERSISTED','CONFIRMED','DONE',{account},'MERCADO_LIVRE',{previousReference},{previousVersion},{candidateReference},{candidateVersion},{_clock.UtcNow},{_clock.UtcNow},0,1)");

    [Fact]
    public async Task RACE_1_unrelated_reconnect_after_the_final_effect_leaves_the_run_succeeded_and_the_new_authority_untouched()
    {
        var retryAfter = _clock.UtcNow.AddMinutes(11);
        var (account, original, runId, result, afterT2) = await RunParkedBeforeFinishAsync(async (db, acct) =>
        {
            var o2 = Guid.NewGuid(); await InsertOperationAsync(db, o2, acct, "RECONNECT", null, null, "test-ref", 2);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account_connection SET confirmed_operation_id={o2}, confirmed_credential_version=2, authorization_state='CONNECTED', runtime_availability='UNAVAILABLE', last_failure_classification='RATE_LIMITED', safe_failure_code='PROVIDER_RATE_LIMITED', runtime_retry_after_until={retryAfter}, last_failure_at={_clock.UtcNow} WHERE marketplace_account_id={acct}");
        });
        await AssertCompletedRaceAsync(account, original, runId, result);
        afterT2.Operation.Should().NotBe(original); afterT2.Runtime.Should().Be(MarketplaceRuntimeAvailability.UNAVAILABLE);
        (await ConnectionStateAsync(account)).Should().Be(afterT2, "T1 must not overwrite O2's runtime, failure or retry-after state");
    }

    [Theory]
    [InlineData("REAUTHORIZATION_REQUIRED", "REFRESH_REJECTED")]
    [InlineData("REVOKED", null)]
    public async Task RACE_1c_authorization_state_change_on_the_snapshot_operation_after_the_final_effect_is_not_overwritten_by_finish(string state, string? safeCode)
    {
        var (account, original, runId, result, afterT2) = await RunParkedBeforeFinishAsync(async (db, acct) =>
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account_connection SET authorization_state={state}, runtime_availability='UNKNOWN', safe_failure_code={safeCode} WHERE marketplace_account_id={acct}"));
        await AssertCompletedRaceAsync(account, original, runId, result);
        afterT2.Authorization.ToString().Should().Be(state);
        (await ConnectionStateAsync(account)).Should().Be(afterT2, "finish must neither MarkAvailable nor record PROVIDER_UNAVAILABLE over the newer authorization state");
    }

    [Fact]
    public async Task RACE_2_valid_direct_refresh_after_the_final_effect_lets_finish_write_runtime()
    {
        var (account, original, runId, result, afterT2) = await RunParkedBeforeFinishAsync(async (db, acct) =>
        {
            var o2 = Guid.NewGuid(); await InsertOperationAsync(db, o2, acct, "REFRESH", "test-ref", 1, "test-ref", 2);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account_connection SET confirmed_operation_id={o2}, confirmed_credential_version=2, runtime_availability='UNKNOWN', last_failure_classification=NULL, safe_failure_code=NULL, runtime_retry_after_until=NULL WHERE marketplace_account_id={acct}");
        });
        await AssertCompletedRaceAsync(account, original, runId, result);
        var after = await ConnectionStateAsync(account);
        after.Operation.Should().Be(afterT2.Operation); after.Version.Should().Be(2); after.Runtime.Should().Be(MarketplaceRuntimeAvailability.AVAILABLE); after.LastSuccess.Should().Be(_clock.UtcNow);
        after.Classification.Should().BeNull(); after.SafeCode.Should().BeNull(); after.RetryAfter.Should().BeNull();
    }

    [Fact]
    public async Task RACE_2b_second_refresh_hop_is_not_authorized_for_the_run_snapshot_and_is_left_untouched()
    {
        var (account, original, runId, result, afterT2) = await RunParkedBeforeFinishAsync(async (db, acct) =>
        {
            var o2 = Guid.NewGuid(); var o3 = Guid.NewGuid();
            await InsertOperationAsync(db, o2, acct, "REFRESH", "test-ref", 1, "test-ref", 2); await InsertOperationAsync(db, o3, acct, "REFRESH", "test-ref", 2, "test-ref", 3);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account_connection SET confirmed_operation_id={o3}, confirmed_credential_version=3, runtime_availability='UNKNOWN', last_failure_classification=NULL, safe_failure_code=NULL, runtime_retry_after_until=NULL WHERE marketplace_account_id={acct}");
        });
        await AssertCompletedRaceAsync(account, original, runId, result);
        afterT2.Runtime.Should().Be(MarketplaceRuntimeAvailability.UNKNOWN);
        (await ConnectionStateAsync(account)).Should().Be(afterT2, "O1 -> O2 -> O3 must not be traversed to authorize O3 for the O1 snapshot");
    }

    [Fact]
    public async Task Finish_transaction_failure_propagates_without_a_second_finish_or_provider_stop_handling()
    {
        var account = await SeedAccountAsync(); var before = await ConnectionStateAsync(account);
        await using (var ddl = Context())
        {
            await ddl.Database.ExecuteSqlRawAsync("CREATE SEQUENCE test_b2_finish_seq START 1");
            await ddl.Database.ExecuteSqlRawAsync("CREATE FUNCTION test_b2_finish_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM nextval('test_b2_finish_seq'); RAISE EXCEPTION 'test finish failure' USING ERRCODE = 'P0001'; END; $$");
            await ddl.Database.ExecuteSqlRawAsync("CREATE TRIGGER test_b2_finish_trigger BEFORE UPDATE ON commerce.marketplace_listing_sync_run FOR EACH ROW WHEN (OLD.status = 'RUNNING' AND NEW.status IN ('SUCCEEDED','PARTIAL','FAILED')) EXECUTE FUNCTION test_b2_finish_fail()");
        }
        try
        {
            Guid runId; Exception? thrown;
            await using (var db = Context())
            {
                runId = (await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow)).Id;
                thrown = await Record.ExceptionAsync(() => CreateExecution(db, new ScriptedReader(["MLB-FINISH-FAIL"], _clock)).RunAsync(runId));
            }
            thrown.Should().NotBeNull(); thrown.Should().NotBeOfType<MarketplaceListingSyncLeaseLostException>(); thrown!.ToString().Should().Contain("test finish failure");
            await using var verify = Context();
            var run = await verify.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId);
            run.Status.Should().Be(MarketplaceListingSyncStatus.RUNNING); run.LeaseToken.Should().NotBeNull(); run.SafeResultCode.Should().BeNull();
            (await verify.Set<MarketplaceListingSyncRunItem>().SingleAsync(x => x.SyncRunId == runId)).Outcome.Should().Be(MarketplaceListingSyncItemOutcome.FOUND);
            await using var conn = new NpgsqlConnection(_connectionString); await conn.OpenAsync(); await using var cmd = new NpgsqlCommand("SELECT is_called, last_value FROM test_b2_finish_seq", conn); await using var reader = await cmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue(); reader.GetBoolean(0).Should().BeTrue(); reader.GetInt64(1).Should().Be(1, "exactly one finish attempt: the failure must not be re-interpreted as a provider stop and finished again");
            await reader.DisposeAsync(); (await ConnectionStateAsync(account)).Should().Be(before, "no runtime or failure write may survive the failed finish");
            (await AuditRowsAsync(runId)).Should().BeEmpty("a run that failed to terminalize must never have a durable LISTING_SYNC_FINISHED row");
        }
        finally
        {
            await using var cleanup = Context();
            await cleanup.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS test_b2_finish_trigger ON commerce.marketplace_listing_sync_run");
            await cleanup.Database.ExecuteSqlRawAsync("DROP FUNCTION IF EXISTS test_b2_finish_fail()");
            await cleanup.Database.ExecuteSqlRawAsync("DROP SEQUENCE IF EXISTS test_b2_finish_seq");
        }
    }

    // ---- ADR-0025 §K: fenced, allowlisted LISTING_SYNC_FINISHED audit ----
    private async Task<List<AuditLogEntry>> AuditRowsAsync(Guid runId)
    {
        await using var db = Context();
        return await db.Set<AuditLogEntry>().AsNoTracking().Where(x => x.EntityTable == "marketplace_listing_sync_run" && x.EntityId == runId).ToListAsync();
    }

    private async Task AssertSingleFinishedAuditAsync(Guid runId, string status, string safeResultCode, string kind = "FULL")
    {
        var rows = await AuditRowsAsync(runId);
        rows.Should().ContainSingle("exactly one audit row exists for the run: LISTING_SYNC_FINISHED, written once by the fenced terminal transition");
        var row = rows[0];
        row.EntitySchema.Should().Be("commerce"); row.Operation.Should().Be("MODIFIED"); row.OldValuesJson.Should().BeNull(); row.ChangedColumns.Should().BeNull();
        using var doc = JsonDocument.Parse(row.NewValuesJson!); var root = doc.RootElement;
        root.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo(["event", "kind", "status", "safeResultCode", "counters"], "structural allowlist: kind/status/counters/safe code only");
        root.GetProperty("counters").EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo(["total", "found", "blockingRetryable", "blockingNonRetryable", "pending", "listingsCreated", "observationsAppended"]);
        root.GetProperty("event").GetString().Should().Be("LISTING_SYNC_FINISHED"); root.GetProperty("kind").GetString().Should().Be(kind);
        root.GetProperty("status").GetString().Should().Be(status); root.GetProperty("safeResultCode").GetString().Should().Be(safeResultCode);
        await using var db = Context(); var run = await db.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId);
        run.Status.ToString().Should().Be(status, "the audit records the run state that actually committed"); run.SafeResultCode.Should().Be(safeResultCode);
    }

    private async Task<(Guid RunId, MarketplaceListingSyncRun Run)> RunFullAsync(Guid account, ScriptedReader reader)
    {
        await using var db = Context(); var created = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
        return (created.Id, (await CreateExecution(db, reader).RunAsync(created.Id))!);
    }

    [Fact]
    public async Task Finished_audit_succeeded_run_has_exactly_one_allowlisted_row_without_provider_or_credential_material()
    {
        var account = await SeedAccountAsync(); const string canary = "CANARY-PROVIDER-TITLE-9f31";
        var reader = new ScriptedReader(["MLB-AUD-1", "MLB-AUD-2"], _clock) { Facts = { ["MLB-AUD-1"] = FactsFor("MLB-AUD-1", "SKU-A1") with { Title = canary } } };
        var (runId, run) = await RunFullAsync(account, reader);
        run.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        await AssertSingleFinishedAuditAsync(runId, "SUCCEEDED", "LISTING_SYNC_COMPLETED");
        var json = (await AuditRowsAsync(runId))[0].NewValuesJson!;
        json.Should().NotContain(canary).And.NotContain("test-only").And.NotContain("Authorization").And.NotContain("token").And.NotContain("test-ref").And.NotContain(account.ToString());
        using var doc = JsonDocument.Parse(json); doc.RootElement.GetProperty("counters").GetProperty("found").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Finished_audit_partial_run_has_exactly_one_row_with_the_committed_terminal_state()
    {
        var account = await SeedAccountAsync(); var reader = new ScriptedReader(["MLB-AUD-OK", "MLB-AUD-BAD"], _clock); reader.Outcomes["MLB-AUD-BAD"] = MarketplaceListingReadOutcome.FAILED;
        var (runId, run) = await RunFullAsync(account, reader);
        run.Status.Should().Be(MarketplaceListingSyncStatus.PARTIAL);
        await AssertSingleFinishedAuditAsync(runId, "PARTIAL", "LISTING_SYNC_PARTIAL");
        using var doc = JsonDocument.Parse((await AuditRowsAsync(runId))[0].NewValuesJson!); var counters = doc.RootElement.GetProperty("counters");
        counters.GetProperty("found").GetInt32().Should().Be(1); counters.GetProperty("blockingRetryable").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Finished_audit_failed_run_has_exactly_one_row_with_the_safe_result_code_only()
    {
        var account = await SeedAccountAsync(); var reader = new ScriptedReader(["MLB-AUD-ONLY"], _clock); reader.Outcomes["MLB-AUD-ONLY"] = MarketplaceListingReadOutcome.FAILED;
        var (runId, run) = await RunFullAsync(account, reader);
        run.Status.Should().Be(MarketplaceListingSyncStatus.FAILED);
        await AssertSingleFinishedAuditAsync(runId, "FAILED", run.SafeResultCode!);
    }

    [Fact]
    public async Task Finished_audit_row_is_rolled_back_with_the_terminal_transition_when_the_transaction_fails_after_the_audit_insert()
    {
        var account = await SeedAccountAsync(); var before = await ConnectionStateAsync(account);
        await using (var ddl = Context())
        {
            await ddl.Database.ExecuteSqlRawAsync("CREATE FUNCTION test_b2_audit_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.entity_table = 'marketplace_listing_sync_run' THEN RAISE EXCEPTION 'test audit failure after insert' USING ERRCODE = 'P0001'; END IF; RETURN NEW; END; $$");
            await ddl.Database.ExecuteSqlRawAsync("CREATE TRIGGER test_b2_audit_fail_trigger AFTER INSERT ON platform.audit_log FOR EACH ROW EXECUTE FUNCTION test_b2_audit_fail()");
        }
        try
        {
            Guid runId; Exception? thrown;
            await using (var db = Context())
            {
                runId = (await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow)).Id;
                thrown = await Record.ExceptionAsync(() => CreateExecution(db, new ScriptedReader(["MLB-AUD-RB"], _clock)).RunAsync(runId));
            }
            thrown.Should().NotBeNull(); thrown.Should().NotBeOfType<MarketplaceListingSyncLeaseLostException>(); thrown!.ToString().Should().Contain("test audit failure after insert");
            await using var verify = Context(); var run = await verify.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId);
            run.Status.Should().Be(MarketplaceListingSyncStatus.RUNNING, "the terminal transition rolled back with the audit row"); run.SafeResultCode.Should().BeNull(); run.FinishedAt.Should().BeNull(); run.LeaseToken.Should().NotBeNull();
            (await AuditRowsAsync(runId)).Should().BeEmpty(); (await ConnectionStateAsync(account)).Should().Be(before);
        }
        finally
        {
            await using var cleanup = Context();
            await cleanup.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS test_b2_audit_fail_trigger ON platform.audit_log");
            await cleanup.Database.ExecuteSqlRawAsync("DROP FUNCTION IF EXISTS test_b2_audit_fail()");
        }
    }

    [Fact]
    public async Task Finished_audit_stale_lease_and_terminal_reentry_never_add_a_second_row()
    {
        var account = await SeedAccountAsync(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MarketplaceListingSyncExecutionTestHooks.BeforeFinishAsync = async _ => { entered.TrySetResult(); await release.Task; };
        try
        {
            Guid runId; await using (var db = Context()) runId = (await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow)).Id;
            var t1 = Task.Run(async () => { await using var db = Context(); return await Record.ExceptionAsync(() => CreateExecution(db, new ScriptedReader(["MLB-AUD-STALE"], _clock)).RunAsync(runId)); });
            await WithSafetyTimeout(entered.Task, "T1 never reached the pre-finish barrier");
            _clock.UtcNow = _clock.UtcNow.AddMinutes(3);
            await using (var db = Context()) (await CreateExecution(db, new ScriptedReader(["MLB-AUD-STALE"], _clock)).RunAsync(runId))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
            await AssertSingleFinishedAuditAsync(runId, "SUCCEEDED", "LISTING_SYNC_COMPLETED");
            release.TrySetResult(); (await WithSafetyTimeout(t1)).Should().BeOfType<MarketplaceListingSyncLeaseLostException>("T1's finish is rejected by the lease fence");
            await AssertSingleFinishedAuditAsync(runId, "SUCCEEDED", "LISTING_SYNC_COMPLETED");
            // Reachable terminal reentry: reclaim of a terminal run, and a replayed finish with any token.
            await using (var db = Context()) (await CreateExecution(db, new ScriptedReader(["MLB-AUD-STALE"], _clock)).RunAsync(runId)).Should().BeNull();
            await using (var db = Context()) (await Record.ExceptionAsync(() => new MarketplaceListingSyncRunKernel(db).FinalizeWithLeaseAsync(runId, Guid.NewGuid(), null, _clock.UtcNow, TimeSpan.FromMinutes(2)))).Should().BeOfType<MarketplaceListingSyncLeaseLostException>();
            await AssertSingleFinishedAuditAsync(runId, "SUCCEEDED", "LISTING_SYNC_COMPLETED");
        }
        finally { MarketplaceListingSyncExecutionTestHooks.BeforeFinishAsync = null; release.TrySetResult(); }
    }

    // ---- handoff B5: StartFullAsync / RetryAsync with one-UoW creation audits (ADR-0025 §K) ----
    private sealed class NoDispatcher : IDomainEventDispatcher
    { public Task DispatchAsync(Verce.SharedKernel.Events.IDomainEvent domainEvent, VerceDbContext context, CancellationToken cancellationToken) => Task.CompletedTask; }

    /// <summary>The real transactional UnitOfWork over the given context, so run + audit atomicity is proven against the production unit of work.</summary>
    private MarketplaceListingSyncService CreateService(VerceDbContext db, MarketplaceListingSyncOptions? settings = null, Quartz.ISchedulerFactory? scheduler = null, IMarketplaceListingReader? reader = null, Action? onFreshScope = null)
    {
        settings ??= new MarketplaceListingSyncOptions { SchedulingEnabled = false };
        var ambient = new AmbientOperationContext(Guid.NewGuid(), AuditSource.Api, _clock.UtcNow); ambient.SetActor(null, "tester");
        // A fresh scope factory over the same database: the service re-reads an active-run race winner from a new scope/context.
        var factory = new ServiceCollection().AddScoped(_ => Context()).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        if (onFreshScope is not null) factory = new HookedScopeFactory(factory, onFreshScope);
        var triggerServices = new ServiceCollection(); if (scheduler is not null) triggerServices.AddSingleton(scheduler);
        var trigger = new MarketplaceListingSyncDispatchTrigger(triggerServices.BuildServiceProvider(), Options.Create(settings), NullLogger<MarketplaceListingSyncDispatchTrigger>.Instance);
        return new MarketplaceListingSyncService(new Verce.Platform.UnitOfWork.UnitOfWork(db, ambient, new NoDispatcher(), _clock), _clock, new Registry(reader ?? new ScriptedReader([], _clock)), ambient, factory, trigger, Options.Create(settings));
    }

    private async Task<List<string>> AuditEventsAsync(Guid runId)
    {
        var events = new List<string>();
        foreach (var row in await AuditRowsAsync(runId)) { using var doc = JsonDocument.Parse(row.NewValuesJson!); events.Add(doc.RootElement.GetProperty("event").GetString()!); }
        return events;
    }

    private async Task AssertCreationAuditAsync(Guid runId, string expectedEvent, string kind, Guid actor)
    {
        var rows = await AuditRowsAsync(runId); rows.Should().ContainSingle(x => x.NewValuesJson!.Contains(expectedEvent));
        var row = rows.Single(x => x.NewValuesJson!.Contains(expectedEvent));
        row.EntitySchema.Should().Be("commerce"); row.Operation.Should().Be("MODIFIED"); row.UserId.Should().Be(actor); row.Source.Should().Be(AuditSource.Api); row.OldValuesJson.Should().BeNull(); row.ChangedColumns.Should().BeNull();
        using var doc = JsonDocument.Parse(row.NewValuesJson!);
        doc.RootElement.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo(["event", "kind", "status"], "structural allowlist: a creation fact carries only event, kind and status");
        doc.RootElement.GetProperty("event").GetString().Should().Be(expectedEvent); doc.RootElement.GetProperty("kind").GetString().Should().Be(kind); doc.RootElement.GetProperty("status").GetString().Should().Be("QUEUED");
        row.NewValuesJson.Should().NotContain("test-ref").And.NotContain("test-only").And.NotContain("Authorization").And.NotContain("token").And.NotContain("seller-");
    }

    private async Task<(Guid Account, Guid RunId)> BuildPartialOriginalAsync()
    {
        var account = await SeedAccountAsync();
        var reader = new ScriptedReader(["MLB-RT-OK", "MLB-RT-BAD", "MLB-RT-NF", "MLB-RT-SM"], _clock);
        reader.Outcomes["MLB-RT-BAD"] = MarketplaceListingReadOutcome.FAILED; reader.Outcomes["MLB-RT-NF"] = MarketplaceListingReadOutcome.NOT_FOUND; reader.Outcomes["MLB-RT-SM"] = MarketplaceListingReadOutcome.SELLER_MISMATCH;
        var (runId, run) = await RunFullAsync(account, reader); run.Status.Should().Be(MarketplaceListingSyncStatus.PARTIAL);
        return (account, runId);
    }

    private async Task InstallAuditFailureTriggerAsync()
    {
        await using var ddl = Context();
        await ddl.Database.ExecuteSqlRawAsync("CREATE FUNCTION test_b5_audit_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.entity_table = 'marketplace_listing_sync_run' THEN RAISE EXCEPTION 'test creation audit failure' USING ERRCODE = 'P0001'; END IF; RETURN NEW; END; $$");
        await ddl.Database.ExecuteSqlRawAsync("CREATE TRIGGER test_b5_audit_fail_trigger AFTER INSERT ON platform.audit_log FOR EACH ROW EXECUTE FUNCTION test_b5_audit_fail()");
    }

    [Fact]
    public async Task B5_start_full_creates_one_queued_run_and_exactly_one_requested_audit_in_one_unit_of_work()
    {
        var account = await SeedAccountAsync(); var actor = Guid.NewGuid(); MarketplaceListingSyncRequestResult result;
        await using (var db = Context()) result = await CreateService(db).StartFullAsync(account, actor);
        result.Succeeded.Should().BeTrue(); result.Code.Should().BeNull(); var runId = result.RunId!.Value;
        await using var verify = Context(); var run = await verify.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync();
        run.Id.Should().Be(runId); run.MarketplaceAccountId.Should().Be(account); run.Kind.Should().Be(MarketplaceListingSyncKind.FULL); run.Status.Should().Be(MarketplaceListingSyncStatus.QUEUED); run.RootRunId.Should().Be(runId); run.RetryOfRunId.Should().BeNull();
        run.RequestedByUserId.Should().Be(actor); run.AttemptCount.Should().Be(0); run.LeaseToken.Should().BeNull(); run.ConfirmedOperationIdAtStart.Should().BeNull("the authorization snapshot is captured at claim, not at request");
        await AssertCreationAuditAsync(runId, "LISTING_SYNC_REQUESTED", "FULL", actor);
        (await AuditEventsAsync(runId)).Should().NotContain(["LISTING_SYNC_RETRY_REQUESTED", "LISTING_SYNC_FINISHED"]);
    }

    [Fact]
    public async Task B5_start_full_run_and_audit_roll_back_together_when_audit_persistence_fails()
    {
        var account = await SeedAccountAsync(); await InstallAuditFailureTriggerAsync();
        Exception? thrown; await using (var db = Context()) thrown = await Record.ExceptionAsync(() => CreateService(db).StartFullAsync(account, Guid.NewGuid()));
        thrown.Should().NotBeNull(); thrown!.ToString().Should().Contain("test creation audit failure");
        await using var verify = Context();
        (await verify.Set<MarketplaceListingSyncRun>().CountAsync()).Should().Be(0, "no orphan run"); (await verify.Set<AuditLogEntry>().CountAsync(x => x.EntityTable == "marketplace_listing_sync_run")).Should().Be(0, "no orphan audit");
    }

    [Fact]
    public async Task B5_start_full_rejects_a_request_that_loses_the_unique_index_race_and_leaves_no_audit()
    {
        // Deterministic: a competing, still-uncommitted QUEUED run is invisible to the pre-check, so the service's insert blocks on the
        // partial unique index until that run commits, then fails with 23505 and must report the winner from a fresh unit of work.
        var account = await SeedAccountAsync(); Guid winner;
        await using var competing = Context("b5-competitor"); await using var tx = await competing.Database.BeginTransactionAsync();
        winner = (await new MarketplaceListingSyncRunKernel(competing).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow)).Id;
        var request = Task.Run(async () => { await using var db = Context("b5-service"); return await CreateService(db).StartFullAsync(account, Guid.NewGuid()); });
        await using (var observer = new NpgsqlConnection(_connectionString))
        {
            await observer.OpenAsync(); var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (true)
            {
                if (request.IsCompleted) throw new InvalidOperationException("the request completed without blocking on the competing uncommitted run");
                await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity w WHERE w.application_name = 'b5-service' AND w.wait_event_type = 'Lock')", observer);
                if ((bool)(await command.ExecuteScalarAsync())!) break;
                if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("service request never blocked on the unique index");
                await Task.Delay(25);
            }
        }
        await tx.CommitAsync();
        var result = await WithSafetyTimeout(request);
        result.Succeeded.Should().BeFalse(); result.Code.Should().Be("LISTING_SYNC_ALREADY_ACTIVE"); result.ActiveRunId.Should().Be(winner);
        await using var verify = Context();
        (await verify.Set<MarketplaceListingSyncRun>().CountAsync()).Should().Be(1); (await verify.Set<AuditLogEntry>().CountAsync(x => x.EntityTable == "marketplace_listing_sync_run")).Should().Be(0, "the rejected request left no audit and the competitor wrote none");
    }

    [Fact]
    public async Task B5_concurrent_start_requests_commit_exactly_one_run_with_exactly_one_requested_audit()
    {
        var account = await SeedAccountAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(async () => { await using var db = Context(); return await CreateService(db).StartFullAsync(account, Guid.NewGuid()); })));
        results.Count(x => x.Succeeded).Should().Be(1); var winner = results.Single(x => x.Succeeded).RunId!.Value;
        results.Where(x => !x.Succeeded).Should().OnlyContain(x => x.Code == "LISTING_SYNC_ALREADY_ACTIVE" && x.ActiveRunId == winner);
        await using var verify = Context(); (await verify.Set<MarketplaceListingSyncRun>().CountAsync()).Should().Be(1);
        (await verify.Set<AuditLogEntry>().CountAsync(x => x.EntityTable == "marketplace_listing_sync_run")).Should().Be(1, "audit count equals committed run count");
        (await AuditEventsAsync(winner)).Should().Equal("LISTING_SYNC_REQUESTED");
    }

    [Theory]
    [InlineData("UPDATE commerce.marketplace_account_connection SET runtime_availability='UNAVAILABLE'", "COMMERCE_OPERATION_UNAVAILABLE", "RUNTIME_UNAVAILABLE")]
    [InlineData("UPDATE commerce.marketplace_account_connection SET runtime_retry_after_until=now() + interval '1 day'", "COMMERCE_OPERATION_UNAVAILABLE", "RATE_LIMITED")]
    [InlineData("UPDATE commerce.marketplace_account_capability SET state='UNKNOWN'", "COMMERCE_CAPABILITY_UNAVAILABLE", "GRANT_UNKNOWN")]
    [InlineData("UPDATE commerce.marketplace_account_connection SET authorization_state='REAUTHORIZATION_REQUIRED'", "MARKETPLACE_AUTHORIZATION_NOT_USABLE", "AUTHORIZATION_NOT_USABLE")]
    [InlineData("UPDATE commerce.marketplace_account SET active=false", "MARKETPLACE_ACCOUNT_INACTIVE", "ACCOUNT_INACTIVE")]
    public async Task B5_guard_refusals_create_no_run_and_no_audit(string mutation, string code, string? reason)
    {
        var account = await SeedAccountAsync();
        await using (var db = Context()) await db.Database.ExecuteSqlRawAsync(mutation);
        MarketplaceListingSyncRequestResult result; await using (var db = Context()) result = await CreateService(db).StartFullAsync(account, Guid.NewGuid());
        result.Succeeded.Should().BeFalse(); result.Code.Should().Be(code); result.Reason.Should().Be(reason);
        await using var verify = Context(); (await verify.Set<MarketplaceListingSyncRun>().CountAsync()).Should().Be(0); (await verify.Set<AuditLogEntry>().CountAsync(x => x.EntityTable == "marketplace_listing_sync_run")).Should().Be(0);
    }

    [Fact]
    public async Task B5_retry_creates_a_server_derived_failed_only_run_with_ancestry_and_exactly_one_retry_requested_audit()
    {
        var (account, originalId) = await BuildPartialOriginalAsync(); var actor = Guid.NewGuid(); MarketplaceListingSyncRequestResult result;
        typeof(MarketplaceListingSyncService).GetMethod(nameof(MarketplaceListingSyncService.RetryAsync))!.GetParameters().Select(x => x.ParameterType).Should().Equal(new List<Type> { typeof(Guid), typeof(Guid), typeof(CancellationToken) }, "callers can only name the source run and the actor: retry targets cannot be supplied");
        await using (var db = Context()) result = await CreateService(db).RetryAsync(originalId, actor);
        result.Succeeded.Should().BeTrue(); var retryId = result.RunId!.Value; retryId.Should().NotBe(originalId);
        await using var verify = Context();
        var retry = await verify.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == retryId);
        retry.Kind.Should().Be(MarketplaceListingSyncKind.FAILED_ONLY); retry.Status.Should().Be(MarketplaceListingSyncStatus.QUEUED); retry.RetryOfRunId.Should().Be(originalId); retry.RootRunId.Should().Be(originalId); retry.MarketplaceAccountId.Should().Be(account); retry.RequestedByUserId.Should().Be(actor); retry.ConfirmedOperationIdAtStart.Should().BeNull();
        var items = await verify.Set<MarketplaceListingSyncRunItem>().AsNoTracking().Where(x => x.SyncRunId == retryId).ToListAsync();
        items.Select(x => x.ExternalListingId).Should().BeEquivalentTo(["MLB-RT-BAD", "MLB-RT-NF"], "only retryable outcomes (TRANSIENT_ERROR, NOT_FOUND) are copied: FOUND and SELLER_MISMATCH are not");
        items.Should().OnlyContain(x => x.Origin == MarketplaceListingSyncItemOrigin.RETRY_TARGET && x.Outcome == MarketplaceListingSyncItemOutcome.PENDING);
        await AssertCreationAuditAsync(retryId, "LISTING_SYNC_RETRY_REQUESTED", "FAILED_ONLY", actor);
        (await AuditEventsAsync(retryId)).Should().NotContain(["LISTING_SYNC_REQUESTED", "LISTING_SYNC_FINISHED"]);
        (await AuditEventsAsync(originalId)).Should().Equal("LISTING_SYNC_FINISHED");
        var original = await verify.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == originalId); original.Status.Should().Be(MarketplaceListingSyncStatus.PARTIAL); original.RetryOfRunId.Should().BeNull();
        (await verify.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.SyncRunId == originalId)).Should().Be(4);
    }

    [Fact]
    public async Task B5_retry_without_retryable_items_or_with_an_unknown_run_creates_nothing()
    {
        var account = await SeedAccountAsync(); var (succeededId, succeeded) = await RunFullAsync(account, new ScriptedReader(["MLB-RT-DONE"], _clock)); succeeded.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        MarketplaceListingSyncRequestResult none;
        await using (var db = Context()) none = await CreateService(db).RetryAsync(succeededId, Guid.NewGuid());
        await using (var db = Context()) (await Record.ExceptionAsync(() => CreateService(db).RetryAsync(Guid.NewGuid(), Guid.NewGuid()))).Should().BeOfType<KeyNotFoundException>();
        none.Succeeded.Should().BeFalse(); none.Code.Should().Be("LISTING_SYNC_NOTHING_TO_RETRY");
        await using var verify = Context(); (await verify.Set<MarketplaceListingSyncRun>().CountAsync()).Should().Be(1);
        (await verify.Set<AuditLogEntry>().CountAsync(x => x.EntityTable == "marketplace_listing_sync_run")).Should().Be(1, "only the original run's FINISHED audit exists");
    }

    [Fact]
    public async Task B5_retry_run_items_ancestry_and_audit_roll_back_together_when_audit_persistence_fails()
    {
        var (_, originalId) = await BuildPartialOriginalAsync(); await InstallAuditFailureTriggerAsync();
        Exception? thrown; await using (var db = Context()) thrown = await Record.ExceptionAsync(() => CreateService(db).RetryAsync(originalId, Guid.NewGuid()));
        thrown.Should().NotBeNull(); thrown!.ToString().Should().Contain("test creation audit failure");
        await using var verify = Context();
        (await verify.Set<MarketplaceListingSyncRun>().CountAsync()).Should().Be(1, "no retry run"); (await verify.Set<MarketplaceListingSyncRun>().CountAsync(x => x.RetryOfRunId != null)).Should().Be(0);
        (await verify.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.Origin == MarketplaceListingSyncItemOrigin.RETRY_TARGET)).Should().Be(0, "no retry items");
        (await AuditEventsAsync(originalId)).Should().Equal("LISTING_SYNC_FINISHED"); (await verify.Set<AuditLogEntry>().CountAsync(x => x.EntityTable == "marketplace_listing_sync_run")).Should().Be(1);
        var original = await verify.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(); original.Status.Should().Be(MarketplaceListingSyncStatus.PARTIAL);
    }

    [Fact]
    public async Task B5_full_lifecycle_owns_each_audit_event_exactly_once_for_start_and_for_retry()
    {
        var account = await SeedAccountAsync(); var actor = Guid.NewGuid(); MarketplaceListingSyncRequestResult started;
        await using (var db = Context()) started = await CreateService(db).StartFullAsync(account, actor);
        var fullId = started.RunId!.Value; (await AuditEventsAsync(fullId)).Should().Equal("LISTING_SYNC_REQUESTED");
        var reader = new ScriptedReader(["MLB-LC-OK", "MLB-LC-BAD"], _clock); reader.Outcomes["MLB-LC-BAD"] = MarketplaceListingReadOutcome.FAILED;
        await using (var db = Context()) (await CreateExecution(db, reader).RunAsync(fullId))!.Status.Should().Be(MarketplaceListingSyncStatus.PARTIAL);
        (await AuditEventsAsync(fullId)).Should().BeEquivalentTo(["LISTING_SYNC_REQUESTED", "LISTING_SYNC_FINISHED"]);
        MarketplaceListingSyncRequestResult retried; await using (var db = Context()) retried = await CreateService(db).RetryAsync(fullId, actor);
        var retryId = retried.RunId!.Value; (await AuditEventsAsync(retryId)).Should().Equal("LISTING_SYNC_RETRY_REQUESTED");
        var healed = new ScriptedReader([], _clock); await using (var db = Context()) (await CreateExecution(db, healed).RunAsync(retryId))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        (await AuditEventsAsync(retryId)).Should().BeEquivalentTo(["LISTING_SYNC_RETRY_REQUESTED", "LISTING_SYNC_FINISHED"]);
        (await AuditEventsAsync(fullId)).Should().BeEquivalentTo(["LISTING_SYNC_REQUESTED", "LISTING_SYNC_FINISHED"], "execution of the retry never re-emits the original's creation audit");
    }

    [Fact]
    public async Task Reclaimed_t2_provider_effect_snapshot_is_unchanged_when_stale_t1_resumes()
    {
        var account = await SeedAccountAsync(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MarketplaceListingSyncExecutionTestHooks.BeforeFencedDetailPersistenceAsync = async _ => { entered.TrySetResult(); await resume.Task; };
        Guid runId;
        try
        {
            await using (var db = Context()) runId = (await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow)).Id;
            var t1 = Task.Run(async () => { await using var db = Context(); return await Record.ExceptionAsync(() => CreateExecution(db, new ScriptedReader(["MLB-STALE-T1"], _clock)).RunAsync(runId)); });
            await entered.Task;
            _clock.UtcNow = _clock.UtcNow.AddMinutes(3);
            await using (var db = Context()) (await CreateExecution(db, new ScriptedReader(["MLB-STALE-T1"], _clock)).RunAsync(runId))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
            StaleSnapshot before; await using (var db = Context()) before = await SnapshotAsync(db, account, runId);
            resume.TrySetResult(); (await t1).Should().BeOfType<MarketplaceListingSyncLeaseLostException>();
            await using (var db = Context()) (await SnapshotAsync(db, account, runId)).Should().BeEquivalentTo(before);
        }
        finally { MarketplaceListingSyncExecutionTestHooks.BeforeFencedDetailPersistenceAsync = null; resume.TrySetResult(); }
    }

    [Fact]
    public async Task Failure_after_real_multi_table_save_rolls_back_listing_observation_linkage_and_run_item()
    {
        var account = await SeedAccountAsync(); var product = Guid.NewGuid(); var sku = "SKU-ROLLBACK";
        Guid runId;
        await using (var db = Context())
        {
            // Start genuinely unlinked.  The mapping makes B2 attempt the real
            // UNLINKED -> DETERMINISTIC_SKU linkage before the injected pre-commit fault.
            var listing = new MarketplaceListing(account, "MLB-ROLLBACK-B2", sku, MarketplaceListingStatus.ACTIVE, "before", 9m);
            db.Add(listing); db.Add(new MarketplaceAccountSkuMapping(account, sku, product, Guid.NewGuid(), _clock.UtcNow)); await db.SaveChangesAsync();
            runId = (await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow)).Id;
        }
        MarketplaceListingSyncExecutionTestHooks.AfterDetailSaveBeforeCommitAsync = _ => Task.FromException(new MarketplaceListingSyncTestRollbackException());
        try
        {
            await using var db = Context(); var act = () => CreateExecution(db, new ScriptedReader(["MLB-ROLLBACK-B2"], _clock) { Facts = { ["MLB-ROLLBACK-B2"] = FactsFor("MLB-ROLLBACK-B2", sku) with { Title = "after", ObservedPrice = 10m } } }).RunAsync(runId);
            await act.Should().ThrowAsync<MarketplaceListingSyncTestRollbackException>();
        }
        finally { MarketplaceListingSyncExecutionTestHooks.AfterDetailSaveBeforeCommitAsync = null; }
        await using (var db = Context())
        {
            var listing = await db.Set<MarketplaceListing>().Include(x => x.Observations).SingleAsync(x => x.ExternalListingId == "MLB-ROLLBACK-B2"); listing.TitleSnapshot.Should().Be("before"); listing.ObservedPrice.Should().Be(9m); listing.ProductId.Should().BeNull(); listing.LinkageState.Should().Be(MarketplaceLinkageState.UNLINKED); listing.LinkageSource.Should().BeNull(); listing.SkuMappingId.Should().BeNull(); listing.SyncState.Should().Be(MarketplaceSyncState.NEVER_SYNCED); listing.Observations.Should().HaveCount(1);
            var item = await db.Set<MarketplaceListingSyncRunItem>().SingleAsync(x => x.SyncRunId == runId); item.Outcome.Should().Be(MarketplaceListingSyncItemOutcome.PENDING); item.MarketplaceListingId.Should().BeNull(); item.AttemptCount.Should().Be(0);
            var run = await db.Set<MarketplaceListingSyncRun>().SingleAsync(x => x.Id == runId); run.Status.Should().Be(MarketplaceListingSyncStatus.RUNNING); run.Phase.Should().Be(MarketplaceListingSyncPhase.DETAILING);
        }
    }

    [Fact]
    public async Task PostgreSql_unique_conflict_retries_once_then_persists_one_authoritative_effect()
    {
        var account = await SeedAccountAsync(); await InstallListingConflictTriggerAsync(1);
        try
        {
            await using (var db = Context()) { var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow); (await CreateExecution(db, new ScriptedReader(["MLB-CONFLICT-ONCE"], _clock)).RunAsync(run.Id))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED); }
            await using (var db = Context()) { (await db.Set<MarketplaceListing>().CountAsync(x => x.ExternalListingId == "MLB-CONFLICT-ONCE")).Should().Be(1); (await db.Set<MarketplaceListingObservation>().CountAsync()).Should().Be(1); var item = await db.Set<MarketplaceListingSyncRunItem>().SingleAsync(); item.Outcome.Should().Be(MarketplaceListingSyncItemOutcome.FOUND); item.AttemptCount.Should().Be(1); }
        }
        finally { await RemoveListingConflictTriggerAsync(); }
    }

    [Fact]
    public async Task PostgreSql_unique_conflict_exhausts_exactly_three_attempts_without_partial_effects()
    {
        var account = await SeedAccountAsync(); await InstallListingConflictTriggerAsync(3);
        try
        {
            await using (var db = Context()) { var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow); (await CreateExecution(db, new ScriptedReader(["MLB-CONFLICT-THRICE"], _clock)).RunAsync(run.Id))!.Status.Should().Be(MarketplaceListingSyncStatus.FAILED); }
            await using (var db = Context()) { (await db.Set<MarketplaceListing>().CountAsync(x => x.ExternalListingId == "MLB-CONFLICT-THRICE")).Should().Be(0); (await db.Set<MarketplaceListingObservation>().CountAsync()).Should().Be(0); var item = await db.Set<MarketplaceListingSyncRunItem>().SingleAsync(); item.Outcome.Should().Be(MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR); item.SafeErrorCode.Should().Be("LISTING_WRITE_CONFLICT"); item.AttemptCount.Should().Be(1); }
        }
        finally { await RemoveListingConflictTriggerAsync(); }
    }

    [Fact]
    public async Task Lease_reclaim_between_real_write_conflict_retries_blocks_t1_before_any_retry_write()
    {
        var account = await SeedAccountAsync(); await InstallListingConflictTriggerAsync(1); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Guid runId;
        MarketplaceListingSyncExecutionTestHooks.BeforeWriteRetryAsync = async (_, _) => { entered.TrySetResult(); await resume.Task; };
        try
        {
            await using (var db = Context()) runId = (await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow)).Id;
            var t1 = Task.Run(async () => { await using var db = Context(); return await Record.ExceptionAsync(() => CreateExecution(db, new ScriptedReader(["MLB-LEASE-RETRY"], _clock)).RunAsync(runId)); });
            await entered.Task; _clock.UtcNow = _clock.UtcNow.AddMinutes(3);
            MarketplaceListingSyncLease t2; await using (var db = Context()) t2 = (await new MarketplaceListingSyncLeaseAuthority(db).ReclaimAsync(runId, _clock.UtcNow, TimeSpan.FromMinutes(2)))!;
            StaleSnapshot before; await using (var db = Context()) before = await SnapshotAsync(db, account, runId);
            resume.TrySetResult(); (await t1).Should().BeOfType<MarketplaceListingSyncLeaseLostException>();
            await using (var db = Context()) { (await SnapshotAsync(db, account, runId)).Should().BeEquivalentTo(before); (await db.Set<MarketplaceListing>().CountAsync()).Should().Be(0); (await db.Set<MarketplaceListingObservation>().CountAsync()).Should().Be(0); (await db.Set<MarketplaceListingSyncRun>().SingleAsync(x => x.Id == runId)).LeaseToken.Should().Be(t2.LeaseToken); }
        }
        finally { MarketplaceListingSyncExecutionTestHooks.BeforeWriteRetryAsync = null; resume.TrySetResult(); await RemoveListingConflictTriggerAsync(); }
    }

    [Theory]
    [InlineData(RecoveryScenario.Success, MarketplaceListingSyncStatus.SUCCEEDED, 1, 1, 2)]
    [InlineData(RecoveryScenario.RefreshRejected, MarketplaceListingSyncStatus.FAILED, 0, 1, 1)]
    [InlineData(RecoveryScenario.Second401, MarketplaceListingSyncStatus.FAILED, 0, 1, 2)]
    public async Task Real_401_recovery_is_single_flight_and_bounded(RecoveryScenario scenario, MarketplaceListingSyncStatus expectedStatus, int expectedListings, int expectedRefreshes, int expectedBulkSends)
    {
        var account = await SeedAccountAsync("777"); await PrepareRefreshLineageAsync(account); var handler = new RecoveryHttpHandler(scenario);
        await using var db = Context();
        var execution = CreateRecoveryExecution(db, handler);
        var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
        var completed = await execution.RunAsync(run.Id); completed!.Status.Should().Be(expectedStatus);
        handler.BulkSends.Should().Be(expectedBulkSends); handler.RefreshSends.Should().Be(expectedRefreshes);
        (await db.Set<MarketplaceListing>().CountAsync()).Should().Be(expectedListings);
        (await db.Set<MarketplaceListingObservation>().CountAsync()).Should().Be(expectedListings);
        var item = await db.Set<MarketplaceListingSyncRunItem>().SingleAsync(x => x.SyncRunId == run.Id);
        if (scenario == RecoveryScenario.Success) item.Outcome.Should().Be(MarketplaceListingSyncItemOutcome.FOUND);
        else item.Outcome.Should().NotBe(MarketplaceListingSyncItemOutcome.FOUND);
        if (scenario == RecoveryScenario.Success) (await db.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == account)).ConfirmedCredentialVersion.Should().Be(2);
    }

    [Fact]
    public async Task Provider_5xx_uses_real_adapter_retries_and_marks_runtime_unavailable()
    {
        var account = await SeedAccountAsync("777"); var handler = new RecoveryHttpHandler(RecoveryScenario.Provider5xx);
        await using var db = Context(); var execution = CreateRecoveryExecution(db, handler, refreshEnabled: false);
        var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
        (await execution.RunAsync(run.Id))!.Status.Should().Be(MarketplaceListingSyncStatus.FAILED);
        handler.BulkSends.Should().Be(3); handler.RefreshSends.Should().Be(0);
        var item = await db.Set<MarketplaceListingSyncRunItem>().SingleAsync(x => x.SyncRunId == run.Id); item.Outcome.Should().Be(MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR); item.SafeErrorCode.Should().Be("PROVIDER_UNAVAILABLE");
        var connection = await db.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == account); connection.RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.UNAVAILABLE); connection.LastFailureClassification.Should().Be("TRANSIENT"); connection.RuntimeRetryAfterUntil.Should().BeNull();
    }

    [Fact]
    public async Task Enumeration_hard_ceiling_rejects_the_200001st_identity_before_any_target_is_persisted()
    {
        var account = await SeedAccountAsync();
        var overLimitPage = Enumerable.Range(1, 200_001).Select(x => $"MLB-LIMIT-{x:000000}").ToArray();
        var reader = new ScriptedReader([], _clock);
        reader.Pages.Enqueue(new MarketplaceListingPage(overLimitPage, new Cursor(), false, 0));

        await using var db = Context();
        var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
        var completed = await CreateExecution(db, reader).RunAsync(run.Id);

        completed!.Status.Should().Be(MarketplaceListingSyncStatus.FAILED);
        completed.SafeResultCode.Should().Be("LISTING_VOLUME_EXCEEDS_LIMIT");
        completed.EnumerationCompleted.Should().BeFalse();
        reader.PageCalls.Should().Be(1);
        reader.DetailBatches.Should().BeEmpty();
        (await db.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.SyncRunId == run.Id)).Should().Be(0);
        (await db.Set<MarketplaceListing>().CountAsync(x => x.MarketplaceAccountId == account)).Should().Be(0);
    }

    [Fact]
    public async Task Known_not_enumerated_registration_honors_the_global_200000_target_ceiling_at_the_exact_boundary()
    {
        var account = await SeedAccountAsync(); Guid runId; MarketplaceListingSyncLease lease;
        await using (var db = Context())
        {
            var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow); runId = run.Id;
            lease = (await new MarketplaceListingSyncLeaseAuthority(db).ClaimAsync(runId, _clock.UtcNow, TimeSpan.FromMinutes(2)))!;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO commerce.marketplace_listing_sync_run_item (id,sync_run_id,external_listing_id,origin,outcome,created_at,updated_at,attempt_count,listing_created,observation_appended)
                SELECT gen_random_uuid(),{runId},'SEED-' || n::text,'ENUMERATED','PENDING',{_clock.UtcNow},{_clock.UtcNow},0,false,false
                  FROM generate_series(1,199999) AS n
                """);
            db.Add(new MarketplaceListing(account, "MLB-200000", null, MarketplaceListingStatus.ACTIVE)); await db.SaveChangesAsync();
            (await new MarketplaceListingSyncRunKernel(db).RegisterKnownNotEnumeratedWithLeaseAsync(runId, lease.LeaseToken, _clock.UtcNow, TimeSpan.FromMinutes(2), null)).Should().Be(1);
            (await db.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.SyncRunId == runId)).Should().Be(200000);
            db.Add(new MarketplaceListing(account, "MLB-200001", null, MarketplaceListingStatus.ACTIVE)); await db.SaveChangesAsync();
            var act = () => new MarketplaceListingSyncRunKernel(db).RegisterKnownNotEnumeratedWithLeaseAsync(runId, lease.LeaseToken, _clock.UtcNow, TimeSpan.FromMinutes(2), null);
            await act.Should().ThrowAsync<MarketplaceListingSyncTargetLimitException>();
            (await db.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.SyncRunId == runId)).Should().Be(200000);
            (await db.Set<MarketplaceListingSyncRunItem>().AnyAsync(x => x.SyncRunId == runId && x.ExternalListingId == "MLB-200001")).Should().BeFalse();
        }
    }

    [Fact]
    public async Task Stale_authority_cannot_overwrite_newer_runtime_state_during_terminalization()
    {
        var account = await SeedAccountAsync();
        var retryAfter = _clock.UtcNow.AddMinutes(11);
        const string unavailable = "UNAVAILABLE";
        const string rateLimited = "RATE_LIMITED";
        var replacement = Guid.NewGuid();
        var reader = new ScriptedReader(["MLB-STALE-RUNTIME"], _clock);
        reader.BeforeDetailsAsync = async () =>
        {
            await using var concurrent = Context();
            await concurrent.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,created_at,updated_at,cleanup_attempt_count,version) VALUES ({replacement},'RECONNECT','SECRET_PERSISTED','CONFIRMED','DONE',{account},'MERCADO_LIVRE',{_clock.UtcNow},{_clock.UtcNow},0,1)");
            await concurrent.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account_connection SET confirmed_operation_id={replacement}, runtime_availability={unavailable}, last_failure_classification={rateLimited}, runtime_retry_after_until={retryAfter} WHERE marketplace_account_id={account}");
        };

        await using (var db = Context())
        {
            var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
            var completed = await CreateExecution(db, reader).RunAsync(run.Id);
            completed!.Status.Should().Be(MarketplaceListingSyncStatus.FAILED);
            completed.SafeResultCode.Should().Be("AUTHORIZATION_NOT_USABLE");
        }

        await using (var db = Context())
        {
            var connection = await db.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == account);
            connection.ConfirmedOperationId.Should().Be(replacement);
            connection.RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.UNAVAILABLE);
            connection.LastFailureClassification.Should().Be("RATE_LIMITED");
            connection.RuntimeRetryAfterUntil.Should().Be(retryAfter);
            (await db.Set<MarketplaceListing>().CountAsync(x => x.MarketplaceAccountId == account)).Should().Be(0);
            (await db.Set<MarketplaceListingObservation>().CountAsync()).Should().Be(0);
            var item = await db.Set<MarketplaceListingSyncRunItem>().SingleAsync();
            item.Outcome.Should().Be(MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR);
            item.SafeErrorCode.Should().Be("AUTHORIZATION_NOT_USABLE");
        }
    }

    [Fact]
    public async Task Non_retryable_detail_batch_does_not_prevent_later_batch_from_persisting()
    {
        var account = await SeedAccountAsync(); var ids = Enumerable.Range(1, 21).Select(x => $"MLB-NR-{x:000}").ToArray(); var reader = new ScriptedReader(ids, _clock);
        reader.DetailFailures.Enqueue(new MarketplaceProviderCallException(new MarketplaceProviderFailure(MarketplaceFailureClassification.NON_RETRYABLE, "PROVIDER_REQUEST_REJECTED", MarketplaceSendCertainty.SENT_AND_RESPONSE_RECEIVED)));
        await using var db = Context(); var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
        (await CreateExecution(db, reader).RunAsync(run.Id))!.Status.Should().Be(MarketplaceListingSyncStatus.PARTIAL);
        (await db.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.SyncRunId == run.Id && x.Outcome == MarketplaceListingSyncItemOutcome.PERMANENT_ERROR)).Should().Be(20);
        (await db.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.SyncRunId == run.Id && x.Outcome == MarketplaceListingSyncItemOutcome.FOUND)).Should().Be(1);
        var lastId = ids[ids.Length - 1]; (await db.Set<MarketplaceListing>().CountAsync(x => x.ExternalListingId == lastId)).Should().Be(1);
        (await db.Set<MarketplaceAccountConnection>().SingleAsync(x => x.MarketplaceAccountId == account)).RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.AVAILABLE);
    }

    private async Task PrepareRefreshLineageAsync(Guid account)
    {
        await using var db = Context(); var operation = await db.Set<MarketplaceAccountConnection>().Where(x => x.MarketplaceAccountId == account).Select(x => x.ConfirmedOperationId).SingleAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE commerce.marketplace_account_operation SET candidate_credential_reference='test-ref', candidate_credential_version=1 WHERE id={operation!.Value}");
    }
    private MarketplaceListingSyncExecution CreateRecoveryExecution(VerceDbContext db, RecoveryHttpHandler handler, bool refreshEnabled = true)
    {
        var options = Options.Create(new MercadoLivreOptions { Enabled = true, ClientId = "900000001", RedirectUri = "https://host.example/callback", ResolvedClientSecret = "test-only-client-secret", AccessTokenRefreshMarginSeconds = 0 });
        var sender = new MarketplaceHttpSender(NullLogger<MarketplaceHttpSender>.Instance);
        var connector = new MercadoLivreConnector(new SingleClientFactory(handler), sender, new MarketplaceSafeReadExecutor(sender, new SystemMarketplaceDelay(), new ZeroJitter(), _clock), options, _clock);
        var registry = new RealRegistry(connector); var store = new RotatingSecretStore(); var services = new WorkflowServices(); var gate = new ProviderExecutionGate(); var uow = new SavingUnitOfWork(db);
        var credentials = new MarketplaceCredentialExecutor(uow, registry, store, gate, _clock, services, options);
        if (refreshEnabled) services.Workflow = new MarketplaceAuthorizationWorkflow(uow, registry, store, gate, _clock, credentials);
        return CreateExecution(db, connector, credentialExecutor: credentials, registryOverride: registry);
    }

    private NormalizedListingFacts FactsFor(string id, string sku) => new(id, sku, "Listing " + id, 10m, MarketplaceListingStatus.ACTIVE, "active", 0, "https://example.test/" + id, _clock.UtcNow, []);

    private async Task InstallListingConflictTriggerAsync(int failures)
    {
        if (failures is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(failures));
        await using var db = Context();
        await db.Database.ExecuteSqlRawAsync("CREATE SEQUENCE test_b2_listing_conflict_seq START 1");
#pragma warning disable EF1002 // The value is constrained to the two literal test thresholds above; PostgreSQL DDL cannot parameterize a function body.
        await db.Database.ExecuteSqlRawAsync($"""CREATE FUNCTION test_b2_listing_conflict() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF nextval('test_b2_listing_conflict_seq') <= {failures} THEN RAISE EXCEPTION 'test write conflict' USING ERRCODE = '23505'; END IF; RETURN NEW; END; $$""");
#pragma warning restore EF1002
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER test_b2_listing_conflict_trigger BEFORE INSERT ON commerce.marketplace_listing FOR EACH ROW EXECUTE FUNCTION test_b2_listing_conflict()");
    }
    private async Task RemoveListingConflictTriggerAsync()
    {
        await using var db = Context();
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS test_b2_listing_conflict_trigger ON commerce.marketplace_listing");
        await db.Database.ExecuteSqlRawAsync("DROP FUNCTION IF EXISTS test_b2_listing_conflict()");
        await db.Database.ExecuteSqlRawAsync("DROP SEQUENCE IF EXISTS test_b2_listing_conflict_seq");
    }
    private async Task<StaleSnapshot> SnapshotAsync(VerceDbContext db, Guid account, Guid runId)
    {
        var run = await db.Set<MarketplaceListingSyncRun>().AsNoTracking().SingleAsync(x => x.Id == runId);
        var items = await db.Set<MarketplaceListingSyncRunItem>().AsNoTracking().Where(x => x.SyncRunId == runId).OrderBy(x => x.ExternalListingId).Select(x => new { x.ExternalListingId, x.Origin, x.Outcome, x.SafeErrorCode, x.ProviderErrorCode, x.AttemptCount, x.MarketplaceListingId, x.ListingCreated, x.ObservationAppended, x.CreatedAt, x.UpdatedAt, x.CompletedAt }).ToListAsync();
        var listings = await db.Set<MarketplaceListing>().AsNoTracking().Where(x => x.MarketplaceAccountId == account).OrderBy(x => x.ExternalListingId).Select(x => new { x.ExternalListingId, x.ExternalSku, x.TitleSnapshot, x.ObservedPrice, x.ObservedStatus, x.ProviderNativeStatus, x.ListingUrl, x.LatestObservationFingerprint, x.LinkageState, x.LinkageSource, x.ProductId, x.ChannelOfferId, x.SkuMappingId, x.AutoLinkSuppressed, x.VariationCount, x.SyncState, x.SyncError, x.ProviderObservedAt, x.LastSyncAttemptAt, x.LastSuccessfulSyncAt, x.Version }).ToListAsync();
        var observations = await db.Set<MarketplaceListingObservation>().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.MarketplaceListingId, x.ObservationKey, x.Fingerprint, x.Provenance, x.ProviderObservedAt, x.IngestedAt, x.ExternalSku, x.ProviderNativeStatus, x.TitleSnapshot, x.ObservedStatus, x.ObservedPrice, x.VariationCount }).ToListAsync();
        var connection = await db.Set<MarketplaceAccountConnection>().AsNoTracking().SingleAsync(x => x.MarketplaceAccountId == account);
        return new StaleSnapshot(run.Status, run.Phase, run.LeaseToken, run.LeaseUntil, run.HeartbeatAt, run.StartedAt, run.FinishedAt, run.UpdatedAt, run.AttemptCount, run.Version, run.ConfirmedOperationIdAtStart, run.EnumerationCompleted, run.SafeResultCode, run.LastFailureClassification, run.ProviderErrorCode, run.RetryAfterUntil, items.Select(x => x.ToString()).ToArray(), listings.Select(x => x.ToString()).ToArray(), observations.Select(x => x.ToString()).ToArray(), connection.AuthorizationState, connection.ConfirmedOperationId, connection.ConfirmedCredentialVersion, connection.RuntimeAvailability, connection.LastFailureClassification, connection.SafeFailureCode, connection.RuntimeRetryAfterUntil, connection.LastSuccessAt, connection.LastFailureAt, connection.UpdatedAt);
    }
    private sealed record StaleSnapshot(MarketplaceListingSyncStatus Status, MarketplaceListingSyncPhase? Phase, Guid? LeaseToken, DateTimeOffset? LeaseUntil, DateTimeOffset? HeartbeatAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, DateTimeOffset UpdatedAt, int AttemptCount, long Version, Guid? Operation, bool EnumerationCompleted, string? Result, string? Failure, string? ProviderError, DateTimeOffset? RunRetryAfter, IReadOnlyList<string?> Items, IReadOnlyList<string?> Listings, IReadOnlyList<string?> Observations, MarketplaceAuthorizationState Authorization, Guid? ConfirmedOperation, long? ConfirmedCredential, MarketplaceRuntimeAvailability Runtime, string? RuntimeReason, string? RuntimeSafeCode, DateTimeOffset? RetryAfter, DateTimeOffset? LastSuccess, DateTimeOffset? LastFailure, DateTimeOffset ConnectionUpdatedAt);

    private MercadoLivreConnector CreateRealConnector(Func<HttpRequestMessage, HttpResponseMessage> respond, BoundedLogProvider telemetry)
    {
        var options = Options.Create(new MercadoLivreOptions { Enabled = true, ClientId = "900000001", RedirectUri = "https://host.example/callback", ResolvedClientSecret = "test-only-client-secret" });
        var sender = new MarketplaceHttpSender(new Logger<MarketplaceHttpSender>(new LoggerFactory([telemetry])));
        return new MercadoLivreConnector(new SingleClientFactory(new ScriptedHttpHandler(respond)), sender, new MarketplaceSafeReadExecutor(sender, new SystemMarketplaceDelay(), new ZeroJitter(), _clock), options, _clock);
    }

    private async Task<Guid> SeedProductAsync(bool active)
    {
        await using var db = Context(); var product = new Product("P" + Guid.NewGuid().ToString("N")[..10], "Sync product", null); if (!active) product.Deactivate(); db.Add(product); await db.SaveChangesAsync(); return product.Id;
    }

    private MarketplaceListingSyncExecution CreateExecution(VerceDbContext db, IMarketplaceListingReader reader, int detailBatchSize = 20, ICommerceProductEligibilityReader? productEligibility = null, MarketplaceCredentialExecutor? credentialExecutor = null, IMarketplaceConnectorRegistry? registryOverride = null, MarketplaceListingSyncOptions? settings = null, MarketplaceProviderInFlightLimiter? limiter = null)
    {
        var registry = registryOverride ?? new Registry(reader);
        var credentials = credentialExecutor ?? new MarketplaceCredentialExecutor(new DirectUnitOfWork(db), registry, new SecretStore(), new ProviderExecutionGate(), _clock,
            new ServiceCollection().BuildServiceProvider(), Options.Create(new MercadoLivreOptions { AccessTokenRefreshMarginSeconds = 0 }), limiter);
        return new MarketplaceListingSyncExecution(db, credentials, registry, productEligibility ?? new AllProductsActive(), _clock, Options.Create(settings ?? new MarketplaceListingSyncOptions { DetailBatchSize = detailBatchSize }), NullLogger<MarketplaceListingSyncExecution>.Instance);
    }

    private async Task<Guid> SeedAccountAsync(string? externalAccountId = null)
    {
        var account = Guid.NewGuid(); var channel = Guid.NewGuid(); var operation = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO pricing.sales_channel (id,code,name,kind,active,created_at,version) VALUES (@channel,@code,@code,'Marketplace',true,@now,1);
            INSERT INTO commerce.marketplace_provider (code,name) VALUES ('MERCADO_LIVRE','Mercado Livre') ON CONFLICT DO NOTHING;
            INSERT INTO commerce.marketplace_account (id,provider_code,external_account_id,sales_channel_id,display_name,active,credential_reference,sync_state,created_at,version) VALUES (@account,'MERCADO_LIVRE',@external,@channel,@external,true,'test-ref','NEVER_SYNCED',@now,1);
            INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,candidate_credential_reference,candidate_credential_version,created_at,updated_at,cleanup_attempt_count,version) VALUES (@operation,'REFRESH','SECRET_PERSISTED','CONFIRMED','DONE',@account,'MERCADO_LIVRE','test-ref',1,@now,@now,0,1);
            INSERT INTO commerce.marketplace_account_connection (marketplace_account_id,authorization_state,runtime_availability,confirmed_credential_version,confirmed_operation_id,access_expires_at,created_at,updated_at) VALUES (@account,'CONNECTED','AVAILABLE',1,@operation,@expires,@now,@now);
            INSERT INTO commerce.marketplace_account_capability (marketplace_account_id,capability_code,state,source,updated_at) VALUES (@account,'LISTINGS_READ','GRANTED','TEST',@now);
            """, connection);
        command.Parameters.AddWithValue("channel", channel); command.Parameters.AddWithValue("account", account); command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue("code", "B2" + account.ToString("N")[..8]); command.Parameters.AddWithValue("external", externalAccountId ?? "seller-" + account.ToString("N"));
        command.Parameters.AddWithValue("now", _clock.UtcNow); command.Parameters.AddWithValue("expires", _clock.UtcNow.AddHours(1));
        await command.ExecuteNonQueryAsync(); return account;
    }

    private sealed class DirectUnitOfWork(VerceDbContext db) : IUnitOfWork
    {
        public async Task ExecuteAsync(Func<VerceDbContext, CancellationToken, Task> command, CancellationToken cancellationToken = default) => await command(db, cancellationToken);
        public async Task<T> ExecuteAsync<T>(Func<VerceDbContext, CancellationToken, Task<T>> command, CancellationToken cancellationToken = default) => await command(db, cancellationToken);
    }
    private sealed class SavingUnitOfWork(VerceDbContext db) : IUnitOfWork
    {
        public async Task ExecuteAsync(Func<VerceDbContext, CancellationToken, Task> command, CancellationToken cancellationToken = default) { await command(db, cancellationToken); await db.SaveChangesAsync(cancellationToken); }
        public async Task<T> ExecuteAsync<T>(Func<VerceDbContext, CancellationToken, Task<T>> command, CancellationToken cancellationToken = default) { var result = await command(db, cancellationToken); await db.SaveChangesAsync(cancellationToken); return result; }
    }
    private sealed class SecretStore : IProtectedCredentialStore
    {
        private static readonly ProtectedCredentialStoreResult Ok = new(ProtectedCredentialStoreOutcome.SUCCESS);
        public Task<ProtectedCredentialStoreResult> CreateStagedAsync(ProtectedCredentialWrite write, CancellationToken ct) => Task.FromResult(Ok);
        public Task<ProtectedCredentialStoreResult> BindCandidateAsync(ProtectedCredentialBinding binding, CancellationToken ct) => Task.FromResult(Ok);
        public Task<ProtectedCredentialStoreResult> PromoteCandidateAsync(Guid operationId, string credentialReference, long credentialVersion, CancellationToken ct) => Task.FromResult(Ok);
        public Task<ProtectedCredentialReadResult> UseConfirmedAsync(ProtectedCredentialRead read, CancellationToken ct) => Task.FromResult(new ProtectedCredentialReadResult(ProtectedCredentialStoreOutcome.SUCCESS, """{"v":1,"access_token":"test-only-access-token","refresh_token":"test-only-refresh-token","user_id":"777"}"""u8.ToArray()));
        public Task<ProtectedCredentialStoreResult> CompareAndSwapReplaceAsync(ProtectedCredentialWrite write, long expectedVersion, CancellationToken ct) => Task.FromResult(Ok);
        public Task<ProtectedCredentialStoreResult> DeleteCandidateAsync(Guid operationId, string credentialReference, long credentialVersion, CancellationToken ct) => Task.FromResult(Ok);
    }
    private sealed class Registry(IMarketplaceListingReader reader) : IMarketplaceConnectorRegistry
    {
        private static readonly FakeAuthorizationConnector Connector = new();
        public bool TryGet(string providerCode, out IMarketplaceAuthorizationConnector connector) { connector = Connector; return providerCode == Connector.ProviderCode; }
        public bool TryGetListingReader(string providerCode, out IMarketplaceListingReader listingReader) { listingReader = reader; return providerCode == reader.ProviderCode; }
    }
    private sealed class RealRegistry(MercadoLivreConnector connector) : IMarketplaceConnectorRegistry
    {
        public bool TryGet(string providerCode, out IMarketplaceAuthorizationConnector authorization) { authorization = connector; return providerCode == connector.ProviderCode; }
        public bool TryGetListingReader(string providerCode, out IMarketplaceListingReader listingReader) { listingReader = connector; return providerCode == connector.ProviderCode; }
    }
    public enum RecoveryScenario { Success, RefreshRejected, Second401, Provider5xx }
    private sealed class RecoveryHttpHandler(RecoveryScenario scenario) : HttpMessageHandler
    {
        public int BulkSends { get; private set; }
        public int RefreshSends { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/items/search")) return Task.FromResult(Json(HttpStatusCode.OK, """{"results":["MLB123"],"scroll_id":null}"""));
            if (path == "/oauth/token")
            {
                RefreshSends++;
                return Task.FromResult(scenario == RecoveryScenario.RefreshRejected
                    ? Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")
                    : Json(HttpStatusCode.OK, """{"access_token":"renewed-access","token_type":"bearer","expires_in":3600,"user_id":777,"refresh_token":"renewed-refresh"}"""));
            }
            if (path == "/items/bulk")
            {
                BulkSends++;
                if (scenario == RecoveryScenario.Provider5xx) return Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, "{}"));
                if (BulkSends == 1 || scenario == RecoveryScenario.Second401) return Task.FromResult(Json(HttpStatusCode.Unauthorized, "{}"));
                return Task.FromResult(Json(HttpStatusCode.OK, """[{"code":200,"body":{"id":"MLB123","seller_id":777,"site_id":"MLB","status":"active","price":10,"currency_id":"BRL"}}]"""));
            }
            throw new InvalidOperationException("unexpected controlled request " + path);
        }
        private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
    private sealed class WorkflowServices : IServiceProvider
    {
        public MarketplaceAuthorizationWorkflow? Workflow { get; set; }
        public object? GetService(Type serviceType) => serviceType == typeof(MarketplaceAuthorizationWorkflow) ? Workflow : null;
    }
    private sealed class RotatingSecretStore : IProtectedCredentialStore
    {
        private byte[] _material = """{"v":1,"access_token":"initial-access","refresh_token":"initial-refresh","user_id":"777"}"""u8.ToArray();
        private long _version = 1; private Guid _operation;
        public Task<ProtectedCredentialStoreResult> CreateStagedAsync(ProtectedCredentialWrite write, CancellationToken ct) => Task.FromResult(new ProtectedCredentialStoreResult(ProtectedCredentialStoreOutcome.SUCCESS));
        public Task<ProtectedCredentialStoreResult> BindCandidateAsync(ProtectedCredentialBinding binding, CancellationToken ct) => Task.FromResult(new ProtectedCredentialStoreResult(ProtectedCredentialStoreOutcome.SUCCESS));
        public Task<ProtectedCredentialStoreResult> PromoteCandidateAsync(Guid operationId, string credentialReference, long credentialVersion, CancellationToken ct) => Task.FromResult(new ProtectedCredentialStoreResult(ProtectedCredentialStoreOutcome.SUCCESS));
        public Task<ProtectedCredentialReadResult> UseConfirmedAsync(ProtectedCredentialRead read, CancellationToken ct) => Task.FromResult(new ProtectedCredentialReadResult(read.CredentialVersion == _version ? ProtectedCredentialStoreOutcome.SUCCESS : ProtectedCredentialStoreOutcome.VERSION_CONFLICT, _material.ToArray()));
        public Task<ProtectedCredentialStoreResult> CompareAndSwapReplaceAsync(ProtectedCredentialWrite write, long expectedVersion, CancellationToken ct)
        {
            if (expectedVersion != _version) return Task.FromResult(new ProtectedCredentialStoreResult(ProtectedCredentialStoreOutcome.VERSION_CONFLICT));
            _version++; _operation = write.OperationId; _material = write.SecretMaterial.ToArray();
            return Task.FromResult(new ProtectedCredentialStoreResult(ProtectedCredentialStoreOutcome.SUCCESS, new ProtectedCredentialReceipt(_operation, write.CredentialReference, _version, "test-receipt", DateTimeOffset.UtcNow)));
        }
        public Task<ProtectedCredentialStoreResult> DeleteCandidateAsync(Guid operationId, string credentialReference, long credentialVersion, CancellationToken ct) => Task.FromResult(new ProtectedCredentialStoreResult(ProtectedCredentialStoreOutcome.SUCCESS));
    }
    private sealed class FakeAuthorizationConnector : IMarketplaceAuthorizationConnector
    {
        public string ProviderCode => "MERCADO_LIVRE";
        public Task<MarketplaceAuthorizationStart> BeginAsync(MarketplaceAuthorizationBeginRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<MarketplaceAuthorizationCompletion> CompleteOnceAsync(MarketplaceAuthorizationCompletionRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<MarketplaceRefreshCompletion> RefreshAsync(MarketplaceRefreshRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<MarketplaceAccountInspection> InspectAsync(MarketplaceAccountInspectionRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class AllProductsActive : ICommerceProductEligibilityReader
    {
        public Task<IReadOnlyDictionary<Guid, bool>> GetActiveAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<Guid, bool>>(ids.ToDictionary(x => x, _ => true));
    }
    private sealed class Cursor : MarketplaceListingCursor { }
    private sealed class ScriptedReader(IReadOnlyList<string> ids, TestClock clock) : IMarketplaceListingReader
    {
        private int _page;
        public string ProviderCode => "MERCADO_LIVRE";
        public int PageCalls { get; private set; }
        public Queue<MarketplaceListingPage> Pages { get; } = [];
        public Queue<Exception> PageFailures { get; } = [];
        public Queue<Exception> DetailFailures { get; } = [];
        public Dictionary<string, NormalizedListingFacts> Facts { get; } = new(StringComparer.Ordinal);
        public List<IReadOnlyList<string>> DetailBatches { get; } = [];
        public Dictionary<string, MarketplaceListingReadOutcome> Outcomes { get; } = new(StringComparer.Ordinal);
        public Func<Task>? BeforeDetailsAsync { get; set; }
        public Task<MarketplaceListingPage> ReadListingPageAsync(MarketplaceListingPageRequest request, CancellationToken ct)
        {
            _page++; PageCalls++;
            if (PageFailures.Count != 0) return Task.FromException<MarketplaceListingPage>(PageFailures.Dequeue());
            if (Pages.Count != 0) return Task.FromResult(Pages.Dequeue());
            if (ids.Count == 0) return Task.FromResult(new MarketplaceListingPage([], null, true, 0));
            return Task.FromResult(_page % 2 == 1
                ? new MarketplaceListingPage(ids.Take(12).Append(ids[0]).ToArray(), new Cursor(), false, 0)
                : new MarketplaceListingPage(ids.Skip(12).ToArray(), null, true, 0));
        }
        public async Task<IReadOnlyList<MarketplaceListingDetailResult>> ReadListingDetailsAsync(MarketplaceListingDetailRequest request, CancellationToken ct)
        {
            DetailBatches.Add(request.ExternalListingIds.ToArray());
            if (DetailFailures.Count != 0) throw DetailFailures.Dequeue();
            if (BeforeDetailsAsync is { } before) { BeforeDetailsAsync = null; await before(); }
            return request.ExternalListingIds.Select(id =>
            {
                var outcome = Outcomes.TryGetValue(id, out var configured) ? configured : MarketplaceListingReadOutcome.FOUND;
                return outcome == MarketplaceListingReadOutcome.FOUND
                    ? new MarketplaceListingDetailResult(id, outcome, Facts.TryGetValue(id, out var facts) ? facts : new NormalizedListingFacts(id, "SKU-" + id, "Listing " + id, 10m, MarketplaceListingStatus.ACTIVE, "active", 0, "https://example.test/" + id, clock.UtcNow, []), null)
                    : new MarketplaceListingDetailResult(id, outcome, null, null);
            }).ToArray();
        }
    }

    private sealed class ConnectorDetailReader(ScriptedReader pages, MercadoLivreConnector connector) : IMarketplaceListingReader
    {
        public string ProviderCode => connector.ProviderCode;
        public Task<MarketplaceListingPage> ReadListingPageAsync(MarketplaceListingPageRequest request, CancellationToken ct) => pages.ReadListingPageAsync(request, ct);
        public Task<IReadOnlyList<MarketplaceListingDetailResult>> ReadListingDetailsAsync(MarketplaceListingDetailRequest request, CancellationToken ct) => connector.ReadListingDetailsAsync(request, ct);
    }

    private sealed class ScriptedHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler);
        public HttpClient CreateClient(string name) => _client;
    }
    private sealed class ZeroJitter : IMarketplaceJitter { public TimeSpan Next() => TimeSpan.Zero; }
    private sealed class BoundedLogProvider : ILoggerProvider
    {
        public List<string> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Capture(Entries);
        public void Dispose() { }
        private sealed class Capture(List<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => entries.Add(formatter(state, exception));
        }
    }
}
