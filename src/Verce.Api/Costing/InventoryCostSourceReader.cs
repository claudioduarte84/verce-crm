using Microsoft.EntityFrameworkCore;
using Verce.Modules.Costing;
using Verce.Modules.Inventory;
using Verce.Platform.Persistence;
using Verce.SharedKernel;

namespace Verce.Api.Costing;

/// <summary>Inventory-side EF adapter for Costing's read-only application port.</summary>
public sealed class InventoryCostSourceReader(VerceDbContext db) : ICostingInventoryReader
{
    public async Task<IReadOnlyList<CostingSupplySource>> SearchAsync(
        string? search, bool includeInactive, CancellationToken cancellationToken)
    {
        var query = db.Set<Supply>().AsNoTracking().AsQueryable();
        if (!includeInactive) query = query.Where(supply => supply.Active);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(supply => EF.Functions.ILike(supply.Name, term) || EF.Functions.ILike(supply.Code, term));
        }

        var supplies = await query.OrderBy(supply => supply.Name).ThenBy(supply => supply.Code).Take(100).ToListAsync(cancellationToken);
        return await MaterializeAsync(supplies, cancellationToken);
    }

    public async Task<CostingSupplySource?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var supply = await db.Set<Supply>().AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (supply is null) return null;
        return (await MaterializeAsync([supply], cancellationToken))[0];
    }

    public async Task<IReadOnlyDictionary<Guid, CostingSupplySource>> GetAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return new Dictionary<Guid, CostingSupplySource>();
        var supplies = await db.Set<Supply>().AsNoTracking().Where(supply => ids.Contains(supply.Id)).ToListAsync(cancellationToken);
        return (await MaterializeAsync(supplies, cancellationToken)).ToDictionary(supply => supply.Id);
    }

    private async Task<IReadOnlyList<CostingSupplySource>> MaterializeAsync(
        IReadOnlyList<Supply> supplies, CancellationToken cancellationToken)
    {
        var bases = await LoadBasesAsync(supplies.Select(supply => supply.Id), cancellationToken);
        return supplies.Select(supply => new CostingSupplySource(
            supply.Id, supply.Code, supply.Name, supply.BaseUnit.ToString(), supply.Active,
            supply.CurrentStockBaseUnit, bases.GetValueOrDefault(supply.Id))).ToArray();
    }

    private async Task<Dictionary<Guid, AcquisitionCostBasis>> LoadBasesAsync(
        IEnumerable<Guid> supplyIds, CancellationToken cancellationToken)
    {
        var ids = supplyIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        var rows = await db.Set<InventoryMovement>().AsNoTracking()
            .Where(movement => ids.Contains(movement.SupplyId)
                && movement.Type == InventoryMovementType.PurchaseReceipt
                && movement.QuantityDeltaBaseUnit > 0
                && movement.UnitCostSnapshot != null)
            .Select(movement => new
            {
                movement.SupplyId,
                movement.QuantityDeltaBaseUnit,
                UnitCost = movement.UnitCostSnapshot!.Value,
            })
            .ToListAsync(cancellationToken);

        return rows.GroupBy(row => row.SupplyId).ToDictionary(group => group.Key, group =>
        {
            var quantity = group.Sum(row => row.QuantityDeltaBaseUnit);
            var weightedValue = group.Sum(row => row.QuantityDeltaBaseUnit * row.UnitCost);
            return new AcquisitionCostBasis(
                Rounding.ToInternal(weightedValue / quantity), Rounding.ToQuantity(quantity), group.Count());
        });
    }
}
