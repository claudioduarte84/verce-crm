using FluentAssertions;
using Verce.SharedKernel.Events;

namespace Verce.Platform.Tests;

/// <summary>
/// ADR-0012 §2, §5: every event's envelope fields are decided at construction, and the
/// causation chain (A-5) is built by whoever raises the event stamping the ambient
/// <see cref="Verce.Platform.UnitOfWork.AmbientOperationContext.CurrentEvent"/>'s EventId as the
/// new event's CausationId. This tests the envelope contract itself, independent of the
/// UnitOfWork loop that drives it (covered end-to-end by Verce.IntegrationTests).
/// </summary>
public class DomainEventEnvelopeTests
{
    private sealed class OrderPlaced : DomainEventBase
    {
        public OrderPlaced(Guid correlationId, Guid? causationId, DateTimeOffset occurredAtUtc)
            : base(correlationId, causationId, occurredAtUtc) { }
    }

    private sealed class OrderShipped : DomainEventBase
    {
        public OrderShipped(Guid correlationId, Guid? causationId, DateTimeOffset occurredAtUtc)
            : base(correlationId, causationId, occurredAtUtc) { }
    }

    [Fact]
    public void EventId_is_a_UUID_v7_assigned_at_construction()
    {
        var e = new OrderPlaced(Guid.CreateVersion7(), null, DateTimeOffset.UtcNow);

        e.EventId.Should().NotBe(Guid.Empty);
        (e.EventId.ToByteArray()[7] >> 4).Should().Be(7);
    }

    [Fact]
    public void EventType_is_the_concrete_CLR_type_name_not_the_base_type()
    {
        var e = new OrderPlaced(Guid.CreateVersion7(), null, DateTimeOffset.UtcNow);
        e.EventType.Should().Be(nameof(OrderPlaced));
    }

    [Fact]
    public void OccurredAtUtc_comes_from_the_supplied_instant_not_wall_clock_time()
    {
        var frozen = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var e = new OrderPlaced(Guid.CreateVersion7(), null, frozen);
        e.OccurredAtUtc.Should().Be(frozen);
    }

    [Fact]
    public void Wave1_event_has_no_causation_only_a_correlation()
    {
        var correlationId = Guid.CreateVersion7();
        var e = new OrderPlaced(correlationId, null, DateTimeOffset.UtcNow);

        e.CorrelationId.Should().Be(correlationId);
        e.CausationId.Should().BeNull("wave 1's cause is the command itself, identified only by CorrelationId");
    }

    [Fact]
    public void ADR0012_A5_causation_chain_links_across_waves_sharing_one_correlation()
    {
        var correlationId = Guid.CreateVersion7();

        var e1 = new OrderPlaced(correlationId, null, DateTimeOffset.UtcNow);
        // Simulates the UnitOfWork stamping AmbientOperationContext.CurrentEvent = e1 before
        // the handler runs, so anything the handler raises carries e1.EventId as its cause.
        var e2 = new OrderShipped(correlationId, e1.EventId, DateTimeOffset.UtcNow);

        e2.CorrelationId.Should().Be(e1.CorrelationId, "every event in one command shares one correlation_id");
        e2.CausationId.Should().Be(e1.EventId, "E2.CausationId = E1.EventId per ADR-0012 A-5");
        e1.CausationId.Should().BeNull();
    }

    [Fact]
    public void A_concrete_domain_event_implements_exactly_IDomainEvent()
    {
        var e = new OrderPlaced(Guid.CreateVersion7(), null, DateTimeOffset.UtcNow);

        e.Should().BeAssignableTo<IDomainEvent>();
        e.Should().NotBeAssignableTo<IIntegrationEvent>();
    }
}
