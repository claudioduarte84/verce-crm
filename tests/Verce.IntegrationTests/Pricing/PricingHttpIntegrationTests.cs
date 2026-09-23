using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verce.Api.Catalog;
using Verce.Api.Inventory;
using Verce.Api.Pricing;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Inventory;
using Verce.Modules.Pricing;
using Verce.Platform.Identity;

namespace Verce.IntegrationTests.Pricing;

/// <summary>
/// S5 mission §55: Pricing HTTP surface — sales channels, versioned fee rules (temporal,
/// non-overlapping), Direct/Marketplace pricing formulas, fee-version resolution, authorization
/// (PricingManage is Owner-only per the mission's matrix) and CSRF.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PricingHttpIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;

    public PricingHttpIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE catalog.product_recipe_material_line, catalog.product_recipe_additional_cost_line,
                           catalog.product_recipe, catalog.product,
                           pricing.fee_rule_version, pricing.fee_rule, pricing.sales_channel,
                           inventory.inventory_movement, inventory.supply, settings.app_setting,
                           platform.account_setup_token, platform.user_role, platform.user_claim,
                           platform.user_login, platform.user_token, platform.audit_log, platform."user"
            RESTART IDENTITY CASCADE;
            """);
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, new Dictionary<string, string?>
        {
            ["Settings:SeedOnStartup"] = "true",
        });
        using var warmup = _factory.CreateHttpsClient();
        (await warmup.GetAsync("/health/live")).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Startup_seeds_the_mandatory_zero_fee_Direct_channel()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var channels = await ReadAsync<IReadOnlyList<SalesChannelResponse>>(await owner.GetAsync("/api/pricing/channels"));
        var direct = channels.Should().ContainSingle(x => x.Code == "DIRECT").Which;
        direct.Kind.Should().Be(SalesChannelKind.Direct);

        var feeRule = await ReadAsync<FeeRuleResponse>(await owner.GetAsync($"/api/pricing/channels/{direct.Id}/fee-rule"));
        feeRule.Versions.Should().ContainSingle();
        feeRule.Versions[0].CommissionPercent.Should().Be(0m);
        feeRule.Versions[0].FixedFee.Should().Be(0m);
        feeRule.Versions[0].ValidUntil.Should().BeNull();
    }

    [Fact]
    public async Task Owner_can_manage_channels_but_Operator_and_Viewer_are_denied_writes()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var (operatorClient, _) = await LoggedInAsAsync(Roles.Operator);
        var (viewer, _) = await LoggedInAsAsync(Roles.Viewer);

        foreach (var client in new[] { owner, operatorClient, viewer })
        {
            (await client.GetAsync("/api/pricing/channels")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.PostAsync("/api/pricing/calculate", new PricingCalculateRequest(80m, 0m, 0m, 0.2m, PriceRoundingPolicy.NONE, null, null)))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await owner.PostAsync("/api/pricing/channels", new SalesChannelCreateRequest("SHOPEE", "Shopee", SalesChannelKind.Marketplace, null, null)))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await operatorClient.PostAsync("/api/pricing/channels", new SalesChannelCreateRequest("MELI", "Mercado Livre", SalesChannelKind.Marketplace, null, null)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsync("/api/pricing/channels", new SalesChannelCreateRequest("AMAZON", "Amazon", SalesChannelKind.Marketplace, null, null)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var anonymous = _factory.CreateHttpsClient();
        (await anonymous.GetAsync("/api/pricing/channels")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync("/api/pricing/calculate", new PricingCalculateRequest(80m, 0m, 0m, 0.2m, PriceRoundingPolicy.NONE, null, null)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Calculate_without_antiforgery_is_denied()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var response = await owner.PostAsync("/api/pricing/calculate",
            new PricingCalculateRequest(80m, 0m, 0m, 0.2m, PriceRoundingPolicy.NONE, null, null), withAntiforgery: false);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Ad_hoc_calculate_matches_the_direct_and_marketplace_golden_formulas()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);

        var direct = await ReadAsync<PricingCalculateResponse>(await owner.PostAsync("/api/pricing/calculate",
            new PricingCalculateRequest(80m, 0m, 0m, 0.20m, PriceRoundingPolicy.NONE, null, null)));
        direct.Denominator.Should().Be(0.80m);
        direct.SuggestedPrice.Should().Be(100m);

        var marketplace = await ReadAsync<PricingCalculateResponse>(await owner.PostAsync("/api/pricing/calculate",
            new PricingCalculateRequest(70m, 0.10m, 5m, 0.15m, PriceRoundingPolicy.NONE, null, null)));
        marketplace.Denominator.Should().Be(0.75m);
        marketplace.SuggestedPrice.Should().Be(100m);
        marketplace.CommissionAmount.Should().Be(10m);
    }

    [Fact]
    public async Task Invalid_denominator_returns_a_stable_unprocessable_error()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var response = await owner.PostAsync("/api/pricing/calculate", new PricingCalculateRequest(80m, 0.60m, 0m, 0.45m, PriceRoundingPolicy.NONE, null, null));
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(response)).Should().Be("PRICING_INVALID_DENOMINATOR");
    }

    [Fact]
    public async Task NINETY_NINE_rounding_never_returns_a_price_below_the_raw_value()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        // cost=40.995, commission=0, margin=0 => rawPrice = 40.995 exactly. Terra B-01's original
        // bug (Math.Floor(raw) + 0.99) returned 40.99 here — BELOW the raw price.
        var result = await ReadAsync<PricingCalculateResponse>(await owner.PostAsync("/api/pricing/calculate",
            new PricingCalculateRequest(40.995m, 0m, 0m, 0m, PriceRoundingPolicy.NINETY_NINE, null, null)));
        result.SuggestedPrice.Should().Be(41.99m);
        result.SuggestedPrice.Should().BeGreaterThanOrEqualTo(result.RawPrice);
    }

    [Fact]
    public async Task DIRECT_channel_cannot_gain_a_non_zero_commission_or_fixed_fee()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var channels = await ReadAsync<IReadOnlyList<SalesChannelResponse>>(await owner.GetAsync("/api/pricing/channels"));
        var direct = channels.Single(x => x.Code == "DIRECT");

        var nonZeroCommission = await owner.PostAsync($"/api/pricing/channels/{direct.Id}/fee-rule/versions", new FeeRuleVersionCreateRequest(
            new DateOnly(2030, 1, 1), null, 0.10m, 0m, FixedFeeApplication.PerUnit, null, null, null, false));
        nonZeroCommission.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(nonZeroCommission)).Should().Be("DIRECT_CHANNEL_FEES_NOT_ALLOWED");

        var nonZeroFixedFee = await owner.PostAsync($"/api/pricing/channels/{direct.Id}/fee-rule/versions", new FeeRuleVersionCreateRequest(
            new DateOnly(2030, 1, 1), null, 0m, 5m, FixedFeeApplication.PerUnit, null, null, null, false));
        nonZeroFixedFee.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(nonZeroFixedFee)).Should().Be("DIRECT_CHANNEL_FEES_NOT_ALLOWED");

        var nonZeroBracketFee = await owner.PostAsync($"/api/pricing/channels/{direct.Id}/fee-rule/versions", new FeeRuleVersionCreateRequest(
            new DateOnly(2030, 1, 1), null, 0m, 0m, FixedFeeApplication.PerUnit, null, null, null, false,
            [new PriceBracketCreateRequest(0m, null, 0.01m, 0m, null, null, 1)]));
        nonZeroBracketFee.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(nonZeroBracketFee)).Should().Be("DIRECT_CHANNEL_FEES_NOT_ALLOWED");

        // A zero-fee version remains legal — the mandatory seeded shape itself. The seeded
        // channel already has an open-ended zero-fee version from 2020-01-01, so this closes it
        // first (any two open-ended ranges would otherwise always overlap regardless of values).
        (await owner.PostAsync($"/api/pricing/channels/{direct.Id}/fee-rule/versions", new FeeRuleVersionCreateRequest(
            new DateOnly(2030, 1, 1), null, 0m, 0m, FixedFeeApplication.PerUnit, null, null, null, CloseCurrentOpenVersion: true)))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Price_brackets_persist_against_their_fee_rule_version_and_real_PostgreSQL_rejects_overlap()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var channel = await ReadAsync<SalesChannelResponse>(await owner.PostAsync("/api/pricing/channels",
            new SalesChannelCreateRequest("BRACKET-PG", "Bracket PostgreSQL", SalesChannelKind.Marketplace, null, null)));
        (await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule", new FeeRuleCreateRequest("Regra por faixa"))).StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule/versions", new FeeRuleVersionCreateRequest(
            new DateOnly(2020, 1, 1), null, 0m, 0m, FixedFeeApplication.PerUnit, null, null, null, false,
            [new PriceBracketCreateRequest(0m, 100m, 0.10m, 1m, null, null, 1),
             new PriceBracketCreateRequest(100m, null, 0.20m, 2m, null, null, 2)]));
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        await using var db = _fixture.CreateContext();
        var rule = await db.Set<FeeRule>().AsNoTracking().Include(x => x.Versions).ThenInclude(x => x.Brackets)
            .SingleAsync(x => x.SalesChannelId == channel.Id);
        var version = rule.Versions.Should().ContainSingle().Which;
        version.Brackets.Should().HaveCount(2);
        version.Brackets.Should().OnlyContain(x => x.FeeRuleVersionId == version.Id);

        var overlap = await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule/versions", new FeeRuleVersionCreateRequest(
            new DateOnly(2030, 1, 1), null, 0m, 0m, FixedFeeApplication.PerUnit, null, null, null, true,
            [new PriceBracketCreateRequest(0m, 100m, 0.10m, 1m, null, null, 1),
             new PriceBracketCreateRequest(90m, null, 0.20m, 2m, null, null, 2)]));
        overlap.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(overlap)).Should().Be("FEE_RULE_VERSION_OVERLAPS_EXISTING");
    }

    [Fact]
    public async Task DIRECT_channel_kind_cannot_be_converted_to_Marketplace()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var channels = await ReadAsync<IReadOnlyList<SalesChannelResponse>>(await owner.GetAsync("/api/pricing/channels"));
        var direct = channels.Single(x => x.Code == "DIRECT");

        var response = await owner.PutAsync($"/api/pricing/channels/{direct.Id}",
            new SalesChannelUpdateRequest(direct.Name, SalesChannelKind.Marketplace, null, null, direct.Version));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(response)).Should().Be("DIRECT_CHANNEL_KIND_IMMUTABLE");
    }

    [Fact]
    public async Task Creating_a_non_DIRECT_coded_channel_with_Kind_Direct_is_rejected()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var response = await owner.PostAsync("/api/pricing/channels", new SalesChannelCreateRequest("OTHERCH", "Other", SalesChannelKind.Direct, null, null));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(response)).Should().Be("DIRECT_CHANNEL_IDENTITY_RESERVED");
    }

    [Fact]
    public async Task Converting_an_existing_Marketplace_channel_to_Kind_Direct_is_rejected()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var channel = await ReadAsync<SalesChannelResponse>(await owner.PostAsync("/api/pricing/channels",
            new SalesChannelCreateRequest("OTHERCH2", "Other 2", SalesChannelKind.Marketplace, null, null)));
        var response = await owner.PutAsync($"/api/pricing/channels/{channel.Id}",
            new SalesChannelUpdateRequest(channel.Name, SalesChannelKind.Direct, null, null, channel.Version));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(response)).Should().Be("DIRECT_CHANNEL_IDENTITY_RESERVED");
    }

    [Fact]
    public async Task Marketplace_channel_creation_and_kind_updates_remain_unaffected_by_the_Direct_identity_reservation()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var channel = await ReadAsync<SalesChannelResponse>(await owner.PostAsync("/api/pricing/channels",
            new SalesChannelCreateRequest("OTHERCH3", "Other 3", SalesChannelKind.Marketplace, null, null)));
        var updated = await owner.PutAsync($"/api/pricing/channels/{channel.Id}",
            new SalesChannelUpdateRequest(channel.Name, SalesChannelKind.Other, null, null, channel.Version));
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync<SalesChannelResponse>(updated)).Kind.Should().Be(SalesChannelKind.Other);
    }

    [Fact]
    public async Task DIRECT_pricing_formula_remains_cost_over_one_minus_margin_and_Marketplace_is_unaffected()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var supply = await CreateSupplyAsync(owner, "MAT-DIRFORM-S5", "Fórmula direta");
        supply = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(1000m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, 100m, null, "NF-1", null, supply.Version)));
        var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("DIRFORM-PROD", "Produto", null)));
        product = await ReadAsync<ProductResponse>(await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, null,
            [new ProductRecipeMaterialLineRequest(supply.Id, 100m, SupplyBaseUnit.Gram, null, null)], [], product.Version)));

        var channels = await ReadAsync<IReadOnlyList<SalesChannelResponse>>(await owner.GetAsync("/api/pricing/channels"));
        var direct = channels.Single(x => x.Code == "DIRECT");
        var directPrice = await ReadAsync<ProductPriceResponse>(await owner.PostAsync($"/api/pricing/products/{product.Id}/price", new ProductPriceRequest(direct.Id, 0.35m)));
        directPrice.CommissionPercent.Should().Be(0m);
        directPrice.FixedFee.Should().Be(0m);
        directPrice.Denominator.Should().Be(0.65m);
        directPrice.SuggestedPrice.Should().Be(15.38m, "10 / (1 - 0.35) = 15.3846... rounded CENT");

        var marketplace = await ReadAsync<SalesChannelResponse>(await owner.PostAsync("/api/pricing/channels",
            new SalesChannelCreateRequest("DIRFORM-MKT", "Marketplace", SalesChannelKind.Marketplace, null, null)));
        await owner.PostAsync($"/api/pricing/channels/{marketplace.Id}/fee-rule", new FeeRuleCreateRequest("Regra"));
        await owner.PostAsync($"/api/pricing/channels/{marketplace.Id}/fee-rule/versions", new FeeRuleVersionCreateRequest(
            new DateOnly(2020, 1, 1), null, 0.10m, 5m, FixedFeeApplication.PerUnit, null, null, null, false));
        var marketplacePrice = await ReadAsync<ProductPriceResponse>(await owner.PostAsync($"/api/pricing/products/{product.Id}/price", new ProductPriceRequest(marketplace.Id, 0.35m)));
        marketplacePrice.CommissionPercent.Should().Be(0.10m);
        marketplacePrice.FixedFee.Should().Be(5m);
        marketplacePrice.SuggestedPrice.Should().NotBe(directPrice.SuggestedPrice, "the DIRECT invariant must not have flattened the marketplace formula too");
    }

    [Fact]
    public async Task Channel_update_with_stale_version_is_rejected()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var channel = await ReadAsync<SalesChannelResponse>(await owner.PostAsync("/api/pricing/channels",
            new SalesChannelCreateRequest("STALE-CH", "Canal", SalesChannelKind.Marketplace, null, null)));
        (await owner.PutAsync($"/api/pricing/channels/{channel.Id}", new SalesChannelUpdateRequest("Canal renomeado", channel.Kind, null, null, channel.Version)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var stale = await owner.PutAsync($"/api/pricing/channels/{channel.Id}",
            new SalesChannelUpdateRequest("Outra renomeação", channel.Kind, null, null, channel.Version));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Overlapping_fee_rule_versions_are_rejected_by_the_temporal_exclusion_constraint()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var channel = await ReadAsync<SalesChannelResponse>(await owner.PostAsync("/api/pricing/channels",
            new SalesChannelCreateRequest("OVERLAP-CH", "Canal com sobreposição", SalesChannelKind.Marketplace, null, null)));
        await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule", new FeeRuleCreateRequest("Regra padrão"));
        (await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule/versions", new FeeRuleVersionCreateRequest(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 1), 0.10m, 5m, FixedFeeApplication.PerUnit, null, null, null, false)))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var overlap = await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule/versions", new FeeRuleVersionCreateRequest(
            new DateOnly(2026, 3, 1), null, 0.12m, 6m, FixedFeeApplication.PerUnit, null, null, null, false));
        overlap.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(overlap)).Should().Be("FEE_RULE_VERSION_OVERLAPS_EXISTING");

        (await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule/versions", new FeeRuleVersionCreateRequest(
            new DateOnly(2026, 6, 1), null, 0.12m, 6m, FixedFeeApplication.PerUnit, null, null, null, false)))
            .StatusCode.Should().Be(HttpStatusCode.Created, "a version starting exactly when the prior one ends does not overlap ([valid_from, valid_until) semantics)");
    }

    [Fact]
    public async Task Product_price_end_to_end_resolves_the_fee_version_valid_today_and_reuses_the_persisted_recipe_cost()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var supply = await CreateSupplyAsync(owner, "MAT-PRICE-S5", "Material para preço");
        supply = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(1000m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, 100m, null, "NF-1", null, supply.Version)));

        var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("PRICE-PROD", "Produto precificado", null)));
        product = await ReadAsync<ProductResponse>(await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, null,
            [new ProductRecipeMaterialLineRequest(supply.Id, 100m, SupplyBaseUnit.Gram, null, null)], [], product.Version)));

        var channel = await ReadAsync<SalesChannelResponse>(await owner.PostAsync("/api/pricing/channels",
            new SalesChannelCreateRequest("MKT-E2E", "Marketplace E2E", SalesChannelKind.Marketplace, null, null)));
        await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule", new FeeRuleCreateRequest("Regra padrão"));
        await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule/versions", new FeeRuleVersionCreateRequest(
            new DateOnly(2020, 1, 1), null, 0.10m, 5m, FixedFeeApplication.PerUnit, null, null, null, false));

        var priceResponse = await owner.PostAsync($"/api/pricing/products/{product.Id}/price", new ProductPriceRequest(channel.Id, 0.35m));
        priceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var price = await ReadAsync<ProductPriceResponse>(priceResponse);

        // Cost = 100g @ 0.10/g weighted average, no wastage override = 10.00. (10 + 5) / (1 - 0.45) = 27.2727...
        price.UnitTotalCost.Should().Be(10m);
        price.CommissionPercent.Should().Be(0.10m);
        price.FixedFee.Should().Be(5m);

        // Terra B-03: the full self-describing snapshot contract — every identity/driver a
        // future S6 QuoteRevision would need to freeze this exact decision without re-deriving it.
        price.ProductId.Should().Be(product.Id);
        price.ProductCode.Should().Be("PRICE-PROD");
        price.ProductName.Should().Be("Produto precificado");
        price.SalesChannelId.Should().Be(channel.Id);
        price.SalesChannelCode.Should().Be("MKT-E2E");
        price.SalesChannelKind.Should().Be(SalesChannelKind.Marketplace);
        price.SalesChannelName.Should().Be("Marketplace E2E");
        price.DesiredMargin.Should().Be(0.35m);
        var feeRule = await ReadAsync<FeeRuleResponse>(await owner.GetAsync($"/api/pricing/channels/{channel.Id}/fee-rule"));
        price.FeeRuleId.Should().Be(feeRule.Id);
        price.FeeRuleVersionId.Should().Be(feeRule.Versions.Single().Id);
        // The exact bug class Terra B-02 fixed in production, avoided here in the assertion
        // itself: compare against the ORGANIZATION (America/Sao_Paulo) date, never the literal
        // UTC date — those two disagree for several hours around each UTC midnight.
        price.OrganizationDate.Should().Be(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById(Verce.SharedKernel.Time.SystemClock.DefaultOrganizationTimeZoneId)).DateTime));
        price.RoundingPolicy.Should().Be(PriceRoundingPolicy.CENT);
        price.Denominator.Should().Be(0.55m);
        price.RawPrice.Should().BeApproximately(27.2727m, 0.001m);
        price.SuggestedPrice.Should().Be(27.27m);
        price.CommissionAmount.Should().Be(2.73m, "10% of the suggested price (27.27), rounded to Money precision");
        price.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Product_price_snapshot_stays_self_describing_across_a_later_fee_version()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var supply = await CreateSupplyAsync(owner, "MAT-SNAP-S5", "Material snapshot");
        supply = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(1000m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, 100m, null, "NF-1", null, supply.Version)));
        var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("SNAP-PROD", "Snapshot", null)));
        product = await ReadAsync<ProductResponse>(await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, null,
            [new ProductRecipeMaterialLineRequest(supply.Id, 100m, SupplyBaseUnit.Gram, null, null)], [], product.Version)));

        var channel = await ReadAsync<SalesChannelResponse>(await owner.PostAsync("/api/pricing/channels",
            new SalesChannelCreateRequest("SNAP-CH", "Canal snapshot", SalesChannelKind.Marketplace, null, null)));
        await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule", new FeeRuleCreateRequest("Regra"));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule/versions", new FeeRuleVersionCreateRequest(
            today.AddYears(-1), today.AddDays(30), 0.10m, 5m, FixedFeeApplication.PerUnit, null, null, "V1", false));

        var v1Price = await ReadAsync<ProductPriceResponse>(await owner.PostAsync($"/api/pricing/products/{product.Id}/price", new ProductPriceRequest(channel.Id, 0.30m)));
        v1Price.CommissionPercent.Should().Be(0.10m);
        var v1FeeRule = await ReadAsync<FeeRuleResponse>(await owner.GetAsync($"/api/pricing/channels/{channel.Id}/fee-rule"));
        v1Price.FeeRuleVersionId.Should().Be(v1FeeRule.Versions.Single().Id);

        // A later version is added, effective from today+30 — the ALREADY-RETURNED v1Price
        // object remains self-describing (it named its own FeeRuleVersion.Id), proving S6 has a
        // valid snapshot source: a later fee change never silently reinterprets a past response.
        await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule/versions", new FeeRuleVersionCreateRequest(
            today.AddDays(30), null, 0.15m, 8m, FixedFeeApplication.PerUnit, null, null, "V2", false));
        v1Price.CommissionPercent.Should().Be(0.10m, "the earlier response object is never mutated by a later fee-rule write");
        v1Price.FeeRuleVersionId.Should().Be(v1FeeRule.Versions.Single().Id);

        // A request still effective under V1's window (today) continues to resolve V1.
        var v1Again = await ReadAsync<ProductPriceResponse>(await owner.PostAsync($"/api/pricing/products/{product.Id}/price", new ProductPriceRequest(channel.Id, 0.30m)));
        v1Again.FeeRuleVersionId.Should().Be(v1Price.FeeRuleVersionId);
        v1Again.CommissionPercent.Should().Be(0.10m);
    }

    [Fact]
    public async Task Product_price_with_no_fee_rule_covering_today_is_rejected()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var supply = await CreateSupplyAsync(owner, "MAT-NOFEE-S5", "Sem regra vigente");
        supply = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(1000m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, 100m, null, "NF-1", null, supply.Version)));
        var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("NOFEE-PROD", "Sem regra", null)));
        product = await ReadAsync<ProductResponse>(await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, null,
            [new ProductRecipeMaterialLineRequest(supply.Id, 10m, SupplyBaseUnit.Gram, null, null)], [], product.Version)));

        var channel = await ReadAsync<SalesChannelResponse>(await owner.PostAsync("/api/pricing/channels",
            new SalesChannelCreateRequest("NOFEE-CH", "Canal sem regra futura", SalesChannelKind.Marketplace, null, null)));
        await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule", new FeeRuleCreateRequest("Regra futura"));
        await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule/versions", new FeeRuleVersionCreateRequest(
            new DateOnly(2999, 1, 1), null, 0.10m, 5m, FixedFeeApplication.PerUnit, null, null, null, false));

        var priceResponse = await owner.PostAsync($"/api/pricing/products/{product.Id}/price", new ProductPriceRequest(channel.Id, null));
        priceResponse.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(priceResponse)).Should().Be("FEE_RULE_NOT_FOUND");
    }

    [Fact]
    public async Task Product_price_against_an_inactive_product_is_rejected()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("INACTIVE-PRICE", "Inativo", null)));
        (await owner.PostAsync($"/api/products/{product.Id}/deactivate?version={product.Version}", (object?)null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var channels = await ReadAsync<IReadOnlyList<SalesChannelResponse>>(await owner.GetAsync("/api/pricing/channels"));
        var direct = channels.Single(x => x.Code == "DIRECT");

        var priceResponse = await owner.PostAsync($"/api/pricing/products/{product.Id}/price", new ProductPriceRequest(direct.Id, null));
        priceResponse.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(priceResponse)).Should().Be("PRODUCT_INACTIVE");
    }

    private async Task<SupplyResponse> CreateSupplyAsync(AuthTestClient client, string code, string name)
    {
        var response = await client.PostAsync("/api/supplies", new SupplyCreateRequest(code, name, null, "FILAMENT", SupplyBaseUnit.Gram, null, null, null, null));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await ReadAsync<SupplyResponse>(response);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private async Task<(AuthTestClient Client, Guid UserId)> LoggedInAsAsync(string role)
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        const string password = "a-perfectly-fine-12char-password";
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!await roles.RoleExistsAsync(role)) (await roles.CreateAsync(new ApplicationRole(role))).Succeeded.Should().BeTrue();
        var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = role + " S5", IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow };
        (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
        (await users.AddToRoleAsync(user, role)).Succeeded.Should().BeTrue();
        var client = new AuthTestClient(_factory.CreateHttpsClient());
        await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new Verce.Api.Auth.LoginRequest(email, password))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await client.EnsureCsrfCookieAsync();
        return (client, user.Id);
    }
}
