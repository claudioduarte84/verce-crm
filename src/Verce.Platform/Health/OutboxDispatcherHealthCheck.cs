using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Verce.Platform.Persistence;
using Verce.SharedKernel.Time;

namespace Verce.Platform.Health;

/// <summary>
/// Readiness contributor for the outbox dispatcher (ADR-0012 §25, §25.1). Distinguishes a
/// business failure (one message stuck at FAILED/ACTIVE — Degraded, HTTP 200) from an
/// infrastructure failure (the dispatcher not draining ELIGIBLE work — Unhealthy, HTTP 503).
///
/// Eligibility is the SAME predicate as the claim query:
///   status = PENDING AND available_at &lt;= now() AND attempt_count &lt; max_attempts
/// A message serving a backoff or scheduled for the future must NEVER count toward a stall —
/// that was the H-RG2-002 defect this check exists to not repeat.
/// </summary>
public sealed class OutboxDispatcherHealthCheck : IHealthCheck
{
    private readonly VerceDbContext _context;
    private readonly IClock _clock;
    private readonly TimeSpan _stuckThreshold;

    public OutboxDispatcherHealthCheck(VerceDbContext context, IClock clock, TimeSpan? stuckThreshold = null)
    {
        _context = context;
        _clock = clock;
        _stuckThreshold = stuckThreshold ?? TimeSpan.FromMinutes(30);
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            const string stalenessSql = """
                SELECT EXTRACT(EPOCH FROM (MAX(@now - available_at)))
                FROM platform.outbox_message
                WHERE status = 'Pending' AND available_at <= @now AND attempt_count < max_attempts;
                """;
            await using var staleCmd = new NpgsqlCommand(stalenessSql, connection);
            staleCmd.Parameters.AddWithValue("now", now);
            var staleSecondsObj = await staleCmd.ExecuteScalarAsync(cancellationToken);
            var oldestEligibleWaitSeconds = staleSecondsObj is null or DBNull ? 0 : Convert.ToDouble(staleSecondsObj);

            const string activeFailuresSql = """
                SELECT count(*) FROM platform.outbox_message WHERE status = 'Failed' AND failure_disposition = 'Active';
                """;
            await using var failuresCmd = new NpgsqlCommand(activeFailuresSql, connection);
            var activeFailures = Convert.ToInt64(await failuresCmd.ExecuteScalarAsync(cancellationToken));

            if (oldestEligibleWaitSeconds > _stuckThreshold.TotalSeconds)
            {
                return HealthCheckResult.Unhealthy(
                    "Outbox dispatcher stalled: an eligible message has waited " +
                    $"{oldestEligibleWaitSeconds:F0}s past its available_at, exceeding the {_stuckThreshold.TotalMinutes:F0}min threshold.");
            }

            if (activeFailures > 0)
            {
                return HealthCheckResult.Degraded(
                    $"{activeFailures} outbox message(s) FAILED with disposition ACTIVE — needs human review, not systemic.");
            }

            return HealthCheckResult.Healthy();
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }
}
