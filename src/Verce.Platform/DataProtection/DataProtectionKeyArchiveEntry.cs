using Verce.SharedKernel.Domain;

namespace Verce.Platform.DataProtection;

/// <summary>
/// Category 4 technical table (ADR-0011 §1, DATA-MODEL.md). <c>recover-data-protection</c>
/// (ADR-0008 §2.4) moves unreadable key rows here rather than deleting them, so a wrapping
/// certificate located later can still recover them — disaster recovery must not be a one-way
/// door.
/// </summary>
public sealed class DataProtectionKeyArchiveEntry : TechnicalEntity, ITechnicalTable
{
    public int OriginalKeyId { get; private set; }
    public string? FriendlyName { get; private set; }
    public string Xml { get; private set; } = string.Empty;
    public DateTimeOffset ArchivedAt { get; private set; }
    public Guid? ArchivedBy { get; private set; }
    public string ArchiveReason { get; private set; } = string.Empty;
    public Guid RecoveryOperationId { get; private set; }

    private DataProtectionKeyArchiveEntry() { }

    public DataProtectionKeyArchiveEntry(
        int originalKeyId, string? friendlyName, string xml, DateTimeOffset archivedAt,
        Guid? archivedBy, string archiveReason, Guid recoveryOperationId)
    {
        Id = Guid.CreateVersion7();
        OriginalKeyId = originalKeyId;
        FriendlyName = friendlyName;
        Xml = xml;
        ArchivedAt = archivedAt;
        ArchivedBy = archivedBy;
        ArchiveReason = archiveReason;
        RecoveryOperationId = recoveryOperationId;
    }
}
