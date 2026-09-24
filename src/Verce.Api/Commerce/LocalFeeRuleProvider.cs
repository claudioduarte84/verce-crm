using Microsoft.EntityFrameworkCore;
using Verce.Modules.Commerce;
using Verce.Modules.Pricing;
using Verce.Platform.Persistence;

namespace Verce.Api.Commerce;

/// <summary>Truthful S8B adapter for local Pricing FeeRules. It never makes a network call and
/// therefore exposes MANUAL provenance with no invented provider freshness.</summary>
public sealed class LocalFeeRuleProvider(VerceDbContext db) : IChannelFeeProvider
{
    public async Task<ChannelFeeQuote?> ResolveAsync(Guid salesChannelId, decimal comparisonPrice, DateOnly asOf, CancellationToken cancellationToken)
    {
        var rule = await db.Set<FeeRule>().AsNoTracking().Include(x => x.Versions).ThenInclude(x => x.Brackets)
            .AsSplitQuery().SingleOrDefaultAsync(x => x.SalesChannelId == salesChannelId && x.Active, cancellationToken);
        var version = rule?.ResolveVersionAt(asOf);
        if (rule is null || version is null) return null;
        var bracket = version.Brackets.OrderBy(x => x.SortOrder).SingleOrDefault(x => x.Contains(comparisonPrice));
        if (version.Brackets.Count > 0 && bracket is null) return null;
        return new ChannelFeeQuote(rule.Id, version.Id, bracket?.Id,
            bracket?.CommissionPercent ?? version.CommissionPercent,
            bracket?.FixedFee ?? version.FixedFee,
            version.FixedFeeApplication == FixedFeeApplication.PerOrder ? "PER_ORDER" : "PER_UNIT",
            bracket?.MinimumFee ?? version.MinimumFee,
            bracket?.MaximumFee ?? version.MaximumFee,
            "MANUAL", null, null);
    }
}
