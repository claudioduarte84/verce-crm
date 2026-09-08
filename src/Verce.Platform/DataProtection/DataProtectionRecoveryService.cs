using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Verce.Platform.Audit;
using Verce.Platform.Cli;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Time;

namespace Verce.Platform.DataProtection;

public enum DataProtectionRecoveryOutcome
{
    Recovered,
    RefusedRingStillDecryptable,
}

/// <summary>
/// <c>recover-data-protection</c> (ADR-0008 §2.4, OPERATIONS §5.2, D-2b/E-5..E-8). An offline,
/// deliberately destructive break-glass command — never reachable over HTTP, requires local
/// host access, the recovery secret and <c>--confirm-destroy-secrets</c>.
///
/// Step 4 of the documented procedure ("clear ai_settings.api_key_encrypted... set
/// api_key_recovery_required = true") is a NO-OP here: <c>ai_settings</c> belongs to S13 (AI
/// Insights) and does not exist in S1's schema. Building it now to satisfy this one step would
/// mean shipping S13 business schema seven sprints early, which the S1 mission explicitly
/// forbids. This is a genuine spec inconsistency (ADR-0008 §2.4 assumes a table that is S13
/// scope) — see the S1 FINAL REPORT's OPEN DECISIONS. Steps 1, 2, 3, 5 and 6 are fully
/// implemented; step 4 is a documented gap, not a silent omission.
/// </summary>
public sealed class DataProtectionRecoveryService
{
    private readonly VerceDbContext _context;
    private readonly IKeyManager _keyManager;
    private readonly IClock _clock;

    public DataProtectionRecoveryService(VerceDbContext context, IKeyManager keyManager, IClock clock)
    {
        _context = context;
        _keyManager = keyManager;
        _clock = clock;
    }

    public async Task<(DataProtectionRecoveryOutcome Outcome, int ArchivedCount)> RecoverAsync(
        Guid? operatorUserId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({AdvisoryLocks.DataProtectionCryptoRecovery})", cancellationToken);

        // Step 1: verify the ring genuinely cannot unwrap the existing keys — refuse if it can.
        // CreateEncryptor() is what actually triggers decryption of the key's protected XML;
        // this is the same operation the framework performs the moment anything tries to use
        // the key ring for real protect/unprotect, not a synthetic probe.
        var allKeys = _keyManager.GetAllKeys();
        var undecryptableKeyIds = new List<Guid>();
        foreach (var key in allKeys)
        {
            try { key.CreateEncryptor(); }
            catch { undecryptableKeyIds.Add(key.KeyId); }
        }

        if (undecryptableKeyIds.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (DataProtectionRecoveryOutcome.RefusedRingStillDecryptable, 0);
        }

        var now = _clock.UtcNow;
        var recoveryOperationId = Guid.CreateVersion7();

        // Step 2: archive (never delete) the unreadable rows.
        var allRows = await _context.DataProtectionKeys.ToListAsync(cancellationToken);
        var archivedCount = 0;
        foreach (var row in allRows)
        {
            var xml = row.Xml;
            if (xml is null) continue;
            var keyId = TryParseKeyId(xml);
            if (keyId is null || !undecryptableKeyIds.Contains(keyId.Value)) continue;

            _context.DataProtectionKeyArchive.Add(new DataProtectionKeyArchiveEntry(
                originalKeyId: row.Id,
                friendlyName: row.FriendlyName,
                xml: xml,
                archivedAt: now,
                archivedBy: operatorUserId,
                archiveReason: "recover-data-protection: ring could not decrypt this key",
                recoveryOperationId: recoveryOperationId));
            _context.DataProtectionKeys.Remove(row);
            archivedCount++;
        }

        await _context.SaveChangesAsync(cancellationToken);

        // Step 3: initialize a new key wrapped with the CURRENT certificate.
        _keyManager.CreateNewKey(activationDate: now, expirationDate: now.AddDays(90));

        // Step 4 (ai_settings.api_key_encrypted / api_key_last_four / is_enabled /
        // api_key_recovery_required): intentionally a no-op — see the class-level doc comment.

        // Step 5: regenerate SecurityStamp for every user — all sessions die; passwords
        // (Identity hashes, not Data Protection payloads) are unaffected.
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE platform.\"user\" SET security_stamp = replace(gen_random_uuid()::text, '-', '')",
            cancellationToken);

        // Step 6: audit row naming the operator and the archived key count.
        _context.AuditLog.Add(new AuditLogEntry(
            occurredAt: now, operationStartedAt: now, userId: operatorUserId,
            userDisplayName: operatorUserId is null ? "CLI:recover-data-protection" : null,
            entitySchema: "platform", entityTable: "data_protection_keys", entityId: recoveryOperationId,
            operation: "RECOVER", changedColumns: null, oldValuesJson: null,
            newValuesJson: $$"""{"archivedCount":{{archivedCount}}}""",
            correlationId: recoveryOperationId, requestId: null, waveIndex: 1, source: AuditSource.Cli));

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return (DataProtectionRecoveryOutcome.Recovered, archivedCount);
    }

    private static Guid? TryParseKeyId(string xml)
    {
        try
        {
            var element = XElement.Parse(xml);
            var idAttribute = element.Attribute("id")?.Value;
            return idAttribute is not null && Guid.TryParse(idAttribute, out var id) ? id : null;
        }
        catch
        {
            return null;
        }
    }
}
