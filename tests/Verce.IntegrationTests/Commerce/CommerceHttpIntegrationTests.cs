using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verce.Api.Auth;
using Verce.Api.Commerce;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Catalog;
using Verce.Modules.Commerce;
using Verce.Modules.Pricing;
using Verce.Modules.Sales;
using Verce.Platform.Identity;

namespace Verce.IntegrationTests.Commerce;

[Collection(PostgresCollection.Name)]
public sealed class CommerceHttpIntegrationTests : IAsyncLifetime
{
    private static int _saleSequence = 700_000;
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;

    public CommerceHttpIntegrationTests(PostgresFixture fixture) => _fixture = fixture;
    public Task InitializeAsync() { _factory = new VerceWebApplicationFactory(_fixture.ConnectionString); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Offer_creation_accepts_the_exact_string_enum_contract_sent_by_the_SPA()
    {
        var product = new Product("P" + Guid.NewGuid().ToString("N")[..20], "Produto oferta", null);
        var channel = NewChannel(SalesChannelKind.Marketplace);
        await using (var db = _fixture.CreateContext())
        {
            db.AddRange(product, channel);
            await db.SaveChangesAsync();
        }

        var owner = await LoggedInOwnerAsync();
        using var document = JsonDocument.Parse($$"""
            {"productId":"{{product.Id}}","salesChannelId":"{{channel.Id}}","intendedUnitPrice":49.9,"priceSource":"MANUAL","sellerPaidShippingAmount":null,"version":null}
            """);
        var response = await owner.PostAsync("/api/commerce/offers", document.RootElement);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        (await _fixture.CreateContext().Set<ChannelOffer>().CountAsync(x =>
            x.ProductId == product.Id && x.SalesChannelId == channel.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Marketplace_account_creation_requires_the_authorization_workflow()
    {
        var owner = await LoggedInOwnerAsync();
        var response = await owner.PostAsync(
            "/api/commerce/marketplace-accounts",
            new MarketplaceAccountWriteRequest("SHOPEE", "forged", Guid.NewGuid(), "Conta forjada", "never-accepted", null));

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await response.Content.ReadAsStringAsync()).Should().Contain("ACCOUNT_CREATION_REQUIRES_AUTHORIZATION");
    }

    [Fact]
    public async Task Offer_activation_rejects_inactive_product_and_inactive_channel()
    {
        var inactiveProduct = new Product("P" + Guid.NewGuid().ToString("N")[..20], "Produto inativo", null); inactiveProduct.Deactivate();
        var activeProduct = new Product("P" + Guid.NewGuid().ToString("N")[..20], "Produto ativo", null);
        var activeChannel = NewChannel(SalesChannelKind.Marketplace);
        var inactiveChannel = NewChannel(SalesChannelKind.Marketplace); inactiveChannel.Deactivate();
        var productOffer = new ChannelOffer(inactiveProduct.Id, activeChannel.Id, 20m, ChannelOfferPriceSource.MANUAL);
        var channelOffer = new ChannelOffer(activeProduct.Id, inactiveChannel.Id, 20m, ChannelOfferPriceSource.MANUAL);
        await using (var db = _fixture.CreateContext())
        {
            db.AddRange(inactiveProduct, activeProduct, activeChannel, inactiveChannel, productOffer, channelOffer);
            await db.SaveChangesAsync();
        }

        var owner = await LoggedInOwnerAsync();
        (await owner.PostAsync($"/api/commerce/offers/{productOffer.Id}/activate?version={productOffer.Version}", new { }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await owner.PostAsync($"/api/commerce/offers/{channelOffer.Id}/activate?version={channelOffer.Version}", new { }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Economics_uses_server_cost_rule_version_and_bracket_and_preserves_unknown_shipping()
    {
        var product = new Product("P" + Guid.NewGuid().ToString("N")[..20], "Produto economia", null);
        product.Recipe.UpdateParameters(null, 60m, 10m, null, null, 1, null);
        var channel = NewChannel(SalesChannelKind.Marketplace);
        var rule = new FeeRule(channel.Id, "Regra economia");
        var version = rule.AddVersion(new DateOnly(2020, 1, 1), null, .05m, .50m,
            FixedFeeApplication.PerUnit, null, null, null, SalesChannelKind.Marketplace,
            [new PriceBracketInput(0m, 100m, .10m, 1m, null, null, 1)]);
        var bracket = version.Brackets.Single();
        await using (var db = _fixture.CreateContext())
        {
            db.AddRange(product, channel, rule);
            await db.SaveChangesAsync();
        }

        var owner = await LoggedInOwnerAsync();
        var forged = new
        {
            productId = product.Id, salesChannelId = channel.Id, intendedUnitPrice = 50m,
            sellerPaidShippingAmount = (decimal?)null, estimatedProductCost = 999999m,
            feeRuleId = Guid.NewGuid(), recommendation = "FORGED"
        };
        var unknownResponse = await owner.PostAsync("/api/commerce/channel-economics/preview", forged);
        var unknownJson = await unknownResponse.Content.ReadAsStringAsync();
        unknownResponse.StatusCode.Should().Be(HttpStatusCode.OK, unknownJson);
        var unknown = JsonSerializer.Deserialize<ChannelEconomicsPreviewResponse>(unknownJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        unknown.EstimatedProductCost.Should().Be(10m, "the persisted labor recipe, not forged client cost, is authoritative");
        unknown.EstimatedCommission.Should().Be(5m);
        unknown.FixedFee.Should().Be(1m);
        unknown.SellerPaidShippingAmount.Should().BeNull();
        unknown.NetRevenue.Should().Be(44m);
        unknown.EstimatedProfit.Should().Be(34m);
        unknown.ContributionMargin.Should().Be(.68m);
        unknown.Markup.Should().Be(4m, "markup is gross price divided by product cost minus one");
        unknown.FeeProvenance.Should().Be("MANUAL");
        unknown.CostCoverage.Should().Be("PARTIAL");
        unknown.EstimateClassification.Should().Be("PARTIAL_ESTIMATE");
        unknown.MissingComponents.Should().Contain("ENERGY_UNAVAILABLE");
        unknown.FeeRuleId.Should().Be(rule.Id);
        unknown.FeeRuleVersionId.Should().Be(version.Id);
        unknown.PriceBracketId.Should().Be(bracket.Id);
        unknownJson.Should().NotContain("LIVE_API").And.NotContain("CACHE").And.NotContain("recommendation");

        var zeroResponse = await owner.PostAsync("/api/commerce/channel-economics/preview",
            new ChannelEconomicsPreviewRequest(product.Id, channel.Id, 50m, 0m));
        var zero = await zeroResponse.Content.ReadFromJsonAsync<ChannelEconomicsPreviewResponse>();
        zero!.SellerPaidShippingAmount.Should().Be(0m);
        zero.EstimatedProfit.Should().Be(unknown.EstimatedProfit);
    }

    [Fact]
    public async Task Economics_allocates_per_order_fixed_fee_over_comparison_quantity()
    {
        var product = new Product("P" + Guid.NewGuid().ToString("N")[..20], "Produto taxa por pedido", null);
        product.Recipe.UpdateParameters(null, 60m, 10m, null, null, 1, null);
        var channel = NewChannel(SalesChannelKind.Marketplace);
        var rule = new FeeRule(channel.Id, "Taxa por pedido");
        var version = rule.AddVersion(new DateOnly(2020, 1, 1), null, 0m, 8m,
            FixedFeeApplication.PerOrder, null, null, null, SalesChannelKind.Marketplace, []);
        await using (var db = _fixture.CreateContext())
        {
            db.AddRange(product, channel, rule);
            await db.SaveChangesAsync();
        }

        var owner = await LoggedInOwnerAsync();
        async Task<ChannelEconomicsPreviewResponse> Preview(int quantity)
        {
            var response = await owner.PostAsync("/api/commerce/channel-economics/preview",
                new ChannelEconomicsPreviewRequest(product.Id, channel.Id, 50m, 0m, quantity));
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            return JsonSerializer.Deserialize<ChannelEconomicsPreviewResponse>(body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }

        var oneUnitOrder = await Preview(1);
        var fourUnitOrder = await Preview(4);

        oneUnitOrder.ComparisonQuantity.Should().Be(1);
        oneUnitOrder.FixedFeeApplication.Should().Be("PER_ORDER");
        oneUnitOrder.FixedFee.Should().Be(8m, "the full Pricing fixed fee applies to a one-unit order");
        oneUnitOrder.EstimatedProfit.Should().Be(32m);
        oneUnitOrder.ContributionMargin.Should().Be(.64m);

        fourUnitOrder.ComparisonQuantity.Should().Be(4);
        fourUnitOrder.FixedFeeApplication.Should().Be("PER_ORDER");
        fourUnitOrder.FixedFee.Should().Be(2m, "the resolved order fee of 8 is allocated over four compared units");
        fourUnitOrder.FixedFee.Should().NotBe(8m);
        fourUnitOrder.EstimatedProductCost.Should().Be(10m);
        fourUnitOrder.EstimatedProfit.Should().Be(38m);
        fourUnitOrder.ContributionMargin.Should().Be(.76m);
        fourUnitOrder.Markup.Should().Be(4m, "markup remains comparison price divided by cost minus one");
        fourUnitOrder.FeeRuleId.Should().Be(rule.Id);
        fourUnitOrder.FeeRuleVersionId.Should().Be(version.Id);
        fourUnitOrder.PriceBracketId.Should().BeNull();
        fourUnitOrder.FeeProvenance.Should().Be("MANUAL");
        fourUnitOrder.FeeObservedAt.Should().BeNull();
        fourUnitOrder.FeeExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task Catalog_composes_commercial_state_sales_coverage_filters_and_pagination_server_side()
    {
        var marker = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        Product Product(string suffix, bool active = true)
        {
            var product = new Product($"{marker}-{suffix}", $"Catálogo {marker} {suffix}", null);
            product.Recipe.UpdateParameters(null, 30m, 10m, null, null, 1, null);
            if (!active) product.Deactivate();
            return product;
        }
        var known = Product("A"); var unknown = Product("B"); var inactive = Product("C", false);
        var channel = NewChannel(SalesChannelKind.Marketplace);
        var knownOffer = new ChannelOffer(known.Id, channel.Id, 50m, ChannelOfferPriceSource.MANUAL); knownOffer.Activate(DateTimeOffset.UtcNow);
        var unknownOffer = new ChannelOffer(unknown.Id, channel.Id, 60m, ChannelOfferPriceSource.MANUAL); unknownOffer.Activate(DateTimeOffset.UtcNow);
        var inactiveOffer = new ChannelOffer(inactive.Id, channel.Id, 70m, ChannelOfferPriceSource.MANUAL); inactiveOffer.Activate(DateTimeOffset.UtcNow);
        var tag = new CommercialTag("TAG-" + marker, "Tag " + marker);
        var profile = new ProductCommercialProfile(known.Id); profile.AssignTag(tag.Id);
        var now = DateTimeOffset.UtcNow;
        var sale = new Sale(Interlocked.Increment(ref _saleSequence), DateOnly.FromDateTime(now.UtcDateTime),
            SaleSource.MANUAL_ENTRY, channel.Id, null, null, null, null, SaleFeeSource.LOCAL_RULE,
            null, null, now, 0m, null, null,
            [new SaleItemSnapshot(known.Id, null, known.Name, 2m, 30m, 0m, 60m, 5m, 10m, 0m, 50m, .833333m)],
            null, now);
        await using (var db = _fixture.CreateContext())
        {
            db.AddRange(known, unknown, inactive, channel, knownOffer, unknownOffer, inactiveOffer, tag, profile, sale);
            await db.SaveChangesAsync();
        }

        var owner = await LoggedInOwnerAsync();
        var page = await (await owner.GetAsync($"/api/commerce/catalog?salesChannelId={channel.Id}&sort=code&page=1&pageSize=2"))
            .Content.ReadFromJsonAsync<CommercialCatalogResponse>();
        page!.Total.Should().Be(3);
        page.Items.Should().HaveCount(2);
        page.Items.Select(x => x.Code).Should().BeInAscendingOrder();

        var all = await (await owner.GetAsync($"/api/commerce/catalog?search={marker}&pageSize=10"))
            .Content.ReadFromJsonAsync<CommercialCatalogResponse>();
        all!.Items.Should().HaveCount(3);
        var knownRow = all.Items.Single(x => x.ProductId == known.Id);
        knownRow.CommercialActive.Should().BeTrue();
        knownRow.Offers.Single().Sales.Coverage.Should().Be("PARTIAL");
        knownRow.Offers.Single().Sales.Units.Should().Be(2m);
        knownRow.Offers.Single().Sales.Revenue.Should().Be(60m);
        knownRow.MissingCostComponents.Should().Contain("ENERGY_UNAVAILABLE");
        var unknownRow = all.Items.Single(x => x.ProductId == unknown.Id);
        unknownRow.Offers.Single().Sales.Coverage.Should().Be("UNKNOWN");
        unknownRow.Offers.Single().Sales.Units.Should().BeNull();
        unknownRow.Offers.Single().Sales.Revenue.Should().BeNull();
        all.Items.Single(x => x.ProductId == inactive.Id).CommercialActive.Should().BeFalse();

        var activeOnly = await (await owner.GetAsync($"/api/commerce/catalog?search={marker}&commercialActive=true&pageSize=10"))
            .Content.ReadFromJsonAsync<CommercialCatalogResponse>();
        activeOnly!.Total.Should().Be(2);
        var technicalInactive = await (await owner.GetAsync($"/api/commerce/catalog?search={marker}&active=false&pageSize=10"))
            .Content.ReadFromJsonAsync<CommercialCatalogResponse>();
        technicalInactive!.Items.Should().ContainSingle(x => x.ProductId == inactive.Id);
        var tagged = await (await owner.GetAsync($"/api/commerce/catalog?tagId={tag.Id}&pageSize=10"))
            .Content.ReadFromJsonAsync<CommercialCatalogResponse>();
        tagged!.Items.Should().ContainSingle(x => x.ProductId == known.Id);
    }

    [Fact]
    public async Task Published_items_keep_the_account_channel_identity_when_cross_module_channel_data_is_unavailable()
    {
        var missingChannelId = Guid.NewGuid();
        var marker = "missing-channel-" + Guid.NewGuid().ToString("N");
        var account = new MarketplaceAccount("SHOPEE", marker, missingChannelId, "Conta sem canal materializado");
        var listing = new MarketplaceListing(account.Id, marker, null, MarketplaceListingStatus.ACTIVE);
        await using (var db = _fixture.CreateContext())
        {
            // M-S8C1-002 (Codex Sol regate): this test must be hermetic — it must never rely on
            // some OTHER test in the shared PostgresCollection fixture having already inserted the
            // "SHOPEE" provider row first. `marketplace_account.provider_code` has a real FK to
            // `marketplace_provider.code` (fk_marketplace_account_marketplace_provider_provider_code)
            // that correctly rejects an orphaned account — that FK is production behavior and is
            // never weakened here. Existence-checked (not a bare Add) because the SAME shared
            // fixture may already have this row from another test in the collection; a plain
            // insert would then fail on the provider's own PK, which is exactly the kind of
            // order-dependency this fix removes, not reintroduces.
            if (!await db.Set<MarketplaceProvider>().AnyAsync(x => x.Code == "SHOPEE"))
                db.Add(new MarketplaceProvider("SHOPEE", "Shopee"));
            db.AddRange(account, listing);
            await db.SaveChangesAsync();
        }

        var owner = await LoggedInOwnerAsync();
        var response = await owner.GetAsync($"/api/commerce/published-items?search={marker}");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var result = JsonSerializer.Deserialize<PublishedItemsResponse>(body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var item = result.Items.Should().ContainSingle().Subject;
        item.SalesChannelId.Should().Be(missingChannelId);
        item.ChannelCode.Should().Be("UNKNOWN");
        item.ChannelName.Should().Be("Canal indisponível");
    }

    [Fact]
    public async Task Published_items_are_listing_centric_filterable_and_support_manual_link_unlink()
    {
        var marker = Guid.NewGuid().ToString("N")[..10];
        var provider = new MarketplaceProvider("P" + marker, "Provider " + marker);
        var channel = NewChannel(SalesChannelKind.Marketplace);
        var account = new MarketplaceAccount(provider.Code, "account-" + marker, channel.Id, "Conta " + marker);
        var product = new Product("P" + marker, "Produto " + marker, null);
        var offer = new ChannelOffer(product.Id, channel.Id, 40m, ChannelOfferPriceSource.MANUAL);
        var unlinked = new MarketplaceListing(account.Id, "U-" + marker, "SKU-U", MarketplaceListingStatus.ACTIVE, "Não vinculada", 40m);
        var review = new MarketplaceListing(account.Id, "R-" + marker, "SKU-R", MarketplaceListingStatus.PAUSED, "Revisão", 41m); review.MarkNeedsReview();
        var linked = new MarketplaceListing(account.Id, "L-" + marker, "SKU-L", MarketplaceListingStatus.ACTIVE, "Vinculada", 42m);
        linked.Link(product.Id, offer.Id, product.Id, channel.Id, channel.Id);
        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        linked.AddObservation("obs-" + marker, "fp-" + marker, ObservationProvenance.MANUAL, observedAt, observedAt);
        await using (var db = _fixture.CreateContext())
        {
            db.AddRange(provider, channel, account, product, offer, unlinked, review, linked);
            await db.SaveChangesAsync();
        }

        var owner = await LoggedInOwnerAsync();
        var page = await (await owner.GetAsync($"/api/commerce/published-items?provider={provider.Code}&sort=recent&page=1&pageSize=2"))
            .Content.ReadFromJsonAsync<PublishedItemsResponse>();
        page!.Total.Should().Be(3);
        page.Items.Should().HaveCount(2);
        page.Items.Should().OnlyContain(x => x.ProviderCode == provider.Code && x.SalesChannelId == channel.Id);

        var linkedPage = await (await owner.GetAsync($"/api/commerce/published-items?provider={provider.Code}&linkage=LINKED&pageSize=10"))
            .Content.ReadFromJsonAsync<PublishedItemsResponse>();
        var linkedRow = linkedPage!.Items.Should().ContainSingle().Subject;
        linkedRow.Id.Should().Be(linked.Id);
        linkedRow.ProductId.Should().Be(product.Id);
        linkedRow.ChannelOfferId.Should().Be(offer.Id);
        linkedRow.LastObservedAt.Should().BeCloseTo(observedAt, TimeSpan.FromMilliseconds(1));
        var reviewPage = await (await owner.GetAsync($"/api/commerce/published-items?marketplaceAccountId={account.Id}&linkage=NEEDS_REVIEW&pageSize=10"))
            .Content.ReadFromJsonAsync<PublishedItemsResponse>();
        reviewPage!.Items.Should().ContainSingle(x => x.Id == review.Id);
        var search = await (await owner.GetAsync($"/api/commerce/published-items?provider={provider.Code}&search=SKU-U&pageSize=10"))
            .Content.ReadFromJsonAsync<PublishedItemsResponse>();
        search!.Items.Should().ContainSingle(x => x.Id == unlinked.Id);

        var link = await owner.PostAsync($"/api/commerce/published-items/{unlinked.Id}/link",
            new LinkListingRequest(product.Id, offer.Id, unlinked.Version));
        link.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var linkedAfter = await (await owner.GetAsync($"/api/commerce/published-items?provider={provider.Code}&search=SKU-U&pageSize=10"))
            .Content.ReadFromJsonAsync<PublishedItemsResponse>();
        var reconciled = linkedAfter!.Items.Single();
        reconciled.LinkageState.Should().Be(MarketplaceLinkageState.LINKED);
        reconciled.ProductId.Should().Be(product.Id);
        reconciled.ChannelOfferId.Should().Be(offer.Id);
        var unlink = await owner.PostAsync($"/api/commerce/published-items/{unlinked.Id}/unlink?version={reconciled.Version}", new { });
        unlink.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var unlinkedAfter = await (await owner.GetAsync($"/api/commerce/published-items?provider={provider.Code}&search=SKU-U&pageSize=10"))
            .Content.ReadFromJsonAsync<PublishedItemsResponse>();
        unlinkedAfter!.Items.Single().LinkageState.Should().Be(MarketplaceLinkageState.UNLINKED);
    }

    private static SalesChannel NewChannel(SalesChannelKind kind) => new(
        "C" + Guid.NewGuid().ToString("N")[..20], "Canal de teste", kind, .30m, null);

    private async Task<AuthTestClient> LoggedInOwnerAsync()
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        const string password = "a-perfectly-fine-12char-password";
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!await roles.RoleExistsAsync(Roles.Owner))
            (await roles.CreateAsync(new ApplicationRole(Roles.Owner))).Succeeded.Should().BeTrue();
        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "Owner Commerce",
            IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow
        };
        (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
        (await users.AddToRoleAsync(user, Roles.Owner)).Succeeded.Should().BeTrue();
        var client = new AuthTestClient(_factory.CreateHttpsClient());
        await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new LoginRequest(email, password))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await client.EnsureCsrfCookieAsync();
        return client;
    }
}
