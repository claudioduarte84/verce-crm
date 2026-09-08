using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Domain;

namespace Verce.Platform.Audit;

/// <summary>
/// Generic technical audit trail (ADR-0010). Append-only; one row per changed entity per save
/// wave. Rows produced by every wave of one command share <see cref="CorrelationId"/> and are
/// distinguished by <see cref="WaveIndex"/> — an entity genuinely modified in two waves yields
/// two rows, because two real state transitions occurred (ADR-0010 §Multi-wave alignment).
///
/// Domain entity (Category 1): it has its own identity and is written by application code
/// (the interceptor), even though nothing ever updates a row after insert.
/// </summary>
public sealed class AuditLogEntry : Entity, IDomainEntity
{
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset OperationStartedAt { get; private set; }
    public Guid? UserId { get; private set; }
    public string? UserDisplayName { get; private set; }
    public string EntitySchema { get; private set; } = string.Empty;
    public string EntityTable { get; private set; } = string.Empty;
    public Guid EntityId { get; private set; }
    public string Operation { get; private set; } = string.Empty; // INSERT | UPDATE | DELETE
    public string[]? ChangedColumns { get; private set; }
    public string? OldValuesJson { get; private set; }
    public string? NewValuesJson { get; private set; }
    public Guid CorrelationId { get; private set; }
    public Guid? RequestId { get; private set; }
    public int WaveIndex { get; private set; }
    public AuditSource Source { get; private set; }

    private AuditLogEntry() { }

    public AuditLogEntry(
        DateTimeOffset occurredAt,
        DateTimeOffset operationStartedAt,
        Guid? userId,
        string? userDisplayName,
        string entitySchema,
        string entityTable,
        Guid entityId,
        string operation,
        string[]? changedColumns,
        string? oldValuesJson,
        string? newValuesJson,
        Guid correlationId,
        Guid? requestId,
        int waveIndex,
        AuditSource source)
    {
        Id = Guid.CreateVersion7();
        OccurredAt = occurredAt;
        OperationStartedAt = operationStartedAt;
        UserId = userId;
        UserDisplayName = userDisplayName;
        EntitySchema = entitySchema;
        EntityTable = entityTable;
        EntityId = entityId;
        Operation = operation;
        ChangedColumns = changedColumns;
        OldValuesJson = oldValuesJson;
        NewValuesJson = newValuesJson;
        CorrelationId = correlationId;
        RequestId = requestId;
        WaveIndex = waveIndex;
        Source = source;
    }
}

/// <summary>Marks an entity type as subject to the generic audit interceptor (ADR-0010).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AuditableAttribute : Attribute
{
}
