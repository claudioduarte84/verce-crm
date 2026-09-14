using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Customers;
using Verce.Modules.Settings;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.S2;

/// <summary>
/// Migration-transition regressions for the S2 Wave 1 CreationSequence gate correction
/// (mission §29-31). Deliberately uses its OWN disposable PostgreSQL container instead of the
/// shared <see cref="PostgresFixture"/>, which always migrates straight to HEAD — these tests
/// need to control exactly which migration is applied at each step.
///
/// <see cref="VerceDbContext.ConfigureModuleAssemblies"/> is a process-wide static, and this
/// class (like <see cref="PostgresFixture"/>) mutates it. Declared in <see cref="PostgresCollection"/>
/// PURELY so xUnit never runs it in parallel with any other Postgres-collection test — without
/// that, two collections racing to set divergent module-assembly lists produces a genuinely
/// non-deterministic EF model ("the model changes each time it is built"), not just a benign
/// re-write of the same value (S3 made the two collections' desired values actually differ:
/// this class needs a deliberately NARROWED module set at times, while every other
/// Postgres-collection test needs the full production catalog).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CreationSequenceMigrationTests : IAsyncLifetime
{
    private const string QuartzTerminalMigration = "20260908004426_AddQuartzSchema";
    // This class deliberately restricts ConfigureModuleAssemblies to Customers+Settings only, to
    // exercise the S1→S2 transition in isolation (see class doc comment). A bare, target-less
    // MigrateAsync() migrates to whichever migration is PHYSICALLY LAST in the assembly — which
    // stopped being AddS2CustomersAndSettings the moment S3 added AddS3SuppliesAndInventory to
    // the same assembly. Every call in this file now targets this constant explicitly instead of
    // relying on that implicit, sprint-fragile assumption.
    private const string S2TerminalMigration = "20260912230413_AddS2CustomersAndSettings";
    private PostgreSqlContainer _container = null!;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        VerceDbContext.ConfigureModuleAssemblies([typeof(CustomersModuleMarker).Assembly, typeof(SettingsModuleMarker).Assembly]);
        _container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("verce_migration_test")
            .WithUsername("verce")
            .WithPassword("verce_test_only")
            .Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private VerceDbContext CreateContext() => new(
        new DbContextOptionsBuilder<VerceDbContext>().UseNpgsql(_connectionString).UseSnakeCaseNamingConvention().Options);

    /// <summary>Widens the process-wide module-assembly registry to the SAME full catalog the
    /// real composition root uses, then returns a context built against it — needed right before
    /// migrating this deliberately S2-only database the rest of the way to head, so the model
    /// backing that migration matches what generated it.</summary>
    private VerceDbContext CreateFullyConfiguredContext()
    {
        VerceDbContext.ConfigureModuleAssemblies(Verce.Api.ModuleAssemblyCatalog.All);
        return CreateContext();
    }

    private static async Task<long> ScalarLongAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string?> ScalarStringAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string?)await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Migration_from_zero_creates_the_full_S2_schema_including_creation_sequence()
    {
        await using (var db = CreateContext())
        {
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(S2TerminalMigration);
            (await db.Database.GetAppliedMigrationsAsync()).Should().HaveCount(4);
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        (await ScalarLongAsync(connection,
            "SELECT count(*) FROM pg_sequences WHERE schemaname = 'customers' AND sequencename = 'customer_creation_sequence_seq'"))
            .Should().Be(1);

        (await ScalarStringAsync(connection, """
            SELECT data_type FROM information_schema.columns
            WHERE table_schema='customers' AND table_name='customer' AND column_name='creation_sequence'
            """)).Should().Be("bigint");
        (await ScalarStringAsync(connection, """
            SELECT is_nullable FROM information_schema.columns
            WHERE table_schema='customers' AND table_name='customer' AND column_name='creation_sequence'
            """)).Should().Be("NO");
        (await ScalarStringAsync(connection, """
            SELECT column_default FROM information_schema.columns
            WHERE table_schema='customers' AND table_name='customer' AND column_name='creation_sequence'
            """)).Should().Contain("nextval");

        (await ScalarLongAsync(connection, """
            SELECT count(*) FROM pg_indexes
            WHERE schemaname='customers' AND tablename='customer'
              AND indexdef ILIKE '%creation_sequence%' AND indexdef ILIKE '%UNIQUE%'
            """)).Should().Be(1);

        // Sequence OWNED BY the column — an internal pg_depend dependency, not a mere reference.
        (await ScalarLongAsync(connection, """
            SELECT count(*)
            FROM pg_depend d
            JOIN pg_class seq ON seq.oid = d.objid AND seq.relkind = 'S'
            JOIN pg_class tbl ON tbl.oid = d.refobjid
            JOIN pg_attribute att ON att.attrelid = tbl.oid AND att.attnum = d.refobjsubid
            WHERE seq.relname = 'customer_creation_sequence_seq' AND tbl.relname = 'customer'
              AND att.attname = 'creation_sequence' AND d.deptype = 'a'
            """)).Should().Be(1);

        (await ScalarLongAsync(connection,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema IN ('customers','settings') AND table_type='BASE TABLE'"))
            .Should().Be(8);
        (await ScalarLongAsync(connection,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema='platform' AND table_name LIKE 'qrtz_%'"))
            .Should().Be(12);

        // The real host's own seed services (SettingsSeedService, InventorySeedService, ...)
        // refuse to seed at all while ANY migration is pending — correctly so, but it means this
        // S2-only database must be brought the rest of the way to head before booting the real
        // host below, exactly like a genuine operator would before relying on it.
        await using (var db = CreateFullyConfiguredContext()) { await db.Database.MigrateAsync(); }

        var storageRoot = Path.Combine(Path.GetTempPath(), "verce-migration-zero-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = new VerceWebApplicationFactory(_connectionString,
                new Dictionary<string, string?> { ["Settings:SeedOnStartup"] = "true", ["BrandAssets:StorageRoot"] = storageRoot });
            using var client = factory.CreateHttpsClient();
            (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);

            await using var verify = CreateContext();
            (await verify.Set<AppSetting>().CountAsync()).Should().Be(SettingCatalog.All.Count);
        }
        finally
        {
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
        }
    }

    [Fact]
    public async Task S1_to_S2_upgrade_preserves_the_owner_and_allocates_creation_sequence_for_new_customers()
    {
        Guid ownerId;
        await using (var db = CreateContext())
        {
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(QuartzTerminalMigration);

            var role = new ApplicationRole(Roles.Owner) { Id = Guid.CreateVersion7() };
            var owner = new ApplicationUser
            {
                Id = Guid.CreateVersion7(),
                UserName = "s1-owner@example.test",
                NormalizedUserName = "S1-OWNER@EXAMPLE.TEST",
                Email = "s1-owner@example.test",
                NormalizedEmail = "S1-OWNER@EXAMPLE.TEST",
                EmailConfirmed = true,
                DisplayName = "S1 Owner",
                IsActive = true,
                SetupStatus = SetupStatus.Active,
                SetupCompletedAt = DateTimeOffset.UtcNow,
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            };
            db.Add(role);
            db.Add(owner);
            await db.SaveChangesAsync();
            db.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid> { UserId = owner.Id, RoleId = role.Id });
            await db.SaveChangesAsync();
            ownerId = owner.Id;
        }

        await using (var db = CreateContext())
        {
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(S2TerminalMigration);
        }

        await using (var db = CreateContext())
        {
            (await db.Users.SingleAsync(x => x.Id == ownerId)).DisplayName.Should().Be("S1 Owner");

            var customer = new Customer(PersonType.Individual, "Cliente pós-migração", null, null, null, null, null);
            db.Add(customer);
            await db.SaveChangesAsync();
            var sequence = (long)db.Entry(customer).Property("CreationSequence").CurrentValue!;
            sequence.Should().BePositive();
        }

        // See the matching comment in Migration_from_zero_creates_the_full_S2_schema... above.
        await using (var db = CreateFullyConfiguredContext()) { await db.Database.MigrateAsync(); }

        var storageRoot = Path.Combine(Path.GetTempPath(), "verce-s1-upgrade-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = new VerceWebApplicationFactory(_connectionString,
                new Dictionary<string, string?> { ["Settings:SeedOnStartup"] = "true", ["BrandAssets:StorageRoot"] = storageRoot });
            using var client = factory.CreateHttpsClient();
            (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);

            await using var verify = CreateContext();
            (await verify.Set<AppSetting>().CountAsync()).Should().Be(SettingCatalog.All.Count);
        }
        finally
        {
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        (await ScalarLongAsync(connection,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema='platform' AND table_name LIKE 'qrtz_%'"))
            .Should().Be(12);
    }

    [Fact]
    public async Task Down_then_up_leaves_no_orphan_sequence_index_or_constraint()
    {
        await using (var db = CreateContext())
        {
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(S2TerminalMigration);
        }

        await using (var db = CreateContext())
        {
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(QuartzTerminalMigration);
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        (await ScalarLongAsync(connection,
            "SELECT count(*) FROM pg_sequences WHERE schemaname = 'customers' AND sequencename = 'customer_creation_sequence_seq'"))
            .Should().Be(0, "downgrade must drop the sequence along with the table that owns it — no orphan");
        (await ScalarLongAsync(connection,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'customers'"))
            .Should().Be(0);

        await using (var db = CreateContext())
        {
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(S2TerminalMigration);
        }

        (await ScalarLongAsync(connection,
            "SELECT count(*) FROM pg_sequences WHERE schemaname = 'customers' AND sequencename = 'customer_creation_sequence_seq'"))
            .Should().Be(1, "the second upgrade must recreate the sequence with no duplicate-object error");

        await using (var db = CreateContext())
        {
            var customer = new Customer(PersonType.Individual, "Pós down-up", null, null, null, null, null);
            db.Add(customer);
            await db.SaveChangesAsync();
            ((long)db.Entry(customer).Property("CreationSequence").CurrentValue!).Should().Be(1,
                "the sequence was recreated from scratch, so the first insert after a clean down/up starts at 1 again");
        }
    }
}
