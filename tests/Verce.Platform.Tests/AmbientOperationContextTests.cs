using FluentAssertions;
using Verce.Platform.UnitOfWork;

namespace Verce.Platform.Tests;

/// <summary>ADR-0012 §10: the ambient context is the one piece of state shared, by reference,
/// across every wave of a single Unit of Work — its identity fields never change after
/// construction, and its mutable fields track live progress through the waves.</summary>
public class AmbientOperationContextTests
{
    [Fact]
    public void CorrelationId_is_fixed_for_the_lifetime_of_the_instance()
    {
        var correlationId = Guid.CreateVersion7();
        var context = new AmbientOperationContext(correlationId, AuditSource.Api, DateTimeOffset.UtcNow);

        context.CorrelationId.Should().Be(correlationId);
        context.WaveIndex = 3;
        context.CorrelationId.Should().Be(correlationId, "CorrelationId identifies the whole command, not a single wave");
    }

    [Fact]
    public void SetActor_updates_both_actor_fields_together()
    {
        var context = new AmbientOperationContext(Guid.CreateVersion7(), AuditSource.Api, DateTimeOffset.UtcNow);
        context.ActorUserId.Should().BeNull();
        context.ActorDisplayName.Should().BeNull();

        var userId = Guid.CreateVersion7();
        context.SetActor(userId, "Ana Owner");

        context.ActorUserId.Should().Be(userId);
        context.ActorDisplayName.Should().Be("Ana Owner");
    }

    [Fact]
    public void A_System_source_context_can_have_no_actor_ADR0010_G5()
    {
        // G-5: the expiration job runs with source=JOB and user_id IS NULL — never MIGRATION.
        var context = new AmbientOperationContext(Guid.CreateVersion7(), AuditSource.Job, DateTimeOffset.UtcNow);

        context.Source.Should().Be(AuditSource.Job);
        context.ActorUserId.Should().BeNull();
    }

    [Fact]
    public void WaveIndex_starts_at_zero_and_can_be_advanced_by_the_UnitOfWork()
    {
        var context = new AmbientOperationContext(Guid.CreateVersion7(), AuditSource.Api, DateTimeOffset.UtcNow);
        context.WaveIndex.Should().Be(0);

        context.WaveIndex = 1;
        context.WaveIndex = 2;
        context.WaveIndex.Should().Be(2);
    }

    [Fact]
    public void CurrentEvent_is_null_outside_dispatch_and_settable_during_it()
    {
        var context = new AmbientOperationContext(Guid.CreateVersion7(), AuditSource.Api, DateTimeOffset.UtcNow);
        context.CurrentEvent.Should().BeNull("wave 1 has no causing event — the command itself is the cause");

        var probe = new ProbeDomainEvent(context.CorrelationId, null, DateTimeOffset.UtcNow);
        context.CurrentEvent = probe;
        context.CurrentEvent.Should().BeSameAs(probe);

        context.CurrentEvent = null; // the UnitOfWork clears it after each wave's dispatch loop
        context.CurrentEvent.Should().BeNull();
    }

    [Fact]
    public void VersionHandledAggregates_records_a_root_exactly_once_per_UoW()
    {
        // ADR-0011 §2.3-2.4 / B-2a: this set is the mechanism that prevents re-deriving
        // "was it Added?" from EF's EntityState after the first save.
        var context = new AmbientOperationContext(Guid.CreateVersion7(), AuditSource.Api, DateTimeOffset.UtcNow);
        var rootId = Guid.CreateVersion7();

        context.VersionHandledAggregates.Should().BeEmpty();

        var addedFirstTime = context.VersionHandledAggregates.Add(rootId);
        var addedSecondTime = context.VersionHandledAggregates.Add(rootId);

        addedFirstTime.Should().BeTrue();
        addedSecondTime.Should().BeFalse("the interceptor must never re-evaluate a root it already settled this UoW");
        context.VersionHandledAggregates.Should().ContainSingle().Which.Should().Be(rootId);
    }

    [Fact]
    public void Two_separate_contexts_never_share_VersionHandledAggregates_state()
    {
        // Two logical operations (two commands, two HTTP requests, two CLI invocations) must
        // be fully isolated from each other — there is no static/shared bookkeeping.
        var rootId = Guid.CreateVersion7();
        var first = new AmbientOperationContext(Guid.CreateVersion7(), AuditSource.Api, DateTimeOffset.UtcNow);
        var second = new AmbientOperationContext(Guid.CreateVersion7(), AuditSource.Api, DateTimeOffset.UtcNow);

        first.VersionHandledAggregates.Add(rootId);

        second.VersionHandledAggregates.Should().BeEmpty("a fresh Unit of Work must not inherit another operation's bookkeeping");
    }

    private sealed class ProbeDomainEvent : Verce.SharedKernel.Events.DomainEventBase
    {
        public ProbeDomainEvent(Guid correlationId, Guid? causationId, DateTimeOffset occurredAtUtc)
            : base(correlationId, causationId, occurredAtUtc) { }
    }
}
