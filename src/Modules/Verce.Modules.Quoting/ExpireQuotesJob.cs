using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartz;

namespace Verce.Modules.Quoting;

/// <summary>Configurable cadence (CLAUDE.md rule 10: never hard-coded) — bound from
/// <c>Quoting:Expiration</c>. Mirrors <c>OutboxSchedulingOptions</c>'s shape exactly.</summary>
public sealed class ExpireQuotesJobOptions
{
    public const string SectionName = "Quoting:Expiration";

    /// <summary>A test host that manipulates quote status directly sets this <c>false</c> so no
    /// live job races its assertions — mirrors <c>Outbox:SchedulingEnabled</c>'s default-off
    /// posture in <c>VerceWebApplicationFactory</c>. Production leaves it <c>true</c>.</summary>
    public bool SchedulingEnabled { get; set; } = true;

    /// <summary>STATE-MACHINES §1.6: "runs hourly." Configurable so a test can run it every few
    /// seconds without touching the production default.</summary>
    public int IntervalSeconds { get; set; } = 3600;

    /// <summary>F-04: how many eligible revisions ONE scheduled sweep processes. Bounded by
    /// <see cref="ExpireQuotesService.MaxBatchSize"/> — validated once, at startup, in
    /// <see cref="QuotingSchedulingServiceCollectionExtensions.AddVerceQuotingScheduling"/>, never
    /// silently clamped.</summary>
    public int BatchSize { get; set; } = ExpireQuotesService.DefaultBatchSize;
}

public static class ExpireQuotesJobKeys
{
    public static readonly JobKey Job = new("ExpireQuotesJob", "quoting");
}

/// <summary>Thin Quartz wrapper delegating to the already-tested <see cref="ExpireQuotesService"/>
/// — mirrors <c>OutboxRetentionJob</c>'s shape exactly (no business logic inline in the job).</summary>
[DisallowConcurrentExecution]
public sealed class ExpireQuotesJob : IJob
{
    private readonly ExpireQuotesService _service;
    private readonly ExpireQuotesJobOptions _options;

    public ExpireQuotesJob(ExpireQuotesService service, IOptions<ExpireQuotesJobOptions> options)
    {
        _service = service;
        _options = options.Value;
    }

    public Task Execute(IJobExecutionContext context) => _service.ExpireEligibleAsync(context.CancellationToken, _options.BatchSize);
}

/// <summary>
/// Registers <see cref="ExpireQuotesJob"/> against the SAME Quartz scheduler the platform's
/// outbox jobs already configure (<c>AddVerceOutboxScheduling</c>) — Quartz.Extensions.Hosting
/// supports calling <c>AddQuartz</c> more than once; each call adds to the one shared
/// <c>SchedulerBuilder</c> rather than creating a second scheduler. Deliberately does NOT call
/// <c>UsePersistentStore</c>/<c>SchedulerId</c> again — those are scheduler-wide and already set.
/// If outbox scheduling happens to be disabled in the same process while this one is left
/// enabled (a test-only combination; production always enables both), Quartz falls back to its
/// default in-memory store for this job alone — acceptable for a non-critical, idempotent,
/// re-runnable expiration sweep, never acceptable for the outbox itself.
/// </summary>
public static class QuotingSchedulingServiceCollectionExtensions
{
    public static IServiceCollection AddVerceQuotingScheduling(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new ExpireQuotesJobOptions();
        configuration.GetSection(ExpireQuotesJobOptions.SectionName).Bind(options);

        // F-04: fail fast at startup on an invalid configured batch size — never silently clamp
        // it, and never let a misconfiguration reach Take(int.MaxValue) at runtime.
        if (options.BatchSize < 1 || options.BatchSize > ExpireQuotesService.MaxBatchSize)
            throw new InvalidOperationException(
                $"EXPIRE_QUOTES_BATCH_SIZE_INVALID: '{ExpireQuotesJobOptions.SectionName}:BatchSize' must be between 1 and " +
                $"{ExpireQuotesService.MaxBatchSize}, but was {options.BatchSize}.");

        services.Configure<ExpireQuotesJobOptions>(configuration.GetSection(ExpireQuotesJobOptions.SectionName));

        if (!options.SchedulingEnabled) return services;

        services.AddQuartz(q =>
        {
            q.AddJob<ExpireQuotesJob>(j => j.WithIdentity(ExpireQuotesJobKeys.Job).StoreDurably());
            q.AddTrigger(t => t
                .ForJob(ExpireQuotesJobKeys.Job)
                .WithIdentity("ExpireQuotesTrigger", "quoting")
                .WithSimpleSchedule(s => s.WithIntervalInSeconds(options.IntervalSeconds).RepeatForever())
                .StartNow());
        });

        return services;
    }
}
