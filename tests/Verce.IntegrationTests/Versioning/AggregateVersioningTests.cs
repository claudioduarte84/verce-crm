using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Verce.IntegrationTests.Versioning;

/// <summary>
/// B-1..B-8 (ROADMAP S1 catalogue): aggregate version starts at 1, an Added root stays at 1
/// across the whole creating Unit of Work, an existing root bumps exactly once per UoW no
/// matter how many children changed, and concurrent writers modifying DIFFERENT children of
/// the same aggregate conflict (ADR-0011 §2).
/// </summary>
[Collection(PostgresCollection.Name)]
public class AggregateVersioningTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public AggregateVersioningTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        // EnsureCreatedAsync is a no-op here: the fixture's PostgreSQL database already exists
        // (migrated for the platform.* schema), and EnsureCreated only creates a database from
        // scratch — it never adds tables to one that already exists. This test-only model's
        // tables are created explicitly instead; idempotent so every test class in the
        // collection can call it safely.
        var (context, _) = TestPlatformFactory.CreateContext(_fixture.ConnectionString);
        await using (context)
        {
            await context.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS test_root (
                    "Id" uuid PRIMARY KEY,
                    "Name" text NOT NULL,
                    "Version" bigint NOT NULL
                );
                CREATE TABLE IF NOT EXISTS test_child (
                    "Id" uuid PRIMARY KEY,
                    "ParentId" uuid NOT NULL REFERENCES test_root("Id") ON DELETE CASCADE,
                    "Label" text NOT NULL
                );
                CREATE TABLE IF NOT EXISTS test_grandchild (
                    "Id" uuid PRIMARY KEY,
                    "ParentId" uuid NOT NULL REFERENCES test_child("Id") ON DELETE CASCADE,
                    "Note" text NOT NULL
                );
                """);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task B1_new_aggregate_root_defaults_to_version_1()
    {
        var (context, _) = TestPlatformFactory.CreateContext(_fixture.ConnectionString);
        await using var ctx = context;

        var root = new TestRoot("aggregate-a");
        ctx.Roots.Add(root);
        await ctx.SaveChangesAsync();

        var reloaded = await ctx.Roots.AsNoTracking().FirstAsync(r => r.Id == root.Id);
        reloaded.Version.Should().Be(1);
    }

    [Fact]
    public async Task B2_added_root_with_children_starts_at_1_no_extra_bump()
    {
        var (context, _) = TestPlatformFactory.CreateContext(_fixture.ConnectionString);
        await using var ctx = context;

        var root = new TestRoot("aggregate-b");
        root.Children.Add(new TestChild(root.Id, "child-1"));
        root.Children.Add(new TestChild(root.Id, "child-2"));
        root.Children.Add(new TestChild(root.Id, "child-3"));
        ctx.Roots.Add(root);

        await ctx.SaveChangesAsync();

        var reloaded = await ctx.Roots.AsNoTracking().FirstAsync(r => r.Id == root.Id);
        reloaded.Version.Should().Be(1, "creating a root with children is ONE insert, not one bump per child");
    }

    [Fact]
    public async Task B2a_added_root_remains_version_1_across_multiple_saves_in_one_logical_operation()
    {
        // Simulates wave 1 (create) + wave 2 (a "handler" modifying the same new root) within
        // ONE AmbientOperationContext — the exact scenario that used to bump 1 -> 2 before the
        // VersionHandledAggregates fix (re-gate H-RG2-001 / H-RG3).
        var (context, ambientContext) = TestPlatformFactory.CreateContext(_fixture.ConnectionString);
        await using var ctx = context;

        var root = new TestRoot("aggregate-c");
        ctx.Roots.Add(root);
        await ctx.SaveChangesAsync(); // wave 1

        root.Name = "aggregate-c-renamed";
        await ctx.SaveChangesAsync(); // wave 2, SAME ambient context / SAME UoW

        root.Version.Should().Be(1, "the root was created in this Unit of Work; it must not bump again within it");
        ambientContext.VersionHandledAggregates.Should().Contain(root.Id);
    }

    [Fact]
    public async Task B3_multiple_children_modified_in_one_save_produce_a_single_version_bump()
    {
        var (setupContext, _) = TestPlatformFactory.CreateContext(_fixture.ConnectionString);
        Guid rootId;
        await using (setupContext)
        {
            var root = new TestRoot("aggregate-d");
            root.Children.Add(new TestChild(root.Id, "x"));
            root.Children.Add(new TestChild(root.Id, "y"));
            root.Children.Add(new TestChild(root.Id, "z"));
            setupContext.Roots.Add(root);
            await setupContext.SaveChangesAsync();
            rootId = root.Id;
        }

        var (context, _) = TestPlatformFactory.CreateContext(_fixture.ConnectionString);
        await using var ctx = context;
        var loadedRoot = await ctx.Roots.Include(r => r.Children).FirstAsync(r => r.Id == rootId);
        loadedRoot.Version.Should().Be(1);

        foreach (var child in loadedRoot.Children) child.Label += "-modified";
        await ctx.SaveChangesAsync();

        var reloaded = await ctx.Roots.AsNoTracking().FirstAsync(r => r.Id == rootId);
        reloaded.Version.Should().Be(2, "three children changed in ONE save must bump the root exactly once, not three times");
    }

    [Fact]
    public async Task B4_existing_aggregate_bumps_once_across_two_waves_in_the_same_unit_of_work()
    {
        var (setupContext, _) = TestPlatformFactory.CreateContext(_fixture.ConnectionString);
        Guid rootId;
        await using (setupContext)
        {
            var root = new TestRoot("aggregate-e");
            root.Children.Add(new TestChild(root.Id, "c1"));
            setupContext.Roots.Add(root);
            await setupContext.SaveChangesAsync();
            rootId = root.Id;
        }

        var (context, ambientContext) = TestPlatformFactory.CreateContext(_fixture.ConnectionString);
        await using var ctx = context;
        var loadedRoot = await ctx.Roots.Include(r => r.Children).FirstAsync(r => r.Id == rootId);
        loadedRoot.Version.Should().Be(1);

        loadedRoot.Children.First().Label = "changed-in-wave-1";
        await ctx.SaveChangesAsync(); // wave 1 -> version becomes 2

        loadedRoot.Name = "changed-in-wave-2";
        await ctx.SaveChangesAsync(); // wave 2, SAME UoW -> must NOT bump again

        loadedRoot.Version.Should().Be(2, "one Unit of Work = one committed version transition, regardless of wave count");
    }

    [Fact]
    public async Task B5_concurrent_writers_modifying_different_children_of_the_same_aggregate_conflict()
    {
        var (setupContext, _) = TestPlatformFactory.CreateContext(_fixture.ConnectionString);
        Guid rootId;
        await using (setupContext)
        {
            var root = new TestRoot("aggregate-f");
            root.Children.Add(new TestChild(root.Id, "child-1"));
            root.Children.Add(new TestChild(root.Id, "child-2"));
            setupContext.Roots.Add(root);
            await setupContext.SaveChangesAsync();
            rootId = root.Id;
        }

        // Two independent contexts (simulating two concurrent requests) both load version N.
        var (contextA, _) = TestPlatformFactory.CreateContext(_fixture.ConnectionString);
        var (contextB, _) = TestPlatformFactory.CreateContext(_fixture.ConnectionString);
        await using var ctxA = contextA;
        await using var ctxB = contextB;

        var rootA = await ctxA.Roots.Include(r => r.Children).FirstAsync(r => r.Id == rootId);
        var rootB = await ctxB.Roots.Include(r => r.Children).FirstAsync(r => r.Id == rootId);

        // A modifies child 1 and commits first.
        rootA.Children.First(c => c.Label == "child-1").Label = "modified-by-A";
        await ctxA.SaveChangesAsync();

        // B modifies a DIFFERENT child (child-2) and commits — this is the critical scenario:
        // two edits to the ROOT ROW is not a sufficient test; different CHILDREN must conflict.
        rootB.Children.First(c => c.Label == "child-2").Label = "modified-by-B";
        var act = async () => await ctxB.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "B loaded version N and A already advanced it to N+1 by modifying a sibling child");
    }

    [Fact]
    public async Task B6_nested_grandchild_mutation_bumps_the_correct_root()
    {
        var (setupContext, _) = TestPlatformFactory.CreateContext(_fixture.ConnectionString);
        Guid rootId;
        await using (setupContext)
        {
            var root = new TestRoot("aggregate-g");
            var child = new TestChild(root.Id, "only-child");
            child.Grandchildren.Add(new TestGrandchild(child.Id, "note-1"));
            root.Children.Add(child);
            setupContext.Roots.Add(root);
            await setupContext.SaveChangesAsync();
            rootId = root.Id;
        }

        var (context, _) = TestPlatformFactory.CreateContext(_fixture.ConnectionString);
        await using var ctx = context;
        var loadedRoot = await ctx.Roots
            .Include(r => r.Children).ThenInclude(c => c.Grandchildren)
            .FirstAsync(r => r.Id == rootId);
        loadedRoot.Version.Should().Be(1);

        loadedRoot.Children.Single().Grandchildren.Single().Note = "changed-at-grandchild-level";
        await ctx.SaveChangesAsync();

        var reloaded = await ctx.Roots.AsNoTracking().FirstAsync(r => r.Id == rootId);
        reloaded.Version.Should().Be(2, "a grandchild-only mutation must still resolve to and bump its root");
    }

    [Fact]
    public async Task B_independent_later_request_bumps_an_existing_committed_root()
    {
        var (setupContext, _) = TestPlatformFactory.CreateContext(_fixture.ConnectionString);
        Guid rootId;
        await using (setupContext)
        {
            var root = new TestRoot("aggregate-h");
            setupContext.Roots.Add(root);
            await setupContext.SaveChangesAsync();
            rootId = root.Id;
        }

        var (context, _) = TestPlatformFactory.CreateContext(_fixture.ConnectionString);
        await using var ctx = context;
        var reloadedRoot = await ctx.Roots.FirstAsync(r => r.Id == rootId);
        reloadedRoot.Name = "renamed-by-a-new-independent-request";
        await ctx.SaveChangesAsync();

        reloadedRoot.Version.Should().Be(2);
    }
}
