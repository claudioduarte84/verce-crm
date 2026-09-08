using Microsoft.Extensions.Options;
using Quartz;
using Verce.Platform.Outbox;

namespace Verce.Platform.Scheduling;

/// <summary>
/// The three production Quartz jobs ARCHITECTURE.md §6.1 requires. Each is a thin orchestration
/// wrapper (mission §11) — no SQL or outbox business logic lives here, only a call into the
/// already-tested platform service. <c>[DisallowConcurrentExecution]</c> stops the SAME job from
/// overlapping its own previous run on ONE scheduler instance; Quartz's clustered persistent
/// store (`platform.qrtz_locks`) is what stops two scheduler INSTANCES from double-firing the
/// same trigger (ARCHITECTURE.md §6.1: "each takes an advisory lock on its job key").
/// </summary>
[DisallowConcurrentExecution]
public sealed class OutboxDispatcherJob : IJob
{
    private readonly OutboxDispatcher _dispatcher;
    private readonly IOptions<OutboxSchedulingOptions> _options;

    public OutboxDispatcherJob(OutboxDispatcher dispatcher, IOptions<OutboxSchedulingOptions> options)
    {
        _dispatcher = dispatcher;
        _options = options;
    }

    public Task Execute(IJobExecutionContext context) =>
        _dispatcher.DispatchBatchAsync(_options.Value.DispatchBatchSize, Environment.MachineName, context.CancellationToken);
}

[DisallowConcurrentExecution]
public sealed class OutboxLeaseReclaimJob : IJob
{
    private readonly OutboxProcessor _processor;

    public OutboxLeaseReclaimJob(OutboxProcessor processor) => _processor = processor;

    public Task Execute(IJobExecutionContext context) => _processor.ReclaimExpiredLeasesAsync(context.CancellationToken);
}

[DisallowConcurrentExecution]
public sealed class OutboxRetentionJob : IJob
{
    private readonly OutboxRetentionService _retentionService;

    public OutboxRetentionJob(OutboxRetentionService retentionService) => _retentionService = retentionService;

    public Task Execute(IJobExecutionContext context) => _retentionService.PurgeEligibleAsync(context.CancellationToken);
}

public static class OutboxJobKeys
{
    public static readonly JobKey Dispatcher = new("OutboxDispatcherJob", "outbox");
    public static readonly JobKey LeaseReclaim = new("OutboxLeaseReclaimJob", "outbox");
    public static readonly JobKey Retention = new("OutboxRetentionJob", "outbox");
}
