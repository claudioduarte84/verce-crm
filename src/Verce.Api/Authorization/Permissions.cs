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
    public static void AddPolicies(Microsoft.AspNetCore.Authorization.AuthorizationOptions options)
    {
        options.AddPolicy(CustomersRead, p => p.RequireRole(Roles.Owner, Roles.Operator, Roles.Viewer));
        options.AddPolicy(CustomersManage, p => p.RequireRole(Roles.Owner, Roles.Operator));
        options.AddPolicy(SettingsManage, p => p.RequireRole(Roles.Owner));
        options.AddPolicy(BrandAssetsManage, p => p.RequireRole(Roles.Owner));
    }
}
