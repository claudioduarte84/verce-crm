using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Verce.Modules.Commerce;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.LiveValidation;

/// <summary>
/// S8C.2 D12(b): the scanner self-test matrix ST-01..ST-10 for <c>tests/live-validation/Invoke-S8C2LeakScan.ps1</c> (LIVE-SCAN-01), run with <c>pwsh -NoProfile -File</c>
/// against synthetic fixtures only. The database surface uses a Testcontainers PostgreSQL (random name) holding <c>verce_s8c2_live</c>, migrated with the real EF
/// migrator and seeded with safe rows. Guard rows use only <c>docker-shim.cmd</c>, so no row ever contacts <c>verce-postgres</c>. Every invocation, in every test,
/// is checked for: empty stderr, a stdout that is exactly the frozen summary schema, none of the planted literals (ST-10), and no file written to the working directory.
/// </summary>
public sealed class LeakScanFixture : IAsyncLifetime
{
    public const string DatabaseName = "verce_s8c2_live";
    public const string Secret = "ST04-synthetic-client-secret-7f3a";
    public static readonly string[] ScannedTables =
    [
        "commerce.marketplace_authorization_session", "commerce.marketplace_account", "commerce.marketplace_account_connection", "commerce.marketplace_account_capability",
        "commerce.marketplace_account_operation", "commerce.marketplace_listing", "commerce.marketplace_listing_observation", "commerce.marketplace_listing_sync_run",
        "commerce.marketplace_listing_sync_run_item", "commerce.marketplace_account_sku_mapping", "platform.audit_log",
    ];

    private PostgreSqlContainer? _container;
    public string ContainerName { get; } = "leakscan-" + Guid.NewGuid().ToString("N")[..12];
    public string ConnectionString { get; private set; } = "";
    public Guid ListingId { get; private set; }
    public static string RepositoryRoot { get; } = FindRepositoryRoot();
    public static string ScriptPath => Path.Combine(RepositoryRoot, "tests", "live-validation", "Invoke-S8C2LeakScan.ps1");
    public static string ShimPath => Path.Combine(RepositoryRoot, "tests", "live-validation", "fixtures", "docker-shim.cmd");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Verce.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("LEAKSCAN_REPOSITORY_ROOT_NOT_FOUND");
    }

    private VerceDbContext Db() => new(new DbContextOptionsBuilder<VerceDbContext>().UseNpgsql(ConnectionString).UseSnakeCaseNamingConvention().Options);

    public async Task InitializeAsync()
    {
        VerceDbContext.ConfigureModuleAssemblies(Verce.Api.ModuleAssemblyCatalog.All);
        _container = new PostgreSqlBuilder("postgres:17-alpine").WithName(ContainerName).WithDatabase(DatabaseName).WithUsername("postgres").WithPassword("leakscan_test_only").Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        await using (var db = Db()) await db.Database.MigrateAsync();
        await SeedAsync();
    }

    public async Task DisposeAsync() { if (_container is not null) await _container.DisposeAsync(); }

    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>Safe rows only: one account + connection (CONNECTED), a listing with normalized facts and its observation, a SUCCEEDED and a PARTIAL run with run items (one
    /// TRANSIENT_ERROR / LISTING_READ_FAILED), one ACTIVE SKU mapping, and generic audit rows. Nothing here resembles a secret, a token or a provider payload.</summary>
    private async Task SeedAsync()
    {
        var account = Guid.NewGuid(); var channel = Guid.NewGuid(); var operation = Guid.NewGuid(); var code = "LS" + account.ToString("N")[..10];
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                INSERT INTO pricing.sales_channel (id,code,name,kind,active,created_at,version) VALUES (@channel,@code,@code,'Marketplace',true,now(),1);
                INSERT INTO commerce.marketplace_provider (code,name) VALUES ('MERCADO_LIVRE','Mercado Livre') ON CONFLICT DO NOTHING;
                INSERT INTO commerce.marketplace_account (id,provider_code,external_account_id,sales_channel_id,display_name,active,credential_reference,sync_state,created_at,version) VALUES (@account,'MERCADO_LIVRE',@code,@channel,@code,true,'safe-technical-reference','NEVER_SYNCED',now(),1);
                INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,candidate_credential_reference,candidate_credential_version,created_at,updated_at,decided_at,cleanup_attempt_count,version) VALUES (@operation,'CONNECT_NEW','SECRET_PERSISTED','CONFIRMED','DONE',@account,'MERCADO_LIVRE','safe-technical-reference',1,now() - interval '30 days',now() - interval '30 days',now() - interval '30 days',0,1);
                INSERT INTO commerce.marketplace_account_connection (marketplace_account_id,authorization_state,runtime_availability,confirmed_credential_version,confirmed_operation_id,access_expires_at,created_at,updated_at) VALUES (@account,'CONNECTED','AVAILABLE',1,@operation,now() + interval '1 hour',now(),now());
                INSERT INTO commerce.marketplace_account_capability (marketplace_account_id,capability_code,state,source,updated_at) VALUES (@account,'LISTINGS_READ','GRANTED','TEST',now());
                """, connection);
            command.Parameters.AddWithValue("channel", channel); command.Parameters.AddWithValue("account", account); command.Parameters.AddWithValue("operation", operation); command.Parameters.AddWithValue("code", code);
            await command.ExecuteNonQueryAsync();
        }

        var now = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
        await using (var db = Db())
        {
            var facts = new NormalizedListingFacts("MLB900000001", "SKU-SAFE-1", "Anúncio sintético seguro", 42.50m, MarketplaceListingStatus.ACTIVE, "active", 1, "https://produto.mercadolivre.com.br/MLB900000001", now, []);
            var listing = MarketplaceListing.ImportFromProvider(account, facts, Guid.NewGuid(), new(MarketplaceLinkageState.UNLINKED, null, null), now);
            db.Add(listing); db.Add(new MarketplaceAccountSkuMapping(account, "SKU-SAFE-1", Guid.NewGuid(), Guid.NewGuid(), now));
            await db.SaveChangesAsync(); ListingId = listing.Id;
        }
        await SeedRunAsync(account, now.AddMinutes(1), found: 2);
        await SeedRunAsync(account, now.AddMinutes(10), found: 1, (MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR, "LISTING_READ_FAILED"));

        // Generic audit rows of the kind the writes above produce (no EF interceptor runs on this direct context): technical column names and ids only.
        await ExecuteAsync("""
            INSERT INTO platform.audit_log (id,occurred_at,operation_started_at,user_id,user_display_name,entity_schema,entity_table,entity_id,operation,changed_columns,old_values_json,new_values_json,correlation_id,request_id,wave_index,source)
            VALUES (gen_random_uuid(),now(),now(),NULL,'Seed','commerce','marketplace_account_sku_mapping',@entity,'INSERT',ARRAY['active'],NULL,'{"active":true,"externalSku":"SKU-SAFE-1"}',gen_random_uuid(),NULL,1,'Api'),
                   (gen_random_uuid(),now(),now(),NULL,'Seed','commerce','marketplace_listing_sync_run',@entity,'INSERT',ARRAY['status'],NULL,'{"kind":"FULL","status":"QUEUED"}',gen_random_uuid(),NULL,1,'Api');
            """, ("entity", Guid.NewGuid()));
    }

    private async Task SeedRunAsync(Guid account, DateTimeOffset requestedAt, int found, params (MarketplaceListingSyncItemOutcome Outcome, string Code)[] blocking)
    {
        var lease = TimeSpan.FromMinutes(2); await using var db = Db(); var kernel = new MarketplaceListingSyncRunKernel(db);
        var run = await kernel.CreateFullAsync(account, Guid.NewGuid(), requestedAt); var claimed = (await new MarketplaceListingSyncLeaseAuthority(db).ClaimAsync(run.Id, requestedAt, lease))!;
        var tag = run.Id.ToString("N")[^10..]; var foundIds = Enumerable.Range(0, found).Select(i => $"MLB-{tag}-F{i:00}").ToArray(); var blockIds = blocking.Select((_, i) => $"MLB-{tag}-B{i:00}").ToArray();
        await kernel.RegisterTargetsWithLeaseAsync(run.Id, claimed.LeaseToken, foundIds.Concat(blockIds), MarketplaceListingSyncItemOrigin.ENUMERATED, requestedAt, lease);
        await kernel.MarkEnumerationCompletedWithLeaseAsync(run.Id, claimed.LeaseToken, requestedAt, lease);
        var items = await db.Set<MarketplaceListingSyncRunItem>().Where(x => x.SyncRunId == run.Id).ToDictionaryAsync(x => x.ExternalListingId);
        foreach (var id in foundIds)
        {
            var listing = new MarketplaceListing(account, id, null, MarketplaceListingStatus.ACTIVE); db.Add(listing); await db.SaveChangesAsync();
            await kernel.RecordOutcomeWithLeaseAsync(run.Id, claimed.LeaseToken, items[id].Id, MarketplaceListingSyncItemOutcome.FOUND, null, listing.Id, true, true, requestedAt, lease);
        }
        for (var i = 0; i < blocking.Length; i++) await kernel.RecordOutcomeWithLeaseAsync(run.Id, claimed.LeaseToken, items[blockIds[i]].Id, blocking[i].Outcome, blocking[i].Code, null, false, false, requestedAt, lease);
        await kernel.FinalizeWithLeaseAsync(run.Id, claimed.LeaseToken, null, requestedAt.AddMinutes(1), lease);
    }

    /// <summary>Row counts of every scanned table plus the database's tuple counters, read after the statistics have settled.</summary>
    public async Task<string> SnapshotAsync()
    {
        string previous = "";
        for (var attempt = 0; attempt < 15; attempt++)
        {
            await using var connection = new NpgsqlConnection(ConnectionString); await connection.OpenAsync();
            var parts = new List<string>();
            foreach (var table in ScannedTables)
            {
                await using var count = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection); parts.Add($"{table}={await count.ExecuteScalarAsync()}");
            }
            await using var stats = new NpgsqlCommand("SELECT tup_inserted, tup_updated, tup_deleted FROM pg_stat_database WHERE datname = current_database()", connection);
            await using var reader = await stats.ExecuteReaderAsync(); await reader.ReadAsync(); parts.Add($"ins={reader.GetInt64(0)} upd={reader.GetInt64(1)} del={reader.GetInt64(2)}");
            var current = string.Join(';', parts);
            if (current == previous) return current;
            previous = current; await Task.Delay(1200);
        }
        return previous;
    }
}

public sealed class LeakScanSelfTests(LeakScanFixture db) : IClassFixture<LeakScanFixture>
{
    private static readonly string[] CountKeys = ["AUTH_HEADER", "BEARER", "ACCESS_TOKEN", "REFRESH_TOKEN", "AUTH_CODE", "CLIENT_SECRET", "TOKEN_ENDPOINT", "RAW_REQUEST", "RAW_RESPONSE",
        "STRUCTURAL.DB_COLUMN", "STRUCTURAL.HTTPCLIENT_LOG", "STRUCTURAL.LOG_BODY", "STRUCTURAL.API_PROPERTY"];
    private const string BasicHeaderLiteral = "c3ludGhldGljLXN0MDI=";

    // ST-10: nothing planted by any row, the synthetic secret, or a comparable form of it may ever appear in the scanner's output.
    private static readonly string[] NeverEchoed =
    [
        LeakScanFixture.Secret, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(LeakScanFixture.Secret))), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(LeakScanFixture.Secret))).ToLowerInvariant(),
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(LeakScanFixture.Secret))), Convert.ToBase64String(Encoding.UTF8.GetBytes(LeakScanFixture.Secret)),
        BasicHeaderLiteral, "st02-synthetic", "st03SyntheticTokenValue0001", "APP_USR-ST03-SYNTHETIC", "TG-abcdef123456", "MLB_TEST_001", "Synthetic Listing", "st07-synthetic",
    ];

    // ---------- harness ----------

    private sealed class Scenario : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "leakscan-test-" + Guid.NewGuid().ToString("N"));
        public string WorkDir => Path.Combine(Root, "work");
        public string Log => Path.Combine(Root, "host.log");
        public string ApiDir => Path.Combine(Root, "api");
        public string Secrets => Path.Combine(Root, "secrets.json");
        public string ShimLog => Path.Combine(Root, "shim.log");
        public Scenario()
        {
            Directory.CreateDirectory(WorkDir); Directory.CreateDirectory(ApiDir);
            File.WriteAllText(Secrets, JsonSerializer.Serialize(new Dictionary<string, string> { ["Marketplaces:MercadoLivre:ClientSecret"] = LeakScanFixture.Secret }));
            File.WriteAllText(Log, CleanLog());
            foreach (var (name, json) in CleanApi()) File.WriteAllText(Path.Combine(ApiDir, name), json);
        }
        public void AppendLog(params string[] lines) => File.AppendAllLines(Log, lines);
        public void Api(string name, string json) => File.WriteAllText(Path.Combine(ApiDir, name), json);
        public int ShimInvocations => File.Exists(ShimLog) ? File.ReadAllLines(ShimLog).Count(x => x.Length > 0) : 0;
        public string[] ShimLines => File.Exists(ShimLog) ? File.ReadAllLines(ShimLog) : [];
        public void Dispose() { try { Directory.Delete(Root, true); } catch { /* temp */ } }
    }

    private sealed record Result(int ExitCode, string Stdout, JsonDocument Summary)
    {
        public string Outcome => Summary.RootElement.GetProperty("result").GetString()!;
        public string? ErrorCode => Summary.RootElement.GetProperty("errorCode").ValueKind == JsonValueKind.Null ? null : Summary.RootElement.GetProperty("errorCode").GetString();
        public JsonElement[] Surfaces => Summary.RootElement.GetProperty("surfaces").EnumerateArray().ToArray();
        public JsonElement Surface(string name) => Surfaces.Single(s => s.GetProperty("surface").GetString() == name);
        public int Count(string surface, string key) => Surface(surface).GetProperty("counts").GetProperty(key).GetInt32();
        public int Units(string surface) => Surface(surface).GetProperty("unitsScanned").GetInt32();
        public int Artifacts(string surface) => Surface(surface).GetProperty("artifactsScanned").GetInt32();
        public void OnlyNonZero(string surface, params string[] allowed) =>
            CountKeys.Where(k => Count(surface, k) != 0).Should().BeSubsetOf(allowed, $"{surface} must report only the planted categories");
    }

    private async Task<Result> RunAsync(Scenario s, string[] arguments, Dictionary<string, string?>? environment = null)
    {
        var before = Directory.GetFileSystemEntries(s.WorkDir, "*", SearchOption.AllDirectories).Length;
        var info = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = s.WorkDir, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-File", LeakScanFixture.ScriptPath }.Concat(arguments)) info.ArgumentList.Add(a);
        info.Environment["LEAKSCAN_SHIM_LOG"] = s.ShimLog;
        foreach (var (key, value) in environment ?? []) { if (value is null) info.Environment.Remove(key); else info.Environment[key] = value; }
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await process.WaitForExitAsync(timeout.Token);
        var output = await stdout; var error = await stderr;

        error.Should().BeEmpty("the scanner's stderr stays empty");
        foreach (var forbidden in NeverEchoed) { output.Should().NotContain(forbidden, "ST-10: no planted literal, secret or comparable form is ever echoed"); }
        Directory.GetFileSystemEntries(s.WorkDir, "*", SearchOption.AllDirectories).Length.Should().Be(before, "ST-10: the scanner writes no file");
        var summary = JsonDocument.Parse(output);
        AssertFrozenSchema(summary.RootElement);
        var expectedExit = summary.RootElement.GetProperty("result").GetString() switch { "PASS" => 0, "FAIL" => 1, "REFUSED" or "INPUT_INVALID" => 2, var other => throw new InvalidOperationException(other) };
        process.ExitCode.Should().Be(expectedExit, "the exit code follows the result: 0 PASS, 1 FAIL, 2 REFUSED/INPUT_INVALID");
        return new Result(process.ExitCode, output, summary);
    }

    private static void AssertFrozenSchema(JsonElement root)
    {
        root.EnumerateObject().Select(p => p.Name).Should().Equal("result", "errorCode", "surfaces");
        root.GetProperty("result").GetString().Should().BeOneOf("PASS", "FAIL", "REFUSED", "INPUT_INVALID");
        foreach (var surface in root.GetProperty("surfaces").EnumerateArray())
        {
            surface.EnumerateObject().Select(p => p.Name).Should().Equal("surface", "artifactsScanned", "unitsScanned", "counts");
            surface.GetProperty("surface").GetString().Should().BeOneOf("LOGS", "DATABASE", "API");
            surface.GetProperty("counts").EnumerateObject().Select(p => p.Name).Should().Equal(CountKeys);
            foreach (var count in surface.GetProperty("counts").EnumerateObject()) count.Value.ValueKind.Should().Be(JsonValueKind.Number);
        }
    }

    private string[] Surfaces(Scenario s, bool log = true, bool database = true, bool api = true)
    {
        var args = new List<string> { "-UserSecretsFile", s.Secrets };
        if (log) args.AddRange(["-LogFile", s.Log]);
        if (api) args.AddRange(["-ApiResponseDirectory", s.ApiDir]);
        if (database) args.AddRange(["-PostgresContainer", db.ContainerName, "-Database", LeakScanFixture.DatabaseName, "-ConfirmDisposable"]);
        return [.. args];
    }

    private string[] GuardArguments(Scenario s, string container, string database, bool confirm = true, bool secrets = false)
    {
        var args = new List<string> { "-PostgresContainer", container, "-Database", database, "-DockerExecutable", LeakScanFixture.ShimPath };
        if (confirm) args.Add("-ConfirmDisposable");
        if (secrets) args.AddRange(["-UserSecretsFile", s.Secrets]);
        return [.. args];
    }

    // ---------- fixtures ----------

    private static string CleanLog() => string.Join('\n',
    [
        "info: Microsoft.Hosting.Lifetime[14]", "      Now listening on: https://localhost:7246",
        "info: Microsoft.AspNetCore.Authorization.DefaultAuthorizationService[1]", "      Authorization was successful.",
        "info: Verce.Infrastructure.Marketplaces.MarketplaceCleanupService[0]", "      Marketplace cleanup sweep finished: 0 operations resolved, 0 retained.",
        "info: Verce.Infrastructure.Marketplaces.MercadoLivre.MarketplaceHttpSender[0]",
        "      MarketplaceProviderCall provider=MERCADO_LIVRE operation=ITEMS_BULK status=200 classification=NONE certainty=SENT_AND_RESPONSE_RECEIVED attempt=1 durationMs=412 account=01a12129-830f-7357-b3f2-0782d3e74381 correlation=5002fe61b3f54b8186448c3a7b1d0e9a requestedIdCount=20 envelopeCount=20 usableEnvelopeCount=20 histogram=200:20",
        "info: Verce.Infrastructure.Marketplaces.MercadoLivre.MarketplaceHttpSender[0]",
        "      MarketplaceProviderCall provider=MERCADO_LIVRE operation=OAUTH_REFRESH status=200 classification=SUCCESS certainty=SENT_AND_RESPONSE_RECEIVED attempt=1 durationMs=88.5 account=01a12129-830f-7357-b3f2-0782d3e74381 correlation=0acbdabf392240a3a6dc3732855a2219",
        "",
    ]);

    private static (string Name, string Json)[] CleanApi() =>
    [
        ("listing-sync.json", """{"availability":{"canStart":true,"blockedReason":null,"requiredAction":"NONE","retryAfterUntil":null,"activeRunId":null},"summary":{"activeRun":null,"latestRun":null,"latestFullRun":null,"lastSucceededFullRunAt":null,"openBlockingItemCount":0,"health":"NEVER_RUN"}}"""),
        ("run.json", """{"runId":"01a12129-830f-7357-b3f2-0782d3e74381","kind":"FULL","status":"PARTIAL","counters":{"total":2,"found":1,"blockingRetryable":1,"blockingNonRetryable":0,"pending":0,"byOutcome":{"FOUND":1,"TRANSIENT_ERROR":1}},"safeResultCode":"LISTING_SYNC_PARTIAL","requiredAction":"NONE"}"""),
        ("items.json", """{"items":[{"externalListingId":"MLB900000002","origin":"ENUMERATED","outcome":"TRANSIENT_ERROR","safeErrorCode":"LISTING_READ_FAILED","marketplaceListingId":null,"retryable":true}],"page":1,"pageSize":25,"total":1}"""),
        ("published-items.json", """{"items":[{"id":"01a12129-830f-7357-b3f2-0782d3e74381","externalListingId":"MLB900000001","externalSku":"SKU-SAFE-1","title":"Anúncio sintético seguro","observedPrice":42.5,"observedStatus":"ACTIVE","linkageState":"UNLINKED","syncState":"SYNCED","listingUrl":"https://produto.mercadolivre.com.br/MLB900000001","providerNativeStatus":"active","variationCount":1,"linkageSource":null}],"page":1,"pageSize":20,"total":1}"""),
        ("connection.json", """{"accountId":"01a12129-830f-7357-b3f2-0782d3e74381","authorizationState":"CONNECTED","runtimeAvailability":"AVAILABLE","identityVerifiedAt":"2026-10-10T12:00:00Z","requiredAction":"NONE","authorizationInProgress":false,"credentialOperationInProgress":false,"version":1}"""),
        ("sku-mappings.json", """{"items":[{"id":"01a12129-830f-7357-b3f2-0782d3e74381","externalSku":"SKU-SAFE-1","productCode":"P-001","productName":"Caneca","active":true,"version":1}],"page":1,"pageSize":25,"total":1}"""),
    ];

    private static void AssertClean(Result result, bool log = true, bool database = true, bool api = true)
    {
        result.Outcome.Should().Be("PASS"); result.ExitCode.Should().Be(0); result.ErrorCode.Should().BeNull();
        var expected = new[] { (log, "LOGS"), (database, "DATABASE"), (api, "API") }.Where(x => x.Item1).Select(x => x.Item2).ToArray();
        result.Surfaces.Select(s => s.GetProperty("surface").GetString()).Should().Equal(expected);
        foreach (var surface in expected)
        {
            result.Artifacts(surface).Should().BeGreaterThan(0); result.Units(surface).Should().BeGreaterThan(0);
            foreach (var key in CountKeys) result.Count(surface, key).Should().Be(0, $"{surface}.{key}");
        }
    }

    // ---------- ST-01 ----------

    [Fact]
    public async Task ST01_clean_fixture_passes_on_all_three_surfaces_without_false_positives_and_leaves_the_database_untouched()
    {
        using var s = new Scenario();
        var before = await db.SnapshotAsync();
        var result = await RunAsync(s, Surfaces(s));
        AssertClean(result);
        result.Units("LOGS").Should().Be(File.ReadAllLines(s.Log).Length); result.Artifacts("DATABASE").Should().Be(11); result.Units("API").Should().Be(6);
        (await db.SnapshotAsync()).Should().Be(before, "the scanner is read-only: row counts and tuple counters are unchanged");
        // Stability: the same input yields the same summary.
        (await RunAsync(s, Surfaces(s))).Stdout.Should().Be(result.Stdout);
    }

    [Fact]
    public async Task ST01_each_surface_passes_alone()
    {
        using var s = new Scenario();
        AssertClean(await RunAsync(s, Surfaces(s, database: false, api: false)), database: false, api: false);
        AssertClean(await RunAsync(s, Surfaces(s, log: false, database: false)), log: false, database: false);
        AssertClean(await RunAsync(s, Surfaces(s, log: false, api: false)), log: false, api: false);
    }

    // ---------- ST-02 ----------

    [Fact]
    public async Task ST02_authorization_header_leak_fails_on_each_surface_and_on_all_together()
    {
        using var s = new Scenario(); var auditId = Guid.NewGuid();
        s.AppendLog("info: Foo.Bar[0]", $"      Authorization: Basic {BasicHeaderLiteral}");
        var log = await RunAsync(s, Surfaces(s, database: false, api: false));
        log.Outcome.Should().Be("FAIL"); log.Count("LOGS", "AUTH_HEADER").Should().BeGreaterThanOrEqualTo(1); log.OnlyNonZero("LOGS", "AUTH_HEADER");
        File.WriteAllText(s.Log, CleanLog());

        s.Api("leak.json", """{"authorization":"st02-synthetic","status":"ok"}""");
        var api = await RunAsync(s, Surfaces(s, log: false, database: false));
        api.Outcome.Should().Be("FAIL"); api.Count("API", "AUTH_HEADER").Should().BeGreaterThanOrEqualTo(1); api.Count("API", "STRUCTURAL.API_PROPERTY").Should().BeGreaterThanOrEqualTo(1);
        api.OnlyNonZero("API", "AUTH_HEADER", "STRUCTURAL.API_PROPERTY");
        File.Delete(Path.Combine(s.ApiDir, "leak.json"));

        await PlantAuditAsync(auditId, """{"authorization":"st02-synthetic"}""");
        try
        {
            var database = await RunAsync(s, Surfaces(s, log: false, api: false));
            database.Outcome.Should().Be("FAIL"); database.Count("DATABASE", "AUTH_HEADER").Should().BeGreaterThanOrEqualTo(1); database.OnlyNonZero("DATABASE", "AUTH_HEADER");

            s.AppendLog("info: Foo.Bar[0]", $"      Authorization: Basic {BasicHeaderLiteral}"); s.Api("leak.json", """{"authorization":"st02-synthetic"}""");
            var all = await RunAsync(s, Surfaces(s));
            all.Outcome.Should().Be("FAIL");
            foreach (var surface in new[] { "LOGS", "DATABASE", "API" }) all.Count(surface, "AUTH_HEADER").Should().BeGreaterThanOrEqualTo(1);
        }
        finally { await RemoveAuditAsync(auditId); }
    }

    // ---------- ST-03 ----------

    [Fact]
    public async Task ST03_bearer_and_token_leaks_fail_where_planted()
    {
        using var s = new Scenario();
        s.AppendLog("info: Foo.Bar[0]", "      header Bearer st03SyntheticTokenValue0001 and the words access_token, refresh_token");
        var log = await RunAsync(s, Surfaces(s, database: false, api: false));
        log.Outcome.Should().Be("FAIL");
        foreach (var key in new[] { "BEARER", "ACCESS_TOKEN", "REFRESH_TOKEN" }) log.Count("LOGS", key).Should().BeGreaterThanOrEqualTo(1, key);
        File.WriteAllText(s.Log, CleanLog());

        s.Api("leak.json", """{"note":"TG-abcdef123456"}""");
        var api = await RunAsync(s, Surfaces(s, log: false, database: false));
        api.Outcome.Should().Be("FAIL"); api.Count("API", "REFRESH_TOKEN").Should().BeGreaterThanOrEqualTo(1); api.OnlyNonZero("API", "REFRESH_TOKEN");
        File.Delete(Path.Combine(s.ApiDir, "leak.json"));

        var original = await TextAsync("SELECT title_snapshot FROM commerce.marketplace_listing WHERE id = '" + db.ListingId + "'");
        await db.ExecuteAsync("UPDATE commerce.marketplace_listing SET title_snapshot = 'APP_USR-ST03-SYNTHETIC' WHERE id = @id", ("id", db.ListingId));
        try
        {
            var database = await RunAsync(s, Surfaces(s, log: false, api: false));
            database.Outcome.Should().Be("FAIL"); database.Count("DATABASE", "BEARER").Should().BeGreaterThanOrEqualTo(1); database.OnlyNonZero("DATABASE", "BEARER");
        }
        finally { await db.ExecuteAsync("UPDATE commerce.marketplace_listing SET title_snapshot = @t WHERE id = @id", ("t", original), ("id", db.ListingId)); }
    }

    // ---------- ST-04 ----------

    [Fact]
    public async Task ST04_the_unlabelled_client_secret_is_found_by_fingerprint_on_each_surface_exactly_once()
    {
        using var s = new Scenario();
        s.AppendLog("info: Foo.Bar[0]", $"      note value {LeakScanFixture.Secret}");
        var log = await RunAsync(s, Surfaces(s, database: false, api: false));
        log.Outcome.Should().Be("FAIL"); log.Count("LOGS", "CLIENT_SECRET").Should().Be(1); log.OnlyNonZero("LOGS", "CLIENT_SECRET");
        File.WriteAllText(s.Log, CleanLog());

        s.Api("leak.json", JsonSerializer.Serialize(new { note = $"note value {LeakScanFixture.Secret}" }));
        var api = await RunAsync(s, Surfaces(s, log: false, database: false));
        api.Outcome.Should().Be("FAIL"); api.Count("API", "CLIENT_SECRET").Should().Be(1); api.OnlyNonZero("API", "CLIENT_SECRET");
        File.Delete(Path.Combine(s.ApiDir, "leak.json"));

        await db.ExecuteAsync("UPDATE commerce.marketplace_listing SET sync_error = @e WHERE id = @id", ("e", $"note value {LeakScanFixture.Secret}"), ("id", db.ListingId));
        try
        {
            var database = await RunAsync(s, Surfaces(s, log: false, api: false));
            database.Outcome.Should().Be("FAIL"); database.Count("DATABASE", "CLIENT_SECRET").Should().Be(1); database.OnlyNonZero("DATABASE", "CLIENT_SECRET");
        }
        finally { await db.ExecuteAsync("UPDATE commerce.marketplace_listing SET sync_error = NULL WHERE id = @id", ("id", db.ListingId)); }
    }

    // ---------- ST-05 ----------

    [Fact]
    public async Task ST05_raw_provider_payload_in_the_database_fails_by_pattern_and_a_raw_body_column_fails_structurally()
    {
        using var s = new Scenario(); var auditId = Guid.NewGuid();
        await PlantAuditAsync(auditId, """{"seller_id":90000001,"site_id":"MLB","available_quantity":5,"status":"active"}""");
        try
        {
            var pattern = await RunAsync(s, Surfaces(s, log: false, api: false));
            pattern.Outcome.Should().Be("FAIL"); pattern.Count("DATABASE", "RAW_RESPONSE").Should().BeGreaterThanOrEqualTo(1);
        }
        finally { await RemoveAuditAsync(auditId); }

        await db.ExecuteAsync("ALTER TABLE commerce.marketplace_listing_observation ADD COLUMN raw_body text");
        try
        {
            var column = await RunAsync(s, Surfaces(s, log: false, api: false));
            column.Outcome.Should().Be("FAIL"); column.Count("DATABASE", "STRUCTURAL.DB_COLUMN").Should().Be(1); column.OnlyNonZero("DATABASE", "STRUCTURAL.DB_COLUMN");
        }
        finally { await db.ExecuteAsync("ALTER TABLE commerce.marketplace_listing_observation DROP COLUMN raw_body"); }
        AssertClean(await RunAsync(s, Surfaces(s)));
    }

    // ---------- ST-06 ----------

    [Fact]
    public async Task ST06_raw_provider_payloads_in_logs_fail_structurally_and_not_by_pattern()
    {
        const string body = """{"id":"MLB_TEST_001","title":"Synthetic Listing","status":"active","price":123.45}""";
        const string providerCall = "MarketplaceProviderCall provider=MERCADO_LIVRE operation=ITEMS_BULK status=200 classification=NONE certainty=SENT_AND_RESPONSE_RECEIVED attempt=1 durationMs=412 account=01a12129-830f-7357-b3f2-0782d3e74381 correlation=5002fe61b3f54b8186448c3a7b1d0e9a requestedIdCount=1 envelopeCount=1 usableEnvelopeCount=1 histogram=200:1";
        using var s = new Scenario();

        s.AppendLog("info: Verce.Infrastructure.Marketplaces.MercadoLivre.MarketplaceHttpSender[0]", $"      {providerCall} responseBody={body}");
        var a = await RunAsync(s, Surfaces(s, database: false, api: false));
        a.Outcome.Should().Be("FAIL"); a.Count("LOGS", "STRUCTURAL.LOG_BODY").Should().Be(1); a.Count("LOGS", "RAW_RESPONSE").Should().Be(0, "proves structural, not pattern, detection");
        File.WriteAllText(s.Log, CleanLog());

        s.AppendLog("info: Verce.Infrastructure.Marketplaces.MercadoLivre.MercadoLivreConnector[0]", $"      {body}");
        var b = await RunAsync(s, Surfaces(s, database: false, api: false));
        b.Outcome.Should().Be("FAIL"); b.Count("LOGS", "STRUCTURAL.LOG_BODY").Should().Be(1); b.Count("LOGS", "RAW_RESPONSE").Should().Be(0);
        File.WriteAllText(s.Log, CleanLog());

        s.AppendLog("trce: System.Net.Http.HttpClient.MercadoLivre.Api.ClientHandler[101]", "      Received HTTP response headers after 12.3ms - 200");
        var c = await RunAsync(s, Surfaces(s, database: false, api: false));
        c.Outcome.Should().Be("FAIL"); c.Count("LOGS", "STRUCTURAL.HTTPCLIENT_LOG").Should().Be(1); c.OnlyNonZero("LOGS", "STRUCTURAL.HTTPCLIENT_LOG");
    }

    // ---------- ST-07 ----------

    [Theory]
    [InlineData("""{"account":{"id":"safe-test-account","provider":{"diagnostics":{"raw_payload":{"id":"MLB_TEST_001","status":"active"}}}}}""")]
    [InlineData("""{"items":[{"diagnostics":{"response_body":{"synthetic":true}}}]}""")]
    [InlineData("""{"pages":[[{"x":{"rawBody":"st07-synthetic"}}]]}""")]
    public async Task ST07_a_forbidden_property_is_found_at_any_depth_including_arrays_of_arrays(string json)
    {
        using var s = new Scenario(); s.Api("leak.json", json);
        var result = await RunAsync(s, Surfaces(s, log: false, database: false));
        result.Outcome.Should().Be("FAIL"); result.Count("API", "STRUCTURAL.API_PROPERTY").Should().Be(1, "a root-only walker would report 0");
    }

    // ---------- ST-08 ----------

    [Theory]
    [InlineData("verce", true, "DATABASE_TARGET_FORBIDDEN")]
    [InlineData("VERCE", true, "DATABASE_TARGET_FORBIDDEN")]
    [InlineData("verce_other", true, "DATABASE_TARGET_NOT_DISPOSABLE")]
    [InlineData("verce_s8c2_live", false, "DISPOSABLE_CONFIRMATION_REQUIRED")]
    public async Task ST08_a_forbidden_or_unconfirmed_database_is_refused_before_any_docker_invocation(string database, bool confirm, string code)
    {
        using var s = new Scenario();
        var result = await RunAsync(s, GuardArguments(s, "st-disposable-pg", database, confirm));
        result.Outcome.Should().Be("REFUSED"); result.ExitCode.Should().Be(2); result.ErrorCode.Should().Be(code); result.Surfaces.Should().BeEmpty();
        s.ShimInvocations.Should().Be(0, "no inspect, no exec, no psql, no SQL");
    }

    // ---------- ST-09 ----------

    [Theory]
    [InlineData("verce-postgres")]
    [InlineData("VERCE-POSTGRES")]
    [InlineData("/verce-postgres")]
    public async Task ST09_the_operational_container_name_is_refused_with_zero_docker_invocations(string container)
    {
        using var s = new Scenario();
        var result = await RunAsync(s, GuardArguments(s, container, LeakScanFixture.DatabaseName));
        result.Outcome.Should().Be("REFUSED"); result.ErrorCode.Should().Be("OPERATIONAL_CONTAINER_TARGET_FORBIDDEN"); result.Surfaces.Should().BeEmpty();
        s.ShimInvocations.Should().Be(0);
    }

    [Theory]
    [InlineData("1a2b3c4d5e6f", "/verce-postgres")]
    [InlineData("verce-db-alias", "/VERCE-POSTGRES")]
    public async Task ST09_an_id_or_alias_that_resolves_to_the_operational_container_is_refused_after_exactly_one_inspect(string reference, string resolvedName)
    {
        using var s = new Scenario();
        var result = await RunAsync(s, GuardArguments(s, reference, LeakScanFixture.DatabaseName), new() { ["LEAKSCAN_SHIM_INSPECT_NAME"] = resolvedName });
        result.Outcome.Should().Be("REFUSED"); result.ErrorCode.Should().Be("OPERATIONAL_CONTAINER_TARGET_FORBIDDEN"); result.Surfaces.Should().BeEmpty();
        s.ShimInvocations.Should().Be(1); s.ShimLines.Single().Should().StartWith("inspect");
        s.ShimLines.Should().NotContain(l => l.Contains("exec") || l.Contains("psql"));
    }

    // ---------- ST-10 ----------

    [Fact]
    public async Task ST10_no_run_of_ST02_to_ST09_echoes_a_secret_or_a_planted_value_or_writes_a_file()
    {
        // RunAsync enforces the no-echo, empty-stderr, frozen-schema and no-new-file contract on EVERY invocation; this test drives one run per leaking scenario.
        using var s = new Scenario(); var seen = new List<string>();
        foreach (var (kind, content) in new[] { ("log", $"info: Foo[0]\n      Authorization: Basic {BasicHeaderLiteral}"), ("log", "info: Foo[0]\n      Bearer st03SyntheticTokenValue0001"), ("log", $"info: Foo[0]\n      {LeakScanFixture.Secret}"),
                     ("log", "info: Verce.Infrastructure.Marketplaces.X[0]\n      {\"id\":\"MLB_TEST_001\",\"title\":\"Synthetic Listing\"}"), ("api", """{"pages":[[{"x":{"rawBody":"st07-synthetic"}}]]}"""), ("api", """{"note":"TG-abcdef123456","authorization":"st02-synthetic"}""") })
        {
            File.WriteAllText(s.Log, CleanLog()); File.Delete(Path.Combine(s.ApiDir, "leak.json"));
            if (kind == "log") s.AppendLog(content.Split('\n')); else s.Api("leak.json", content);
            var result = await RunAsync(s, Surfaces(s, database: false));
            result.Outcome.Should().Be("FAIL"); seen.Add(result.Stdout);
        }
        var refused = await RunAsync(s, GuardArguments(s, "verce-postgres", "verce")); seen.Add(refused.Stdout);
        seen.Should().OnlyContain(text => NeverEchoed.All(literal => !text.Contains(literal)));
    }

    // ---------- additional negatives and false-pass prevention ----------

    [Fact]
    public async Task Invalid_json_in_a_saved_response_is_input_invalid_never_a_pass()
    {
        using var s = new Scenario(); s.Api("broken.json", """{"items":[{"a":1}""");
        var result = await RunAsync(s, Surfaces(s, log: false, database: false));
        result.Outcome.Should().Be("INPUT_INVALID"); result.ErrorCode.Should().Be("API_RESPONSE_NOT_JSON"); result.ExitCode.Should().Be(2);
    }

    [Fact]
    public async Task A_root_array_and_an_empty_valued_sensitive_property_are_both_failures_but_a_benign_name_is_not()
    {
        using var s = new Scenario();
        s.Api("leak.json", """[[{"inner":{"token":""}}]]""");
        var empty = await RunAsync(s, Surfaces(s, log: false, database: false));
        empty.Outcome.Should().Be("FAIL"); empty.Count("API", "STRUCTURAL.API_PROPERTY").Should().Be(1, "the property is forbidden whatever its value");
        s.Api("leak.json", """{"authorizationState":"CONNECTED","authorizationInProgress":false,"requiredAction":"NONE","tokenKind":"none","emailVerified":true}""");
        AssertClean(await RunAsync(s, Surfaces(s, log: false, database: false)), log: false, database: false);
    }

    [Fact]
    public async Task A_raw_authorization_header_with_a_bearer_value_in_a_log_fails_both_categories()
    {
        using var s = new Scenario(); s.AppendLog("dbug: Foo[0]", "      Authorization: Bearer st03SyntheticTokenValue0001");
        var result = await RunAsync(s, Surfaces(s, database: false, api: false));
        result.Outcome.Should().Be("FAIL"); result.Count("LOGS", "AUTH_HEADER").Should().BeGreaterThanOrEqualTo(1); result.Count("LOGS", "BEARER").Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task An_exception_message_carrying_the_client_secret_fails_even_across_lines()
    {
        using var s = new Scenario();
        s.AppendLog("fail: Verce.Api.Something[0]", "      Unhandled exception while exchanging credentials", $"System.InvalidOperationException: rejected value {LeakScanFixture.Secret}", "   at Verce.Api.Something.Run()");
        var result = await RunAsync(s, Surfaces(s, database: false, api: false));
        result.Outcome.Should().Be("FAIL"); result.Count("LOGS", "CLIENT_SECRET").Should().Be(1);
    }

    [Fact]
    public async Task A_database_whose_container_identity_cannot_be_proven_is_refused_and_nothing_is_executed()
    {
        using var s = new Scenario();
        var unproven = await RunAsync(s, GuardArguments(s, "st-disposable-pg", LeakScanFixture.DatabaseName)); // the shim has no scripted name: inspect fails
        unproven.Outcome.Should().Be("REFUSED"); unproven.ErrorCode.Should().Be("CONTAINER_IDENTITY_UNPROVEN"); unproven.Surfaces.Should().BeEmpty();
        s.ShimInvocations.Should().Be(1); s.ShimLines.Should().OnlyContain(l => l.StartsWith("inspect"));

        var missing = await RunAsync(s, ["-Database", LeakScanFixture.DatabaseName, "-ConfirmDisposable", "-DockerExecutable", LeakScanFixture.ShimPath]);
        missing.Outcome.Should().Be("REFUSED"); missing.ErrorCode.Should().Be("CONTAINER_IDENTITY_UNPROVEN");
        s.ShimInvocations.Should().Be(1, "no further docker call for the second run");

        var absent = await RunAsync(s, ["-PostgresContainer", "no-such-container-" + Guid.NewGuid().ToString("N")[..8], "-Database", LeakScanFixture.DatabaseName, "-ConfirmDisposable", "-UserSecretsFile", s.Secrets]);
        absent.Outcome.Should().Be("REFUSED"); absent.ErrorCode.Should().Be("CONTAINER_IDENTITY_UNPROVEN");
    }

    [Fact]
    public async Task Missing_paths_unreadable_files_and_malformed_invocations_are_input_invalid_with_exit_code_2()
    {
        using var s = new Scenario();
        async Task<Result> Invalid(params string[] args) { var r = await RunAsync(s, args); r.Outcome.Should().Be("INPUT_INVALID"); r.ExitCode.Should().Be(2); r.Surfaces.Should().BeEmpty(); return r; }

        (await Invalid("-LogFile", Path.Combine(s.Root, "missing.log"), "-UserSecretsFile", s.Secrets)).ErrorCode.Should().Be("LOG_FILE_NOT_FOUND");
        (await Invalid("-ApiResponseDirectory", Path.Combine(s.Root, "missing"), "-UserSecretsFile", s.Secrets)).ErrorCode.Should().Be("API_DIRECTORY_NOT_FOUND");
        (await Invalid("-UserSecretsFile", s.Secrets)).ErrorCode.Should().Be("NO_SURFACE_SELECTED");
        (await Invalid("-LogFile", s.Log)).ErrorCode.Should().Be("USER_SECRETS_FILE_REQUIRED");
        (await Invalid("-LogFile", s.Log, "-UserSecretsFile", s.Secrets, "-Bogus", "x")).ErrorCode.Should().Be("UNKNOWN_ARGUMENT");
        (await Invalid("-LogFile", s.Log, "-UserSecretsFile", Path.Combine(s.Root, "nope.json"))).ErrorCode.Should().Be("USER_SECRETS_UNREADABLE");

        File.WriteAllText(Path.Combine(s.Root, "empty.log"), "");
        (await Invalid("-LogFile", Path.Combine(s.Root, "empty.log"), "-UserSecretsFile", s.Secrets)).ErrorCode.Should().Be("LOG_FILE_EMPTY");
        var emptyApi = Directory.CreateDirectory(Path.Combine(s.Root, "empty-api")).FullName;
        (await Invalid("-ApiResponseDirectory", emptyApi, "-UserSecretsFile", s.Secrets)).ErrorCode.Should().Be("API_DIRECTORY_EMPTY");

        File.WriteAllText(Path.Combine(s.Root, "short.json"), """{"Marketplaces:MercadoLivre:ClientSecret":"abc"}""");
        (await Invalid("-LogFile", s.Log, "-UserSecretsFile", Path.Combine(s.Root, "short.json"))).ErrorCode.Should().Be("CLIENT_SECRET_TOO_SHORT");
        File.WriteAllText(Path.Combine(s.Root, "nokey.json"), """{"Other":"value-without-the-key"}""");
        (await Invalid("-LogFile", s.Log, "-UserSecretsFile", Path.Combine(s.Root, "nokey.json"))).ErrorCode.Should().Be("CLIENT_SECRET_NOT_FOUND");
    }

    [Fact]
    public async Task A_read_error_during_the_scan_is_input_invalid_and_never_a_pass()
    {
        using var s = new Scenario();
        await using var exclusive = new FileStream(s.Log, FileMode.Open, FileAccess.ReadWrite, FileShare.None); // the log is held open exclusively
        var result = await RunAsync(s, Surfaces(s, database: false, api: false));
        result.Outcome.Should().Be("INPUT_INVALID"); result.ErrorCode.Should().Be("LOG_FILE_UNREADABLE"); result.ExitCode.Should().Be(2);
    }

    [Fact]
    public async Task The_nested_secret_in_a_decoded_json_string_is_found_even_when_the_raw_text_escapes_it()
    {
        using var s = new Scenario();
        s.Api("leak.json", "{\"note\":\"\\u0053T04-synthetic-client-secret-7f3a\"}"); // \u0053 = 'S': the raw file never contains the secret contiguously
        var result = await RunAsync(s, Surfaces(s, log: false, database: false));
        result.Outcome.Should().Be("FAIL"); result.Count("API", "CLIENT_SECRET").Should().Be(1);
    }

    // ---------- database helpers ----------

    private Task PlantAuditAsync(Guid id, string newValuesJson) => db.ExecuteAsync("""
        INSERT INTO platform.audit_log (id,occurred_at,operation_started_at,user_id,user_display_name,entity_schema,entity_table,entity_id,operation,changed_columns,old_values_json,new_values_json,correlation_id,request_id,wave_index,source)
        VALUES (@id,now(),now(),NULL,'Planted','commerce','marketplace_listing',gen_random_uuid(),'UPDATE',ARRAY['x'],NULL,@json,gen_random_uuid(),NULL,1,'Api')
        """, ("id", id), ("json", newValuesJson));

    private Task RemoveAuditAsync(Guid id) => db.ExecuteAsync("SET session_replication_role = replica; DELETE FROM platform.audit_log WHERE id = @id; RESET session_replication_role;", ("id", id));

    private async Task<string> TextAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
