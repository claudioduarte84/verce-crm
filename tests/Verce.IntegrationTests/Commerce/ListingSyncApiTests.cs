using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Verce.Api.Auth;
using Verce.Api.Commerce;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Commerce;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.Commerce;

/// <summary>S8C.2 Stream C1: the listing-sync HTTP API (ADR-0025 §J, ADR-0027). The registry is a test double with a listing reader for every provider; no
/// provider, OAuth or network call is possible, and the dispatch job is not registered, so created runs stay QUEUED.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ListingSyncApiTests : IAsyncLifetime
{
    private const string Prefix = "/api/commerce";
    private static readonly string[] ForbiddenNames = ["accesstoken", "refreshtoken", "credentialreference", "authorizationoperationid", "confirmedoperationidatstart", "leasetoken", "leaseuntil", "heartbeatat", "scrollid", "rawpayload", "providererror", "providererrorcode", "authorization"];

    private readonly PostgresFixture _fixture;
    private readonly CapturingLoggerProvider _logs = new();
    private readonly StubRegistry _registry = new();
    private VerceWebApplicationFactory _factory = null!;

    public ListingSyncApiTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, configureTestServices: services =>
        {
            services.AddSingleton<IMarketplaceConnectorRegistry>(_registry);
            services.AddSingleton<ILoggerProvider>(_logs);
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await CleanupOwnedRowsAsync();
    }

    // ---------- ownership inventory: every row this class creates is recorded by id and removed by id; nothing is removed by a broad predicate ----------
    private readonly List<Guid> _accounts = [];   // marketplace accounts (+ their connection, capabilities, account-bound operations)
    private readonly List<Guid> _channels = [];   // sales channels created for those accounts
    private readonly List<Guid> _users = [];      // identities created by LoggedInAsAsync
    private readonly List<Guid> _rolesCreated = []; // roles this class had to create (removed only while no user is in them)
    private readonly List<Guid> _operations = []; // account-null (provider-wide) operations inserted by tests
    private readonly HashSet<Guid> _runs = [];    // discovered from owned accounts at cleanup time, before deletion
    private readonly HashSet<Guid> _listings = [];

    private Guid[] OwnedAuditEntityIds() => [.. _accounts, .. _channels, .. _users, .. _operations, .. _runs, .. _listings];

    private async Task CollectOwnedChildIdsAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("""
            SELECT 'run', id FROM commerce.marketplace_listing_sync_run WHERE marketplace_account_id = ANY(@accounts)
            UNION ALL SELECT 'listing', id FROM commerce.marketplace_listing WHERE marketplace_account_id = ANY(@accounts)
            UNION ALL SELECT 'operation', id FROM commerce.marketplace_account_operation WHERE marketplace_account_id = ANY(@accounts)
            """, connection);
        command.Parameters.AddWithValue("accounts", _accounts.ToArray());
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { var id = reader.GetGuid(1); switch (reader.GetString(0)) { case "run": _runs.Add(id); break; case "listing": _listings.Add(id); break; default: _operations.Add(id); break; } }
    }

    /// <summary>Deletes exactly the recorded rows (idempotent; safe after partial setup). Triggers are bypassed only for this session so FK order never matters.</summary>
    private async Task CleanupOwnedRowsAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString); await connection.OpenAsync();
        await CollectOwnedChildIdsAsync(connection);
        if (OwnedAuditEntityIds().Length == 0 && _rolesCreated.Count == 0) return;
        await using var command = new NpgsqlCommand("""
            SET session_replication_role = replica;
            DELETE FROM platform.audit_log WHERE user_id = ANY(@users) OR entity_id = ANY(@owned);
            DELETE FROM commerce.marketplace_listing_sync_run_item WHERE sync_run_id = ANY(@runs);
            DELETE FROM commerce.marketplace_listing_sync_run WHERE id = ANY(@runs);
            DELETE FROM commerce.marketplace_listing_observation WHERE marketplace_listing_id = ANY(@listings);
            DELETE FROM commerce.marketplace_listing WHERE id = ANY(@listings);
            DELETE FROM commerce.marketplace_account_capability WHERE marketplace_account_id = ANY(@accounts);
            DELETE FROM commerce.marketplace_account_connection WHERE marketplace_account_id = ANY(@accounts);
            DELETE FROM commerce.marketplace_account_operation WHERE id = ANY(@ops);
            DELETE FROM commerce.marketplace_account WHERE id = ANY(@accounts);
            DELETE FROM pricing.sales_channel WHERE id = ANY(@channels);
            DELETE FROM platform.user_role WHERE user_id = ANY(@users);
            DELETE FROM platform.user_claim WHERE user_id = ANY(@users);
            DELETE FROM platform.user_login WHERE user_id = ANY(@users);
            DELETE FROM platform.user_token WHERE user_id = ANY(@users);
            DELETE FROM platform.account_setup_token WHERE user_id = ANY(@users);
            DELETE FROM platform."user" WHERE id = ANY(@users);
            DELETE FROM platform.role_claim WHERE role_id = ANY(@roles) AND NOT EXISTS (SELECT 1 FROM platform.user_role ur WHERE ur.role_id = platform.role_claim.role_id);
            DELETE FROM platform.role WHERE id = ANY(@roles) AND NOT EXISTS (SELECT 1 FROM platform.user_role ur WHERE ur.role_id = platform.role.id);
            RESET session_replication_role;
            """, connection);
        command.Parameters.AddWithValue("users", _users.ToArray()); command.Parameters.AddWithValue("owned", OwnedAuditEntityIds()); command.Parameters.AddWithValue("runs", _runs.ToArray());
        command.Parameters.AddWithValue("listings", _listings.ToArray()); command.Parameters.AddWithValue("accounts", _accounts.ToArray()); command.Parameters.AddWithValue("ops", _operations.ToArray());
        command.Parameters.AddWithValue("channels", _channels.ToArray()); command.Parameters.AddWithValue("roles", _rolesCreated.ToArray());
        await command.ExecuteNonQueryAsync();
    }

    private sealed record OwnedResidue(long Audit, long Users, long Roles, long Accounts, long Operations, long Runs, long Items, long Listings, long Channels)
    { public long Total => Audit + Users + Roles + Accounts + Operations + Runs + Items + Listings + Channels; }

    private async Task<OwnedResidue> OwnedResidueAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM platform.audit_log WHERE user_id = ANY(@users) OR entity_id = ANY(@owned)),
                   (SELECT count(*) FROM platform."user" WHERE id = ANY(@users)),
                   (SELECT count(*) FROM platform.role WHERE id = ANY(@roles) AND NOT EXISTS (SELECT 1 FROM platform.user_role ur WHERE ur.role_id = platform.role.id)),
                   (SELECT count(*) FROM commerce.marketplace_account WHERE id = ANY(@accounts)) + (SELECT count(*) FROM commerce.marketplace_account_connection WHERE marketplace_account_id = ANY(@accounts)),
                   (SELECT count(*) FROM commerce.marketplace_account_operation WHERE id = ANY(@ops) OR marketplace_account_id = ANY(@accounts)),
                   (SELECT count(*) FROM commerce.marketplace_listing_sync_run WHERE id = ANY(@runs) OR marketplace_account_id = ANY(@accounts)),
                   (SELECT count(*) FROM commerce.marketplace_listing_sync_run_item WHERE sync_run_id = ANY(@runs)),
                   (SELECT count(*) FROM commerce.marketplace_listing WHERE id = ANY(@listings) OR marketplace_account_id = ANY(@accounts)),
                   (SELECT count(*) FROM pricing.sales_channel WHERE id = ANY(@channels))
            """, connection);
        command.Parameters.AddWithValue("users", _users.ToArray()); command.Parameters.AddWithValue("owned", OwnedAuditEntityIds()); command.Parameters.AddWithValue("roles", _rolesCreated.ToArray());
        command.Parameters.AddWithValue("accounts", _accounts.ToArray()); command.Parameters.AddWithValue("ops", _operations.ToArray()); command.Parameters.AddWithValue("runs", _runs.ToArray());
        command.Parameters.AddWithValue("listings", _listings.ToArray()); command.Parameters.AddWithValue("channels", _channels.ToArray());
        await using var reader = await command.ExecuteReaderAsync(); await reader.ReadAsync();
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8));
    }
    private sealed class StubRegistry : IMarketplaceConnectorRegistry
    {
        public bool ReaderRegistered { get; set; } = true;
        public bool TryGet(string providerCode, out IMarketplaceAuthorizationConnector connector) { connector = null!; return false; }
        public bool TryGetListingReader(string providerCode, out IMarketplaceListingReader reader) { reader = new NeverCalledReader(); return ReaderRegistered; }
    }

    private sealed class NeverCalledReader : IMarketplaceListingReader
    {
        public string ProviderCode => "MERCADO_LIVRE";
        public Task<MarketplaceListingPage> ReadListingPageAsync(MarketplaceListingPageRequest request, CancellationToken ct) => throw new InvalidOperationException("no provider call is allowed from the HTTP tests");
        public Task<IReadOnlyList<MarketplaceListingDetailResult>> ReadListingDetailsAsync(MarketplaceListingDetailRequest request, CancellationToken ct) => throw new InvalidOperationException("no provider call is allowed from the HTTP tests");
    }

    // ---------- fixtures ----------

    private VerceDbContext Db() => new(new DbContextOptionsBuilder<VerceDbContext>().UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention().Options);

    private async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync();
    }

    private async Task<Guid> SeedAccountAsync()
    {
        var account = Guid.NewGuid(); var channel = Guid.NewGuid(); var operation = Guid.NewGuid(); var code = "C1" + account.ToString("N")[..10];
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO pricing.sales_channel (id,code,name,kind,active,created_at,version) VALUES (@channel,@code,@code,'Marketplace',true,now(),1);
            INSERT INTO commerce.marketplace_provider (code,name) VALUES ('MERCADO_LIVRE','Mercado Livre') ON CONFLICT DO NOTHING;
            INSERT INTO commerce.marketplace_account (id,provider_code,external_account_id,sales_channel_id,display_name,active,credential_reference,sync_state,created_at,version) VALUES (@account,'MERCADO_LIVRE',@code,@channel,@code,true,'test-ref','NEVER_SYNCED',now(),1);
            INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,candidate_credential_reference,candidate_credential_version,created_at,updated_at,decided_at,cleanup_attempt_count,version) VALUES (@operation,'CONNECT_NEW','SECRET_PERSISTED','CONFIRMED','DONE',@account,'MERCADO_LIVRE','test-ref',1,now() - interval '30 days',now() - interval '30 days',now() - interval '30 days',0,1);
            INSERT INTO commerce.marketplace_account_connection (marketplace_account_id,authorization_state,runtime_availability,confirmed_credential_version,confirmed_operation_id,access_expires_at,created_at,updated_at) VALUES (@account,'CONNECTED','AVAILABLE',1,@operation,now() + interval '1 hour',now(),now());
            INSERT INTO commerce.marketplace_account_capability (marketplace_account_id,capability_code,state,source,updated_at) VALUES (@account,'LISTINGS_READ','GRANTED','TEST',now());
            """, connection);
        _accounts.Add(account); _channels.Add(channel);
        command.Parameters.AddWithValue("channel", channel); command.Parameters.AddWithValue("account", account); command.Parameters.AddWithValue("operation", operation); command.Parameters.AddWithValue("code", code);
        await command.ExecuteNonQueryAsync(); return account;
    }

    /// <summary>Creates a terminal run through the certified kernel: <paramref name="found"/> FOUND items, <paramref name="pending"/> items left PENDING (turned into
    /// TRANSIENT_ERROR(stop) when a stop reason is given) and optional blocking item outcomes. Finishing time is requestedAt + 1 minute.</summary>
    private async Task<Guid> SeedRunAsync(Guid account, DateTimeOffset requestedAt, int found = 0, int pending = 0, string? stopReason = null, bool enumerationCompleted = true, params (MarketplaceListingSyncItemOutcome Outcome, string Code)[] blocking)
    {
        var lease = TimeSpan.FromMinutes(2); await using var db = Db(); var kernel = new MarketplaceListingSyncRunKernel(db);
        var run = await kernel.CreateFullAsync(account, Guid.NewGuid(), requestedAt); var claimed = (await new MarketplaceListingSyncLeaseAuthority(db).ClaimAsync(run.Id, requestedAt, lease))!;
        var tag = run.Id.ToString("N")[^10..]; var foundIds = Enumerable.Range(0, found).Select(i => $"MLB-{tag}-F{i:00}").ToArray(); var blockIds = blocking.Select((_, i) => $"MLB-{tag}-B{i:00}").ToArray(); var pendingIds = Enumerable.Range(0, pending).Select(i => $"MLB-{tag}-P{i:00}").ToArray();
        await kernel.RegisterTargetsWithLeaseAsync(run.Id, claimed.LeaseToken, foundIds.Concat(blockIds).Concat(pendingIds), MarketplaceListingSyncItemOrigin.ENUMERATED, requestedAt, lease);
        if (enumerationCompleted) await kernel.MarkEnumerationCompletedWithLeaseAsync(run.Id, claimed.LeaseToken, requestedAt, lease);
        var items = await db.Set<MarketplaceListingSyncRunItem>().Where(x => x.SyncRunId == run.Id).ToDictionaryAsync(x => x.ExternalListingId);
        foreach (var id in foundIds)
        {
            var listing = new MarketplaceListing(account, id, null, MarketplaceListingStatus.ACTIVE); db.Add(listing); await db.SaveChangesAsync();
            await kernel.RecordOutcomeWithLeaseAsync(run.Id, claimed.LeaseToken, items[id].Id, MarketplaceListingSyncItemOutcome.FOUND, null, listing.Id, true, true, requestedAt, lease);
        }
        for (var i = 0; i < blocking.Length; i++) await kernel.RecordOutcomeWithLeaseAsync(run.Id, claimed.LeaseToken, items[blockIds[i]].Id, blocking[i].Outcome, blocking[i].Code, null, false, false, requestedAt, lease);
        await kernel.FinalizeWithLeaseAsync(run.Id, claimed.LeaseToken, stopReason, requestedAt.AddMinutes(1), lease);
        return run.Id;
    }

    private Task SetSafeResultCodeAsync(Guid run, string code) => SqlAsync($"UPDATE commerce.marketplace_listing_sync_run SET safe_result_code = '{code}' WHERE id = '{run}'");

    private async Task<AuthTestClient> LoggedInAsAsync(string role)
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test"; const string password = "a-perfectly-fine-12char-password";
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(); var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!await roles.RoleExistsAsync(role)) { var created = new ApplicationRole(role); (await roles.CreateAsync(created)).Succeeded.Should().BeTrue(); _rolesCreated.Add(created.Id); }
        var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = role + " Sync", IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow };
        _users.Add(user.Id); (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue(); (await users.AddToRoleAsync(user, role)).Succeeded.Should().BeTrue();
        var client = new AuthTestClient(_factory.CreateHttpsClient()); await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new LoginRequest(email, password))).StatusCode.Should().Be(HttpStatusCode.NoContent); await client.EnsureCsrfCookieAsync();
        return client;
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    private static string? Str(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static void AssertNoForbiddenNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject()) { ForbiddenNames.Should().NotContain(property.Name.ToLowerInvariant(), "the API must never serialize internal/credential fields"); AssertNoForbiddenNames(property.Value); }
                break;
            case JsonValueKind.Array: foreach (var item in element.EnumerateArray()) AssertNoForbiddenNames(item); break;
        }
    }

    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddDays(-2);

    // ---------- permission matrix (all five operations) ----------

    [Theory]
    [InlineData(Roles.Owner, true)]
    [InlineData(Roles.Operator, true)]
    [InlineData(Roles.Viewer, false)]
    public async Task C1_permission_matrix_covers_all_five_operations(string role, bool mayMutate)
    {
        var client = await LoggedInAsAsync(role); var account = await SeedAccountAsync(); var run = await SeedRunAsync(account, Base, found: 1, blocking: (MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR, "LISTING_READ_FAILED"));
        // reads: every role
        foreach (var path in new[] { $"{Prefix}/marketplace-accounts/{account}/listing-sync", $"{Prefix}/marketplace-listing-syncs/{run}", $"{Prefix}/marketplace-listing-syncs/{run}/items" })
            (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK, $"{role} reads {path}");
        // retry before start (start would make a run active)
        var retry = await client.PostRawAsync($"{Prefix}/marketplace-listing-syncs/{run}/retry", null);
        retry.StatusCode.Should().Be(mayMutate ? HttpStatusCode.Accepted : HttpStatusCode.Forbidden);
        if (mayMutate) await SqlAsync($"UPDATE commerce.marketplace_listing_sync_run SET status='FAILED', safe_result_code='TEST_DONE', finished_at=now(), lease_token=NULL, lease_until=NULL WHERE marketplace_account_id='{account}' AND status IN ('QUEUED','RUNNING')");
        var start = await client.PostRawAsync($"{Prefix}/marketplace-accounts/{account}/listing-syncs", null);
        start.StatusCode.Should().Be(mayMutate ? HttpStatusCode.Accepted : HttpStatusCode.Forbidden);
    }

    // ---------- start ----------

    [Fact]
    public async Task C1_start_returns_202_with_a_safe_run_dto_and_never_waits_for_completion()
    {
        var client = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync();
        var response = await client.PostRawAsync($"{Prefix}/marketplace-accounts/{account}/listing-syncs", null);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted); using var json = await JsonAsync(response); var root = json.RootElement;
        response.Headers.Location!.ToString().Should().EndWith(root.GetProperty("runId").GetString()!);
        Str(root, "kind").Should().Be("FULL"); Str(root, "status").Should().Be("QUEUED"); Str(root, "requiredAction").Should().Be("NONE"); Str(root, "accountId").Should().Be(account.ToString());
        root.GetProperty("attemptCount").GetInt32().Should().Be(0); root.GetProperty("counters").GetProperty("total").GetInt32().Should().Be(0);
        root.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo(["runId", "accountId", "kind", "status", "phase", "rootRunId", "retryOfRunId", "requestedAt", "startedAt", "finishedAt", "attemptCount", "enumerationCompleted", "counters", "safeResultCode", "requiredAction"]);
        AssertNoForbiddenNames(root);
        await using var db = Db(); (await db.Set<MarketplaceListingSyncRun>().CountAsync(x => x.MarketplaceAccountId == account)).Should().Be(1);
    }

    [Fact]
    public async Task C1_start_rejects_missing_antiforgery_and_unknown_account_and_any_request_body()
    {
        var client = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync();
        (await client.PostRawAsync($"{Prefix}/marketplace-accounts/{account}/listing-syncs", null, withAntiforgery: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsync($"{Prefix}/marketplace-accounts/{account}/listing-syncs", new { externalListingIds = new[] { "MLB1" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostRawAsync($"{Prefix}/marketplace-accounts/{Guid.NewGuid()}/listing-syncs", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using var db = Db(); (await db.Set<MarketplaceListingSyncRun>().CountAsync(x => x.MarketplaceAccountId == account)).Should().Be(0);
    }

    [Fact]
    public async Task C1_start_active_run_conflict_returns_409_with_the_active_run_id()
    {
        var client = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync();
        using var first = await JsonAsync(await client.PostRawAsync($"{Prefix}/marketplace-accounts/{account}/listing-syncs", null)); var winner = first.RootElement.GetProperty("runId").GetString();
        var second = await client.PostRawAsync($"{Prefix}/marketplace-accounts/{account}/listing-syncs", null);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict); using var json = await JsonAsync(second);
        Str(json.RootElement, "code").Should().Be("LISTING_SYNC_ALREADY_ACTIVE"); Str(json.RootElement, "blockedReason").Should().Be("SYNC_ACTIVE"); Str(json.RootElement, "requiredAction").Should().Be("NONE"); Str(json.RootElement, "activeRunId").Should().Be(winner);
        second.Headers.Contains("Retry-After").Should().BeFalse();
    }

    [Theory]
    [InlineData("UPDATE commerce.marketplace_account SET active=false", 409, "MARKETPLACE_ACCOUNT_INACTIVE", "ACCOUNT_INACTIVE", "NONE")]
    [InlineData("UPDATE pricing.sales_channel SET active=false", 422, "MARKETPLACE_ACCOUNT_CHANNEL_INVALID", "CHANNEL_INVALID", "NONE")]
    [InlineData("--reader-missing", 409, "MARKETPLACE_PROVIDER_NOT_SUPPORTED", "PROVIDER_NOT_CONFIGURED", "CONTACT_ADMIN")]
    [InlineData("UPDATE commerce.marketplace_provider_capability SET state='UNKNOWN' WHERE capability_code='LISTINGS_READ'", 409, "COMMERCE_CAPABILITY_UNAVAILABLE", "CAPABILITY_UNSUPPORTED", "NONE")]
    [InlineData("UPDATE commerce.marketplace_account_capability SET state='UNKNOWN' WHERE marketplace_account_id='@account'", 409, "COMMERCE_CAPABILITY_UNAVAILABLE", "GRANT_UNKNOWN", "NONE")]
    [InlineData("UPDATE commerce.marketplace_account_capability SET state='DENIED' WHERE marketplace_account_id='@account'", 409, "COMMERCE_CAPABILITY_UNAVAILABLE", "GRANT_DENIED", "CHECK_PROVIDER_ACCOUNT")]
    [InlineData("UPDATE commerce.marketplace_account_connection SET authorization_state='REAUTHORIZATION_REQUIRED' WHERE marketplace_account_id='@account'", 409, "MARKETPLACE_AUTHORIZATION_NOT_USABLE", "AUTHORIZATION_NOT_USABLE", "REAUTHORIZE")]
    [InlineData("UPDATE commerce.marketplace_account_connection SET authorization_state='REAUTHORIZATION_REQUIRED', safe_failure_code='PROVIDER_APP_CREDENTIALS_INVALID' WHERE marketplace_account_id='@account'", 409, "MARKETPLACE_AUTHORIZATION_NOT_USABLE", "AUTHORIZATION_NOT_USABLE", "CONTACT_ADMIN")]
    [InlineData("UPDATE commerce.marketplace_account_connection SET authorization_state='REAUTHORIZATION_REQUIRED', safe_failure_code='PROBE_CREDENTIAL_UNREADABLE' WHERE marketplace_account_id='@account'", 409, "MARKETPLACE_AUTHORIZATION_NOT_USABLE", "AUTHORIZATION_NOT_USABLE", "REAUTHORIZE_AFTER_STORE_LOSS")]
    [InlineData("UPDATE commerce.marketplace_account_connection SET authorization_state='REVOKED' WHERE marketplace_account_id='@account'", 409, "MARKETPLACE_AUTHORIZATION_NOT_USABLE", "AUTHORIZATION_NOT_USABLE", "AUTHORIZE")]
    [InlineData("INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,created_at,updated_at,cleanup_attempt_count,version) VALUES (gen_random_uuid(),'REFRESH','EXTERNAL_IN_FLIGHT','PENDING','NOT_REQUIRED','@account','MERCADO_LIVRE',now(),now(),0,1)", 409, "MARKETPLACE_AUTHORIZATION_NOT_USABLE", "AUTHORIZATION_NOT_USABLE", "NONE")]
    [InlineData("--provider-fence", 409, "MARKETPLACE_AUTHORIZATION_NOT_USABLE", "AUTHORIZATION_NOT_USABLE", "NONE")]
    [InlineData("UPDATE commerce.marketplace_account_connection SET runtime_retry_after_until=now() + interval '100 seconds' WHERE marketplace_account_id='@account'", 503, "COMMERCE_OPERATION_UNAVAILABLE", "RATE_LIMITED", "NONE")]
    [InlineData("UPDATE commerce.marketplace_account_connection SET runtime_availability='UNAVAILABLE' WHERE marketplace_account_id='@account'", 503, "COMMERCE_OPERATION_UNAVAILABLE", "RUNTIME_UNAVAILABLE", "CHECK_PROVIDER_ACCOUNT")]
    public async Task C1_availability_and_start_refusals_follow_the_frozen_http_and_required_action_mapping(string mutation, int status, string code, string reason, string action)
    {
        var client = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync(); var viewer = await LoggedInAsAsync(Roles.Viewer);
        _registry.ReaderRegistered = mutation != "--reader-missing"; Guid? fenceId = null;
        try
        {
            if (mutation == "--provider-fence") { fenceId = Guid.NewGuid(); _operations.Add(fenceId.Value); await SqlAsync($"INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,created_at,updated_at,cleanup_attempt_count,version) VALUES ('{fenceId}','CONNECT_NEW','EXTERNAL_IN_FLIGHT','PENDING','NOT_REQUIRED',NULL,'MERCADO_LIVRE',now(),now(),0,1)"); }
            else if (!mutation.StartsWith("--")) await SqlAsync(mutation.Replace("@account", account.ToString()));
            var start = await client.PostRawAsync($"{Prefix}/marketplace-accounts/{account}/listing-syncs", null);
            start.StatusCode.Should().Be((HttpStatusCode)status); using var problem = await JsonAsync(start);
            Str(problem.RootElement, "code").Should().Be(code); Str(problem.RootElement, "blockedReason").Should().Be(reason); Str(problem.RootElement, "requiredAction").Should().Be(action);
            AssertNoForbiddenNames(problem.RootElement);
            if (reason == "RATE_LIMITED")
            {
                start.Headers.GetValues("Retry-After").Single().Should().MatchRegex("^(100|101)$", "ceiling seconds until RetryAfterUntil"); problem.RootElement.TryGetProperty("retryAfterUntil", out _).Should().BeTrue();
            }
            else start.Headers.Contains("Retry-After").Should().BeFalse("no header without RetryAfterUntil");
            // The status route reports the SAME blockedReason / requiredAction through the shared evaluator.
            using var statusDoc = await JsonAsync(await viewer.GetAsync($"{Prefix}/marketplace-accounts/{account}/listing-sync")); var availability = statusDoc.RootElement.GetProperty("availability");
            availability.GetProperty("canStart").GetBoolean().Should().BeFalse(); Str(availability, "blockedReason").Should().Be(reason); Str(availability, "requiredAction").Should().Be(action);
            await using var db = Db(); (await db.Set<MarketplaceListingSyncRun>().CountAsync(x => x.MarketplaceAccountId == account)).Should().Be(0);
        }
        finally
        {
            _registry.ReaderRegistered = true;
            if (fenceId is { } owned) await SqlAsync($"DELETE FROM commerce.marketplace_account_operation WHERE id = '{owned}'"); // only the fence row this test inserted
            await SqlAsync("UPDATE commerce.marketplace_provider_capability SET state='SUPPORTED' WHERE provider_code='MERCADO_LIVRE' AND capability_code='LISTINGS_READ'");
        }
    }

    [Fact]
    public async Task C1_status_without_blockers_is_startable_with_action_none_and_viewer_can_read_unknown_is_404()
    {
        var viewer = await LoggedInAsAsync(Roles.Viewer); var account = await SeedAccountAsync();
        var response = await viewer.GetAsync($"{Prefix}/marketplace-accounts/{account}/listing-sync"); response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = await JsonAsync(response); var availability = json.RootElement.GetProperty("availability");
        availability.GetProperty("canStart").GetBoolean().Should().BeTrue(); Str(availability, "requiredAction").Should().Be("NONE"); availability.GetProperty("blockedReason").ValueKind.Should().Be(JsonValueKind.Null);
        var summary = json.RootElement.GetProperty("summary"); Str(summary, "health").Should().Be("NEVER_RUN"); summary.GetProperty("openBlockingItemCount").GetInt32().Should().Be(0);
        summary.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo(["activeRun", "latestRun", "latestFullRun", "lastSucceededFullRunAt", "openBlockingItemCount", "health"]);
        AssertNoForbiddenNames(json.RootElement);
        (await viewer.GetAsync($"{Prefix}/marketplace-accounts/{Guid.NewGuid()}/listing-sync")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task C1_status_summary_runs_carry_the_run_required_action()
    {
        var client = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync();
        var run = await SeedRunAsync(account, Base, found: 0, enumerationCompleted: false, stopReason: "PROVIDER_ACCESS_FORBIDDEN", pending: 1);
        using var json = await JsonAsync(await client.GetAsync($"{Prefix}/marketplace-accounts/{account}/listing-sync")); var summary = json.RootElement.GetProperty("summary");
        Str(summary.GetProperty("latestRun"), "runId").Should().Be(run.ToString()); Str(summary.GetProperty("latestRun"), "requiredAction").Should().Be("CHECK_PROVIDER_ACCOUNT"); Str(summary.GetProperty("latestFullRun"), "requiredAction").Should().Be("CHECK_PROVIDER_ACCOUNT");
        await AssertEmbeddedRunMatchesStandaloneAsync(client, summary, "latestRun"); await AssertEmbeddedRunMatchesStandaloneAsync(client, summary, "latestFullRun"); summary.GetProperty("latestRun").GetProperty("counters").GetProperty("total").GetInt32().Should().Be(1); Str(summary, "health").Should().Be("FAILED");
    }

    // ---------- run ----------

    [Fact]
    public async Task C1_run_returns_safe_projection_with_counters_matching_run_items_and_404_for_unknown()
    {
        var client = await LoggedInAsAsync(Roles.Operator); var account = await SeedAccountAsync();
        var run = await SeedRunAsync(account, Base, found: 2, blocking: [(MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR, "LISTING_READ_FAILED"), (MarketplaceListingSyncItemOutcome.SELLER_MISMATCH, "LISTING_SELLER_MISMATCH")]);
        var response = await client.GetAsync($"{Prefix}/marketplace-listing-syncs/{run}"); response.StatusCode.Should().Be(HttpStatusCode.OK); using var json = await JsonAsync(response); var root = json.RootElement;
        Str(root, "status").Should().Be("PARTIAL"); Str(root, "safeResultCode").Should().Be("LISTING_SYNC_PARTIAL"); Str(root, "requiredAction").Should().Be("NONE");
        var counters = root.GetProperty("counters"); counters.GetProperty("total").GetInt32().Should().Be(4); counters.GetProperty("found").GetInt32().Should().Be(2); counters.GetProperty("blockingRetryable").GetInt32().Should().Be(1); counters.GetProperty("blockingNonRetryable").GetInt32().Should().Be(1);
        counters.GetProperty("listingsCreated").GetInt32().Should().Be(2); counters.GetProperty("byOutcome").GetProperty("FOUND").GetInt32().Should().Be(2); counters.GetProperty("byOrigin").GetProperty("ENUMERATED").GetInt32().Should().Be(4);
        AssertNoForbiddenNames(root);
        (await client.GetAsync($"{Prefix}/marketplace-listing-syncs/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------- items ----------

    [Fact]
    public async Task C1_items_pagination_normalization_filter_total_ordering_and_retryable()
    {
        var client = await LoggedInAsAsync(Roles.Viewer); var account = await SeedAccountAsync(); var run = await SeedRunAsync(account, Base);
        await SqlAsync($"""
            INSERT INTO commerce.marketplace_listing_sync_run_item (id,sync_run_id,external_listing_id,origin,outcome,created_at,updated_at,attempt_count,listing_created,observation_appended)
            SELECT gen_random_uuid(),'{run}','MLB-IT-' || lpad(n::text,3,'0'),'ENUMERATED','PENDING',now(),now(),0,false,false FROM generate_series(1,150) AS n;
            UPDATE commerce.marketplace_listing_sync_run_item SET outcome='TRANSIENT_ERROR', safe_error_code='LISTING_READ_FAILED', completed_at=now(), attempt_count=1 WHERE sync_run_id='{run}' AND external_listing_id <= 'MLB-IT-010';
            UPDATE commerce.marketplace_listing_sync_run_item SET outcome='PERMANENT_ERROR', safe_error_code='LISTING_RESPONSE_INVALID', completed_at=now(), attempt_count=1 WHERE sync_run_id='{run}' AND external_listing_id = 'MLB-IT-011';
            """);
        async Task<JsonDocument> Page(string query) { var response = await client.GetAsync($"{Prefix}/marketplace-listing-syncs/{run}/items{query}"); response.StatusCode.Should().Be(HttpStatusCode.OK); return await JsonAsync(response); }
        using (var zero = await Page("?pageSize=0")) { zero.RootElement.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo(["items", "page", "pageSize", "total"]); zero.RootElement.GetProperty("pageSize").GetInt32().Should().Be(1); zero.RootElement.GetProperty("items").GetArrayLength().Should().Be(1); AssertNoForbiddenNames(zero.RootElement); }
        using (var big = await Page("?pageSize=500")) { big.RootElement.GetProperty("pageSize").GetInt32().Should().Be(100); big.RootElement.GetProperty("items").GetArrayLength().Should().Be(100); big.RootElement.GetProperty("total").GetInt32().Should().Be(150); }
        using (var page0 = await Page("?page=0&pageSize=10")) { page0.RootElement.GetProperty("page").GetInt32().Should().Be(1); Str(page0.RootElement.GetProperty("items")[0], "externalListingId").Should().Be("MLB-IT-001"); }
        using (var filtered = await Page("?outcome=TRANSIENT_ERROR&pageSize=100")) { filtered.RootElement.GetProperty("total").GetInt32().Should().Be(10); filtered.RootElement.GetProperty("items").EnumerateArray().Should().OnlyContain(x => x.GetProperty("retryable").GetBoolean() && Str(x, "outcome") == "TRANSIENT_ERROR" && Str(x, "safeErrorCode") == "LISTING_READ_FAILED"); }
        using (var permanent = await Page("?outcome=PERMANENT_ERROR")) permanent.RootElement.GetProperty("items")[0].GetProperty("retryable").GetBoolean().Should().BeFalse();
        using var first = await Page("?page=1&pageSize=100"); using var second = await Page("?page=2&pageSize=100");
        var ids = first.RootElement.GetProperty("items").EnumerateArray().Concat(second.RootElement.GetProperty("items").EnumerateArray()).Select(x => Str(x, "externalListingId")!).ToList();
        ids.Should().Equal(Enumerable.Range(1, 150).Select(n => $"MLB-IT-{n:000}")); Str(first.RootElement.GetProperty("items")[0], "origin").Should().Be("ENUMERATED");
        (await client.GetAsync($"{Prefix}/marketplace-listing-syncs/{Guid.NewGuid()}/items")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------- retry ----------

    [Fact]
    public async Task C1_retry_creates_a_server_derived_failed_only_run_and_rejects_client_supplied_targets_and_missing_antiforgery()
    {
        var client = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync();
        var original = await SeedRunAsync(account, Base, found: 1, blocking: [(MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR, "LISTING_READ_FAILED"), (MarketplaceListingSyncItemOutcome.NOT_FOUND, "LISTING_NOT_FOUND_AT_PROVIDER"), (MarketplaceListingSyncItemOutcome.SELLER_MISMATCH, "LISTING_SELLER_MISMATCH")]);
        var url = $"{Prefix}/marketplace-listing-syncs/{original}/retry";
        (await client.PostRawAsync(url, null, withAntiforgery: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsync(url, new { externalListingIds = new[] { "MLB-FORGED" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "targets are never accepted from the client");
        await using (var check = Db()) (await check.Set<MarketplaceListingSyncRun>().CountAsync(x => x.MarketplaceAccountId == account)).Should().Be(1);
        var response = await client.PostRawAsync(url, null); response.StatusCode.Should().Be(HttpStatusCode.Accepted); using var json = await JsonAsync(response);
        Str(json.RootElement, "kind").Should().Be("FAILED_ONLY"); Str(json.RootElement, "retryOfRunId").Should().Be(original.ToString()); Str(json.RootElement, "rootRunId").Should().Be(original.ToString()); json.RootElement.GetProperty("counters").GetProperty("byOrigin").GetProperty("RETRY_TARGET").GetInt32().Should().Be(2);
        await using var db = Db(); var retryId = Guid.Parse(Str(json.RootElement, "runId")!);
        (await db.Set<MarketplaceListingSyncRunItem>().Where(x => x.SyncRunId == retryId).Select(x => x.ExternalListingId).ToListAsync()).Should().HaveCount(2).And.NotContain("MLB-FORGED");
        // while that retry is active a second retry conflicts and reports the active run
        var conflict = await client.PostRawAsync(url, null); conflict.StatusCode.Should().Be(HttpStatusCode.Conflict); using var conflictJson = await JsonAsync(conflict);
        Str(conflictJson.RootElement, "code").Should().Be("LISTING_SYNC_ALREADY_ACTIVE"); Str(conflictJson.RootElement, "activeRunId").Should().Be(retryId.ToString());
    }

    /// <summary>M060 / handoff D4 "Restart the host (new DI container, same DB) … POST retry": the whole ASP.NET host (and therefore its DI container, caches and
    /// in-memory state) is disposed and a brand-new one is built over the SAME database; everything the retry needs must come from persisted rows.</summary>
    [Fact]
    public async Task M060_D4_http_POST_retry_after_the_whole_host_is_disposed_and_recreated_uses_only_persisted_state()
    {
        var account = await SeedAccountAsync();
        var original = await SeedRunAsync(account, Base, found: 95, blocking: Enumerable.Repeat((MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR, "LISTING_READ_FAILED"), 5).ToArray());
        List<string> failingIds;
        await using (var before = Db()) failingIds = await before.Set<MarketplaceListingSyncRunItem>().Where(x => x.SyncRunId == original && x.Outcome == MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR).Select(x => x.ExternalListingId).OrderBy(x => x).ToListAsync();
        failingIds.Should().HaveCount(5);

        await _factory.DisposeAsync();                                             // host 1 is gone: DI container, singletons, caches
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, configureTestServices: services =>
        {
            services.AddSingleton<IMarketplaceConnectorRegistry>(_registry);
            services.AddSingleton<ILoggerProvider>(_logs);
        });
        var owner = await LoggedInAsAsync(Roles.Owner);                            // host 2 authenticates against persisted identity

        using (var items = await JsonAsync(await owner.GetAsync($"{Prefix}/marketplace-listing-syncs/{original}/items?outcome=TRANSIENT_ERROR&pageSize=100")))
        {
            items.RootElement.GetProperty("total").GetInt32().Should().Be(5, "the unresolved identities survived the restart");
            items.RootElement.GetProperty("items").EnumerateArray().Select(x => Str(x, "externalListingId")).Should().BeEquivalentTo(failingIds);
        }
        using (var summary = await JsonAsync(await owner.GetAsync($"{Prefix}/marketplace-accounts/{account}/listing-sync")))
        {
            summary.RootElement.GetProperty("summary").GetProperty("openBlockingItemCount").GetInt32().Should().Be(5);
            Str(summary.RootElement.GetProperty("summary"), "health").Should().Be("ATTENTION_REQUIRED");
        }

        var response = await owner.PostRawAsync($"{Prefix}/marketplace-listing-syncs/{original}/retry", null);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var json = await JsonAsync(response);
        Str(json.RootElement, "kind").Should().Be("FAILED_ONLY"); Str(json.RootElement, "retryOfRunId").Should().Be(original.ToString()); Str(json.RootElement, "rootRunId").Should().Be(original.ToString());
        json.RootElement.GetProperty("counters").GetProperty("total").GetInt32().Should().Be(5);
        json.RootElement.GetProperty("counters").GetProperty("byOrigin").GetProperty("RETRY_TARGET").GetInt32().Should().Be(5);
        var retryId = Guid.Parse(Str(json.RootElement, "runId")!);
        await using var db = Db();
        (await db.Set<MarketplaceListingSyncRunItem>().Where(x => x.SyncRunId == retryId).Select(x => x.ExternalListingId).OrderBy(x => x).ToListAsync()).Should().Equal(failingIds, "targets are derived from the persisted source run only");
        (await db.Set<MarketplaceListingSyncRunItem>().Where(x => x.SyncRunId == retryId).AllAsync(x => x.Origin == MarketplaceListingSyncItemOrigin.RETRY_TARGET && x.Outcome == MarketplaceListingSyncItemOutcome.PENDING)).Should().BeTrue();
        (await db.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.SyncRunId == original)).Should().Be(100, "the original run's items are untouched");
        _logs.Entries.Should().NotContain(e => e.Message.Contains("access_token") || e.Message.Contains("Bearer "));
    }

    [Fact]
    public async Task C1_retry_nothing_to_retry_is_409_and_unknown_run_is_404_and_viewer_is_forbidden()
    {
        var owner = await LoggedInAsAsync(Roles.Owner); var viewer = await LoggedInAsAsync(Roles.Viewer); var account = await SeedAccountAsync(); var succeeded = await SeedRunAsync(account, Base, found: 2);
        var nothing = await owner.PostRawAsync($"{Prefix}/marketplace-listing-syncs/{succeeded}/retry", null); nothing.StatusCode.Should().Be(HttpStatusCode.Conflict); using var json = await JsonAsync(nothing);
        Str(json.RootElement, "code").Should().Be("LISTING_SYNC_NOTHING_TO_RETRY"); json.RootElement.TryGetProperty("blockedReason", out _).Should().BeFalse();
        (await owner.PostRawAsync($"{Prefix}/marketplace-listing-syncs/{Guid.NewGuid()}/retry", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await viewer.PostRawAsync($"{Prefix}/marketplace-listing-syncs/{succeeded}/retry", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---------- ADR-0027 run requiredAction ----------

    private static readonly string[] FrozenBlockedReasons = ["ACCOUNT_INACTIVE", "CHANNEL_INVALID", "PROVIDER_NOT_CONFIGURED", "CAPABILITY_UNSUPPORTED", "GRANT_UNKNOWN", "GRANT_DENIED", "AUTHORIZATION_NOT_USABLE", "RATE_LIMITED", "RUNTIME_UNAVAILABLE", "SYNC_ACTIVE"];
    private static readonly string[] FrozenRunCodes =
    [
        "LISTING_SYNC_COMPLETED", "LISTING_SYNC_PARTIAL", "LISTING_SYNC_NO_SUCCESSFUL_ITEMS", "LISTING_ENUMERATION_INCOMPLETE", "RUN_EXECUTION_ATTEMPTS_EXHAUSTED", "PROVIDER_UNAVAILABLE", "PROVIDER_RATE_LIMITED",
        "ACCOUNT_INACTIVE", "CHANNEL_INVALID", "PROVIDER_NOT_CONFIGURED", "MARKETPLACE_PROVIDER_NOT_SUPPORTED", "CAPABILITY_UNSUPPORTED", "GRANT_UNKNOWN", "GRANT_DENIED", "AUTHORIZATION_NOT_USABLE", "RUNTIME_UNAVAILABLE", "RATE_LIMITED",
        "PROVIDER_ACCESS_FORBIDDEN", "CREDENTIAL_STORE_UNAVAILABLE", "LISTING_VOLUME_EXCEEDS_LIMIT", "CAPABILITY_CODE_REQUIRED", "PROVIDER_CREDENTIAL_REJECTED", "CREDENTIAL_CONFIRMED_LOST",
    ];

    [Fact]
    public void C1_the_current_safe_code_catalogs_are_pinned_in_both_directions()
    {
        ListingSyncRequiredActionRules.BlockedReasonActions.Keys.Should().BeEquivalentTo(FrozenBlockedReasons);
        ListingSyncRequiredActionRules.RunCodes.Keys.Should().BeEquivalentTo(FrozenRunCodes);
        MarketplaceRequiredActions.PrecedenceHighestFirst.Should().Equal("CONTACT_ADMIN", "REAUTHORIZE_AFTER_STORE_LOSS", "RECONNECT_EXISTING_ACCOUNT", "REAUTHORIZE", "AUTHORIZE", "CHECK_PROVIDER_ACCOUNT", "NONE");
        MarketplaceRequiredActions.Highest(["NONE", "CHECK_PROVIDER_ACCOUNT", "REAUTHORIZE", "CONTACT_ADMIN"]).Should().Be("CONTACT_ADMIN");
        MarketplaceRequiredActions.Highest(["CONTACT_ADMIN", "REAUTHORIZE", "NONE", "CHECK_PROVIDER_ACCOUNT"]).Should().Be("CONTACT_ADMIN", "order independent");
        MarketplaceRequiredActions.Highest([]).Should().Be("NONE");
    }

    public static IEnumerable<object[]> RunActionRows() =>
    [
        ["LISTING_SYNC_NO_SUCCESSFUL_ITEMS", "NONE"], ["LISTING_ENUMERATION_INCOMPLETE", "NONE"], ["RUN_EXECUTION_ATTEMPTS_EXHAUSTED", "NONE"], ["PROVIDER_UNAVAILABLE", "NONE"], ["PROVIDER_RATE_LIMITED", "NONE"],
        ["ACCOUNT_INACTIVE", "NONE"], ["CHANNEL_INVALID", "NONE"], ["PROVIDER_NOT_CONFIGURED", "NONE"], ["MARKETPLACE_PROVIDER_NOT_SUPPORTED", "NONE"], ["CAPABILITY_UNSUPPORTED", "NONE"], ["GRANT_UNKNOWN", "NONE"], ["GRANT_DENIED", "NONE"],
        ["AUTHORIZATION_NOT_USABLE", "NONE"], ["RUNTIME_UNAVAILABLE", "NONE"], ["RATE_LIMITED", "NONE"],
        ["PROVIDER_ACCESS_FORBIDDEN", "CHECK_PROVIDER_ACCOUNT"], ["CREDENTIAL_STORE_UNAVAILABLE", "CONTACT_ADMIN"], ["LISTING_VOLUME_EXCEEDS_LIMIT", "CONTACT_ADMIN"], ["CAPABILITY_CODE_REQUIRED", "CONTACT_ADMIN"],
        ["PROVIDER_CREDENTIAL_REJECTED", "REAUTHORIZE"], ["CREDENTIAL_CONFIRMED_LOST", "REAUTHORIZE_AFTER_STORE_LOSS"],
    ];

    [Theory]
    [MemberData(nameof(RunActionRows))]
    public async Task C1_failed_and_partial_runs_map_every_stop_code_per_adr_0027(string code, string expected)
    {
        var client = await LoggedInAsAsync(Roles.Viewer); var account = await SeedAccountAsync();
        var failed = await SeedRunAsync(account, Base, enumerationCompleted: false, pending: 1, stopReason: "PROVIDER_UNAVAILABLE"); await SetSafeResultCodeAsync(failed, code);
        var other = await SeedAccountAsync(); var partial = await SeedRunAsync(other, Base, found: 1, pending: 1, stopReason: code);
        foreach (var (run, status) in new[] { (failed, "FAILED"), (partial, "PARTIAL") })
        {
            using var json = await JsonAsync(await client.GetAsync($"{Prefix}/marketplace-listing-syncs/{run}")); Str(json.RootElement, "status").Should().Be(status);
            Str(json.RootElement, "requiredAction").Should().Be(expected, $"{status} run with {code}");
        }
    }

    [Fact]
    public async Task C1_non_terminal_succeeded_and_partial_without_a_stop_code_have_no_required_action()
    {
        var client = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync();
        var succeeded = await SeedRunAsync(account, Base, found: 1); var partial = await SeedRunAsync(account, Base.AddMinutes(10), found: 1, blocking: (MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR, "LISTING_READ_FAILED"));
        var queued = Guid.Parse(Str((await JsonAsync(await client.PostRawAsync($"{Prefix}/marketplace-accounts/{account}/listing-syncs", null))).RootElement, "runId")!);
        foreach (var run in new[] { succeeded, partial, queued })
        {
            using var json = await JsonAsync(await client.GetAsync($"{Prefix}/marketplace-listing-syncs/{run}")); Str(json.RootElement, "requiredAction").Should().Be("NONE");
        }
    }

    private async Task<string> RunActionAsync(AuthTestClient client, Guid run) { using var json = await JsonAsync(await client.GetAsync($"{Prefix}/marketplace-listing-syncs/{run}")); return Str(json.RootElement, "requiredAction")!; }

    private Task InsertConfirmedOperationAsync(Guid account, string kind, string decidedAtSql) => SqlAsync($"""
        WITH op AS (INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,candidate_credential_reference,candidate_credential_version,previous_credential_reference,previous_confirmed_credential_version,created_at,updated_at,decided_at,cleanup_attempt_count,version)
            VALUES (gen_random_uuid(),'{kind}','SECRET_PERSISTED','CONFIRMED','DONE','{account}','MERCADO_LIVRE','test-ref',2,'test-ref',1,now(),now(),{decidedAtSql},0,1) RETURNING id)
        UPDATE commerce.marketplace_account_connection SET confirmed_operation_id = (SELECT id FROM op), confirmed_credential_version = 2 WHERE marketplace_account_id = '{account}'
        """);

    [Fact]
    public async Task C1_credential_rule_connection_action_wins_reconsent_clears_refresh_does_not_and_supersession_clears()
    {
        var client = await LoggedInAsAsync(Roles.Viewer);
        // 1. current connection action wins (VERCE application credentials invalid => CONTACT_ADMIN)
        var a1 = await SeedAccountAsync(); var r1 = await SeedRunAsync(a1, Base, enumerationCompleted: false, pending: 1, stopReason: "PROVIDER_CREDENTIAL_REJECTED");
        await SqlAsync($"UPDATE commerce.marketplace_account_connection SET authorization_state='REAUTHORIZATION_REQUIRED', safe_failure_code='PROVIDER_APP_CREDENTIALS_INVALID' WHERE marketplace_account_id='{a1}'");
        (await RunActionAsync(client, r1)).Should().Be("CONTACT_ADMIN");
        // base (CONNECTED, nothing newer)
        var a2 = await SeedAccountAsync(); var r2 = await SeedRunAsync(a2, Base, enumerationCompleted: false, pending: 1, stopReason: "PROVIDER_CREDENTIAL_REJECTED"); (await RunActionAsync(client, r2)).Should().Be("REAUTHORIZE");
        // 2/3. a later RECONNECT or CONNECT_NEW confirmed after the run finished is re-consent
        await InsertConfirmedOperationAsync(a2, "RECONNECT", "now()"); (await RunActionAsync(client, r2)).Should().Be("NONE");
        var a3 = await SeedAccountAsync(); var r3 = await SeedRunAsync(a3, Base, enumerationCompleted: false, pending: 1, stopReason: "PROVIDER_CREDENTIAL_REJECTED");
        await InsertConfirmedOperationAsync(a3, "CONNECT_NEW", "now()"); (await RunActionAsync(client, r3)).Should().Be("NONE");
        // re-consent decided BEFORE the run finished does not clear it
        var a3b = await SeedAccountAsync(); var r3b = await SeedRunAsync(a3b, DateTimeOffset.UtcNow, enumerationCompleted: false, pending: 1, stopReason: "PROVIDER_CREDENTIAL_REJECTED");
        await InsertConfirmedOperationAsync(a3b, "RECONNECT", "now() - interval '1 day'"); (await RunActionAsync(client, r3b)).Should().Be("REAUTHORIZE");
        // 4. a later REFRESH is the same authorization: not re-consent
        var a4 = await SeedAccountAsync(); var r4 = await SeedRunAsync(a4, Base, enumerationCompleted: false, pending: 1, stopReason: "PROVIDER_CREDENTIAL_REJECTED");
        await InsertConfirmedOperationAsync(a4, "REFRESH", "now()"); (await RunActionAsync(client, r4)).Should().Be("REAUTHORIZE");
        // 5/6. only a LATER terminal run supersedes: earlier terminal and later non-terminal runs do not
        var a5 = await SeedAccountAsync(); var earlier = await SeedRunAsync(a5, Base.AddDays(-1), found: 1); var r5 = await SeedRunAsync(a5, Base, enumerationCompleted: false, pending: 1, stopReason: "PROVIDER_CREDENTIAL_REJECTED");
        (await RunActionAsync(client, r5)).Should().Be("REAUTHORIZE");
        var owner = await LoggedInAsAsync(Roles.Owner); var queued = await owner.PostRawAsync($"{Prefix}/marketplace-accounts/{a5}/listing-syncs", null); queued.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await RunActionAsync(client, r5)).Should().Be("REAUTHORIZE", "a later QUEUED run does not supersede");
        await SqlAsync($"UPDATE commerce.marketplace_listing_sync_run SET status='SUCCEEDED', safe_result_code='LISTING_SYNC_COMPLETED', enumeration_completed=true, finished_at=now(), lease_token=NULL, lease_until=NULL WHERE marketplace_account_id='{a5}' AND status='QUEUED'");
        (await RunActionAsync(client, r5)).Should().Be("NONE", "a later terminal run supersedes");
        earlier.Should().NotBeEmpty();
    }

    [Fact]
    public async Task C1_supersedable_codes_clear_after_a_later_terminal_run_and_store_loss_vs_outage_are_distinct()
    {
        var client = await LoggedInAsAsync(Roles.Viewer); var account = await SeedAccountAsync();
        var forbidden = await SeedRunAsync(account, Base, enumerationCompleted: false, pending: 1, stopReason: "PROVIDER_ACCESS_FORBIDDEN"); (await RunActionAsync(client, forbidden)).Should().Be("CHECK_PROVIDER_ACCOUNT");
        var outage = await SeedRunAsync(account, Base.AddMinutes(30), enumerationCompleted: false, pending: 1, stopReason: "CREDENTIAL_STORE_UNAVAILABLE"); (await RunActionAsync(client, outage)).Should().Be("CONTACT_ADMIN");
        (await RunActionAsync(client, forbidden)).Should().Be("NONE", "the store-outage run is a later terminal run");
        var lost = await SeedAccountAsync(); var lostRun = await SeedRunAsync(lost, Base, enumerationCompleted: false, pending: 1, stopReason: "CREDENTIAL_CONFIRMED_LOST");
        (await RunActionAsync(client, lostRun)).Should().Be("REAUTHORIZE_AFTER_STORE_LOSS");
    }

    [Fact]
    public async Task C1_partial_run_with_several_stop_codes_uses_the_adr_precedence()
    {
        var client = await LoggedInAsAsync(Roles.Viewer);
        var a1 = await SeedAccountAsync(); var r1 = await SeedRunAsync(a1, Base, found: 1, pending: 2, stopReason: "CREDENTIAL_STORE_UNAVAILABLE");
        await SqlAsync($"UPDATE commerce.marketplace_listing_sync_run_item SET safe_error_code='PROVIDER_ACCESS_FORBIDDEN' WHERE id = (SELECT id FROM commerce.marketplace_listing_sync_run_item WHERE sync_run_id='{r1}' AND outcome='TRANSIENT_ERROR' ORDER BY external_listing_id LIMIT 1)");
        (await RunActionAsync(client, r1)).Should().Be("CONTACT_ADMIN");
        var a2 = await SeedAccountAsync(); var r2 = await SeedRunAsync(a2, Base, found: 1, pending: 2, stopReason: "PROVIDER_ACCESS_FORBIDDEN");
        await SqlAsync($"UPDATE commerce.marketplace_listing_sync_run_item SET safe_error_code='PROVIDER_CREDENTIAL_REJECTED' WHERE id = (SELECT id FROM commerce.marketplace_listing_sync_run_item WHERE sync_run_id='{r2}' AND outcome='TRANSIENT_ERROR' ORDER BY external_listing_id LIMIT 1)");
        (await RunActionAsync(client, r2)).Should().Be("REAUTHORIZE", "REAUTHORIZE outranks CHECK_PROVIDER_ACCOUNT");
        // item-level TRANSIENT codes are not stop codes
        var a3 = await SeedAccountAsync(); var r3 = await SeedRunAsync(a3, Base, found: 1, blocking: (MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR, "LISTING_WRITE_CONFLICT")); (await RunActionAsync(client, r3)).Should().Be("NONE");
    }

    [Fact]
    public async Task C1_unknown_future_safe_code_maps_to_none_with_exactly_one_safe_warning()
    {
        var client = await LoggedInAsAsync(Roles.Viewer); var account = await SeedAccountAsync();
        var run = await SeedRunAsync(account, Base, enumerationCompleted: false, pending: 1, stopReason: "PROVIDER_UNAVAILABLE"); await SetSafeResultCodeAsync(run, "FUTURE_UNMAPPED_CODE_X");
        var response = await client.GetAsync($"{Prefix}/marketplace-listing-syncs/{run}"); response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = await JsonAsync(response); Str(json.RootElement, "requiredAction").Should().Be("NONE"); Str(json.RootElement, "safeResultCode").Should().Be("FUTURE_UNMAPPED_CODE_X");
        var warnings = _logs.Entries.Where(x => x.Level == LogLevel.Warning && x.Message.Contains("FUTURE_UNMAPPED_CODE_X")).ToList();
        warnings.Should().ContainSingle(); warnings[0].State.Where(kv => kv.Key != "{OriginalFormat}").Select(kv => kv.Key).Should().BeEquivalentTo(["SafeCode"]);
        warnings[0].Message.Should().NotContain("test-ref").And.NotContain("token");
    }

    // ---------- Terra T-FC1-01: Start/Retry take NO request body ----------

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _bytes;
        public UnknownLengthContent(string payload) { _bytes = System.Text.Encoding.UTF8.GetBytes(payload); Headers.ContentType = new("application/json"); }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(_bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; } // forces chunked transfer: Content-Length unknown to the server
    }

    private static HttpContent PayloadContent(string variant, string payload) => variant == "chunked" ? new UnknownLengthContent(payload) : new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

    /// <summary>Arranges a request target that is otherwise valid: a startable account (start) or an eligible PARTIAL source run (retry).</summary>
    private async Task<(string Url, Guid Account)> BodyContractTargetAsync(string endpoint)
    {
        var account = await SeedAccountAsync();
        if (endpoint == "start") return ($"{Prefix}/marketplace-accounts/{account}/listing-syncs", account);
        var source = await SeedRunAsync(account, Base, found: 1, blocking: (MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR, "LISTING_READ_FAILED"));
        return ($"{Prefix}/marketplace-listing-syncs/{source}/retry", account);
    }

    private async Task<long> OwnedSyncAuditRowsForAsync(Guid actor)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM platform.audit_log WHERE user_id = @actor AND entity_table = 'marketplace_listing_sync_run'", connection); command.Parameters.AddWithValue("actor", actor);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public static IEnumerable<object[]> RejectedPayloadRows()
    {
        foreach (var endpoint in new[] { "start", "retry" })
            foreach (var variant in new[] { "length", "chunked" })
                foreach (var payload in new[] { "{}", "{\"externalListingIds\":[\"X\"]}", "[]", "null", "\"\"", "0", "true" })
                    yield return [endpoint, variant, payload];
    }

    [Theory]
    [MemberData(nameof(RejectedPayloadRows))]
    public async Task C1_start_and_retry_reject_every_request_payload_including_an_empty_json_object(string endpoint, string variant, string payload)
    {
        var client = await LoggedInAsAsync(Roles.Owner); var actor = _users[^1]; var (url, account) = await BodyContractTargetAsync(endpoint);
        var response = await client.PostRawAsync(url, PayloadContent(variant, payload)); response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{endpoint} {variant} body {payload}");
        // The rejected request had no effect at all: no run, no retry items, no creation audit attributed to the actor.
        await using var db = Db();
        (await db.Set<MarketplaceListingSyncRun>().CountAsync(x => x.MarketplaceAccountId == account)).Should().Be(endpoint == "start" ? 0 : 1);
        (await db.Set<MarketplaceListingSyncRunItem>().CountAsync(x => x.Origin == MarketplaceListingSyncItemOrigin.RETRY_TARGET && db.Set<MarketplaceListingSyncRun>().Any(r => r.Id == x.SyncRunId && r.MarketplaceAccountId == account))).Should().Be(0);
        (await OwnedSyncAuditRowsForAsync(actor)).Should().Be(0);
    }

    [Theory]
    [InlineData("start", "none")] [InlineData("start", "empty-json-typed")] [InlineData("start", "empty-chunked")]
    [InlineData("retry", "none")] [InlineData("retry", "empty-json-typed")] [InlineData("retry", "empty-chunked")]
    public async Task C1_start_and_retry_without_a_payload_proceed_normally(string endpoint, string variant)
    {
        var client = await LoggedInAsAsync(Roles.Owner); var (url, _) = await BodyContractTargetAsync(endpoint);
        HttpContent? content = variant switch { "none" => null, "empty-json-typed" => new StringContent("", System.Text.Encoding.UTF8, "application/json"), _ => new UnknownLengthContent("") };
        var response = await client.PostRawAsync(url, content); response.StatusCode.Should().Be(HttpStatusCode.Accepted, $"{endpoint} with {variant}");
    }

    [Fact]
    public async Task C1_openapi_still_declares_no_request_body_for_start_and_retry()
    {
        var client = await LoggedInAsAsync(Roles.Owner); using var doc = await JsonAsync(await client.GetAsync("/openapi/v1.json")); var paths = doc.RootElement.GetProperty("paths");
        paths.GetProperty("/api/commerce/marketplace-accounts/{id}/listing-syncs").GetProperty("post").TryGetProperty("requestBody", out _).Should().BeFalse();
        paths.GetProperty("/api/commerce/marketplace-listing-syncs/{runId}/retry").GetProperty("post").TryGetProperty("requestBody", out _).Should().BeFalse();
    }

    // ---------- Terra T-FC1-02: embedded summary runs carry the same counters as GET run ----------

    private async Task AssertEmbeddedRunMatchesStandaloneAsync(AuthTestClient client, JsonElement summary, string position)
    {
        var embedded = summary.GetProperty(position); embedded.ValueKind.Should().Be(JsonValueKind.Object, position);
        embedded.GetProperty("counters").ValueKind.Should().Be(JsonValueKind.Object, $"{position} must carry counters");
        using var standalone = await JsonAsync(await client.GetAsync($"{Prefix}/marketplace-listing-syncs/{Str(embedded, "runId")}"));
        embedded.GetProperty("counters").GetRawText().Should().Be(standalone.RootElement.GetProperty("counters").GetRawText(), $"{position} counters equal the standalone run counters field by field");
        Str(embedded, "requiredAction").Should().Be(Str(standalone.RootElement, "requiredAction"), $"{position} requiredAction equals the standalone run requiredAction");
    }

    [Fact]
    public async Task C1_embedded_summary_runs_expose_the_standalone_counters_in_all_three_positions()
    {
        var owner = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync();
        var original = await SeedRunAsync(account, Base, found: 1, blocking: [(MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR, "LISTING_READ_FAILED"), (MarketplaceListingSyncItemOutcome.NOT_FOUND, "LISTING_NOT_FOUND_AT_PROVIDER"), (MarketplaceListingSyncItemOutcome.SELLER_MISMATCH, "LISTING_SELLER_MISMATCH")]);
        using var retry = await JsonAsync(await owner.PostRawAsync($"{Prefix}/marketplace-listing-syncs/{original}/retry", null)); var retryId = Str(retry.RootElement, "runId")!;
        using var status = await JsonAsync(await owner.GetAsync($"{Prefix}/marketplace-accounts/{account}/listing-sync")); var summary = status.RootElement.GetProperty("summary");
        Str(summary.GetProperty("activeRun"), "runId").Should().Be(retryId); Str(summary.GetProperty("latestRun"), "runId").Should().Be(retryId); Str(summary.GetProperty("latestFullRun"), "runId").Should().Be(original.ToString());
        foreach (var position in new[] { "activeRun", "latestRun", "latestFullRun" }) await AssertEmbeddedRunMatchesStandaloneAsync(owner, summary, position);
        // concrete values: the original PARTIAL run ...
        var counters = summary.GetProperty("latestFullRun").GetProperty("counters");
        counters.GetProperty("total").GetInt32().Should().Be(4); counters.GetProperty("found").GetInt32().Should().Be(1); counters.GetProperty("blockingRetryable").GetInt32().Should().Be(2); counters.GetProperty("blockingNonRetryable").GetInt32().Should().Be(1);
        counters.GetProperty("pending").GetInt32().Should().Be(0); counters.GetProperty("listingsCreated").GetInt32().Should().Be(1); counters.GetProperty("observationsAppended").GetInt32().Should().Be(1);
        counters.GetProperty("byOutcome").GetProperty("FOUND").GetInt32().Should().Be(1); counters.GetProperty("byOutcome").GetProperty("TRANSIENT_ERROR").GetInt32().Should().Be(1); counters.GetProperty("byOrigin").GetProperty("ENUMERATED").GetInt32().Should().Be(4);
        // ... and the QUEUED retry run (active + latest) with its two server-derived targets
        foreach (var position in new[] { "activeRun", "latestRun" })
        {
            var retryCounters = summary.GetProperty(position).GetProperty("counters");
            retryCounters.GetProperty("total").GetInt32().Should().Be(2); retryCounters.GetProperty("pending").GetInt32().Should().Be(2); retryCounters.GetProperty("found").GetInt32().Should().Be(0); retryCounters.GetProperty("byOrigin").GetProperty("RETRY_TARGET").GetInt32().Should().Be(2);
            Str(summary.GetProperty(position), "requiredAction").Should().Be("NONE");
        }
    }

    [Fact]
    public async Task C1_embedded_runs_keep_the_adr_0027_required_action_while_carrying_counters()
    {
        var client = await LoggedInAsAsync(Roles.Viewer); var account = await SeedAccountAsync();
        var run = await SeedRunAsync(account, Base, found: 0, enumerationCompleted: false, pending: 2, stopReason: "PROVIDER_CREDENTIAL_REJECTED");
        async Task<JsonDocument> Status() => await JsonAsync(await client.GetAsync($"{Prefix}/marketplace-accounts/{account}/listing-sync"));
        using (var before = await Status())
        {
            var latestFull = before.RootElement.GetProperty("summary").GetProperty("latestFullRun"); Str(latestFull, "requiredAction").Should().Be("REAUTHORIZE");
            latestFull.GetProperty("counters").GetProperty("total").GetInt32().Should().Be(2); latestFull.GetProperty("counters").GetProperty("byOutcome").GetProperty("TRANSIENT_ERROR").GetInt32().Should().Be(2);
            await AssertEmbeddedRunMatchesStandaloneAsync(client, before.RootElement.GetProperty("summary"), "latestFullRun");
        }
        await InsertConfirmedOperationAsync(account, "RECONNECT", "now()");
        using var after = await Status(); var embedded = after.RootElement.GetProperty("summary").GetProperty("latestFullRun");
        Str(embedded, "requiredAction").Should().Be("NONE", "re-consent after the run clears the historical action"); embedded.GetProperty("counters").GetProperty("total").GetInt32().Should().Be(2); run.Should().NotBeEmpty();
    }

    // ---------- Terra T-FC1-03: ownership-scoped cleanup ----------

    [Fact]
    public async Task C1_cleanup_removes_every_owned_row_and_nothing_foreign()
    {
        var owner = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync();
        (await owner.PostRawAsync($"{Prefix}/marketplace-accounts/{account}/listing-syncs", null)).StatusCode.Should().Be(HttpStatusCode.Accepted); // creates a run + LISTING_SYNC_REQUESTED audit
        var ownedOperation = Guid.NewGuid(); _operations.Add(ownedOperation);
        await SqlAsync($"INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,created_at,updated_at,cleanup_attempt_count,version) VALUES ('{ownedOperation}','CONNECT_NEW','PREPARED','PENDING','NOT_REQUIRED',NULL,'MERCADO_LIVRE',now(),now(),0,1)");
        // foreign sentinels this class does NOT own: an account-null operation (the shape the old broad delete removed), an audit row and an identity
        var foreignOperation = Guid.NewGuid(); var foreignAudit = Guid.NewGuid(); var foreignUser = Guid.CreateVersion7();
        await SqlAsync($"INSERT INTO commerce.marketplace_account_operation (id,kind,phase,decision,cleanup_state,marketplace_account_id,provider_code,created_at,updated_at,cleanup_attempt_count,version) VALUES ('{foreignOperation}','CONNECT_NEW','PREPARED','PENDING','NOT_REQUIRED',NULL,'MERCADO_LIVRE',now(),now(),0,1)");
        await using (var db = Db()) { db.Add(new Verce.Platform.Audit.AuditLogEntry(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, "test", "c1_foreign_sentinel", foreignAudit, "INSERT", null, null, null, Guid.NewGuid(), null, 1, Verce.Platform.UnitOfWork.AuditSource.System)); await db.SaveChangesAsync(); }
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(); var email = foreignUser.ToString("N") + "@foreign.test";
            (await users.CreateAsync(new ApplicationUser { Id = foreignUser, UserName = email, Email = email, DisplayName = "Foreign", IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow })).Succeeded.Should().BeTrue();
        }
        try
        {
            await using (var connection = new NpgsqlConnection(_fixture.ConnectionString)) { await connection.OpenAsync(); await CollectOwnedChildIdsAsync(connection); }
            var before = await OwnedResidueAsync();
            before.Audit.Should().BeGreaterThan(0); before.Users.Should().BeGreaterThan(0); before.Accounts.Should().BeGreaterThan(0); before.Operations.Should().BeGreaterThan(0); before.Runs.Should().Be(1); before.Channels.Should().Be(1, "the proof is not vacuous: there is residue to clean");

            await CleanupOwnedRowsAsync();

            (await OwnedResidueAsync()).Should().Be(new OwnedResidue(0, 0, 0, 0, 0, 0, 0, 0, 0), "audit, identities, accounts, operations, runs, items, listings and channels owned by this class are gone");
            await using var verify = Db();
            (await verify.Set<MarketplaceAccountOperation>().AnyAsync(x => x.Id == foreignOperation)).Should().BeTrue("a foreign account-null operation must survive");
            (await verify.Set<Verce.Platform.Audit.AuditLogEntry>().AnyAsync(x => x.EntityId == foreignAudit)).Should().BeTrue("a foreign audit row must survive");
            (await verify.Users.AnyAsync(x => x.Id == foreignUser)).Should().BeTrue("a foreign identity must survive");
        }
        finally
        {
            await SqlAsync($"DELETE FROM commerce.marketplace_account_operation WHERE id = '{foreignOperation}'; DELETE FROM platform.audit_log WHERE entity_id = '{foreignAudit}'");
            using var scope = _factory.Services.CreateScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            if (await users.FindByIdAsync(foreignUser.ToString()) is { } leftover) await users.DeleteAsync(leftover);
        }
    }
    // ---------- connection RequiredAction regression + OpenAPI ----------

    [Theory]
    [InlineData("CONNECTED", "AVAILABLE", null, "NONE")]
    [InlineData("CONNECTED", "UNAVAILABLE", null, "CHECK_PROVIDER_ACCOUNT")]
    [InlineData("REVOKED", "UNKNOWN", null, "AUTHORIZE")]
    [InlineData("NOT_CONNECTED", "UNKNOWN", null, "AUTHORIZE")]
    [InlineData("REAUTHORIZATION_REQUIRED", "UNKNOWN", null, "REAUTHORIZE")]
    [InlineData("REAUTHORIZATION_REQUIRED", "UNKNOWN", "PROVIDER_APP_CREDENTIALS_INVALID", "CONTACT_ADMIN")]
    [InlineData("REAUTHORIZATION_REQUIRED", "UNKNOWN", "RECONNECT_EXISTING_ACCOUNT", "RECONNECT_EXISTING_ACCOUNT")]
    [InlineData("REAUTHORIZATION_REQUIRED", "UNKNOWN", "PROBE_CREDENTIAL_UNREADABLE", "REAUTHORIZE_AFTER_STORE_LOSS")]
    [InlineData("REAUTHORIZATION_REQUIRED", "UNKNOWN", "OPERATION_RECOVERY_FAIL_CLOSED", "REAUTHORIZE_AFTER_STORE_LOSS")]
    public async Task C1_existing_connection_required_action_behavior_is_unchanged_by_the_shared_helper(string state, string runtime, string? safeCode, string expected)
    {
        var client = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync();
        await SqlAsync($"UPDATE commerce.marketplace_account_connection SET authorization_state='{state}', runtime_availability='{runtime}', safe_failure_code={(safeCode is null ? "NULL" : $"'{safeCode}'")} WHERE marketplace_account_id='{account}'");
        using var json = await JsonAsync(await client.GetAsync($"{Prefix}/marketplace-accounts/{account}/connection")); Str(json.RootElement, "requiredAction").Should().Be(expected);
    }

    [Fact]
    public async Task C1_openapi_exposes_the_five_operations_with_dtos_status_codes_and_string_enums()
    {
        var client = await LoggedInAsAsync(Roles.Owner); var response = await client.GetAsync("/openapi/v1.json"); response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await JsonAsync(response); var paths = doc.RootElement.GetProperty("paths");
        JsonElement Op(string path, string verb) => paths.GetProperty(path).GetProperty(verb);
        var start = Op("/api/commerce/marketplace-accounts/{id}/listing-syncs", "post"); var retry = Op("/api/commerce/marketplace-listing-syncs/{runId}/retry", "post");
        foreach (var operation in new[] { start, retry }) foreach (var code in new[] { "202", "400", "404", "409", "422", "503" }) operation.GetProperty("responses").TryGetProperty(code, out _).Should().BeTrue($"response {code}");
        Op("/api/commerce/marketplace-accounts/{id}/listing-sync", "get").GetProperty("responses").TryGetProperty("200", out _).Should().BeTrue();
        Op("/api/commerce/marketplace-listing-syncs/{runId}", "get").GetProperty("responses").TryGetProperty("404", out _).Should().BeTrue();
        var items = Op("/api/commerce/marketplace-listing-syncs/{runId}/items", "get"); items.GetProperty("parameters").EnumerateArray().Select(p => Str(p, "name")).Should().Contain(["outcome", "page", "pageSize"]);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas"); foreach (var name in new[] { "ListingSyncRunResponse", "ListingSyncStatusResponse", "ListingSyncAvailabilityResponse", "ListingSyncRunItemResponse" }) schemas.TryGetProperty(name, out _).Should().BeTrue(name);
        var raw = doc.RootElement.GetRawText(); foreach (var forbidden in new[] { "leaseToken", "leaseUntil", "heartbeatAt", "credentialReference" }) raw.Should().NotContain("\"" + forbidden + "\"");
        foreach (var name in new[] { "MarketplaceListingSyncKind", "MarketplaceListingSyncStatus", "MarketplaceListingSyncItemOutcome", "MarketplaceListingSyncHealth" })
        {
            var enumValues = schemas.GetProperty(name).GetProperty("enum").EnumerateArray().ToList(); enumValues.Should().NotBeEmpty().And.OnlyContain(x => x.ValueKind == JsonValueKind.String, $"{name} is a string enum (never integers)");
        }
        schemas.GetProperty("MarketplaceListingSyncStatus").GetProperty("enum").EnumerateArray().Select(x => x.GetString()).Should().BeEquivalentTo(["QUEUED", "RUNNING", "SUCCEEDED", "PARTIAL", "FAILED"]);
    }
}
