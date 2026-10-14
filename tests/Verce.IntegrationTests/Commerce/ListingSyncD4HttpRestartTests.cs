using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Verce.Api.Auth;
using Verce.Infrastructure.Marketplaces;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Commerce;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// M063 / C063-04 — D4-HTTP-1 (handoff D4 / G03, ADR-0029 contract 062): the complete 100/95/5 → host restart → HTTP POST retry → dispatcher executes the retry → HEALTHY
/// sequence in ONE test, over a disposable PostgreSQL and the real Verce.Api host (Development, real Mercado Livre connector, real endpoints/auth/CSRF/service/dispatcher/
/// executor/kernel/authority). The ONLY substitutions (the three the contract permits): (1) the primary HTTP handlers of the two Mercado Livre named clients, (2) the protected
/// credential store (synthetic secret), (3) listing-sync scheduling OFF (the dispatcher is driven explicitly, never replaced). The wire is the provider: external state, so the same
/// instance serves both hosts. Nothing else crosses the restart except the database.
/// </summary>
[Collection(Verce.IntegrationTests.S8C2.ExecutionHooksCollection.Name)]   // real runs are executed here: never in parallel with a class that arms the static execution test hooks
public sealed class ListingSyncD4HttpRestartTests : IAsyncLifetime
{
    private const string Prefix = "/api/commerce";
    private const string AccessSentinel = "d4-synthetic-access-token";
    private const string Password = "a-perfectly-fine-12char-password";

    private PostgreSqlContainer _container = null!;
    private string _connectionString = string.Empty;
    private readonly CapturingLoggerProvider _logs = new();
    private readonly List<MarketplaceTestHarness> _harnesses = [];

    public async Task InitializeAsync()
    {
        VerceDbContext.ConfigureModuleAssemblies(Verce.Api.ModuleAssemblyCatalog.All);
        _container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("verce_s8c2_d4_http").WithUsername("verce").WithPassword("verce_test_only").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
        await using var db = new VerceDbContext(new DbContextOptionsBuilder<VerceDbContext>().UseNpgsql(_connectionString).UseSnakeCaseNamingConvention().Options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var harness in _harnesses) harness.Dispose();
        await _container.DisposeAsync();
    }

    // ------------------------------------------------------------------ the provider (external state, shared by both hosts)

    private sealed class Provider(IReadOnlyList<string> ids, HashSet<string> failing) : HttpMessageHandler
    {
        public volatile bool FailingActive = true;
        public ConcurrentQueue<string> Unexpected { get; } = new();
        private readonly object _gate = new();
        public List<string[]> BulkRequests { get; } = [];
        public int Scans { get; private set; }
        public Dictionary<string, int> RequestsPerId { get; } = new(StringComparer.Ordinal);
        public List<string> Attributes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/users/777/items/search")
            {
                var scroll = HttpUtility.ParseQueryString(uri.Query)["scroll_id"];
                lock (_gate) Scans++;
                return Task.FromResult(Ok(scroll is null
                    ? "{\"results\":[" + string.Join(",", ids.Select(i => "\"" + i + "\"")) + "],\"scroll_id\":\"S1\"}"
                    : "{\"results\":[],\"scroll_id\":\"S1\"}"));
            }
            if (uri.AbsolutePath == "/items/bulk")
            {
                var query = HttpUtility.ParseQueryString(uri.Query); var requested = query["ids"]!.Split(',');
                lock (_gate) { BulkRequests.Add(requested); Attributes.Add(query["attributes"]!); foreach (var id in requested) RequestsPerId[id] = RequestsPerId.GetValueOrDefault(id) + 1; }
                var elements = requested.Select(id => FailingActive && failing.Contains(id)
                    ? "{\"id\":\"" + id + "\",\"status_code\":500,\"body\":{\"id\":\"" + id + "\"}}"
                    : "{\"id\":\"" + id + "\",\"status_code\":200,\"body\":{\"id\":\"" + id + "\",\"seller_id\":777,\"site_id\":\"MLB\",\"status\":\"active\",\"title\":\"D4 " + id + "\",\"price\":25.50,\"currency_id\":\"BRL\"}}");
                return Task.FromResult(Ok("[" + string.Join(",", elements) + "]"));
            }
            Unexpected.Enqueue(request.Method + " " + uri.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        }

        private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    /// <summary>Substitution 2: the protected store hands the real connector a synthetic secret (user id 777) for the confirmed credential; every other operation succeeds.</summary>
    private sealed class SyntheticSecretStore : IProtectedCredentialStore
    {
        private static readonly ProtectedCredentialStoreResult Ok = new(ProtectedCredentialStoreOutcome.SUCCESS);
        public Task<ProtectedCredentialStoreResult> CreateStagedAsync(ProtectedCredentialWrite write, CancellationToken ct) => Task.FromResult(Ok);
        public Task<ProtectedCredentialStoreResult> BindCandidateAsync(ProtectedCredentialBinding binding, CancellationToken ct) => Task.FromResult(Ok);
        public Task<ProtectedCredentialStoreResult> PromoteCandidateAsync(Guid operationId, string credentialReference, long credentialVersion, CancellationToken ct) => Task.FromResult(Ok);
        public Task<ProtectedCredentialReadResult> UseConfirmedAsync(ProtectedCredentialRead read, CancellationToken ct) =>
            Task.FromResult(new ProtectedCredentialReadResult(ProtectedCredentialStoreOutcome.SUCCESS, Encoding.UTF8.GetBytes("{\"v\":1,\"access_token\":\"" + AccessSentinel + "\",\"refresh_token\":\"d4-synthetic-refresh-token\",\"user_id\":\"777\"}")));
        public Task<ProtectedCredentialStoreResult> CompareAndSwapReplaceAsync(ProtectedCredentialWrite write, long expectedVersion, CancellationToken ct) => Task.FromResult(Ok);
        public Task<ProtectedCredentialStoreResult> DeleteCandidateAsync(Guid operationId, string credentialReference, long credentialVersion, CancellationToken ct) => Task.FromResult(Ok);
    }

    private VerceWebApplicationFactory NewHost(Provider provider)
    {
        var harness = new MarketplaceTestHarness(); _harnesses.Add(harness);
        const string origin = "https://localhost:4173";
        var configuration = new Dictionary<string, string?>(harness.ExtraConfiguration)
        {
            ["Verce:PublicOrigin"] = origin,
            ["Marketplaces:MercadoLivre:Enabled"] = "true",
            ["Marketplaces:MercadoLivre:ClientId"] = "900000001",
            ["Marketplaces:MercadoLivre:ClientSecret"] = "d4-only-secret",
            ["Marketplaces:MercadoLivre:RedirectUri"] = origin + "/api/commerce/marketplace-authorizations/MERCADO_LIVRE/callback",
            ["Marketplaces:ListingSync:SchedulingEnabled"] = "false",          // substitution 3: no Quartz job; the dispatcher is driven explicitly
        };
        return new VerceWebApplicationFactory(_connectionString, configuration, configureTestServices: services =>
        {
            services.RemoveAll<IProtectedCredentialStore>();
            services.AddSingleton<IProtectedCredentialStore, SyntheticSecretStore>();
            services.AddHttpClient("MercadoLivre.Api").ConfigurePrimaryHttpMessageHandler(() => provider);      // substitution 1
            services.AddHttpClient("MercadoLivre.OAuth").ConfigurePrimaryHttpMessageHandler(() => provider);
            services.AddSingleton<ILoggerProvider>(_logs);
        });
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Guid> SeedAccountAsync()
    {
        var account = Guid.NewGuid(); var channel = Guid.NewGuid(); var operation = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO pricing.sales_channel (id,code,name,kind,active,created_at,version) VALUES (@channel,'D4HTTP','D4HTTP','Marketplace',true,now(),1);
            INSERT INTO commerce.marketplace_provider (code,name) VALUES ('MERCADO_LIVRE','Mercado Livre') ON CONFLICT DO NOTHING;
            INSERT INTO commerce.marketplace_account (id,provider_code,external_account_id,sales_channel_id,display_name,active,credential_reference,sync_state,created_at,version) VALUES (@account,'MERCADO_LIVRE','777',@channel,'D4 account',true,'d4-ref','NEVER_SYNCED',now(),1);
            INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,candidate_credential_reference,candidate_credential_version,created_at,updated_at,decided_at,cleanup_attempt_count,version) VALUES (@operation,'CONNECT_NEW','SECRET_PERSISTED','CONFIRMED','DONE',@account,'MERCADO_LIVRE','d4-ref',1,now() - interval '30 days',now() - interval '30 days',now() - interval '30 days',0,1);
            INSERT INTO commerce.marketplace_account_connection (marketplace_account_id,authorization_state,runtime_availability,confirmed_credential_version,confirmed_operation_id,access_expires_at,created_at,updated_at) VALUES (@account,'CONNECTED','AVAILABLE',1,@operation,now() + interval '1 hour',now(),now());
            INSERT INTO commerce.marketplace_account_capability (marketplace_account_id,capability_code,state,source,updated_at) VALUES (@account,'LISTINGS_READ','GRANTED','TEST',now());
            """, connection);
        command.Parameters.AddWithValue("channel", channel); command.Parameters.AddWithValue("account", account); command.Parameters.AddWithValue("operation", operation);
        await command.ExecuteNonQueryAsync(); return account;
    }

    private async Task<AuthTestClient> LoginOwnerAsync(VerceWebApplicationFactory host, string email, bool create)
    {
        if (create)
        {
            using var scope = host.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(); var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            if (!await roles.RoleExistsAsync(Roles.Owner)) (await roles.CreateAsync(new ApplicationRole(Roles.Owner))).Succeeded.Should().BeTrue();
            var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "D4 Owner", IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow };
            (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue(); (await users.AddToRoleAsync(user, Roles.Owner)).Succeeded.Should().BeTrue();
        }
        var client = new AuthTestClient(host.CreateHttpsClient()); await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new LoginRequest(email, Password))).StatusCode.Should().Be(HttpStatusCode.NoContent); await client.EnsureCsrfCookieAsync();
        return client;
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    /// <summary>Drives the REAL dispatcher of the given host (never a direct kernel/execution call) until the run is terminal, observing it only through HTTP.</summary>
    private static async Task<JsonDocument> DispatchUntilTerminalAsync(VerceWebApplicationFactory host, AuthTestClient client, Guid runId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90); var lastDispatch = DateTimeOffset.MinValue;
        while (true)
        {
            if (DateTimeOffset.UtcNow - lastDispatch > TimeSpan.FromSeconds(2))
            {
                await using var scope = host.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<MarketplaceListingSyncDispatcher>().RunOnceAsync(CancellationToken.None);
                lastDispatch = DateTimeOffset.UtcNow;
            }
            var json = await JsonAsync(await client.GetAsync($"{Prefix}/marketplace-listing-syncs/{runId}"));
            var status = json.RootElement.GetProperty("status").GetString();
            if (status is not ("QUEUED" or "RUNNING")) return json;
            json.Dispose();
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("run never reached a terminal state: " + status);
            await Task.Delay(50);
        }
    }

    private static async Task WaitExecutorIdleAsync(VerceWebApplicationFactory host)
    {
        var executor = host.Services.GetRequiredService<MarketplaceListingSyncExecutor>(); var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (executor.Executing != 0) { if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("executor not idle"); await Task.Delay(25); }
    }

    private async Task<string> SqlJsonAsync(string sql, Guid run)
    {
        await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); command.Parameters.AddWithValue("r", run);
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountAsync(string sql, Guid account)
    {
        await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); command.Parameters.AddWithValue("a", account);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private const string FrozenAttributes = "body.id,body.seller_id,body.site_id,body.title,body.price,body.currency_id,body.status,body.sub_status,body.permalink,body.last_updated,body.attributes,body.variations";

    // ------------------------------------------------------------------ D4-HTTP-1

    [Fact]
    public async Task D4_HTTP_1_provider_100_95_5_then_a_new_host_then_POST_retry_executed_by_the_dispatcher_to_HEALTHY_with_the_original_run_byte_identical()
    {
        var ids = Enumerable.Range(1, 100).Select(i => $"MLB{9900000000L + i}").ToArray();
        var failing = new[] { ids[2], ids[24], ids[46], ids[70], ids[97] }.ToHashSet(StringComparer.Ordinal);     // one per detail chunk of 20
        var provider = new Provider(ids, failing);
        var account = await SeedAccountAsync(); var email = Guid.NewGuid().ToString("N") + "@example.test";
        Guid originalId; string originalRunBefore, originalItemsBefore; IServiceProvider host1Services; MarketplaceListingSyncExecutor executor1;
        Verce.IntegrationTests.S8C2.MarketplaceListingSyncExecutionIntegrationTests.ArmedExecutionHooks().Should().BeEmpty("a static execution hook armed by another test would fire inside this journey");

        // ---------------------------------------------------------------- Phase A — first host, first execution
        await using (var host1 = NewHost(provider))
        {
            host1Services = host1.Services; executor1 = host1.Services.GetRequiredService<MarketplaceListingSyncExecutor>();
            var owner = await LoginOwnerAsync(host1, email, create: true);
            var start = await owner.PostRawAsync($"{Prefix}/marketplace-accounts/{account}/listing-syncs", null);
            start.StatusCode.Should().Be(HttpStatusCode.Accepted);
            using (var startJson = await JsonAsync(start)) originalId = Guid.Parse(startJson.RootElement.GetProperty("runId").GetString()!);

            using (var run = await DispatchUntilTerminalAsync(host1, owner, originalId))
            {
                run.RootElement.GetProperty("status").GetString().Should().Be("PARTIAL", "step 5");
                var counters = run.RootElement.GetProperty("counters");
                counters.GetProperty("total").GetInt32().Should().Be(100); counters.GetProperty("found").GetInt32().Should().Be(95);
                counters.GetProperty("blockingRetryable").GetInt32().Should().Be(5); counters.GetProperty("blockingNonRetryable").GetInt32().Should().Be(0); counters.GetProperty("pending").GetInt32().Should().Be(0);
            }
            provider.Scans.Should().Be(2, "one FULL enumeration of two scan pages");
            provider.RequestsPerId.Where(x => failing.Contains(x.Key)).Select(x => x.Value).Should().OnlyContain(n => n == 2, "step 4: element 500 answered twice for each of the five");
            provider.RequestsPerId.Where(x => !failing.Contains(x.Key)).Select(x => x.Value).Should().OnlyContain(n => n == 1);
            provider.BulkRequests.Count(r => r.Length == 20).Should().Be(5); provider.BulkRequests.Count(r => r.Length == 1).Should().Be(5);
            provider.Attributes.Should().OnlyContain(a => a == FrozenAttributes);

            using (var summary = await JsonAsync(await owner.GetAsync($"{Prefix}/marketplace-accounts/{account}/listing-sync")))
            {
                var s = summary.RootElement.GetProperty("summary");
                s.GetProperty("health").GetString().Should().Be("ATTENTION_REQUIRED", "step 6"); s.GetProperty("openBlockingItemCount").GetInt32().Should().Be(5);
            }
            originalRunBefore = await SqlJsonAsync("SELECT to_jsonb(r)::text FROM commerce.marketplace_listing_sync_run r WHERE id = @r", originalId);          // step 7: snapshot
            originalItemsBefore = await SqlJsonAsync("SELECT coalesce(jsonb_agg(to_jsonb(i) ORDER BY i.external_listing_id), '[]'::jsonb)::text FROM commerce.marketplace_listing_sync_run_item i WHERE sync_run_id = @r", originalId);
            await WaitExecutorIdleAsync(host1);
        }                                                                                                          // step 8: the first host is disposed completely
        (await CountAsync("SELECT count(*) FROM commerce.marketplace_listing WHERE marketplace_account_id = @a", account)).Should().Be(95, "no listing exists for an identity the provider never resolved");

        // ---------------------------------------------------------------- Phase B/C/D/E — a brand-new host over the same database
        provider.FailingActive = false;                                                                                   // the provider has recovered
        int scansBefore = provider.Scans, bulkBefore = provider.BulkRequests.Count;
        await using (var host2 = NewHost(provider))                                                                       // steps 9-10: new host, new DI container, same database
        {
            host2.Services.Should().NotBeSameAs(host1Services, "step 9-10: a different DI container");
            host2.Services.GetRequiredService<MarketplaceListingSyncExecutor>().Should().NotBeSameAs(executor1, "the singleton executor of the first host did not survive the restart");
            var owner = await LoginOwnerAsync(host2, email, create: false);                                               // step 11: new login against persisted identity

            using (var items = await JsonAsync(await owner.GetAsync($"{Prefix}/marketplace-listing-syncs/{originalId}/items?outcome=TRANSIENT_ERROR&pageSize=100")))   // step 12
            {
                items.RootElement.GetProperty("total").GetInt32().Should().Be(5);
                items.RootElement.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("externalListingId").GetString()).Should().BeEquivalentTo(failing);
            }

            var retry = await owner.PostRawAsync($"{Prefix}/marketplace-listing-syncs/{originalId}/retry", null);        // step 13
            retry.StatusCode.Should().Be(HttpStatusCode.Accepted, "step 14");
            Guid retryId;
            using (var retryJson = await JsonAsync(retry))
            {
                retryId = Guid.Parse(retryJson.RootElement.GetProperty("runId").GetString()!);
                retryJson.RootElement.GetProperty("kind").GetString().Should().Be("FAILED_ONLY", "step 15");
                retryJson.RootElement.GetProperty("retryOfRunId").GetString().Should().Be(originalId.ToString(), "step 17");
                retryJson.RootElement.GetProperty("rootRunId").GetString().Should().Be(originalId.ToString());
                retryJson.RootElement.GetProperty("counters").GetProperty("byOrigin").GetProperty("RETRY_TARGET").GetInt32().Should().Be(5, "step 16");
            }

            using (var finished = await DispatchUntilTerminalAsync(host2, owner, retryId))                                // step 18: the new host's REAL dispatcher
            {
                finished.RootElement.GetProperty("status").GetString().Should().Be("SUCCEEDED", "step 21");
                var counters = finished.RootElement.GetProperty("counters");
                counters.GetProperty("found").GetInt32().Should().Be(5); counters.GetProperty("listingsCreated").GetInt32().Should().Be(5);
            }
            provider.Scans.Should().Be(scansBefore, "step 19: a FAILED_ONLY retry makes no enumeration call");
            provider.BulkRequests.Skip(bulkBefore).Should().ContainSingle("step 20").Which.OrderBy(x => x, StringComparer.Ordinal).Should().Equal(failing.OrderBy(x => x, StringComparer.Ordinal));

            (await CountAsync("SELECT count(*) FROM commerce.marketplace_listing WHERE marketplace_account_id = @a", account)).Should().Be(100, "step 22");
            (await CountAsync("SELECT count(*) FROM (SELECT external_listing_id FROM commerce.marketplace_listing WHERE marketplace_account_id = @a GROUP BY 1 HAVING count(*) > 1) d", account)).Should().Be(0, "step 26: no duplicates");
            (await CountAsync("SELECT count(*) FROM commerce.marketplace_listing_observation o JOIN commerce.marketplace_listing l ON l.id = o.marketplace_listing_id WHERE l.marketplace_account_id = @a", account)).Should().Be(100, "one observation per listing");
            (await SqlJsonAsync("SELECT to_jsonb(r)::text FROM commerce.marketplace_listing_sync_run r WHERE id = @r", originalId)).Should().Be(originalRunBefore, "step 23: the original run is byte-identical");
            (await SqlJsonAsync("SELECT coalesce(jsonb_agg(to_jsonb(i) ORDER BY i.external_listing_id), '[]'::jsonb)::text FROM commerce.marketplace_listing_sync_run_item i WHERE sync_run_id = @r", originalId)).Should().Be(originalItemsBefore, "step 23: and so are its items");

            using (var summary = await JsonAsync(await owner.GetAsync($"{Prefix}/marketplace-accounts/{account}/listing-sync")))
            {
                var s = summary.RootElement.GetProperty("summary");
                s.GetProperty("health").GetString().Should().Be("HEALTHY", "step 24"); s.GetProperty("openBlockingItemCount").GetInt32().Should().Be(0, "step 25");
            }
            await WaitExecutorIdleAsync(host2);
        }
        provider.Unexpected.Should().BeEmpty("the real connector called only the two frozen read endpoints");
        _logs.Messages.Should().NotContain(m => m.Contains(AccessSentinel) || m.Contains("Bearer "), "no secret in any captured log, across both hosts");
    }
}
