using FluentAssertions;
using Verce.Modules.Commerce;

namespace Verce.Commerce.Tests;

/// <summary>M060 / F03 runtime twin: <c>latest_observation_fingerprint</c> is <c>char(64)</c>, the S8B manual-observation contract allows a 1..128 character fingerprint.</summary>
public sealed class MarketplaceListingFingerprintMarkerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T12:00:00Z");
    private static MarketplaceListing NewListing() => new(Guid.NewGuid(), "MLB-FP", null, MarketplaceListingStatus.ACTIVE);

    [Theory]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(65)]
    [InlineData(100)]
    [InlineData(128)]
    public void A_fingerprint_that_is_not_exactly_64_characters_is_kept_whole_on_the_observation_and_is_never_the_latest_marker(int length)
    {
        var listing = NewListing(); var fingerprint = new string('f', length);
        listing.ApplyObservation("manual-" + length, fingerprint, ObservationProvenance.MANUAL, Now, Now, MarketplaceListingStatus.PAUSED, 9m).Should().BeTrue();
        listing.LatestObservationFingerprint.Should().BeNull("only a 64-character value can live in the char(64) column; never truncated, never padded");
        listing.Observations.Should().Contain(o => o.Fingerprint == fingerprint, "the observation (varchar(128)) keeps the complete client value");
    }

    [Fact]
    public void A_64_character_fingerprint_is_the_marker_and_dedupes_an_identical_following_observation()
    {
        var listing = NewListing(); var fingerprint = new string('a', 64);
        listing.ApplyObservation("k1", fingerprint, ObservationProvenance.MANUAL, Now, Now, MarketplaceListingStatus.PAUSED, 9m).Should().BeTrue();
        listing.LatestObservationFingerprint.Should().Be(fingerprint);
        listing.ApplyObservation("k2", fingerprint, ObservationProvenance.MANUAL, Now.AddMinutes(1), Now.AddMinutes(1), MarketplaceListingStatus.PAUSED, 9m).Should().BeFalse("same fingerprint as the latest: no new observation");
    }

    [Fact]
    public void The_same_observation_key_with_a_different_fingerprint_is_still_a_conflict_whatever_the_fingerprint_length()
    {
        var listing = NewListing();
        listing.ApplyObservation("k", new string('f', 100), ObservationProvenance.MANUAL, Now, Now, MarketplaceListingStatus.PAUSED, 9m);
        var act = () => listing.ApplyObservation("k", new string('e', 100), ObservationProvenance.MANUAL, Now, Now, MarketplaceListingStatus.PAUSED, 9m);
        act.Should().Throw<ArgumentException>().WithMessage("LISTING_OBSERVATION_KEY_CONFLICT");
        listing.ApplyObservation("k", new string('f', 100), ObservationProvenance.MANUAL, Now, Now, MarketplaceListingStatus.PAUSED, 9m).Should().BeFalse("replaying the same key and fingerprint is idempotent");
    }

    [Fact]
    public void A_non_64_character_fingerprint_keeps_the_S8B_consecutive_duplicate_suppression()
    {
        var listing = NewListing(); var a = new string('a', 100); var b = new string('b', 100);
        listing.ApplyObservation("k1", a, ObservationProvenance.MANUAL, Now, Now, MarketplaceListingStatus.PAUSED, 9m).Should().BeTrue();
        listing.ApplyObservation("k2", a, ObservationProvenance.MANUAL, Now, Now, MarketplaceListingStatus.PAUSED, 9m).Should().BeFalse("consecutive identical fingerprint: S8B rule");
        listing.ApplyObservation("k3", b, ObservationProvenance.MANUAL, Now, Now, MarketplaceListingStatus.PAUSED, 9m).Should().BeTrue();
        listing.ApplyObservation("k4", a, ObservationProvenance.MANUAL, Now, Now, MarketplaceListingStatus.PAUSED, 9m).Should().BeTrue("A, B, A is three observations");
        listing.Observations.Skip(1).Select(o => o.Fingerprint).Should().Equal(a, b, a);   // Skip(1): the constructor's own initial observation
    }
}
