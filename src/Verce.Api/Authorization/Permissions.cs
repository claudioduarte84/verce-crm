using Verce.Platform.Identity;

namespace Verce.Api.Authorization;

/// <summary>Permission-policy names are the only authorization vocabulary used by feature
/// endpoints (SECURITY §3.2). The role mapping is deliberately centralized here.</summary>
public static class Permissions
{
    public const string CustomersRead = "customers:read";
    public const string CustomersManage = "customers:manage";
    public const string SettingsManage = "settings:manage";
    public const string BrandAssetsManage = "brand-assets:manage";
    public const string SuppliesRead = "supplies:read";
    public const string SuppliesManage = "supplies:manage";
    public const string InventoryRead = "inventory:read";
    public const string InventoryManage = "inventory:manage";
    public const string CostingRead = "costing:read";
    public const string CostingCalculate = "costing:calculate";
    public const string CatalogRead = "catalog:read";
    public const string CatalogManage = "catalog:manage";
    public const string PricingRead = "pricing:read";
    public const string PricingCalculate = "pricing:calculate";
    public const string PricingManage = "pricing:manage";
    public const string QuotingRead = "quoting:read";
    public const string QuotingManage = "quoting:manage";
    public const string ProductionRead = "production:read";
    public const string SalesRead = "sales:read";
    public const string SalesManage = "sales:manage";
    public const string ExpensesRead = "expenses:read";
    public const string ExpensesManage = "expenses:manage";
    public static void AddPolicies(Microsoft.AspNetCore.Authorization.AuthorizationOptions options)
    {
        options.AddPolicy(CustomersRead, p => p.RequireRole(Roles.Owner, Roles.Operator, Roles.Viewer));
        options.AddPolicy(CustomersManage, p => p.RequireRole(Roles.Owner, Roles.Operator));
        options.AddPolicy(SettingsManage, p => p.RequireRole(Roles.Owner));
        options.AddPolicy(BrandAssetsManage, p => p.RequireRole(Roles.Owner));
        options.AddPolicy(SuppliesRead, p => p.RequireRole(Roles.Owner, Roles.Operator, Roles.Viewer));
        options.AddPolicy(SuppliesManage, p => p.RequireRole(Roles.Owner, Roles.Operator));
        options.AddPolicy(InventoryRead, p => p.RequireRole(Roles.Owner, Roles.Operator, Roles.Viewer));
        options.AddPolicy(InventoryManage, p => p.RequireRole(Roles.Owner, Roles.Operator));
        options.AddPolicy(CostingRead, p => p.RequireRole(Roles.Owner, Roles.Operator, Roles.Viewer));
        options.AddPolicy(CostingCalculate, p => p.RequireRole(Roles.Owner, Roles.Operator, Roles.Viewer));
        options.AddPolicy(CatalogRead, p => p.RequireRole(Roles.Owner, Roles.Operator, Roles.Viewer));
        options.AddPolicy(CatalogManage, p => p.RequireRole(Roles.Owner, Roles.Operator));
        options.AddPolicy(PricingRead, p => p.RequireRole(Roles.Owner, Roles.Operator, Roles.Viewer));
        options.AddPolicy(PricingCalculate, p => p.RequireRole(Roles.Owner, Roles.Operator, Roles.Viewer));
        options.AddPolicy(PricingManage, p => p.RequireRole(Roles.Owner));
        options.AddPolicy(QuotingRead, p => p.RequireRole(Roles.Owner, Roles.Operator, Roles.Viewer));
        options.AddPolicy(QuotingManage, p => p.RequireRole(Roles.Owner, Roles.Operator));
        options.AddPolicy(ProductionRead, p => p.RequireRole(Roles.Owner, Roles.Operator, Roles.Viewer));
        options.AddPolicy(SalesRead, p => p.RequireRole(Roles.Owner, Roles.Operator, Roles.Viewer));
        options.AddPolicy(SalesManage, p => p.RequireRole(Roles.Owner, Roles.Operator));
        options.AddPolicy(ExpensesRead, p => p.RequireRole(Roles.Owner, Roles.Operator, Roles.Viewer));
        options.AddPolicy(ExpensesManage, p => p.RequireRole(Roles.Owner, Roles.Operator));
    }
}
