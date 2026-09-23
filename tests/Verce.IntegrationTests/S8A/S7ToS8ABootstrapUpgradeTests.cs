using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.S8A;

/// <summary>Permanent S7-terminal to S8A compatibility certification. It deliberately does not
/// reuse the S6->S7 bootstrap: the fixture is frozen at the terminal S7 migration before the
/// S8A migration is applied.</summary>
public sealed class S7ToS8ABootstrapUpgradeTests : IAsyncLifetime
{
    private const string S7FinalMigration = "20260921123702_AddS7DocumentTemplateEngine";
    private PostgreSqlContainer _container = null!;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        VerceDbContext.ConfigureModuleAssemblies(Verce.Api.ModuleAssemblyCatalog.All);
        _container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("verce_s7_to_s8a")
            .WithUsername("verce").WithPassword("verce_test_only").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private VerceDbContext Context() => new(new DbContextOptionsBuilder<VerceDbContext>()
        .UseNpgsql(_connectionString).UseSnakeCaseNamingConvention().Options);

    [Fact]
    public async Task Terminal_S7_commercial_snapshots_upgrade_to_S8A_without_repricing_or_backfill()
    {
        await using (var db = Context())
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(S7FinalMigration);

        var quote = Guid.NewGuid(); var revision = Guid.NewGuid(); var item = Guid.NewGuid();
        var channel = Guid.NewGuid(); var ruleVersion = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO pricing.sales_channel (id, code, name, kind, active, created_at, version)
                VALUES (@channel, 'S7-HIST', 'Canal S7 histórico', 'Marketplace', true, @now, 1);
                INSERT INTO pricing.fee_rule (id, sales_channel_id, name, active, created_at, version)
                VALUES (gen_random_uuid(), @channel, 'Regra S7', true, @now, 1);
                INSERT INTO pricing.fee_rule_version (id, fee_rule_id, valid_from, commission_percent, fixed_fee, fixed_fee_application, created_at)
                SELECT @ruleVersion, id, DATE '2020-01-01', .10, 2.00, 'PerUnit', @now FROM pricing.fee_rule WHERE sales_channel_id=@channel;
                INSERT INTO quoting.quote (id, number, number_date, number_sequence, customer_id, current_revision_id, created_at, version)
                VALUES (@quote, '260922-71', DATE '2026-09-22', 71, NULL, @revision, @now, 1);
                INSERT INTO quoting.quote_revision (id, quote_id, revision_index, revision_suffix, status, sales_channel_id, issued_at, validity_days, valid_until,
                    subtotal_amount, discount_amount, total_amount, total_cost_amount, expected_profit_amount, effective_margin_percent, created_at)
                VALUES (@revision, @quote, 1, '', 'APPROVED', @channel, @now, 15, DATE '2026-10-07', 120.00, 12.00, 108.00, 50.00, 45.00, .416667, @now);
                INSERT INTO quoting.quote_item (id, quote_revision_id, line_number, product_name_snapshot, quantity, unit_total_cost, cost_engine_version,
                    desired_margin_percent, sales_channel_id, fee_rule_version_id, commission_percent, fixed_fee_application, raw_fixed_fee, allocated_order_fee,
                    fixed_fee_per_unit, rounding_policy_applied, suggested_unit_price, commission_amount_per_unit, price_overridden, unit_price, discount_kind,
                    discount_value, discount_amount, net_unit_price, line_total_amount, line_cost_amount, line_fee_amount, expected_profit_amount,
                    effective_margin_percent, created_at)
                VALUES (@item, @revision, 1, 'Produto congelado S7', 2, 25.000000, 'S7', .40, @channel, @ruleVersion, .10, 'PerUnit', 2.000000, 0, 2.000000,
                    'CENT', 60.00, 5.00, false, 60.00, 'Amount', 6.00, 12.00, 54.00, 108.00, 50.00, 14.00, 44.00, .407407, @now);
                """;
            command.Parameters.AddWithValue("channel", channel); command.Parameters.AddWithValue("ruleVersion", ruleVersion);
            command.Parameters.AddWithValue("quote", quote); command.Parameters.AddWithValue("revision", revision); command.Parameters.AddWithValue("item", item); command.Parameters.AddWithValue("now", now);
            await command.ExecuteNonQueryAsync();
        }

        await using (var before = new NpgsqlConnection(_connectionString))
        {
            await before.OpenAsync(); await using var command = new NpgsqlCommand("SELECT total_amount, total_cost_amount, expected_profit_amount FROM quoting.quote_revision WHERE id=@id", before);
            command.Parameters.AddWithValue("id", revision); await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue(); reader.GetDecimal(0).Should().Be(108m); reader.GetDecimal(1).Should().Be(50m); reader.GetDecimal(2).Should().Be(45m);
        }

        await using (var db = Context()) await db.Database.MigrateAsync();
        await using (var current = Context())
        {
            var loaded = await current.Set<Verce.Modules.Quoting.QuoteRevision>().Include(x => x.Items).SingleAsync(x => x.Id == revision);
            loaded.SubtotalAmount.Should().Be(120m); loaded.DiscountAmount.Should().Be(12m); loaded.TotalAmount.Should().Be(108m);
            loaded.TotalCostAmount.Should().Be(50m); loaded.ExpectedProfitAmount.Should().Be(45m); loaded.EffectiveMarginPercent.Should().Be(.416667m);
            var line = loaded.Items.Single(); line.Quantity.Should().Be(2m); line.UnitPrice.Should().Be(60m); line.DiscountAmount.Should().Be(12m);
            line.LineTotalAmount.Should().Be(108m); line.UnitTotalCost.Should().Be(25m); line.LineCostAmount.Should().Be(50m); line.LineFeeAmount.Should().Be(14m);
            line.ExpectedProfitAmount.Should().Be(44m); line.EffectiveMarginPercent.Should().Be(.407407m); line.BracketId.Should().BeNull(); line.BracketResolution.Should().BeNull(); line.FeeBasisAmount.Should().BeNull();
        }

        await using var proof = new NpgsqlConnection(_connectionString);
        await proof.OpenAsync();
        foreach (var table in new[] { "sales.sale", "sales.sale_item", "sales.sale_status_history", "finance.expense_category", "finance.expense", "pricing.price_bracket" })
        {
            await using var command = new NpgsqlCommand("SELECT to_regclass(@name)::text", proof); command.Parameters.AddWithValue("name", table);
            (await command.ExecuteScalarAsync()).Should().NotBeNull($"S8A table {table} must exist after migration");
        }
    }
}
