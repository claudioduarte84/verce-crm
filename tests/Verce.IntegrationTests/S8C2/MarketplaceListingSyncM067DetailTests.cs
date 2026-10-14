using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Verce.Infrastructure.Marketplaces;
using Verce.Infrastructure.Marketplaces.MercadoLivre;
using Verce.Modules.Commerce;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.S8C2;

/// <summary>
/// M067 / DET-1, DET-2 (ADR-0030 §2): stored credential material that the adapter cannot use is a problem of the CREDENTIAL, never of the listings. Over a disposable PostgreSQL and the real
/// connector + credential executor + execution + run kernel (lease fencing intact): the run stops as <c>CREDENTIAL_CONFIRMED_LOST</c>, the items that were never read stay retryable
/// (<c>TRANSIENT_ERROR</c>, never <c>PERMANENT_ERROR/LISTING_READ_REJECTED</c>), no request reaches the provider, the connection is untouched, and a later FAILED_ONLY retry with usable
/// material completes the work. Part of the partial class that already sits in the static-hooks collection.
/// </summary>
public sealed partial class MarketplaceListingSyncExecutionIntegrationTests
{
    private static readonly byte[] M067Valid = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"m067-access-token","refresh_token":"m067-refresh-token","user_id":"777"}""");
    private static readonly byte[] M067Unusable = Encoding.UTF8.GetBytes("{not-json CANARY-M067-DET");
    private static readonly byte[] M067Surrogate = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"CANARY-M067-DET\ud800","refresh_token":"r","user_id":"777"}""");

    /// <summary>A store that always answers SUCCESS (authentic, version-bound) with the material chosen for the n-th read (1-based).</summary>
    private sealed class MaterialStore(Func<int, byte[]> material) : IProtectedCredentialStore
    {
        private static readonly ProtectedCredentialStoreResult Ok = new(ProtectedCredentialStoreOutcome.SUCCESS);
        public int Reads { get; private set; }
        public Task<ProtectedCredentialStoreResult> CreateStagedAsync(ProtectedCredentialWrite write, CancellationToken ct) => Task.FromResult(Ok);
        public Task<ProtectedCredentialStoreResult> BindCandidateAsync(ProtectedCredentialBinding binding, CancellationToken ct) => Task.FromResult(Ok);
        public Task<ProtectedCredentialStoreResult> PromoteCandidateAsync(Guid operationId, string credentialReference, long credentialVersion, CancellationToken ct) => Task.FromResult(Ok);
        public Task<ProtectedCredentialReadResult> UseConfirmedAsync(ProtectedCredentialRead read, CancellationToken ct) => Task.FromResult(new ProtectedCredentialReadResult(ProtectedCredentialStoreOutcome.SUCCESS, material(++Reads)));
        public Task<ProtectedCredentialStoreResult> CompareAndSwapReplaceAsync(ProtectedCredentialWrite write, long expectedVersion, CancellationToken ct) => Task.FromResult(Ok);
        public Task<ProtectedCredentialStoreResult> DeleteCandidateAsync(Guid operationId, string credentialReference, long credentialVersion, CancellationToken ct) => Task.FromResult(Ok);
    }

    private MarketplaceListingSyncExecution CreateM067Execution(VerceDbContext db, MaterialStore store, Func<HttpRequestMessage, HttpResponseMessage> wire)
    {
        var options = Options.Create(new MercadoLivreOptions { Enabled = true, ClientId = "900000001", RedirectUri = "https://host.example/callback", ResolvedClientSecret = "test-only-client-secret", AccessTokenRefreshMarginSeconds = 0 });
        var sender = new MarketplaceHttpSender(NullLogger<MarketplaceHttpSender>.Instance);
        var connector = new MercadoLivreConnector(new SingleClientFactory(new ScriptedHttpHandler(wire)), sender, new MarketplaceSafeReadExecutor(sender, new SystemMarketplaceDelay(), new ZeroJitter(), _clock), options, _clock);
        var registry = new RealRegistry(connector);
        var credentials = new MarketplaceCredentialExecutor(new DirectUnitOfWork(db), registry, store, new ProviderExecutionGate(), _clock, new WorkflowServices(), options);
        return CreateExecution(db, connector, credentialExecutor: credentials, registryOverride: registry);
    }

    private async Task<string> M067RowJsonAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); command.Parameters.AddWithValue("id", id);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
    }

    private Task<string> M067ConnectionAsync(Guid account) => M067RowJsonAsync("SELECT to_jsonb(c)::text FROM commerce.marketplace_account_connection c WHERE marketplace_account_id = @id", account);
    private Task<string> M067ListingsAsync(Guid account) => M067RowJsonAsync("SELECT coalesce(jsonb_agg(to_jsonb(l) ORDER BY l.external_listing_id), '[]'::jsonb)::text FROM commerce.marketplace_listing l WHERE marketplace_account_id = @id", account);
    private Task<string> M067ItemsAsync(Guid run) => M067RowJsonAsync("SELECT coalesce(jsonb_agg(to_jsonb(i) ORDER BY i.external_listing_id), '[]'::jsonb)::text FROM commerce.marketplace_listing_sync_run_item i WHERE sync_run_id = @id", run);

    private async Task<(string Outcome, string? Code)[]> M067ItemOutcomesAsync(Guid run)
    {
        await using var db = Context();
        var items = await db.Set<MarketplaceListingSyncRunItem>().AsNoTracking().Where(x => x.SyncRunId == run).OrderBy(x => x.ExternalListingId).Select(x => new { x.Outcome, x.SafeErrorCode }).ToListAsync();
        return items.Select(x => (x.Outcome.ToString(), x.SafeErrorCode)).ToArray();
    }

    [Theory]
    [InlineData("unreadable JSON")]
    [InlineData("lone surrogate in a field")]
    public async Task DET_1_a_FAILED_ONLY_run_with_unusable_stored_material_stops_as_CREDENTIAL_CONFIRMED_LOST_and_never_blames_the_listings(string which)
    {
        var material = which == "unreadable JSON" ? M067Unusable : M067Surrogate;
        var account = await SeedAccountAsync("777");
        var ids = Enumerable.Range(1, 6).Select(x => $"MLB-D1-{x:000}").ToArray();
        var reader = new ScriptedReader(ids, _clock); reader.Outcomes[ids[1]] = MarketplaceListingReadOutcome.FAILED; reader.Outcomes[ids[4]] = MarketplaceListingReadOutcome.FAILED;

        Guid source, retry; string connectionBefore, listingsBefore, sourceItemsBefore;
        await using (var db = Context())
        {
            source = (await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow)).Id;
            (await CreateExecution(db, reader).RunAsync(source))!.Status.Should().Be(MarketplaceListingSyncStatus.PARTIAL);
            retry = (await new MarketplaceListingSyncRunKernel(db).CreateFailedOnlyRetryAsync(source, Guid.NewGuid(), _clock.UtcNow.AddMinutes(1))).Id;
        }
        connectionBefore = await M067ConnectionAsync(account); listingsBefore = await M067ListingsAsync(account); sourceItemsBefore = await M067ItemsAsync(source);
        (await M067ItemOutcomesAsync(retry)).Should().HaveCount(2).And.OnlyContain(x => x.Outcome == "PENDING");

        var requests = 0; var store = new MaterialStore(_ => material);
        await using (var db = Context())
        {
            var finished = await CreateM067Execution(db, store, _ => { Interlocked.Increment(ref requests); return new HttpResponseMessage(HttpStatusCode.OK); }).RunAsync(retry);
            finished!.Status.Should().Be(MarketplaceListingSyncStatus.FAILED);
            finished.SafeResultCode.Should().Be("CREDENTIAL_CONFIRMED_LOST", "ADR-0030 §2: the confirmed version is lost for good, not a provider or enumeration failure");
            finished.LeaseToken.Should().BeNull("the finish went through the fenced lease path and released it");
            finished.AttemptCount.Should().Be(1);
        }
        store.Reads.Should().Be(1, "the credential was read once, refused, and never retried");
        requests.Should().Be(0, "zero provider requests");
        var outcomes = await M067ItemOutcomesAsync(retry);
        outcomes.Should().OnlyContain(x => x.Outcome == "TRANSIENT_ERROR" && x.Code == "CREDENTIAL_CONFIRMED_LOST", "the items were never read: they stay retryable, carrying the stop code");
        outcomes.Should().NotContain(x => x.Outcome == "PERMANENT_ERROR" || x.Code == "LISTING_READ_REJECTED");
        (await M067ConnectionAsync(account)).Should().Be(connectionBefore, "a run never changes the connection or the runtime (ADR-0026/0029 §3)");
        (await M067ListingsAsync(account)).Should().Be(listingsBefore); (await M067ItemsAsync(source)).Should().Be(sourceItemsBefore, "the source run is untouched");
        (await M067ItemsAsync(retry) + await M067ConnectionAsync(account)).Should().NotContain("CANARY-M067");

        // recovery: with usable material the same items are retried and read
        Guid again;
        await using (var db = Context())
        {
            again = (await new MarketplaceListingSyncRunKernel(db).CreateFailedOnlyRetryAsync(retry, Guid.NewGuid(), _clock.UtcNow.AddMinutes(2))).Id;
            (await CreateExecution(db, new ScriptedReader(ids, _clock)).RunAsync(again))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        }
        (await M067ItemOutcomesAsync(again)).Should().OnlyContain(x => x.Outcome == "FOUND");
    }

    [Fact]
    public async Task DET_2_usable_material_for_the_enumeration_then_unusable_for_the_detail_leaves_the_enumerated_items_retryable_and_the_scan_alone_in_the_wire()
    {
        var account = await SeedAccountAsync("777");
        var scans = 0; var others = new List<string>();
        var store = new MaterialStore(n => n == 1 ? M067Valid : M067Unusable);    // read 1 = the scan page, read 2 = the first detail chunk
        Guid run; string connectionBefore = await M067ConnectionAsync(account);
        await using (var db = Context())
        {
            run = (await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow)).Id;
            var finished = await CreateM067Execution(db, store, request =>
            {
                if (request.RequestUri!.AbsolutePath == "/users/777/items/search") { Interlocked.Increment(ref scans); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"results":["MLB1001","MLB1002","MLB1003"]}""", Encoding.UTF8, "application/json") }; }
                lock (others) others.Add(request.RequestUri.PathAndQuery); return new HttpResponseMessage(HttpStatusCode.NotFound);
            }).RunAsync(run);
            finished!.Status.Should().Be(MarketplaceListingSyncStatus.FAILED);
            finished.SafeResultCode.Should().Be("CREDENTIAL_CONFIRMED_LOST");
            finished.EnumerationCompleted.Should().BeTrue("the single scan page ended the enumeration before the credential turned out unusable");
        }
        store.Reads.Should().Be(2); scans.Should().Be(1); others.Should().BeEmpty("no bulk request, no further call of any kind");
        var outcomes = await M067ItemOutcomesAsync(run);
        outcomes.Should().HaveCount(3).And.OnlyContain(x => x.Outcome == "TRANSIENT_ERROR" && x.Code == "CREDENTIAL_CONFIRMED_LOST");
        (await M067ListingsAsync(account)).Should().Be("[]", "nothing was read, so no listing exists");
        (await M067ConnectionAsync(account)).Should().Be(connectionBefore);

        Guid retry;
        await using (var db = Context())
        {
            retry = (await new MarketplaceListingSyncRunKernel(db).CreateFailedOnlyRetryAsync(run, Guid.NewGuid(), _clock.UtcNow.AddMinutes(1))).Id;
            (await CreateExecution(db, new ScriptedReader(["MLB1001", "MLB1002", "MLB1003"], _clock)).RunAsync(retry))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
        }
        (await M067ItemOutcomesAsync(retry)).Should().HaveCount(3).And.OnlyContain(x => x.Outcome == "FOUND");
    }
}
