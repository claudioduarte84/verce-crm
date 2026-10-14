using Microsoft.EntityFrameworkCore;
using Verce.Modules.Catalog;
using Verce.Modules.Commerce;
using Verce.Platform.Persistence;

namespace Verce.Api.Commerce;

/// <summary>Composition-root implementation of the Commerce port. It intentionally exposes only
/// the active bit, keeping Catalog's aggregate and table private to listing synchronization.</summary>
public sealed class CommerceProductEligibilityReader(VerceDbContext db) : ICommerceProductEligibilityReader
{
    public async Task<IReadOnlyDictionary<Guid, bool>> GetActiveAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        if (productIds.Count == 0) return new Dictionary<Guid, bool>();
        return await db.Set<Product>().AsNoTracking().Where(x => productIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Active, cancellationToken);
    }
}
