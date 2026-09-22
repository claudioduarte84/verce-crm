using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verce.Platform.Persistence;

namespace Verce.Modules.Documents;

/// <summary>
/// ADR-0007 §7 (S7/S14 scope authority gate, OPTION A): seeds the closed <see cref="DocumentType"/>
/// catalogue and the default `VERCE | Proposta Comercial Padrão` template as ordinary rows —
/// mirrors <c>Verce.Modules.Settings.SettingsSeedService</c>'s exact established pattern
/// (idempotent Add-if-not-exists, gated on "no pending migrations" so this binary's model is
/// never seeded against a not-yet-migrated schema, opt-in via <c>Settings:SeedOnStartup</c> for
/// database-less composition tests). This is the repository's own committed seeding convention —
/// followed here rather than inventing a second, migration-embedded mechanism.
/// </summary>
public sealed class DocumentsSeedService(IServiceScopeFactory scopes, IConfiguration configuration) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!bool.TryParse(configuration["Settings:SeedOnStartup"], out var seedOnStartup) || !seedOnStartup) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VerceDbContext>();
        if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any()) return;

        foreach (var (code, name) in new[] { ("QUOTE", "Orçamento"), ("PRODUCTION_ORDER", "Ordem de produção"), ("SHIPPING_LABEL", "Etiqueta de envio") })
            if (!await db.Set<DocumentType>().AnyAsync(x => x.Code == code, cancellationToken))
                db.Add(new DocumentType(code, name));
        await db.SaveChangesAsync(cancellationToken);

        // Idempotency (mission §79): restart must never duplicate the default template or mint a
        // second Version 1 — checked by the SAME "is there already a default QUOTE template"
        // query a second startup would run.
        var hasDefaultQuoteTemplate = await db.Set<DocumentTemplate>()
            .AnyAsync(t => t.DocumentTypeCode == "QUOTE" && t.IsDefault && t.IsActive, cancellationToken);
        if (!hasDefaultQuoteTemplate)
        {
            var template = new DocumentTemplate("QUOTE", "VERCE | Proposta Comercial Padrão", isDefault: true);
            // mission §77/§78: the seed must already satisfy every publish-time validation rule —
            // PublishFirstVersion runs the SAME validator S14's future publish action will use.
            template.PublishFirstVersion(
                DefaultProposalTemplateSeedData.BuildDefinitionJson(),
                DefaultProposalTemplateSeedData.BuildPageSetupJson(),
                DefaultProposalTemplateSeedData.SchemaVersion,
                DocumentTemplateValidator.Validate,
                DateTimeOffset.UtcNow,
                publishedBy: null);
            db.Add(template);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
