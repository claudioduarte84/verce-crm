using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verce.Api.Catalog;
using Verce.Api.Inventory;
using Verce.Api.Pricing;
using Verce.Api.Quoting;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Inventory;
using Verce.Modules.Pricing;
using Verce.Modules.Quoting;
using Verce.Platform.Identity;

namespace Verce.IntegrationTests.Quoting;

/// <summary>
/// B-01 (true clone-candidate revision construction) and B-02 (full cost-snapshot preservation)
/// against the real host and real PostgreSQL. A sourced line's Product identity, description and
/// entire CostEngine breakdown must survive a revision unchanged even after the underlying Supply
/// cost/Product data has moved on — only explicitly editable commercial inputs (quantity, margin,
/// override, discount) may change on a sourced line.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QuoteCloneAndSnapshotHttpIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;

    public QuoteCloneAndSnapshotHttpIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE quoting.quote_status_history, quoting.quote_item, quoting.quote_revision, quoting.quote,
                           quoting.quote_number_counter, production.production_order,
                           pricing.fee_rule_version, pricing.fee_rule, pricing.sales_channel,
                           settings.app_setting, platform.account_setup_token, platform.user_role,
                           platform.user_claim, platform.user_login, platform.user_token, platform.audit_log, platform."user"
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
    public async Task Revise_with_only_quantity_changed_on_a_sourced_line_preserves_the_frozen_cost_snapshot_verbatim()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var direct = await DirectChannelAsync(owner);
        var (product, supply) = await CreateProductWithRecipeAsync(owner, "CLONE-PROD-1", unitCost: 100m);

        var r1 = await ReadAsync<QuoteResponse>(await owner.PostAsync("/api/quotes", new QuoteCreateRequest(null, direct.Id,
            [new QuoteItemRequest(null, product.Id, null, null, 1m, 0.30m, null, QuoteDiscountKind.None, 0m)], null)));
        var r1Item = r1.CurrentRevision.Items.Single();
        r1Item.ProductNameSnapshot.Should().Be(product.Name);
        r1Item.CostSnapshot.Materials.Should().NotBeEmpty();
        var frozenUnitCost = r1Item.UnitTotalCost;
        var frozenMaterials = r1Item.CostSnapshot.Materials;

        // Change the underlying cost basis AFTER R1 was issued — a new, much more expensive
        // purchase receipt becomes the CURRENT cost basis for this Supply.
        await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(1000m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, 900m, null, "NF-EXPENSIVE", null, supply.Version + 1));

        // Revise: ONLY quantity changes (1 -> 3) on the sourced line.
        var r2 = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{r1.Id}/revise",
            new QuoteReviseRequest(null, direct.Id,
                [new QuoteItemRequest(r1Item.Id, null, null, null, 3m, 0.30m, null, QuoteDiscountKind.None, 0m)], null, r1.Version)));
        var r2Item = r2.CurrentRevision.Items.Single();

        // Identity and the FULL cost breakdown are copied verbatim — never re-resolved.
        r2Item.SourceQuoteItemId.Should().Be(r1Item.Id);
        r2Item.ProductId.Should().Be(product.Id);
        r2Item.ProductNameSnapshot.Should().Be(r1Item.ProductNameSnapshot);
        r2Item.Description.Should().Be(r1Item.Description);
        r2Item.UnitTotalCost.Should().Be(frozenUnitCost, "cost is frozen per line — a quantity change never refreshes it");
        r2Item.CostSnapshot.Should().BeEquivalentTo(r1Item.CostSnapshot, "the ENTIRE cost breakdown must survive verbatim, not just the scalar unit cost");
        r2Item.CostSnapshot.Materials.Should().BeEquivalentTo(frozenMaterials);

        // Only the explicitly-editable commercial input changed.
        r2Item.Quantity.Should().Be(3m);
        r2Item.LineCostAmount.Should().Be(Math.Round(frozenUnitCost * 3m, 2), "the allocation basis recomputes from frozen cost x new quantity");

        // R1 itself is untouched — the Quote's CURRENT revision is now R2, so R1 must be read
        // directly from the database by its own revision id.
        await using var db = _fixture.CreateContext();
        var r1Revision = await db.Set<Verce.Modules.Quoting.QuoteRevision>()
            .Include(x => x.Items).ThenInclude(x => x.CostSnapshot)
            .Include(x => x.Items).ThenInclude(x => x.Materials)
            .SingleAsync(x => x.Id == r1.CurrentRevision.Id);
        r1Revision.Status.Should().Be(QuoteRevisionStatus.SUPERSEDED);
        var r1ItemAfter = r1Revision.Items.Single();
        r1ItemAfter.Quantity.Should().Be(1m, "R1 must never mutate");
        r1ItemAfter.UnitTotalCost.Should().Be(frozenUnitCost);
    }

    [Fact]
    public async Task Revise_with_a_new_line_alongside_a_sourced_line_resolves_CURRENT_data_only_for_the_new_line()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var direct = await DirectChannelAsync(owner);
        var (product, _) = await CreateProductWithRecipeAsync(owner, "CLONE-PROD-2", unitCost: 100m);
        var (secondProduct, _) = await CreateProductWithRecipeAsync(owner, "CLONE-PROD-2B", unitCost: 50m);

        var r1 = await ReadAsync<QuoteResponse>(await owner.PostAsync("/api/quotes", new QuoteCreateRequest(null, direct.Id,
            [new QuoteItemRequest(null, product.Id, null, null, 1m, 0.30m, null, QuoteDiscountKind.None, 0m)], null)));
        var r1Item = r1.CurrentRevision.Items.Single();

        var r2 = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{r1.Id}/revise",
            new QuoteReviseRequest(null, direct.Id,
            [
                new QuoteItemRequest(r1Item.Id, null, null, null, 1m, 0.30m, null, QuoteDiscountKind.None, 0m),
                new QuoteItemRequest(null, secondProduct.Id, null, null, 2m, 0.30m, null, QuoteDiscountKind.None, 0m),
            ], null, r1.Version)));

        r2.CurrentRevision.Items.Should().HaveCount(2);
        var sourced = r2.CurrentRevision.Items.Single(i => i.SourceQuoteItemId == r1Item.Id);
        var brandNew = r2.CurrentRevision.Items.Single(i => i.SourceQuoteItemId == null);
        sourced.ProductId.Should().Be(product.Id);
        brandNew.ProductId.Should().Be(secondProduct.Id);
        brandNew.ProductNameSnapshot.Should().Be(secondProduct.Name);
        brandNew.CostSnapshot.Materials.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Revise_omitting_a_source_line_removes_it_from_the_new_revision()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var direct = await DirectChannelAsync(owner);
        var (product, _) = await CreateProductWithRecipeAsync(owner, "CLONE-PROD-3", unitCost: 100m);

        var r1 = await ReadAsync<QuoteResponse>(await owner.PostAsync("/api/quotes", new QuoteCreateRequest(null, direct.Id,
        [
            new QuoteItemRequest(null, product.Id, null, null, 1m, 0.30m, null, QuoteDiscountKind.None, 0m),
            new QuoteItemRequest(null, null, "Item avulso", 10m, 1m, 0.30m, null, QuoteDiscountKind.None, 0m),
        ], null)));
        var keep = r1.CurrentRevision.Items.Single(i => i.ProductId == product.Id);

        var r2 = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{r1.Id}/revise",
            new QuoteReviseRequest(null, direct.Id,
                [new QuoteItemRequest(keep.Id, null, null, null, 1m, 0.30m, null, QuoteDiscountKind.None, 0m)], null, r1.Version)));

        r2.CurrentRevision.Items.Should().ContainSingle();
        r2.CurrentRevision.Items.Single().SourceQuoteItemId.Should().Be(keep.Id);
    }

    [Fact]
    public async Task Revise_with_an_unknown_source_item_id_is_rejected()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var direct = await DirectChannelAsync(owner);
        var r1 = await CreateSimpleAdHocQuoteAsync(owner, direct);

        var response = await owner.PostAsync($"/api/quotes/{r1.Id}/revise", new QuoteReviseRequest(null, direct.Id,
            [new QuoteItemRequest(Guid.NewGuid(), null, null, null, 1m, 0.30m, null, QuoteDiscountKind.None, 0m)], null, r1.Version));
        // StatusCodeFor's generic "*_NOT_FOUND -> 404" rule applies here (same convention as
        // every other *_NOT_FOUND code in the endpoint), not the 422 used for a content/shape problem.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemCodeAsync(response)).Should().Be("QUOTE_ITEM_SOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task Revise_with_a_source_item_from_a_different_quote_is_rejected()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var direct = await DirectChannelAsync(owner);
        var quoteA = await CreateSimpleAdHocQuoteAsync(owner, direct);
        var quoteB = await CreateSimpleAdHocQuoteAsync(owner, direct);
        var foreignItemId = quoteB.CurrentRevision.Items.Single().Id;

        var response = await owner.PostAsync($"/api/quotes/{quoteA.Id}/revise", new QuoteReviseRequest(null, direct.Id,
            [new QuoteItemRequest(foreignItemId, null, null, null, 1m, 0.30m, null, QuoteDiscountKind.None, 0m)], null, quoteA.Version));
        // StatusCodeFor's generic "*_NOT_FOUND -> 404" rule applies here (same convention as
        // every other *_NOT_FOUND code in the endpoint), not the 422 used for a content/shape problem.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemCodeAsync(response)).Should().Be("QUOTE_ITEM_SOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task Revise_with_a_duplicate_source_item_id_across_two_request_lines_is_rejected()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var direct = await DirectChannelAsync(owner);
        var r1 = await CreateSimpleAdHocQuoteAsync(owner, direct);
        var sourceId = r1.CurrentRevision.Items.Single().Id;

        var response = await owner.PostAsync($"/api/quotes/{r1.Id}/revise", new QuoteReviseRequest(null, direct.Id,
        [
            new QuoteItemRequest(sourceId, null, null, null, 1m, 0.30m, null, QuoteDiscountKind.None, 0m),
            new QuoteItemRequest(sourceId, null, null, null, 2m, 0.30m, null, QuoteDiscountKind.None, 0m),
        ], null, r1.Version));
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(response)).Should().Be("QUOTE_ITEM_SOURCE_DUPLICATE");
    }

    [Fact]
    public async Task Revise_attempting_to_change_the_Product_of_a_sourced_line_is_rejected()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var direct = await DirectChannelAsync(owner);
        var (product, _) = await CreateProductWithRecipeAsync(owner, "CLONE-PROD-4", unitCost: 100m);
        var (otherProduct, _) = await CreateProductWithRecipeAsync(owner, "CLONE-PROD-4B", unitCost: 50m);

        var r1 = await ReadAsync<QuoteResponse>(await owner.PostAsync("/api/quotes", new QuoteCreateRequest(null, direct.Id,
            [new QuoteItemRequest(null, product.Id, null, null, 1m, 0.30m, null, QuoteDiscountKind.None, 0m)], null)));
        var r1Item = r1.CurrentRevision.Items.Single();

        var response = await owner.PostAsync($"/api/quotes/{r1.Id}/revise", new QuoteReviseRequest(null, direct.Id,
            [new QuoteItemRequest(r1Item.Id, otherProduct.Id, null, null, 1m, 0.30m, null, QuoteDiscountKind.None, 0m)], null, r1.Version));
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(response)).Should().Be("QUOTE_ITEM_SOURCE_PRODUCT_IMMUTABLE");
    }

    [Fact]
    public async Task Create_rejects_a_SourceQuoteItemId_since_there_is_no_prior_revision_yet()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var direct = await DirectChannelAsync(owner);
        var response = await owner.PostAsync("/api/quotes", new QuoteCreateRequest(null, direct.Id,
            [new QuoteItemRequest(Guid.NewGuid(), null, "X", 10m, 1m, 0.3m, null, QuoteDiscountKind.None, 0m)], null));
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(response)).Should().Be("QUOTE_ITEM_SOURCE_NOT_ALLOWED_ON_CREATE");
    }

    [Fact]
    public async Task Creating_a_Product_based_line_snapshots_Product_Description_never_the_clients_ad_hoc_text()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var direct = await DirectChannelAsync(owner);
        var (product, _) = await CreateProductWithRecipeAsync(owner, "CLONE-PROD-5", unitCost: 100m, description: "Descrição real do produto");

        var quote = await ReadAsync<QuoteResponse>(await owner.PostAsync("/api/quotes", new QuoteCreateRequest(null, direct.Id,
            [new QuoteItemRequest(null, product.Id, "Texto ad-hoc do cliente — deve ser ignorado", null, 1m, 0.30m, null, QuoteDiscountKind.None, 0m)], null)));

        quote.CurrentRevision.Items.Single().Description.Should().Be("Descrição real do produto");
    }

    [Fact]
    public async Task Full_cost_breakdown_materials_labor_machine_and_additional_costs_are_all_present_in_the_response()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var direct = await DirectChannelAsync(owner);
        var (product, _) = await CreateProductWithRecipeAsync(owner, "CLONE-PROD-6", unitCost: 100m);

        var quote = await ReadAsync<QuoteResponse>(await owner.PostAsync("/api/quotes", new QuoteCreateRequest(null, direct.Id,
            [new QuoteItemRequest(null, product.Id, null, null, 1m, 0.30m, null, QuoteDiscountKind.None, 0m)], null)));

        var snapshot = quote.CurrentRevision.Items.Single().CostSnapshot;
        snapshot.Materials.Should().NotBeEmpty();
        snapshot.AdditionalCosts.Should().ContainSingle(a => a.Description == "Embalagem" && a.Amount == 4m);
        snapshot.LaborMinutes.Should().Be(20m);
        snapshot.LaborCost.Should().BeGreaterThan(0m);
        snapshot.MachineMinutes.Should().Be(180m);
        snapshot.MachineCost.Should().BeGreaterThan(0m);
        snapshot.EngineVersion.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Revise_with_only_quantity_changed_inherits_the_frozen_fee_context_even_after_a_newer_version_becomes_effective()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var (channel, v1) = await CreateMarketplaceChannelAsync(owner, "F01-CH-1", 0.10m, 5.00m, FixedFeeApplication.PerUnit);

        var r1 = await ReadAsync<QuoteResponse>(await owner.PostAsync("/api/quotes", new QuoteCreateRequest(null, channel.Id,
            [new QuoteItemRequest(null, null, "Item", 50m, 1m, 0.30m, null, QuoteDiscountKind.None, 0m)], null)));
        var r1Item = r1.CurrentRevision.Items.Single();
        r1Item.FeeRuleVersionId.Should().Be(v1.Id);
        r1Item.CommissionPercent.Should().Be(0.10m);
        r1Item.RawFixedFee.Should().Be(5.00m);

        // V2 becomes the CURRENT effective version for this channel — closing V1's open end.
        await AddFeeRuleVersionAsync(owner, channel.Id, DateOnly.FromDateTime(DateTime.UtcNow), 0.20m, 9.00m, FixedFeeApplication.PerUnit, closeCurrentOpenVersion: true);

        // Revise: ONLY quantity changes. F-01: must NOT pick up V2 merely because it is now current.
        var r2 = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{r1.Id}/revise",
            new QuoteReviseRequest(null, channel.Id,
                [new QuoteItemRequest(r1Item.Id, null, null, null, 3m, 0.30m, null, QuoteDiscountKind.None, 0m)], null, r1.Version)));
        var r2Item = r2.CurrentRevision.Items.Single();

        r2Item.FeeRuleVersionId.Should().Be(v1.Id, "the revision's fee context is inherited verbatim, never re-resolved against 'today'");
        r2Item.CommissionPercent.Should().Be(0.10m);
        r2Item.RawFixedFee.Should().Be(5.00m);
        r2Item.FixedFeeApplication.Should().Be(QuoteFixedFeeApplication.PerUnit);
        r2Item.Quantity.Should().Be(3m, "the explicitly-editable commercial input still changes");

        // R1 itself is untouched.
        await using var db = _fixture.CreateContext();
        var r1Revision = await db.Set<Verce.Modules.Quoting.QuoteRevision>().Include(x => x.Items).SingleAsync(x => x.Id == r1.CurrentRevision.Id);
        r1Revision.Items.Single().FeeRuleVersionId.Should().Be(v1.Id);
    }

    [Fact]
    public async Task Revise_adding_a_new_line_under_the_same_channel_uses_the_inherited_fee_context_not_the_current_one()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var (channel, v1) = await CreateMarketplaceChannelAsync(owner, "F01-CH-2", 0.10m, 5.00m, FixedFeeApplication.PerUnit);

        var r1 = await ReadAsync<QuoteResponse>(await owner.PostAsync("/api/quotes", new QuoteCreateRequest(null, channel.Id,
            [new QuoteItemRequest(null, null, "Item", 50m, 1m, 0.30m, null, QuoteDiscountKind.None, 0m)], null)));
        var r1Item = r1.CurrentRevision.Items.Single();

        await AddFeeRuleVersionAsync(owner, channel.Id, DateOnly.FromDateTime(DateTime.UtcNow), 0.20m, 9.00m, FixedFeeApplication.PerUnit, closeCurrentOpenVersion: true);

        // R2: keep the sourced line AND add a brand-new ad-hoc line, same channel.
        var r2 = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{r1.Id}/revise",
            new QuoteReviseRequest(null, channel.Id,
            [
                new QuoteItemRequest(r1Item.Id, null, null, null, 1m, 0.30m, null, QuoteDiscountKind.None, 0m),
                new QuoteItemRequest(null, null, "Novo item", 20m, 1m, 0.30m, null, QuoteDiscountKind.None, 0m),
            ], null, r1.Version)));

        r2.CurrentRevision.Items.Should().OnlyContain(i => i.FeeRuleVersionId == v1.Id,
            "one coherent S6 fee group: the NEW line must use the inherited V1 context, never a newer version while sourced lines keep V1");
        r2.CurrentRevision.Items.Should().OnlyContain(i => i.CommissionPercent == 0.10m && i.RawFixedFee == 5.00m);
    }

    [Fact]
    public async Task Revise_with_an_explicit_sales_channel_change_resolves_the_new_channels_current_fee_context()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var (channelA, v1A) = await CreateMarketplaceChannelAsync(owner, "F01-CH-3A", 0.10m, 5.00m, FixedFeeApplication.PerUnit);
        var (channelB, v1B) = await CreateMarketplaceChannelAsync(owner, "F01-CH-3B", 0.25m, 7.00m, FixedFeeApplication.PerUnit);

        var r1 = await ReadAsync<QuoteResponse>(await owner.PostAsync("/api/quotes", new QuoteCreateRequest(null, channelA.Id,
            [new QuoteItemRequest(null, null, "Item", 50m, 1m, 0.30m, null, QuoteDiscountKind.None, 0m)], null)));
        var r1Item = r1.CurrentRevision.Items.Single();
        r1Item.FeeRuleVersionId.Should().Be(v1A.Id);

        // Explicit channel change on revise — this IS the "refresh" signal (mission §7), unlike
        // resubmitting the SAME channel id, which must never refresh (proven above).
        var r2 = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{r1.Id}/revise",
            new QuoteReviseRequest(null, channelB.Id,
                [new QuoteItemRequest(r1Item.Id, null, null, null, 1m, 0.30m, null, QuoteDiscountKind.None, 0m)], null, r1.Version)));
        var r2Item = r2.CurrentRevision.Items.Single();

        r2Item.FeeRuleVersionId.Should().Be(v1B.Id);
        r2Item.CommissionPercent.Should().Be(0.25m);
        r2Item.RawFixedFee.Should().Be(7.00m);
        r2Item.SalesChannelId.Should().Be(channelB.Id);
    }

    private async Task<(SalesChannelResponse Channel, FeeRuleVersionResponse Version)> CreateMarketplaceChannelAsync(
        AuthTestClient owner, string code, decimal commissionPercent, decimal fixedFee, FixedFeeApplication application)
    {
        var channel = await ReadAsync<SalesChannelResponse>(await owner.PostAsync("/api/pricing/channels",
            new SalesChannelCreateRequest(code, code, SalesChannelKind.Marketplace, null, null)));
        await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule", new FeeRuleCreateRequest($"Regra {code}"));
        var feeRule = await ReadAsync<FeeRuleResponse>(await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule/versions",
            new FeeRuleVersionCreateRequest(new DateOnly(2020, 1, 1), null, commissionPercent, fixedFee, application, null, null, null, false)));
        return (channel, feeRule.Versions.Single());
    }

    private async Task AddFeeRuleVersionAsync(AuthTestClient owner, Guid channelId, DateOnly validFrom, decimal commissionPercent,
        decimal fixedFee, FixedFeeApplication application, bool closeCurrentOpenVersion)
    {
        var response = await owner.PostAsync($"/api/pricing/channels/{channelId}/fee-rule/versions",
            new FeeRuleVersionCreateRequest(validFrom, null, commissionPercent, fixedFee, application, null, null, null, closeCurrentOpenVersion));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private async Task<SalesChannelResponse> DirectChannelAsync(AuthTestClient owner)
    {
        var channels = await ReadAsync<IReadOnlyList<SalesChannelResponse>>(await owner.GetAsync("/api/pricing/channels"));
        return channels.Single(x => x.Code == "DIRECT");
    }

    private async Task<QuoteResponse> CreateSimpleAdHocQuoteAsync(AuthTestClient owner, SalesChannelResponse channel) =>
        await ReadAsync<QuoteResponse>(await owner.PostAsync("/api/quotes", new QuoteCreateRequest(null, channel.Id,
            [new QuoteItemRequest(null, null, "Item avulso", 10.00m, 1m, 0.35m, null, QuoteDiscountKind.None, 0m)], null)));

    private async Task<(ProductResponse Product, SupplyResponse Supply)> CreateProductWithRecipeAsync(
        AuthTestClient owner, string productCode, decimal unitCost, string? description = null)
    {
        var supply = await ReadAsync<SupplyResponse>(await owner.PostAsync("/api/supplies",
            new SupplyCreateRequest(productCode + "-SUP", "Insumo " + productCode, null, "FILAMENT", SupplyBaseUnit.Gram, null, null, null, null)));
        supply = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(1000m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, unitCost, null, null, "NF-INICIAL", null, supply.Version)));

        var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products",
            new ProductCreateRequest(productCode, "Produto " + productCode, description)));
        product = await ReadAsync<ProductResponse>(await owner.PutAsync($"/api/products/{product.Id}/recipe",
            new ProductRecipeUpdateRequest(null, 20m, 30m, 180m, 2m, 1, "Nota",
                [new ProductRecipeMaterialLineRequest(supply.Id, 10m, SupplyBaseUnit.Gram, null, null)],
                [new ProductRecipeAdditionalCostLineRequest("Embalagem", 4m)],
                product.Version)));
        return (product, supply);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
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
        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            DisplayName = role + " S6-Clone",
            IsActive = true,
            SetupStatus = SetupStatus.Active,
            SetupCompletedAt = DateTimeOffset.UtcNow,
        };
        (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
        (await users.AddToRoleAsync(user, role)).Succeeded.Should().BeTrue();
        var client = new AuthTestClient(_factory.CreateHttpsClient());
        await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new Verce.Api.Auth.LoginRequest(email, password))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await client.EnsureCsrfCookieAsync();
        return (client, user.Id);
    }
}
