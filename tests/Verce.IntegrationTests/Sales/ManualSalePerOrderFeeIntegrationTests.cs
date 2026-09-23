using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verce.Api.Catalog;
using Verce.Api.Customers;
using Verce.Api.Inventory;
using Verce.Api.Pricing;
using Verce.Api.Sales;
using Verce.Api.Quoting;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Inventory;
using Verce.Modules.Pricing;
using Verce.Modules.Customers;
using Verce.Modules.Quoting;
using Verce.Platform.Identity;

namespace Verce.IntegrationTests.Sales;

/// <summary>Real PostgreSQL proof that a manual sale uses the same deterministic per-order
/// allocation authority as Quote construction rather than multiplying a fixed fee per line.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ManualSalePerOrderFeeIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;

    public ManualSalePerOrderFeeIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE sales.sale, quoting.quote_status_history, quoting.quote_item_material_snapshot, quoting.quote_item_additional_cost_snapshot,
                           quoting.quote_item_cost_snapshot, quoting.quote_item, quoting.quote_revision, quoting.quote, quoting.quote_number_counter,
                           customers.customer_address, customers.customer, production.production_order,
                           catalog.product_recipe_material_line, catalog.product_recipe_additional_cost_line,
                           catalog.product_recipe, catalog.product, pricing.price_bracket, pricing.fee_rule_version,
                           pricing.fee_rule, pricing.sales_channel, inventory.inventory_movement, inventory.supply,
                           settings.app_setting, platform.account_setup_token, platform.user_role, platform.user_claim,
                           platform.user_login, platform.user_token, platform.audit_log, platform."user"
            RESTART IDENTITY CASCADE;
            """);
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, new Dictionary<string, string?> { ["Settings:SeedOnStartup"] = "true" });
        using var warmup = _factory.CreateHttpsClient();
        (await warmup.GetAsync("/health/live")).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Manual_marketplace_sale_allocates_a_PerOrder_fixed_fee_once_across_multiple_lines()
    {
        var owner = await LoggedInOwnerAsync();
        var supply = await ReadAsync<SupplyResponse>(await owner.PostAsync("/api/supplies",
            new SupplyCreateRequest("S8A-PERORDER", "Material PerOrder", null, "FILAMENT", SupplyBaseUnit.Gram, null, null, null, null)));
        supply = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(1000m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, 100m, null, null, null, supply.Version)));
        var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("S8A-PERORDER", "Produto PerOrder", null)));
        product = await ReadAsync<ProductResponse>(await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, null, [new ProductRecipeMaterialLineRequest(supply.Id, 100m, SupplyBaseUnit.Gram, null, null)], [], product.Version)));
        var channel = await ReadAsync<SalesChannelResponse>(await owner.PostAsync("/api/pricing/channels",
            new SalesChannelCreateRequest("S8A-PERORDER", "Marketplace PerOrder", SalesChannelKind.Marketplace, null, null)));
        (await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule", new FeeRuleCreateRequest("Regra PerOrder"))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule/versions", new FeeRuleVersionCreateRequest(
            new DateOnly(2020, 1, 1), null, 0.10m, 5m, FixedFeeApplication.PerOrder, null, null, null, false))).StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await owner.PostAsync("/api/sales", new ManualSaleRequest(channel.Id, null, null, null, 0m, null,
            [new ManualSaleItemRequest(product.Id, 2m, 50m, 0m), new ManualSaleItemRequest(product.Id, 1m, 50m, 0m)]));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var sale = await ReadAsync<SaleResponse>(response);

        sale.Source.Should().Be(Verce.Modules.Sales.SaleSource.MANUAL_ENTRY);
        sale.Items.Should().HaveCount(2);
        sale.ChannelFeeAmount.Should().Be(20m, "15.00 commission plus the 5.00 order fee exactly once");
        sale.Items.Sum(x => x.ChannelFeeAmount).Should().Be(20m);
        sale.Items.Select(x => x.ChannelFeeAmount).Should().BeEquivalentTo([13.33m, 6.67m]);
        sale.TotalCostAmount.Should().Be(30m);
        sale.GrossProfitAmount.Should().Be(100m);
        sale.EffectiveMarginPercent.Should().Be(0.666667m);
    }

    [Fact]
    public async Task Manual_direct_sale_resolves_estimated_cost_server_side_and_has_zero_channel_fee()
    {
        var owner = await LoggedInOwnerAsync();
        var supply = await ReadAsync<SupplyResponse>(await owner.PostAsync("/api/supplies",
            new SupplyCreateRequest("S8A-DIRECT", "Material Direct", null, "FILAMENT", SupplyBaseUnit.Gram, null, null, null, null)));
        supply = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(1000m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, 100m, null, null, null, supply.Version)));
        var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("S8A-DIRECT", "Produto Direct", null)));
        product = await ReadAsync<ProductResponse>(await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, null, [new ProductRecipeMaterialLineRequest(supply.Id, 100m, SupplyBaseUnit.Gram, null, null)], [], product.Version)));
        var direct = (await ReadAsync<IReadOnlyList<SalesChannelResponse>>(await owner.GetAsync("/api/pricing/channels"))).Single(x => x.Code == "DIRECT");

        var response = await owner.PostAsync("/api/sales", new ManualSaleRequest(direct.Id, null, null, null, 0m, null,
            [new ManualSaleItemRequest(product.Id, 2m, 25m, 0m)]));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var sale = await ReadAsync<SaleResponse>(response);

        sale.Source.Should().Be(Verce.Modules.Sales.SaleSource.MANUAL_ENTRY);
        sale.SalesChannelId.Should().Be(direct.Id);
        sale.CostBasis.Should().Be(Verce.Modules.Sales.SaleCostBasis.ESTIMATED);
        sale.Items.Single().UnitCostAmount.Should().Be(10m);
        sale.Items.Single().LineCostAmount.Should().Be(20m);
        sale.ChannelFeeAmount.Should().Be(0m);
        sale.Items.Single().ChannelFeeAmount.Should().Be(0m);
    }

    [Fact]
    public async Task Manual_sale_without_a_usable_cost_basis_is_rejected_without_persisting_sale_rows()
    {
        var owner = await LoggedInOwnerAsync();
        var supply = await ReadAsync<SupplyResponse>(await owner.PostAsync("/api/supplies",
            new SupplyCreateRequest("S8A-NOCOST-SUPPLY", "Material sem custo", null, "FILAMENT", SupplyBaseUnit.Gram, null, null, null, null)));
        var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("S8A-NOCOST", "Produto sem custo", null)));
        product = await ReadAsync<ProductResponse>(await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, null, [new ProductRecipeMaterialLineRequest(supply.Id, 100m, SupplyBaseUnit.Gram, null, null)], [], product.Version)));
        var direct = (await ReadAsync<IReadOnlyList<SalesChannelResponse>>(await owner.GetAsync("/api/pricing/channels"))).Single(x => x.Code == "DIRECT");

        var response = await owner.PostAsync("/api/sales", new ManualSaleRequest(direct.Id, null, null, null, 0m, null,
            [new ManualSaleItemRequest(product.Id, 1m, 25m, 0m)]));
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("code").GetString().Should().Be("COST_BASIS_UNAVAILABLE");
        await using var db = _fixture.CreateContext();
        (await db.Set<Verce.Modules.Sales.Sale>().CountAsync()).Should().Be(0);
        (await db.Set<Verce.Modules.Sales.SaleItem>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Quote_conversion_copies_the_frozen_snapshot_after_customer_product_and_cost_authority_mutate()
    {
        var owner = await LoggedInOwnerAsync();
        var supply = await ReadAsync<SupplyResponse>(await owner.PostAsync("/api/supplies",
            new SupplyCreateRequest("S8A-COPY", "Material de snapshot", null, "FILAMENT", SupplyBaseUnit.Gram, null, null, null, null)));
        supply = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(1000m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, 100m, null, null, null, supply.Version)));
        var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("S8A-COPY", "Produto congelado", null)));
        product = await ReadAsync<ProductResponse>(await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, null, [new ProductRecipeMaterialLineRequest(supply.Id, 100m, SupplyBaseUnit.Gram, null, null)], [], product.Version)));
        var customer = await ReadAsync<CustomerResponse>(await owner.PostAsync("/api/customers",
            new CustomerRequest(PersonType.Individual, "Cliente congelado", null, null, null, null, null, 0)));
        var direct = (await ReadAsync<IReadOnlyList<SalesChannelResponse>>(await owner.GetAsync("/api/pricing/channels"))).Single(x => x.Code == "DIRECT");
        var quote = await ReadAsync<QuoteResponse>(await owner.PostAsync("/api/quotes", new QuoteCreateRequest(customer.Id, direct.Id,
            [new QuoteItemRequest(null, product.Id, null, null, 2m, .35m, null, QuoteDiscountKind.None, 0m)], null)));
        (string CustomerName, string ProductName, decimal Quantity, decimal UnitPrice, decimal Discount, decimal Total, decimal UnitCost, decimal LineCost, decimal Fee, decimal Profit, decimal Margin) frozen;
        await using (var db = _fixture.CreateContext())
        {
            var revision = await db.Set<QuoteRevision>().Include(x => x.Items).SingleAsync(x => x.Id == quote.CurrentRevision.Id);
            var item = revision.Items.Single();
            frozen = (revision.CustomerNameSnapshot!, item.ProductNameSnapshot, item.Quantity, item.UnitPrice, item.DiscountAmount,
                item.LineTotalAmount, item.UnitTotalCost, item.LineCostAmount, item.LineFeeAmount, item.ExpectedProfitAmount, item.EffectiveMarginPercent);
        }
        var approved = await ReadAsync<QuoteResponse>(await owner.PostAsync($"/api/quotes/{quote.Id}/approve", new QuoteVersionedRequest(quote.Version)));
        var renamedCustomer = await ReadAsync<CustomerResponse>(await owner.PutAsync($"/api/customers/{customer.Id}",
            new CustomerRequest(customer.PersonType, "Cliente atual", customer.TradeName, customer.Document, customer.Email, customer.Phone, customer.Notes, customer.Version)));
        var renamedProduct = await ReadAsync<ProductResponse>(await owner.PutAsync($"/api/products/{product.Id}", new ProductUpdateRequest("Produto atual", product.Description, product.Version)));
        var beforeAuthority = supply.LatestPurchaseUnitCost;
        supply = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(1000m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow.AddMinutes(1), null, 200m, null, null, null, supply.Version)));
        renamedCustomer.Name.Should().NotBe(frozen.CustomerName);
        renamedProduct.Name.Should().NotBe(frozen.ProductName);
        supply.LatestPurchaseUnitCost.Should().NotBe(beforeAuthority, "the recipe references this supply cost authority");

        var converted = await ReadAsync<SaleResponse>(await owner.PostAsync($"/api/sales/from-quote/{approved.CurrentRevision.Id}", new ConvertQuoteRequest(Guid.NewGuid())));
        var saleItem = converted.Items.Single();
        converted.CustomerNameSnapshot.Should().Be(frozen.CustomerName);
        saleItem.ProductName.Should().Be(frozen.ProductName);
        saleItem.Quantity.Should().Be(frozen.Quantity);
        saleItem.UnitPrice.Should().Be(frozen.UnitPrice);
        saleItem.DiscountAmount.Should().Be(frozen.Discount);
        saleItem.LineTotalAmount.Should().Be(frozen.Total);
        saleItem.UnitCostAmount.Should().Be(frozen.UnitCost);
        saleItem.LineCostAmount.Should().Be(frozen.LineCost);
        saleItem.ChannelFeeAmount.Should().Be(frozen.Fee);
        saleItem.GrossProfitAmount.Should().Be(frozen.Profit);
        converted.EffectiveMarginPercent.Should().Be(frozen.Margin);
    }

    private async Task<AuthTestClient> LoggedInOwnerAsync()
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        const string password = "a-perfectly-fine-12char-password";
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!await roles.RoleExistsAsync(Roles.Owner)) (await roles.CreateAsync(new ApplicationRole(Roles.Owner))).Succeeded.Should().BeTrue();
        var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "Owner", IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow };
        (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
        (await users.AddToRoleAsync(user, Roles.Owner)).Succeeded.Should().BeTrue();
        var client = new AuthTestClient(_factory.CreateHttpsClient());
        await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new Verce.Api.Auth.LoginRequest(email, password))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await client.EnsureCsrfCookieAsync();
        return client;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
