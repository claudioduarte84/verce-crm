using System.Reflection;
using FluentAssertions;
using Verce.Modules.Production;

namespace Verce.Production.Tests;

/// <summary>ADR-0020 §A.2/§A.3/§A.8 — the minimum Production Core's own guard predicates.</summary>
public class ProductionOrderTests
{
    private static ProductionOrder NewOrder() =>
        ProductionOrder.CreateQueued(1, new DateOnly(2026, 9, 20), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void CreateQueued_starts_in_QUEUED()
    {
        var order = NewOrder();
        order.Status.Should().Be(ProductionOrderStatus.QUEUED);
        order.HasPendingRevision.Should().BeFalse();
        order.OrderNumber.Should().Be("260920-1");
    }

    [Theory]
    [InlineData(ProductionOrderStatus.QUEUED, false)]
    [InlineData(ProductionOrderStatus.IN_PRODUCTION, true)]
    [InlineData(ProductionOrderStatus.READY, true)]
    [InlineData(ProductionOrderStatus.SHIPPED, true)]
    [InlineData(ProductionOrderStatus.DELIVERED, false)]
    [InlineData(ProductionOrderStatus.CANCELED, false)]
    public void BlocksApproval_matches_the_ADR_0020_A3_guard_set_exactly(ProductionOrderStatus status, bool expected)
    {
        var order = NewOrder();
        SetStatus(order, status);
        order.BlocksApproval.Should().Be(expected);
    }

    [Theory]
    [InlineData(ProductionOrderStatus.QUEUED, true)]
    [InlineData(ProductionOrderStatus.IN_PRODUCTION, true)]
    [InlineData(ProductionOrderStatus.READY, true)]
    [InlineData(ProductionOrderStatus.SHIPPED, true)]
    [InlineData(ProductionOrderStatus.DELIVERED, false)]
    [InlineData(ProductionOrderStatus.CANCELED, false)]
    public void IsNonTerminal_matches_STATE_MACHINES_4_1(ProductionOrderStatus status, bool expected)
    {
        var order = NewOrder();
        SetStatus(order, status);
        order.IsNonTerminal.Should().Be(expected);
    }

    [Fact]
    public void CancelAsSupersededByRevision_only_works_from_QUEUED()
    {
        var order = NewOrder();
        SetStatus(order, ProductionOrderStatus.IN_PRODUCTION);
        var act = () => order.CancelAsSupersededByRevision(Guid.NewGuid());
        act.Should().Throw<ArgumentException>().WithMessage("PRODUCTION_ORDER_NOT_QUEUED");
    }

    [Fact]
    public void CancelAsSupersededByRevision_sets_the_terminal_state_reason_and_pointer()
    {
        var order = NewOrder();
        var supersedingOrderId = Guid.NewGuid();
        order.CancelAsSupersededByRevision(supersedingOrderId);
        order.Status.Should().Be(ProductionOrderStatus.CANCELED);
        order.CancellationReason.Should().Be("SUPERSEDED_BY_REVISION");
        order.SupersededByOrderId.Should().Be(supersedingOrderId);
        order.HasPendingRevision.Should().BeFalse();
    }

    [Fact]
    public void SetHasPendingRevision_is_purely_advisory_and_never_changes_Status()
    {
        var order = NewOrder();
        order.SetHasPendingRevision(true);
        order.HasPendingRevision.Should().BeTrue();
        order.Status.Should().Be(ProductionOrderStatus.QUEUED); // unaffected — advisory only (§A.4)
    }

    private static void SetStatus(ProductionOrder order, ProductionOrderStatus status)
    {
        // Test-only reflection to reach the intermediate states S6 does not itself drive
        // (those are S9's operational transitions) — proving THIS aggregate's predicates are
        // correct for every status the full enum can hold, not only the ones S6 can produce.
        typeof(ProductionOrder).GetProperty(nameof(ProductionOrder.Status), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(order, status);
    }
}
