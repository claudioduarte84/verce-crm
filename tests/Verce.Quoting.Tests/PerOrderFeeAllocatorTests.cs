using FluentAssertions;
using Verce.Modules.Quoting;

namespace Verce.Quoting.Tests;

/// <summary>CR-07.7 / ADR-0020 §C.8 — the canonical A-G vectors, exact decimal values.</summary>
public class PerOrderFeeAllocatorTests
{
    [Fact]
    public void A_single_line()
    {
        var result = PerOrderFeeAllocator.Allocate(5.00m, [new AllocationLineInput(1, 40.00m)]);
        result.Should().ContainSingle().Which.AllocatedAmount.Should().Be(5.00m);
    }

    [Fact]
    public void B_two_equal_basis_lines()
    {
        var result = PerOrderFeeAllocator.Allocate(5.00m, [new AllocationLineInput(1, 10.00m), new AllocationLineInput(2, 10.00m)]);
        result.Single(r => r.LineNumber == 1).AllocatedAmount.Should().Be(2.50m);
        result.Single(r => r.LineNumber == 2).AllocatedAmount.Should().Be(2.50m);
        result.Sum(r => r.AllocatedAmount).Should().Be(5.00m);
    }

    [Fact]
    public void C_three_equal_basis_lines_residual_cent_goes_to_lowest_line_number()
    {
        var result = PerOrderFeeAllocator.Allocate(10.00m,
            [new AllocationLineInput(1, 10.00m), new AllocationLineInput(2, 10.00m), new AllocationLineInput(3, 10.00m)]);
        result.Single(r => r.LineNumber == 1).AllocatedAmount.Should().Be(3.34m);
        result.Single(r => r.LineNumber == 2).AllocatedAmount.Should().Be(3.33m);
        result.Single(r => r.LineNumber == 3).AllocatedAmount.Should().Be(3.33m);
        result.Sum(r => r.AllocatedAmount).Should().Be(10.00m);
    }

    [Fact]
    public void D_unequal_basis()
    {
        var result = PerOrderFeeAllocator.Allocate(10.00m,
            [new AllocationLineInput(1, 100.00m), new AllocationLineInput(2, 50.00m), new AllocationLineInput(3, 25.00m)]);
        result.Single(r => r.LineNumber == 1).AllocatedAmount.Should().Be(5.71m);
        result.Single(r => r.LineNumber == 2).AllocatedAmount.Should().Be(2.86m);
        result.Single(r => r.LineNumber == 3).AllocatedAmount.Should().Be(1.43m);
        result.Sum(r => r.AllocatedAmount).Should().Be(10.00m);
    }

    [Fact]
    public void E_quantity_change_is_a_full_recomputation_of_D()
    {
        // L1 quantity 1 -> 2 doubles its basis; the whole group is recomputed from scratch.
        var result = PerOrderFeeAllocator.Allocate(10.00m,
            [new AllocationLineInput(1, 200.00m), new AllocationLineInput(2, 50.00m), new AllocationLineInput(3, 25.00m)]);
        result.Single(r => r.LineNumber == 1).AllocatedAmount.Should().Be(7.27m);
        result.Single(r => r.LineNumber == 2).AllocatedAmount.Should().Be(1.82m);
        result.Single(r => r.LineNumber == 3).AllocatedAmount.Should().Be(0.91m);
        result.Sum(r => r.AllocatedAmount).Should().Be(10.00m);
    }

    [Fact]
    public void F_line_removal_recomputes_the_remaining_group_not_a_patch()
    {
        var result = PerOrderFeeAllocator.Allocate(10.00m, [new AllocationLineInput(1, 100.00m), new AllocationLineInput(2, 50.00m)]);
        result.Single(r => r.LineNumber == 1).AllocatedAmount.Should().Be(6.67m);
        result.Single(r => r.LineNumber == 2).AllocatedAmount.Should().Be(3.33m);
        result.Sum(r => r.AllocatedAmount).Should().Be(10.00m);
    }

    [Fact]
    public void G_zero_basis_falls_back_to_equal_split_two_lines()
    {
        var result = PerOrderFeeAllocator.Allocate(5.00m, [new AllocationLineInput(1, 0m), new AllocationLineInput(2, 0m)]);
        result.Single(r => r.LineNumber == 1).AllocatedAmount.Should().Be(2.50m);
        result.Single(r => r.LineNumber == 2).AllocatedAmount.Should().Be(2.50m);
    }

    [Fact]
    public void G_zero_basis_falls_back_to_equal_split_three_lines_with_residual_to_lowest_line_number()
    {
        var result = PerOrderFeeAllocator.Allocate(10.00m,
            [new AllocationLineInput(1, 0m), new AllocationLineInput(2, 0m), new AllocationLineInput(3, 0m)]);
        result.Single(r => r.LineNumber == 1).AllocatedAmount.Should().Be(3.34m);
        result.Single(r => r.LineNumber == 2).AllocatedAmount.Should().Be(3.33m);
        result.Single(r => r.LineNumber == 3).AllocatedAmount.Should().Be(3.33m);
    }

    [Fact]
    public void Zero_fee_allocates_zero_to_every_line()
    {
        var result = PerOrderFeeAllocator.Allocate(0m, [new AllocationLineInput(1, 40.00m), new AllocationLineInput(2, 10.00m)]);
        result.Should().OnlyContain(r => r.AllocatedAmount == 0m);
    }

    [Fact]
    public void Negative_fee_is_rejected_defensively()
    {
        var act = () => PerOrderFeeAllocator.Allocate(-1m, [new AllocationLineInput(1, 10m)]);
        act.Should().Throw<ArgumentException>().WithMessage("PER_ORDER_ALLOCATION_NEGATIVE_FEE");
    }

    /// <summary>B-03: defense in depth. Pricing's FeeRuleVersion constructor already rejects a
    /// fractional-cent fee for new versions, but not every caller of this allocator goes through
    /// that HTTP validation path — the allocator itself must never silently truncate a residual
    /// that would break the exact-partition invariant (sum(alloc) == orderFee).</summary>
    [Fact]
    public void A_fractional_cent_order_fee_is_rejected_rather_than_silently_truncated()
    {
        var act = () => PerOrderFeeAllocator.Allocate(1.005m, [new AllocationLineInput(1, 10m), new AllocationLineInput(2, 10m)]);
        act.Should().Throw<ArgumentException>().WithMessage("FIXED_FEE_PRECISION_INVALID");
    }

    [Fact]
    public void Empty_line_set_allocates_nothing()
    {
        PerOrderFeeAllocator.Allocate(10m, []).Should().BeEmpty();
    }

    /// <summary>CR-07.7a: the invariant that actually matters — verified with a fixed-seed
    /// randomized property test (deterministic, never flaky) across many fee/basis/line-count
    /// combinations, always summing exactly and never going negative.</summary>
    [Fact]
    public void Property_allocation_always_sums_exactly_and_never_negative()
    {
        var random = new Random(20260920);
        for (var iteration = 0; iteration < 2000; iteration++)
        {
            var lineCount = random.Next(1, 12);
            var orderFee = Math.Round((decimal)random.NextDouble() * 10_000m, 2);
            var lines = Enumerable.Range(1, lineCount)
                .Select(n => new AllocationLineInput(n, Math.Round((decimal)random.NextDouble() * 5_000m, 6)))
                .ToArray();

            var result = PerOrderFeeAllocator.Allocate(orderFee, lines);

            result.Sum(r => r.AllocatedAmount).Should().Be(orderFee, $"iteration {iteration} with fee {orderFee} over {lineCount} lines");
            result.Should().OnlyContain(r => r.AllocatedAmount >= 0m, $"iteration {iteration}");
        }
    }
}
