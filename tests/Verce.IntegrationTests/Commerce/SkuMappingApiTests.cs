using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Verce.Api.Auth;
using Verce.Api.Commerce;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Catalog;
using Verce.Modules.Commerce;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.Commerce;

/// <summary>S8C.2 Slice 1: the explicit SKU-authority HTTP API (ADR-0025 §D/§J, handoff B7/C1/C2/D5) and the four Published Items fields. No provider, OAuth or network
/// call is possible. Every row this class creates is recorded by id and removed by id (the shared database must be order-independent).</summary>
[Collection(PostgresCollection.Name)]
public sealed class SkuMappingApiTests : IAsyncLifetime
{
    private const string Prefix = "/api/commerce";
    private static readonly string[] ForbiddenNames = ["confirmedby", "confirmedbyuserid", "invalidatedby", "invalidatedbyuserid", "createdby", "updatedby", "accesstoken", "refreshtoken", "credentialreference", "rawpayload", "providererror", "scrollid", "quantity", "stock", "availablequantity"];

    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;
    private readonly List<Guid> _accounts = [], _channels = [], _users = [], _rolesCreated = [], _products = [], _mappings = [], _listings = [];
    private readonly List<string> _triggers = [];

    public SkuMappingApiTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() { _factory = new VerceWebApplicationFactory(_fixture.ConnectionString); return Task.CompletedTask; }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString); await connection.OpenAsync();
        foreach (var trigger in _triggers) await using (var drop = new NpgsqlCommand($"DROP TRIGGER IF EXISTS {trigger} ON commerce.marketplace_account_sku_mapping; DROP FUNCTION IF EXISTS {trigger}_fn();", connection)) await drop.ExecuteNonQueryAsync();
        await using (var collect = new NpgsqlCommand("SELECT id FROM commerce.marketplace_account_sku_mapping WHERE marketplace_account_id = ANY(@a)", connection))
        {
            collect.Parameters.AddWithValue("a", _accounts.ToArray());
            await using var reader = await collect.ExecuteReaderAsync(); while (await reader.ReadAsync()) _mappings.Add(reader.GetGuid(0));
        }
        await using var command = new NpgsqlCommand("""
            SET session_replication_role = replica;
            DELETE FROM platform.audit_log WHERE user_id = ANY(@users) OR entity_id = ANY(@owned);
            DELETE FROM commerce.marketplace_listing_observation WHERE marketplace_listing_id IN (SELECT id FROM commerce.marketplace_listing WHERE marketplace_account_id = ANY(@accounts));
            DELETE FROM commerce.marketplace_listing WHERE marketplace_account_id = ANY(@accounts);
            DELETE FROM commerce.marketplace_account_sku_mapping WHERE marketplace_account_id = ANY(@accounts);
            DELETE FROM commerce.marketplace_account WHERE id = ANY(@accounts);
            DELETE FROM pricing.sales_channel WHERE id = ANY(@channels);
            DELETE FROM catalog.product WHERE id = ANY(@products);
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
        command.Parameters.AddWithValue("users", _users.ToArray()); command.Parameters.AddWithValue("owned", _accounts.Concat(_channels).Concat(_users).Concat(_products).Concat(_mappings).Concat(_listings).ToArray());
        command.Parameters.AddWithValue("accounts", _accounts.ToArray()); command.Parameters.AddWithValue("channels", _channels.ToArray()); command.Parameters.AddWithValue("products", _products.ToArray()); command.Parameters.AddWithValue("roles", _rolesCreated.ToArray());
        await command.ExecuteNonQueryAsync();
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
        var account = Guid.NewGuid(); var channel = Guid.NewGuid(); var code = "SM" + account.ToString("N")[..10];
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO pricing.sales_channel (id,code,name,kind,active,created_at,version) VALUES (@channel,@code,@code,'Marketplace',true,now(),1);
            INSERT INTO commerce.marketplace_provider (code,name) VALUES ('MERCADO_LIVRE','Mercado Livre') ON CONFLICT DO NOTHING;
            INSERT INTO commerce.marketplace_account (id,provider_code,external_account_id,sales_channel_id,display_name,active,credential_reference,sync_state,created_at,version) VALUES (@account,'MERCADO_LIVRE',@code,@channel,@code,true,'test-ref','NEVER_SYNCED',now(),1);
            """, connection);
        _accounts.Add(account); _channels.Add(channel);
        command.Parameters.AddWithValue("channel", channel); command.Parameters.AddWithValue("account", account); command.Parameters.AddWithValue("code", code);
        await command.ExecuteNonQueryAsync(); return account;
    }

    private async Task<Guid> SeedProductAsync(bool active = true)
    {
        await using var db = Db(); var product = new Product("SM" + Guid.NewGuid().ToString("N")[..10], "Produto mapeado", null); if (!active) product.Deactivate();
        db.Add(product); await db.SaveChangesAsync(); _products.Add(product.Id); return product.Id;
    }

    private async Task<AuthTestClient> LoggedInAsAsync(string role)
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test"; const string password = "a-perfectly-fine-12char-password";
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(); var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!await roles.RoleExistsAsync(role)) { var created = new ApplicationRole(role); (await roles.CreateAsync(created)).Succeeded.Should().BeTrue(); _rolesCreated.Add(created.Id); }
        var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = role + " Mapper", IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow };
        _users.Add(user.Id); (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue(); (await users.AddToRoleAsync(user, role)).Succeeded.Should().BeTrue();
        var client = new AuthTestClient(_factory.CreateHttpsClient()); await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new LoginRequest(email, password))).StatusCode.Should().Be(HttpStatusCode.NoContent); await client.EnsureCsrfCookieAsync();
        return client;
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    private static string? Str(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");
    private static void AssertNoForbiddenNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object: foreach (var property in element.EnumerateObject()) { ForbiddenNames.Should().NotContain(property.Name.ToLowerInvariant()); AssertNoForbiddenNames(property.Value); } break;
            case JsonValueKind.Array: foreach (var item in element.EnumerateArray()) AssertNoForbiddenNames(item); break;
        }
    }

    private static string ConfirmUrl(Guid account) => $"{Prefix}/marketplace-accounts/{account}/sku-mappings";
    private static string DeactivateUrl(Guid mapping) => $"{Prefix}/sku-mappings/{mapping}/deactivate";

    private async Task<(Guid Id, long Version)> ConfirmOkAsync(AuthTestClient client, Guid account, string sku, Guid product)
    {
        var response = await client.PostAsync(ConfirmUrl(account), new { externalSku = sku, productId = product });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using var doc = await JsonAsync(response); var id = doc.RootElement.GetProperty("id").GetGuid(); _mappings.Add(id);
        return (id, doc.RootElement.GetProperty("version").GetInt64());
    }

    private async Task<MarketplaceAccountSkuMapping> MappingAsync(Guid id) { await using var db = Db(); return await db.Set<MarketplaceAccountSkuMapping>().AsNoTracking().SingleAsync(x => x.Id == id); }
    private async Task<int> ActiveCountAsync(Guid account, string sku) { await using var db = Db(); return await db.Set<MarketplaceAccountSkuMapping>().CountAsync(x => x.MarketplaceAccountId == account && x.ExternalSku == sku && x.Active); }
    private async Task<int> TotalCountAsync(Guid account) { await using var db = Db(); return await db.Set<MarketplaceAccountSkuMapping>().CountAsync(x => x.MarketplaceAccountId == account); }
    private async Task<long> AuditCountAsync(Guid entityId) { await using var connection = new NpgsqlConnection(_fixture.ConnectionString); await connection.OpenAsync(); await using var c = new NpgsqlCommand("SELECT count(*) FROM platform.audit_log WHERE entity_id = @id", connection); c.Parameters.AddWithValue("id", entityId); return (long)(await c.ExecuteScalarAsync())!; }

    // ---------- confirm ----------

    [Fact]
    public async Task Owner_confirms_a_mapping_with_201_normalized_sku_product_composition_an_allowlisted_dto_and_a_generic_audit_row()
    {
        var owner = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync(); var product = await SeedProductAsync();
        var response = await owner.PostAsync(ConfirmUrl(account), new { externalSku = "  SKU-é\u0001  ", productId = product });
        response.StatusCode.Should().Be(HttpStatusCode.Created); using var doc = await JsonAsync(response); var root = doc.RootElement;
        _mappings.Add(root.GetProperty("id").GetGuid());
        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["id", "marketplaceAccountId", "externalSku", "productId", "productCode", "productName", "active", "confirmedAt", "invalidatedAt", "invalidatedReason", "version"]);
        AssertNoForbiddenNames(root);
        Str(root, "externalSku").Should().Be("SKU-é", "NFC, trimmed, control characters removed"); root.GetProperty("active").GetBoolean().Should().BeTrue();
        root.GetProperty("marketplaceAccountId").GetGuid().Should().Be(account); root.GetProperty("productId").GetGuid().Should().Be(product);
        Str(root, "productName").Should().Be("Produto mapeado"); Str(root, "productCode").Should().StartWith("SM");
        root.GetProperty("invalidatedAt").ValueKind.Should().Be(JsonValueKind.Null); root.GetProperty("invalidatedReason").ValueKind.Should().Be(JsonValueKind.Null); root.GetProperty("version").GetInt64().Should().Be(1);
        var stored = await MappingAsync(root.GetProperty("id").GetGuid()); stored.ExternalSku.Should().Be("SKU-é"); stored.Active.Should().BeTrue(); stored.ConfirmedByUserId.Should().NotBe(Guid.Empty);
        (await AuditCountAsync(stored.Id)).Should().BeGreaterThan(0, "SKU mappings use the generic audit (ADR-0025 §K)");
    }

    [Fact]
    public async Task A_second_confirmation_without_correct_replace_information_is_a_409_conflict_and_never_replaces_silently()
    {
        var owner = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync(); var p1 = await SeedProductAsync(); var p2 = await SeedProductAsync();
        var (first, version) = await ConfirmOkAsync(owner, account, "SKU-C", p1);
        var attempts = new object[]
        {
            new { externalSku = "SKU-C", productId = p2 },
            new { externalSku = "SKU-C", productId = p2, replaceMappingId = Guid.NewGuid(), replaceMappingVersion = version },
            new { externalSku = "SKU-C", productId = p2, replaceMappingId = first, replaceMappingVersion = version + 1 },
            new { externalSku = "SKU-C", productId = p2, replaceMappingId = first },
            new { externalSku = "SKU-C", productId = p2, replaceMappingVersion = version },
        };
        foreach (var attempt in attempts)
        {
            var response = await owner.PostAsync(ConfirmUrl(account), attempt);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict); using var problem = await JsonAsync(response);
            Str(problem.RootElement, "code").Should().Be("SKU_MAPPING_CONFLICT"); Str(problem.RootElement, "title").Should().Be("SKU_MAPPING_CONFLICT");
        }
        var stored = await MappingAsync(first); stored.Active.Should().BeTrue(); stored.ProductId.Should().Be(p1); stored.Version.Should().Be(version);
        (await TotalCountAsync(account)).Should().Be(1);
    }

    [Fact]
    public async Task A_matching_replace_invalidates_the_old_mapping_and_creates_the_new_one_in_one_unit_of_work()
    {
        var operatorClient = await LoggedInAsAsync(Roles.Operator); var account = await SeedAccountAsync(); var p1 = await SeedProductAsync(); var p2 = await SeedProductAsync();
        var (first, version) = await ConfirmOkAsync(operatorClient, account, "SKU-R", p1);
        var response = await operatorClient.PostAsync(ConfirmUrl(account), new { externalSku = " SKU-R ", productId = p2, replaceMappingId = first, replaceMappingVersion = version });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync()); using var doc = await JsonAsync(response); var newId = doc.RootElement.GetProperty("id").GetGuid(); _mappings.Add(newId);
        var old = await MappingAsync(first); var created = await MappingAsync(newId);
        old.Active.Should().BeFalse(); old.InvalidatedReason.Should().Be(MarketplaceAccountSkuMappingInvalidatedReason.REPLACED_BY_NEW_MAPPING); old.InvalidatedAt.Should().NotBeNull(); old.InvalidatedByUserId.Should().NotBeNull();
        created.Active.Should().BeTrue(); created.ProductId.Should().Be(p2); created.ExternalSku.Should().Be("SKU-R");
        (await ActiveCountAsync(account, "SKU-R")).Should().Be(1, "never two ACTIVE mappings for one account/SKU");
        old.Version.Should().Be(version + 1, "one unit of work advances the old root exactly once");
    }

    [Fact]
    public async Task A_failure_while_creating_the_replacement_rolls_back_the_invalidation_too()
    {
        var owner = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync(); var p1 = await SeedProductAsync(); var p2 = await SeedProductAsync();
        var (first, version) = await ConfirmOkAsync(owner, account, "SKU-ROLLBACK", p1);
        var trigger = "test_sku_rollback_" + Guid.NewGuid().ToString("N")[..8]; _triggers.Add(trigger);
        await SqlAsync($"""
            CREATE FUNCTION {trigger}_fn() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected insert failure' USING ERRCODE = 'P0001'; END $$;
            CREATE TRIGGER {trigger} BEFORE INSERT ON commerce.marketplace_account_sku_mapping FOR EACH ROW WHEN (NEW.product_id = '{p2}') EXECUTE FUNCTION {trigger}_fn();
            """);
        HttpResponseMessage? response = null;
        try { response = await owner.PostAsync(ConfirmUrl(account), new { externalSku = "SKU-ROLLBACK", productId = p2, replaceMappingId = first, replaceMappingVersion = version }); } catch { /* the host may surface the injected failure to the caller */ }
        if (response is not null) ((int)response.StatusCode).Should().BeGreaterThanOrEqualTo(500);
        var old = await MappingAsync(first);
        old.Active.Should().BeTrue("the invalidation rolled back with the failed insert"); old.InvalidatedAt.Should().BeNull(); old.InvalidatedReason.Should().BeNull(); old.Version.Should().Be(version);
        (await TotalCountAsync(account)).Should().Be(1);
    }

    [Fact]
    public async Task The_unique_index_arbitrates_a_confirmation_race_and_only_that_constraint_becomes_a_conflict()
    {
        var owner = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync(); var winner = await SeedProductAsync(); var loser = await SeedProductAsync();
        var trigger = "test_sku_race_" + Guid.NewGuid().ToString("N")[..8]; _triggers.Add(trigger);
        // A concurrent request wins the race: its ACTIVE row appears after this request passed its pre-check and before its own insert.
        await SqlAsync($"""
            CREATE FUNCTION {trigger}_fn() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
              INSERT INTO commerce.marketplace_account_sku_mapping (id,marketplace_account_id,external_sku,product_id,active,confirmed_by_user_id,confirmed_at,created_at,version)
              VALUES (gen_random_uuid(), NEW.marketplace_account_id, NEW.external_sku, '{winner}', true, NEW.confirmed_by_user_id, now(), now(), 1);
              RETURN NEW; END $$;
            CREATE TRIGGER {trigger} BEFORE INSERT ON commerce.marketplace_account_sku_mapping FOR EACH ROW WHEN (NEW.product_id = '{loser}') EXECUTE FUNCTION {trigger}_fn();
            """);
        var response = await owner.PostAsync(ConfirmUrl(account), new { externalSku = "SKU-RACE", productId = loser });
        response.StatusCode.Should().Be(HttpStatusCode.Conflict); using var problem = await JsonAsync(response); Str(problem.RootElement, "code").Should().Be("SKU_MAPPING_CONFLICT");
        // The injected "winner" lives in the loser's own transaction (no second connection inside a trigger), so the whole unit of work rolled back: nothing may survive.
        (await TotalCountAsync(account)).Should().Be(0, "the failed unit of work left no mapping behind");
    }

    [Fact]
    public async Task Ineligible_products_and_invalid_skus_are_422_with_local_codes_and_create_nothing()
    {
        var owner = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync(); var inactive = await SeedProductAsync(active: false); var active = await SeedProductAsync();
        (string Sku, Guid Product, string Code)[] cases =
        [
            ("SKU-X", inactive, "SKU_MAPPING_PRODUCT_INELIGIBLE"), ("SKU-X", Guid.NewGuid(), "SKU_MAPPING_PRODUCT_INELIGIBLE"), ("SKU-X", Guid.Empty, "SKU_MAPPING_PRODUCT_INELIGIBLE"),
            ("   ", active, "SKU_MAPPING_SKU_INVALID"), ("\u0001\u0002", active, "SKU_MAPPING_SKU_INVALID"), (new string('A', 201), active, "SKU_MAPPING_SKU_INVALID"),
        ];
        foreach (var (sku, product, code) in cases)
        {
            var response = await owner.PostAsync(ConfirmUrl(account), new { externalSku = sku, productId = product });
            response.StatusCode.Should().Be((HttpStatusCode)422, code); using var problem = await JsonAsync(response); Str(problem.RootElement, "code").Should().Be(code); Str(problem.RootElement, "title").Should().Be(code);
            (await response.Content.ReadAsStringAsync()).Should().NotContain("Exception").And.NotContain("at Verce");
        }
        var missingSku = await owner.PostRawAsync(ConfirmUrl(account), Body($$"""{"productId":"{{active}}"}"""));
        missingSku.StatusCode.Should().Be((HttpStatusCode)422); using (var problem = await JsonAsync(missingSku)) Str(problem.RootElement, "code").Should().Be("SKU_MAPPING_SKU_INVALID");
        (await TotalCountAsync(account)).Should().Be(0);
    }

    [Fact]
    public async Task Confirming_for_an_unknown_account_is_404()
    {
        var owner = await LoggedInAsAsync(Roles.Owner); var product = await SeedProductAsync();
        (await owner.PostAsync(ConfirmUrl(Guid.NewGuid()), new { externalSku = "SKU-404", productId = product })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------- deactivate ----------

    private static (bool Active, MarketplaceAccountSkuMappingInvalidatedReason? Reason, DateTimeOffset? At, Guid? By, long Version) State(MarketplaceAccountSkuMapping m) => (m.Active, m.InvalidatedReason, m.InvalidatedAt, m.InvalidatedByUserId, m.Version);

    private static async Task AssertConcurrencyConflictAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Conflict); using var problem = await JsonAsync(response);
        Str(problem.RootElement, "code").Should().Be("CONCURRENCY_CONFLICT"); Str(problem.RootElement, "title").Should().Be("CONCURRENCY_CONFLICT");
    }

    private async Task<Guid[]> AccountMappingIdsAsync(Guid account) { await using var db = Db(); return await db.Set<MarketplaceAccountSkuMapping>().AsNoTracking().Where(x => x.MarketplaceAccountId == account).Select(x => x.Id).ToArrayAsync(); }

    // ADR-0028 scenario A: the first effective deactivation.
    [Fact]
    public async Task Deactivate_active_mapping_with_the_current_version_is_204_with_the_operator_reason_one_version_bump_and_one_audit_row()
    {
        var owner = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync(); var product = await SeedProductAsync();
        var (id, version) = await ConfirmOkAsync(owner, account, "SKU-D", product); var auditBefore = await AuditCountAsync(id);
        (await owner.PostAsync(DeactivateUrl(id), new { version })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var stored = await MappingAsync(id);
        stored.Active.Should().BeFalse(); stored.InvalidatedReason.Should().Be(MarketplaceAccountSkuMappingInvalidatedReason.OPERATOR_DEACTIVATED);
        stored.InvalidatedAt.Should().NotBeNull(); stored.InvalidatedByUserId.Should().NotBeNull(); stored.Version.Should().Be(version + 1, "exactly one version bump");
        (await AuditCountAsync(id)).Should().Be(auditBefore + 1, "exactly one audit row for the transition");
        // A mapping is never reactivated: the same SKU is confirmed again as a new mapping.
        var (second, _) = await ConfirmOkAsync(owner, account, "SKU-D", product); second.Should().NotBe(id);
    }

    // ADR-0028 scenario B: replay with the current (post-invalidation) version is a 204 no-op for either invalidation reason.
    [Fact]
    public async Task Deactivate_replay_on_an_inactive_mapping_with_the_current_version_is_a_204_noop_for_both_invalidation_reasons()
    {
        var owner = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync(); var p1 = await SeedProductAsync(); var p2 = await SeedProductAsync();

        var (deactivated, v1) = await ConfirmOkAsync(owner, account, "SKU-B1", p1);
        (await owner.PostAsync(DeactivateUrl(deactivated), new { version = v1 })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var (original, v2) = await ConfirmOkAsync(owner, account, "SKU-B2", p1);
        var replace = await owner.PostAsync(ConfirmUrl(account), new { externalSku = "SKU-B2", productId = p2, replaceMappingId = original, replaceMappingVersion = v2 });
        replace.StatusCode.Should().Be(HttpStatusCode.Created); using (var doc = await JsonAsync(replace)) _mappings.Add(doc.RootElement.GetProperty("id").GetGuid());

        foreach (var (id, reason) in new[] { (deactivated, MarketplaceAccountSkuMappingInvalidatedReason.OPERATOR_DEACTIVATED), (original, MarketplaceAccountSkuMappingInvalidatedReason.REPLACED_BY_NEW_MAPPING) })
        {
            var before = await MappingAsync(id); var auditBefore = await AuditCountAsync(id);
            before.Active.Should().BeFalse(); before.InvalidatedReason.Should().Be(reason);
            for (var replay = 0; replay < 2; replay++)
            {
                var response = await owner.PostAsync(DeactivateUrl(id), new { version = before.Version });
                response.StatusCode.Should().Be(HttpStatusCode.NoContent, $"replay {replay} of {reason}");
                (await response.Content.ReadAsStringAsync()).Should().BeEmpty().And.NotContain("SKU_MAPPING_NOT_ACTIVE");
            }
            State(await MappingAsync(id)).Should().Be(State(before), "a no-op never rewrites the reason, the invalidation time/actor or the version");
            (await AuditCountAsync(id)).Should().Be(auditBefore, "a no-op writes no audit row");
        }
        // The replacement is still the only ACTIVE mapping for SKU-B2: the replay did not touch it.
        (await ActiveCountAsync(account, "SKU-B2")).Should().Be(1);
    }

    // ADR-0028 scenario C: a stale version is a concurrency conflict in every state, and writes nothing.
    [Fact]
    public async Task Deactivate_with_a_stale_version_is_409_concurrency_conflict_on_active_and_on_inactive_mappings_and_writes_nothing()
    {
        var owner = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync(); var p1 = await SeedProductAsync(); var p2 = await SeedProductAsync();

        var (active, activeVersion) = await ConfirmOkAsync(owner, account, "SKU-S1", p1);
        var beforeActive = await MappingAsync(active); var auditActive = await AuditCountAsync(active);
        await AssertConcurrencyConflictAsync(await owner.PostAsync(DeactivateUrl(active), new { version = activeVersion + 5 }));
        await AssertConcurrencyConflictAsync(await owner.PostAsync(DeactivateUrl(active), new { version = activeVersion - 1 }));
        State(await MappingAsync(active)).Should().Be(State(beforeActive)); (await AuditCountAsync(active)).Should().Be(auditActive);

        // INACTIVE by operator deactivation: the version the client held before the invalidation is stale.
        var (deactivated, deactivatedVersion) = await ConfirmOkAsync(owner, account, "SKU-S2", p1);
        (await owner.PostAsync(DeactivateUrl(deactivated), new { version = deactivatedVersion })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        // INACTIVE because it was replaced: a stale client must see the conflict, not a silent 204 that hides the replacement.
        var (replaced, replacedVersion) = await ConfirmOkAsync(owner, account, "SKU-S3", p1);
        (await owner.PostAsync(ConfirmUrl(account), new { externalSku = "SKU-S3", productId = p2, replaceMappingId = replaced, replaceMappingVersion = replacedVersion })).StatusCode.Should().Be(HttpStatusCode.Created);
        _mappings.AddRange(await AccountMappingIdsAsync(account));
        foreach (var (id, staleVersion) in new[] { (deactivated, deactivatedVersion), (replaced, replacedVersion) })
        {
            var before = await MappingAsync(id); var audit = await AuditCountAsync(id);
            await AssertConcurrencyConflictAsync(await owner.PostAsync(DeactivateUrl(id), new { version = staleVersion }));
            State(await MappingAsync(id)).Should().Be(State(before)); (await AuditCountAsync(id)).Should().Be(audit);
        }
        (await ActiveCountAsync(account, "SKU-S3")).Should().Be(1);
    }

    // ADR-0028 scenario E: genuinely concurrent HTTP requests (two independent sessions released together) carrying the same version.
    [Fact]
    public async Task Two_concurrent_deactivations_with_the_same_version_yield_exactly_one_204_one_409_one_transition_and_one_audit_row()
    {
        var first = await LoggedInAsAsync(Roles.Owner); var second = await LoggedInAsAsync(Roles.Operator); var account = await SeedAccountAsync(); var product = await SeedProductAsync();
        for (var round = 0; round < 8; round++)
        {
            var (id, version) = await ConfirmOkAsync(first, account, $"SKU-E{round}", product); var auditBefore = await AuditCountAsync(id);
            using var gate = new ManualResetEventSlim(false);
            Task<HttpResponseMessage> Fire(AuthTestClient client) => Task.Run(() => { gate.Wait(); return client.PostAsync(DeactivateUrl(id), new { version }); });
            var a = Fire(first); var b = Fire(second); gate.Set();
            var responses = await Task.WhenAll(a, b);
            responses.Count(r => r.StatusCode == HttpStatusCode.NoContent).Should().Be(1, $"round {round}: exactly one request wins");
            var losers = responses.Where(r => r.StatusCode != HttpStatusCode.NoContent).ToArray(); losers.Should().ContainSingle();
            await AssertConcurrencyConflictAsync(losers[0]);
            var stored = await MappingAsync(id);
            stored.Active.Should().BeFalse(); stored.InvalidatedReason.Should().Be(MarketplaceAccountSkuMappingInvalidatedReason.OPERATOR_DEACTIVATED); stored.Version.Should().Be(version + 1, $"round {round}: exactly one version bump");
            (await AuditCountAsync(id)).Should().Be(auditBefore + 1, $"round {round}: exactly one audit row for the single transition");
        }
    }

    [Fact]
    public async Task Deactivating_an_unknown_mapping_is_404()
    {
        var owner = await LoggedInAsAsync(Roles.Owner);
        (await owner.PostAsync(DeactivateUrl(Guid.NewGuid()), new { version = 1 })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------- permissions / antiforgery / strict JSON ----------

    [Fact]
    public async Task Owner_and_Operator_confirm_and_deactivate_while_Viewer_reads_but_never_mutates()
    {
        var account = await SeedAccountAsync(); var product = await SeedProductAsync(); var owner = await LoggedInAsAsync(Roles.Owner); var operatorClient = await LoggedInAsAsync(Roles.Operator); var viewer = await LoggedInAsAsync(Roles.Viewer);
        var (byOwner, ownerVersion) = await ConfirmOkAsync(owner, account, "SKU-OWNER", product);
        var (byOperator, operatorVersion) = await ConfirmOkAsync(operatorClient, account, "SKU-OPERATOR", product);
        (await operatorClient.PostAsync(DeactivateUrl(byOwner), new { version = ownerVersion })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.PostAsync(DeactivateUrl(byOperator), new { version = operatorVersion })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await viewer.PostAsync(ConfirmUrl(account), new { externalSku = "SKU-VIEWER", productId = product })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var (live, liveVersion) = await ConfirmOkAsync(owner, account, "SKU-LIVE", product);
        (await viewer.PostAsync(DeactivateUrl(live), new { version = liveVersion })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await MappingAsync(live)).Active.Should().BeTrue(); (await ActiveCountAsync(account, "SKU-VIEWER")).Should().Be(0);
        var list = await viewer.GetAsync($"{ConfirmUrl(account)}"); list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await JsonAsync(list); doc.RootElement.GetProperty("total").GetInt32().Should().Be(3); AssertNoForbiddenNames(doc.RootElement);
    }

    [Fact]
    public async Task Both_mutations_require_the_antiforgery_token_before_any_mutation_semantics()
    {
        var owner = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync(); var product = await SeedProductAsync();
        (await owner.PostAsync(ConfirmUrl(account), new { externalSku = "SKU-CSRF", productId = product }, withAntiforgery: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TotalCountAsync(account)).Should().Be(0);
        var (id, version) = await ConfirmOkAsync(owner, account, "SKU-CSRF2", product);
        (await owner.PostAsync(DeactivateUrl(id), new { version }, withAntiforgery: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await MappingAsync(id)).Active.Should().BeTrue();
        // A request that is wrong in two ways (no token AND an unknown property) is still the antiforgery refusal, never a mutation.
        (await owner.PostRawAsync(DeactivateUrl(id), Body($$"""{"version":{{version}},"extra":1}"""), withAntiforgery: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unknown_json_properties_and_malformed_bodies_are_400_and_mutate_nothing()
    {
        var owner = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync(); var product = await SeedProductAsync();
        foreach (var body in new[] { $$"""{"externalSku":"SKU-U","productId":"{{product}}","unexpected":true}""", $$"""{"externalSku":"SKU-U","productId":"{{product}}","ProductCode":"P1"}""", "", "null", "[]", "{not json" })
            (await owner.PostRawAsync(ConfirmUrl(account), Body(body))).StatusCode.Should().Be(HttpStatusCode.BadRequest, $"confirm body '{body}'");
        (await TotalCountAsync(account)).Should().Be(0);
        var (id, version) = await ConfirmOkAsync(owner, account, "SKU-U", product);
        foreach (var body in new[] { $$"""{"version":{{version}},"extra":"x"}""", "{}", """{"version":null}""", """{"version":"abc"}""", "", "null" })
            (await owner.PostRawAsync(DeactivateUrl(id), Body(body))).StatusCode.Should().Be(HttpStatusCode.BadRequest, $"deactivate body '{body}'");
        (await MappingAsync(id)).Active.Should().BeTrue();
    }

    // ---------- listing isolation / account isolation ----------

    private sealed record ListingSnapshot(Guid? ProductId, string LinkageState, string? LinkageSource, Guid? SkuMappingId, string SyncState, long Version, bool Suppressed, long Observations);

    private async Task<ListingSnapshot> SnapshotAsync(Guid listing)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT product_id, linkage_state, linkage_source, sku_mapping_id, sync_state, version, auto_link_suppressed, (SELECT count(*) FROM commerce.marketplace_listing_observation o WHERE o.marketplace_listing_id = l.id)
            FROM commerce.marketplace_listing l WHERE id = @id
            """, connection);
        command.Parameters.AddWithValue("id", listing); await using var r = await command.ExecuteReaderAsync(); await r.ReadAsync();
        return new(r.IsDBNull(0) ? null : r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.GetString(4), r.GetInt64(5), r.GetBoolean(6), r.GetInt64(7));
    }

    [Fact]
    public async Task Confirm_replace_and_deactivate_never_link_unlink_or_touch_any_listing()
    {
        var owner = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync(); var manualProduct = await SeedProductAsync(); var p1 = await SeedProductAsync(); var p2 = await SeedProductAsync();
        Guid manual, unlinked;
        await using (var db = Db())
        {
            var manualListing = new MarketplaceListing(account, "MLB-ISO-M", "SKU-ISO", MarketplaceListingStatus.ACTIVE, "Manual", 10m); manualListing.Link(manualProduct, null, null, null, Guid.NewGuid());
            var unlinkedListing = new MarketplaceListing(account, "MLB-ISO-U", "SKU-ISO", MarketplaceListingStatus.ACTIVE, "Unlinked", 11m);
            db.AddRange(manualListing, unlinkedListing); await db.SaveChangesAsync(); manual = manualListing.Id; unlinked = unlinkedListing.Id; _listings.Add(manual); _listings.Add(unlinked);
        }
        var before = (Manual: await SnapshotAsync(manual), Unlinked: await SnapshotAsync(unlinked));
        before.Manual.LinkageSource.Should().Be("MANUAL"); before.Unlinked.LinkageState.Should().Be("UNLINKED");

        var (first, version) = await ConfirmOkAsync(owner, account, "SKU-ISO", p1);
        (await SnapshotAsync(manual)).Should().Be(before.Manual, "confirming never relinks a manual listing"); (await SnapshotAsync(unlinked)).Should().Be(before.Unlinked, "confirming never auto-links an existing listing");
        var replace = await owner.PostAsync(ConfirmUrl(account), new { externalSku = "SKU-ISO", productId = p2, replaceMappingId = first, replaceMappingVersion = version });
        replace.StatusCode.Should().Be(HttpStatusCode.Created); using (var doc = await JsonAsync(replace)) { var replacement = doc.RootElement.GetProperty("id").GetGuid(); _mappings.Add(replacement);
            (await SnapshotAsync(manual)).Should().Be(before.Manual); (await SnapshotAsync(unlinked)).Should().Be(before.Unlinked);
            (await owner.PostAsync(DeactivateUrl(replacement), new { version = 1 })).StatusCode.Should().Be(HttpStatusCode.NoContent); }
        (await SnapshotAsync(manual)).Should().Be(before.Manual, "deactivation never unlinks"); (await SnapshotAsync(unlinked)).Should().Be(before.Unlinked);
    }

    [Fact]
    public async Task A_mapping_of_one_account_never_authorizes_or_affects_another_account()
    {
        var owner = await LoggedInAsAsync(Roles.Owner); var accountA = await SeedAccountAsync(); var accountB = await SeedAccountAsync(); var product = await SeedProductAsync();
        var (a, aVersion) = await ConfirmOkAsync(owner, accountA, "SKU-SHARED", product);
        var list = await owner.GetAsync(ConfirmUrl(accountB)); using (var doc = await JsonAsync(list)) doc.RootElement.GetProperty("total").GetInt32().Should().Be(0);
        var (b, _) = await ConfirmOkAsync(owner, accountB, "SKU-SHARED", product); b.Should().NotBe(a);
        // Naming account A's mapping as the replacement target for account B is a conflict, never a cross-account replace.
        var cross = await owner.PostAsync(ConfirmUrl(accountB), new { externalSku = "SKU-SHARED", productId = product, replaceMappingId = a, replaceMappingVersion = aVersion });
        cross.StatusCode.Should().Be(HttpStatusCode.Conflict); (await MappingAsync(a)).Active.Should().BeTrue();
        (await owner.PostAsync(DeactivateUrl(a), new { version = aVersion })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await MappingAsync(b)).Active.Should().BeTrue("deactivating account A's mapping leaves account B's untouched");
        (await owner.GetAsync(ConfirmUrl(Guid.NewGuid()))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------- list ----------

    [Fact]
    public async Task List_filters_by_active_pages_with_clamped_size_and_orders_by_sku()
    {
        var owner = await LoggedInAsAsync(Roles.Owner); var account = await SeedAccountAsync(); var product = await SeedProductAsync();
        var (b, bVersion) = await ConfirmOkAsync(owner, account, "SKU-B", product); await ConfirmOkAsync(owner, account, "SKU-A", product); await ConfirmOkAsync(owner, account, "SKU-C", product);
        (await owner.PostAsync(DeactivateUrl(b), new { version = bVersion })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        async Task<(int Total, string[] Skus, int Page, int PageSize)> Read(string query)
        {
            using var doc = await JsonAsync(await owner.GetAsync($"{ConfirmUrl(account)}{query}")); var root = doc.RootElement;
            return (root.GetProperty("total").GetInt32(), root.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("externalSku").GetString()!).ToArray(), root.GetProperty("page").GetInt32(), root.GetProperty("pageSize").GetInt32());
        }
        (await Read("")).Skus.Should().Equal("SKU-A", "SKU-B", "SKU-C");
        (await Read("?active=true")).Skus.Should().Equal("SKU-A", "SKU-C");
        var inactive = await Read("?active=false"); inactive.Skus.Should().Equal("SKU-B");
        var paged = await Read("?page=2&pageSize=2"); paged.Skus.Should().Equal("SKU-C"); paged.Total.Should().Be(3); paged.Page.Should().Be(2);
        (await Read("?pageSize=1000")).PageSize.Should().Be(100); (await Read("?pageSize=0")).PageSize.Should().Be(1); (await Read("?page=0")).Page.Should().Be(1);
        using var history = await JsonAsync(await owner.GetAsync($"{ConfirmUrl(account)}?active=false"));
        var row = history.RootElement.GetProperty("items")[0]; Str(row, "invalidatedReason").Should().Be("OPERATOR_DEACTIVATED"); row.GetProperty("active").GetBoolean().Should().BeFalse(); Str(row, "invalidatedAt").Should().NotBeNull();
    }

    // ---------- Published Items: the four S8C.2 fields ----------

    [Fact]
    public async Task Published_items_expose_the_four_fields_from_persisted_state_without_stock_or_provider_payload()
    {
        var owner = await LoggedInAsAsync(Roles.Owner); var viewer = await LoggedInAsAsync(Roles.Viewer); var account = await SeedAccountAsync(); var product = await SeedProductAsync();
        var (mapping, _) = await ConfirmOkAsync(owner, account, "SKU-PI", product); var now = DateTimeOffset.Parse("2026-10-05T12:00:00Z");
        NormalizedListingFacts Facts(string id, string? sku, decimal? price, string? native, int variations, string? url) => new(id, sku, "Titulo " + id, price, MarketplaceListingStatus.PAUSED, native, variations, url, now, []);
        Guid automatic, manual, unlinked;
        await using (var db = Db())
        {
            var a = MarketplaceListing.ImportFromProvider(account, Facts("MLB-PI-A", "SKU-PI", 25.90m, "paused:out_of_stock", 3, "https://produto.mercadolivre.com.br/MLB-PI-A"), Guid.NewGuid(), new(MarketplaceLinkageState.LINKED, product, mapping), now);
            var m = MarketplaceListing.ImportFromProvider(account, Facts("MLB-PI-M", "SKU-OTHER", null, null, 0, null), Guid.NewGuid(), new(MarketplaceLinkageState.UNLINKED, null, null), now); m.Link(product, null, null, null, Guid.NewGuid());
            var u = MarketplaceListing.ImportFromProvider(account, Facts("MLB-PI-U", null, 9m, "paused", 0, "https://produto.mercadolivre.com.br/MLB-PI-U"), Guid.NewGuid(), new(MarketplaceLinkageState.UNLINKED, null, null), now);
            db.AddRange(a, m, u); await db.SaveChangesAsync(); automatic = a.Id; manual = m.Id; unlinked = u.Id; _listings.AddRange([automatic, manual, unlinked]);
        }
        var response = await viewer.GetAsync($"{Prefix}/published-items?marketplaceAccountId={account}&pageSize=100"); response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await JsonAsync(response); AssertNoForbiddenNames(doc.RootElement);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("externalListingId").GetString()!);
        items.Should().HaveCount(3);
        var auto = items["MLB-PI-A"]; Str(auto, "listingUrl").Should().Be("https://produto.mercadolivre.com.br/MLB-PI-A"); Str(auto, "providerNativeStatus").Should().Be("paused:out_of_stock"); auto.GetProperty("variationCount").GetInt32().Should().Be(3); Str(auto, "linkageSource").Should().Be("DETERMINISTIC_SKU");
        Str(auto, "linkageState").Should().Be("LINKED"); Str(auto, "observedStatus").Should().Be("PAUSED"); auto.GetProperty("observedPrice").GetDecimal().Should().Be(25.90m);
        var man = items["MLB-PI-M"]; Str(man, "linkageSource").Should().Be("MANUAL"); man.GetProperty("listingUrl").ValueKind.Should().Be(JsonValueKind.Null); man.GetProperty("providerNativeStatus").ValueKind.Should().Be(JsonValueKind.Null); man.GetProperty("variationCount").GetInt32().Should().Be(0);
        man.GetProperty("observedPrice").ValueKind.Should().Be(JsonValueKind.Null, "an unknown price stays null, never zero");
        var unl = items["MLB-PI-U"]; unl.GetProperty("linkageSource").ValueKind.Should().Be(JsonValueKind.Null, "linkageSource is null unless LINKED"); Str(unl, "linkageState").Should().Be("UNLINKED");
        foreach (var item in items.Values)
            foreach (var existing in new[] { "id", "providerCode", "marketplaceAccountId", "accountName", "salesChannelId", "channelCode", "channelName", "externalListingId", "externalSku", "title", "observedPrice", "observedStatus", "productId", "productCode", "productName", "channelOfferId", "linkageState", "syncState", "lastObservedAt", "lastSyncAttemptAt", "lastSuccessfulSyncAt", "safeSyncError", "version", "listingUrl", "providerNativeStatus", "variationCount", "linkageSource" })
                item.TryGetProperty(existing, out _).Should().BeTrue($"the S8B field set is unchanged and the four S8C.2 fields are added: {existing}");
    }

    [Fact]
    public void Both_listing_dtos_declare_exactly_the_four_new_fields()
    {
        foreach (var type in new[] { typeof(ListingResponse), typeof(PublishedItemReadModel) })
        {
            var names = type.GetProperties().Select(p => p.Name).ToArray();
            names.Should().Contain(["ListingUrl", "ProviderNativeStatus", "VariationCount", "LinkageSource"], type.Name);
            names.Should().NotContain(n => n.Contains("Stock", StringComparison.OrdinalIgnoreCase) || n.Contains("Quantity", StringComparison.OrdinalIgnoreCase), type.Name);
        }
    }

    [Fact]
    public void The_openapi_metadata_types_the_published_items_response_and_declares_the_three_mapping_routes_with_their_policies()
    {
        var endpoints = _factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().ToArray();
        Microsoft.AspNetCore.Routing.RouteEndpoint Find(string method, string pattern) => endpoints.Single(e => e.RoutePattern.RawText == pattern && e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods.Contains(method));
        static IEnumerable<Type?> Produced(Microsoft.AspNetCore.Routing.RouteEndpoint e, int status) => e.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Http.Metadata.IProducesResponseTypeMetadata>().Where(m => m.StatusCode == status).Select(m => m.Type);
        static string[] Policies(Microsoft.AspNetCore.Routing.RouteEndpoint e) => e.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Select(a => a.Policy ?? "").ToArray();

        Produced(Find("GET", "/api/commerce/published-items"), 200).Should().Contain(typeof(PublishedItemsResponse), "the generated contract must not be `content?: never`");
        var list = Find("GET", "/api/commerce/marketplace-accounts/{id:guid}/sku-mappings"); Produced(list, 200).Should().Contain(typeof(PagedResponse<SkuMappingResponse>)); Policies(list).Should().Equal("commerce:read");
        var confirm = Find("POST", "/api/commerce/marketplace-accounts/{id:guid}/sku-mappings"); Produced(confirm, 201).Should().Contain(typeof(SkuMappingResponse)); Policies(confirm).Should().Equal("commerce:manage");
        var deactivate = Find("POST", "/api/commerce/sku-mappings/{mappingId:guid}/deactivate"); Produced(deactivate, 204).Should().NotBeEmpty(); Policies(deactivate).Should().Equal("commerce:manage");
    }
}
