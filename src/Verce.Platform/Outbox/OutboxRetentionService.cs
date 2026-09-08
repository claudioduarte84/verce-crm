using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Verce.Platform.Persistence;
using Verce.SharedKernel.Time;

namespace Verce.Platform.Outbox;

/// <summary>ADR-0012 §23 retention. Attempts and their terminal parent are purged together in
/// one transaction; unresolved failures, pending work and processing work are never eligible.</summary>
public sealed class OutboxRetentionService
{
    private readonly VerceDbContext _context;
    private readonly IClock _clock;

    public OutboxRetentionService(VerceDbContext context, IClock clock)
    {
        _context = context;
        _clock = clock;
    }

    public async Task<int> PurgeEligibleAsync(CancellationToken cancellationToken = default)
    {
        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var now = _clock.UtcNow;
            const string predicate = """
                (status = 'Processed' AND processed_at < @processed_cutoff)
                OR (status = 'Failed' AND failure_disposition = 'Dismissed' AND dismissed_at < @dismissed_cutoff)
                """;

            // H-RETENTION-001: lock and freeze the eligible id set with ONE predicate evaluation
            // before deleting anything. FOR UPDATE blocks a concurrent requeue on the same rows
            // until one side commits — whichever transaction commits first determines the outcome,
            // and the loser's predicate (re-checked against the row's post-lock committed state)
            // either excludes the row here or finds it already gone on the requeue side. Deleting
            // attempts and the parent from this SAME frozen id set (never re-running the predicate
            // a second time) is what makes the two deletes atomic with respect to each other.
            const string selectEligibleSql = $"""
                SELECT id FROM platform.outbox_message WHERE {predicate} FOR UPDATE;
                """;

            var eligibleIds = new List<Guid>();
            await using (var select = new NpgsqlCommand(selectEligibleSql, connection, transaction))
            {
                select.Parameters.AddWithValue("processed_cutoff", now.AddDays(-90));
                select.Parameters.AddWithValue("dismissed_cutoff", now.AddDays(-365));
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) eligibleIds.Add(reader.GetGuid(0));
            }

            if (eligibleIds.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return 0;
            }

            const string attemptsSql = """
                DELETE FROM platform.outbox_message_attempt WHERE outbox_message_id = ANY(@ids);
                """;
            const string messagesSql = """
                DELETE FROM platform.outbox_message WHERE id = ANY(@ids);
                """;

            await using (var attempts = new NpgsqlCommand(attemptsSql, connection, transaction))
            {
                attempts.Parameters.AddWithValue("ids", eligibleIds.ToArray());
                await attempts.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var messages = new NpgsqlCommand(messagesSql, connection, transaction);
            messages.Parameters.AddWithValue("ids", eligibleIds.ToArray());
            var deleted = await messages.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return deleted;
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
    }
}
