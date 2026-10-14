using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Verce.Modules.Commerce;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// M068 / HOST-8 — the DETAIL path through the REAL host (API retry endpoint, real dispatcher, real connector, credential executor, workflow and the REAL protected store), which the
/// M067 suite proved only below the API (DET-1/2 call the execution directly): a PARTIAL run is built through the real endpoints, the confirmed material is then replaced by an authentic
/// but unusable envelope (through the store's own protocol), and a FAILED_ONLY retry started over HTTP must stop as <c>CREDENTIAL_CONFIRMED_LOST</c> WITHOUT blaming the listings, without a
/// single bulk request, with the original run byte-identical, and must complete once usable material is back. Also proves the public surfaces (run, summary, availability).
/// </summary>
public sealed partial class ListingSyncM067CredentialMaterialTests
{
    private static readonly byte[] Unusable068 = System.Text.Encoding.UTF8.GetBytes("{\"v\":1,\"access_token\":\"" + Canary + "\\ud800\",\"refresh_token\":\"r\",\"user_id\":\"777\"}");

    /// <summary>Replaces the confirmed envelope by another AUTHENTIC, version-bound one (same operation, version 1) through the store's own staged → bound → promoted protocol.</summary>
    private static async Task SwapMaterialAsync(Ctx ctx, Acct a, byte[] material)
    {
        File.Delete(a.File);
        var store = ctx.Host.Services.GetRequiredService<IProtectedCredentialStore>(); var session = Guid.NewGuid();
        (await store.CreateStagedAsync(new ProtectedCredentialWrite(a.Operation, session, a.Id, "MERCADO_LIVRE", a.Reference, material, null), CancellationToken.None)).Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
        (await store.BindCandidateAsync(new ProtectedCredentialBinding(a.Operation, session, a.Id, "MERCADO_LIVRE", a.Reference), CancellationToken.None)).Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
        (await store.PromoteCandidateAsync(a.Operation, a.Reference, 1, CancellationToken.None)).Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
        (await store.UseConfirmedAsync(new ProtectedCredentialRead(a.Id, "MERCADO_LIVRE", a.Reference, 1, a.Operation), CancellationToken.None)).Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS, "the swapped envelope is authentic: SUCCESS");
    }

    private async Task<string> ItemsJsonAsync(Guid run) =>
        await ScalarAsync("SELECT coalesce(jsonb_agg(to_jsonb(i) ORDER BY i.external_listing_id), '[]'::jsonb)::text FROM commerce.marketplace_listing_sync_run_item i WHERE sync_run_id = @r", ("r", run));

    [Fact]
    public async Task HOST_8_a_FAILED_ONLY_retry_over_HTTP_with_unusable_material_stops_as_CREDENTIAL_CONFIRMED_LOST_without_blaming_a_listing_or_sending_a_bulk_request()
    {
        var ctx = await NewHostAsync(); await using var _ = ctx.Host;
        var account = await AddAccountAsync(ctx, "detailhost", Valid);
        ctx.Wire.FailingBulkIds["MLB1"] = 1; ctx.Wire.FailingBulkIds["MLB2"] = 1;

        // 1. a PARTIAL run through the real endpoints: 3 enumerated, 1 found, 2 blocking-retryable
        var partial = await RunFullAsync(ctx, account);
        partial.Status.Should().Be("PARTIAL"); partial.Listings.Should().Be(1);
        var bulkAfterPartial = ctx.Wire.Bulk; var totalAfterPartial = ctx.Wire.Total;
        var originalRun = await ScalarAsync("SELECT to_jsonb(r)::text FROM commerce.marketplace_listing_sync_run r WHERE id = @r", ("r", partial.RunId));
        var originalItems = await ItemsJsonAsync(partial.RunId);
        var listingsBefore = await ScalarAsync("SELECT coalesce(jsonb_agg(to_jsonb(l) ORDER BY l.external_listing_id), '[]'::jsonb)::text FROM commerce.marketplace_listing l WHERE marketplace_account_id = @a", ("a", account.Id));

        // 2. the confirmed material becomes unusable (authentic envelope, unusable content)
        await SwapMaterialAsync(ctx, account, Unusable068);
        var connectionBefore = await ConnectionJsonAsync(account);

        // 3. FAILED_ONLY retry through the real endpoint, executed by the real dispatcher
        var retry = await ctx.Owner.PostRawAsync($"{Prefix}/marketplace-listing-syncs/{partial.RunId}/retry", null);
        var retryText = await retry.Content.ReadAsStringAsync(); retry.StatusCode.Should().Be(HttpStatusCode.Accepted, retryText);
        Guid retryId; using (var json = JsonDocument.Parse(retryText)) { retryId = Guid.Parse(json.RootElement.GetProperty("runId").GetString()!); json.RootElement.GetProperty("kind").GetString().Should().Be("FAILED_ONLY"); }
        using (var run = await DispatchUntilTerminalAsync(ctx, retryId))
        {
            run.RootElement.GetProperty("status").GetString().Should().Be("FAILED");
            run.RootElement.GetProperty("safeResultCode").GetString().Should().Be("CREDENTIAL_CONFIRMED_LOST");
            run.RootElement.GetProperty("requiredAction").GetString().Should().Be("REAUTHORIZE_AFTER_STORE_LOSS");
        }
        ctx.Wire.Bulk.Should().Be(bulkAfterPartial, "no bulk request for the retry"); ctx.Wire.Total.Should().Be(totalAfterPartial, "no request of any kind");
        var bulkDuringRetry = ctx.Wire.Bulk - bulkAfterPartial;
        (await ConnectionJsonAsync(account)).Should().Be(connectionBefore, "the run never touches the connection");
        (await ScalarAsync("SELECT to_jsonb(r)::text FROM commerce.marketplace_listing_sync_run r WHERE id = @r", ("r", partial.RunId))).Should().Be(originalRun, "the original run is byte-identical");
        (await ItemsJsonAsync(partial.RunId)).Should().Be(originalItems);
        (await ScalarAsync("SELECT coalesce(jsonb_agg(to_jsonb(l) ORDER BY l.external_listing_id), '[]'::jsonb)::text FROM commerce.marketplace_listing l WHERE marketplace_account_id = @a", ("a", account.Id))).Should().Be(listingsBefore);

        // the two items: retryable, carrying the stop code — never PERMANENT_ERROR / LISTING_READ_REJECTED
        var rows = await ScalarAsync("SELECT string_agg(outcome || ':' || coalesce(safe_error_code,''), ',' ORDER BY external_listing_id) FROM commerce.marketplace_listing_sync_run_item WHERE sync_run_id = @r", ("r", retryId));
        rows.Should().Be("TRANSIENT_ERROR:CREDENTIAL_CONFIRMED_LOST,TRANSIENT_ERROR:CREDENTIAL_CONFIRMED_LOST");

        // the public surfaces: the embedded latest run carries the action, availability still lets a new run start (ADR-0027: a run's loss evidence does not block admission)
        using (var state = JsonDocument.Parse(await GetAsync(ctx, $"{Prefix}/marketplace-accounts/{account.Id}/listing-sync")))
        {
            var summary = state.RootElement.GetProperty("summary");
            summary.GetProperty("latestRun").GetProperty("safeResultCode").GetString().Should().Be("CREDENTIAL_CONFIRMED_LOST");
            summary.GetProperty("latestRun").GetProperty("requiredAction").GetString().Should().Be("REAUTHORIZE_AFTER_STORE_LOSS");
            summary.GetProperty("health").GetString().Should().Be("ATTENTION_REQUIRED"); summary.GetProperty("openBlockingItemCount").GetInt32().Should().Be(2);
            state.RootElement.GetProperty("availability").GetProperty("canStart").GetBoolean().Should().BeTrue();
        }

        // 4. usable material is back (the provider recovered too): the same two items are retried and read
        await SwapMaterialAsync(ctx, account, Valid); ctx.Wire.FailingBulkIds.Clear();
        var again = await ctx.Owner.PostRawAsync($"{Prefix}/marketplace-listing-syncs/{retryId}/retry", null);
        var againText = await again.Content.ReadAsStringAsync(); again.StatusCode.Should().Be(HttpStatusCode.Accepted, againText);
        Guid againId; using (var json = JsonDocument.Parse(againText)) againId = Guid.Parse(json.RootElement.GetProperty("runId").GetString()!);
        using (var run = await DispatchUntilTerminalAsync(ctx, againId)) run.RootElement.GetProperty("status").GetString().Should().Be("SUCCEEDED");
        (await ListingCountAsync(account)).Should().Be(3);
        ctx.Wire.Unexpected.Should().BeEmpty();
        await AssertNoSecretAnywhereAsync(ctx);
        Evidence.Record("host8-detail-real-path", new { partial = new { partial.Status, bulkCalls = bulkAfterPartial }, retryRun = new { status = "FAILED", code = "CREDENTIAL_CONFIRMED_LOST", requiredAction = "REAUTHORIZE_AFTER_STORE_LOSS" }, itemsOfRetry = rows, bulkRequestsDuringRetry = bulkDuringRetry, originalRunByteIdentical = true, recovered = "SUCCEEDED", listings = 3 });
    }
}
