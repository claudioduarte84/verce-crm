namespace Verce.Modules.Commerce;

// ADR-0025 §B.1 — provider-neutral listing-read port. No ML type/URL/DTO belongs here; only the
// normalized facts a real adapter (Verce.Infrastructure.Marketplaces.MercadoLivre) produces after
// discarding everything the frozen field table (§B.3) does not keep. Never persisted verbatim —
// the sync executor (Stream B) is the only writer, always inside a lease-fenced transaction.
public interface IMarketplaceListingReader
{
    string ProviderCode { get; }
    Task<MarketplaceListingPage> ReadListingPageAsync(MarketplaceListingPageRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<MarketplaceListingDetailResult>> ReadListingDetailsAsync(MarketplaceListingDetailRequest request, CancellationToken cancellationToken);
}

/// <summary>Opaque, in-memory-only enumeration cursor (e.g. a Mercado Livre scroll_id). Never
/// persisted, serialized, logged or returned across a process boundary — §B.2/§E.2: the scroll
/// lifetime risk means a failed continuation restarts enumeration rather than resuming a stored
/// cursor.</summary>
public abstract class MarketplaceListingCursor
{
}

public sealed record MarketplaceListingPageRequest(Guid AccountId, string ExternalAccountId, ReadOnlyMemory<byte> SecretMaterial, MarketplaceListingCursor? Cursor);

/// <summary><paramref name="RejectedIdentifierCount"/> counts IDs the adapter received but dropped
/// as not matching its own identifier shape (§B.2) — never silently absorbed into
/// <paramref name="ExternalListingIds"/>.</summary>
public sealed record MarketplaceListingPage(IReadOnlyList<string> ExternalListingIds, MarketplaceListingCursor? NextCursor, bool EndOfEnumeration, int RejectedIdentifierCount);

public sealed record MarketplaceListingDetailRequest(Guid AccountId, string ExternalAccountId, ReadOnlyMemory<byte> SecretMaterial, IReadOnlyList<string> ExternalListingIds);

public enum MarketplaceListingReadOutcome { FOUND, NOT_FOUND, ACCESS_DENIED, SELLER_MISMATCH, INVALID_RESPONSE, UNMAPPED_STATUS, FAILED }

public sealed record MarketplaceListingDetailResult(string ExternalListingId, MarketplaceListingReadOutcome Outcome, NormalizedListingFacts? Facts, string? ProviderErrorCode);

/// <summary>§B.3 frozen normalized field table. A null field always has a specific, documented
/// meaning (never "zero"/"now"/"active" by default) — see each field's XML doc.</summary>
/// <param name="ExternalSku">SELLER_SKU attribute value; null means no authoritative SKU (absent,
/// ambiguous across variations, or invalid after normalization).</param>
/// <param name="ObservedPrice">BRL only, scale &lt;= 2, &gt; 0; null means unknown price — NEVER
/// zero, and never the item's promotional/sale price.</param>
/// <param name="ProviderObservedAt">Provider `last_updated`; null means unknown — never "now".</param>
public sealed record NormalizedListingFacts(
    string ExternalListingId,
    string? ExternalSku,
    string? Title,
    decimal? ObservedPrice,
    MarketplaceListingStatus ObservedStatus,
    string? ProviderNativeStatus,
    int VariationCount,
    string? ListingUrl,
    DateTimeOffset? ProviderObservedAt,
    IReadOnlyList<string> NormalizationWarnings);

/// <summary>ADR-0025 §D.2 — a Commerce contract implemented in Verce.Api against Catalog, so
/// Commerce never gains a physical FK/navigation onto a Catalog Product (rule 11).</summary>
public interface ICommerceProductEligibilityReader
{
    Task<IReadOnlyDictionary<Guid, bool>> GetActiveAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);
}
