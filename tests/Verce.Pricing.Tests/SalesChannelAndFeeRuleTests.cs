using FluentAssertions;
using Verce.Modules.Pricing;

namespace Verce.Pricing.Tests;

public class SalesChannelTests
{
    [Fact]
    public void Code_is_normalized_to_uppercase()
    {
        var channel = new SalesChannel("shopz", "ShopZ", SalesChannelKind.Marketplace, null, null);
        channel.Code.Should().Be("SHOPZ");
        channel.Active.Should().BeTrue();
    }

    [Fact]
    public void Invalid_code_characters_are_rejected()
    {
        var act = () => new SalesChannel("SHOP Z!", "ShopZ", SalesChannelKind.Marketplace, null, null);
        act.Should().Throw<ArgumentException>().WithMessage("SALES_CHANNEL_CODE_INVALID_CHARACTERS");
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.0)]
    public void Invalid_default_margin_is_rejected(decimal margin)
    {
        var act = () => new SalesChannel("SHOPZ", "ShopZ", SalesChannelKind.Marketplace, margin, null);
        act.Should().Throw<ArgumentException>().WithMessage("PRICING_INVALID_MARGIN");
    }

    [Fact]
    public void Activate_and_deactivate_toggle_state()
    {
        var channel = new SalesChannel("SHOPZ", "ShopZ", SalesChannelKind.Marketplace, null, null);
        channel.Deactivate();
        channel.Active.Should().BeFalse();
        channel.Activate();
        channel.Active.Should().BeTrue();
    }

    [Fact]
    public void DirectChannelCode_identifies_the_seeded_system_channel()
    {
        SalesChannel.DirectChannelCode.Should().Be("DIRECT");
    }

    [Fact]
    public void The_seeded_DIRECT_channel_kind_cannot_be_converted_away_from_Direct()
    {
        var direct = new SalesChannel(SalesChannel.DirectChannelCode, "Venda Direta", SalesChannelKind.Direct, null, null);
        var act = () => direct.UpdateDetails("Venda Direta", SalesChannelKind.Marketplace, null, null);
        act.Should().Throw<ArgumentException>().WithMessage("DIRECT_CHANNEL_KIND_IMMUTABLE");
    }

    [Fact]
    public void The_seeded_DIRECT_channel_can_still_update_name_margin_and_notes()
    {
        var direct = new SalesChannel(SalesChannel.DirectChannelCode, "Venda Direta", SalesChannelKind.Direct, null, null);
        direct.UpdateDetails("Venda Direta (renomeado)", SalesChannelKind.Direct, 0.20m, "nota");
        direct.Name.Should().Be("Venda Direta (renomeado)");
        direct.DefaultMarginPercent.Should().Be(0.20m);
    }

    [Fact]
    public void Creating_a_non_DIRECT_coded_channel_with_Kind_Direct_is_rejected()
    {
        // Terra N-04: DIRECT identity is a reserved one-to-one Code/Kind pair — no channel other
        // than the canonical "DIRECT" row may ever be Kind=Direct.
        var act = () => new SalesChannel("OTHER", "Other", SalesChannelKind.Direct, null, null);
        act.Should().Throw<ArgumentException>().WithMessage("DIRECT_CHANNEL_IDENTITY_RESERVED");
    }

    [Fact]
    public void Converting_an_existing_Marketplace_channel_to_Kind_Direct_is_rejected()
    {
        var channel = new SalesChannel("OTHER", "Other", SalesChannelKind.Marketplace, null, null);
        var act = () => channel.UpdateDetails("Other", SalesChannelKind.Direct, null, null);
        act.Should().Throw<ArgumentException>().WithMessage("DIRECT_CHANNEL_IDENTITY_RESERVED");
    }

    [Fact]
    public void Marketplace_and_Other_channel_creation_and_updates_remain_unaffected_by_the_Direct_identity_reservation()
    {
        var channel = new SalesChannel("MELI", "Mercado Livre", SalesChannelKind.Marketplace, null, null);
        channel.UpdateDetails("Mercado Livre", SalesChannelKind.Other, null, null);
        channel.Kind.Should().Be(SalesChannelKind.Other);
        channel.UpdateDetails("Mercado Livre", SalesChannelKind.Marketplace, null, null);
        channel.Kind.Should().Be(SalesChannelKind.Marketplace);
    }

    [Fact]
    public void A_non_DIRECT_coded_channel_may_freely_change_kind()
    {
        var channel = new SalesChannel("SHOPZ", "ShopZ", SalesChannelKind.Marketplace, null, null);
        channel.UpdateDetails("ShopZ", SalesChannelKind.Other, null, null);
        channel.Kind.Should().Be(SalesChannelKind.Other);
    }
}

public class FeeRuleTests
{
    [Fact]
    public void Duplicate_or_overlapping_versions_are_a_database_concern_but_construction_still_validates_shape()
    {
        var rule = new FeeRule(Guid.NewGuid(), "Test rule");
        var version = rule.AddVersion(new DateOnly(2026, 1, 1), null, 0.10m, 5m, FixedFeeApplication.PerUnit, null, null, null);
        version.CommissionPercent.Should().Be(0.10m);
        rule.Versions.Should().ContainSingle();
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.0)]
    public void Invalid_commission_percent_is_rejected_at_version_construction(decimal commission)
    {
        var rule = new FeeRule(Guid.NewGuid(), "Test rule");
        var act = () => rule.AddVersion(new DateOnly(2026, 1, 1), null, commission, 5m, FixedFeeApplication.PerUnit, null, null, null);
        act.Should().Throw<ArgumentException>().WithMessage("PRICING_INVALID_COMMISSION");
    }

    [Fact]
    public void Negative_fixed_fee_is_rejected()
    {
        var rule = new FeeRule(Guid.NewGuid(), "Test rule");
        var act = () => rule.AddVersion(new DateOnly(2026, 1, 1), null, 0.10m, -1m, FixedFeeApplication.PerUnit, null, null, null);
        act.Should().Throw<ArgumentException>().WithMessage("FEE_RULE_VERSION_FIXED_FEE_INVALID");
    }

    [Fact]
    public void Minimum_fee_greater_than_maximum_fee_is_rejected()
    {
        var rule = new FeeRule(Guid.NewGuid(), "Test rule");
        var act = () => rule.AddVersion(new DateOnly(2026, 1, 1), null, 0.10m, 5m, FixedFeeApplication.PerUnit, minimumFee: 10m, maximumFee: 5m, notes: null);
        act.Should().Throw<ArgumentException>().WithMessage("FEE_RULE_VERSION_FEE_RANGE_INVALID");
    }

    [Fact]
    public void CloseOpenVersion_sets_ValidUntil_on_the_open_ended_version_only()
    {
        var rule = new FeeRule(Guid.NewGuid(), "Test rule");
        rule.AddVersion(new DateOnly(2026, 1, 1), null, 0.10m, 5m, FixedFeeApplication.PerUnit, null, null, null);
        rule.CloseOpenVersion(new DateOnly(2026, 6, 1));
        rule.Versions.Single().ValidUntil.Should().Be(new DateOnly(2026, 6, 1));
    }

    [Fact]
    public void Closing_a_version_before_its_own_start_is_rejected()
    {
        var rule = new FeeRule(Guid.NewGuid(), "Test rule");
        rule.AddVersion(new DateOnly(2026, 6, 1), null, 0.10m, 5m, FixedFeeApplication.PerUnit, null, null, null);
        var act = () => rule.CloseOpenVersion(new DateOnly(2026, 1, 1));
        act.Should().Throw<ArgumentException>().WithMessage("FEE_RULE_VERSION_INVALID_WINDOW");
    }

    [Fact]
    public void Empty_sales_channel_id_is_rejected()
    {
        var act = () => new FeeRule(Guid.Empty, "Test rule");
        act.Should().Throw<ArgumentException>().WithMessage("FEE_RULE_SALES_CHANNEL_REQUIRED");
    }

    [Fact]
    public void A_Direct_channel_kind_rejects_a_non_zero_commission()
    {
        var rule = new FeeRule(Guid.NewGuid(), "Venda Direta — sem comissão");
        var act = () => rule.AddVersion(new DateOnly(2026, 1, 1), null, 0.10m, 0m, FixedFeeApplication.PerUnit, null, null, null, SalesChannelKind.Direct);
        act.Should().Throw<ArgumentException>().WithMessage("DIRECT_CHANNEL_FEES_NOT_ALLOWED");
    }

    [Fact]
    public void A_Direct_channel_kind_rejects_a_non_zero_fixed_fee()
    {
        var rule = new FeeRule(Guid.NewGuid(), "Venda Direta — sem comissão");
        var act = () => rule.AddVersion(new DateOnly(2026, 1, 1), null, 0m, 5m, FixedFeeApplication.PerUnit, null, null, null, SalesChannelKind.Direct);
        act.Should().Throw<ArgumentException>().WithMessage("DIRECT_CHANNEL_FEES_NOT_ALLOWED");
    }

    [Fact]
    public void A_Direct_channel_kind_accepts_the_mandatory_zero_fee_version()
    {
        var rule = new FeeRule(Guid.NewGuid(), "Venda Direta — sem comissão");
        var version = rule.AddVersion(new DateOnly(2026, 1, 1), null, 0m, 0m, FixedFeeApplication.PerUnit, null, null, null, SalesChannelKind.Direct);
        version.CommissionPercent.Should().Be(0m);
        version.FixedFee.Should().Be(0m);
    }

    [Fact]
    public void A_Marketplace_channel_kind_is_unaffected_by_the_Direct_invariant()
    {
        var rule = new FeeRule(Guid.NewGuid(), "Marketplace rule");
        var version = rule.AddVersion(new DateOnly(2026, 1, 1), null, 0.10m, 5m, FixedFeeApplication.PerUnit, null, null, null, SalesChannelKind.Marketplace);
        version.CommissionPercent.Should().Be(0.10m);
        version.FixedFee.Should().Be(5m);
    }

    [Fact]
    public void Omitting_channelKind_defaults_to_no_restriction_preserving_pre_existing_call_sites()
    {
        var rule = new FeeRule(Guid.NewGuid(), "Test rule");
        var version = rule.AddVersion(new DateOnly(2026, 1, 1), null, 0.10m, 5m, FixedFeeApplication.PerUnit, null, null, null);
        version.CommissionPercent.Should().Be(0.10m);
    }

    // B-03: a PER_ORDER/PerUnit fixed fee is a BRL amount and must carry exact whole-cent
    // precision — a fractional-cent fee (e.g. 1.005) breaks PerOrderFeeAllocator's exact-partition
    // invariant (sum(alloc) == orderFee), so it is rejected at construction time, never
    // silently rounded/truncated. Only NEW versions are affected — no pre-existing row is rewritten.
    [Theory]
    [InlineData(1.00)]
    [InlineData(1.01)]
    [InlineData(0)]
    [InlineData(10.99)]
    public void Fixed_fee_with_exact_cent_precision_is_accepted(decimal fixedFee)
    {
        var rule = new FeeRule(Guid.NewGuid(), "Test rule");
        var version = rule.AddVersion(new DateOnly(2026, 1, 1), null, 0.10m, fixedFee, FixedFeeApplication.PerUnit, null, null, null);
        version.FixedFee.Should().Be(fixedFee);
    }

    [Theory]
    [InlineData(1.005)]
    [InlineData(0.001)]
    [InlineData(2.999)]
    public void Fixed_fee_with_a_fractional_cent_is_rejected(decimal fixedFee)
    {
        var rule = new FeeRule(Guid.NewGuid(), "Test rule");
        var act = () => rule.AddVersion(new DateOnly(2026, 1, 1), null, 0.10m, fixedFee, FixedFeeApplication.PerUnit, null, null, null);
        act.Should().Throw<ArgumentException>().WithMessage("FIXED_FEE_PRECISION_INVALID");
    }

    [Fact]
    public void Minimum_fee_with_a_fractional_cent_is_rejected()
    {
        var rule = new FeeRule(Guid.NewGuid(), "Test rule");
        var act = () => rule.AddVersion(new DateOnly(2026, 1, 1), null, 0.10m, 5m, FixedFeeApplication.PerUnit, minimumFee: 1.005m, maximumFee: null, notes: null);
        act.Should().Throw<ArgumentException>().WithMessage("FIXED_FEE_PRECISION_INVALID");
    }

    [Fact]
    public void Maximum_fee_with_a_fractional_cent_is_rejected()
    {
        var rule = new FeeRule(Guid.NewGuid(), "Test rule");
        var act = () => rule.AddVersion(new DateOnly(2026, 1, 1), null, 0.10m, 5m, FixedFeeApplication.PerUnit, minimumFee: null, maximumFee: 9.995m, notes: null);
        act.Should().Throw<ArgumentException>().WithMessage("FIXED_FEE_PRECISION_INVALID");
    }

    [Fact]
    public void Commission_percent_precision_is_left_untouched_by_the_B03_guard()
    {
        // Commission is a fraction (CLAUDE.md rule 4), not a BRL amount — B-03 explicitly scopes
        // the precision guard to fixed/minimum/maximum fee only.
        var rule = new FeeRule(Guid.NewGuid(), "Test rule");
        var version = rule.AddVersion(new DateOnly(2026, 1, 1), null, 0.123456m, 5m, FixedFeeApplication.PerUnit, null, null, null);
        version.CommissionPercent.Should().Be(0.123456m);
    }

    [Fact]
    public void Negative_fixed_fee_precision_check_never_masks_the_pre_existing_negative_fee_guard()
    {
        var rule = new FeeRule(Guid.NewGuid(), "Test rule");
        var act = () => rule.AddVersion(new DateOnly(2026, 1, 1), null, 0.10m, -1.00m, FixedFeeApplication.PerUnit, null, null, null);
        act.Should().Throw<ArgumentException>().WithMessage("FEE_RULE_VERSION_FIXED_FEE_INVALID");
    }
}
