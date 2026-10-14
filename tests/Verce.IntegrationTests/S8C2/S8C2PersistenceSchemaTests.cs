using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.S8C2;

/// <summary>Hermetic B1A migration and schema proof. Each database is an owned Testcontainers
/// PostgreSQL instance; no shared or operational connection string is used.</summary>
public sealed class S8C2PersistenceSchemaTests : IAsyncLifetime
{
    private const string S8C1 = "20260925092305_AddS8C1MarketplaceAuthorizationFoundation";
    private const string S8C2 = "20260927114823_AddS8C2MarketplaceListingSync";
    private PostgreSqlContainer _fresh = null!;
    private PostgreSqlContainer _upgrade = null!;
    private string _freshCs = string.Empty;
    private string _upgradeCs = string.Empty;

    public async Task InitializeAsync()
    {
        VerceDbContext.ConfigureModuleAssemblies(Verce.Api.ModuleAssemblyCatalog.All);
        _fresh = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("verce_s8c2_b1a_fresh").WithUsername("verce").WithPassword("verce_test_only").Build();
        _upgrade = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("verce_s8c2_b1a_upgrade").WithUsername("verce").WithPassword("verce_test_only").Build();
        await Task.WhenAll(_fresh.StartAsync(), _upgrade.StartAsync());
        _freshCs = _fresh.GetConnectionString();
        _upgradeCs = _upgrade.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        await _fresh.DisposeAsync();
        await _upgrade.DisposeAsync();
    }

    private VerceDbContext Fresh() => new(new DbContextOptionsBuilder<VerceDbContext>().UseNpgsql(_freshCs).UseSnakeCaseNamingConvention().Options);
    private VerceDbContext Upgrade() => new(new DbContextOptionsBuilder<VerceDbContext>().UseNpgsql(_upgradeCs).UseSnakeCaseNamingConvention().Options);
    private static async Task ExecuteAsync(string cs, string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(cs); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<T?> ScalarAsync<T>(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? default : (T)value!;
    }

    [Fact]
    public async Task Fresh_zero_to_head_creates_the_frozen_S8C2_schema()
    {
        await using var db = Fresh();
        await db.Database.MigrateAsync();
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(S8C2);

        (await ScalarAsync<long>(_freshCs, "SELECT count(*) FROM information_schema.tables WHERE table_schema='commerce' AND table_name IN ('marketplace_listing_sync_run','marketplace_listing_sync_run_item','marketplace_account_sku_mapping')")).Should().Be(3);
        (await ScalarAsync<long>(_freshCs, "SELECT count(*) FROM pg_indexes WHERE schemaname='commerce' AND indexname='ix_marketplace_listing_sync_run_marketplace_account_id'")).Should().Be(1);
        (await ScalarAsync<long>(_freshCs, "SELECT count(*) FROM pg_constraint WHERE conname IN ('fk_marketplace_listing_sync_run_root_run','fk_marketplace_listing_sync_run_retry_of_run')")).Should().Be(2);
        (await ScalarAsync<long>(_freshCs, "SELECT count(*) FROM information_schema.columns WHERE table_schema='commerce' AND table_name='marketplace_listing_sync_run' AND column_name IN ('lease_token','lease_until','heartbeat_at','version')")).Should().Be(4);
    }

    [Fact]
    public async Task S8C1_upgrade_down_and_reupgrade_preserve_the_schema_contract()
    {
        await using (var db = Upgrade()) await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(S8C1);
        var now = DateTimeOffset.UtcNow; var channel = Guid.NewGuid(); var account = Guid.NewGuid(); var actor = Guid.NewGuid();
        await ExecuteAsync(_upgradeCs, """
            INSERT INTO pricing.sales_channel (id,code,name,kind,active,default_margin_percent,notes,created_at,created_by,updated_at,updated_by,version)
            VALUES (@channel,'S8C2B1A','S8C2 B1A','Marketplace',true,NULL,NULL,@now,NULL,NULL,NULL,1);
            INSERT INTO commerce.marketplace_provider (code,name) VALUES ('MERCADO_LIVRE','Mercado Livre');
            INSERT INTO commerce.marketplace_account (id,provider_code,external_account_id,sales_channel_id,display_name,active,credential_reference,sync_state,last_sync_attempt_at,last_successful_sync_at,created_at,created_by,updated_at,updated_by,version)
            VALUES (@account,'MERCADO_LIVRE','b1a-test-account',@channel,'B1A test account',true,NULL,'NEVER_SYNCED',NULL,NULL,@now,NULL,NULL,NULL,1);
            """, new NpgsqlParameter("channel", channel), new NpgsqlParameter("account", account), new NpgsqlParameter("now", now));

        await using (var db = Upgrade()) await db.Database.MigrateAsync();
        (await Upgrade().Database.GetAppliedMigrationsAsync()).Should().Contain(S8C2);
        var run = Guid.NewGuid();
        await ExecuteAsync(_upgradeCs, """
            INSERT INTO commerce.marketplace_listing_sync_run (id,marketplace_account_id,provider_code,kind,status,root_run_id,requested_by_user_id,requested_at,updated_at,attempt_count,version)
            VALUES (@run,@account,'MERCADO_LIVRE','FULL','QUEUED',@run,@actor,@now,@now,0,1);
            INSERT INTO commerce.marketplace_listing_sync_run_item (id,sync_run_id,external_listing_id,origin,outcome,attempt_count,created_at,updated_at)
            VALUES (@item,@run,'MLB-B1A','ENUMERATED','PENDING',0,@now,@now);
            INSERT INTO commerce.marketplace_account_sku_mapping (id,marketplace_account_id,external_sku,product_id,active,confirmed_by_user_id,confirmed_at,created_at,version)
            VALUES (@mapping,@account,'SKU-B1A',@product,true,@actor,@now,@now,1);
            """, new NpgsqlParameter("run", run), new NpgsqlParameter("account", account), new NpgsqlParameter("actor", actor), new NpgsqlParameter("now", now), new NpgsqlParameter("item", Guid.NewGuid()), new NpgsqlParameter("mapping", Guid.NewGuid()), new NpgsqlParameter("product", Guid.NewGuid()));

        var duplicateRun = () => ExecuteAsync(_upgradeCs, "INSERT INTO commerce.marketplace_listing_sync_run (id,marketplace_account_id,provider_code,kind,status,root_run_id,requested_by_user_id,requested_at,updated_at,attempt_count,version) VALUES (@id,@account,'MERCADO_LIVRE','FULL','QUEUED',@id,@actor,@now,@now,0,1)", new NpgsqlParameter("id", Guid.NewGuid()), new NpgsqlParameter("account", account), new NpgsqlParameter("actor", actor), new NpgsqlParameter("now", now));
        await duplicateRun.Should().ThrowAsync<PostgresException>().Where(e => e.SqlState == "23505");
        await ExecuteAsync(_upgradeCs, "UPDATE commerce.marketplace_listing_sync_run SET status='SUCCEEDED',finished_at=@now,safe_result_code='OK',enumeration_completed=true WHERE id=@run", new NpgsqlParameter("now", now), new NpgsqlParameter("run", run));
        await duplicateRun();

        var duplicateItem = () => ExecuteAsync(_upgradeCs, "INSERT INTO commerce.marketplace_listing_sync_run_item (id,sync_run_id,external_listing_id,origin,outcome,attempt_count,created_at,updated_at) VALUES (@id,@run,'MLB-B1A','ENUMERATED','PENDING',0,@now,@now)", new NpgsqlParameter("id", Guid.NewGuid()), new NpgsqlParameter("run", run), new NpgsqlParameter("now", now));
        await duplicateItem.Should().ThrowAsync<PostgresException>().Where(e => e.SqlState == "23505");
        var duplicateMapping = () => ExecuteAsync(_upgradeCs, "INSERT INTO commerce.marketplace_account_sku_mapping (id,marketplace_account_id,external_sku,product_id,active,confirmed_by_user_id,confirmed_at,created_at,version) VALUES (@id,@account,'SKU-B1A',@product,true,@actor,@now,@now,1)", new NpgsqlParameter("id", Guid.NewGuid()), new NpgsqlParameter("account", account), new NpgsqlParameter("product", Guid.NewGuid()), new NpgsqlParameter("actor", actor), new NpgsqlParameter("now", now));
        await duplicateMapping.Should().ThrowAsync<PostgresException>().Where(e => e.SqlState == "23505");
        await ExecuteAsync(_upgradeCs, "UPDATE commerce.marketplace_account_sku_mapping SET active=false,invalidated_at=@now,invalidated_reason='OPERATOR_DEACTIVATED' WHERE marketplace_account_id=@account AND external_sku='SKU-B1A'", new NpgsqlParameter("now", now), new NpgsqlParameter("account", account));
        await duplicateMapping();

        await using (var db = Upgrade()) await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(S8C1);
        (await ScalarAsync<string>(_upgradeCs, "SELECT to_regclass('commerce.marketplace_listing_sync_run')::text")).Should().BeNull();
        await using (var db = Upgrade()) await db.Database.MigrateAsync();
        (await Upgrade().Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }
}
