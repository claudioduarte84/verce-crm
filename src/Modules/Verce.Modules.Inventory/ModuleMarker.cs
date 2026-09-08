namespace Verce.Modules.Inventory;

/// <summary>
/// Marker for the Inventory module boundary (ARCHITECTURE.md §3 module map).
/// Owns: Supply, Filament, FilamentLot, SupplyLot, cost history, StockMovement, StockCount.
/// This module has no domain logic yet — it is scaffolded in S1 purely so that
/// module boundaries exist and are enforceable by Verce.Architecture.Tests.
/// Real aggregates, application services and infrastructure arrive in the sprint
/// that owns this module per docs/ROADMAP.md.
/// </summary>
public static class InventoryModuleMarker
{
    public const string ModuleName = "Inventory";
}
