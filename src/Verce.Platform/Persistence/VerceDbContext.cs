using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Verce.Platform.Audit;
using Verce.Platform.DataProtection;
using Verce.Platform.Identity;
using Verce.Platform.Outbox;

namespace Verce.Platform.Persistence;

/// <summary>
/// The single composed DbContext for the whole system (ADR-0001 §5.1). Entity configurations
/// are discovered per-module by <see cref="DbContext.OnModelCreating"/> via assembly scanning
/// of <see cref="Microsoft.EntityFrameworkCore.IEntityTypeConfiguration{TEntity}"/>. In S1, only
/// the platform schema has real tables (Identity, outbox, audit, setup tokens) — module schemas
/// arrive with each module's own sprint.
///
/// Deliberately NOT sealed: Verce.IntegrationTests derives a test-only subclass
/// (<c>ProbeVerceDbContext</c>) that adds a test-only aggregate on top of the real model, so the
/// A-series domain-event/UnitOfWork contracts can be proven against the ACTUAL
/// <see cref="UnitOfWork.UnitOfWork"/> and the ACTUAL composed model, not a parallel fake —
/// see ADR-0011/ADR-0012 and the S1 FINAL REPORT's IMPLEMENTATION DEVIATIONS. No production
/// code subclasses it; this is the only sanctioned use of that seam.
/// </summary>
public class VerceDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>, IDataProtectionKeyContext
{
    public VerceDbContext(DbContextOptions<VerceDbContext> options) : base(options)
    {
    }

    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<AccountSetupToken> AccountSetupTokens => Set<AccountSetupToken>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<OutboxMessageAttempt> OutboxMessageAttempts => Set<OutboxMessageAttempt>();

    /// <summary>Required by <see cref="IDataProtectionKeyContext"/> — Category 4
    /// technical/framework table (ADR-0011 §1.3), physical shape owned by the framework and
    /// never altered to add application metadata columns.</summary>
    public DbSet<Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey> DataProtectionKeys => Set<Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey>();

    /// <summary>ADR-0008 §2.4: where <c>recover-data-protection</c> archives unreadable key rows
    /// instead of deleting them.</summary>
    public DbSet<DataProtectionKeyArchiveEntry> DataProtectionKeyArchive => Set<DataProtectionKeyArchiveEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // ---- Identity tables live in the "platform" schema, renamed per DATA-MODEL §1 ----
        builder.Entity<ApplicationUser>(b =>
        {
            b.ToTable("user", "platform");
            b.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
            b.Property(u => u.SetupStatus).HasConversion<string>().HasMaxLength(32);
        });
        builder.Entity<ApplicationRole>(b => b.ToTable("role", "platform"));
        builder.Entity<IdentityUserRole<Guid>>(b => b.ToTable("user_role", "platform"));
        builder.Entity<IdentityUserClaim<Guid>>(b => b.ToTable("user_claim", "platform"));
        builder.Entity<IdentityUserLogin<Guid>>(b => b.ToTable("user_login", "platform"));
        builder.Entity<IdentityUserToken<Guid>>(b => b.ToTable("user_token", "platform"));
        builder.Entity<IdentityRoleClaim<Guid>>(b => b.ToTable("role_claim", "platform"));

        // ---- platform.audit_log — append-only, no FK to user (ADR-0010 rule 2) ----
        builder.Entity<AuditLogEntry>(b =>
        {
            b.ToTable("audit_log", "platform");
            b.HasKey(e => e.Id);
            b.Property(e => e.EntitySchema).HasMaxLength(64).IsRequired();
            b.Property(e => e.EntityTable).HasMaxLength(128).IsRequired();
            b.Property(e => e.Operation).HasMaxLength(16).IsRequired();
            b.Property(e => e.Source).HasConversion<string>().HasMaxLength(16);
            b.HasIndex(e => new { e.EntityTable, e.EntityId, e.OccurredAt });
            b.HasIndex(e => e.OccurredAt);
            b.HasIndex(e => e.CorrelationId);
        });

        // ---- platform.account_setup_token ----
        builder.Entity<AccountSetupToken>(b =>
        {
            b.ToTable("account_setup_token", "platform");
            b.HasKey(e => e.Id);
            b.Property(e => e.TokenHash).HasMaxLength(64).IsRequired();
            b.HasIndex(e => e.TokenHash).IsUnique();
            b.Property(e => e.Purpose).HasConversion<string>().HasMaxLength(16);
            b.HasIndex(e => e.UserId).HasFilter("consumed_at IS NULL AND invalidated_at IS NULL");
        });

        // ---- platform.outbox_message ----
        builder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_message", "platform");
            b.HasKey(e => e.Id);
            b.Property(e => e.EventType).HasMaxLength(200).IsRequired();
            b.Property(e => e.PayloadJson).HasColumnType("jsonb").IsRequired();
            b.Property(e => e.IdempotencyKey).HasMaxLength(400);
            b.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(e => e.FailureDispositionValue).HasConversion<string>().HasMaxLength(16).HasColumnName("failure_disposition");
            b.Property(e => e.AggregateType).HasMaxLength(200).IsRequired();

            b.HasIndex(e => e.IdempotencyKey).IsUnique().HasFilter("idempotency_key IS NOT NULL");
            b.HasIndex(e => new { e.Status, e.AvailableAt })
                .HasFilter("status = 'Pending'")
                .HasDatabaseName("ix_outbox_message_pending_eligible");
            b.HasIndex(e => e.LeaseUntil)
                .HasFilter("status = 'Processing'")
                .HasDatabaseName("ix_outbox_message_processing_lease");
            b.HasIndex(e => e.CorrelationId);

            b.ToTable(t => t.HasCheckConstraint(
                "ck_outbox_message_disposition_iff_failed",
                "(status = 'Failed') = (failure_disposition IS NOT NULL)"));
            b.ToTable(t => t.HasCheckConstraint(
                "ck_outbox_message_generation_positive",
                "execution_generation >= 1"));
            b.ToTable(t => t.HasCheckConstraint(
                "ck_outbox_message_attempt_within_budget",
                "attempt_count <= max_attempts"));
        });

        // ---- platform.data_protection_keys — framework schema, unmodified (ADR-0011 §1.3/§4) ----
        builder.Entity<Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey>(b =>
        {
            b.ToTable("data_protection_keys", "platform");
        });

        // ---- platform.outbox_message_attempt — append-only, generation-scoped identity ----
        builder.Entity<OutboxMessageAttempt>(b =>
        {
            b.ToTable("outbox_message_attempt", "platform");
            b.HasKey(e => e.Id);
            b.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(24);
            b.HasIndex(e => new { e.OutboxMessageId, e.ExecutionGeneration, e.AttemptNumber }).IsUnique();
            b.HasIndex(e => new { e.OutboxMessageId, e.ExecutionGeneration, e.StartedAt });
        });

        // ---- platform.data_protection_key_archive (ADR-0008 §2.4) — archived, never deleted ----
        builder.Entity<DataProtectionKeyArchiveEntry>(b =>
        {
            b.ToTable("data_protection_key_archive", "platform");
            b.HasKey(e => e.Id);
            b.Property(e => e.ArchiveReason).HasMaxLength(200).IsRequired();
            b.HasIndex(e => e.RecoveryOperationId);
        });
    }
}
