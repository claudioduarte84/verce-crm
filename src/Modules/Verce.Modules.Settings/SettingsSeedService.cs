using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Http;
using Verce.Platform.Persistence;

namespace Verce.Modules.Settings;

/// <summary>Seeds only closed lookup/configuration data after migrations. It deliberately does
/// not create credentials or uploaded files; the Owner controls all mutable brand content.</summary>
public sealed class SettingsSeedService(
    IServiceScopeFactory scopes,
    Microsoft.Extensions.Configuration.IConfiguration configuration) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Database-less composition tests intentionally boot the real host with an unreachable
        // connection string only to inspect routing metadata. Seeding is opt-in for those hosts.
        if (!bool.TryParse(configuration["Settings:SeedOnStartup"], out var seedOnStartup) || !seedOnStartup) return;
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<VerceDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<BrandAssetStorage>();
        if (!(await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
        {
            foreach (var (code, name) in new[] { ("PRIMARY_LOGO", "Logo principal"), ("COMPACT_LOGO", "Logo compacto"), ("NEGATIVE_LOGO", "Logo negativo"), ("SYMBOL", "Símbolo"), ("FAVICON", "Favicon"), ("DOCUMENT_LOGO", "Logo de documento"), ("OTHER", "Outro") })
                if (!await db.Set<BrandAssetType>().AnyAsync(x => x.Code == code, cancellationToken)) db.Add(new BrandAssetType(code, name));
            if (!await db.Set<CompanyProfile>().AnyAsync(cancellationToken)) db.Add(new CompanyProfile(new("VERCE 3D", "VERCE 3D", null, null, null, null, null, null, null, null, null, null, null, null, null, "BR", "America/Sao_Paulo", "BRL")));
            foreach (var seed in SettingSeeds.All)
                if (!await db.Set<AppSetting>().AnyAsync(x => x.Key == seed.Key, cancellationToken)) db.Add(new AppSetting(seed.Key, seed.Value, seed.Type, seed.Scope, seed.Description));
            if (!await db.Set<BrandAsset>().AnyAsync(cancellationToken))
            {
                // Deterministic, visible default VERCE branding (H-S2-001) — a real geometric
                // mark in the frozen v1 palette, not a transparent 1×1 placeholder. Each boot
                // asset gets its OWN generated image (distinct content, distinct hash), passed
                // through the identical upload/storage pipeline a real operator upload uses.
                // Owners replace these through the versioned library; no visual import exists in
                // the frontend (ADR-0015 §5).
                var now = DateTimeOffset.UtcNow;
                async Task<BrandAsset> SeedAssetAsync(string typeCode, string name, byte[] png)
                {
                    await using var stream = new MemoryStream(png, writable: false);
                    var file = new FormFile(stream, 0, png.Length, "file", $"verce-default-{typeCode.ToLowerInvariant()}.png") { Headers = new HeaderDictionary(), ContentType = "image/png" };
                    var stored = await storage.StoreAsync(file, cancellationToken);
                    var asset = new BrandAsset(typeCode, name);
                    asset.AddVersion(stored.Upload, null, now);
                    db.Add(asset);
                    return asset;
                }

                var primary = await SeedAssetAsync("PRIMARY_LOGO", "VERCE 3D", BrandSeedAssets.WideLockupPng());
                var compact = await SeedAssetAsync("COMPACT_LOGO", "VERCE 3D compacto", BrandSeedAssets.CompactMarkPng());
                await SeedAssetAsync("SYMBOL", "VERCE 3D símbolo", BrandSeedAssets.CompactMarkPng());
                var favicon = await SeedAssetAsync("FAVICON", "VERCE 3D favicon", BrandSeedAssets.FaviconMarkPng());

                db.AddRange(
                    new BrandingAssignment("SYSTEM_LOGO", primary.Id),
                    new BrandingAssignment("SYSTEM_LOGO_COMPACT", compact.Id),
                    new BrandingAssignment("FAVICON", favicon.Id),
                    new BrandingAssignment("DOCUMENT_DEFAULT_LOGO", primary.Id));
            }
            await db.SaveChangesAsync(cancellationToken);
        }
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
public static class SettingSeeds
{
    public static readonly (string Key, string Value, AppSettingValueType Type, string Scope, string Description)[] All =
        SettingCatalog.All.Select(item => (item.Key, item.DefaultValue, item.ValueType, item.Scope, item.Description)).ToArray();
}
