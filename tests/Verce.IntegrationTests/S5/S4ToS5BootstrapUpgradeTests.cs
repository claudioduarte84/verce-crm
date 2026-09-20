using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Verce.Api.Catalog;
using Verce.Api.Customers;
using Verce.Api.Inventory;
using Verce.Api.Pricing;
using Verce.Api.Settings;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Costing;
using Verce.Modules.Customers;
using Verce.Modules.Inventory;
using Verce.Modules.Pricing;
using Verce.Modules.Settings;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.S5;

/// <summary>
/// Terra B-05: the S4→S5 upgrade certification performed manually during the S5 corrections
/// rounds must be a PERMANENT, independently-rerunnable repository artifact — not a one-off
/// developer session against a hand-provisioned disposable container. This test reproduces that
/// exact proof end to end: a database frozen at the terminal S4 migration, seeded with
/// representative S4 business data through the real application over real HTTP, upgraded to the
/// final S5 migration, then rebooted through the real composition root (never a private seed
/// method called directly) to prove the canonical DIRECT seed, its final invariants (B-04-R/N-04
/// closed corrections), and every piece of S4 data survive untouched.
///
/// Deliberately uses its OWN disposable PostgreSQL container and <see cref="IMigrator"/> directly
/// (the <c>WastagePercentageMigrationTests</c>/<c>CreationSequenceMigrationTests</c> pattern),
/// because the shared <see cref="PostgresFixture"/> always migrates straight to HEAD and cannot
/// pause at an intermediate migration.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class S4ToS5BootstrapUpgradeTests : IAsyncLifetime
{
    private const string S4TerminalMigration = "20260919005716_ConvertCostingWastageRateToPercentagePoints";
    private const string S5FinalMigration = "20260919152506_AddS5ProductsRecipesAndPricing";
    private const string OwnerPassword = "a-perfectly-fine-12char-password";
    private const string WastageKey = "costing.default_wastage_rate";

    private PostgreSqlContainer _container = null!;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        VerceDbContext.ConfigureModuleAssemblies(Verce.Api.ModuleAssemblyCatalog.All);
        _container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("verce_s4_to_s5_bootstrap_test")
            .WithUsername("verce")
            .WithPassword("verce_test_only")
            .Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private VerceDbContext CreateContext() => new(
        new DbContextOptionsBuilder<VerceDbContext>().UseNpgsql(_connectionString).UseSnakeCaseNamingConvention().Options);

    private async Task MigrateToAsync(string targetMigration)
    {
        await using var db = CreateContext();
        await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(targetMigration);
    }

    private VerceWebApplicationFactory NewFactory() => new(_connectionString, new Dictionary<string, string?> { ["Settings:SeedOnStartup"] = "true" });

    /// <summary>At the terminal S4 migration, the running app's OWN model already includes the
    /// not-yet-applied S5 migration, so every hosted seed service's "no pending migrations" guard
    /// (<c>InventorySeedService</c> included) correctly no-ops rather than seeding against a
    /// not-fully-migrated schema — exactly the same real behavior the manual S5 upgrade
    /// certification relied on. A representative S4 Supply still needs a real category row to
    /// reference, so this inserts the one <c>InventorySeedService</c> would have created on a
    /// genuine S4-only deployment.</summary>
    private async Task SeedFilamentCategoryAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO inventory.supply_category (code, name, is_active) VALUES ('FILAMENT', 'Filamento', true)";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Same reasoning as <see cref="SeedFilamentCategoryAsync"/>: <c>SettingsSeedService</c>
    /// also no-ops at the terminal S4 migration (the S5 migration counts as pending against the
    /// app's own model), so the representative <c>costing.default_wastage_rate</c> row a genuine
    /// S4-only deployment would already have is inserted directly, in S4's own final
    /// percentage-point semantics (ADR-0018) — never the legacy fraction.</summary>
    private async Task SeedWastageSettingAsync(string value, long version)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings.app_setting (id, key, value, value_type, scope, description, is_secret, created_at, version)
            VALUES (gen_random_uuid(), @key, @value, 'Decimal', 'costing', 'Perda padrão em pontos percentuais (0 a 100; ADR-0018)', false, now(), @version)
            """;
        command.Parameters.AddWithValue("key", WastageKey);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("version", version);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<AuthTestClient> CreateAndLogInOwnerAsync(VerceWebApplicationFactory factory, string email)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            if (!await roles.RoleExistsAsync(Roles.Owner)) (await roles.CreateAsync(new ApplicationRole(Roles.Owner))).Succeeded.Should().BeTrue();
            var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "S4 Owner", IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow };
            (await users.CreateAsync(user, OwnerPassword)).Succeeded.Should().BeTrue();
            (await users.AddToRoleAsync(user, Roles.Owner)).Succeeded.Should().BeTrue();
        }
        var client = new AuthTestClient(factory.CreateHttpsClient());
        await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new Verce.Api.Auth.LoginRequest(email, OwnerPassword))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await client.EnsureCsrfCookieAsync();
        return client;
    }

    private static async Task<(AuthTestClient Client, bool Succeeded)> LogInExistingOwnerAsync(VerceWebApplicationFactory factory, string email)
    {
        var client = new AuthTestClient(factory.CreateHttpsClient());
        await client.EnsureCsrfCookieAsync();
        var response = await client.PostAsync("/api/auth/login", new Verce.Api.Auth.LoginRequest(email, OwnerPassword));
        await client.EnsureCsrfCookieAsync();
        return (client, response.StatusCode == HttpStatusCode.NoContent);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [Fact]
    public async Task S4_terminal_database_upgrades_to_S5_bootstraps_canonical_Direct_and_preserves_existing_business_data()
    {
        // ================= Stage 1: representative S4 state, at the TERMINAL S4 migration =================
        await MigrateToAsync(S4TerminalMigration);
        await SeedFilamentCategoryAsync();
        await SeedWastageSettingAsync("5", version: 1);

        var ownerEmail = Guid.NewGuid().ToString("N") + "@example.test";
        Guid customerId;
        CustomerResponse customerBefore;
        SupplyResponse supplyBefore;
        InventoryMovementResponse movementBefore;
        string wastageValueBefore;
        long wastageVersionBefore;

        await using (var s4Factory = NewFactory())
        {
            using var warmup = s4Factory.CreateHttpsClient();
            (await warmup.GetAsync("/health/live")).EnsureSuccessStatusCode();

            var owner = await CreateAndLogInOwnerAsync(s4Factory, ownerEmail);

            customerBefore = await ReadAsync<CustomerResponse>(await owner.PostAsync("/api/customers",
                new CustomerRequest(PersonType.Individual, "Cliente S4 Bootstrap", null, null, "cliente@example.test", null, null, 0)));
            customerId = customerBefore.Id;

            var supply = await ReadAsync<SupplyResponse>(await owner.PostAsync("/api/supplies",
                new SupplyCreateRequest("S4-BOOT-MAT", "Material S4 Bootstrap", null, "FILAMENT", SupplyBaseUnit.Gram, null, null, null, null)));
            supplyBefore = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/purchase-receipt",
                new PurchaseReceiptRequest(1000m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, 100m, null, "NF-BOOT-1", null, supply.Version)));
            supplyBefore.CurrentStockBaseUnit.Should().Be(1000m);
            supplyBefore.LatestPurchaseUnitCost.Should().Be(0.1m);

            var movements = await ReadAsync<InventoryMovementListResponse>(await owner.GetAsync($"/api/supplies/{supplyBefore.Id}/inventory/movements?page=1&pageSize=20"));
            movementBefore = movements.Items.Should().ContainSingle().Which;
            movementBefore.Type.Should().Be(InventoryMovementType.PurchaseReceipt);
            movementBefore.QuantityDeltaBaseUnit.Should().Be(1000m);
            movementBefore.UnitCostSnapshot.Should().Be(0.1m);
            movementBefore.TotalCostSnapshot.Should().Be(100m);

            // Read back through the real Settings endpoint (not the raw row) to prove the value
            // this test inserted for the S4 baseline is what the application itself reports.
            var settingsBefore = await ReadAsync<AppSettingResponse[]>(await owner.GetAsync("/api/settings"));
            var wastageBefore = settingsBefore.Single(x => x.Key == WastageKey);
            wastageValueBefore = wastageBefore.Value;
            wastageVersionBefore = wastageBefore.Version;
            wastageValueBefore.Should().Be("5", "S4's final percentage-point semantics — never the legacy fraction \"0.05\" (ADR-0018)");
        }

        // ================= Stage 2: apply the FINAL S5 migration =================
        await MigrateToAsync(S5FinalMigration);

        await using (var db = CreateContext())
        {
            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            applied.Should().Contain(S4TerminalMigration, "prior S4 migrations must remain recorded, never rewritten");
            applied.Should().Contain(S5FinalMigration, "the final S5 migration must be the one actually applied — not an earlier approximation");
        }

        // ================= Stage 3: REAL application bootstrap against the upgraded database =================
        Guid directChannelId;
        await using (var s5Factory = NewFactory())
        {
            using var warmup = s5Factory.CreateHttpsClient();
            (await warmup.GetAsync("/health/live")).EnsureSuccessStatusCode();

            // The pre-existing S4 Owner must still be able to authenticate — not a replacement
            // Owner created after the upgrade.
            var (owner, loginSucceeded) = await LogInExistingOwnerAsync(s5Factory, ownerEmail);
            loginSucceeded.Should().BeTrue("the pre-existing S4 Owner's credentials must survive the S5 upgrade unchanged");

            // ---- Canonical DIRECT seed, produced by the REAL PricingSeedService via normal startup ----
            var channels = await ReadAsync<IReadOnlyList<SalesChannelResponse>>(await owner.GetAsync("/api/pricing/channels"));
            var direct = channels.Should().ContainSingle(x => x.Code == "DIRECT").Which;
            direct.Kind.Should().Be(SalesChannelKind.Direct);
            directChannelId = direct.Id;

            var feeRule = await ReadAsync<FeeRuleResponse>(await owner.GetAsync($"/api/pricing/channels/{direct.Id}/fee-rule"));
            feeRule.SalesChannelId.Should().Be(direct.Id);
            var feeVersion = feeRule.Versions.Should().ContainSingle().Which;
            feeVersion.CommissionPercent.Should().Be(0m);
            feeVersion.FixedFee.Should().Be(0m);
            feeVersion.ValidUntil.Should().BeNull();

            // ---- N-04: noncanonical Direct rejected, nothing persisted ----
            var noncanonical = await owner.PostAsync("/api/pricing/channels",
                new SalesChannelCreateRequest("OTHERCH", "Other", SalesChannelKind.Direct, null, null));
            noncanonical.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ProblemCodeAsync(noncanonical)).Should().Be("DIRECT_CHANNEL_IDENTITY_RESERVED");
            (await ReadAsync<IReadOnlyList<SalesChannelResponse>>(await owner.GetAsync("/api/pricing/channels")))
                .Should().NotContain(x => x.Code == "OTHERCH");

            // ---- N-03: non-zero fee on canonical DIRECT rejected, no version persisted ----
            var nonzeroFee = await owner.PostAsync($"/api/pricing/channels/{direct.Id}/fee-rule/versions",
                new FeeRuleVersionCreateRequest(new DateOnly(2030, 1, 1), null, 0.10m, 0m, FixedFeeApplication.PerUnit, null, null, null, false));
            nonzeroFee.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ProblemCodeAsync(nonzeroFee)).Should().Be("DIRECT_CHANNEL_FEES_NOT_ALLOWED");
            (await ReadAsync<FeeRuleResponse>(await owner.GetAsync($"/api/pricing/channels/{direct.Id}/fee-rule")))
                .Versions.Should().ContainSingle("neither rejected attempt above may leave a persisted row");

            // ---- S4 data preservation ----
            var customerAfter = await ReadAsync<CustomerResponse>(await owner.GetAsync($"/api/customers/{customerId}"));
            customerAfter.Id.Should().Be(customerBefore.Id);
            customerAfter.Name.Should().Be(customerBefore.Name);
            customerAfter.Document.Should().Be(customerBefore.Document);
            customerAfter.Email.Should().Be(customerBefore.Email);
            customerAfter.Version.Should().Be(customerBefore.Version);
            customerAfter.IsActive.Should().BeTrue();

            var supplyAfter = await ReadAsync<SupplyResponse>(await owner.GetAsync($"/api/supplies/{supplyBefore.Id}"));
            supplyAfter.Id.Should().Be(supplyBefore.Id);
            supplyAfter.Code.Should().Be(supplyBefore.Code);
            supplyAfter.Active.Should().Be(supplyBefore.Active);
            supplyAfter.BaseUnit.Should().Be(supplyBefore.BaseUnit);
            supplyAfter.CurrentStockBaseUnit.Should().Be(supplyBefore.CurrentStockBaseUnit);
            supplyAfter.Version.Should().Be(supplyBefore.Version);
            supplyAfter.LatestPurchaseUnitCost.Should().Be(supplyBefore.LatestPurchaseUnitCost);

            var movementsAfter = await ReadAsync<InventoryMovementListResponse>(await owner.GetAsync($"/api/supplies/{supplyBefore.Id}/inventory/movements?page=1&pageSize=20"));
            var movementAfter = movementsAfter.Items.Should().ContainSingle().Which;
            movementAfter.Id.Should().Be(movementBefore.Id);
            movementAfter.Type.Should().Be(movementBefore.Type);
            movementAfter.QuantityDeltaBaseUnit.Should().Be(movementBefore.QuantityDeltaBaseUnit);
            movementAfter.UnitCostSnapshot.Should().Be(movementBefore.UnitCostSnapshot);
            movementAfter.TotalCostSnapshot.Should().Be(movementBefore.TotalCostSnapshot);

            var settingsAfter = await ReadAsync<AppSettingResponse[]>(await owner.GetAsync("/api/settings"));
            var wastageAfter = settingsAfter.Single(x => x.Key == WastageKey);
            wastageAfter.Value.Should().Be(wastageValueBefore);
            wastageAfter.Version.Should().Be(wastageVersionBefore, "the S5 upgrade must not touch an unrelated Setting's row");

            // ---- Cost basis continuity: a NEW S5 Product built on the PRESERVED S4 acquisition
            // history must resolve the exact same weighted-average basis the S4 purchase established.
            var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products",
                new ProductCreateRequest("S4-BOOT-PROD", "Produto pós-upgrade", null)));
            product = await ReadAsync<ProductResponse>(await owner.PutAsync($"/api/products/{product.Id}/recipe",
                new ProductRecipeUpdateRequest(null, null, null, null, null, 1, null,
                    [new ProductRecipeMaterialLineRequest(supplyBefore.Id, 100m, SupplyBaseUnit.Gram, null, null)], [], product.Version)));
            var cost = await ReadAsync<CostCalculationResult>(await owner.GetAsync($"/api/products/{product.Id}/cost"));
            var material = cost.Materials.Should().ContainSingle().Which;
            material.UnitCostBaseUnit.Should().Be(0.1m, "the weighted-average basis from the preserved S4 purchase receipt must still resolve correctly");
            material.CostSource.Should().Be(MaterialCostSource.WEIGHTED_AVERAGE_ACQUISITION);
        }

        // ================= Stage 4: idempotency — reboot a SECOND time against the SAME upgraded database =================
        await using (var s5FactorySecond = NewFactory())
        {
            using var warmup = s5FactorySecond.CreateHttpsClient();
            (await warmup.GetAsync("/health/live")).EnsureSuccessStatusCode();

            await using var db = CreateContext();
            (await db.Set<SalesChannel>().CountAsync(x => x.Code == "DIRECT")).Should().Be(1,
                "a second application startup against the same database must never create a duplicate DIRECT channel");
            (await db.Set<FeeRule>().CountAsync(x => x.SalesChannelId == directChannelId)).Should().Be(1,
                "a second startup must never create a duplicate FeeRule for the canonical channel");
            var versionsForDirect = await db.Set<FeeRuleVersion>().Where(x => x.FeeRuleId ==
                db.Set<FeeRule>().Where(r => r.SalesChannelId == directChannelId).Select(r => r.Id).Single()).ToListAsync();
            versionsForDirect.Should().ContainSingle("no duplicate or overlapping zero-fee version may be introduced by re-seeding");
            versionsForDirect[0].CommissionPercent.Should().Be(0m);
            versionsForDirect[0].FixedFee.Should().Be(0m);

            (await db.Set<Customer>().CountAsync(x => x.Id == customerId)).Should().Be(1);
            (await db.Set<Supply>().CountAsync(x => x.Id == supplyBefore.Id)).Should().Be(1);
        }
    }
}
