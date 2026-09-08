using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Verce.Platform.Outbox;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Outbox;

/// <summary>
/// C-1..C-9g (ROADMAP S1 catalogue): claim eligibility/fencing, the single terminal-attempt
/// rule, and — most importantly — the B-RG3-001 fix: requeue opens a new execution generation
/// so the next claim's attempt-history insert can never collide with the previous round.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OutboxProcessorTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public OutboxProcessorTests(PostgresFixture fixture) => _fixture = fixture;

    // The collection shares one Postgres instance across every test class for speed. Without
    // resetting the outbox tables, a claim-eligible message left PENDING by an earlier test
    // would be claimable by this test's workers too, corrupting exact-count assertions like
    // "totalClaimed == 1".
    public async Task InitializeAsync()
    {
        await using var context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE platform.outbox_message_attempt, platform.outbox_message RESTART IDENTITY CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private OutboxMessage SeedMessage(int maxAttempts = 5, string? idempotencyKey = null) =>
        OutboxMessage.Enqueue(
            eventType: "TestEvent",
            payloadJson: "{}",
            idempotencyKey: idempotencyKey,
            correlationId: Guid.CreateVersion7(),
            requestId: null,
            actorUserId: null,
            aggregateType: "Test",
            aggregateId: null,
            now: DateTimeOffset.UtcNow,
            maxAttempts: maxAttempts);

    [Fact]
    public async Task C1_two_workers_claiming_concurrently_never_claim_the_same_message()
    {
        await using var seedContext = _fixture.CreateContext();
        var message = SeedMessage();
        seedContext.OutboxMessages.Add(message);
        await seedContext.SaveChangesAsync();

        await using var contextA = _fixture.CreateContext();
        await using var contextB = _fixture.CreateContext();
        var processorA = new OutboxProcessor(contextA, new SystemClock());
        var processorB = new OutboxProcessor(contextB, new SystemClock());

        var claimTaskA = processorA.ClaimBatchAsync(batchSize: 1, workerId: "worker-a");
        var claimTaskB = processorB.ClaimBatchAsync(batchSize: 1, workerId: "worker-b");
        var results = await Task.WhenAll(claimTaskA, claimTaskB);

        var totalClaimed = results.Sum(r => r.Count);
        totalClaimed.Should().Be(1, "there is only one eligible message; SKIP LOCKED must prevent a double-claim");
    }

    [Fact]
    public async Task C2_stale_lease_owner_cannot_complete_after_another_worker_reclaims()
    {
        await using var seedContext = _fixture.CreateContext();
        var message = SeedMessage();
        seedContext.OutboxMessages.Add(message);
        await seedContext.SaveChangesAsync();

        await using var contextA = _fixture.CreateContext();
        var processorA = new OutboxProcessor(contextA, new SystemClock());
        var claimedByA = (await processorA.ClaimBatchAsync(1, "worker-a", TimeSpan.FromMilliseconds(1))).Single();

        await Task.Delay(50); // let the lease expire

        // Simulate the reclaim sweep + a second worker claiming the now-PENDING message.
        await using var reclaimContext = _fixture.CreateContext();
        var reclaimProcessor = new OutboxProcessor(reclaimContext, new SystemClock());
        await reclaimProcessor.ReclaimExpiredLeasesAsync();

        await using var contextB = _fixture.CreateContext();
        var processorB = new OutboxProcessor(contextB, new SystemClock());
        var claimedByB = (await processorB.ClaimBatchAsync(1, "worker-b")).Single();

        claimedByB.ProcessingToken.Should().NotBe(claimedByA.ProcessingToken);

        // Worker A, unaware it lost the lease, tries to complete with its STALE token.
        await using var completeContextA = _fixture.CreateContext();
        var completeProcessorA = new OutboxProcessor(completeContextA, new SystemClock());
        var staleCompletionSucceeded = await completeProcessorA.CompleteAsync(claimedByA.MessageId, claimedByA.ProcessingToken);

        staleCompletionSucceeded.Should().BeFalse("A's token no longer matches — 0 rows affected, A must abandon rather than overwrite B");

        // B's completion must still work normally afterward.
        await using var completeContextB = _fixture.CreateContext();
        var completeProcessorB = new OutboxProcessor(completeContextB, new SystemClock());
        (await completeProcessorB.CompleteAsync(claimedByB.MessageId, claimedByB.ProcessingToken)).Should().BeTrue();
    }

    [Fact]
    public async Task C6a_retryable_failure_on_final_attempt_becomes_failed_not_pending()
    {
        await using var seedContext = _fixture.CreateContext();
        var message = SeedMessage(maxAttempts: 1); // the very first claim IS the final attempt
        seedContext.OutboxMessages.Add(message);
        await seedContext.SaveChangesAsync();

        await using var context = _fixture.CreateContext();
        var processor = new OutboxProcessor(context, new SystemClock());
        var claimed = (await processor.ClaimBatchAsync(1, "worker-x")).Single();

        await processor.FailRetryableAsync(claimed.MessageId, claimed.ProcessingToken, "boom", TimeSpan.FromMinutes(1));

        await using var verifyContext = _fixture.CreateContext();
        var reloaded = await verifyContext.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == claimed.MessageId);

        reloaded.Status.Should().Be(OutboxStatus.Failed, "attempt_count already equals max_attempts — this must be terminal, never PENDING again");
        reloaded.FailureDispositionValue.Should().Be(FailureDisposition.Active);
        reloaded.AvailableAt.Should().BeNull("a terminal message is not scheduled for anything");
    }

    [Fact]
    public async Task C6b_exhausted_message_cannot_be_claimed()
    {
        await using var seedContext = _fixture.CreateContext();
        var message = SeedMessage(maxAttempts: 1);
        seedContext.OutboxMessages.Add(message);
        await seedContext.SaveChangesAsync();

        await using var context1 = _fixture.CreateContext();
        var processor1 = new OutboxProcessor(context1, new SystemClock());
        var claimed = (await processor1.ClaimBatchAsync(1, "worker-1")).Single();
        await processor1.FailRetryableAsync(claimed.MessageId, claimed.ProcessingToken, "boom", TimeSpan.FromMinutes(1));

        // Now FAILED with attempt_count == max_attempts. A later claim attempt must find nothing.
        await using var context2 = _fixture.CreateContext();
        var processor2 = new OutboxProcessor(context2, new SystemClock());
        var secondClaim = await processor2.ClaimBatchAsync(1, "worker-2");

        secondClaim.Should().BeEmpty();
    }

    [Fact]
    public async Task C9c_requeue_starts_a_new_execution_generation_and_resets_attempt_count()
    {
        await using var seedContext = _fixture.CreateContext();
        var message = SeedMessage(maxAttempts: 1);
        seedContext.OutboxMessages.Add(message);
        await seedContext.SaveChangesAsync();

        await using var context1 = _fixture.CreateContext();
        var processor1 = new OutboxProcessor(context1, new SystemClock());
        var claimed = (await processor1.ClaimBatchAsync(1, "worker-1")).Single();
        await processor1.FailRetryableAsync(claimed.MessageId, claimed.ProcessingToken, "boom", TimeSpan.FromMinutes(1));

        await using var requeueContext = _fixture.CreateContext();
        var requeueProcessor = new OutboxProcessor(requeueContext, new SystemClock());
        (await requeueProcessor.RequeueAsync(message.Id)).Should().BeTrue();

        await using var verifyContext = _fixture.CreateContext();
        var reloaded = await verifyContext.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == message.Id);

        reloaded.Status.Should().Be(OutboxStatus.Pending);
        reloaded.ExecutionGeneration.Should().Be(2);
        reloaded.AttemptCount.Should().Be(0);
        reloaded.FailureDispositionValue.Should().BeNull();

        var generation1History = await verifyContext.OutboxMessageAttempts.AsNoTracking()
            .Where(a => a.OutboxMessageId == message.Id && a.ExecutionGeneration == 1)
            .ToListAsync();
        generation1History.Should().HaveCount(1, "generation 1's attempt history must remain, untouched by the requeue");
    }

    [Fact]
    public async Task C9d_claim_after_requeue_does_not_collide_with_previous_generation_history()
    {
        // THE exact scenario the re-gate blocker (B-RG3-001) was about: generation 1 has
        // attempt 1 in history; after requeue, generation 2's first claim inserts attempt 1
        // again — WITHOUT violating UNIQUE(outbox_message_id, execution_generation, attempt_number).
        await using var seedContext = _fixture.CreateContext();
        var message = SeedMessage(maxAttempts: 1);
        seedContext.OutboxMessages.Add(message);
        await seedContext.SaveChangesAsync();

        await using var context1 = _fixture.CreateContext();
        var processor1 = new OutboxProcessor(context1, new SystemClock());
        var firstClaim = (await processor1.ClaimBatchAsync(1, "worker-1")).Single();
        firstClaim.ExecutionGeneration.Should().Be(1);
        firstClaim.AttemptNumber.Should().Be(1);
        await processor1.FailRetryableAsync(firstClaim.MessageId, firstClaim.ProcessingToken, "boom", TimeSpan.FromMinutes(1));

        await using var requeueContext = _fixture.CreateContext();
        var requeueProcessor = new OutboxProcessor(requeueContext, new SystemClock());
        await requeueProcessor.RequeueAsync(message.Id);

        await using var context2 = _fixture.CreateContext();
        var processor2 = new OutboxProcessor(context2, new SystemClock());
        var actClaim = async () => (await processor2.ClaimBatchAsync(1, "worker-2")).Single();
        var secondClaim = await actClaim.Should().NotThrowAsync(
            "inserting (message, generation 2, attempt 1) must never collide with (message, generation 1, attempt 1)");

        secondClaim.Subject.ExecutionGeneration.Should().Be(2);
        secondClaim.Subject.AttemptNumber.Should().Be(1);

        await using var verifyContext = _fixture.CreateContext();
        var allHistory = await verifyContext.OutboxMessageAttempts.AsNoTracking()
            .Where(a => a.OutboxMessageId == message.Id)
            .ToListAsync();
        allHistory.Should().HaveCount(2);
        allHistory.Should().Contain(a => a.ExecutionGeneration == 1 && a.AttemptNumber == 1);
        allHistory.Should().Contain(a => a.ExecutionGeneration == 2 && a.AttemptNumber == 1);
    }

    [Fact]
    public async Task C9e_requeue_preserves_the_business_idempotency_key()
    {
        await using var seedContext = _fixture.CreateContext();
        var message = SeedMessage(maxAttempts: 1, idempotencyKey: "document:render-request-123");
        seedContext.OutboxMessages.Add(message);
        await seedContext.SaveChangesAsync();

        await using var context1 = _fixture.CreateContext();
        var processor1 = new OutboxProcessor(context1, new SystemClock());
        var claimed = (await processor1.ClaimBatchAsync(1, "worker-1")).Single();
        await processor1.FailRetryableAsync(claimed.MessageId, claimed.ProcessingToken, "boom", TimeSpan.FromMinutes(1));

        await using var requeueContext = _fixture.CreateContext();
        var requeueProcessor = new OutboxProcessor(requeueContext, new SystemClock());
        await requeueProcessor.RequeueAsync(message.Id);

        await using var verifyContext = _fixture.CreateContext();
        var reloaded = await verifyContext.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == message.Id);

        reloaded.ExecutionGeneration.Should().Be(2, "the generation changed");
        reloaded.IdempotencyKey.Should().Be("document:render-request-123", "the business effect being retried is the SAME one");
        reloaded.Id.Should().Be(message.Id, "requeue never creates a new message identity");
    }

    [Fact]
    public async Task C9f_requeue_then_success_ends_processed_with_no_disposition_and_full_history()
    {
        await using var seedContext = _fixture.CreateContext();
        var message = SeedMessage(maxAttempts: 1);
        seedContext.OutboxMessages.Add(message);
        await seedContext.SaveChangesAsync();

        await using var context1 = _fixture.CreateContext();
        var processor1 = new OutboxProcessor(context1, new SystemClock());
        var g1Claim = (await processor1.ClaimBatchAsync(1, "worker-1")).Single();
        await processor1.FailRetryableAsync(g1Claim.MessageId, g1Claim.ProcessingToken, "boom", TimeSpan.FromMinutes(1));

        await using var requeueContext = _fixture.CreateContext();
        await new OutboxProcessor(requeueContext, new SystemClock()).RequeueAsync(message.Id);

        await using var context2 = _fixture.CreateContext();
        var processor2 = new OutboxProcessor(context2, new SystemClock());
        var g2Claim = (await processor2.ClaimBatchAsync(1, "worker-2")).Single();
        (await processor2.CompleteAsync(g2Claim.MessageId, g2Claim.ProcessingToken)).Should().BeTrue();

        await using var verifyContext = _fixture.CreateContext();
        var reloaded = await verifyContext.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == message.Id);
        reloaded.Status.Should().Be(OutboxStatus.Processed);
        reloaded.FailureDispositionValue.Should().BeNull("there is no RESOLVED disposition — success IS processed, with no disposition at all");

        var allHistory = await verifyContext.OutboxMessageAttempts.AsNoTracking()
            .Where(a => a.OutboxMessageId == message.Id).OrderBy(a => a.ExecutionGeneration).ToListAsync();
        allHistory.Should().HaveCount(2);
        allHistory[0].Outcome.Should().Be(OutboxAttemptOutcome.RetryableFailure);
        allHistory[1].Outcome.Should().Be(OutboxAttemptOutcome.Succeeded);
    }

    [Fact]
    public async Task C3_stale_lease_owner_cannot_heartbeat_after_another_worker_reclaims()
    {
        await using var seedContext = _fixture.CreateContext();
        var message = SeedMessage();
        seedContext.OutboxMessages.Add(message);
        await seedContext.SaveChangesAsync();

        await using var contextA = _fixture.CreateContext();
        var processorA = new OutboxProcessor(contextA, new SystemClock());
        var claimedByA = (await processorA.ClaimBatchAsync(1, "worker-a", TimeSpan.FromMilliseconds(1))).Single();

        await Task.Delay(50);
        await using var reclaimContext = _fixture.CreateContext();
        await new OutboxProcessor(reclaimContext, new SystemClock()).ReclaimExpiredLeasesAsync();

        await using var contextB = _fixture.CreateContext();
        var claimedByB = (await new OutboxProcessor(contextB, new SystemClock()).ClaimBatchAsync(1, "worker-b")).Single();

        await using var heartbeatContextA = _fixture.CreateContext();
        var heartbeatSucceeded = await new OutboxProcessor(heartbeatContextA, new SystemClock())
            .HeartbeatAsync(claimedByA.MessageId, claimedByA.ProcessingToken);

        heartbeatSucceeded.Should().BeFalse("A's token no longer matches — 0 rows affected, never overwrite B's lease");

        await using var verifyContext = _fixture.CreateContext();
        var reloaded = await verifyContext.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == message.Id);
        reloaded.ProcessingToken.Should().Be(claimedByB.ProcessingToken, "B's lease must be completely unaffected by A's stale heartbeat");
    }

    [Fact]
    public async Task C4_attempt_count_increments_at_claim_before_the_consumer_ever_runs()
    {
        await using var seedContext = _fixture.CreateContext();
        var message = SeedMessage();
        seedContext.OutboxMessages.Add(message);
        await seedContext.SaveChangesAsync();

        await using var context = _fixture.CreateContext();
        await new OutboxProcessor(context, new SystemClock()).ClaimBatchAsync(1, "worker-x");

        await using var verifyContext = _fixture.CreateContext();
        var reloaded = await verifyContext.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == message.Id);
        reloaded.AttemptCount.Should().Be(1, "the claim itself increments attempt_count — the consumer has not even started yet");
    }

    [Fact]
    public async Task C5_a_lease_expiry_crash_on_the_final_attempt_becomes_failed_not_a_further_attempt()
    {
        await using var seedContext = _fixture.CreateContext();
        var message = SeedMessage(maxAttempts: 1); // claim = the final permitted attempt
        seedContext.OutboxMessages.Add(message);
        await seedContext.SaveChangesAsync();

        await using var context = _fixture.CreateContext();
        await new OutboxProcessor(context, new SystemClock()).ClaimBatchAsync(1, "worker-x", TimeSpan.FromMilliseconds(1));

        await Task.Delay(50); // the worker crashes — nobody calls Complete/Fail; the lease just expires
        await using var reclaimContext = _fixture.CreateContext();
        await new OutboxProcessor(reclaimContext, new SystemClock()).ReclaimExpiredLeasesAsync();

        await using var verifyContext = _fixture.CreateContext();
        var reloaded = await verifyContext.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == message.Id);
        reloaded.Status.Should().Be(OutboxStatus.Failed, "a crash on the final attempt is terminal — identical to an explicit retryable failure there (C-6a)");
        reloaded.FailureReason.Should().Be("LEASE_EXPIRED_ON_FINAL_ATTEMPT");
        reloaded.AvailableAt.Should().BeNull();
    }

    [Fact]
    public async Task C7_a_non_retryable_failure_is_immediately_failed_regardless_of_remaining_budget()
    {
        await using var seedContext = _fixture.CreateContext();
        var message = SeedMessage(maxAttempts: 5); // plenty of budget remaining
        seedContext.OutboxMessages.Add(message);
        await seedContext.SaveChangesAsync();

        await using var context = _fixture.CreateContext();
        var processor = new OutboxProcessor(context, new SystemClock());
        var claimed = (await processor.ClaimBatchAsync(1, "worker-x")).Single();
        await processor.FailNonRetryableAsync(claimed.MessageId, claimed.ProcessingToken, "ValidationError", "payload rejected by consumer");

        await using var verifyContext = _fixture.CreateContext();
        var reloaded = await verifyContext.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == message.Id);
        reloaded.Status.Should().Be(OutboxStatus.Failed, "non-retryable skips the ladder entirely, even with 4 attempts of budget left");
        reloaded.FailureReason.Should().Be("NON_RETRYABLE");
        reloaded.AvailableAt.Should().BeNull();
    }

    [Fact]
    public async Task C8_dismissing_a_failure_preserves_all_history_and_stops_it_counting_as_degraded()
    {
        await using var seedContext = _fixture.CreateContext();
        var message = SeedMessage(maxAttempts: 1);
        seedContext.OutboxMessages.Add(message);
        await seedContext.SaveChangesAsync();

        await using var context1 = _fixture.CreateContext();
        var processor1 = new OutboxProcessor(context1, new SystemClock());
        var claimed = (await processor1.ClaimBatchAsync(1, "worker-1")).Single();
        await processor1.FailRetryableAsync(claimed.MessageId, claimed.ProcessingToken, "boom", TimeSpan.FromMinutes(1));

        var dismisser = Guid.NewGuid();
        await using var dismissContext = _fixture.CreateContext();
        (await new OutboxProcessor(dismissContext, new SystemClock()).DismissAsync(message.Id, dismisser, "known transient provider outage")).Should().BeTrue();

        await using var verifyContext = _fixture.CreateContext();
        var reloaded = await verifyContext.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == message.Id);
        reloaded.Status.Should().Be(OutboxStatus.Failed, "dismissal is a disposition change, never a status/history change");
        reloaded.FailureDispositionValue.Should().Be(FailureDisposition.Dismissed);
        reloaded.DismissedBy.Should().Be(dismisser);
        reloaded.LastError.Should().NotBeNullOrEmpty("last_error must remain — dismissal never erases the facts");

        var history = await verifyContext.OutboxMessageAttempts.AsNoTracking().Where(a => a.OutboxMessageId == message.Id).ToListAsync();
        history.Should().ContainSingle("every attempt row must remain after dismissal");
    }

    [Fact]
    public async Task C9g_requeuing_a_dismissed_failure_also_opens_a_new_generation_and_clears_the_disposition()
    {
        await using var seedContext = _fixture.CreateContext();
        var message = SeedMessage(maxAttempts: 1);
        seedContext.OutboxMessages.Add(message);
        await seedContext.SaveChangesAsync();

        await using var context1 = _fixture.CreateContext();
        var processor1 = new OutboxProcessor(context1, new SystemClock());
        var claimed = (await processor1.ClaimBatchAsync(1, "worker-1")).Single();
        await processor1.FailRetryableAsync(claimed.MessageId, claimed.ProcessingToken, "boom", TimeSpan.FromMinutes(1));

        await using var dismissContext = _fixture.CreateContext();
        await new OutboxProcessor(dismissContext, new SystemClock()).DismissAsync(message.Id, Guid.NewGuid(), "thought it was permanent");

        // An Owner changes their mind and revives the dismissed message. RequeueAsync's WHERE
        // clause matches on status = 'Failed' alone — a DISMISSED message's status is still
        // Failed (disposition is a separate column) — so this must succeed exactly like any
        // other requeue, opening a new generation regardless of the prior disposition.
        await using var requeueContext = _fixture.CreateContext();
        (await new OutboxProcessor(requeueContext, new SystemClock()).RequeueAsync(message.Id)).Should().BeTrue();

        await using var verifyContext = _fixture.CreateContext();
        var reloaded = await verifyContext.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == message.Id);
        reloaded.ExecutionGeneration.Should().Be(2, "dismissal does not block a subsequent requeue from opening a new generation");
        reloaded.Status.Should().Be(OutboxStatus.Pending);
        reloaded.FailureDispositionValue.Should().BeNull("requeue clears the disposition regardless of whether it was Active or Dismissed");
        reloaded.AttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task C9b_dismissed_failure_is_not_claimable()
    {
        await using var seedContext = _fixture.CreateContext();
        var message = SeedMessage(maxAttempts: 1);
        seedContext.OutboxMessages.Add(message);
        await seedContext.SaveChangesAsync();

        await using var context1 = _fixture.CreateContext();
        var processor1 = new OutboxProcessor(context1, new SystemClock());
        var claimed = (await processor1.ClaimBatchAsync(1, "worker-1")).Single();
        await processor1.FailRetryableAsync(claimed.MessageId, claimed.ProcessingToken, "boom", TimeSpan.FromMinutes(1));

        await using var dismissContext = _fixture.CreateContext();
        var dismissProcessor = new OutboxProcessor(dismissContext, new SystemClock());
        (await dismissProcessor.DismissAsync(message.Id, Guid.NewGuid(), "no longer relevant")).Should().BeTrue();

        await using var context2 = _fixture.CreateContext();
        var processor2 = new OutboxProcessor(context2, new SystemClock());
        var attemptedClaim = await processor2.ClaimBatchAsync(1, "worker-2");
        attemptedClaim.Should().BeEmpty("a DISMISSED message is FAILED, and the claim query only looks at PENDING");
    }
}
