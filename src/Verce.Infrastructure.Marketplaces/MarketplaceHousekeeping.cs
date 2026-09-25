using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace Verce.Infrastructure.Marketplaces;

/// <summary>ADR-0024 "Cleanup": startup + every-15-minutes, batch 100. Configurable per CLAUDE.md
/// rule 10 — never hard-coded.</summary>
public sealed class MarketplaceCleanupJobOptions
{
    public const string SectionName = "Marketplaces:Cleanup";
    public bool SchedulingEnabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 900;
    public int BatchSize { get; set; } = 100;
}

public static class MarketplaceCleanupJobKeys
{
    public static readonly JobKey Job = new("MarketplaceCleanupJob", "marketplaces");
}

/// <summary>
/// One sweep = expire due PENDING sessions, resolve abandoned CLAIMED sessions/orphan PENDING
/// operations through the terminal arbiter, then physically clean up already-terminal rows whose
/// retention is due. Every phase is bounded and keyset-paginated so one permanently failing
/// record never blocks the rest (ADR-0024 "Cleanup").
/// </summary>
public sealed class MarketplaceCleanupService
{
    private readonly MarketplaceAuthorizationWorkflow _workflow;
    private readonly ILogger<MarketplaceCleanupService> _logger;

    public MarketplaceCleanupService(MarketplaceAuthorizationWorkflow workflow, ILogger<MarketplaceCleanupService> logger)
    { _workflow = workflow; _logger = logger; }

    public async Task RunOnceAsync(int batchSize, CancellationToken cancellationToken)
    {
        // G-02: credential decryptability is validated BEFORE other housekeeping — a startup run
        // must never let provider execution proceed against material it cannot prove is usable.
        var invalidated = await _workflow.ValidateConnectedCredentialsAsync(batchSize, cancellationToken);
        var expired = await _workflow.ExpireDueSessionsAsync(batchSize, cancellationToken);
        var resolved = await _workflow.ResolveAbandonedAsync(batchSize, cancellationToken);
        var swept = await _workflow.CleanupTerminalAsync(batchSize, cancellationToken);
        _logger.LogInformation(
            "Marketplace cleanup sweep: {Invalidated} credentials failed validation, {Expired} sessions expired, {Resolved} abandoned records resolved, {Swept} terminal records swept.",
            invalidated, expired, resolved, swept);
    }
}

[DisallowConcurrentExecution]
public sealed class MarketplaceCleanupJob : IJob
{
    private readonly MarketplaceCleanupService _service;
    private readonly MarketplaceCleanupJobOptions _options;

    public MarketplaceCleanupJob(MarketplaceCleanupService service, IOptions<MarketplaceCleanupJobOptions> options)
    { _service = service; _options = options.Value; }

    public Task Execute(IJobExecutionContext context) => _service.RunOnceAsync(_options.BatchSize, context.CancellationToken);
}

public static class MarketplaceSchedulingServiceCollectionExtensions
{
    /// <summary>Only called when the local marketplace store/registry are actually registered
    /// (Development + configured CredentialStoreRoot) — Production has nothing to reconcile and
    /// no fake/local store is ever wired there (G-06).</summary>
    public static IServiceCollection AddVerceMarketplaceHousekeeping(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new MarketplaceCleanupJobOptions();
        configuration.GetSection(MarketplaceCleanupJobOptions.SectionName).Bind(options);
        if (options.BatchSize < 1 || options.BatchSize > 1000)
            throw new InvalidOperationException($"MARKETPLACE_CLEANUP_BATCH_SIZE_INVALID: must be between 1 and 1000, was {options.BatchSize}.");
        services.Configure<MarketplaceCleanupJobOptions>(configuration.GetSection(MarketplaceCleanupJobOptions.SectionName));
        services.AddScoped<MarketplaceCleanupService>();

        services.AddHostedService(sp => new MarketplaceStartupRecoveryScopedHostedService(sp));

        if (!options.SchedulingEnabled) return services;

        services.AddQuartz(q =>
        {
            q.AddJob<MarketplaceCleanupJob>(j => j.WithIdentity(MarketplaceCleanupJobKeys.Job).StoreDurably());
            q.AddTrigger(t => t
                .ForJob(MarketplaceCleanupJobKeys.Job)
                .WithIdentity("MarketplaceCleanupTrigger", "marketplaces")
                .WithSimpleSchedule(s => s.WithIntervalInSeconds(options.IntervalSeconds).RepeatForever())
                .StartNow());
        });

        return services;
    }
}

/// <summary>
/// <see cref="MarketplaceCleanupService"/> is Scoped (it depends on the Scoped
/// <see cref="MarketplaceAuthorizationWorkflow"/> / <c>IUnitOfWork</c> / <c>DbContext</c> chain),
/// but <c>IHostedService</c> instances are resolved once from the root (Singleton-lifetime)
/// container. This wrapper creates its own scope per call, exactly like Quartz's own job
/// activator does for <see cref="MarketplaceCleanupJob"/>.
/// </summary>
internal sealed class MarketplaceStartupRecoveryScopedHostedService : IHostedService
{
    private readonly IServiceProvider _root;
    public MarketplaceStartupRecoveryScopedHostedService(IServiceProvider root) => _root = root;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _root.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<MarketplaceCleanupService>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<MarketplaceCleanupJobOptions>>().Value;
        await service.RunOnceAsync(options.BatchSize, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
