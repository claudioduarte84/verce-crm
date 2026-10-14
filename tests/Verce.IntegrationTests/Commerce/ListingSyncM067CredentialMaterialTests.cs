using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Verce.Api.Auth;
using Verce.Api.Commerce;
using Verce.Infrastructure.Marketplaces;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Commerce;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// M067 / HOST-1..HOST-7 (ADR-0030 §1/§2): stored credential material the adapter cannot use, over a disposable PostgreSQL and the REAL Verce.Api host — real endpoints, authentication/CSRF,
/// dispatcher, executor, kernel, authority, resolver, Mercado Livre connector, credential executor, authorization workflow and the REAL <see cref="LocalProtectedCredentialStore"/> (never a
/// double). The only substitutions are the primary HTTP handlers of the two Mercado Livre named clients (the provider wire, which also COUNTS every request) and listing-sync scheduling off.
/// Material is planted through the store's own staged → bound → promoted protocol with the account's confirmed operation, exactly like the Gate 065 diagnostic, so the store answers SUCCESS for
/// authentic, version-bound envelopes whose CONTENT only the adapter can judge.
/// </summary>
[Collection(Verce.IntegrationTests.S8C2.ExecutionHooksCollection.Name)]   // real runs are executed here: never in parallel with a class that arms the static execution test hooks
public sealed partial class ListingSyncM067CredentialMaterialTests : IAsyncLifetime
{
    private const string Prefix = "/api/commerce";
    private const string Password = "a-perfectly-fine-12char-password";
    private const string Origin = "https://localhost:4173";
    private const string Canary = MercadoLivreConnectorM067MaterialTests.Canary;
    private static readonly byte[] Valid = Encoding.UTF8.GetBytes("""{"v":1,"access_token":"host-valid-access","refresh_token":"host-valid-refresh","user_id":"777"}""");

    private PostgreSqlContainer _container = null!;
    private string _connectionString = string.Empty;
    private readonly CapturingLoggerProvider _logs = new();
    private readonly List<string> _roots = [];

    public async Task InitializeAsync()
    {
        Verce.IntegrationTests.S8C2.MarketplaceListingSyncExecutionIntegrationTests.ArmedExecutionHooks().Should().BeEmpty("a static execution hook armed by another test would fire inside this journey");
        VerceDbContext.ConfigureModuleAssemblies(Verce.Api.ModuleAssemblyCatalog.All);
        _container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("verce_s8c2_m067_host").WithUsername("verce").WithPassword("verce_test_only").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
        await using var db = new VerceDbContext(new DbContextOptionsBuilder<VerceDbContext>().UseNpgsql(_connectionString).UseSnakeCaseNamingConvention().Options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        foreach (var root in _roots) { try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best effort: OS temp */ } }
    }

    // ------------------------------------------------------------------ the provider wire (external state; counts every request)

    private sealed class Wire : HttpMessageHandler
    {
        private int _total, _scans, _bulk, _grant, _usersMe, _oauth;
        public int Total => Volatile.Read(ref _total); public int Scans => Volatile.Read(ref _scans); public int Bulk => Volatile.Read(ref _bulk);
        public int Grant => Volatile.Read(ref _grant); public int UsersMe => Volatile.Read(ref _usersMe); public int OAuth => Volatile.Read(ref _oauth);
        public Func<HttpRequestMessage, HttpResponseMessage>? OAuthBehavior { get; set; }
        public ConcurrentDictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> ScanBehavior { get; } = new();
        public ConcurrentQueue<string> BearerSeen { get; } = new();
        public ConcurrentQueue<string> Unexpected { get; } = new();
        public ConcurrentDictionary<string, byte> FailingBulkIds { get; } = new();    // M068: elements answered with status 500 (to build a PARTIAL run)
        public string IssuedAccess { get; set; } = "recovered-access";
        public string IssuedRefresh { get; set; } = "recovered-refresh";
        public (int Total, int Scans, int Bulk, int OAuth) Mark() => (Total, Scans, Bulk, OAuth);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _total);
            var uri = request.RequestUri!; var path = uri.AbsolutePath;
            if (path == "/oauth/token")
            {
                Interlocked.Increment(ref _oauth);
                return Task.FromResult(OAuthBehavior?.Invoke(request) ?? Json(HttpStatusCode.OK, "{\"access_token\":\"" + IssuedAccess + "\",\"token_type\":\"bearer\",\"expires_in\":21600,\"user_id\":777,\"refresh_token\":\"" + IssuedRefresh + "\"}"));
            }
            if (path == "/users/me") { Interlocked.Increment(ref _usersMe); return Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":777,\"site_id\":\"MLB\"}")); }
            if (path.StartsWith("/users/") && path.EndsWith("/items/search"))
            {
                var external = path["/users/".Length..^"/items/search".Length];
                if (!HttpUtility.ParseQueryString(uri.Query)["search_type"]?.Equals("scan") ?? true) { Interlocked.Increment(ref _grant); return Task.FromResult(Json(HttpStatusCode.OK, "{\"results\":[\"MLB1\"]}")); }
                Interlocked.Increment(ref _scans); BearerSeen.Enqueue(request.Headers.Authorization?.ToString() ?? "");
                return Task.FromResult(ScanBehavior.TryGetValue(external, out var behavior) ? behavior(request) : Json(HttpStatusCode.OK, "{\"results\":[\"MLB1\",\"MLB2\",\"MLB3\"]}"));
            }
            if (path == "/items/bulk")
            {
                Interlocked.Increment(ref _bulk); BearerSeen.Enqueue(request.Headers.Authorization?.ToString() ?? "");
                var ids = HttpUtility.ParseQueryString(uri.Query)["ids"]!.Split(',');
                return Task.FromResult(Json(HttpStatusCode.OK, "[" + string.Join(",", ids.Select(id => FailingBulkIds.ContainsKey(id) ? "{\"id\":\"" + id + "\",\"status_code\":500,\"body\":{\"id\":\"" + id + "\"}}" : "{\"id\":\"" + id + "\",\"status_code\":200,\"body\":{\"id\":\"" + id + "\",\"seller_id\":777,\"site_id\":\"MLB\",\"status\":\"active\",\"title\":\"M067 " + id + "\",\"price\":25.50,\"currency_id\":\"BRL\"}}")) + "]"));
            }
            Unexpected.Enqueue(request.Method + " " + uri.PathAndQuery);
            return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // ------------------------------------------------------------------ host, accounts, planted material

    private sealed record Ctx(VerceWebApplicationFactory Host, AuthTestClient Owner, Wire Wire, string Root, List<string> Api);
    private sealed record Acct(Guid Id, Guid Operation, string External, string Reference, string File);

    private async Task<Ctx> NewHostAsync()
    {
        var wire = new Wire(); var root = Path.Combine(Path.GetTempPath(), "verce-m067-store-" + Guid.NewGuid().ToString("N")); _roots.Add(root);
        var configuration = new Dictionary<string, string?>
        {
            ["Verce:PublicOrigin"] = Origin,
            ["Marketplaces:CredentialStoreRoot"] = root,
            ["Marketplaces:Cleanup:SchedulingEnabled"] = "false",
            ["Marketplaces:Housekeeping:SchedulingEnabled"] = "false",
            ["Marketplaces:MercadoLivre:Enabled"] = "true",
            ["Marketplaces:MercadoLivre:ClientId"] = "900000001",
            ["Marketplaces:MercadoLivre:ClientSecret"] = "m067-only-secret",
            ["Marketplaces:MercadoLivre:RedirectUri"] = Origin + "/api/commerce/marketplace-authorizations/MERCADO_LIVRE/callback",
            ["Marketplaces:ListingSync:SchedulingEnabled"] = "false",
        };
        var host = new VerceWebApplicationFactory(_connectionString, configuration, configureTestServices: services =>
        {
            services.AddHttpClient("MercadoLivre.Api").ConfigurePrimaryHttpMessageHandler(() => wire);
            services.AddHttpClient("MercadoLivre.OAuth").ConfigurePrimaryHttpMessageHandler(() => wire);
            services.AddSingleton<ILoggerProvider>(_logs);
        });
        var owner = await LoginOwnerAsync(host);          // starts the host: the production startup sweep has run BEFORE any account is planted
        return new Ctx(host, owner, wire, root, []);
    }

    private static async Task<AuthTestClient> LoginOwnerAsync(VerceWebApplicationFactory host)
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(); var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            if (!await roles.RoleExistsAsync(Roles.Owner)) (await roles.CreateAsync(new ApplicationRole(Roles.Owner))).Succeeded.Should().BeTrue();
            var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "M067 Owner", IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow };
            (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue(); (await users.AddToRoleAsync(user, Roles.Owner)).Succeeded.Should().BeTrue();
        }
        var client = new AuthTestClient(host.CreateHttpsClient()); await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new LoginRequest(email, Password))).StatusCode.Should().Be(HttpStatusCode.NoContent); await client.EnsureCsrfCookieAsync();
        return client;
    }

    /// <summary>Seeds a CONNECTED, AVAILABLE account (GRANTED LISTINGS_READ, confirmed operation, pointer to <c>reference</c>) and — when <paramref name="material"/> is given — plants it with the real store.</summary>
    private async Task<Acct> AddAccountAsync(Ctx ctx, string tag, byte[]? material, string external = "777", bool accessExpired = false)
    {
        var account = Guid.NewGuid(); var channel = Guid.NewGuid(); var operation = Guid.NewGuid(); var reference = "m067-ref-" + tag;
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                INSERT INTO pricing.sales_channel (id,code,name,kind,active,created_at,version) VALUES (@channel,@code,@code,'Marketplace',true,now(),1);
                INSERT INTO commerce.marketplace_provider (code,name) VALUES ('MERCADO_LIVRE','Mercado Livre') ON CONFLICT DO NOTHING;
                INSERT INTO commerce.marketplace_account (id,provider_code,external_account_id,sales_channel_id,display_name,active,credential_reference,sync_state,created_at,version) VALUES (@account,'MERCADO_LIVRE',@external,@channel,@code,true,@ref,'NEVER_SYNCED',now(),1);
                INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,candidate_credential_reference,candidate_credential_version,created_at,updated_at,decided_at,cleanup_attempt_count,version) VALUES (@operation,'CONNECT_NEW','SECRET_PERSISTED','CONFIRMED','DONE',@account,'MERCADO_LIVRE',@ref,1,now() - interval '30 days',now() - interval '30 days',now() - interval '30 days',0,1);
                INSERT INTO commerce.marketplace_account_connection (marketplace_account_id,authorization_state,runtime_availability,confirmed_credential_version,confirmed_operation_id,access_expires_at,created_at,updated_at) VALUES (@account,'CONNECTED','AVAILABLE',1,@operation,@expires,now(),now());
                INSERT INTO commerce.marketplace_account_capability (marketplace_account_id,capability_code,state,source,updated_at) VALUES (@account,'LISTINGS_READ','GRANTED','TEST',now());
                """, connection);
            command.Parameters.AddWithValue("channel", channel); command.Parameters.AddWithValue("account", account); command.Parameters.AddWithValue("operation", operation);
            command.Parameters.AddWithValue("code", "M067" + tag); command.Parameters.AddWithValue("external", external); command.Parameters.AddWithValue("ref", reference);
            command.Parameters.AddWithValue("expires", accessExpired ? DateTimeOffset.UtcNow.AddMinutes(-5) : DateTimeOffset.UtcNow.AddHours(1));
            await command.ExecuteNonQueryAsync();
        }
        var file = Path.GetFullPath(Path.Combine(ctx.Root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference))).ToLowerInvariant() + ".bin"));
        if (material is not null)
        {
            var store = ctx.Host.Services.GetRequiredService<IProtectedCredentialStore>(); store.GetType().Name.Should().Be("LocalProtectedCredentialStore", "the REAL store, never a double");
            var session = Guid.NewGuid();
            (await store.CreateStagedAsync(new ProtectedCredentialWrite(operation, session, account, "MERCADO_LIVRE", reference, material, null), CancellationToken.None)).Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
            (await store.BindCandidateAsync(new ProtectedCredentialBinding(operation, session, account, "MERCADO_LIVRE", reference), CancellationToken.None)).Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
            (await store.PromoteCandidateAsync(operation, reference, 1, CancellationToken.None)).Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
            file.StartsWith(ctx.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase).Should().BeTrue(); File.Exists(file).Should().BeTrue();
            (await store.UseConfirmedAsync(new ProtectedCredentialRead(account, "MERCADO_LIVRE", reference, 1, operation), CancellationToken.None)).Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS, "the planted envelope is authentic and version-bound: SUCCESS");
        }
        return new Acct(account, operation, external, reference, file);
    }

    // ------------------------------------------------------------------ observation

    private async Task<string> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
    }

    private Task<string> ConnectionJsonAsync(Acct a) => ScalarAsync("SELECT to_jsonb(c)::text FROM commerce.marketplace_account_connection c WHERE marketplace_account_id = @a", ("a", a.Id));
    private Task<long> AccountVersionAsync(Acct a) => ScalarAsync("SELECT version FROM commerce.marketplace_account WHERE id = @a", ("a", a.Id)).ContinueWith(t => long.Parse(t.Result));
    private async Task<long> CountAsync(string sql, Acct a) => long.Parse(await ScalarAsync(sql, ("a", a.Id)));
    private Task<long> ListingCountAsync(Acct a) => CountAsync("SELECT count(*) FROM commerce.marketplace_listing WHERE marketplace_account_id = @a", a);

    private sealed record Conn(string Authorization, string Runtime, string? FailureCode, long? ConfirmedVersion);

    private async Task<Conn> ConnAsync(Acct a)
    {
        await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT authorization_state, runtime_availability, safe_failure_code, confirmed_credential_version FROM commerce.marketplace_account_connection WHERE marketplace_account_id = @a", connection);
        command.Parameters.AddWithValue("a", a.Id);
        await using var reader = await command.ExecuteReaderAsync(); (await reader.ReadAsync()).Should().BeTrue();
        return new Conn(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3));
    }

    private async Task<string> GetAsync(Ctx ctx, string path)
    {
        var response = await ctx.Owner.GetAsync(path); var text = await response.Content.ReadAsStringAsync(); response.StatusCode.Should().Be(HttpStatusCode.OK, path + " " + text);
        lock (ctx.Api) ctx.Api.Add(text); return text;
    }

    private static async Task<JsonDocument> DispatchUntilTerminalAsync(Ctx ctx, Guid runId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90); var lastDispatch = DateTimeOffset.MinValue;
        while (true)
        {
            if (DateTimeOffset.UtcNow - lastDispatch > TimeSpan.FromSeconds(2))
            {
                await using var scope = ctx.Host.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<MarketplaceListingSyncDispatcher>().RunOnceAsync(CancellationToken.None);   // the REAL dispatcher, never a direct kernel call
                lastDispatch = DateTimeOffset.UtcNow;
            }
            var text = await (await ctx.Owner.GetAsync($"{Prefix}/marketplace-listing-syncs/{runId}")).Content.ReadAsStringAsync(); lock (ctx.Api) ctx.Api.Add(text);
            var json = JsonDocument.Parse(text); var status = json.RootElement.GetProperty("status").GetString();
            if (status is not ("QUEUED" or "RUNNING")) return json;
            json.Dispose();
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("run never reached a terminal state: " + status);
            await Task.Delay(50);
        }
    }

    private sealed record Observed(Guid RunId, string Status, string? Code, string RunAction, bool CanStart, string AvailAction, string? BlockedReason, Conn Connection, string ConnectionBefore, string ConnectionAfter, int Total, int Scans, int Bulk, int OAuth, long Listings, bool EnumerationCompleted);

    /// <summary>POST start (real endpoint) → the real dispatcher → terminal run → availability → the connection row, with the wire's counters for exactly this journey.</summary>
    private async Task<Observed> RunFullAsync(Ctx ctx, Acct a)
    {
        var mark = ctx.Wire.Mark(); var connectionBefore = await ConnectionJsonAsync(a);
        var start = await ctx.Owner.PostRawAsync($"{Prefix}/marketplace-accounts/{a.Id}/listing-syncs", null);
        var startText = await start.Content.ReadAsStringAsync(); start.StatusCode.Should().Be(HttpStatusCode.Accepted, startText);
        Guid runId; using (var startJson = JsonDocument.Parse(startText)) runId = Guid.Parse(startJson.RootElement.GetProperty("runId").GetString()!);
        using var run = await DispatchUntilTerminalAsync(ctx, runId);
        using var state = JsonDocument.Parse(await GetAsync(ctx, $"{Prefix}/marketplace-accounts/{a.Id}/listing-sync")); var availability = state.RootElement.GetProperty("availability");
        var now = ctx.Wire.Mark();
        return new Observed(runId, run.RootElement.GetProperty("status").GetString()!, run.RootElement.GetProperty("safeResultCode").GetString(), run.RootElement.GetProperty("requiredAction").GetString()!,
            availability.GetProperty("canStart").GetBoolean(), availability.GetProperty("requiredAction").GetString()!, availability.TryGetProperty("blockedReason", out var br) && br.ValueKind == JsonValueKind.String ? br.GetString() : null,
            await ConnAsync(a), connectionBefore, await ConnectionJsonAsync(a), now.Total - mark.Total, now.Scans - mark.Scans, now.Bulk - mark.Bulk, now.OAuth - mark.OAuth, await ListingCountAsync(a),
            run.RootElement.GetProperty("enumerationCompleted").GetBoolean());
    }

    private async Task AssertNoSecretAnywhereAsync(Ctx ctx, params string[] secrets)
    {
        var db = await ScalarAsync("""
            SELECT coalesce((SELECT string_agg(to_jsonb(r)::text,'') FROM commerce.marketplace_listing_sync_run r),'') ||
                   coalesce((SELECT string_agg(to_jsonb(i)::text,'') FROM commerce.marketplace_listing_sync_run_item i),'') ||
                   coalesce((SELECT string_agg(to_jsonb(c)::text,'') FROM commerce.marketplace_account_connection c),'') ||
                   coalesce((SELECT string_agg(to_jsonb(o)::text,'') FROM commerce.marketplace_account_operation o),'') ||
                   coalesce((SELECT string_agg(to_jsonb(a)::text,'') FROM commerce.marketplace_account a),'') ||
                   coalesce((SELECT string_agg(to_jsonb(l)::text,'') FROM commerce.marketplace_listing l),'')
            """);
        var logs = string.Join("\n", _logs.Messages) + "\n" + string.Join("\n", _logs.Entries.SelectMany(e => e.State.Values.Select(v => v?.ToString() ?? "").Append(e.ExceptionText ?? "")));
        string api; lock (ctx.Api) api = string.Join("\n", ctx.Api);
        foreach (var secret in secrets.Append(Canary).Append("host-valid-access").Append("host-valid-refresh"))
        {
            db.Should().NotContain(secret, "database rows"); api.Should().NotContain(secret, "API responses"); logs.Should().NotContain(secret, "log messages and structured state");
        }
        logs.Should().NotContain("Bearer ", "no authorization header in any log");
    }

    private static readonly Dictionary<string, byte[]> Fixtures = MercadoLivreConnectorM067MaterialTests.Unusable.ToDictionary(f => f.Id, f => f.Material);

    // ============================================================ HOST-1 — every unusable fixture, one real run each

    [Fact]
    public async Task HOST_1_unusable_stored_material_stops_the_real_run_as_CREDENTIAL_CONFIRMED_LOST_with_zero_HTTP_and_an_untouched_connection()
    {
        var ctx = await NewHostAsync(); var rows = new List<object>();
        await using var _ = ctx.Host;
        using (new AssertionScope())
            foreach (var (id, what, material) in MercadoLivreConnectorM067MaterialTests.Unusable)
            {
                if (material.Length == 0) { rows.Add(new { fixture = id, what, skipped = "the real store refuses to persist empty material (Validate); covered at adapter level by MAT-1" }); continue; }
                var account = await AddAccountAsync(ctx, id.Replace("-", ""), material, external: "h1-" + id);
                var o = await RunFullAsync(ctx, account); var label = $"{id} ({what})";
                o.Status.Should().Be("FAILED", label); o.Code.Should().Be("CREDENTIAL_CONFIRMED_LOST", label); o.EnumerationCompleted.Should().BeFalse(label);
                o.Total.Should().Be(0, label + ": zero HTTP requests"); o.Scans.Should().Be(0, label); o.Bulk.Should().Be(0, label); o.OAuth.Should().Be(0, label);
                o.RunAction.Should().Be("REAUTHORIZE_AFTER_STORE_LOSS", label); o.AvailAction.Should().Be("NONE", label); o.CanStart.Should().BeTrue(label);
                o.ConnectionAfter.Should().Be(o.ConnectionBefore, label + ": the run never touches the connection");
                o.Connection.Authorization.Should().Be("CONNECTED", label); o.Connection.Runtime.Should().Be("AVAILABLE", label + ": runtime preserved (ADR-0029 §3)");
                o.Listings.Should().Be(0, label);
                rows.Add(new { fixture = id, what, status = o.Status, code = o.Code, runAction = o.RunAction, availabilityAction = o.AvailAction, canStart = o.CanStart, runtime = o.Connection.Runtime, authorization = o.Connection.Authorization, httpRequests = o.Total, scans = o.Scans, bulk = o.Bulk, oauthPosts = o.OAuth, listings = o.Listings, connectionUnchanged = o.ConnectionAfter == o.ConnectionBefore });
            }
        ctx.Wire.Total.Should().Be(0, "no request of any kind reached the provider during the whole matrix"); ctx.Wire.Unexpected.Should().BeEmpty();
        await AssertNoSecretAnywhereAsync(ctx);
        Evidence.Record("host1-matrix", new { accounts = rows.Count, totalHttpRequests = ctx.Wire.Total, results = rows });
    }

    // ============================================================ HOST-2 — the nine Gate 065 scenarios, as a repository test

    private sealed record Expect(string Status, string Code, string RunAction, bool CanStart, string AvailAction, string Runtime, string Authorization, int Scans, int OAuth);

    [Fact]
    public async Task HOST_2_the_nine_Gate065_scenarios_reproduce_the_frozen_classification_table_on_the_real_store()
    {
        var ctx = await NewHostAsync(); await using var _ = ctx.Host;
        const string Lost = "REAUTHORIZE_AFTER_STORE_LOSS";
        var table = new (string Name, Expect Expected)[]
        {
            ("valid", new("SUCCEEDED", "LISTING_SYNC_COMPLETED", "NONE", true, "NONE", "AVAILABLE", "CONNECTED", 1, 0)),
            ("store_unavailable", new("FAILED", "CREDENTIAL_STORE_UNAVAILABLE", "CONTACT_ADMIN", true, "NONE", "AVAILABLE", "CONNECTED", 0, 0)),
            ("confirmed_lost", new("FAILED", "CREDENTIAL_CONFIRMED_LOST", Lost, true, "NONE", "AVAILABLE", "CONNECTED", 0, 0)),
            ("decrypt_failure", new("FAILED", "CREDENTIAL_CONFIRMED_LOST", Lost, true, "NONE", "AVAILABLE", "CONNECTED", 0, 0)),
            ("invalid_stored_json", new("FAILED", "CREDENTIAL_CONFIRMED_LOST", Lost, true, "NONE", "AVAILABLE", "CONNECTED", 0, 0)),
            ("invalid_stored_utf16", new("FAILED", "CREDENTIAL_CONFIRMED_LOST", Lost, true, "NONE", "AVAILABLE", "CONNECTED", 0, 0)),
            ("provider_401", new("FAILED", "AUTHORIZATION_NOT_USABLE", "NONE", false, "REAUTHORIZE", "UNKNOWN", "REAUTHORIZATION_REQUIRED", 1, 1)),
            ("provider_400", new("FAILED", "LISTING_ENUMERATION_INCOMPLETE", "NONE", true, "NONE", "AVAILABLE", "CONNECTED", 1, 0)),
            ("provider_bad_json", new("FAILED", "LISTING_ENUMERATION_INCOMPLETE", "NONE", true, "NONE", "AVAILABLE", "CONNECTED", 1, 0)),
        };
        var rows = new List<object>();
        ctx.Wire.OAuthBehavior = _ => Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}");      // only the provider_401 journey ever reaches the token endpoint
        using (new AssertionScope())
            foreach (var (name, expected) in table)
            {
                var material = name switch { "invalid_stored_json" => Fixtures["M-02"], "invalid_stored_utf16" => Fixtures["M-08"], _ => Valid };
                var account = await AddAccountAsync(ctx, name.Replace("_", ""), material, external: name == "valid" ? "777" : "s-" + name);
                FileStream? held = null;
                try
                {
                    switch (name)
                    {
                        case "store_unavailable": held = new FileStream(account.File, FileMode.Open, FileAccess.ReadWrite, FileShare.None); break;
                        case "confirmed_lost": File.Delete(account.File); break;
                        case "decrypt_failure": await File.WriteAllBytesAsync(account.File, [1, 2, 3, 4]); break;
                        case "provider_401": ctx.Wire.ScanBehavior[account.External] = _ => Json(HttpStatusCode.Unauthorized, "{}"); break;
                        case "provider_400": ctx.Wire.ScanBehavior[account.External] = _ => Json(HttpStatusCode.BadRequest, "{}"); break;
                        case "provider_bad_json": ctx.Wire.ScanBehavior[account.External] = _ => Json(HttpStatusCode.OK, "{not json"); break;
                    }
                    var o = await RunFullAsync(ctx, account); var label = name;
                    o.Status.Should().Be(expected.Status, label); o.Code.Should().Be(expected.Code, label); o.RunAction.Should().Be(expected.RunAction, label); o.CanStart.Should().Be(expected.CanStart, label);
                    o.AvailAction.Should().Be(expected.AvailAction, label); o.Connection.Runtime.Should().Be(expected.Runtime, label); o.Connection.Authorization.Should().Be(expected.Authorization, label);
                    o.Scans.Should().Be(expected.Scans, label); o.OAuth.Should().Be(expected.OAuth, label);
                    if (name == "valid") { o.Bulk.Should().Be(1); o.Listings.Should().Be(3); } else o.Bulk.Should().Be(0, label);
                    if (name is not ("valid" or "provider_401")) o.ConnectionAfter.Should().Be(o.ConnectionBefore, label + ": connection untouched");
                    if (name is "invalid_stored_json" or "invalid_stored_utf16") o.Total.Should().Be(0, label + ": zero HTTP");
                    rows.Add(new { scenario = name, status = o.Status, code = o.Code, runAction = o.RunAction, availabilityAction = o.AvailAction, canStart = o.CanStart, runtime = o.Connection.Runtime, authorization = o.Connection.Authorization, connectionFailureCode = o.Connection.FailureCode, scans = o.Scans, bulk = o.Bulk, tokenPosts = o.OAuth, httpRequests = o.Total, listings = o.Listings });
                }
                finally { held?.Dispose(); }
            }
        ctx.Wire.Unexpected.Should().BeEmpty();
        await AssertNoSecretAnywhereAsync(ctx);
        Evidence.Record("host2-nine-scenarios", new { scenarios = rows.Count, results = rows });
    }

    // ============================================================ HOST-3 — the Owner probe

    [Theory]
    [InlineData("M-08")]
    [InlineData("M-13")]
    [InlineData("M-02")]
    public async Task HOST_3_the_owner_probe_with_unusable_material_takes_the_existing_confirmed_loss_branch_with_zero_HTTP(string fixture)
    {
        var ctx = await NewHostAsync(); await using var _ = ctx.Host;
        var account = await AddAccountAsync(ctx, "probe", Fixtures[fixture]); var mark = ctx.Wire.Mark();
        var probe = await ctx.Owner.PostAsync($"{Prefix}/marketplace-accounts/{account.Id}/probe", new ReauthorizeMarketplaceAccountRequest(await AccountVersionAsync(account)));
        var probeText = await probe.Content.ReadAsStringAsync(); lock (ctx.Api) ctx.Api.Add(probeText);
        var connection = await ConnAsync(account);
        connection.Authorization.Should().Be("REAUTHORIZATION_REQUIRED"); connection.FailureCode.Should().Be("PROBE_CREDENTIAL_UNREADABLE", "never PROBE_FAILED");
        connection.Runtime.Should().NotBe("UNAVAILABLE", "a local credential defect is not a provider outage"); connection.FailureCode.Should().NotBe("PROBE_FAILED");
        using (var status = JsonDocument.Parse(await GetAsync(ctx, $"{Prefix}/marketplace-accounts/{account.Id}/connection"))) status.RootElement.GetProperty("requiredAction").GetString().Should().Be("REAUTHORIZE_AFTER_STORE_LOSS");
        (ctx.Wire.Total - mark.Total).Should().Be(0, "zero HTTP");
        await AssertNoSecretAnywhereAsync(ctx);
        Evidence.Record("host3-probe-" + fixture, new { fixture, probeHttpStatus = (int)probe.StatusCode, authorization = connection.Authorization, failureCode = connection.FailureCode, runtime = connection.Runtime, requiredAction = "REAUTHORIZE_AFTER_STORE_LOSS", httpRequests = ctx.Wire.Total - mark.Total });
    }

    // ============================================================ HOST-4 — complete recovery through the real reauthorization flow

    [Fact]
    public async Task HOST_4_recovery_failure_then_probe_then_real_reauthorization_then_a_new_sync_that_succeeds()
    {
        var ctx = await NewHostAsync(); await using var _ = ctx.Host;
        var account = await AddAccountAsync(ctx, "recover", Fixtures["M-08"]);

        // 1. the failure: the run stops, the connection is untouched
        var failed = await RunFullAsync(ctx, account);
        failed.Status.Should().Be("FAILED"); failed.Code.Should().Be("CREDENTIAL_CONFIRMED_LOST"); failed.RunAction.Should().Be("REAUTHORIZE_AFTER_STORE_LOSS"); failed.Total.Should().Be(0);

        // 2. the Owner probe observes the same loss: the connection now asks for the store-loss recovery
        (await ctx.Owner.PostAsync($"{Prefix}/marketplace-accounts/{account.Id}/probe", new ReauthorizeMarketplaceAccountRequest(await AccountVersionAsync(account)))).Dispose();
        (await ConnAsync(account)).Should().Match<Conn>(c => c.Authorization == "REAUTHORIZATION_REQUIRED" && c.FailureCode == "PROBE_CREDENTIAL_UNREADABLE");
        using (var availability = JsonDocument.Parse(await GetAsync(ctx, $"{Prefix}/marketplace-accounts/{account.Id}/listing-sync")))
        {
            availability.RootElement.GetProperty("availability").GetProperty("canStart").GetBoolean().Should().BeFalse();
            availability.RootElement.GetProperty("availability").GetProperty("requiredAction").GetString().Should().Be("REAUTHORIZE_AFTER_STORE_LOSS");
        }
        ctx.Wire.Total.Should().Be(0, "nothing reached the provider before the new consent");

        // 3. the REAL reauthorization flow (begin → provider consent simulated by the wire → callback), never a database edit
        var begin = await ctx.Owner.PostAsync($"{Prefix}/marketplace-accounts/{account.Id}/reauthorize", new ReauthorizeMarketplaceAccountRequest(await AccountVersionAsync(account)));
        var beginText = await begin.Content.ReadAsStringAsync(); begin.StatusCode.Should().Be(HttpStatusCode.OK, beginText);
        string state; using (var beginJson = JsonDocument.Parse(beginText)) state = HttpUtility.ParseQueryString(new Uri(beginJson.RootElement.GetProperty("authorizationUri").GetString()!).Query)["state"]!;
        ctx.Wire.IssuedAccess = "recovered-access"; ctx.Wire.IssuedRefresh = "recovered-refresh";
        var callback = await ctx.Owner.GetAsync($"{Prefix}/marketplace-authorizations/MERCADO_LIVRE/callback?state={Uri.EscapeDataString(state)}&code=m067-synthetic-code");
        callback.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.OK, HttpStatusCode.NotFound);
        var recovered = await ConnAsync(account);
        recovered.Authorization.Should().Be("CONNECTED"); recovered.ConfirmedVersion.Should().Be(2, "the new consent installed a new confirmed version of the same reference"); recovered.FailureCode.Should().BeNull();
        ctx.Wire.OAuth.Should().Be(1, "exactly one token exchange, through the real adapter");

        // 4. the failed run no longer asks for a recovery that has happened (ADR-0027 §4.1 step 2)
        using (var oldRun = JsonDocument.Parse(await GetAsync(ctx, $"{Prefix}/marketplace-listing-syncs/{failed.RunId}"))) oldRun.RootElement.GetProperty("requiredAction").GetString().Should().Be("NONE");

        // 5. a new sync with the new material completes
        var scansBefore = ctx.Wire.Scans; var next = await RunFullAsync(ctx, account);
        next.Status.Should().Be("SUCCEEDED"); next.Code.Should().Be("LISTING_SYNC_COMPLETED"); next.RunAction.Should().Be("NONE"); next.CanStart.Should().BeTrue();
        next.Listings.Should().Be(3); ctx.Wire.Scans.Should().Be(scansBefore + 1);
        ctx.Wire.BearerSeen.Should().OnlyContain(b => b == "Bearer recovered-access", "the recovered credential is what travels");
        ctx.Wire.Unexpected.Should().BeEmpty();
        await AssertNoSecretAnywhereAsync(ctx, "recovered-access", "recovered-refresh");
        Evidence.Record("host4-recovery", new { failedRun = new { failed.Status, failed.Code, failed.RunAction, failed.Total }, afterProbe = "REAUTHORIZATION_REQUIRED / PROBE_CREDENTIAL_UNREADABLE / REAUTHORIZE_AFTER_STORE_LOSS", afterReconnect = recovered, oldRunRequiredAction = "NONE", newRun = new { next.Status, next.Code, next.RunAction, next.Listings, next.Scans, next.Bulk } });
    }

    // ============================================================ HOST-5 / HOST-6 — the margin refresh keeps its ADR-0025 §F.3 rows

    [Fact]
    public async Task HOST_5_a_margin_refresh_with_unparseable_material_is_REFRESH_TOKEN_UNAVAILABLE_with_zero_POST_never_REFRESH_OUTCOME_UNKNOWN()
    {
        var ctx = await NewHostAsync(); await using var _ = ctx.Host;
        var account = await AddAccountAsync(ctx, "refreshbad", Fixtures["M-08"], accessExpired: true);
        var o = await RunFullAsync(ctx, account);
        o.Status.Should().Be("FAILED"); o.Code.Should().Be("AUTHORIZATION_NOT_USABLE"); o.Total.Should().Be(0, "no token POST, no read"); o.OAuth.Should().Be(0);
        o.Connection.Authorization.Should().Be("REAUTHORIZATION_REQUIRED"); o.Connection.FailureCode.Should().Be("REFRESH_TOKEN_UNAVAILABLE"); o.AvailAction.Should().Be("REAUTHORIZE"); o.CanStart.Should().BeFalse();
        (await ScalarAsync("SELECT decision || '/' || coalesce(safe_result_code,'') FROM commerce.marketplace_account_operation WHERE marketplace_account_id = @a AND kind = 'REFRESH'", ("a", account.Id))).Should().Be("FAIL_CLOSED/REFRESH_TOKEN_UNAVAILABLE");
        await AssertNoSecretAnywhereAsync(ctx);
        Evidence.Record("host5-refresh-unparseable", new { status = o.Status, code = o.Code, connectionFailureCode = o.Connection.FailureCode, authorization = o.Connection.Authorization, availabilityAction = o.AvailAction, tokenPosts = o.OAuth, httpRequests = o.Total, refreshOperation = "FAIL_CLOSED/REFRESH_TOKEN_UNAVAILABLE" });
    }

    [Fact]
    public async Task HOST_6_a_margin_refresh_with_a_valid_refresh_token_recovers_material_whose_access_token_was_not_transmissible_without_any_re_consent()
    {
        var ctx = await NewHostAsync(); await using var _ = ctx.Host;
        ctx.Wire.IssuedAccess = "refreshed-access"; ctx.Wire.IssuedRefresh = "refreshed-refresh";
        var account = await AddAccountAsync(ctx, "refreshgood", Fixtures["M-13"], accessExpired: true);
        var o = await RunFullAsync(ctx, account);
        o.OAuth.Should().Be(1, "exactly one refresh POST"); o.Status.Should().Be("SUCCEEDED"); o.Code.Should().Be("LISTING_SYNC_COMPLETED"); o.Listings.Should().Be(3);
        o.Connection.Authorization.Should().Be("CONNECTED"); o.Connection.ConfirmedVersion.Should().Be(2);
        ctx.Wire.BearerSeen.Should().OnlyContain(b => b == "Bearer refreshed-access");
        await AssertNoSecretAnywhereAsync(ctx, "refreshed-access", "refreshed-refresh");
        Evidence.Record("host6-refresh-recovers", new { status = o.Status, tokenPosts = o.OAuth, scans = o.Scans, bulk = o.Bulk, confirmedVersion = o.Connection.ConfirmedVersion, listings = o.Listings });
    }

    // ============================================================ HOST-7 — a transient store outage is never confused with a confirmed loss

    [Fact]
    public async Task HOST_7_the_same_account_is_CREDENTIAL_STORE_UNAVAILABLE_while_the_file_is_locked_and_CREDENTIAL_CONFIRMED_LOST_once_the_unusable_content_is_readable()
    {
        var ctx = await NewHostAsync(); await using var _ = ctx.Host;
        var account = await AddAccountAsync(ctx, "twostates", Fixtures["M-02"]);
        Observed locked;
        using (new FileStream(account.File, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) locked = await RunFullAsync(ctx, account);
        locked.Status.Should().Be("FAILED"); locked.Code.Should().Be("CREDENTIAL_STORE_UNAVAILABLE"); locked.RunAction.Should().Be("CONTACT_ADMIN"); locked.Total.Should().Be(0);
        var released = await RunFullAsync(ctx, account);
        released.Status.Should().Be("FAILED"); released.Code.Should().Be("CREDENTIAL_CONFIRMED_LOST"); released.RunAction.Should().Be("REAUTHORIZE_AFTER_STORE_LOSS"); released.Total.Should().Be(0);
        released.ConnectionAfter.Should().Be(locked.ConnectionBefore, "neither run touched the connection");
        await AssertNoSecretAnywhereAsync(ctx);
        Evidence.Record("host7-two-states", new { locked = new { locked.Code, locked.RunAction }, released = new { released.Code, released.RunAction }, httpRequests = ctx.Wire.Total });
    }
}
