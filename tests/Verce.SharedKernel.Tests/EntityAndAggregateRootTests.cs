using FluentAssertions;
using Verce.SharedKernel.Domain;
using Verce.SharedKernel.Events;

namespace Verce.SharedKernel.Tests;

file sealed class TestAggregate : AggregateRoot
{
    public TestAggregate() { }
    public TestAggregate(Guid id) : base(id) { }

    public void DoSomething() => Raise(new TestDomainEvent(Guid.NewGuid(), null, DateTimeOffset.UtcNow));

    // Thin public wrappers for the platform-only internal API — legitimate here only because
    // InternalsVisibleTo("Verce.SharedKernel.Tests") grants this test assembly the same access
    // Verce.Platform's real interceptor has (ADR-0011 §2.4).
    public IReadOnlyList<IEvent> DrainForTest() => DrainPendingEvents();
    public void BumpVersionForTest() => BumpVersion();
    public void InitializeForInsertTest() => InitializeVersionForInsert();
}

file sealed class TestDomainEvent : DomainEventBase
{
    public TestDomainEvent(Guid correlationId, Guid? causationId, DateTimeOffset occurredAtUtc)
        : base(correlationId, causationId, occurredAtUtc) { }
}

public class EntityAndAggregateRootTests
{
    [Fact]
    public void New_entity_gets_a_UUID_v7_identifier()
    {
        var aggregate = new TestAggregate();

        aggregate.Id.Should().NotBe(Guid.Empty);
        // UUID v7: version nibble is 7, found in the 7th byte's high nibble per RFC 9562.
        var bytes = aggregate.Id.ToByteArray();
        (bytes[7] >> 4).Should().Be(7);
    }

    [Fact]
    public void Two_entities_created_moments_apart_have_ascending_UUID_v7_prefixes()
    {
        var first = new TestAggregate();
        Thread.Sleep(5);
        var second = new TestAggregate();

        // Compare the big-endian timestamp prefix (first 6 bytes, 12 hex chars) — proves index
        // locality, NOT business ordering (ADR-0011 §1.2 — v7 is identity/locality, never used
        // to order). MUST read the prefix from Guid.ToString("N"), never Guid.ToByteArray():
        // .NET's ToByteArray() stores the first three RFC 9562 fields (including the 48-bit
        // timestamp) in little-endian order, so a byte-array slice does NOT reflect chronological
        // order at all — comparing it produced results that looked like flaky timing noise but
        // were actually a deterministic consequence of reading the wrong byte order.
        string Prefix(Guid id) => id.ToString("N")[..12];
        string.Compare(Prefix(first.Id), Prefix(second.Id), StringComparison.Ordinal)
            .Should().BeLessThanOrEqualTo(0);
    }

    [Fact]
    public void New_aggregate_root_defaults_to_version_1()
    {
        var aggregate = new TestAggregate();
        aggregate.Version.Should().Be(1);
    }

    [Fact]
    public void Entities_are_equal_by_type_and_id()
    {
        var id = Guid.CreateVersion7();
        var a = new TestAggregate(id);
        var b = new TestAggregate(id);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void Raise_adds_to_pending_events_and_DrainPendingEvents_empties_it()
    {
        var aggregate = new TestAggregate();
        aggregate.DoSomething();

        aggregate.PendingEvents.Should().HaveCount(1);

        var drained = aggregate.DrainForTest();

        drained.Should().HaveCount(1);
        aggregate.PendingEvents.Should().BeEmpty("draining must clear the buffer atomically");
    }

    [Fact]
    public void Draining_twice_in_a_row_returns_nothing_the_second_time()
    {
        var aggregate = new TestAggregate();
        aggregate.DoSomething();

        var first = aggregate.DrainForTest();
        var second = aggregate.DrainForTest();

        first.Should().HaveCount(1);
        second.Should().BeEmpty("a drained event must never be dispatched twice within one UoW");
    }

    [Fact]
    public void BumpVersion_increments_by_exactly_one()
    {
        var aggregate = new TestAggregate();
        aggregate.BumpVersionForTest();
        aggregate.Version.Should().Be(2);
    }

    [Fact]
    public void InitializeVersionForInsert_pins_version_to_1_even_if_called_after_mutation()
    {
        var aggregate = new TestAggregate();
        aggregate.BumpVersionForTest(); // simulate accidental early bump
        aggregate.InitializeForInsertTest();

        aggregate.Version.Should().Be(1, "an Added root is inserted at 1, never compared against a prior value");
    }
}
