using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Verce.Platform.Ownership;
using Verce.SharedKernel.Domain;

namespace Verce.Platform.UnitOfWork;

/// <summary>
/// Advances each modified aggregate's <see cref="AggregateRoot.Version"/> by EXACTLY ONE per
/// Unit of Work, regardless of how many children changed or how many save waves ran
/// (ADR-0011 §2.4). Runs before every <c>SaveChanges</c>.
///
/// Critical fix (re-gate H-RG2-001 / H-RG3): whether a root is "new in this UoW" is decided
/// ONCE — the first time this interceptor sees it — and recorded in the UoW-scoped
/// <see cref="AmbientOperationContext.VersionHandledAggregates"/> set. It is NEVER re-derived
/// from EF's <see cref="EntityState"/> after the fact, because that state flips to
/// <see cref="EntityState.Unchanged"/>/<see cref="EntityState.Modified"/> after the first save —
/// which is exactly the bug this interceptor exists to avoid re-introducing.
/// </summary>
public sealed class AggregateVersionInterceptor : SaveChangesInterceptor
{
    private readonly AggregateOwnershipRegistry _registry;
    private readonly AmbientOperationContext _ambientContext;

    public AggregateVersionInterceptor(AggregateOwnershipRegistry registry, AmbientOperationContext ambientContext)
    {
        _registry = registry;
        _ambientContext = ambientContext;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null) Process(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null) Process(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Process(DbContext context)
    {
        // Snapshot BEFORE mutating anything, so bumping a root (which is itself a change)
        // does not retroactively make us think the root was "already changed independently".
        var candidateEntries = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(e => _registry.IsTracked(e.Entity.GetType()))
            .ToList();

        foreach (var entry in candidateEntries)
        {
            var rootType = _registry.ResolveRoot(entry.Entity.GetType());
            var rootEntry = entry.Entity.GetType() == rootType
                ? entry
                : FindTrackedRootEntry(context, entry, rootType);

            if (rootEntry is null) continue; // root not tracked — nothing we can bump safely
            if (rootEntry.Entity is not AggregateRoot root) continue;

            if (_ambientContext.VersionHandledAggregates.Contains(root.Id))
                continue; // already settled for this Unit of Work — do not bump twice

            if (rootEntry.State == EntityState.Added)
            {
                root.InitializeVersionForInsert(); // pinned to 1, no concurrency predicate on insert
            }
            else
            {
                root.BumpVersion();
                rootEntry.Property(nameof(AggregateRoot.Version)).IsModified = true;
                if (rootEntry.State == EntityState.Unchanged)
                    rootEntry.State = EntityState.Modified;
            }

            _ambientContext.VersionHandledAggregates.Add(root.Id);
        }
    }

    /// <summary>
    /// Walks the IOwnedBy&lt;TParent&gt; chain from a changed child up to its root, resolving
    /// each intermediate ancestor by ID against the SAME change tracker — this is what "children
    /// are always loaded/modified through their root" (ADR-0011 §2.6) makes reliable: every
    /// ancestor is expected to already be tracked in this context.
    /// </summary>
    private static EntityEntry? FindTrackedRootEntry(DbContext context, EntityEntry childEntry, Type rootType)
    {
        object current = childEntry.Entity;

        while (current.GetType() != rootType)
        {
            var ownedByInterface = current.GetType().GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IOwnedBy<>));
            if (ownedByInterface is null) return null;

            var parentType = ownedByInterface.GetGenericArguments()[0];
            var parentId = (Guid)ownedByInterface.GetProperty(nameof(IOwnedBy<object>.ParentId))!.GetValue(current)!;

            var parentEntry = context.ChangeTracker.Entries()
                .FirstOrDefault(e => e.Entity.GetType() == parentType && e.Entity is IDomainEntity de && de.Id == parentId);

            if (parentEntry is null) return null; // ancestor not tracked — cannot resolve safely
            current = parentEntry.Entity;
        }

        return context.Entry(current);
    }
}
