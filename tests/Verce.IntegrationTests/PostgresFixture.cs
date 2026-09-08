using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests;

/// <summary>
/// Spins up a REAL PostgreSQL container (Testcontainers) once per test collection and applies
/// migrations from scratch — the mission is explicit that SQLite must not substitute for
/// PostgreSQL behaviour (advisory locks, FOR UPDATE SKIP LOCKED, exclusion/check constraints).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("verce_test")
            .WithUsername("verce")
            .WithPassword("verce_test_only")
            .Build();

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        // Migration-from-scratch validation (ROADMAP §17): a clean database must apply every
        // migration and be usable, without a developer's manually prepared database.
        var options = new DbContextOptionsBuilder<VerceDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var context = new VerceDbContext(options);
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    public VerceDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<VerceDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new VerceDbContext(options);
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
