using FluentAssertions;
using Verce.Modules.Commerce;

namespace Verce.Commerce.Tests;

public sealed class CommerceDomainTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-0.01)]
    public void Channel_offer_rejects_non_positive_price(decimal price)
    {
        var act = () => new ChannelOffer(Guid.NewGuid(), Guid.NewGuid(), price, ChannelOfferPriceSource.MANUAL);
        act.Should().Throw<ArgumentException>().WithMessage("CHANNEL_OFFER_PRICE_INVALID");
    }

    [Fact]
    public void Channel_offer_records_activation_and_deactivation_lifecycle()
    {
        var offer = new ChannelOffer(Guid.NewGuid(), Guid.NewGuid(), 10m, ChannelOfferPriceSource.MANUAL);
        var activated = DateTimeOffset.UtcNow;
        var deactivated = activated.AddMinutes(1);

        offer.Activate(activated);
        offer.Status.Should().Be(ChannelOfferStatus.ACTIVE);
        offer.ActivatedAt.Should().Be(activated);
        offer.DeactivatedAt.Should().BeNull();

        offer.Deactivate(deactivated);
        offer.Status.Should().Be(ChannelOfferStatus.INACTIVE);
        offer.DeactivatedAt.Should().Be(deactivated);
    }

    [Fact]
    public void Channel_offer_keeps_unknown_shipping_distinct_from_explicit_zero()
    {
        var unknown = new ChannelOffer(Guid.NewGuid(), Guid.NewGuid(), 10m, ChannelOfferPriceSource.MANUAL);
        var zero = new ChannelOffer(Guid.NewGuid(), Guid.NewGuid(), 10m, ChannelOfferPriceSource.MANUAL, 0m);
        unknown.SellerPaidShippingAmount.Should().BeNull();
        zero.SellerPaidShippingAmount.Should().Be(0m);
    }

    [Fact]
    public void Marketplace_account_rejects_sales_channel_reassignment()
    {
        var account = new MarketplaceAccount("SHOPEE", "shop-1", Guid.NewGuid(), "Loja");
        var act = () => account.RejectSalesChannelReassignment(Guid.NewGuid());
        act.Should().Throw<ArgumentException>().WithMessage("MARKETPLACE_ACCOUNT_CHANNEL_IMMUTABLE");
    }

    [Fact]
    public void Marketplace_account_capabilities_preserve_unknown_granted_and_denied_states()
    {
        var account = new MarketplaceAccount("SHOPEE", "shop-1", Guid.NewGuid(), "Loja");
        account.SetCapability("LISTINGS_READ", AccountCapabilityState.UNKNOWN);
        account.SetCapability("LISTINGS_WRITE", AccountCapabilityState.GRANTED);
        account.SetCapability("ORDERS_READ", AccountCapabilityState.DENIED);

        account.Capabilities.Select(x => x.State).Should().Equal(
            AccountCapabilityState.UNKNOWN,
            AccountCapabilityState.GRANTED,
            AccountCapabilityState.DENIED);
    }

    [Theory]
    [InlineData(ProviderCapabilityState.UNKNOWN)]
    [InlineData(ProviderCapabilityState.SUPPORTED)]
    [InlineData(ProviderCapabilityState.UNSUPPORTED)]
    public void Provider_capability_represents_every_structural_fact(ProviderCapabilityState state)
    {
        var capability = new MarketplaceProviderCapability("SHOPEE", "LISTINGS_READ", state);
        capability.State.Should().Be(state);
    }

    [Fact]
    public void Runtime_failure_does_not_rewrite_capability_facts()
    {
        var provider = new MarketplaceProviderCapability("SHOPEE", "LISTINGS_READ", ProviderCapabilityState.SUPPORTED);
        var account = new MarketplaceAccount("SHOPEE", "shop-1", Guid.NewGuid(), "Loja");
        account.SetCapability("LISTINGS_READ", AccountCapabilityState.GRANTED);

        account.RecordRuntimeFailure("timeout", DateTimeOffset.UtcNow);

        provider.State.Should().Be(ProviderCapabilityState.SUPPORTED);
        account.Capabilities.Single().State.Should().Be(AccountCapabilityState.GRANTED);
        account.Connection.RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.UNAVAILABLE);
        account.SyncState.Should().Be(MarketplaceSyncState.NEVER_SYNCED);
    }

    [Theory]
    [InlineData(ProviderCapabilityState.SUPPORTED, AccountCapabilityState.GRANTED, true, true)]
    [InlineData(ProviderCapabilityState.UNKNOWN, AccountCapabilityState.GRANTED, true, false)]
    [InlineData(ProviderCapabilityState.UNSUPPORTED, AccountCapabilityState.GRANTED, true, false)]
    [InlineData(ProviderCapabilityState.SUPPORTED, AccountCapabilityState.UNKNOWN, true, false)]
    [InlineData(ProviderCapabilityState.SUPPORTED, AccountCapabilityState.DENIED, true, false)]
    [InlineData(ProviderCapabilityState.SUPPORTED, AccountCapabilityState.GRANTED, false, false)]
    public void Effective_capability_requires_supported_granted_and_active(
        ProviderCapabilityState provider, AccountCapabilityState account, bool active, bool expected)
    {
        CommercePolicy.HasEffectiveCapability(provider, account, active).Should().Be(expected);
    }

    [Fact]
    public void Commercial_active_requires_active_product_and_active_offer()
    {
        CommercePolicy.IsCommerciallyActive(true, [ChannelOfferStatus.INACTIVE, ChannelOfferStatus.ACTIVE]).Should().BeTrue();
        CommercePolicy.IsCommerciallyActive(false, [ChannelOfferStatus.ACTIVE]).Should().BeFalse();
        CommercePolicy.IsCommerciallyActive(true, [ChannelOfferStatus.INACTIVE]).Should().BeFalse();
    }

    [Fact]
    public void Marketplace_sales_coverage_is_partial_with_known_sales_and_unknown_without_them()
    {
        CommercePolicy.ResolveSalesCoverage(false, true).Should().Be(SalesMetricCoverage.PARTIAL);
        CommercePolicy.ResolveSalesCoverage(false, false).Should().Be(SalesMetricCoverage.UNKNOWN);
        CommercePolicy.ResolveSalesCoverage(true, false).Should().Be(SalesMetricCoverage.COMPLETE);
    }

    [Fact]
    public void Listing_allows_product_only_link_and_rejects_mismatched_offer()
    {
        var product = Guid.NewGuid(); var channel = Guid.NewGuid();
        var listing = new MarketplaceListing(Guid.NewGuid(), "listing-1", null, MarketplaceListingStatus.ACTIVE);
        listing.Link(product, null, null, null, channel);
        listing.LinkageState.Should().Be(MarketplaceLinkageState.LINKED);
        listing.ProductId.Should().Be(product); listing.ChannelOfferId.Should().BeNull();
        var act = () => listing.Link(product, Guid.NewGuid(), Guid.NewGuid(), channel, channel);
        act.Should().Throw<ArgumentException>().WithMessage("LISTING_OFFER_MISMATCH");
    }

    [Fact]
    public void Listing_review_and_unlink_states_clear_every_local_link()
    {
        var listing = new MarketplaceListing(Guid.NewGuid(), "listing-1", null, MarketplaceListingStatus.ACTIVE);
        listing.Link(Guid.NewGuid(), null, null, null, Guid.NewGuid());

        listing.MarkNeedsReview();
        listing.LinkageState.Should().Be(MarketplaceLinkageState.NEEDS_REVIEW);
        listing.ProductId.Should().BeNull();
        listing.ChannelOfferId.Should().BeNull();

        listing.Unlink();
        listing.LinkageState.Should().Be(MarketplaceLinkageState.UNLINKED);
        listing.ProductId.Should().BeNull();
        listing.ChannelOfferId.Should().BeNull();
    }

    [Fact]
    public void Observations_are_key_idempotent_and_only_suppress_consecutive_fingerprints()
    {
        var listing = new MarketplaceListing(Guid.NewGuid(), "listing-1", null, MarketplaceListingStatus.ACTIVE);
        var now = DateTimeOffset.UtcNow;
        listing.AddObservation("a", "one", ObservationProvenance.MANUAL, now, now).Should().BeTrue();
        listing.AddObservation("a", "two", ObservationProvenance.MANUAL, now, now).Should().BeFalse();
        listing.AddObservation("b", "one", ObservationProvenance.MANUAL, now, now).Should().BeFalse();
        listing.AddObservation("c", "two", ObservationProvenance.MANUAL, now, now).Should().BeTrue();
        listing.AddObservation("d", "one", ObservationProvenance.MANUAL, now, now).Should().BeTrue();
        listing.Observations.Skip(1).Select(x => x.Fingerprint).Should().Equal("one", "two", "one");
    }

    [Fact]
    public void Commercial_profile_enforces_one_primary_and_no_duplicate_asset()
    {
        var profile = new ProductCommercialProfile(Guid.NewGuid()); var asset = Guid.NewGuid();
        profile.AddImage(asset, CommercialImageRole.PRIMARY, 0, null);
        profile.Invoking(x => x.AddImage(asset, CommercialImageRole.GALLERY, 1, null)).Should().Throw<ArgumentException>();
        profile.Invoking(x => x.AddImage(Guid.NewGuid(), CommercialImageRole.PRIMARY, 1, null)).Should().Throw<ArgumentException>().WithMessage("COMMERCIAL_IMAGE_PRIMARY_EXISTS");
    }

    [Fact]
    public void Commercial_profile_rejects_negative_image_order_and_deduplicates_tags()
    {
        var profile = new ProductCommercialProfile(Guid.NewGuid());
        profile.Invoking(x => x.AddImage(Guid.NewGuid(), CommercialImageRole.GALLERY, -1, null))
            .Should().Throw<ArgumentException>().WithMessage("COMMERCIAL_IMAGE_SORT_ORDER_INVALID");

        var tag = Guid.NewGuid();
        profile.AssignTag(tag);
        profile.AssignTag(tag);
        profile.Tags.Should().ContainSingle(x => x.CommercialTagId == tag);
        profile.RemoveTag(tag);
        profile.Tags.Should().BeEmpty();
    }
}
