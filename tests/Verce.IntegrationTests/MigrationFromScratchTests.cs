using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Verce.IntegrationTests;

/// <summary>
/// ROADMAP §17: a clean PostgreSQL database must apply migrations from zero and be usable
/// without a developer's manually prepared database. The <see cref="PostgresFixture"/> already
/// performs this on a fresh Testcontainers instance for every test in this collection — this
/// test asserts the OUTCOME explicitly rather than relying on fixture setup succeeding silently.
/// </summary>
[Collection(PostgresCollection.Name)]
public class MigrationFromScratchTests
{
    private readonly PostgresFixture _fixture;
    public MigrationFromScratchTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Every_migration_is_applied_and_no_pending_migrations_remain()
    {
        await using var context = _fixture.CreateContext();
        var pending = await context.Database.GetPendingMigrationsAsync();
        pending.Should().BeEmpty();

        var applied = await context.Database.GetAppliedMigrationsAsync();
        applied.Should().Contain(m => m.Contains("InitialPlatform"));
    }

    [Fact]
    public async Task Platform_schema_and_core_tables_exist()
    {
        await using var context = _fixture.CreateContext();
        var canConnect = await context.Database.CanConnectAsync();
        canConnect.Should().BeTrue();

        // A representative write/read round-trip proves the schema is genuinely usable,
        // not merely "migration ran without throwing".
        var probe = Verce.Platform.Outbox.OutboxMessage.Enqueue(
            "ProbeEvent", "{}", null, Guid.NewGuid(), null, null, "Probe", null, DateTimeOffset.UtcNow);
        context.OutboxMessages.Add(probe);
        await context.SaveChangesAsync();

        var reloaded = await context.OutboxMessages.FindAsync(probe.Id);
        reloaded.Should().NotBeNull();
    }
}

/// <summary>
/// D-12 needs a database NOTHING has touched since migration — the shared
/// <see cref="PostgresFixture"/> is reused across every class in <see cref="PostgresCollection"/>,
/// so a class in this collection could run after another has already created users. This gets
/// its OWN dedicated, disposable Testcontainers Postgres to make the assertion meaningful rather
/// than order-dependent.
/// </summary>
public class MigrationSeedingTests : IAsyncLifetime
{
    private PostgresFixture _dedicatedFixture = null!;

    public async Task InitializeAsync()
    {
        _dedicatedFixture = new PostgresFixture();
        await _dedicatedFixture.InitializeAsync();
    }

    public async Task DisposeAsync() => await _dedicatedFixture.DisposeAsync();

    [Fact]
    public async Task D12_no_migration_or_seed_path_creates_any_user_on_a_freshly_migrated_database()
    {
        await using var context = _dedicatedFixture.CreateContext();
        var userCount = await context.Users.CountAsync();
        userCount.Should().Be(0, "D-12: bootstrap-owner is the ONLY way an Owner is ever created — no seeded credential may exist");
    }
}
