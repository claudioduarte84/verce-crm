using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verce.Platform.Persistence;

namespace Verce.Modules.Pricing;

/// <summary>
/// Seeds the mandatory "Venda Direta" <see cref="SalesChannel"/> and its zero-fee
/// <see cref="FeeRuleVersion"/> (ADR-0005 §2: "the seeded zero-fee rule is therefore mandatory
/// seed data, not a convenience" — direct sale is the degenerate marketplace, never a special
/// case in <see cref="PricingEngine"/>). Mirrors <c>InventorySeedService</c>'s pattern exactly:
/// opt-in via config, gated on no pending migrations, idempotent. Deliberately does NOT seed any
/// real marketplace vendor (Shopee, Mercado Livre, ...) — mission §61/§76: those are commercial
/// configuration an Owner enters, never hard-coded business data.
/// </summary>
public sealed class PricingSeedService(IServiceScopeFactory scopes, IConfiguration configuration) : IHostedService
{
    /// <summary>Kept as an alias of <see cref="SalesChannel.DirectChannelCode"/> — the AR that
    /// enforces the Direct-channel invariants (Terra N-03) owns the canonical constant.</summary>
    public const string DirectChannelCode = SalesChannel.DirectChannelCode;
    private static readonly DateOnly SeedValidFrom = new(2020, 1, 1);

    /// <summary>
    /// H-03: this service depends on exactly ONE migration — the one that creates
    /// <c>pricing.sales_channel</c>/<c>pricing.fee_rule</c>/<c>pricing.fee_rule_version</c>.
    /// A blanket "zero pending migrations" check is wrong: a binary that ships a LATER, unrelated
    /// migration (e.g. S6's Quoting/Production schema) would see that later migration as
    /// "pending" against an S5-terminal database and wrongly refuse to seed Pricing data, even
    /// though every table this service touches already exists. Checking for THIS migration by
    /// name in the applied set is schema-aware — it seeds as soon as its own dependency is
    /// satisfied, regardless of what else the running binary's model happens to know about.
    /// </summary>
    private const string RequiredMigrationId = "20260919152506_AddS5ProductsRecipesAndPricing";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!bool.TryParse(configuration["Settings:SeedOnStartup"], out var seedOnStartup) || !seedOnStartup) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VerceDbContext>();
        var applied = await db.Database.GetAppliedMigrationsAsync(cancellationToken);
        if (!applied.Contains(RequiredMigrationId)) return;

        var direct = await db.Set<SalesChannel>().SingleOrDefaultAsync(x => x.Code == DirectChannelCode, cancellationToken);
        if (direct is null)
        {
            direct = new SalesChannel(DirectChannelCode, "Venda Direta", SalesChannelKind.Direct, defaultMarginPercent: null, notes: null);
            db.Add(direct);
        }

        var feeRule = await db.Set<FeeRule>().Include(x => x.Versions).SingleOrDefaultAsync(x => x.SalesChannelId == direct.Id, cancellationToken);
        if (feeRule is null)
        {
            feeRule = new FeeRule(direct.Id, "Venda Direta — sem comissão");
            feeRule.AddVersion(SeedValidFrom, validUntil: null, commissionPercent: 0m, fixedFee: 0m,
                FixedFeeApplication.PerUnit, minimumFee: null, maximumFee: null, notes: "Seed ADR-0005 §2",
                channelKind: SalesChannelKind.Direct);
            db.Add(feeRule);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
