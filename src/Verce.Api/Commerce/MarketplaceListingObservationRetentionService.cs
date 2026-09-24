using Microsoft.EntityFrameworkCore;
using Verce.Modules.Commerce;
using Verce.Modules.Settings;
using Verce.Platform.Persistence;
using Verce.SharedKernel.Time;

namespace Verce.Api.Commerce;

/// <summary>Bounded, local cleanup for safe listing observations. Scheduling is intentionally
/// deferred: ADR-0023 freezes the cleanup seam, not a second S8B scheduler.</summary>
public sealed class MarketplaceListingObservationRetentionService(
    VerceDbContext db, AppSettingValueReader settings, IClock clock)
{
    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var days = await settings.GetIntAsync("commerce.listing_observation_retention_days", cancellationToken);
        var cutoff = clock.UtcNow.AddDays(-days);
        // The latest row is determined independently for each listing, then excluded even when
        // it predates the cutoff. This preserves a durable safe current-history anchor.
        var candidates = await db.Set<MarketplaceListingObservation>()
            .Where(x => x.IngestedAt < cutoff)
            .Where(x => db.Set<MarketplaceListingObservation>().Any(y =>
                y.MarketplaceListingId == x.MarketplaceListingId &&
                (y.IngestedAt > x.IngestedAt || (y.IngestedAt == x.IngestedAt && y.Id.CompareTo(x.Id) > 0))))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0) return 0;
        return await db.Set<MarketplaceListingObservation>().Where(x => candidates.Contains(x.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
