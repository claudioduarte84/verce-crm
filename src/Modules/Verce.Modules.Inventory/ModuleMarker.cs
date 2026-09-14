namespace Verce.Modules.Inventory;

/// <summary>
/// Marker for the Inventory module boundary (ARCHITECTURE.md §3 module map).
/// Owns: Supply, SupplyCategory, InventoryMovement.
/// S3 — Supplies &amp; Inventory: the authoritative registry of materials/production inputs and
/// their stock ledger. Filament is modeled as an optional detail block on Supply (not a separate
/// aggregate) — see docs/architecture/ADR-0017-inventory-ledger-and-stock-concurrency.md for why
/// this narrows the original S1-era Filament-as-its-own-aggregate sketch.
/// </summary>
public static class InventoryModuleMarker
{
    public const string ModuleName = "Inventory";
}
