using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Verce.Platform.Persistence;
using Verce.SharedKernel.Time;

namespace Verce.Platform.Outbox;

/// <summary>
/// Claims, completes, fails, heartbeats, reclaims, requeues and dismisses outbox messages using
/// raw parameterized SQL — the only way to get PostgreSQL's <c>FOR UPDATE SKIP LOCKED</c> and
/// the lease-fencing predicate exactly right (ADR-0012 §14-21). EF's ordinary optimistic
/// concurrency token is not used here; the fencing token IS the concurrency mechanism for this
/// table (ADR-0011 §2.8 exemption).
/// </summary>
public sealed class OutboxProcessor
{
    private readonly VerceDbContext _context;
    private readonly IClock _clock;

    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(5);

    public OutboxProcessor(VerceDbContext context, IClock clock)
    {
        _context = context;
        _clock = clock;
    }

    /// <summary>
    /// Claims up to <paramref name="batchSize"/> ELIGIBLE messages
    /// (status=PENDING, available_at&lt;=now, attempt_count&lt;max_attempts), atomically
    /// assigning each a fresh processing_token and inserting its attempt-history row
    /// (ADR-0012 §14, §19.3). Uses FOR UPDATE SKIP LOCKED so concurrent dispatcher workers never
    /// claim the same row and never block on each other.
    /// </summary>
    public async Task<IReadOnlyList<ClaimedMessage>> ClaimBatchAsync(
        int batchSize, string workerId, TimeSpan? leaseDuration = null, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var lease = leaseDuration ?? DefaultLeaseDuration;
        var claimed = new List<ClaimedMessage>();

        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            // Each claimed row gets its OWN fresh processing_token, so fencing is genuinely
            // per-message rather than shared across a batch. A single UPDATE...LIMIT n statement
            // cannot vary a parameter per row, so rows are claimed one at a time, each under
            // FOR UPDATE SKIP LOCKED, up to batchSize or until nothing eligible remains.
            for (var i = 0; i < batchSize; i++)
            {
                var token = Guid.CreateVersion7();
                const string claimOneSql = """
                    WITH eligible AS (
                        SELECT id
                        FROM platform.outbox_message
                        WHERE status = 'Pending'
                          AND available_at <= @now
                          AND attempt_count < max_attempts
                        ORDER BY available_at, created_at
                        FOR UPDATE SKIP LOCKED
                        LIMIT 1
                    )
                    UPDATE platform.outbox_message m
                    SET status = 'Processing',
                        processing_token = @token,
                        processing_started_at = @now,
                        lease_until = @lease_until,
                        worker_id = @worker_id,
                        attempt_count = m.attempt_count + 1
                    FROM eligible
                    WHERE m.id = eligible.id
                    RETURNING m.id, m.execution_generation, m.attempt_count, m.max_attempts, m.event_type, m.payload_json, m.idempotency_key;
                    """;

                await using var cmd = new NpgsqlCommand(claimOneSql, connection, transaction);
                cmd.Parameters.AddWithValue("now", now);
                cmd.Parameters.AddWithValue("token", token);
                cmd.Parameters.AddWithValue("worker_id", workerId);
                cmd.Parameters.AddWithValue("lease_until", now + lease);

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    break; // nothing left eligible
                }

                var messageId = reader.GetGuid(0);
                var generation = reader.GetInt32(1);
                var attemptNumber = reader.GetInt32(2);
                var maxAttempts = reader.GetInt32(3);
                var eventType = reader.GetString(4);
                var payload = reader.GetString(5);
                var idempotencyKey = reader.IsDBNull(6) ? null : reader.GetString(6);

                await reader.CloseAsync();

                // Insert the attempt-history row for this generation/attempt — the fix for
                // B-RG3-001: identity is (message, generation, attempt), so a requeued round
                // never collides with a previous one.
                const string insertAttemptSql = """
                    INSERT INTO platform.outbox_message_attempt
                        (id, outbox_message_id, execution_generation, attempt_number, processing_token, worker_id, started_at)
                    VALUES (@id, @message_id, @generation, @attempt_number, @token, @worker_id, @now);
                    """;
                await using var insertCmd = new NpgsqlCommand(insertAttemptSql, connection, transaction);
                insertCmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
                insertCmd.Parameters.AddWithValue("message_id", messageId);
                insertCmd.Parameters.AddWithValue("generation", generation);
                insertCmd.Parameters.AddWithValue("attempt_number", attemptNumber);
                insertCmd.Parameters.AddWithValue("token", token);
                insertCmd.Parameters.AddWithValue("worker_id", workerId);
                insertCmd.Parameters.AddWithValue("now", now);
                await insertCmd.ExecuteNonQueryAsync(cancellationToken);

                claimed.Add(new ClaimedMessage(messageId, token, generation, attemptNumber, maxAttempts, eventType, payload, idempotencyKey));
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }

        return claimed;
    }

    /// <summary>Fenced completion (ADR-0012 §15). 0 rows affected means the caller's lease
    /// expired and another worker owns the message — the caller must log and abandon, never
    /// retry the write.</summary>
    public async Task<bool> CompleteAsync(Guid messageId, Guid processingToken, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var rows = await ExecuteFencedAsync("""
            UPDATE platform.outbox_message
            SET status = 'Processed', processed_at = @now, lease_until = NULL, processing_token = NULL
            WHERE id = @id AND status = 'Processing' AND processing_token = @token;
            """, messageId, processingToken, ("now", now), cancellationToken);

        if (rows > 0) await FinishAttemptAsync(messageId, processingToken, now, "Succeeded", null, null, cancellationToken);
        return rows > 0;
    }

    /// <summary>Fenced retryable-failure transition (ADR-0012 §16). Branches on the SAME budget
    /// rule as a lease-expiry crash: attempt_count &gt;= max_attempts always yields FAILED, never
    /// a further PENDING attempt — this is exactly the B-RG3-001-adjacent invariant the re-gate
    /// required (final attempt is terminal regardless of how it ends).</summary>
    public async Task<bool> FailRetryableAsync(Guid messageId, Guid processingToken, string error, TimeSpan backoff, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var rows = await ExecuteFencedAsync("""
            UPDATE platform.outbox_message
            SET status = CASE WHEN attempt_count >= max_attempts THEN 'Failed' ELSE 'Pending' END,
                failure_disposition = CASE WHEN attempt_count >= max_attempts THEN 'Active' END,
                failed_at = CASE WHEN attempt_count >= max_attempts THEN @now END,
                failure_reason = CASE WHEN attempt_count >= max_attempts THEN 'RETRY_BUDGET_EXHAUSTED' END,
                available_at = CASE WHEN attempt_count >= max_attempts THEN NULL ELSE @next_available END,
                last_error = @error, last_error_at = @now,
                lease_until = NULL, processing_token = NULL
            WHERE id = @id AND status = 'Processing' AND processing_token = @token;
            """, messageId, processingToken, [("now", now), ("next_available", now + backoff), ("error", error)], cancellationToken);

        if (rows > 0) await FinishAttemptAsync(messageId, processingToken, now, "RetryableFailure", "Retryable", error, cancellationToken);
        return rows > 0;
    }

    /// <summary>Fenced non-retryable failure — immediate FAILED regardless of remaining budget.</summary>
    public async Task<bool> FailNonRetryableAsync(Guid messageId, Guid processingToken, string errorType, string error, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var rows = await ExecuteFencedAsync("""
            UPDATE platform.outbox_message
            SET status = 'Failed', failure_disposition = 'Active', failed_at = @now,
                failure_reason = 'NON_RETRYABLE', available_at = NULL,
                last_error = @error, last_error_at = @now,
                lease_until = NULL, processing_token = NULL
            WHERE id = @id AND status = 'Processing' AND processing_token = @token;
            """, messageId, processingToken, [("now", now), ("error", error)], cancellationToken);

        if (rows > 0) await FinishAttemptAsync(messageId, processingToken, now, "NonRetryableFailure", errorType, error, cancellationToken);
        return rows > 0;
    }

    /// <summary>Fenced heartbeat: resets the lease to now + the CONFIGURED lease duration —
    /// never a caller-supplied arbitrary window (ADR-0012 §17).</summary>
    public async Task<bool> HeartbeatAsync(Guid messageId, Guid processingToken, TimeSpan? leaseDuration = null, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var lease = leaseDuration ?? DefaultLeaseDuration;
        return await ExecuteFencedAsync("""
            UPDATE platform.outbox_message
            SET lease_until = @lease_until
            WHERE id = @id AND status = 'Processing' AND processing_token = @token;
            """, messageId, processingToken, [("lease_until", now + lease)], cancellationToken) > 0;
    }

    /// <summary>Sweeps expired leases (ADR-0012 §16): FAILED if the budget is exhausted,
    /// otherwise back to PENDING — with NO attempt refund (a crash consumes the attempt it was
    /// on, exactly like an explicit retryable failure would).</summary>
    public async Task<int> ReclaimExpiredLeasesAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var connection = _context.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                // The attempt was consumed even though the worker died. Finalize its append-only
                // history before clearing the parent's fencing token, otherwise a crash leaves an
                // indeterminate attempt row and hides the cause of the redelivery.
                const string finishAttemptsSql = """
                    UPDATE platform.outbox_message_attempt a
                    SET finished_at = @now,
                        outcome = 'LeaseExpired',
                        error_type = 'LeaseExpired',
                        error_message = 'Worker lease expired before completion'
                    FROM platform.outbox_message m
                    WHERE a.outbox_message_id = m.id
                      AND a.processing_token = m.processing_token
                      AND a.finished_at IS NULL
                      AND m.status = 'Processing'
                      AND m.lease_until < @now;
                    """;
                await using (var finishAttempts = new NpgsqlCommand(finishAttemptsSql, (NpgsqlConnection)connection, transaction))
                {
                    finishAttempts.Parameters.AddWithValue("now", now);
                    await finishAttempts.ExecuteNonQueryAsync(cancellationToken);
                }

                const string sql = """
                UPDATE platform.outbox_message
                SET status = CASE WHEN attempt_count >= max_attempts THEN 'Failed' ELSE 'Pending' END,
                    failure_disposition = CASE WHEN attempt_count >= max_attempts THEN 'Active' END,
                    failed_at = CASE WHEN attempt_count >= max_attempts THEN @now END,
                    failure_reason = CASE WHEN attempt_count >= max_attempts THEN 'LEASE_EXPIRED_ON_FINAL_ATTEMPT' END,
                    available_at = CASE WHEN attempt_count >= max_attempts THEN NULL ELSE @now END,
                    lease_until = NULL,
                    processing_token = NULL
                WHERE status = 'Processing' AND lease_until < @now;
                """;
                await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)connection, transaction);
                cmd.Parameters.AddWithValue("now", now);
                var reclaimed = await cmd.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return reclaimed;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    /// <summary>Fenced requeue primitive (ADR-0012 §21). Opens a NEW execution generation so the
    /// next claim's attempt-history insert can never collide with the previous round's rows —
    /// this is the direct fix for B-RG3-001. Preserves id, idempotency_key, last_error and every
    /// attempt row; resets attempt_count to 0 for a fresh per-round budget.
    /// <c>internal</c> (H-OUTBOX-002): this primitive has no actor, no reason and writes no audit
    /// row — <see cref="OutboxAdministrationService.RequeueAsync"/> is the only supported entry
    /// point from outside this assembly. Visible to Verce.IntegrationTests only to keep the
    /// low-level fenced-SQL tests that exercise this mechanism directly.</summary>
    internal async Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var connection = _context.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            const string sql = """
                UPDATE platform.outbox_message
                SET status = 'Pending',
                    execution_generation = execution_generation + 1,
                    attempt_count = 0,
                    failure_disposition = NULL,
                    failed_at = NULL,
                    failure_reason = NULL,
                    available_at = @now,
                    processing_token = NULL,
                    lease_until = NULL
                WHERE id = @id AND status = 'Failed';
                """;
            await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)connection);
            cmd.Parameters.AddWithValue("id", messageId);
            cmd.Parameters.AddWithValue("now", now);
            return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    /// <summary>Fenced dismissal primitive (ADR-0012 §18). Never deletes anything — records a
    /// decision alongside the facts. A dismissed message stays FAILED and is therefore never
    /// claimable (the claim query only looks at PENDING).
    /// <c>internal</c> (H-OUTBOX-003): same reasoning as <see cref="RequeueAsync"/> — this
    /// primitive accepts a caller-supplied actor with no Owner-authorization boundary and writes
    /// no <c>audit_log</c> row. S1 ships no supported entry point for it from outside this
    /// assembly (there is no dismiss HTTP endpoint); internalizing removes the public bypass
    /// without building one. Visible to Verce.IntegrationTests only, for the low-level
    /// fenced-SQL tests that exercise this mechanism directly.</summary>
    internal async Task<bool> DismissAsync(Guid messageId, Guid dismissedBy, string reason, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var connection = _context.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            const string sql = """
                UPDATE platform.outbox_message
                SET failure_disposition = 'Dismissed', dismissed_at = @now, dismissed_by = @by, dismissal_reason = @reason
                WHERE id = @id AND status = 'Failed' AND failure_disposition = 'Active';
                """;
            await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)connection);
            cmd.Parameters.AddWithValue("id", messageId);
            cmd.Parameters.AddWithValue("now", now);
            cmd.Parameters.AddWithValue("by", dismissedBy);
            cmd.Parameters.AddWithValue("reason", reason);
            return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    private async Task<int> ExecuteFencedAsync(string sql, Guid messageId, Guid processingToken,
        (string name, object value) extraParam, CancellationToken cancellationToken) =>
        await ExecuteFencedAsync(sql, messageId, processingToken, [extraParam], cancellationToken);

    private async Task<int> ExecuteFencedAsync(string sql, Guid messageId, Guid processingToken,
        IEnumerable<(string name, object value)> extraParams, CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)connection);
            cmd.Parameters.AddWithValue("id", messageId);
            cmd.Parameters.AddWithValue("token", processingToken);
            foreach (var (name, value) in extraParams)
                cmd.Parameters.AddWithValue(name, value);
            return await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    private async Task FinishAttemptAsync(Guid messageId, Guid processingToken, DateTimeOffset finishedAt,
        string outcome, string? errorType, string? errorMessage, CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            const string sql = """
                UPDATE platform.outbox_message_attempt
                SET finished_at = @finished_at, outcome = @outcome, error_type = @error_type, error_message = @error_message
                WHERE outbox_message_id = @message_id AND processing_token = @token;
                """;
            await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)connection);
            cmd.Parameters.AddWithValue("finished_at", finishedAt);
            cmd.Parameters.AddWithValue("outcome", outcome);
            cmd.Parameters.AddWithValue("error_type", (object?)errorType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("error_message", (object?)errorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("message_id", messageId);
            cmd.Parameters.AddWithValue("token", processingToken);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }
}

public sealed record ClaimedMessage(
    Guid MessageId, Guid ProcessingToken, int ExecutionGeneration, int AttemptNumber, int MaxAttempts,
    string EventType, string PayloadJson, string? IdempotencyKey);
