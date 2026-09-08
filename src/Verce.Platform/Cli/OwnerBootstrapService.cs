using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;
using Verce.SharedKernel.Time;

namespace Verce.Platform.Cli;

public enum BootstrapOutcome { Created, OwnerAlreadyExists }
public enum RecoveryOutcome { Recovered, UserNotFound }

/// <summary>
/// Implements the first-Owner bootstrap and sole-Owner recovery break-glass flows
/// (ADR-0009 §6, §9, §9.1). Both are OFFLINE/LOCAL operations — never reachable over HTTP.
/// </summary>
public sealed class OwnerBootstrapService
{
    private readonly VerceDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IClock _clock;

    public OwnerBootstrapService(VerceDbContext context, UserManager<ApplicationUser> userManager, RoleManager<ApplicationRole> roleManager, IClock clock)
    {
        _context = context;
        _userManager = userManager;
        _roleManager = roleManager;
        _clock = clock;
    }

    /// <summary>
    /// Creates the first Owner. Serialized on <see cref="AdvisoryLocks.OwnerBootstrap"/> — a
    /// constant lock key that exists even on an empty database, so two concurrent invocations
    /// cannot both observe zero Owners and both create one (ADR-0009 §6.1).
    /// Returns (Created, rawSetupToken) or (OwnerAlreadyExists, null).
    /// </summary>
    public async Task<(BootstrapOutcome Outcome, string? RawSetupToken, Guid? UserId)> BootstrapOwnerAsync(
        string email, string displayName, CancellationToken cancellationToken = default)
    {
        await EnsureOwnerRoleExistsAsync();

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({AdvisoryLocks.OwnerBootstrap})", cancellationToken);

        var ownerRole = await _roleManager.FindByNameAsync(Roles.Owner);
        var anyOwnerExists = await _context.UserRoles.AnyAsync(ur => ur.RoleId == ownerRole!.Id, cancellationToken);

        if (anyOwnerExists)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (BootstrapOutcome.OwnerAlreadyExists, null, null);
        }

        var now = _clock.UtcNow;
        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            DisplayName = displayName,
            IsActive = true,
            SetupStatus = SetupStatus.PendingSetup,
            SetupCompletedAt = null,
        };

        // Created with NO password at all — there is no interval in which a guessable
        // credential exists (ADR-0009 §6.3).
        var createResult = await _userManager.CreateAsync(user);
        if (!createResult.Succeeded)
            throw new InvalidOperationException("Failed to create Owner identity: " +
                string.Join("; ", createResult.Errors.Select(e => e.Description)));

        await _userManager.AddToRoleAsync(user, Roles.Owner);

        var (rawToken, hash) = SecretChallenge.GenerateSetupToken();
        var setupToken = new AccountSetupToken(
            userId: user.Id, tokenHash: hash, purpose: AccountSetupTokenPurpose.Bootstrap,
            createdAt: now, validFor: TimeSpan.FromMinutes(30), createdBySource: "CLI", createdByUserId: null);
        _context.AccountSetupTokens.Add(setupToken);

        _context.AuditLog.Add(new Audit.AuditLogEntry(
            occurredAt: now, operationStartedAt: now, userId: null, userDisplayName: "CLI:bootstrap-owner",
            entitySchema: "platform", entityTable: "user", entityId: user.Id, operation: "INSERT",
            changedColumns: null, oldValuesJson: null, newValuesJson: $"{{\"email\":\"{email}\"}}",
            correlationId: Guid.CreateVersion7(), requestId: null, waveIndex: 1, source: UnitOfWork.AuditSource.Cli));

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return (BootstrapOutcome.Created, rawToken, user.Id);
    }

    /// <summary>
    /// Sole-Owner break-glass recovery (ADR-0009 §9.1). NOT subject to the API's
    /// LAST_OWNER_PROTECTED guard: it never removes the Owner role, only transitions the
    /// account to PendingSetup, and the zero-active-Owner window it opens is deliberate,
    /// audited, and closed atomically by token consumption.
    /// </summary>
    public async Task<(RecoveryOutcome Outcome, string? RawSetupToken)> RecoverOwnerAsync(
        string email, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({AdvisoryLocks.OwnerRoleMutation})", cancellationToken);

        var user = await _userManager.FindByEmailAsync(email);
        if (user is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (RecoveryOutcome.UserNotFound, null);
        }

        var now = _clock.UtcNow;

        // Invalidate every prior unconsumed token for this account (newest-only rule).
        var priorTokens = await _context.AccountSetupTokens
            .Where(t => t.UserId == user.Id && t.ConsumedAt == null && t.InvalidatedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var prior in priorTokens) prior.Invalidate(now);

        user.PasswordHash = null;
        user.SetupStatus = SetupStatus.PendingSetup;
        user.SetupCompletedAt = null;
        user.RecoveryStartedAt = now;
        await _userManager.UpdateSecurityStampAsync(user); // terminates every active session
        await _userManager.UpdateAsync(user);

        var (rawToken, hash) = SecretChallenge.GenerateSetupToken();
        var recoveryToken = new AccountSetupToken(
            userId: user.Id, tokenHash: hash, purpose: AccountSetupTokenPurpose.Recovery,
            createdAt: now, validFor: TimeSpan.FromMinutes(30), createdBySource: "CLI", createdByUserId: null);
        _context.AccountSetupTokens.Add(recoveryToken);

        _context.AuditLog.Add(new Audit.AuditLogEntry(
            occurredAt: now, operationStartedAt: now, userId: null, userDisplayName: "CLI:recover-owner",
            entitySchema: "platform", entityTable: "user", entityId: user.Id, operation: "UPDATE",
            changedColumns: new[] { "password_hash", "setup_status", "recovery_started_at", "security_stamp" },
            oldValuesJson: null, newValuesJson: null,
            correlationId: Guid.CreateVersion7(), requestId: null, waveIndex: 1, source: UnitOfWork.AuditSource.Cli));

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return (RecoveryOutcome.Recovered, rawToken);
    }

    /// <summary>Consumes a setup/reset/recovery token atomically (ADR-0009 §7.2): exactly one
    /// concurrent caller can succeed. On success, sets the password and transitions the account
    /// to Active.</summary>
    public async Task<bool> ConsumeSetupTokenAsync(string rawToken, string newPassword, CancellationToken cancellationToken = default)
    {
        var hash = SecretChallenge.HashToken(rawToken);
        var now = _clock.UtcNow;

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        // Atomic conditional consumption — 0 rows affected means already consumed, invalidated,
        // expired, or unknown; all rejected identically by the caller.
        var affected = await _context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE platform.account_setup_token
            SET consumed_at = {now}
            WHERE token_hash = {hash} AND consumed_at IS NULL AND invalidated_at IS NULL AND expires_at > {now}
            """, cancellationToken);

        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var token = await _context.AccountSetupTokens.FirstAsync(t => t.TokenHash == hash, cancellationToken);
        var user = await _userManager.FindByIdAsync(token.UserId.ToString());
        if (user is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var addPasswordResult = await _userManager.AddPasswordAsync(user, newPassword);
        if (!addPasswordResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        user.SetupStatus = SetupStatus.Active;
        user.SetupCompletedAt = now;
        user.RecoveryStartedAt = null;
        await _userManager.UpdateSecurityStampAsync(user);
        await _userManager.UpdateAsync(user);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task EnsureOwnerRoleExistsAsync()
    {
        foreach (var roleName in Roles.All)
        {
            if (!await _roleManager.RoleExistsAsync(roleName))
                await _roleManager.CreateAsync(new ApplicationRole(roleName));
        }
    }
}
