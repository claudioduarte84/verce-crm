using System.Reflection;

namespace Verce.Api;

/// <summary>
/// The complete module set composed by the application. Keeping it in one place prevents
/// architecture verification from silently inspecting a smaller model than production.
/// </summary>
public static class ModuleAssemblyCatalog
{
    public static IReadOnlyList<Assembly> All { get; } =
    [
        typeof(Verce.Modules.Customers.CustomersModuleMarker).Assembly,
        typeof(Verce.Modules.Catalog.CatalogModuleMarker).Assembly,
        typeof(Verce.Modules.Inventory.InventoryModuleMarker).Assembly,
        typeof(Verce.Modules.Costing.CostingModuleMarker).Assembly,
        typeof(Verce.Modules.Pricing.PricingModuleMarker).Assembly,
        typeof(Verce.Modules.Quoting.QuotingModuleMarker).Assembly,
        typeof(Verce.Modules.Sales.SalesModuleMarker).Assembly,
        typeof(Verce.Modules.Production.ProductionModuleMarker).Assembly,
        typeof(Verce.Modules.Energy.EnergyModuleMarker).Assembly,
        typeof(Verce.Modules.Documents.DocumentsModuleMarker).Assembly,
        typeof(Verce.Modules.Finance.FinanceModuleMarker).Assembly,
        typeof(Verce.Modules.Reporting.ReportingModuleMarker).Assembly,
        typeof(Verce.Modules.AI.AIModuleMarker).Assembly,
        typeof(Verce.Modules.Settings.SettingsModuleMarker).Assembly,
    ];
}
