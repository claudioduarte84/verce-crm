using Microsoft.AspNetCore.Identity;

namespace Verce.Platform.Identity;

/// <summary>Account setup lifecycle (ADR-0009 §7.1). Login is rejected for PendingSetup
/// regardless of whether a password hash exists — checked before password verification.</summary>
public enum SetupStatus
{
    PendingSetup,
    Active,
}

/// <summary>
/// Framework-owned Identity table (ADR-0011 Category 4), extended with the application columns
/// the bootstrap/recovery flows require. PK is Guid — a deliberate, supported customization of
/// ASP.NET Core Identity, not a modification of its internal schema.
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastLoginAt { get; set; }

    public SetupStatus SetupStatus { get; set; } = SetupStatus.PendingSetup;
    public DateTimeOffset? SetupCompletedAt { get; set; }

    /// <summary>Non-null while a local break-glass recover-owner is in flight (ADR-0009 §9.1).</summary>
    public DateTimeOffset? RecoveryStartedAt { get; set; }
}

/// <summary>Framework-owned Identity role table, PK Guid to match ApplicationUser.</summary>
public sealed class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole() { }
    public ApplicationRole(string roleName) : base(roleName) { }
}

/// <summary>Role name constants (SECURITY §3.1). Never string-literal role checks in handlers —
/// authorization uses permission-policy constants (defined in Verce.Api) mapped to these.</summary>
public static class Roles
{
    public const string Owner = "Owner";
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";

    public static readonly IReadOnlyList<string> All = new[] { Owner, Operator, Viewer };
}
