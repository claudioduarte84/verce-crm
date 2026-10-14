using FluentAssertions;
using Verce.Modules.Commerce;

namespace Verce.Commerce.Tests;

public sealed class MarketplaceListingSyncKernelTests
{
    [Fact]
    public void G05_100_found_and_5_blocking_is_partial_and_only_retryable_outcomes_are_selected()
    {
        var counters=new MarketplaceListingSyncCounters(100,95,5,0,0,95,95);
        MarketplaceListingSyncKernel.Compute(MarketplaceListingSyncKind.FULL,true,counters,null).Status.Should().Be(MarketplaceListingSyncStatus.PARTIAL);
        MarketplaceListingSyncKernel.IsRetryable(MarketplaceListingSyncItemOutcome.NOT_FOUND).Should().BeTrue();
        MarketplaceListingSyncKernel.IsRetryable(MarketplaceListingSyncItemOutcome.ACCESS_DENIED).Should().BeTrue();
        MarketplaceListingSyncKernel.IsRetryable(MarketplaceListingSyncItemOutcome.TRANSIENT_ERROR).Should().BeTrue();
        MarketplaceListingSyncKernel.IsRetryable(MarketplaceListingSyncItemOutcome.SELLER_MISMATCH).Should().BeFalse();
        MarketplaceListingSyncKernel.IsRetryable(MarketplaceListingSyncItemOutcome.PERMANENT_ERROR).Should().BeFalse();
    }

    [Fact]
    public void G05_failed_and_succeeded_cases_follow_the_frozen_rule()
    {
        MarketplaceListingSyncKernel.Compute(MarketplaceListingSyncKind.FULL,false,new(0,0,0,0,0,0,0),"LISTING_ENUMERATION_INCOMPLETE").Status.Should().Be(MarketplaceListingSyncStatus.FAILED);
        MarketplaceListingSyncKernel.Compute(MarketplaceListingSyncKind.FULL,true,new(4,4,0,0,0,4,4),null).Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);
    }
}
