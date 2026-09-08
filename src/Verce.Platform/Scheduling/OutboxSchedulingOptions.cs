namespace Verce.Platform.Scheduling;

/// <summary>
/// Strongly-typed configuration for the three Quartz jobs ARCHITECTURE.md §6.1 requires
/// (`OutboxDispatcherJob`, `OutboxLeaseReclaimJob`, `OutboxRetentionJob`). Defaults are the
/// approved production cadences (every 15s / every 1min / daily) — a test host overrides these
/// via configuration, never by editing the job code (mission §16/§23/§74).
/// </summary>
public sealed class OutboxSchedulingOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Master switch. Production composition leaves this true by default (mission §15);
    /// a test host that needs to manipulate outbox rows without a live scheduler racing it sets
    /// this to false explicitly.</summary>
    public bool SchedulingEnabled { get; set; } = true;

    public int DispatchIntervalSeconds { get; set; } = 15;

    public int DispatchBatchSize { get; set; } = 20;

    public int LeaseReclaimIntervalSeconds { get; set; } = 60;

    /// <summary>Quartz cron expression. Default: daily at 03:00 server time — ARCHITECTURE.md
    /// §6.1 says "daily" without naming a specific hour.</summary>
    public string RetentionCronSchedule { get; set; } = "0 0 3 * * ?";
}
