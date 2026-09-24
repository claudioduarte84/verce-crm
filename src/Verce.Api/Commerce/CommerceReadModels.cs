using Microsoft.EntityFrameworkCore;
using Verce.Api.Catalog;
using Verce.Modules.Catalog;
using Verce.Modules.Commerce;
using Verce.Modules.Costing;
using Verce.Modules.Pricing;
using Verce.Modules.Sales;
using Verce.Modules.Settings;
using Verce.Platform.Persistence;

namespace Verce.Api.Commerce;

public sealed record CommercialTagReadModel(Guid Id, string Code, string Name);
public sealed record CommercialImageReadModel(Guid BrandAssetId, Guid? VersionId, string Url, string? AltText);
public sealed record ChannelSalesReadModel(decimal? Units, decimal? Revenue, string Coverage);
public sealed record CatalogOfferReadModel(Guid Id, Guid SalesChannelId, string ChannelCode, string ChannelName,
    SalesChannelKind ChannelKind, ChannelOfferStatus Status, decimal IntendedUnitPrice, ChannelOfferPriceSource PriceSource,
    decimal? SellerPaidShippingAmount, string ShippingState, ChannelSalesReadModel Sales, long Version);
public sealed record CommercialCatalogItemReadModel(Guid ProductId, string Code, string Name, bool TechnicalActive,
    bool CommercialActive, CommercialImageReadModel? PrimaryImage, IReadOnlyList<CommercialTagReadModel> Tags,
    decimal? EstimatedUnitCost, string CostCoverage, IReadOnlyList<string> MissingCostComponents,
    IReadOnlyList<CatalogOfferReadModel> Offers);
public sealed record CommercialCatalogResponse(IReadOnlyList<CommercialCatalogItemReadModel> Items, int Page, int PageSize, int Total);

public sealed record PublishedItemReadModel(Guid Id, string ProviderCode, Guid MarketplaceAccountId, string AccountName,
    Guid SalesChannelId, string ChannelCode, string ChannelName, string ExternalListingId, string? ExternalSku,
    string? Title, decimal? ObservedPrice, MarketplaceListingStatus ObservedStatus, Guid? ProductId, string? ProductCode,
    string? ProductName, Guid? ChannelOfferId, MarketplaceLinkageState LinkageState, MarketplaceSyncState SyncState,
    DateTimeOffset? LastObservedAt, DateTimeOffset? LastSyncAttemptAt, DateTimeOffset? LastSuccessfulSyncAt,
    string? SafeSyncError, long Version);
public sealed record PublishedItemsResponse(IReadOnlyList<PublishedItemReadModel> Items, int Page, int PageSize, int Total);

public static class CommerceReadModels
{
    public static async Task<IResult> CatalogAsync(VerceDbContext db, ICostingInventoryReader inventory,
        AppSettingValueReader settings, string? search = null, bool? active = null, bool? commercialActive = null,
        Guid? salesChannelId = null, Guid? tagId = null, string? sort = null, int page = 1, int pageSize = 25,
        CancellationToken ct = default)
    {
        var offersQuery = db.Set<ChannelOffer>().AsNoTracking();
        if (salesChannelId is not null) offersQuery = offersQuery.Where(x => x.SalesChannelId == salesChannelId);
        var productsQuery = db.Set<Product>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim(); productsQuery = productsQuery.Where(x => x.Name.Contains(term) || x.Code.Contains(term)); }
        if (active is not null) productsQuery = productsQuery.Where(x => x.Active == active);
        if (commercialActive is not null)
        {
            var activeProductIds = offersQuery.Where(x => x.Status == ChannelOfferStatus.ACTIVE).Select(x => x.ProductId);
            productsQuery = commercialActive.Value
                ? productsQuery.Where(x => x.Active && activeProductIds.Contains(x.Id))
                : productsQuery.Where(x => !x.Active || !activeProductIds.Contains(x.Id));
        }
        if (salesChannelId is not null) productsQuery = productsQuery.Where(x => offersQuery.Select(o => o.ProductId).Contains(x.Id));
        if (tagId is not null)
        {
            var taggedProfiles = db.Set<ProductCommercialTag>().Where(x => x.CommercialTagId == tagId).Select(x => x.ProductCommercialProfileId);
            productsQuery = productsQuery.Where(x => db.Set<ProductCommercialProfile>().Where(p => taggedProfiles.Contains(p.Id)).Select(p => p.ProductId).Contains(x.Id));
        }
        productsQuery = sort == "code" ? productsQuery.OrderBy(x => x.Code) : productsQuery.OrderBy(x => x.Name).ThenBy(x => x.Code);
        var safePage = Math.Max(1, page); var size = Math.Clamp(pageSize, 1, 100); var total = await productsQuery.CountAsync(ct);
        var products = await productsQuery.Include(x => x.Recipe).ThenInclude(x => x.MaterialLines)
            .Include(x => x.Recipe).ThenInclude(x => x.AdditionalCostLines).AsSplitQuery()
            .Skip((safePage - 1) * size).Take(size).ToListAsync(ct);
        var productIds = products.Select(x => x.Id).ToArray();
        var offers = await db.Set<ChannelOffer>().AsNoTracking().Where(x => productIds.Contains(x.ProductId)).ToListAsync(ct);
        var channelIds = offers.Select(x => x.SalesChannelId).Distinct().ToArray();
        var channels = await db.Set<SalesChannel>().AsNoTracking().Where(x => channelIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var profiles = await db.Set<ProductCommercialProfile>().AsNoTracking().Include(x => x.Images).Include(x => x.Tags)
            .Where(x => productIds.Contains(x.ProductId)).AsSplitQuery().ToListAsync(ct);
        var tagIds = profiles.SelectMany(x => x.Tags).Select(x => x.CommercialTagId).Distinct().ToArray();
        var tags = await db.Set<CommercialTag>().AsNoTracking().Where(x => tagIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var assetIds = profiles.SelectMany(x => x.Images).Select(x => x.BrandAssetId).Distinct().ToArray();
        var assets = await db.Set<BrandAsset>().AsNoTracking().Where(x => assetIds.Contains(x.Id) && x.IsActive && x.DeletedAt == null)
            .ToDictionaryAsync(x => x.Id, ct);
        var sales = await (from item in db.Set<SaleItem>().AsNoTracking()
                           join sale in db.Set<Sale>().AsNoTracking() on item.SaleId equals sale.Id
                           where item.ProductId != null && productIds.Contains(item.ProductId.Value) && sale.Status == SaleStatus.CONFIRMED
                           group item by new { ProductId = item.ProductId!.Value, sale.SalesChannelId } into grouped
                           select new { grouped.Key.ProductId, grouped.Key.SalesChannelId, Units = grouped.Sum(x => x.Quantity), Revenue = grouped.Sum(x => x.LineTotalAmount) })
            .ToListAsync(ct);
        var result = new List<CommercialCatalogItemReadModel>();
        foreach (var product in products)
        {
            decimal? estimatedCost = null; var missing = new List<string> { "ENERGY_UNAVAILABLE" };
            try { estimatedCost = (await ProductCostCalculator.CalculateAsync(product, inventory, settings, ct)).Totals.EstimatedUnitCost; }
            catch (ArgumentException) { missing.Add("PRODUCT_COST_UNAVAILABLE"); }
            var profile = profiles.SingleOrDefault(x => x.ProductId == product.Id);
            var primary = profile?.Images.OrderBy(x => x.Role == CommercialImageRole.PRIMARY ? 0 : 1).ThenBy(x => x.SortOrder)
                .FirstOrDefault(x => assets.ContainsKey(x.BrandAssetId));
            CommercialImageReadModel? image = null;
            if (primary is not null && assets.TryGetValue(primary.BrandAssetId, out var asset) && asset.CurrentVersionId is { } versionId)
                image = new(primary.BrandAssetId, versionId, $"/api/settings/brand-assets/versions/{versionId}/content", primary.AltText);
            var productOffers = offers.Where(x => x.ProductId == product.Id).OrderBy(x => channels.GetValueOrDefault(x.SalesChannelId)?.Name).Select(offer =>
            {
                var channel = channels[offer.SalesChannelId]; var metric = sales.SingleOrDefault(x => x.ProductId == product.Id && x.SalesChannelId == offer.SalesChannelId);
                var coverage = CommercePolicy.ResolveSalesCoverage(channel.Kind == SalesChannelKind.Direct, metric is not null).ToString();
                var direct = channel.Kind == SalesChannelKind.Direct;
                return new CatalogOfferReadModel(offer.Id, channel.Id, channel.Code, channel.Name, channel.Kind, offer.Status,
                    offer.IntendedUnitPrice, offer.PriceSource, offer.SellerPaidShippingAmount,
                    offer.SellerPaidShippingAmount is null ? "UNKNOWN" : "KNOWN", new(direct ? metric?.Units ?? 0m : metric?.Units, direct ? metric?.Revenue ?? 0m : metric?.Revenue, coverage), offer.Version);
            }).ToArray();
            result.Add(new(product.Id, product.Code, product.Name, product.Active,
                CommercePolicy.IsCommerciallyActive(product.Active, productOffers.Select(x => x.Status)), image,
                profile?.Tags.Where(x => tags.ContainsKey(x.CommercialTagId)).Select(x => tags[x.CommercialTagId]).OrderBy(x => x.Name)
                    .Select(x => new CommercialTagReadModel(x.Id, x.Code, x.Name)).ToArray() ?? [],
                estimatedCost, estimatedCost is null ? "UNAVAILABLE" : "PARTIAL_ESTIMATE", missing, productOffers));
        }
        return Results.Ok(new CommercialCatalogResponse(result, safePage, size, total));
    }

    public static async Task<IResult> PublishedItemsAsync(VerceDbContext db, string? provider = null,
        Guid? marketplaceAccountId = null, Guid? salesChannelId = null, MarketplaceListingStatus? observedStatus = null,
        MarketplaceLinkageState? linkage = null, MarketplaceSyncState? syncState = null, string? search = null,
        string? sort = null, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        var accounts = db.Set<MarketplaceAccount>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(provider)) accounts = accounts.Where(x => x.ProviderCode == provider.Trim().ToUpper());
        if (marketplaceAccountId is not null) accounts = accounts.Where(x => x.Id == marketplaceAccountId);
        if (salesChannelId is not null) accounts = accounts.Where(x => x.SalesChannelId == salesChannelId);
        var query = db.Set<MarketplaceListing>().AsNoTracking().Where(x => accounts.Select(a => a.Id).Contains(x.MarketplaceAccountId));
        if (linkage is not null) query = query.Where(x => x.LinkageState == linkage);
        if (observedStatus is not null) query = query.Where(x => x.ObservedStatus == observedStatus);
        if (syncState is not null) query = query.Where(x => x.SyncState == syncState);
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim(); query = query.Where(x => x.ExternalListingId.Contains(term) || (x.ExternalSku != null && x.ExternalSku.Contains(term)) || (x.TitleSnapshot != null && x.TitleSnapshot.Contains(term))); }
        query = sort == "recent"
            ? query.OrderByDescending(x => x.ProviderObservedAt).ThenBy(x => x.ExternalListingId)
                .ThenBy(x => accounts.Where(a => a.Id == x.MarketplaceAccountId).Select(a => a.ProviderCode).Single())
                .ThenBy(x => accounts.Where(a => a.Id == x.MarketplaceAccountId).Select(a => a.ExternalAccountId).Single())
            : query.OrderBy(x => x.ExternalListingId)
                .ThenBy(x => accounts.Where(a => a.Id == x.MarketplaceAccountId).Select(a => a.ProviderCode).Single())
                .ThenBy(x => accounts.Where(a => a.Id == x.MarketplaceAccountId).Select(a => a.ExternalAccountId).Single());
        var safePage = Math.Max(1, page); var size = Math.Clamp(pageSize, 1, 100); var total = await query.CountAsync(ct);
        var rows = await query.Skip((safePage - 1) * size).Take(size).ToListAsync(ct);
        var accountIds = rows.Select(x => x.MarketplaceAccountId).Distinct().ToArray();
        var accountRows = await db.Set<MarketplaceAccount>().AsNoTracking().Where(x => accountIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var channelIds = accountRows.Values.Select(x => x.SalesChannelId).Distinct().ToArray();
        var channels = await db.Set<SalesChannel>().AsNoTracking().Where(x => channelIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var productIds = rows.Where(x => x.ProductId != null).Select(x => x.ProductId!.Value).Distinct().ToArray();
        var products = await db.Set<Product>().AsNoTracking().Where(x => productIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var items = rows.Select(row =>
        {
            var account = accountRows[row.MarketplaceAccountId];
            var channel = channels.GetValueOrDefault(account.SalesChannelId);
            var product = row.ProductId is { } id ? products.GetValueOrDefault(id) : null;
            return new PublishedItemReadModel(row.Id, account.ProviderCode, account.Id, account.DisplayName,
                account.SalesChannelId, channel?.Code ?? "UNKNOWN", channel?.Name ?? "Canal indisponível",
                row.ExternalListingId, row.ExternalSku, row.TitleSnapshot, row.ObservedPrice, row.ObservedStatus,
                row.ProductId, product?.Code, product?.Name, row.ChannelOfferId, row.LinkageState, row.SyncState,
                row.ProviderObservedAt, row.LastSyncAttemptAt,
                row.LastSuccessfulSyncAt, row.SyncError, row.Version);
        }).ToArray();
        return Results.Ok(new PublishedItemsResponse(items, safePage, size, total));
    }
}
