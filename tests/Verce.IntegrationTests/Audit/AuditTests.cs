using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Verce.Platform.UnitOfWork;

namespace Verce.IntegrationTests.Audit;

/// <summary>
/// G-1..G-5 (ROADMAP S1 catalogue, ADR-0010): the generic audit interceptor's wave/correlation
/// bookkeeping, proven directly against <see cref="AuditTestModel.AuditTestRoot"/> since no
/// real business entity is marked <c>[Auditable]</c> yet in S1 (see FINAL REPORT open issues).
/// </summary>
[Collection(PostgresCollection.Name)]
public class AuditTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    public AuditTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS audit_test_root (
                id uuid PRIMARY KEY,
                name text NOT NULL UNIQUE
            );
            TRUNCATE TABLE audit_test_root;
            TRUNCATE TABLE platform.audit_log RESTART IDENTITY CASCADE;
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task G1_every_wave_of_one_command_shares_one_correlation_id_and_actor()
    {
        var actorId = Guid.CreateVersion7();
        var (context, _) = AuditTestFactory.CreateContext(_fixture.ConnectionString, actorId: actorId, actorName: "Ana Owner");

        var root = new AuditTestRoot("root-g1");
        context.Roots.Add(root);
        await context.SaveChangesAsync(); // wave 1

        root.Name = "root-g1-renamed";
        await context.SaveChangesAsync(); // wave 2, SAME ambient context = same command

        await using var verifyContext = _fixture.CreateContext();
        var rows = await verifyContext.AuditLog.Where(a => a.EntityId == root.Id).ToListAsync();

        rows.Should().HaveCount(2);
        rows.Select(r => r.CorrelationId).Distinct().Should().ContainSingle();
        rows.Select(r => r.UserId).Distinct().Should().ContainSingle().Which.Should().Be(actorId);
        rows.Select(r => r.UserDisplayName).Distinct().Should().ContainSingle().Which.Should().Be("Ana Owner");
    }

    [Fact]
    public async Task G2_an_entity_genuinely_modified_in_two_waves_gets_two_rows_with_the_right_wave_index()
    {
        var (context, ambient) = AuditTestFactory.CreateContext(_fixture.ConnectionString);

        var root = new AuditTestRoot("root-g2");
        context.Roots.Add(root);
        ambient.WaveIndex = 1;
        await context.SaveChangesAsync();

        root.Name = "root-g2-renamed";
        ambient.WaveIndex = 2;
        await context.SaveChangesAsync();

        await using var verifyContext = _fixture.CreateContext();
        var rows = await verifyContext.AuditLog
            .Where(a => a.EntityId == root.Id)
            .OrderBy(a => a.WaveIndex)
            .ToListAsync();

        rows.Should().HaveCount(2);
        rows[0].WaveIndex.Should().Be(1);
        rows[1].WaveIndex.Should().Be(2);
        rows[0].OperationStartedAt.Should().Be(rows[1].OperationStartedAt, "both waves belong to the same command");
        rows[0].OccurredAt.Should().BeOnOrBefore(rows[1].OccurredAt, "each wave has its own occurred_at instant");
    }

    [Fact]
    public async Task G3_an_entity_untouched_after_its_one_wave_produces_exactly_one_row_no_duplication()
    {
        var (context, ambient) = AuditTestFactory.CreateContext(_fixture.ConnectionString);

        var rootA = new AuditTestRoot("root-g3-a");
        context.Roots.Add(rootA);
        ambient.WaveIndex = 1;
        await context.SaveChangesAsync(); // wave 1: only rootA changes

        var rootB = new AuditTestRoot("root-g3-b");
        context.Roots.Add(rootB);
        ambient.WaveIndex = 2;
        await context.SaveChangesAsync(); // wave 2: only rootB changes — rootA is untouched

        await using var verifyContext = _fixture.CreateContext();
        var rootARows = await verifyContext.AuditLog.Where(a => a.EntityId == rootA.Id).ToListAsync();
        var rootBRows = await verifyContext.AuditLog.Where(a => a.EntityId == rootB.Id).ToListAsync();

        rootARows.Should().ContainSingle("rootA was only ever changed in wave 1 — no artificial duplication for wave 2");
        rootARows.Single().WaveIndex.Should().Be(1);
        rootBRows.Should().ContainSingle();
        rootBRows.Single().WaveIndex.Should().Be(2);
    }

    [Fact]
    public async Task G4_a_failed_save_rolls_back_both_the_business_row_and_its_audit_row_together()
    {
        var (context, _) = AuditTestFactory.CreateContext(_fixture.ConnectionString);

        // Seed a row that will collide with the unique index on Name, forcing SaveChangesAsync
        // to fail atomically — proving the audit row queued in the SAME call never survives
        // when the business change it describes does not (ADR-0010: no best-effort audit path).
        await using (var seedContext = _fixture.CreateContext())
        {
            await seedContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO audit_test_root (id, name) VALUES (gen_random_uuid(), 'root-g4-duplicate');");
        }

        var colliding = new AuditTestRoot("root-g4-duplicate");
        context.Roots.Add(colliding);

        var act = async () => await context.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();

        await using var verifyContext = _fixture.CreateContext();
        (await verifyContext.AuditLog.Where(a => a.EntityId == colliding.Id).CountAsync()).Should().Be(0,
            "the audit row for the failed business change must not persist either");
    }

    [Fact]
    public async Task G5_a_system_job_actor_is_recorded_with_no_user_id()
    {
        var (context, _) = AuditTestFactory.CreateContext(_fixture.ConnectionString, source: AuditSource.Job);

        var root = new AuditTestRoot("root-g5");
        context.Roots.Add(root);
        await context.SaveChangesAsync();

        await using var verifyContext = _fixture.CreateContext();
        var row = await verifyContext.AuditLog.SingleAsync(a => a.EntityId == root.Id);

        row.Source.Should().Be(AuditSource.Job);
        row.UserId.Should().BeNull();
    }

    [Fact]
    public void G5_AuditSource_has_no_Migration_value_ever()
    {
        // The other half of G-5 ("no row anywhere carries source = MIGRATION") is structurally
        // guaranteed: the enum simply has no such member, so no code path can ever produce one.
        Enum.GetNames<AuditSource>().Should().NotContain("Migration");
    }
}
