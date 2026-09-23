using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verce.Platform.Persistence;

namespace Verce.Modules.Finance;

public sealed class FinanceSeedService(IServiceScopeFactory scopes, IConfiguration configuration) : IHostedService
{
    private const string RequiredMigrationId = "AddS8ASalesAndExpenses";
    public static readonly (string Name, AccountingTreatment Treatment)[] DefaultCategories =
    [
        ("Filamento", AccountingTreatment.INVENTORY_PURCHASE), ("Insumos", AccountingTreatment.INVENTORY_PURCHASE),
        ("Energia", AccountingTreatment.OPERATING_EXPENSE), ("Embalagem", AccountingTreatment.OPERATING_EXPENSE),
        ("Marketing", AccountingTreatment.OPERATING_EXPENSE), ("Frete", AccountingTreatment.OPERATING_EXPENSE),
        ("Manutenção", AccountingTreatment.OPERATING_EXPENSE), ("Equipamento", AccountingTreatment.ASSET_ACQUISITION),
        ("Impostos/Taxas", AccountingTreatment.OPERATING_EXPENSE), ("Outros", AccountingTreatment.OPERATING_EXPENSE)
    ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!bool.TryParse(configuration["Settings:SeedOnStartup"], out var enabled) || !enabled) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VerceDbContext>();
        var applied = await db.Database.GetAppliedMigrationsAsync(cancellationToken);
        if (!applied.Any(x => x.EndsWith(RequiredMigrationId, StringComparison.Ordinal))) return;
        foreach (var (name, treatment) in DefaultCategories)
            if (!await db.Set<ExpenseCategory>().AnyAsync(x => x.Name == name, cancellationToken)) db.Add(new ExpenseCategory(name, treatment));
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
