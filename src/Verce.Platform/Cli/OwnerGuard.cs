using Microsoft.EntityFrameworkCore;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;

namespace Verce.Platform.Cli;

public sealed class LastOwnerProtectedException : Exception
{
    public LastOwnerProtectedException() : base(
        "This operation would leave zero active Owners. Rejected (LAST_OWNER_PROTECTED). " +
        "Use the offline recover-owner break-glass command if the account is genuinely locked out.")
    { }
}

/// <summary>
/// API-surface guard against ever reaching zero active Owners (ADR-0009 §10). Serialized on
/// <see cref="AdvisoryLocks.OwnerRoleMutation"/> so two concurrent removals of the last two
/// Owners cannot both pass a stale count. This guard applies ONLY to the normal API/UI surface
/// — the local break-glass <c>recover-owner</c> command is the single documented exception
/// (ADR-0009 §9.1) and does not call this class.
/// </summary>
public sealed class OwnerGuard
{
    private readonly VerceDbContext _context;

    public OwnerGuard(VerceDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Runs <paramref name="mutation"/> inside the advisory lock, re-counting active Owners
    /// afterward; rolls back with <see cref="LastOwnerProtectedException"/> if the count would
    /// reach zero. <c>pg_advisory_xact_lock</c> is released at the enclosing TRANSACTION's end,
    /// not at the end of the single statement that acquired it — so this method owns its own
    /// transaction end-to-end (D-9): without one, the lock would be acquired and released by
    /// that one raw-SQL statement alone, leaving the mutation, save and recount completely
    /// unguarded, which defeats the entire point of taking the lock.
    /// </summary>
    public async Task ExecuteGuardedAsync(Func<Task> mutation, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({AdvisoryLocks.OwnerRoleMutation})", cancellationToken);

            await mutation();
            await _context.SaveChangesAsync(cancellationToken);

            var remainingActiveOwners = await CountActiveOwnersAsync(cancellationToken);
            if (remainingActiveOwners == 0)
                throw new LastOwnerProtectedException();

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<int> CountActiveOwnersAsync(CancellationToken cancellationToken = default)
    {
        var ownerRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == Roles.Owner, cancellationToken);
        if (ownerRole is null) return 0;

        return await (
            from user in _context.Users
            join userRole in _context.UserRoles on user.Id equals userRole.UserId
            where userRole.RoleId == ownerRole.Id
               && user.IsActive
               && user.SetupStatus == SetupStatus.Active
            select user.Id
        ).CountAsync(cancellationToken);
    }
}
