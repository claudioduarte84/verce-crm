using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Verce.Api.Commerce;
using Verce.Infrastructure.Marketplaces;
using Verce.Modules.Commerce;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.S8C2;

public static class ExecutionHooksCollection { public const string Name = "S8C2 execution test hooks (process-wide statics)"; }

/// <summary>
/// M063 — ADR-0029 on a real PostgreSQL: PR-5 (exact prices persist exactly, inexact ones persist as unknown) and S09-1..S09-8 (a whole-call NON_RETRYABLE failure during enumeration stops
/// the run as LISTING_ENUMERATION_INCOMPLETE with the runtime untouched). Same hermetic harness as the B2 suite: disposable Testcontainers database, real connector over a scripted wire.
/// </summary>
// Serialized with every other class that really EXECUTES listing-sync runs in this process: MarketplaceListingSyncExecutionTestHooks are process-wide statics that the first
// execution to reach them consumes, so two such classes running in parallel would steal each other's hooks (observed: RACE_2 vs the D4 HTTP host test).
[Collection(ExecutionHooksCollection.Name)]
public sealed partial class MarketplaceListingSyncExecutionIntegrationTests
{
    // ======================================================================== PR-5 — price exactness end to end

    [Fact]
    public async Task M063_PR_5_exact_prices_persist_exactly_and_inexact_prices_persist_as_unknown_while_the_run_and_the_runtime_stay_healthy()
    {
        var account = await SeedAccountAsync("777");
        var prices = new Dictionary<string, string>
        {
            ["MLB9600000001"] = "1.00000000000000000000000000001",          // Gate 061 F061-01: used to be persisted as 1.00
            ["MLB9600000002"] = "9999999999999999.9900000000000001",        // Gate 061 F061-01: used to be persisted as the ceiling
            ["MLB9600000003"] = "9999999999999999.99",
            ["MLB9600000004"] = "19.90",
            ["MLB9600000005"] = "10.100",
            ["MLB9600000006"] = "1E-400",
            ["MLB9600000007"] = "79228162514264337593543950335",
            ["MLB9600000008"] = "1E+0000000002",
            ["MLB9600000009"] = "-0",
        };
        var expected = new Dictionary<string, decimal?>
        {
            ["MLB9600000001"] = null, ["MLB9600000002"] = null, ["MLB9600000003"] = 9999999999999999.99m, ["MLB9600000004"] = 19.90m, ["MLB9600000005"] = 10.10m,
            ["MLB9600000006"] = null, ["MLB9600000007"] = null, ["MLB9600000008"] = 100.00m, ["MLB9600000009"] = null,
        };
        await using var boot = NewBoot(prices.Keys.ToArray(), p => p.Element = id => ElementJson(id, 200, b => b["price"] = JsonNode.Parse(prices[id])));
        var runId = (await boot.Service.StartFullAsync(account, Guid.NewGuid())).RunId!.Value;
        var run = (await boot.Execution.RunAsync(runId))!;

        run.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED); run.SafeResultCode.Should().Be("LISTING_SYNC_COMPLETED");
        boot.Wire.RequestsPerId.Values.Should().OnlyContain(n => n == 1, "an unusable price is a field result, never a failed element that would be re-requested");
        await using var db = Context();
        var listings = await db.Set<MarketplaceListing>().AsNoTracking().Include(x => x.Observations).Where(x => x.MarketplaceAccountId == account).ToDictionaryAsync(x => x.ExternalListingId);
        listings.Should().HaveCount(9);
        foreach (var (id, price) in expected)
        {
            listings[id].ObservedPrice.Should().Be(price, id);
            listings[id].Observations.Should().ContainSingle().Which.ObservedPrice.Should().Be(price, id);
            listings[id].SyncState.Should().Be(MarketplaceSyncState.SYNCED);
        }
        (await db.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.SyncRunId == runId && x.Outcome == MarketplaceListingSyncItemOutcome.FOUND)).Should().Be(9);
        (await db.Set<MarketplaceAccountConnection>().AsNoTracking().SingleAsync(x => x.MarketplaceAccountId == account)).RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.AVAILABLE);
        // the persisted numeric(18,2) has exactly two decimals for every kept price
        (await JsonAsync("SELECT string_agg(observed_price::text, ',' ORDER BY external_listing_id) FROM commerce.marketplace_listing WHERE marketplace_account_id = @a", ("a", account)))
            .Should().Be("9999999999999999.99,19.90,10.10,100.00", "NULL prices are omitted by string_agg; kept prices keep numeric(18,2)'s two decimals");
    }

    // ======================================================================== S09 — enumeration NON_RETRYABLE (ADR-0029 §2/§3)

    /// <summary>A scripted provider with the real connector: <paramref name="scan"/> answers every /items/search call (1-based call number, scroll id or null); /items/bulk answers healthy elements.</summary>
    private sealed class EnumerationWire(Func<int, string?, HttpResponseMessage> scan)
    {
        public int Scans { get; private set; }
        public BulkProvider Bulk { get; } = new();
        public HttpResponseMessage Handle(HttpRequestMessage request)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/items/search", StringComparison.Ordinal))
            {
                Scans++;
                var scroll = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["scroll_id"];
                return scan(Scans, scroll);
            }
            return Bulk.Handle(request);
        }
    }

    private static HttpResponseMessage Page(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage FirstPageFailure(string variant) => variant switch
    {
        "malformed200" => Page("{not json"),
        "missingResults200" => Page("{}"),
        "arrayRoot200" => Page("[]"),
        "status400" => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"message\":\"x\"}", Encoding.UTF8, "application/json") },
        "oversized200" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1024 * 1024 + 1]) },
        _ => throw new ArgumentOutOfRangeException(nameof(variant)),
    };

    private async Task<(Guid RunId, MarketplaceListingSyncRun Run)> RunWithWireAsync(Guid account, EnumerationWire wire, VerceDbContext db)
    {
        var connector = CreateRealConnector(wire.Handle, new BoundedLogProvider());
        var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
        return (run.Id, (await CreateExecution(db, connector).RunAsync(run.Id))!);
    }

    [Theory]
    [InlineData("malformed200")]
    [InlineData("missingResults200")]
    [InlineData("arrayRoot200")]
    [InlineData("status400")]
    [InlineData("oversized200")]
    public async Task M063_S09_1_to_6_a_NON_RETRYABLE_first_page_stops_the_run_as_ENUMERATION_INCOMPLETE_without_touching_the_runtime_and_a_new_start_is_allowed(string variant)
    {
        var account = await SeedAccountAsync("777");
        var connectionBefore = await ConnectionStateAsync(account);
        var wire = new EnumerationWire((_, _) => FirstPageFailure(variant));
        await using var db = Context();
        var (runId, run) = await RunWithWireAsync(account, wire, db);

        run.Status.Should().Be(MarketplaceListingSyncStatus.FAILED); run.SafeResultCode.Should().Be("LISTING_ENUMERATION_INCOMPLETE"); run.EnumerationCompleted.Should().BeFalse();
        wire.Scans.Should().Be(1, "a permanent failure is never re-requested"); wire.Bulk.BulkRequests.Should().BeEmpty("no detail read after a failed enumeration");
        (await db.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.SyncRunId == runId)).Should().Be(0); (await db.Set<MarketplaceListing>().CountAsync()).Should().Be(0);
        (await ConnectionStateAsync(account)).Should().Be(connectionBefore, "ADR-0029 §3: this stop leaves the runtime availability (and every other connection field) untouched");

        // S09-7: no runtime block, nothing for a person to do, and the next start is admitted at once
        var service = CreateService(db, reader: CreateRealConnector(wire.Handle, new BoundedLogProvider()));
        var status = await service.GetStatusAsync(account);
        status.Availability.CanStart.Should().BeTrue(); status.Availability.BlockedReason.Should().BeNull();
        status.Summary.Health.Should().Be(MarketplaceListingSyncHealth.FAILED);
        var detail = await service.GetRunAsync(runId);
        (await new ListingSyncRequiredActionResolver(db, NullLogger.Instance).ForRunAsync(detail.Run, CancellationToken.None)).Should().Be("NONE", "ADR-0027 §4: LISTING_ENUMERATION_INCOMPLETE ⇒ NONE");
        (await service.StartFullAsync(account, Guid.NewGuid())).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task M063_S09_5_a_malformed_second_page_after_a_valid_first_page_turns_the_first_page_items_into_TRANSIENT_ERROR_with_the_stop_code_and_reconciles_nothing()
    {
        var account = await SeedAccountAsync("777");
        const string known = "MLB9710000099"; var ids = new[] { "MLB9710000001", "MLB9710000002", "MLB9710000003" };
        await using (var seed = Context()) { seed.Add(new MarketplaceListing(account, known, "SKU-K", MarketplaceListingStatus.ACTIVE, "Known title", 7.00m)); await seed.SaveChangesAsync(); }
        var listingsBefore = await JsonAsync("SELECT coalesce(jsonb_agg(to_jsonb(l) ORDER BY l.external_listing_id), '[]'::jsonb)::text FROM commerce.marketplace_listing l WHERE marketplace_account_id = @a", ("a", account));
        var connectionBefore = await ConnectionStateAsync(account);
        var wire = new EnumerationWire((n, _) => n == 1 ? Page("{\"results\":[" + string.Join(",", ids.Select(i => "\"" + i + "\"")) + "],\"scroll_id\":\"S1\"}") : Page("{not json"));
        await using var db = Context();
        var (runId, run) = await RunWithWireAsync(account, wire, db);

        run.Status.Should().Be(MarketplaceListingSyncStatus.FAILED); run.SafeResultCode.Should().Be("LISTING_ENUMERATION_INCOMPLETE"); run.EnumerationCompleted.Should().BeFalse();
        wire.Scans.Should().Be(2); wire.Bulk.BulkRequests.Should().BeEmpty();
        var items = await db.Set<MarketplaceListingSyncRunItem>().AsNoTracking().Where(x => x.SyncRunId == runId).ToListAsync();
        items.Should().HaveCount(3, "the identities already observed stay traceable").And.OnlyContain(x => x.Origin == MarketplaceListingSyncItemOrigin.ENUMERATED && x.Outcome == MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR && x.SafeErrorCode == "LISTING_ENUMERATION_INCOMPLETE");
        items.Select(x => x.ExternalListingId).Should().BeEquivalentTo(ids); items.Should().NotContain(x => x.Origin == MarketplaceListingSyncItemOrigin.KNOWN_NOT_ENUMERATED && x.ExternalListingId == known, "no reconciliation after an incomplete enumeration");
        (await JsonAsync("SELECT coalesce(jsonb_agg(to_jsonb(l) ORDER BY l.external_listing_id), '[]'::jsonb)::text FROM commerce.marketplace_listing l WHERE marketplace_account_id = @a", ("a", account))).Should().Be(listingsBefore, "no listing changes");
        (await ConnectionStateAsync(account)).Should().Be(connectionBefore);

        var service = CreateService(db, reader: CreateRealConnector(wire.Handle, new BoundedLogProvider()));
        (await service.GetStatusAsync(account)).Availability.CanStart.Should().BeTrue();
        var retry = await service.RetryAsync(runId, Guid.NewGuid());
        retry.Succeeded.Should().BeTrue("the transient items stay eligible for FAILED_ONLY");
        (await service.GetItemsAsync(retry.RunId!.Value, null, 1, 100)).Items.Select(x => x.ExternalListingId).Should().BeEquivalentTo(ids);
    }

    [Fact]
    public async Task M063_S09_7_the_stop_code_is_in_the_frozen_run_catalog_and_no_new_code_exists()
    {
        ListingSyncRequiredActionRules.RunCodes.Should().ContainKey("LISTING_ENUMERATION_INCOMPLETE");
        ListingSyncRequiredActionRules.RunCodes["LISTING_ENUMERATION_INCOMPLETE"].Should().Be(new ListingSyncRequiredActionRules.RunCodeRule(ListingSyncRequiredActionRules.RunCodeKind.Fixed, MarketplaceRequiredActions.None));
        ListingSyncRequiredActionRules.RunCodes.Keys.Should().OnlyContain(k => !k.Contains("RESPONSE_INVALID") && !k.Contains("ENUMERATION_REJECTED"), "ADR-0029 creates no public code");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task M063_S09_8_every_other_stop_and_the_detail_chunk_rule_keep_their_frozen_mapping()
    {
        async Task<(MarketplaceListingSyncRun Run, MarketplaceAccountConnection Connection)> RunAsync(EnumerationWire wire)
        {
            var account = await SeedAccountAsync(); await using var db = Context();
            var (_, run) = await RunWithWireAsync(account, wire, db);
            return (run, await db.Set<MarketplaceAccountConnection>().AsNoTracking().SingleAsync(x => x.MarketplaceAccountId == account));
        }

        var unavailable = await RunAsync(new EnumerationWire((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }));
        unavailable.Run.SafeResultCode.Should().Be("PROVIDER_UNAVAILABLE"); unavailable.Connection.RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.UNAVAILABLE);

        var limited = await RunAsync(new EnumerationWire((_, _) => { var r = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{}") }; r.Headers.TryAddWithoutValidation("Retry-After", "3600"); return r; }));
        limited.Run.SafeResultCode.Should().Be("PROVIDER_RATE_LIMITED"); limited.Connection.RuntimeRetryAfterUntil.Should().Be(_clock.UtcNow.AddSeconds(3600)); limited.Connection.RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.UNAVAILABLE);

        var forbidden = await RunAsync(new EnumerationWire((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") }));
        forbidden.Run.SafeResultCode.Should().Be("PROVIDER_ACCESS_FORBIDDEN"); forbidden.Connection.RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.AVAILABLE, "guard/credential-class stops do not touch the runtime");

        // a continuation answered 404 three times: cursor invalid, restarted twice, then INCOMPLETE (unchanged rule; the family ADR-0029 reuses)
        var cursorWire = new EnumerationWire((_, scroll) => scroll is null ? Page("{\"results\":[\"MLB9720000001\"],\"scroll_id\":\"S1\"}") : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        var cursor = await RunAsync(cursorWire);
        cursor.Run.Status.Should().Be(MarketplaceListingSyncStatus.FAILED); cursor.Run.SafeResultCode.Should().Be("LISTING_ENUMERATION_INCOMPLETE"); cursorWire.Scans.Should().Be(6, "three scans from the first page, each followed by a rejected continuation");
        cursor.Connection.RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.AVAILABLE);

        // a whole detail chunk that is NON_RETRYABLE is still PERMANENT_ERROR / LISTING_READ_REJECTED and does not stop the run (ADR-0025 §E.3)
        var detailWire = new EnumerationWire((_, _) => Page("{\"results\":[\"MLB9730000001\",\"MLB9730000002\"]}"));
        detailWire.Bulk.WholeResponse = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") };
        var account2 = await SeedAccountAsync(); await using (var db = Context())
        {
            var (runId, run) = await RunWithWireAsync(account2, detailWire, db);
            run.SafeResultCode.Should().NotBe("LISTING_ENUMERATION_INCOMPLETE").And.NotBe("PROVIDER_UNAVAILABLE");
            (await db.Set<MarketplaceListingSyncRunItem>().Where(x => x.SyncRunId == runId).ToListAsync()).Should().OnlyContain(x => x.Outcome == MarketplaceListingSyncItemOutcome.PERMANENT_ERROR && x.SafeErrorCode == "LISTING_READ_REJECTED");
        }
    }
}
