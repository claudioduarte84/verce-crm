namespace Verce.Platform.Cli;

/// <summary>
/// PostgreSQL advisory lock key registry (ADR-0009 §6.1). Keys are constants, documented in
/// one place so two features never collide. All locks are transaction-scoped
/// (<c>pg_advisory_xact_lock</c>) — released automatically on commit or rollback, including if
/// the process is killed, so a crashed operation can never wedge the system.
/// </summary>
public static class AdvisoryLocks
{
    /// <summary>Owner bootstrap (ADR-0009 §6.1). Exists as a constant key, not a row — safe on
    /// an empty database where no Owner row exists yet to lock.</summary>
    public const long OwnerBootstrap = 8401001;

    /// <summary>Owner-role mutation / last-Owner guard (ADR-0009 §10, §9.1).</summary>
    public const long OwnerRoleMutation = 8401002;

    /// <summary>Data Protection crypto recovery (ADR-0008 §2.4).</summary>
    public const long DataProtectionCryptoRecovery = 8401003;
}
