using Microsoft.EntityFrameworkCore;
using Verce.Platform.Audit;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Domain;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Audit;

// Test-only aggregate used ONLY to exercise AuditSaveChangesInterceptor against a REAL
// PostgreSQL database. No entity in the S1 data model is marked [Auditable] yet (S1 ships zero
// business entities — see the S1 FINAL REPORT open issues), so the generic audit mechanism is
// otherwise completely uncovered by integration tests; this proves ADR-0010's mechanics
// directly, the same way TestAggregateModel.cs proves the versioning interceptor's mechanics.

[Auditable]
public sealed class AuditTestRoot : Entity, IDomainEntity
{
    public string Name { get; set; } = string.Empty;
    private AuditTestRoot() { }
    public AuditTestRoot(string name) { Name = name; }
}

public sealed class AuditTestDbContext : DbContext
{
    public DbSet<AuditTestRoot> Roots => Set<AuditTestRoot>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    public AuditTestDbContext(DbContextOptions<AuditTestDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<AuditTestRoot>(b =>
        {
            b.ToTable("audit_test_root");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Name).IsUnique();
        });

        // Mirrors VerceDbContext's own mapping exactly (Persistence/VerceDbContext.cs) so this
        // maps onto the SAME real platform.audit_log table created by the real migration.
        builder.Entity<AuditLogEntry>(b =>
        {
            b.ToTable("audit_log", "platform");
            b.HasKey(e => e.Id);
            b.Property(e => e.EntitySchema).HasMaxLength(64).IsRequired();
            b.Property(e => e.EntityTable).HasMaxLength(128).IsRequired();
            b.Property(e => e.Operation).HasMaxLength(16).IsRequired();
            b.Property(e => e.Source).HasConversion<string>().HasMaxLength(16);
        });
    }
}

public static class AuditTestFactory
{
    public static (AuditTestDbContext Context, AmbientOperationContext Ambient) CreateContext(
        string connectionString, AuditSource source = AuditSource.Api, Guid? actorId = null, string? actorName = null)
    {
        var ambientContext = new AmbientOperationContext(Guid.CreateVersion7(), source, DateTimeOffset.UtcNow);
        if (actorId is not null) ambientContext.SetActor(actorId, actorName);

        var interceptor = new AuditSaveChangesInterceptor(ambientContext, new SystemClock());
        var options = new DbContextOptionsBuilder<AuditTestDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptor)
            .Options;

        return (new AuditTestDbContext(options), ambientContext);
    }
}
