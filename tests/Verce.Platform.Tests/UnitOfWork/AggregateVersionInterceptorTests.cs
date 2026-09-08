using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Verce.Platform.Ownership;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Domain;

namespace Verce.Platform.Tests.UnitOfWork;

/// <summary>
/// Exercises <see cref="AggregateVersionInterceptor"/>'s pure decision logic — which root gets
/// bumped, how many times, and when insert-vs-modify is decided — against EF Core's InMemory
/// provider. This is legitimate here because nothing under test depends on PostgreSQL-specific
/// SQL or true optimistic-concurrency conflict detection (that is B-5, covered against a real
/// database by Verce.IntegrationTests' AggregateVersioningTests); this only needs a real
/// ChangeTracker/EntityState/SaveChanges lifecycle, which InMemory provides faithfully.
/// </summary>
public class AggregateVersionInterceptorTests
{
    private sealed class IcRoot : AggregateRoot
    {
        public string Name { get; private set; } = string.Empty;
        public List<IcChild> Children { get; private set; } = new();

        private IcRoot() { }
        public IcRoot(string name) { Name = name; }

        public IcChild AddChild(string label)
        {
            var child = new IcChild(Id, label);
            Children.Add(child);
            return child;
        }

        public void Rename(string name) => Name = name;
    }

    private sealed class IcChild : Entity, IOwnedBy<IcRoot>
    {
        public Guid ParentId { get; private set; }
        public string Label { get; private set; } = string.Empty;
        public List<IcGrandchild> Grandchildren { get; private set; } = new();

        private IcChild() { }
        public IcChild(Guid parentId, string label) { ParentId = parentId; Label = label; }

        public IcGrandchild AddGrandchild(string note)
        {
            var grandchild = new IcGrandchild(Id, note);
            Grandchildren.Add(grandchild);
            return grandchild;
        }

        public void Relabel(string label) => Label = label;
    }

    private sealed class IcGrandchild : Entity, IOwnedBy<IcChild>
    {
        public Guid ParentId { get; private set; }
        public string Note { get; private set; } = string.Empty;

        private IcGrandchild() { }
        public IcGrandchild(Guid parentId, string note) { ParentId = parentId; Note = note; }

        public void Annotate(string note) => Note = note;
    }

    private sealed class IcDbContext : DbContext
    {
        public DbSet<IcRoot> Roots => Set<IcRoot>();
        public DbSet<IcChild> Children => Set<IcChild>();
        public DbSet<IcGrandchild> Grandchildren => Set<IcGrandchild>();

        public IcDbContext(DbContextOptions<IcDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<IcRoot>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasMany(x => x.Children).WithOne().HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Cascade);
            });
            builder.Entity<IcChild>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasMany(x => x.Grandchildren).WithOne().HasForeignKey(g => g.ParentId).OnDelete(DeleteBehavior.Cascade);
            });
            builder.Entity<IcGrandchild>(b => b.HasKey(x => x.Id));
        }
    }

    private static readonly AggregateOwnershipRegistry Registry =
        AggregateOwnershipRegistry.BuildAndValidate(new[] { typeof(IcRoot).Assembly });

    private static (IcDbContext Context, AmbientOperationContext Ambient) NewContext(string dbName)
    {
        var ambientContext = new AmbientOperationContext(Guid.CreateVersion7(), AuditSource.System, DateTimeOffset.UtcNow);
        var interceptor = new AggregateVersionInterceptor(Registry, ambientContext);
        var options = new DbContextOptionsBuilder<IcDbContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(interceptor)
            .Options;
        return (new IcDbContext(options), ambientContext);
    }

    [Fact]
    public async Task B1_a_new_aggregate_root_is_inserted_at_version_1()
    {
        var (context, _) = NewContext(Guid.NewGuid().ToString());
        var root = new IcRoot("root-a");
        context.Roots.Add(root);

        await context.SaveChangesAsync();

        root.Version.Should().Be(1);
    }

    [Fact]
    public async Task B2_an_added_root_with_a_new_child_in_one_save_starts_at_1_with_no_extra_bump()
    {
        var (context, ambient) = NewContext(Guid.NewGuid().ToString());
        var root = new IcRoot("root-a");
        root.AddChild("child-a");
        context.Roots.Add(root);

        await context.SaveChangesAsync();

        root.Version.Should().Be(1, "an Added root's version is pinned, never compared against a prior value");
        ambient.VersionHandledAggregates.Should().Contain(root.Id);
    }

    [Fact]
    public async Task B2a_an_added_root_remains_version_1_across_a_second_save_in_the_same_ambient_context()
    {
        var (context, _) = NewContext(Guid.NewGuid().ToString());
        var root = new IcRoot("root-a");
        context.Roots.Add(root);
        await context.SaveChangesAsync(); // wave 1: Added -> pinned to 1, marked handled

        root.Rename("root-a-renamed"); // wave 2 mutates the SAME root, same ambient context/UoW
        await context.SaveChangesAsync();

        root.Version.Should().Be(1, "the root was registered as handled at creation; no later wave may bump it (B-2a)");
    }

    [Fact]
    public async Task B2b_a_later_independent_ambient_context_bumps_an_already_committed_root_again()
    {
        var dbName = Guid.NewGuid().ToString();
        Guid rootId;
        {
            var (setupContext, _) = NewContext(dbName);
            var root = new IcRoot("root-a");
            setupContext.Roots.Add(root);
            await setupContext.SaveChangesAsync();
            rootId = root.Id;
        }

        var (context, _) = NewContext(dbName); // fresh ambient context = a new, independent request
        var loadedRoot = await context.Roots.SingleAsync(r => r.Id == rootId);
        loadedRoot.Version.Should().Be(1);

        loadedRoot.Rename("root-a-renamed-again");
        await context.SaveChangesAsync();

        loadedRoot.Version.Should().Be(2, "an independent later request must bump the already-committed root (B-2b)");
    }

    [Fact]
    public async Task B4_existing_root_bumps_exactly_once_across_two_waves_child_then_root_itself()
    {
        var dbName = Guid.NewGuid().ToString();
        Guid rootId;
        {
            var (setupContext, _) = NewContext(dbName);
            var root = new IcRoot("root-a");
            root.AddChild("child-a");
            setupContext.Roots.Add(root);
            await setupContext.SaveChangesAsync();
            rootId = root.Id;
        }

        var (context, _) = NewContext(dbName); // ONE ambient context spanning both waves below
        var loadedRoot = await context.Roots.Include(r => r.Children).SingleAsync(r => r.Id == rootId);

        // wave 1: modify the CHILD only
        loadedRoot.Children.Single().Relabel("child-a-renamed");
        await context.SaveChangesAsync();
        var versionAfterWave1 = loadedRoot.Version;

        // wave 2: modify the ROOT itself directly, same context/ambient (same logical UoW)
        loadedRoot.Rename("root-a-renamed");
        await context.SaveChangesAsync();

        versionAfterWave1.Should().Be(2, "the child-only mutation in wave 1 must bump the root by exactly one");
        loadedRoot.Version.Should().Be(2, "wave 2's direct root mutation must NOT bump again in the same UoW (B-4)");
    }

    [Fact]
    public async Task B6_a_nested_grandchild_mutation_resolves_to_and_bumps_only_the_ultimate_root()
    {
        var dbName = Guid.NewGuid().ToString();
        Guid rootId;
        {
            var (setupContext, _) = NewContext(dbName);
            var root = new IcRoot("root-a");
            var child = root.AddChild("child-a");
            child.AddGrandchild("grandchild-a");
            setupContext.Roots.Add(root);
            await setupContext.SaveChangesAsync();
            rootId = root.Id;
        }

        var (context, _) = NewContext(dbName);
        var loadedRoot = await context.Roots
            .Include(r => r.Children).ThenInclude(c => c.Grandchildren)
            .SingleAsync(r => r.Id == rootId);

        loadedRoot.Children.Single().Grandchildren.Single().Annotate("changed");
        await context.SaveChangesAsync();

        loadedRoot.Version.Should().Be(2, "B-6: only the grandchild changed, yet the ultimate root's version bumps");
    }
}
