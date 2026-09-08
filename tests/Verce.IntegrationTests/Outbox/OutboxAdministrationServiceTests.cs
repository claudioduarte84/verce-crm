using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Verce.Platform.Outbox;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Outbox;

/// <summary>H-OUTBOX-002: the Owner-only requeue primitive requires an actor and a non-blank
/// reason, writes an audit row in the SAME transaction as the mutation, and distinguishes an
/// ordinary requeue from a DISMISSED revival (ADR-0012 §21).</summary>
[Collection(PostgresCollection.Name)]
public class OutboxAdministrationServiceTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));

    public OutboxAdministrationServiceTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE platform.outbox_message_attempt, platform.outbox_message, platform.audit_log
            RESTART IDENTITY CASCADE;
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private OutboxMessage Seed(string key) =>
        OutboxMessage.Enqueue("ProbeIntegrationEvent", "{}", key, Guid.CreateVersion7(), null, null, "Probe", null, _clock.UtcNow, maxAttempts: 5);

    private async Task<Guid> SeedFailedMessageAsync(string key, string disposition)
    {
        await using var seed = _fixture.CreateContext();
        var message = Seed(key);
        seed.OutboxMessages.Add(message);
        await seed.SaveChangesAsync();

        var attemptId = Guid.CreateVersion7();
        var processingToken = Guid.CreateVersion7();
        await seed.Database.ExecuteSqlRawAsync("""
            UPDATE platform.outbox_message
            SET status = 'Failed', failure_disposition = {0}, failed_at = {1},
                dismissed_at = CASE WHEN {0} = 'Dismissed' THEN {1} END, available_at = NULL
            WHERE id = {2};
            INSERT INTO platform.outbox_message_attempt (id, outbox_message_id, execution_generation, attempt_number, processing_token, started_at, finished_at, outcome)
            VALUES ({3}, {2}, 1, 1, {4}, {1}, {1}, 'RetryableFailure');
            """, disposition, _clock.UtcNow, message.Id, attemptId, processingToken);

        return message.Id;
    }

    [Fact]
    public async Task An_ACTIVE_failure_requeues_and_is_audited_as_an_ordinary_requeue()
    {
        var messageId = await SeedFailedMessageAsync("admin:active", "Active");
        var actor = Guid.CreateVersion7();

        await using var context = _fixture.CreateContext();
        var service = new OutboxAdministrationService(context, _clock);
        var outcome = await service.RequeueAsync(messageId, actor, "operator asked for a manual retry");

        outcome.Should().Be(OutboxRequeueOutcome.Requeued);

        await using var verify = _fixture.CreateContext();
        var message = await verify.OutboxMessages.SingleAsync(m => m.Id == messageId);
        message.Status.Should().Be(OutboxStatus.Pending);
        message.ExecutionGeneration.Should().Be(2);
        message.AttemptCount.Should().Be(0);
        message.FailureDispositionValue.Should().BeNull();
        (await verify.OutboxMessageAttempts.CountAsync(a => a.OutboxMessageId == messageId)).Should().Be(1, "history is preserved, never deleted by a requeue");

        var audit = await verify.AuditLog.SingleAsync(a => a.EntityId == messageId);
        audit.Operation.Should().Be("OUTBOX_REQUEUE");
        audit.UserId.Should().Be(actor);
        audit.EntityTable.Should().Be("outbox_message");
        audit.NewValuesJson.Should().Contain("operator asked for a manual retry");
    }

    [Fact]
    public async Task A_DISMISSED_failure_requeues_and_is_audited_as_a_distinct_revival()
    {
        var messageId = await SeedFailedMessageAsync("admin:dismissed", "Dismissed");
        var actor = Guid.CreateVersion7();

        await using var context = _fixture.CreateContext();
        var service = new OutboxAdministrationService(context, _clock);
        var outcome = await service.RequeueAsync(messageId, actor, "new information changed the decision");

        outcome.Should().Be(OutboxRequeueOutcome.Requeued);

        await using var verify = _fixture.CreateContext();
        var message = await verify.OutboxMessages.SingleAsync(m => m.Id == messageId);
        message.Status.Should().Be(OutboxStatus.Pending);
        message.ExecutionGeneration.Should().Be(2);

        var audit = await verify.AuditLog.SingleAsync(a => a.EntityId == messageId);
        audit.Operation.Should().Be("OUTBOX_REVIVAL", "a revival from DISMISSED must be visibly distinct from an ordinary requeue");
    }

    [Fact]
    public async Task A_blank_reason_is_rejected_before_anything_is_mutated()
    {
        var messageId = await SeedFailedMessageAsync("admin:blank-reason", "Active");

        await using var context = _fixture.CreateContext();
        var service = new OutboxAdministrationService(context, _clock);
        var act = async () => await service.RequeueAsync(messageId, Guid.CreateVersion7(), "   ");

        await act.Should().ThrowAsync<ArgumentException>();

        await using var verify = _fixture.CreateContext();
        (await verify.OutboxMessages.SingleAsync(m => m.Id == messageId)).Status.Should().Be(OutboxStatus.Failed);
        (await verify.AuditLog.AnyAsync(a => a.EntityId == messageId)).Should().BeFalse();
    }

    [Fact]
    public async Task A_message_that_is_not_FAILED_is_reported_not_eligible_and_writes_no_audit_row()
    {
        await using var seed = _fixture.CreateContext();
        var message = Seed("admin:not-failed");
        seed.OutboxMessages.Add(message);
        await seed.SaveChangesAsync();

        await using var context = _fixture.CreateContext();
        var service = new OutboxAdministrationService(context, _clock);
        var outcome = await service.RequeueAsync(message.Id, Guid.CreateVersion7(), "irrelevant");

        outcome.Should().Be(OutboxRequeueOutcome.NotEligible);

        await using var verify = _fixture.CreateContext();
        (await verify.OutboxMessages.SingleAsync(m => m.Id == message.Id)).Status.Should().Be(OutboxStatus.Pending);
        (await verify.AuditLog.AnyAsync(a => a.EntityId == message.Id)).Should().BeFalse();
    }
}
