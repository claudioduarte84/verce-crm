using System.Net;
using Microsoft.AspNetCore.Identity;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Verce.Api.Settings;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Settings;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.Costing;

/// <summary>
/// H-08 closure: the S4 data migration <c>ConvertCostingWastageRateToPercentagePoints</c> changes
/// the SEMANTIC MEANING of <c>costing.default_wastage_rate</c>'s stored text (a fraction becomes a
/// percentage point), not merely its formatting, so it must bump <see cref="AppSetting.Version"/>
/// in the same statement — otherwise a stale editor/client that read the row before deployment
/// could still overwrite it after deployment under the pre-migration Version, silently
/// resurrecting the old fractional interpretation (Sol's independent harness review).
///
/// Deliberately uses its OWN disposable PostgreSQL container and <see cref="IMigrator"/>
/// directly, following <c>CreationSequenceMigrationTests</c>' established pattern, because these
/// tests need to control EXACTLY which migration is applied at each step — the shared
/// <see cref="PostgresFixture"/> always migrates straight to HEAD.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WastagePercentageMigrationTests : IAsyncLifetime
{
    private const string S3TerminalMigration = "20260914045941_AddS3SuppliesAndInventory";
    private const string S4WastageMigration = "20260919005716_ConvertCostingWastageRateToPercentagePoints";
    private const string WastageKey = "costing.default_wastage_rate";
    private const string MarginKey = "pricing.default_margin_percent";

    private PostgreSqlContainer _container = null!;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        VerceDbContext.ConfigureModuleAssemblies(Verce.Api.ModuleAssemblyCatalog.All);
        _container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("verce_s4_wastage_migration_test")
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

    private async Task InsertSettingAsync(string key, string value, string scope, long version)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings.app_setting (id, key, value, value_type, scope, description, is_secret, created_at, version)
            VALUES (gen_random_uuid(), @key, @value, 'Decimal', @scope, 'test row', false, now(), @version)
            """;
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.AddWithValue("version", version);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<(string Value, long Version)> ReadSettingAsync(string key)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value, version FROM settings.app_setting WHERE key = @key";
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"expected exactly one row for {key}");
        return (reader.GetString(0), reader.GetInt64(1));
    }

    [Fact]
    public async Task Up_converts_the_wastage_setting_with_canonical_text_and_increments_its_version_while_an_unrelated_setting_is_untouched()
    {
        await MigrateToAsync(S3TerminalMigration);
        await InsertSettingAsync(WastageKey, "0.05", "costing", version: 3);
        await InsertSettingAsync(MarginKey, "0.35", "pricing", version: 7);

        await MigrateToAsync(S4WastageMigration);

        var wastage = await ReadSettingAsync(WastageKey);
        wastage.Value.Should().Be("5", "trim_scale must drop mathematically unnecessary trailing zeros — not \"5.00\"");
        wastage.Version.Should().Be(4, "the migration changes the SEMANTIC meaning of Value, so it must bump Version (H-08)");

        var margin = await ReadSettingAsync(MarginKey);
        margin.Value.Should().Be("0.35", "an unrelated Setting's Value must never be touched by this migration");
        margin.Version.Should().Be(7, "an unrelated Setting's Version must never be touched by this migration");
    }

    [Fact]
    public async Task Down_then_re_up_is_deterministic_and_keeps_Version_monotonically_increasing()
    {
        await MigrateToAsync(S3TerminalMigration);
        await InsertSettingAsync(WastageKey, "0.125", "costing", version: 1);

        await MigrateToAsync(S4WastageMigration);
        var afterUp = await ReadSettingAsync(WastageKey);
        afterUp.Value.Should().Be("12.5");
        afterUp.Version.Should().Be(2);

        await MigrateToAsync(S3TerminalMigration); // Down
        var afterDown = await ReadSettingAsync(WastageKey);
        afterDown.Value.Should().Be("0.125");
        afterDown.Version.Should().Be(3, "Down must still move Version FORWARD — a concurrency token is never allowed to decrement, including on rollback");

        await MigrateToAsync(S4WastageMigration); // re-Up
        var afterReUp = await ReadSettingAsync(WastageKey);
        afterReUp.Value.Should().Be("12.5");
        afterReUp.Version.Should().Be(4);
    }

    [Fact]
    public async Task Malformed_legacy_value_fails_the_migration_deterministically_without_touching_value_or_version()
    {
        await MigrateToAsync(S3TerminalMigration);
        await InsertSettingAsync(WastageKey, "abc", "costing", version: 1);

        var act = async () => await MigrateToAsync(S4WastageMigration);
        (await act.Should().ThrowAsync<Npgsql.PostgresException>()).Which.SqlState.Should().Be("22P02");

        await using (var db = CreateContext())
        {
            (await db.Database.GetAppliedMigrationsAsync()).Should().NotContain(S4WastageMigration,
                "a failed migration transaction must not be recorded as applied");
        }

        var wastage = await ReadSettingAsync(WastageKey);
        wastage.Value.Should().Be("abc", "a malformed legacy value must be left untouched, never silently repaired");
        wastage.Version.Should().Be(1);
    }

    [Fact]
    public async Task Fresh_database_with_no_row_yet_migrates_safely()
    {
        await MigrateToAsync(S4WastageMigration);
        await using var db = CreateContext();
        (await db.Set<AppSetting>().CountAsync(x => x.Key == WastageKey)).Should().Be(0,
            "the migration only updates an existing row by key; a fresh database has none until the seed service runs afterward");
    }

    /// <summary>
    /// §33/§27: the real optimistic-concurrency contract, exercised through the actual
    /// application over real HTTP — not merely comparing two integers in SQL. A client that read
    /// the Setting's Version before the migration ran, and attempts to write the OLD fractional
    /// value back afterward under that stale Version, must be rejected exactly like any other
    /// concurrent edit of the same row — never silently accepted because "the number matched at
    /// read time".
    /// </summary>
    [Fact]
    public async Task A_stale_client_that_read_the_pre_migration_version_is_rejected_by_the_real_settings_endpoint()
    {
        await MigrateToAsync(S3TerminalMigration);
        await InsertSettingAsync(WastageKey, "0.05", "costing", version: 1);

        long staleVersionTheClientReadBeforeDeployment;
        await using (var db = CreateContext())
        {
            staleVersionTheClientReadBeforeDeployment = await db.Set<AppSetting>()
                .Where(x => x.Key == WastageKey).Select(x => x.Version).SingleAsync();
        }
        staleVersionTheClientReadBeforeDeployment.Should().Be(1);

        // Deployment: the migration runs, changing Value's meaning and bumping Version.
        await MigrateToAsync(S4WastageMigration);
        var afterDeployment = await ReadSettingAsync(WastageKey);
        afterDeployment.Value.Should().Be("5");
        afterDeployment.Version.Should().Be(2);

        await using var factory = new VerceWebApplicationFactory(_connectionString,
            new Dictionary<string, string?> { ["Settings:SeedOnStartup"] = "false" });

        var email = Guid.NewGuid().ToString("N") + "@example.test";
        const string password = "a-perfectly-fine-12char-password";
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            if (!await roles.RoleExistsAsync(Roles.Owner)) (await roles.CreateAsync(new ApplicationRole(Roles.Owner))).Succeeded.Should().BeTrue();
            var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "Stale Client Owner", IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow };
            (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
            (await users.AddToRoleAsync(user, Roles.Owner)).Succeeded.Should().BeTrue();
        }
        var client = new AuthTestClient(factory.CreateHttpsClient());
        await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new Verce.Api.Auth.LoginRequest(email, password))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await client.EnsureCsrfCookieAsync();

        // The stale write: the OLD fractional value, under the OLD (pre-migration) Version.
        var staleWrite = await client.PutAsync($"/api/settings/{WastageKey}", new SettingUpdateRequest("0.10", staleVersionTheClientReadBeforeDeployment));
        staleWrite.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a client holding the pre-migration Version must never be able to resurrect the old fractional interpretation");

        var stillUnchanged = await ReadSettingAsync(WastageKey);
        stillUnchanged.Value.Should().Be("5", "the rejected stale write must not have mutated the row");
        stillUnchanged.Version.Should().Be(2);

        // The CURRENT version must still accept an ordinary update.
        var currentWrite = await client.PutAsync($"/api/settings/{WastageKey}", new SettingUpdateRequest("7", 2));
        currentWrite.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var afterCurrentWrite = await ReadSettingAsync(WastageKey);
        afterCurrentWrite.Value.Should().Be("7");
        afterCurrentWrite.Version.Should().Be(3);
    }
}
