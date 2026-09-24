using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Verce.Modules.Catalog;
using Verce.Modules.Finance;
using Verce.Modules.Pricing;
using Verce.Modules.Sales;
using Verce.Modules.Settings;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.S8B;

/// <summary>Permanent terminal-S8A to S8B upgrade proof. The database exists only inside
/// this Testcontainers fixture and is disposed after the test.</summary>
public sealed class S8AToS8BUpgradeTests : IAsyncLifetime
{
    private const string S8AFinalMigration = "20260922154716_AddS8ASalesAndExpenses";
    private PostgreSqlContainer _container = null!;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        VerceDbContext.ConfigureModuleAssemblies(Verce.Api.ModuleAssemblyCatalog.All);
        _container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("verce_s8a_to_s8b")
            .WithUsername("verce")
            .WithPassword("verce_test_only")
            .Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private VerceDbContext Context() => new(new DbContextOptionsBuilder<VerceDbContext>()
        .UseNpgsql(_connectionString).UseSnakeCaseNamingConvention().Options);

    [Fact]
    public async Task Terminal_S8A_facts_upgrade_to_S8B_without_mutation_or_synthetic_commerce_data()
    {
        await using (var db = Context())
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(S8AFinalMigration);

        var now = DateTimeOffset.UtcNow;
        var channel = new SalesChannel("S8A-HIST", "Canal histórico S8A", SalesChannelKind.Marketplace, .30m, null);
        var product = new Product("S8A-PRODUCT", "Produto histórico S8A", null);
        product.Recipe.UpdateParameters(null, 30m, 20m, null, null, 1, null);
        var feeRule = new FeeRule(channel.Id, "Regra histórica S8A");
        var feeVersion = feeRule.AddVersion(new DateOnly(2020, 1, 1), null, .10m, 2m,
            FixedFeeApplication.PerUnit, null, null, null, SalesChannelKind.Marketplace,
            [new PriceBracketInput(0m, 100m, .12m, 3m, null, null, 1)]);
        var category = new ExpenseCategory("Categoria histórica S8A", AccountingTreatment.OPERATING_EXPENSE);
        var expense = new Expense(category.Id, "Despesa histórica S8A", 25m, new DateOnly(2026, 9, 22),
            AccountingTreatment.OPERATING_EXPENSE, salesChannelId: channel.Id);
        var sale = new Sale(810001, new DateOnly(2026, 9, 22), SaleSource.MANUAL_ENTRY, channel.Id,
            null, null, null, null, SaleFeeSource.LOCAL_RULE, null, null, now, 0m, null, null,
            [new SaleItemSnapshot(product.Id, null, product.Name, 2m, 60m, 0m, 120m, 10m, 20m, 14m, 86m, .716667m)],
            null, now);
        var setting = new AppSetting("commerce.listing_observation_retention_days", "180", AppSettingValueType.Int,
            "commerce", "Retenção de observações de listagem");
        await using (var db = Context())
        {
            db.AddRange(channel, product, feeRule, category, expense, sale, setting);
            await db.SaveChangesAsync();
        }

        var quote = Guid.NewGuid(); var revision = Guid.NewGuid(); var quoteItem = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO quoting.quote (id, number, number_date, number_sequence, customer_id, current_revision_id, created_at, version)
                VALUES (@quote, '260922-81', DATE '2026-09-22', 81, NULL, @revision, @now, 1);
                INSERT INTO quoting.quote_revision (id, quote_id, revision_index, revision_suffix, status, sales_channel_id,
                    issued_at, validity_days, valid_until, subtotal_amount, discount_amount, total_amount, total_cost_amount,
                    expected_profit_amount, effective_margin_percent, created_at)
                VALUES (@revision, @quote, 1, '', 'APPROVED', @channel, @now, 15, DATE '2026-10-07',
                    120.00, 0.00, 120.00, 20.00, 86.00, .716667, @now);
                INSERT INTO quoting.quote_item (id, quote_revision_id, line_number, product_id, product_name_snapshot, quantity,
                    unit_total_cost, cost_engine_version, desired_margin_percent, sales_channel_id, fee_rule_version_id,
                    commission_percent, fixed_fee_application, raw_fixed_fee, allocated_order_fee, fixed_fee_per_unit,
                    rounding_policy_applied, suggested_unit_price, commission_amount_per_unit, price_overridden, unit_price,
                    discount_kind, discount_value, discount_amount, net_unit_price, line_total_amount, line_cost_amount,
                    line_fee_amount, expected_profit_amount, effective_margin_percent, created_at)
                VALUES (@item, @revision, 1, @product, 'Produto histórico S8A', 2, 10.000000, 'S8A', .30, @channel,
                    @ruleVersion, .10, 'PerUnit', 2.000000, 0, 2.000000, 'CENT', 60.00, 8.00, false, 60.00,
                    'None', 0.00, 0.00, 60.00, 120.00, 20.00, 20.00, 80.00, .666667, @now);
                """;
            command.Parameters.AddWithValue("channel", channel.Id);
            command.Parameters.AddWithValue("product", product.Id);
            command.Parameters.AddWithValue("ruleVersion", feeVersion.Id);
            command.Parameters.AddWithValue("quote", quote);
            command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue("item", quoteItem);
            command.Parameters.AddWithValue("now", now);
            await command.ExecuteNonQueryAsync();
        }

        await using (var db = Context()) await db.Database.MigrateAsync();

        await using var proof = new NpgsqlConnection(_connectionString);
        await proof.OpenAsync();
        await using (var command = new NpgsqlCommand("SELECT name, active, version FROM pricing.sales_channel WHERE id=@id", proof))
        {
            command.Parameters.AddWithValue("id", channel.Id);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("Canal histórico S8A");
            reader.GetBoolean(1).Should().BeTrue();
            reader.GetInt64(2).Should().Be(channel.Version);
        }
        await using (var command = new NpgsqlCommand("SELECT name, default_treatment, version FROM finance.expense_category WHERE id=@id", proof))
        {
            command.Parameters.AddWithValue("id", category.Id);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("Categoria histórica S8A");
            reader.GetString(1).Should().Be("OPERATING_EXPENSE");
            reader.GetInt64(2).Should().Be(category.Version);
        }

        await using (var command = new NpgsqlCommand("SELECT name, active FROM catalog.product WHERE id=@id", proof))
        {
            command.Parameters.AddWithValue("id", product.Id); await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue(); reader.GetString(0).Should().Be(product.Name); reader.GetBoolean(1).Should().BeTrue();
        }
        await using (var command = new NpgsqlCommand("SELECT commission_percent, fixed_fee FROM pricing.fee_rule_version WHERE id=@id", proof))
        {
            command.Parameters.AddWithValue("id", feeVersion.Id); await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue(); reader.GetDecimal(0).Should().Be(.10m); reader.GetDecimal(1).Should().Be(2m);
        }
        await using (var command = new NpgsqlCommand("SELECT count(*) FROM pricing.price_bracket WHERE fee_rule_version_id=@id", proof))
        { command.Parameters.AddWithValue("id", feeVersion.Id); Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(1); }
        await using (var command = new NpgsqlCommand("SELECT net_amount FROM sales.sale WHERE id=@id", proof))
        { command.Parameters.AddWithValue("id", sale.Id); Convert.ToDecimal(await command.ExecuteScalarAsync()).Should().Be(120m); }
        await using (var command = new NpgsqlCommand("SELECT quantity, line_total_amount FROM sales.sale_item WHERE sale_id=@id", proof))
        {
            command.Parameters.AddWithValue("id", sale.Id); await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue(); reader.GetDecimal(0).Should().Be(2m); reader.GetDecimal(1).Should().Be(120m);
        }
        await using (var command = new NpgsqlCommand("SELECT amount FROM finance.expense WHERE id=@id", proof))
        { command.Parameters.AddWithValue("id", expense.Id); Convert.ToDecimal(await command.ExecuteScalarAsync()).Should().Be(25m); }
        await using (var command = new NpgsqlCommand("SELECT total_amount, total_cost_amount FROM quoting.quote_revision WHERE id=@id", proof))
        {
            command.Parameters.AddWithValue("id", revision); await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue(); reader.GetDecimal(0).Should().Be(120m); reader.GetDecimal(1).Should().Be(20m);
        }

        foreach (var table in new[]
        {
            "commerce.channel_offer", "commerce.marketplace_provider",
            "commerce.marketplace_account", "commerce.marketplace_listing",
            "commerce.marketplace_listing_observation", "commerce.product_commercial_profile",
            "commerce.product_commercial_image", "commerce.commercial_tag"
        })
        {
            await using var command = new NpgsqlCommand("SELECT to_regclass(@name)::text", proof);
            command.Parameters.AddWithValue("name", table);
            (await command.ExecuteScalarAsync()).Should().NotBeNull($"S8B table {table} must exist");
        }

        await using (var command = new NpgsqlCommand("SELECT count(*) FROM commerce.marketplace_account", proof))
            Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(0);
        await using (var command = new NpgsqlCommand("SELECT count(*) FROM commerce.marketplace_listing", proof))
            Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(0);

        // S8B is still the uncommitted terminal migration, so its Down path is part of this
        // contract. Roll back in the same disposable database and prove S8A facts survive.
        await using (var db = Context())
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(S8AFinalMigration);

        foreach (var table in new[]
        {
            "commerce.channel_offer", "commerce.marketplace_provider",
            "commerce.marketplace_account", "commerce.marketplace_listing",
            "commerce.marketplace_listing_observation", "commerce.product_commercial_profile",
            "commerce.product_commercial_image", "commerce.commercial_tag"
        })
        {
            await using var command = new NpgsqlCommand("SELECT to_regclass(@name)::text", proof);
            command.Parameters.AddWithValue("name", table);
            (await command.ExecuteScalarAsync()).Should().BeOfType<DBNull>($"S8B table {table} must be absent after rollback");
        }

        await using (var command = new NpgsqlCommand("SELECT name FROM catalog.product WHERE id=@id", proof))
        { command.Parameters.AddWithValue("id", product.Id); (await command.ExecuteScalarAsync()).Should().Be(product.Name); }
        await using (var command = new NpgsqlCommand("SELECT count(*) FROM sales.sale_item WHERE sale_id=@id", proof))
        { command.Parameters.AddWithValue("id", sale.Id); Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(1); }
        await using (var command = new NpgsqlCommand("SELECT amount FROM finance.expense WHERE id=@id", proof))
        { command.Parameters.AddWithValue("id", expense.Id); Convert.ToDecimal(await command.ExecuteScalarAsync()).Should().Be(25m); }
        await using (var command = new NpgsqlCommand("SELECT total_amount FROM quoting.quote_revision WHERE id=@id", proof))
        { command.Parameters.AddWithValue("id", revision); Convert.ToDecimal(await command.ExecuteScalarAsync()).Should().Be(120m); }
    }
}
