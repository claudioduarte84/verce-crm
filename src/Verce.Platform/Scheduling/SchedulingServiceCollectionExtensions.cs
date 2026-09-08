using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace Verce.Platform.Scheduling;

/// <summary>
/// B-OUTBOX-001 (S1 Implementation Gate): the outbox must actually run in production without a
/// test or operator calling the dispatcher manually. Wires Quartz.NET with a persistent
/// PostgreSQL job store (ARCHITECTURE.md §6.1) and the three approved jobs, at the approved
/// cadences, driven by <see cref="OutboxSchedulingOptions"/> — never a hand-rolled loop, timer,
/// or health-endpoint trigger (mission §9).
/// </summary>
public static class SchedulingServiceCollectionExtensions
{
    public static IServiceCollection AddVerceOutboxScheduling(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new OutboxSchedulingOptions();
        configuration.GetSection(OutboxSchedulingOptions.SectionName).Bind(options);
        services.Configure<OutboxSchedulingOptions>(configuration.GetSection(OutboxSchedulingOptions.SectionName));

        if (!options.SchedulingEnabled)
        {
            // A test host that needs to manipulate outbox rows deterministically, without a live
            // scheduler racing its assertions, sets Outbox:SchedulingEnabled=false explicitly.
            // Production composition never overrides this — the default above is true.
            return services;
        }

        var connectionString = configuration.GetConnectionString("Verce")
            ?? throw new InvalidOperationException("ConnectionStrings:Verce is not configured.");

        services.AddQuartz(q =>
        {
            // ARCHITECTURE.md §6.1: "two instances never overlap" requires each PROCESS to have
            // its own scheduler instance id under UseClustering() below — the Quartz default
            // ("NON_CLUSTERED") is the SAME literal string for every instance, which would make
            // two real replicas collide in qrtz_scheduler_state instead of coordinating through
            // it. "AUTO" asks Quartz to generate one unique id per process at startup.
            q.SchedulerId = "AUTO";

            q.UsePersistentStore(store =>
            {
                store.UseProperties = true;
                store.UsePostgres(pg =>
                {
                    pg.ConnectionString = connectionString;
                    pg.TablePrefix = "platform.qrtz_";
                });
                store.UseNewtonsoftJsonSerializer();
                store.UseClustering();
            });

            q.AddJob<OutboxDispatcherJob>(j => j.WithIdentity(OutboxJobKeys.Dispatcher).StoreDurably());
            q.AddTrigger(t => t
                .ForJob(OutboxJobKeys.Dispatcher)
                .WithIdentity("OutboxDispatcherTrigger", "outbox")
                .WithSimpleSchedule(s => s.WithIntervalInSeconds(options.DispatchIntervalSeconds).RepeatForever())
                .StartNow());

            q.AddJob<OutboxLeaseReclaimJob>(j => j.WithIdentity(OutboxJobKeys.LeaseReclaim).StoreDurably());
            q.AddTrigger(t => t
                .ForJob(OutboxJobKeys.LeaseReclaim)
                .WithIdentity("OutboxLeaseReclaimTrigger", "outbox")
                .WithSimpleSchedule(s => s.WithIntervalInSeconds(options.LeaseReclaimIntervalSeconds).RepeatForever())
                .StartNow());

            q.AddJob<OutboxRetentionJob>(j => j.WithIdentity(OutboxJobKeys.Retention).StoreDurably());
            q.AddTrigger(t => t
                .ForJob(OutboxJobKeys.Retention)
                .WithIdentity("OutboxRetentionTrigger", "outbox")
                .WithCronSchedule(options.RetentionCronSchedule));
        });

        services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

        return services;
    }
}
