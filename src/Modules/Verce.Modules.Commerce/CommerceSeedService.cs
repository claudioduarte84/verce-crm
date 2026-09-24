using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verce.Platform.Persistence;

namespace Verce.Modules.Commerce;

/// <summary>Seeds only provider-neutral reference rows after the S8B migration. No account,
/// listing or credential is fabricated; repeated starts preserve all operator-owned data.</summary>
public sealed class CommerceSeedService(IServiceScopeFactory scopes, IConfiguration configuration) : IHostedService
{
    private const string RequiredMigrationId = "20260923131458_AddS8BCommerceFoundation";
    private static readonly (string Code,string Name)[] Providers=[("MERCADO_LIVRE","Mercado Livre"),("SHOPEE","Shopee"),("TIKTOK_SHOP","TikTok Shop")];
    private static readonly string[] Capabilities=["LISTINGS_READ","LISTINGS_WRITE","ORDERS_READ","FEES_QUOTE","ANALYTICS_READ","ADS_READ","SHIPPING_READ","INVENTORY_SYNC"];
    public async Task StartAsync(CancellationToken ct){if(!bool.TryParse(configuration["Settings:SeedOnStartup"],out var enabled)||!enabled)return;using var scope=scopes.CreateScope();var db=scope.ServiceProvider.GetRequiredService<VerceDbContext>();if(!(await db.Database.GetAppliedMigrationsAsync(ct)).Contains(RequiredMigrationId))return;foreach(var p in Providers){if(!await db.Set<MarketplaceProvider>().AnyAsync(x=>x.Code==p.Code,ct))db.Add(new MarketplaceProvider(p.Code,p.Name));foreach(var capability in Capabilities)if(!await db.Set<MarketplaceProviderCapability>().AnyAsync(x=>x.ProviderCode==p.Code&&x.CapabilityCode==capability,ct))db.Add(new MarketplaceProviderCapability(p.Code,capability));}await db.SaveChangesAsync(ct);}
    public Task StopAsync(CancellationToken ct)=>Task.CompletedTask;
}
