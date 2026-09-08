using Microsoft.EntityFrameworkCore;
using Verce.Platform.Ownership;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Domain;

namespace Verce.IntegrationTests.Versioning;

// Test-only aggregate model used ONLY to exercise the platform's versioning/ownership
// mechanics against a REAL PostgreSQL database (ADR-0011 §2). No business meaning.

public sealed class TestRoot : AggregateRoot
{
    public string Name { get; set; } = string.Empty;
    public List<TestChild> Children { get; set; } = new();
    private TestRoot() { }
    public TestRoot(string name) { Name = name; }
}

public sealed class TestChild : Entity, IOwnedBy<TestRoot>
{
    public Guid ParentId { get; set; }
    public string Label { get; set; } = string.Empty;
    public List<TestGrandchild> Grandchildren { get; set; } = new();
    private TestChild() { }
    public TestChild(Guid parentId, string label) { ParentId = parentId; Label = label; }
}

public sealed class TestGrandchild : Entity, IOwnedBy<TestChild>
{
    public Guid ParentId { get; set; }
    public string Note { get; set; } = string.Empty;
    private TestGrandchild() { }
    public TestGrandchild(Guid parentId, string note) { ParentId = parentId; Note = note; }
}

public sealed class TestDbContext : DbContext
{
    public DbSet<TestRoot> Roots => Set<TestRoot>();
    public DbSet<TestChild> Children => Set<TestChild>();
    public DbSet<TestGrandchild> Grandchildren => Set<TestGrandchild>();

    public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<TestRoot>(b =>
        {
            b.ToTable("test_root");
            b.HasKey(x => x.Id);
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasMany(x => x.Children).WithOne().HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<TestChild>(b =>
        {
            b.ToTable("test_child");
            b.HasKey(x => x.Id);
            b.HasMany(x => x.Grandchildren).WithOne().HasForeignKey(g => g.ParentId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<TestGrandchild>(b =>
        {
            b.ToTable("test_grandchild");
            b.HasKey(x => x.Id);
        });
    }
}

/// <summary>Builds a fresh AggregateOwnershipRegistry + a fresh AmbientOperationContext for one
/// test's Unit of Work, wiring the SAME AggregateVersionInterceptor Verce.Platform ships.</summary>
public static class TestPlatformFactory
{
    public static (TestDbContext Context, AmbientOperationContext AmbientContext) CreateContext(string connectionString)
    {
        var registry = AggregateOwnershipRegistry.BuildAndValidate(new[] { typeof(TestRoot).Assembly });
        var ambientContext = new AmbientOperationContext(Guid.CreateVersion7(), AuditSource.System, DateTimeOffset.UtcNow);
        var interceptor = new AggregateVersionInterceptor(registry, ambientContext);

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(interceptor)
            .Options;

        return (new TestDbContext(options), ambientContext);
    }
}
