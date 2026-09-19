namespace Verce.Modules.Costing;

public sealed record AcquisitionCostBasis(
    decimal WeightedAverageUnitCost,
    decimal EligibleQuantityBaseUnit,
    int EligibleReceiptCount);

public sealed record CostingSupplySource(
    Guid Id,
    string Code,
    string Name,
    string BaseUnit,
    bool Active,
    decimal CurrentStockBaseUnit,
    AcquisitionCostBasis? AcquisitionBasis);

/// <summary>
/// Application read port used by Costing orchestration. Its contract contains no EF or Inventory
/// type, so the pure Costing module remains independently testable and cannot form a module cycle.
/// </summary>
public interface ICostingInventoryReader
{
    Task<IReadOnlyList<CostingSupplySource>> SearchAsync(string? search, bool includeInactive, CancellationToken cancellationToken);
    Task<CostingSupplySource?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, CostingSupplySource>> GetAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}
