using FluentAssertions;
using Verce.Modules.Commerce;

namespace Verce.Commerce.Tests;

public sealed class MarketplaceListingB2DomainTests
{
    [Fact]
    public void Fingerprint_v2_is_stable_for_equal_facts_and_changes_for_material_state()
    {
        var baseline = Facts(price: 42.50m, title: "Base");
        MarketplaceListingFingerprint.ComputeV2(baseline).Should().Be(MarketplaceListingFingerprint.ComputeV2(baseline with { ProviderObservedAt = DateTimeOffset.UtcNow.AddDays(1), ListingUrl = "https://different.example/listing" }));
        MarketplaceListingFingerprint.ComputeV2(baseline).Should().NotBe(MarketplaceListingFingerprint.ComputeV2(baseline with { ObservedPrice = 43m }));
    }

    [Fact]
    public void Provider_import_appends_only_on_material_change_and_does_not_overwrite_manual_authority()
    {
        var account = Guid.NewGuid(); var run1 = Guid.NewGuid(); var run2 = Guid.NewGuid(); var now = DateTimeOffset.Parse("2026-09-28T12:00:00Z");
        var listing = MarketplaceListing.ImportFromProvider(account, Facts(), run1, new(MarketplaceLinkageState.UNLINKED, null, null), now);
        listing.Observations.Should().ContainSingle();
        listing.ApplyProviderFacts(Facts() with { ProviderObservedAt = now.AddHours(1) }, run2, now.AddHours(1)).Should().BeFalse();
        listing.Observations.Should().ContainSingle();
        listing.Link(Guid.NewGuid(), null, null, null, Guid.NewGuid());
        listing.ApplyProviderFacts(Facts(title: "Changed"), run2, now.AddHours(2)).Should().BeTrue();
        listing.LinkageSource.Should().Be(MarketplaceListingLinkageSource.MANUAL);
        listing.Observations.Should().HaveCount(2);
    }

    private static NormalizedListingFacts Facts(decimal? price = 42.50m, string? title = "Base") =>
        new("MLB-1", "SKU-1", title, price, MarketplaceListingStatus.ACTIVE, "active", 0, "https://example.test/MLB-1", DateTimeOffset.Parse("2026-09-28T10:00:00Z"), []);
}
