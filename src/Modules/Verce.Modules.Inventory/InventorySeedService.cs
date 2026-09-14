using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verce.Platform.Persistence;

namespace Verce.Modules.Inventory;

/// <summary>Seeds the closed <see cref="SupplyCategory"/> reference-data vocabulary (S3 mission
/// §7) after migrations — mirrors <c>Verce.Modules.Settings.SettingsSeedService</c>'s pattern for
/// <c>BrandAssetType</c> exactly: opt-in via config, gated on no pending migrations, idempotent
/// per row.</summary>
public sealed class InventorySeedService(IServiceScopeFactory scopes, IConfiguration configuration) : IHostedService
{
    public static readonly (string Code, string Name)[] DefaultCategories =
    [
        ("FILAMENT", "Filamento"),
        ("RESIN", "Resina"),
        ("PACKAGING", "Embalagem"),
        ("HARDWARE", "Ferragem"),
        ("ELECTRONICS", "Eletrônico"),
        ("FINISHING", "Acabamento"),
        ("CONSUMABLE", "Consumível"),
        ("OTHER", "Outro"),
    ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!bool.TryParse(configuration["Settings:SeedOnStartup"], out var seedOnStartup) || !seedOnStartup) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VerceDbContext>();
        if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any()) return;

        foreach (var (code, name) in DefaultCategories)
            if (!await db.Set<SupplyCategory>().AnyAsync(x => x.Code == code, cancellationToken))
                db.Add(new SupplyCategory(code, name));
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
