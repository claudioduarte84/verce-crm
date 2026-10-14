using FluentAssertions;
using Verce.Modules.Commerce;

namespace Verce.Commerce.Tests;

/// <summary>S8C.2 B7 (ADR-0025 §D.2/§D.3): the SKU-mapping aggregate lifecycle. Version stays interceptor-owned, so no test mutates it.</summary>
public sealed class MarketplaceAccountSkuMappingDomainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-05T12:00:00Z");

    [Fact]
    public void New_mapping_is_active_account_scoped_and_normalizes_the_sku_exactly_like_the_listing_import()
    {
        var account = Guid.NewGuid(); var product = Guid.NewGuid(); var actor = Guid.NewGuid();
        var mapping = new MarketplaceAccountSkuMapping(account, " é\u0001 ", product, actor, Now);
        mapping.ExternalSku.Should().Be("é", "NFC, trimmed, control characters removed (ADR-0025 §B.3)");
        mapping.ExternalSku.Should().Be(MarketplaceSkuNormalizer.Normalize(" é\u0001 "));
        mapping.Active.Should().BeTrue(); mapping.MarketplaceAccountId.Should().Be(account); mapping.ProductId.Should().Be(product);
        mapping.ConfirmedByUserId.Should().Be(actor); mapping.ConfirmedAt.Should().Be(Now);
        mapping.InvalidatedAt.Should().BeNull(); mapping.InvalidatedByUserId.Should().BeNull(); mapping.InvalidatedReason.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u0001\u0002")]
    public void An_empty_normalized_sku_is_rejected_with_the_frozen_code(string sku)
    {
        var act = () => new MarketplaceAccountSkuMapping(Guid.NewGuid(), sku, Guid.NewGuid(), Guid.NewGuid(), Now);
        act.Should().Throw<ArgumentException>().WithMessage("SKU_MAPPING_SKU_INVALID*");
    }

    [Fact]
    public void A_sku_longer_than_200_characters_after_normalization_is_rejected()
    {
        new MarketplaceAccountSkuMapping(Guid.NewGuid(), new string('A', 200), Guid.NewGuid(), Guid.NewGuid(), Now).ExternalSku.Should().HaveLength(200);
        var act = () => new MarketplaceAccountSkuMapping(Guid.NewGuid(), new string('A', 201), Guid.NewGuid(), Guid.NewGuid(), Now);
        act.Should().Throw<ArgumentException>().WithMessage("SKU_MAPPING_SKU_INVALID*");
    }

    [Fact]
    public void Deactivate_records_the_operator_reason_time_and_actor_and_keeps_the_identity_of_the_authority()
    {
        var mapping = Active(); var operatorId = Guid.NewGuid(); var at = Now.AddMinutes(5);
        mapping.Deactivate(operatorId, at);
        mapping.Active.Should().BeFalse();
        mapping.InvalidatedReason.Should().Be(MarketplaceAccountSkuMappingInvalidatedReason.OPERATOR_DEACTIVATED);
        mapping.InvalidatedAt.Should().Be(at); mapping.InvalidatedByUserId.Should().Be(operatorId);
        mapping.ExternalSku.Should().Be("SKU-1"); mapping.ConfirmedAt.Should().Be(Now);
    }

    [Fact]
    public void Replace_records_the_replacement_reason()
    {
        var mapping = Active(); var operatorId = Guid.NewGuid(); var at = Now.AddMinutes(7);
        mapping.Replace(operatorId, at);
        mapping.Active.Should().BeFalse();
        mapping.InvalidatedReason.Should().Be(MarketplaceAccountSkuMappingInvalidatedReason.REPLACED_BY_NEW_MAPPING);
        mapping.InvalidatedAt.Should().Be(at); mapping.InvalidatedByUserId.Should().Be(operatorId);
    }

    [Fact]
    public void An_invalidated_mapping_is_never_reactivated_or_invalidated_twice()
    {
        var deactivated = Active(); deactivated.Deactivate(Guid.NewGuid(), Now);
        var replaced = Active(); replaced.Replace(Guid.NewGuid(), Now);
        foreach (var mapping in new[] { deactivated, replaced })
        {
            var reason = mapping.InvalidatedReason; var at = mapping.InvalidatedAt;
            ((Action)(() => mapping.Deactivate(Guid.NewGuid(), Now.AddHours(1)))).Should().Throw<InvalidOperationException>().WithMessage("SKU_MAPPING_NOT_ACTIVE");
            ((Action)(() => mapping.Replace(Guid.NewGuid(), Now.AddHours(1)))).Should().Throw<InvalidOperationException>().WithMessage("SKU_MAPPING_NOT_ACTIVE");
            mapping.Active.Should().BeFalse(); mapping.InvalidatedReason.Should().Be(reason); mapping.InvalidatedAt.Should().Be(at);
        }
    }

    [Fact]
    public void An_invalidation_without_an_actor_is_rejected_and_leaves_the_mapping_active()
    {
        var mapping = Active();
        ((Action)(() => mapping.Deactivate(Guid.Empty, Now))).Should().Throw<ArgumentException>().WithMessage("SKU_MAPPING_REFERENCE_REQUIRED*");
        mapping.Active.Should().BeTrue(); mapping.InvalidatedAt.Should().BeNull(); mapping.InvalidatedReason.Should().BeNull();
    }

    [Fact]
    public void Active_flag_and_invalidation_metadata_always_agree_with_the_database_check_constraint()
    {
        // ck_marketplace_account_sku_mapping_active: active = (invalidated_at IS NULL AND invalidated_reason IS NULL)
        var mapping = Active();
        (mapping.Active == (mapping.InvalidatedAt is null && mapping.InvalidatedReason is null)).Should().BeTrue();
        mapping.Deactivate(Guid.NewGuid(), Now);
        (mapping.Active == (mapping.InvalidatedAt is null && mapping.InvalidatedReason is null)).Should().BeTrue();
    }

    private static MarketplaceAccountSkuMapping Active() => new(Guid.NewGuid(), "SKU-1", Guid.NewGuid(), Guid.NewGuid(), Now);
}
