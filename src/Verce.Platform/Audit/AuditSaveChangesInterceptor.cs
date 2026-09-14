using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Verce.Platform.UnitOfWork;
using Verce.Platform.Persistence;
using Verce.SharedKernel.Domain;
using Verce.SharedKernel.Time;

namespace Verce.Platform.Audit;

/// <summary>
/// Writes one <see cref="AuditLogEntry"/> per changed <c>[Auditable]</c> entity per save wave,
/// in the SAME transaction as the business change (ADR-0010). If this interceptor throws, the
/// whole SaveChanges — and therefore the business transaction — fails; there is deliberately no
/// best-effort audit path (ADR-0010 §Multi-wave alignment: "audit failure fails the business
/// transaction").
/// </summary>
public sealed class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly AmbientOperationContext _ambientContext;
    private readonly IClock _clock;

    public AuditSaveChangesInterceptor(AmbientOperationContext ambientContext, IClock clock)
    {
        _ambientContext = ambientContext;
        _clock = clock;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null) WriteAuditEntries(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null) WriteAuditEntries(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private void WriteAuditEntries(DbContext context)
    {
        var now = _clock.UtcNow;

        foreach (var entry in context.ChangeTracker.Entries()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            if (entry.Metadata.FindProperty(ApplicationMetadataConfiguration.CreatedAt) is null
                || entry.Metadata.FindProperty(ApplicationMetadataConfiguration.CreatedBy) is null
                || entry.Metadata.FindProperty(ApplicationMetadataConfiguration.UpdatedAt) is null
                || entry.Metadata.FindProperty(ApplicationMetadataConfiguration.UpdatedBy) is null) continue;
            if (entry.State == EntityState.Added)
            {
                entry.Property(ApplicationMetadataConfiguration.CreatedAt).CurrentValue = now;
                entry.Property(ApplicationMetadataConfiguration.CreatedBy).CurrentValue = _ambientContext.ActorUserId;
                entry.Property(ApplicationMetadataConfiguration.UpdatedAt).CurrentValue = null;
                entry.Property(ApplicationMetadataConfiguration.UpdatedBy).CurrentValue = null;
            }
            else
            {
                entry.Property(ApplicationMetadataConfiguration.UpdatedAt).CurrentValue = now;
                entry.Property(ApplicationMetadataConfiguration.UpdatedBy).CurrentValue = _ambientContext.ActorUserId;
            }
        }

        var auditableEntries = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(e => e.Entity.GetType().GetCustomAttributes(typeof(AuditableAttribute), inherit: true).Length > 0)
            .ToList();

        if (auditableEntries.Count == 0) return;

        foreach (var entry in auditableEntries)
        {
            var (oldValues, newValues, changedColumns) = ExtractValues(entry);

            var entityId = entry.Entity is IDomainEntity domainEntity ? domainEntity.Id : Guid.Empty;
            var (schema, table) = ResolveSchemaAndTable(context, entry);

            var auditEntry = new AuditLogEntry(
                occurredAt: now,
                operationStartedAt: _ambientContext.OperationStartedAtUtc,
                userId: _ambientContext.ActorUserId,
                userDisplayName: _ambientContext.ActorDisplayName,
                entitySchema: schema,
                entityTable: table,
                entityId: entityId,
                operation: entry.State.ToString().ToUpperInvariant(),
                changedColumns: changedColumns,
                oldValuesJson: oldValues,
                newValuesJson: newValues,
                correlationId: _ambientContext.CorrelationId,
                requestId: _ambientContext.RequestId,
                waveIndex: _ambientContext.WaveIndex,
                source: _ambientContext.Source);

            context.Add(auditEntry);
        }
    }

    private static (string? oldJson, string? newJson, string[]? changedColumns) ExtractValues(EntityEntry entry)
    {
        var isSecret = entry.Metadata.GetProperties()
            .Where(p => p.Name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToHashSet();

        Dictionary<string, object?>? oldValues = null;
        Dictionary<string, object?>? newValues = null;
        List<string>? changed = null;

        if (entry.State != EntityState.Added)
        {
            oldValues = new();
            foreach (var prop in entry.Properties)
                oldValues[prop.Metadata.Name] = isSecret.Contains(prop.Metadata.Name) ? "***" : prop.OriginalValue;
        }

        if (entry.State != EntityState.Deleted)
        {
            newValues = new();
            foreach (var prop in entry.Properties)
                newValues[prop.Metadata.Name] = isSecret.Contains(prop.Metadata.Name) ? "***" : prop.CurrentValue;
        }

        if (entry.State == EntityState.Modified)
        {
            changed = entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).ToList();
        }

        var options = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        return (
            oldValues is null ? null : JsonSerializer.Serialize(oldValues, options),
            newValues is null ? null : JsonSerializer.Serialize(newValues, options),
            changed?.ToArray());
    }

    private static (string schema, string table) ResolveSchemaAndTable(DbContext context, EntityEntry entry)
    {
        var tableName = entry.Metadata.GetTableName() ?? entry.Entity.GetType().Name;
        var schema = entry.Metadata.GetSchema() ?? "public";
        return (schema, tableName);
    }
}
