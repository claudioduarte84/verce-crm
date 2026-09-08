using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verce.Platform.Outbox;

/// <summary>One in-process handler for one persisted integration-event type. The explicit
/// timeout is validated against the lease at startup (ADR-0012 §17).</summary>
public interface IIntegrationEventConsumer
{
    string EventType { get; }
    TimeSpan Timeout { get; }
    Task HandleAsync(ClaimedMessage message, CancellationToken cancellationToken);
}

/// <summary>Signals a payload or schema fault which cannot succeed by retrying.</summary>
public sealed class NonRetryableOutboxException : Exception
{
    public NonRetryableOutboxException(string message) : base(message) { }
    public NonRetryableOutboxException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class OutboxConsumerRegistry
{
    private readonly IReadOnlyDictionary<string, IIntegrationEventConsumer> _consumers;

    public OutboxConsumerRegistry(IEnumerable<IIntegrationEventConsumer> consumers)
    {
        var materialized = consumers.ToList();
        var duplicate = materialized.GroupBy(c => c.EventType, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() != 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Exactly one outbox consumer must be registered for event type '{duplicate.Key}'.");

        var invalidTimeout = materialized.FirstOrDefault(c => c.Timeout <= TimeSpan.Zero || c.Timeout >= OutboxProcessor.DefaultLeaseDuration);
        if (invalidTimeout is not null)
            throw new InvalidOperationException(
                $"Outbox consumer '{invalidTimeout.EventType}' timeout must be positive and strictly below the {OutboxProcessor.DefaultLeaseDuration.TotalMinutes:0}-minute lease.");

        _consumers = materialized.ToDictionary(c => c.EventType, StringComparer.Ordinal);
    }

    public IIntegrationEventConsumer Resolve(string eventType) =>
        _consumers.TryGetValue(eventType, out var consumer)
            ? consumer
            : throw new NonRetryableOutboxException($"No registered outbox consumer for event type '{eventType}'.");
}

/// <summary>Centralized ADR-0012 §20 retry ladder. The processor owns fenced persistence;
/// this policy owns only the deterministic decision.</summary>
public sealed class OutboxRetryPolicy
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2),
    ];

    public RetryDisposition Decide(int attemptNumber, int maxAttempts) =>
        attemptNumber >= maxAttempts
            ? RetryDisposition.Terminal
            : new RetryDisposition(false, Delays[Math.Min(attemptNumber - 1, Delays.Length - 1)]);
}

public sealed record RetryDisposition(bool IsTerminal, TimeSpan? Delay)
{
    public static readonly RetryDisposition Terminal = new(true, null);
}

/// <summary>Real production dispatch pipeline: claim, resolve exactly one consumer, invoke it,
/// then apply the fenced success/failure transition. It deliberately has no business consumer;
/// modules register those in their own composition roots.</summary>
public sealed class OutboxDispatcher
{
    private readonly OutboxProcessor _processor;
    private readonly OutboxConsumerRegistry _registry;
    private readonly OutboxRetryPolicy _retryPolicy;

    public OutboxDispatcher(OutboxProcessor processor, IEnumerable<IIntegrationEventConsumer> consumers, OutboxRetryPolicy retryPolicy)
    {
        _processor = processor;
        _registry = new OutboxConsumerRegistry(consumers);
        _retryPolicy = retryPolicy;
    }

    public async Task<int> DispatchBatchAsync(int batchSize, string workerId, CancellationToken cancellationToken = default)
    {
        var claimed = await _processor.ClaimBatchAsync(batchSize, workerId, cancellationToken: cancellationToken);
        foreach (var message in claimed)
        {
            try
            {
                var consumer = _registry.Resolve(message.EventType);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(consumer.Timeout);
                await consumer.HandleAsync(message, timeout.Token);
                await _processor.CompleteAsync(message.MessageId, message.ProcessingToken, cancellationToken);
            }
            catch (NonRetryableOutboxException ex)
            {
                await _processor.FailNonRetryableAsync(message.MessageId, message.ProcessingToken, ex.GetType().Name, ex.Message, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var disposition = _retryPolicy.Decide(message.AttemptNumber, message.MaxAttempts);
                // FailRetryableAsync is the source of truth for the per-message terminal-budget
                // check. The policy supplies only the approved delay for a non-terminal round.
                await _processor.FailRetryableAsync(message.MessageId, message.ProcessingToken, ex.Message,
                    disposition.Delay ?? TimeSpan.Zero, cancellationToken);
            }
        }

        return claimed.Count;
    }
}

/// <summary>Runs during host startup so an invalid consumer timeout or duplicate event binding
/// prevents the process from accepting work. An empty consumer set is valid in S1.</summary>
public sealed class OutboxConsumerStartupValidator : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public OutboxConsumerStartupValidator(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        _ = new OutboxConsumerRegistry(scope.ServiceProvider.GetServices<IIntegrationEventConsumer>());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
