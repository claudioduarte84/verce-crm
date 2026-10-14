using Microsoft.EntityFrameworkCore;
using Npgsql;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Time;

namespace Verce.Modules.Commerce;

/// <summary>Outcome of a mapping command. Refusals carry only the frozen local safe code (STATE-MACHINES §6); an unknown account or mapping is not a result
/// but a <see cref="KeyNotFoundException"/>. <see cref="MappingId"/> is the created mapping for a confirmation.</summary>
public sealed record MarketplaceSkuMappingResult(bool Succeeded, Guid? MappingId, string? Code = null)
{
    public static MarketplaceSkuMappingResult Created(Guid mappingId) => new(true, mappingId);
    public static MarketplaceSkuMappingResult Done() => new(true, null);
    public static MarketplaceSkuMappingResult Refused(string code) => new(false, null, code);
}

/// <summary>
/// Handoff B7 / ADR-0025 §D: the only way an operator creates, replaces or removes SKU authority. Provider-neutral: it never reads a listing, never creates or
/// links a Product or Offer and never relinks a listing (a mapping only feeds future SKU-LINK-1 evaluations). One command is one unit of work, so a replacement
/// either commits the invalidated mapping and the new one together or neither. The partial unique index remains the final arbiter of "one ACTIVE mapping per
/// (account, SKU)"; only that exact constraint, and only the row-version token, are translated to a conflict.
/// </summary>
public sealed class MarketplaceSkuMappingService(IUnitOfWork uow, IClock clock, ICommerceProductEligibilityReader products)
{
    public const string SkuInvalid = "SKU_MAPPING_SKU_INVALID";
    public const string ProductIneligible = "SKU_MAPPING_PRODUCT_INELIGIBLE";
    public const string Conflict = "SKU_MAPPING_CONFLICT";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    /// <summary>The filtered unique index on (marketplace_account_id, external_sku) WHERE active (PostgreSQL truncates identifiers to 63 characters).</summary>
    public const string ActiveSkuIndexName = "ix_marketplace_account_sku_mapping_marketplace_account_id_exte";

    /// <summary>Order is frozen (handoff B7): SKU normalization, account, Product eligibility, then the existing ACTIVE mapping decision.</summary>
    public async Task<MarketplaceSkuMappingResult> ConfirmAsync(Guid accountId, string? externalSku, Guid productId, Guid? replaceMappingId, long? replaceMappingVersion, Guid actorId, CancellationToken cancellationToken = default)
    {
        string sku;
        try { sku = MarketplaceSkuNormalizer.Normalize(externalSku ?? throw new ArgumentException(SkuInvalid)); }
        catch (ArgumentException) { return MarketplaceSkuMappingResult.Refused(SkuInvalid); }
        try
        {
            return await uow.ExecuteAsync(async (db, ct) =>
            {
                if (!await db.Set<MarketplaceAccount>().AsNoTracking().AnyAsync(x => x.Id == accountId, ct)) throw new KeyNotFoundException("MARKETPLACE_ACCOUNT");
                var eligibility = await products.GetActiveAsync([productId], ct);
                if (!eligibility.TryGetValue(productId, out var active) || !active) return MarketplaceSkuMappingResult.Refused(ProductIneligible);
                var now = clock.UtcNow;
                var existing = await db.Set<MarketplaceAccountSkuMapping>().SingleOrDefaultAsync(x => x.MarketplaceAccountId == accountId && x.ExternalSku == sku && x.Active, ct);
                if (existing is not null)
                {
                    // No, partial or stale replace information never replaces silently.
                    if (replaceMappingId != existing.Id || replaceMappingVersion != existing.Version) return MarketplaceSkuMappingResult.Refused(Conflict);
                    existing.Replace(actorId, now);
                }
                var mapping = new MarketplaceAccountSkuMapping(accountId, sku, productId, actorId, now);
                db.Add(mapping);
                return MarketplaceSkuMappingResult.Created(mapping.Id);
            }, cancellationToken);
        }
        // Two requests passed the pre-check together and the unique index let one win, or the named mapping changed between read and write.
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: ActiveSkuIndexName }) { return MarketplaceSkuMappingResult.Refused(Conflict); }
        catch (DbUpdateConcurrencyException) { return MarketplaceSkuMappingResult.Refused(Conflict); }
    }

    public async Task<MarketplaceSkuMappingResult> DeactivateAsync(Guid mappingId, long version, Guid actorId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await uow.ExecuteAsync(async (db, ct) =>
            {
                var mapping = await db.Set<MarketplaceAccountSkuMapping>().SingleOrDefaultAsync(x => x.Id == mappingId, ct) ?? throw new KeyNotFoundException("SKU_MAPPING");
                if (mapping.Version != version) return MarketplaceSkuMappingResult.Refused(ConcurrencyConflict);
                // ADR-0028: an already INACTIVE mapping at its current version is a no-op. Nothing is tracked, so the unit of work neither saves, bumps the version nor audits.
                if (!mapping.Active) return MarketplaceSkuMappingResult.Done();
                mapping.Deactivate(actorId, clock.UtcNow);
                return MarketplaceSkuMappingResult.Done();
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException) { return MarketplaceSkuMappingResult.Refused(ConcurrencyConflict); }
    }
}
