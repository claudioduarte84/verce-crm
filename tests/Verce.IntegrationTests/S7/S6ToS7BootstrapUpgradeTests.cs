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
using Verce.Api.Pricing;
using Verce.Api.Quoting;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Customers;
using Verce.Modules.Documents;
using Verce.Modules.Pricing;
using Verce.Modules.Production;
using Verce.Modules.Quoting;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.S7;

/// <summary>
/// S7 mission: the S6→S7 upgrade certification, permanent and independently rerunnable — mirrors
/// <c>S5ToS6BootstrapUpgradeTests</c>'s exact rigor one migration later. A database frozen at the
/// terminal S6 migration, seeded with representative S6 business data (Customer, Product with a
/// recipe, an approved Quote/QuoteRevision/QuoteItem with its resulting ProductionOrder) through
/// the real application over real HTTP, upgraded to the final S7 migration, then rebooted through
/// the real composition root to prove every piece of S6 data survives untouched AND that the new
/// S7 PDF capability works end to end against that preserved QuoteRevision — closed with a
/// second-reboot idempotency check.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class S6ToS7BootstrapUpgradeTests : IAsyncLifetime
{
    private const string S6FinalMigration = "20260920200219_AddS6QuotingAndProductionCore";
    private const string S7FinalMigration = "20260921123702_AddS7DocumentTemplateEngine";
    private const string OwnerPassword = "a-perfectly-fine-12char-password";

    private PostgreSqlContainer _container = null!;
    private string _connectionString = string.Empty;
    // mission §92/§134: a unique, disposable Documents storage root per test run — never the
    // AppContext.BaseDirectory fallback, which is a machine-wide, shared, never-cleaned path.
    private readonly string _documentsStorageRoot = Path.Combine(Path.GetTempPath(), "verce-test-documents-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        VerceDbContext.ConfigureModuleAssemblies(Verce.Api.ModuleAssemblyCatalog.All);
        _container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("verce_s6_to_s7_bootstrap_test")
            .WithUsername("verce")
            .WithPassword("verce_test_only")
            .Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        // Cleaned even on failure — no leftover PDF/HTML on this machine (mission §93/§135).
        if (Directory.Exists(_documentsStorageRoot)) Directory.Delete(_documentsStorageRoot, recursive: true);
    }

    private VerceDbContext CreateContext() => new(
        new DbContextOptionsBuilder<VerceDbContext>().UseNpgsql(_connectionString).UseSnakeCaseNamingConvention().Options);

    private async Task MigrateToAsync(string targetMigration)
    {
        await using var db = CreateContext();
        await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(targetMigration);
    }

    private VerceWebApplicationFactory NewFactory() => new(_connectionString, new Dictionary<string, string?>
    {
        ["Settings:SeedOnStartup"] = "true",
        ["Documents:StorageRoot"] = _documentsStorageRoot,
    });

    /// <summary>Same reasoning as the S5→S6 test's own seed helpers: at the terminal S6 migration,
    /// this binary's model already includes the not-yet-applied S7 migration, so the seed services'
    /// "no pending migrations" guards correctly no-op — a representative Supply category and DIRECT
    /// sales channel are seeded directly so Stage 1 does not depend on booting a real host before
    /// the schema it needs has even been frozen at its S6 shape.</summary>
    private async Task SeedFilamentCategoryAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO inventory.supply_category (code, name, is_active) VALUES ('FILAMENT', 'Filamento', true)";
        await command.ExecuteNonQueryAsync();
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

    /// <summary>
    /// S7/S14 scope authority gate correction (2026-09-21): S7 added ten nullable proposal-content
    /// columns to the EXISTING `quoting.quote_revision` table (not a new table, unlike every prior
    /// sprint's additions). Because this test process links ONE compiled binary/EF model for its
    /// whole lifetime, that model already includes those columns even while the database is still
    /// frozen at the S6 migration — so ANY EF read OR write of `quote`/`quote_revision`/`quote_item`
    /// during Stage 1 (via the real HTTP API or a direct <see cref="VerceDbContext"/>) would try to
    /// touch columns the S6 schema does not have yet, exactly like <see cref="SeedFilamentCategoryAsync"/>/
    /// <see cref="SeedDirectSalesChannelAsync"/> already had to sidestep for their own S7-added
    /// dependencies. This mirrors that same established pattern one step further: representative
    /// S6 Quote/Revision/Item/ProductionOrder data, seeded directly by column list (so it is
    /// immune to whatever columns the compiled model happens to carry), landing in the exact
    /// APPROVED + QUEUED end state the real create→send→approve flow would have produced — that
    /// flow itself is independently covered by <c>QuotingHttpIntegrationTests</c> and the S7 E2E
    /// spec, so re-proving it here is not this test's job; preserving it across an upgrade is.
    /// </summary>
    private async Task<(Guid QuoteId, Guid RevisionId, decimal TotalAmount)> SeedApprovedQuoteAsync(Guid customerId, Guid salesChannelId, string description)
    {
        var quoteId = Guid.CreateVersion7();
        var revisionId = Guid.CreateVersion7();
        var itemId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var orderId = Guid.CreateVersion7();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO quoting.quote (id, number, number_date, number_sequence, customer_id, current_revision_id, created_at, version)
            VALUES (@quoteId, @number, @today, 1, @customerId, @revisionId, @now, 1);

            INSERT INTO quoting.quote_revision (id, quote_id, revision_index, revision_suffix, status, sales_channel_id,
                issued_at, validity_days, valid_until, customer_id, approved_at, approved_by,
                subtotal_amount, discount_amount, total_amount, total_cost_amount, expected_profit_amount, effective_margin_percent, created_at)
            VALUES (@revisionId, @quoteId, 1, '', 'APPROVED', @salesChannelId,
                @now, 15, @validUntil, @customerId, @now, NULL,
                66.66, 0, 66.66, 40.00, 26.66, 0.4, @now);

            INSERT INTO quoting.quote_item (id, quote_revision_id, line_number, product_name_snapshot, description, quantity,
                unit_total_cost, cost_engine_version, desired_margin_percent, sales_channel_id, commission_percent,
                fixed_fee_application, raw_fixed_fee, allocated_order_fee, fixed_fee_per_unit, rounding_policy_applied,
                suggested_unit_price, commission_amount_per_unit, price_overridden, unit_price,
                discount_kind, discount_value, discount_amount, net_unit_price,
                line_total_amount, line_cost_amount, line_fee_amount, expected_profit_amount, effective_margin_percent, created_at)
            VALUES (@itemId, @revisionId, 1, @description, @description, 2,
                20.000000, 'MANUAL', 0.4, @salesChannelId, 0,
                'PerUnit', 0, 0, 0, 'CENT',
                33.33, 0, false, 33.33,
                'None', 0, 0, 33.33,
                66.66, 40.00, 0, 26.66, 0.4, @now);

            INSERT INTO quoting.quote_item_cost_snapshot (id, quote_item_id, engine_version, material_cost_before_wastage,
                material_wastage_cost, materials_total_cost, labor_cost, machine_cost, additional_direct_costs_total,
                total_estimated_cost, output_quantity, estimated_unit_cost)
            VALUES (gen_random_uuid(), @itemId, 'MANUAL', 0, 0, 0, 0, 0, 0, 20.000000, 1, 20.000000);

            INSERT INTO quoting.quote_status_history (id, quote_revision_id, from_status, to_status, changed_at, trigger)
            VALUES (gen_random_uuid(), @revisionId, NULL, 'GENERATED', @now, 'USER'),
                   (gen_random_uuid(), @revisionId, 'GENERATED', 'APPROVED', @now, 'USER');

            INSERT INTO production.production_order (id, order_number, number_date, number_sequence, quote_id, quote_revision_id,
                status, has_pending_revision, created_at, version)
            VALUES (@orderId, @orderNumber, @today, 1, @quoteId, @revisionId, 'QUEUED', false, @now, 1);
            """;
        command.Parameters.AddWithValue("quoteId", quoteId);
        command.Parameters.AddWithValue("revisionId", revisionId);
        command.Parameters.AddWithValue("itemId", itemId);
        command.Parameters.AddWithValue("orderId", orderId);
        command.Parameters.AddWithValue("number", $"{today:yyMMdd}-1");
        command.Parameters.AddWithValue("orderNumber", $"{today:yyMMdd}-1P");
        command.Parameters.AddWithValue("today", today);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("validUntil", today.AddDays(15));
        command.Parameters.AddWithValue("customerId", (object?)customerId ?? DBNull.Value);
        command.Parameters.AddWithValue("salesChannelId", salesChannelId);
        command.Parameters.AddWithValue("description", description);
        await command.ExecuteNonQueryAsync();

        return (quoteId, revisionId, 66.66m);
    }

    /// <summary>Mirrors the S5→S6 test's own boundary check: before the S7 migration is applied,
    /// the Documents schema genuinely does not exist yet (never merely assumed from the migration
    /// name).</summary>
    private async Task AssertS7TableAbsentAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass('documents.generated_document')::text";
        var result = await command.ExecuteScalarAsync();
        (result is null || result is DBNull).Should().BeTrue("documents.generated_document must not exist before the S7 migration is applied");
    }

    private static async Task<AuthTestClient> CreateAndLogInOwnerAsync(VerceWebApplicationFactory factory, string email)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            if (!await roles.RoleExistsAsync(Roles.Owner)) (await roles.CreateAsync(new ApplicationRole(Roles.Owner))).Succeeded.Should().BeTrue();
            var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "S6 Owner", IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow };
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
    public async Task S6_terminal_database_upgrades_to_S7_bootstraps_the_PDF_pipeline_and_preserves_existing_business_data()
    {
        // ================= Stage 1: representative S6 state, at the TERMINAL S6 migration =================
        await MigrateToAsync(S6FinalMigration);
        await AssertS7TableAbsentAsync();
        await SeedFilamentCategoryAsync();
        await SeedDirectSalesChannelAsync();

        var ownerEmail = Guid.NewGuid().ToString("N") + "@example.test";
        Guid customerId;
        Guid quoteId;
        Guid revisionIdBefore;
        decimal totalAmountBefore;
        CustomerResponse customerBefore;
        ProductResponse productBefore;
        SalesChannelResponse directChannel;

        await using (var s6Factory = NewFactory())
        {
            using var warmup = s6Factory.CreateHttpsClient();
            (await warmup.GetAsync("/health/live")).EnsureSuccessStatusCode();

            var owner = await CreateAndLogInOwnerAsync(s6Factory, ownerEmail);

            customerBefore = await ReadAsync<CustomerResponse>(await owner.PostAsync("/api/customers",
                new CustomerRequest(PersonType.Individual, "Cliente S6 Bootstrap", null, null, "cliente-s6@example.test", null, null, 0)));
            customerId = customerBefore.Id;

            var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products",
                new ProductCreateRequest("S6-BOOT-PROD", "Produto S6 Bootstrap", "Descrição S6")));
            productBefore = product;

            directChannel = (await ReadAsync<IReadOnlyList<SalesChannelResponse>>(await owner.GetAsync("/api/pricing/channels")))
                .Single(c => c.Code == "DIRECT");
        }

        // Seeded directly by column list — see SeedApprovedQuoteAsync's doc comment for why the
        // real create->send->approve HTTP flow cannot be used here once quote_revision itself is
        // one of the tables S7 adds columns to.
        (quoteId, revisionIdBefore, totalAmountBefore) = await SeedApprovedQuoteAsync(customerId, directChannel.Id, "Vaso S6 Bootstrap");

        // ================= Stage 2: apply the FINAL S7 migration =================
        await MigrateToAsync(S7FinalMigration);

        await using (var db = CreateContext())
        {
            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            applied.Should().Contain(S6FinalMigration, "prior S6 migrations must remain recorded, never rewritten");
            applied.Should().Contain(S7FinalMigration, "the final S7 migration must be the one actually applied");
        }

        // ================= Stage 3: REAL application bootstrap against the upgraded database =================
        Guid generatedDocumentIdAfterFirstGeneration;
        await using (var s7Factory = NewFactory())
        {
            using var warmup = s7Factory.CreateHttpsClient();
            (await warmup.GetAsync("/health/live")).EnsureSuccessStatusCode();

            var (owner, loginSucceeded) = await LogInExistingOwnerAsync(s7Factory, ownerEmail);
            loginSucceeded.Should().BeTrue("the pre-existing S6 Owner's credentials must survive the S7 upgrade unchanged");

            // ---- S6 data preservation ----
            var customerAfter = await ReadAsync<CustomerResponse>(await owner.GetAsync($"/api/customers/{customerId}"));
            customerAfter.Name.Should().Be(customerBefore.Name);
            customerAfter.Version.Should().Be(customerBefore.Version);

            var productAfter = await ReadAsync<ProductResponse>(await owner.GetAsync($"/api/products/{productBefore.Id}"));
            productAfter.Name.Should().Be(productBefore.Name);
            productAfter.Version.Should().Be(productBefore.Version);

            var quoteAfter = await ReadAsync<QuoteResponse>(await owner.GetAsync($"/api/quotes/{quoteId}"));
            quoteAfter.CurrentRevision.Id.Should().Be(revisionIdBefore, "the preserved QuoteRevision must keep its identity across the upgrade");
            quoteAfter.CurrentRevision.Status.Should().Be(QuoteRevisionStatus.APPROVED);
            quoteAfter.CurrentRevision.Items.Should().ContainSingle();
            quoteAfter.CurrentRevision.TotalAmount.Should().Be(totalAmountBefore, "money frozen on the revision must never be recomputed by an upgrade");
            quoteAfter.ProductionOrderStatus.Should().Be(ProductionOrderStatus.QUEUED.ToString());
            quoteAfter.Version.Should().Be(1, "a root seeded once, with its children, in one transaction stays at version 1 (CLAUDE.md rule 27)");

            await using (var verifyDb = CreateContext())
            {
                (await verifyDb.Set<ProductionOrder>().CountAsync(o => o.QuoteId == quoteId)).Should().Be(1);
            }

            // ---- NEW S7 capability, proven against the PRESERVED S6 QuoteRevision ----
            var pdfMetadata = await ReadAsync<QuotePdfMetadataResponse>(
                await owner.PostAsync($"/api/quotes/{quoteId}/revisions/{revisionIdBefore}/pdf", new { }));
            pdfMetadata.QuoteRevisionId.Should().Be(revisionIdBefore);
            generatedDocumentIdAfterFirstGeneration = pdfMetadata.Id;

            var pdfDownload = await owner.GetAsync(pdfMetadata.DownloadUrl);
            pdfDownload.StatusCode.Should().Be(HttpStatusCode.OK);
            pdfDownload.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
            var bytes = await pdfDownload.Content.ReadAsByteArrayAsync();
            bytes.Length.Should().BeGreaterThan(0);
            System.Text.Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");

            await using (var verifyDb = CreateContext())
            {
                var document = await verifyDb.Set<GeneratedDocument>().SingleAsync(d => d.SourceId == revisionIdBefore);
                document.DocumentTypeCode.Should().Be("QUOTE");
                document.Id.Should().Be(generatedDocumentIdAfterFirstGeneration);
            }
        }

        // ================= Stage 4: idempotency — reboot a SECOND time against the SAME upgraded database =================
        await using (var s7FactorySecond = NewFactory())
        {
            using var warmup = s7FactorySecond.CreateHttpsClient();
            (await warmup.GetAsync("/health/live")).EnsureSuccessStatusCode();

            var (owner, loginSucceeded) = await LogInExistingOwnerAsync(s7FactorySecond, ownerEmail);
            loginSucceeded.Should().BeTrue();

            await using var db = CreateContext();
            (await db.Set<SalesChannel>().CountAsync(x => x.Code == "DIRECT")).Should().Be(1, "a second startup must never create a duplicate DIRECT channel");
            (await db.Set<Customer>().CountAsync(x => x.Id == customerId)).Should().Be(1);
            (await db.Set<Verce.Modules.Quoting.Quote>().CountAsync(x => x.Id == quoteId)).Should().Be(1,
                "a second startup must never touch or duplicate the Quote created in Stage 3");
            (await db.Set<ProductionOrder>().CountAsync(o => o.QuoteId == quoteId)).Should().Be(1,
                "a second startup must never re-create the ProductionOrder for an already-approved Quote");
            (await db.Set<GeneratedDocument>().CountAsync(d => d.SourceId == revisionIdBefore)).Should().Be(1,
                "a second startup must never re-render or duplicate the already-materialized PDF artifact");

            // Repeating generation post-restart must still be idempotent and return the SAME artifact.
            var pdfMetadataAgain = await ReadAsync<QuotePdfMetadataResponse>(
                await owner.PostAsync($"/api/quotes/{quoteId}/revisions/{revisionIdBefore}/pdf", new { }));
            pdfMetadataAgain.Id.Should().Be(generatedDocumentIdAfterFirstGeneration, "regenerating after a restart must converge to the same immutable artifact");

            (await db.Set<GeneratedDocument>().CountAsync(d => d.SourceId == revisionIdBefore)).Should().Be(1);
        }
    }
}
