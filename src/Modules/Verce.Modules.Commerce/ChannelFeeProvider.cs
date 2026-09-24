namespace Verce.Modules.Commerce;

/// <summary>Commerce's provider-neutral fee seam. S8B resolves local configured rules only;
/// provider traffic belongs to a later discovery and connector slice.</summary>
public interface IChannelFeeProvider
{
    Task<ChannelFeeQuote?> ResolveAsync(Guid salesChannelId, decimal comparisonPrice, DateOnly asOf, CancellationToken cancellationToken);
}

public sealed record ChannelFeeQuote(
    Guid FeeRuleId,
    Guid FeeRuleVersionId,
    Guid? PriceBracketId,
    decimal CommissionPercent,
    decimal FixedFee,
    string FixedFeeApplication,
    decimal? MinimumFee,
    decimal? MaximumFee,
    string Provenance,
    DateTimeOffset? ObservedAt,
    DateTimeOffset? ExpiresAt);
