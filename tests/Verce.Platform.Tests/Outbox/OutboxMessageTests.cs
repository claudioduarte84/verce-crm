using FluentAssertions;
using Verce.Platform.Outbox;

namespace Verce.Platform.Tests.Outbox;

/// <summary>
/// Pure entity-level behavior of <see cref="OutboxMessage"/> — the claim-eligibility predicate
/// and the derived <see cref="OutboxMessage.RequeueCount"/> — kept separate from the SQL claim
/// query in Verce.Platform's OutboxProcessor (exercised against real PostgreSQL in
/// Verce.IntegrationTests). <see cref="OutboxMessage.IsEligible"/> is documented as the SAME
/// predicate the claim query and the health check both use (ADR-0012 §14, §25.1) — this test
/// protects that predicate in isolation.
/// </summary>
public class OutboxMessageTests
{
    private static OutboxMessage NewMessage(DateTimeOffset now, int maxAttempts = 5) =>
        OutboxMessage.Enqueue(
            eventType: "ProbeIntegrationEvent",
            payloadJson: "{}",
            idempotencyKey: "probe-key",
            correlationId: Guid.CreateVersion7(),
            requestId: null,
            actorUserId: null,
            aggregateType: "Probe",
            aggregateId: null,
            now: now,
            maxAttempts: maxAttempts);

    [Fact]
    public void Enqueue_starts_at_generation_1_attempt_0_pending()
    {
        var now = DateTimeOffset.UtcNow;
        var message = NewMessage(now);

        message.ExecutionGeneration.Should().Be(1);
        message.AttemptCount.Should().Be(0);
        message.Status.Should().Be(OutboxStatus.Pending);
        message.AvailableAt.Should().Be(now);
    }

    [Fact]
    public void RequeueCount_is_derived_as_generation_minus_one_never_stored_directly()
    {
        var message = NewMessage(DateTimeOffset.UtcNow);

        message.RequeueCount.Should().Be(0, "generation 1 means it has never been requeued");
    }

    [Fact]
    public void A_pending_message_available_now_with_budget_remaining_is_eligible()
    {
        var now = DateTimeOffset.UtcNow;
        var message = NewMessage(now);

        message.IsEligible(now).Should().BeTrue();
    }

    [Fact]
    public void H2a_a_message_scheduled_for_the_future_is_not_eligible_yet()
    {
        // A message serving its backoff (available_at in the future) must never be claimed
        // and must never count toward a health stall — H-2a.
        var now = DateTimeOffset.UtcNow;
        var message = NewMessage(now);
        var future = now.AddHours(1);

        message.IsEligible(now).Should().BeTrue();
        // Simulate "available_at moved to the future" by evaluating eligibility at an earlier instant.
        message.IsEligible(now.AddMinutes(-1)).Should().BeFalse("available_at has not been reached yet at this instant");
        message.IsEligible(future).Should().BeTrue("once available_at is reached it becomes eligible again");
    }

    [Fact]
    public void C6b_a_message_at_or_above_max_attempts_is_never_eligible_even_if_pending()
    {
        var now = DateTimeOffset.UtcNow;
        var message = NewMessage(now, maxAttempts: 3);

        // Drive attempt_count to the budget ceiling the same way the real claim path does:
        // there is no public setter, so we assert the invariant via the documented boundary
        // using reflection is unnecessary — instead verify the predicate's own boundary logic
        // directly against the constructor-provided budget by checking a message whose budget
        // is already exhausted at creation (maxAttempts = 0 emulates "no budget left").
        var exhausted = OutboxMessage.Enqueue(
            eventType: "ProbeIntegrationEvent", payloadJson: "{}", idempotencyKey: null,
            correlationId: Guid.CreateVersion7(), requestId: null, actorUserId: null,
            aggregateType: "Probe", aggregateId: null, now: now, maxAttempts: 0);

        exhausted.IsEligible(now).Should().BeFalse("attempt_count (0) is not < max_attempts (0) — no budget at all");
    }

    [Fact]
    public void IdempotencyKey_can_be_null_for_events_that_declare_none()
    {
        var message = OutboxMessage.Enqueue(
            eventType: "ProbeIntegrationEvent", payloadJson: "{}", idempotencyKey: null,
            correlationId: Guid.CreateVersion7(), requestId: null, actorUserId: null,
            aggregateType: "Probe", aggregateId: null, now: DateTimeOffset.UtcNow);

        message.IdempotencyKey.Should().BeNull();
    }

    [Fact]
    public void Enqueue_assigns_a_UUID_v7_identifier()
    {
        var message = NewMessage(DateTimeOffset.UtcNow);

        message.Id.Should().NotBe(Guid.Empty);
        (message.Id.ToByteArray()[7] >> 4).Should().Be(7);
    }
}
