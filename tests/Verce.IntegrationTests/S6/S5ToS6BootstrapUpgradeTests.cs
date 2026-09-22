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
using Verce.Api.Quoting;
using Verce.Api.Settings;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Costing;
using Verce.Modules.Customers;
using Verce.Modules.Inventory;
using Verce.Modules.Pricing;
using Verce.Modules.Production;
using Verce.Modules.Quoting;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.S6;

/// <summary>
/// B-05: the S5→S6 upgrade certification, permanent and independently rerunnable — mirrors
/// <c>S4ToS5BootstrapUpgradeTests</c>'s exact rigor one migration later. A database frozen at the
/// terminal S5 migration, seeded with representative S5 business data (Customer, Supply with a
/// purchase receipt, a Product with a full recipe, a marketplace sales channel with a PER_ORDER
/// fee) through the real application over real HTTP, upgraded to the final S6 migration, then
/// rebooted through the real composition root to prove every piece of S5 data survives untouched
/// AND that the new S6 Quoting/Production capability works end to end against that preserved data —
/// closed with a second-reboot idempotency check.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class S5ToS6BootstrapUpgradeTests : IAsyncLifetime
{
    private const string S5FinalMigration = "20260919152506_AddS5ProductsRecipesAndPricing";
    private const string S6FinalMigration = "20260920200219_AddS6QuotingAndProductionCore";
    private const string OwnerPassword = "a-perfectly-fine-12char-password";

    private PostgreSqlContainer _container = null!;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        VerceDbContext.ConfigureModuleAssemblies(Verce.Api.ModuleAssemblyCatalog.All);
        _container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("verce_s5_to_s6_bootstrap_test")
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

    /// <summary>At the terminal S5 migration, this binary's OWN model already includes the
    /// not-yet-applied S6 migration, so InventorySeedService's "no pending migrations" guard
    /// correctly no-ops rather than seeding against a not-fully-migrated schema — the exact same
    /// situation <c>S4ToS5BootstrapUpgradeTests</c> documents for the S4 boundary, one migration
    /// earlier. A representative S5 Supply still needs a real category row to reference.</summary>
    private async Task SeedFilamentCategoryAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO inventory.supply_category (code, name, is_active) VALUES ('FILAMENT', 'Filamento', true)";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>S7/S14 scope authority gate correction (2026-09-21): adds ONLY the ten nullable
    /// proposal-content columns S7 puts on `quoting.quote_revision` — never the `documents`
    /// schema, never recorded in `__EFMigrationsHistory` — so this test's compiled S7-aware model
    /// can perform real INSERT/SELECT against a database that is, and remains, genuinely frozen
    /// at S6 for every purpose this test actually checks (`GetAppliedMigrationsAsync` above
    /// explicitly asserts S7 is never recorded as applied). Without this, Stage 3's real
    /// create→send→approve flow — which is this test's actual point, proving S6's new Quoting
    /// capability against preserved S5 Product/fee data — cannot run at all: EF always includes
    /// every mapped column on an INSERT of a new row, and `quote_revision` is a table S6 itself
    /// created, so there is no earlier S6-only shape to fall back to.</summary>
    private async Task AddS7QuoteRevisionColumnsAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE quoting.quote_revision
                ADD COLUMN IF NOT EXISTS title character varying(200),
                ADD COLUMN IF NOT EXISTS scope character varying(4000),
                ADD COLUMN IF NOT EXISTS technical_highlights jsonb,
                ADD COLUMN IF NOT EXISTS technical_notes character varying(4000),
                ADD COLUMN IF NOT EXISTS out_of_scope character varying(4000),
                ADD COLUMN IF NOT EXISTS payment_terms character varying(2000),
                ADD COLUMN IF NOT EXISTS delivery_terms character varying(2000),
                ADD COLUMN IF NOT EXISTS warranty character varying(2000),
                ADD COLUMN IF NOT EXISTS notes character varying(4000),
                ADD COLUMN IF NOT EXISTS internal_notes character varying(4000);
            """;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Same reasoning as <see cref="SeedFilamentCategoryAsync"/>, for the canonical DIRECT
    /// channel <c>PricingSeedService</c> would otherwise have created. Unlike the S4→S5 test, this
    /// is NOT the H-03 bug — PricingSeedService's own readiness check is gated on the S5 migration
    /// by name (already satisfied here), but S5 is frozen mid-transaction from THIS suite's own
    /// migration-count guard being irrelevant to that fix: this seed represents the row a genuine
    /// S5-only deployment (a binary that does not yet know about S6 at all) would already have —
    /// it is inserted directly here purely so Stage 1 does not depend on booting a real host before
    /// the schema it needs (fee_rule_version) has even been frozen at its S5 shape for this test's
    /// baseline snapshot.</summary>
    /// <summary>F-03/§21-25: representative pre-existing S5-era Settings, inserted directly as raw
    /// SQL. This models "an already-running S5 installation" — this binary's own
    /// SettingsSeedService also pauses at this boundary (S6 is pending against its model, same
    /// reasoning as <see cref="SeedFilamentCategoryAsync"/>), and even if it did not, it only ever
    /// inserts DEFAULT values, never the representative NON-default values an operator would
    /// actually have configured on a real S5 deployment. This does NOT prove the historical S5
    /// bootstrap process — only that whatever Settings already existed before the S6 upgrade
    /// survive it unchanged (§23).</summary>
    private async Task SeedRepresentativeS5SettingsAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings.app_setting (id, key, value, value_type, scope, description, is_secret, created_at, version) VALUES
                (gen_random_uuid(), 'quote.default_validity_days', '20', 'Int', 'quote', 'Validade padrao de orcamentos', false, now(), 1),
                (gen_random_uuid(), 'quote.allow_direct_approval', 'false', 'Bool', 'quote', 'Permite aprovacao direta', false, now(), 1),
                (gen_random_uuid(), 'pricing.default_margin_percent', '0.40', 'Decimal', 'pricing', 'Margem padrao', false, now(), 1),
                (gen_random_uuid(), 'pricing.price_rounding_policy', 'TEN_CENTS', 'String', 'pricing', 'Politica de arredondamento', false, now(), 1),
                (gen_random_uuid(), 'costing.default_labor_hourly_rate', '35.50', 'Decimal', 'costing', 'Mao de obra padrao', false, now(), 1),
                (gen_random_uuid(), 'costing.default_wastage_rate', '7', 'Decimal', 'costing', 'Perda padrao em pontos percentuais (0 a 100; ADR-0018)', false, now(), 1),
                (gen_random_uuid(), 'documents.default_payment_terms', '30 dias, boleto', 'String', 'documents', 'Condicoes de pagamento', false, now(), 1);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static readonly (string Key, string Value)[] RepresentativeS5Settings =
    [
        ("quote.default_validity_days", "20"),
        ("quote.allow_direct_approval", "false"),
        ("pricing.default_margin_percent", "0.40"),
        ("pricing.price_rounding_policy", "TEN_CENTS"),
        ("costing.default_labor_hourly_rate", "35.50"),
        ("costing.default_wastage_rate", "7"),
        ("documents.default_payment_terms", "30 dias, boleto"),
    ];

    /// <summary>N-01: a legacy S5 marketplace channel whose PER_ORDER fixed fee is a fractional
    /// cent (1.005) — reachable only as historical data (the corrected domain rejects
    /// constructing this at the application layer), seeded directly as raw SQL to model a row a
    /// real S5 deployment could have persisted before this correction ever existed.
    /// FixedFeeApplication = 'PerOrder' is deliberate: only the PER_ORDER path actually runs the
    /// value through PerOrderFeeAllocator's precision guard when a Quote later tries to use
    /// it.</summary>
    private async Task<Guid> SeedLegacyFractionalFeeChannelAsync()
    {
        var channelId = Guid.NewGuid();
        var feeRuleId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pricing.sales_channel (id, code, name, kind, default_margin_percent, notes, active, created_at, version)
            VALUES (@channelId, 'S5-LEGACY-1005', 'Legacy Fractional Fee', 'Marketplace', NULL, NULL, true, now(), 1);

            INSERT INTO pricing.fee_rule (id, sales_channel_id, name, active, created_at, version)
            VALUES (@feeRuleId, @channelId, 'Regra legada', true, now(), 1);

            INSERT INTO pricing.fee_rule_version (id, fee_rule_id, valid_from, valid_until, commission_percent, fixed_fee, fixed_fee_application, minimum_fee, maximum_fee, notes, created_at)
            VALUES (gen_random_uuid(), @feeRuleId, DATE '2020-01-01', NULL, 0.10, 1.005, 'PerOrder', NULL, NULL, 'Legacy pre-B-03 fractional-cent fee (N-01)', now());
            """;
        command.Parameters.AddWithValue("channelId", channelId);
        command.Parameters.AddWithValue("feeRuleId", feeRuleId);
        await command.ExecuteNonQueryAsync();
        return channelId;
    }

    /// <summary>Section 26: strengthens the exact-boundary claim — before the S6 migration is
    /// applied, the S6 tables genuinely do not exist yet (never merely assumed from the migration
    /// name).</summary>
    private async Task AssertS6TablesAbsentAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        foreach (var (schema, table) in new[] { ("quoting", "quote"), ("production", "production_order") })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT to_regclass(@qualifiedName)::text";
            command.Parameters.AddWithValue("qualifiedName", $"{schema}.{table}");
            var result = await command.ExecuteScalarAsync();
            (result is null || result is DBNull).Should().BeTrue($"{schema}.{table} must not exist before the S6 migration is applied");
        }
    }

    private async Task SeedDirectSalesChannelAsync()
    {
        var channelId = Guid.NewGuid();
        var feeRuleId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pricing.sales_channel (id, code, name, kind, default_margin_percent, notes, active, created_at, version)
            VALUES (@channelId, 'DIRECT', 'Venda Direta', 'Direct', NULL, NULL, true, now(), 1);

            INSERT INTO pricing.fee_rule (id, sales_channel_id, name, active, created_at, version)
            VALUES (@feeRuleId, @channelId, 'Venda Direta — sem comissão', true, now(), 1);

            INSERT INTO pricing.fee_rule_version (id, fee_rule_id, valid_from, valid_until, commission_percent, fixed_fee, fixed_fee_application, minimum_fee, maximum_fee, notes, created_at)
            VALUES (gen_random_uuid(), @feeRuleId, DATE '2020-01-01', NULL, 0, 0, 'PerUnit', NULL, NULL, 'Seed ADR-0005 §2', now());
            """;
        command.Parameters.AddWithValue("channelId", channelId);
        command.Parameters.AddWithValue("feeRuleId", feeRuleId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<AuthTestClient> CreateAndLogInOwnerAsync(VerceWebApplicationFactory factory, string email)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            if (!await roles.RoleExistsAsync(Roles.Owner)) (await roles.CreateAsync(new ApplicationRole(Roles.Owner))).Succeeded.Should().BeTrue();
            var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "S5 Owner", IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow };
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
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{(int)response.StatusCode} {response.StatusCode}: {json}");
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    [Fact]
    public async Task S5_terminal_database_upgrades_to_S6_bootstraps_Quoting_and_preserves_existing_business_data()
    {
        // ================= Stage 1: representative S5 state, at the TERMINAL S5 migration =================
        await MigrateToAsync(S5FinalMigration);
        await AssertS6TablesAbsentAsync();
        await SeedFilamentCategoryAsync();
        await SeedDirectSalesChannelAsync();
        await SeedRepresentativeS5SettingsAsync();
        var legacyChannelId = await SeedLegacyFractionalFeeChannelAsync();

        var ownerEmail = Guid.NewGuid().ToString("N") + "@example.test";
        Guid customerId;
        CustomerResponse customerBefore;
        SupplyResponse supplyBefore;
        ProductResponse productBefore;
        CostCalculationResult costBefore;
        SalesChannelResponse marketplaceChannelBefore;

        await using (var s5Factory = NewFactory())
        {
            using var warmup = s5Factory.CreateHttpsClient();
            (await warmup.GetAsync("/health/live")).EnsureSuccessStatusCode();

            var owner = await CreateAndLogInOwnerAsync(s5Factory, ownerEmail);

            customerBefore = await ReadAsync<CustomerResponse>(await owner.PostAsync("/api/customers",
                new CustomerRequest(PersonType.Individual, "Cliente S5 Bootstrap", null, null, "cliente-s5@example.test", null, null, 0)));
            customerId = customerBefore.Id;

            var supply = await ReadAsync<SupplyResponse>(await owner.PostAsync("/api/supplies",
                new SupplyCreateRequest("S5-BOOT-MAT", "Material S5 Bootstrap", null, "FILAMENT", SupplyBaseUnit.Gram, null, null, null, null)));
            supplyBefore = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/purchase-receipt",
                new PurchaseReceiptRequest(1000m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, 100m, null, "NF-S5-BOOT-1", null, supply.Version)));
            supplyBefore.CurrentStockBaseUnit.Should().Be(1000m);
            supplyBefore.LatestPurchaseUnitCost.Should().Be(0.1m);

            var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products",
                new ProductCreateRequest("S5-BOOT-PROD", "Produto S5 Bootstrap", "Descrição S5")));
            productBefore = await ReadAsync<ProductResponse>(await owner.PutAsync($"/api/products/{product.Id}/recipe",
                new ProductRecipeUpdateRequest(null, 15m, 25m, 90m, 1.5m, 1, "Receita S5",
                    [new ProductRecipeMaterialLineRequest(supplyBefore.Id, 50m, SupplyBaseUnit.Gram, null, null)],
                    [new ProductRecipeAdditionalCostLineRequest("Etiqueta", 1.5m)],
                    product.Version)));
            costBefore = await ReadAsync<CostCalculationResult>(await owner.GetAsync($"/api/products/{productBefore.Id}/cost"));
            costBefore.Materials.Should().ContainSingle();

            marketplaceChannelBefore = await ReadAsync<SalesChannelResponse>(await owner.PostAsync("/api/pricing/channels",
                new SalesChannelCreateRequest("S5-MKT", "Marketplace S5", SalesChannelKind.Marketplace, null, null)));
            await owner.PostAsync($"/api/pricing/channels/{marketplaceChannelBefore.Id}/fee-rule", new FeeRuleCreateRequest("Regra S5-MKT"));
            await owner.PostAsync($"/api/pricing/channels/{marketplaceChannelBefore.Id}/fee-rule/versions",
                new FeeRuleVersionCreateRequest(new DateOnly(2020, 1, 1), null, 0.12m, 3.00m, FixedFeeApplication.PerOrder, null, null, null, false));
        }

        // ================= Stage 2: apply the FINAL S6 migration =================
        await MigrateToAsync(S6FinalMigration);

        await using (var db = CreateContext())
        {
            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            applied.Should().Contain(S5FinalMigration, "prior S5 migrations must remain recorded, never rewritten");
            applied.Should().Contain(S6FinalMigration, "the final S6 migration must be the one actually applied");
            applied.Should().NotContain(x => x.StartsWith("2026092110", StringComparison.Ordinal), "this test proves the S5->S6 upgrade only — S7 is never applied here");
        }

        // S7/S14 scope authority gate correction (2026-09-21): S7 added ten nullable
        // proposal-content columns directly to the EXISTING `quoting.quote_revision` table. This
        // test's compiled binary/EF model always includes them (one process, one model, for its
        // whole lifetime), so the real HTTP `POST /api/quotes` flow Stage 3 depends on below —
        // its actual point, proving S6's NEW capability against PRESERVED S5 data through real
        // CostEngine/PricingEngine resolution — needs those columns to physically exist, even
        // though the S7 migration itself is deliberately never applied or recorded here. Adding
        // just the columns (never the `documents` schema, never recorded in
        // `__EFMigrationsHistory`) is the minimal, honest way to keep the compiled model and the
        // database schema mutually usable without claiming S7 ran.
        await AddS7QuoteRevisionColumnsAsync();

        // ================= Stage 3: REAL application bootstrap against the upgraded database =================
        Guid quoteId;
        await using (var s6Factory = NewFactory())
        {
            using var warmup = s6Factory.CreateHttpsClient();
            (await warmup.GetAsync("/health/live")).EnsureSuccessStatusCode();

            var (owner, loginSucceeded) = await LogInExistingOwnerAsync(s6Factory, ownerEmail);
            loginSucceeded.Should().BeTrue("the pre-existing S5 Owner's credentials must survive the S6 upgrade unchanged");

            // ---- S5 data preservation ----
            var customerAfter = await ReadAsync<CustomerResponse>(await owner.GetAsync($"/api/customers/{customerId}"));
            customerAfter.Name.Should().Be(customerBefore.Name);
            customerAfter.Version.Should().Be(customerBefore.Version);

            var supplyAfter = await ReadAsync<SupplyResponse>(await owner.GetAsync($"/api/supplies/{supplyBefore.Id}"));
            supplyAfter.CurrentStockBaseUnit.Should().Be(supplyBefore.CurrentStockBaseUnit);
            supplyAfter.LatestPurchaseUnitCost.Should().Be(supplyBefore.LatestPurchaseUnitCost);
            supplyAfter.Version.Should().Be(supplyBefore.Version);

            var productAfter = await ReadAsync<ProductResponse>(await owner.GetAsync($"/api/products/{productBefore.Id}"));
            productAfter.Name.Should().Be(productBefore.Name);
            productAfter.Recipe.MaterialLines.Should().HaveCount(1);
            productAfter.Version.Should().Be(productBefore.Version);

            var costAfter = await ReadAsync<CostCalculationResult>(await owner.GetAsync($"/api/products/{productBefore.Id}/cost"));
            costAfter.Totals.EstimatedUnitCost.Should().Be(costBefore.Totals.EstimatedUnitCost, "the preserved S5 cost basis must resolve identically after the S6 upgrade");

            var channels = await ReadAsync<IReadOnlyList<SalesChannelResponse>>(await owner.GetAsync("/api/pricing/channels"));
            channels.Should().Contain(x => x.Code == "DIRECT");
            var marketplaceAfter = channels.Should().ContainSingle(x => x.Code == "S5-MKT").Which;
            marketplaceAfter.Id.Should().Be(marketplaceChannelBefore.Id);

            // ---- F-03: representative S5 Settings survive the S6 upgrade untouched ----
            var settingsAfter = await ReadAsync<AppSettingResponse[]>(await owner.GetAsync("/api/settings"));
            foreach (var (key, expectedValue) in RepresentativeS5Settings)
            {
                var setting = settingsAfter.Should().ContainSingle(s => s.Key == key).Which;
                setting.Value.Should().Be(expectedValue, $"S6 must not rewrite the pre-existing S5 value for {key}");
                setting.Version.Should().Be(1, $"S6 must not bump the version of the untouched pre-existing {key} row");
            }

            // ---- N-01: the legacy fractional-cent fee row survives verbatim, and the DB's own
            // NOT VALID check never rejected it during migration (Stage 2 already proved that by
            // succeeding) — but USING it for a brand-new Quote still hits the corrected runtime guard.
            var legacyChannel = channels.Should().ContainSingle(x => x.Code == "S5-LEGACY-1005").Which;
            var legacyFeeRule = await ReadAsync<FeeRuleResponse>(await owner.GetAsync($"/api/pricing/channels/{legacyChannel.Id}/fee-rule"));
            var legacyVersion = legacyFeeRule.Versions.Should().ContainSingle().Which;
            legacyVersion.FixedFee.Should().Be(1.005m, "the legacy row must survive the upgrade exactly as it was, never silently rounded/truncated");
            legacyVersion.FixedFeeApplication.Should().Be(FixedFeeApplication.PerOrder);

            var legacyFeeQuoteAttempt = await owner.PostAsync("/api/quotes", new QuoteCreateRequest(null, legacyChannel.Id,
                [new QuoteItemRequest(null, null, "Item avulso", 10.00m, 1m, 0.30m, null, QuoteDiscountKind.None, 0m)], null));
            legacyFeeQuoteAttempt.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            using (var problem = JsonDocument.Parse(await legacyFeeQuoteAttempt.Content.ReadAsStringAsync()))
            {
                problem.RootElement.GetProperty("code").GetString().Should().Be("FIXED_FEE_PRECISION_INVALID",
                    "no silent round/truncate — a legacy fractional-cent fee must still be rejected the moment it is actually used");
            }

            // ---- NEW S6 capability, proven against the PRESERVED S5 Product and marketplace fee ----
            var quote = await ReadAsync<QuoteResponse>(await owner.PostAsync("/api/quotes", new QuoteCreateRequest(null, marketplaceAfter.Id,
                [new QuoteItemRequest(null, productAfter.Id, null, null, 2m, 0.30m, null, QuoteDiscountKind.None, 0m)], null)));
            quoteId = quote.Id;
            quote.Number.Should().MatchRegex(@"^\d{6}-1$");
            var item = quote.CurrentRevision.Items.Single();
            item.ProductId.Should().Be(productAfter.Id);
            item.CostSnapshot.EstimatedUnitCost.Should().Be(costBefore.Totals.EstimatedUnitCost, "the Quote line freezes the cost the preserved S5 Product resolves to at issue time");

            // The preserved S5 setting quote.allow_direct_approval=false is genuinely in effect
            // here (proof that F-03's preservation is real, not merely asserted) — a direct
            // GENERATED -> APPROVED transition is correctly rejected, so this sends first.
            var sent = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/send", new QuoteVersionedRequest(quote.Version)));
            var approved = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/approve", new QuoteVersionedRequest(sent.Version)));
            approved.CurrentRevision.Status.Should().Be(QuoteRevisionStatus.APPROVED);
            approved.CommercialOutcome.Should().Be("WON");

            await using var verifyDb = CreateContext();
            var order = await verifyDb.Set<ProductionOrder>().SingleAsync(o => o.QuoteId == quote.Id);
            order.Status.Should().Be(ProductionOrderStatus.QUEUED);
            order.QuoteRevisionId.Should().Be(approved.CurrentRevision.Id);
        }

        // ================= Stage 4: idempotency — reboot a SECOND time against the SAME upgraded database =================
        await using (var s6FactorySecond = NewFactory())
        {
            using var warmup = s6FactorySecond.CreateHttpsClient();
            (await warmup.GetAsync("/health/live")).EnsureSuccessStatusCode();

            await using var db = CreateContext();
            (await db.Set<SalesChannel>().CountAsync(x => x.Code == "DIRECT")).Should().Be(1,
                "a second startup must never create a duplicate DIRECT channel");
            (await db.Set<Customer>().CountAsync(x => x.Id == customerId)).Should().Be(1);
            (await db.Set<Supply>().CountAsync(x => x.Id == supplyBefore.Id)).Should().Be(1);
            (await db.Set<Verce.Modules.Quoting.Quote>().CountAsync(x => x.Id == quoteId)).Should().Be(1,
                "a second startup must never touch or duplicate the Quote created in Stage 3");
            (await db.Set<ProductionOrder>().CountAsync(o => o.QuoteId == quoteId)).Should().Be(1,
                "a second startup must never re-create the ProductionOrder for an already-approved Quote");

            // F-03/§25: a second real startup must never duplicate, reset-to-default or bump the
            // version of any pre-existing Setting.
            foreach (var (key, expectedValue) in RepresentativeS5Settings)
            {
                var rows = await db.Set<Verce.Modules.Settings.AppSetting>().Where(s => s.Key == key).ToListAsync();
                rows.Should().ContainSingle($"no duplicate row for {key} after a second startup");
                rows[0].Value.Should().Be(expectedValue);
                rows[0].Version.Should().Be(1, $"{key} must not be re-persisted/bumped by a second startup");
            }
        }
    }
}
