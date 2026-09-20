using System.Net;
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
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Pricing;

/// <summary>
/// Terra B-02: FeeRuleVersion applicability must resolve against the ORGANIZATION business date
/// (America/Sao_Paulo, via <see cref="IClock.OrganizationToday"/>), never <c>UtcNow.Date</c> —
/// a real UTC/BRT calendar-boundary instant is used here so the regression is proven through the
/// actual Product Pricing / FeeRule resolution HTTP path, not merely a unit test of the clock
/// helper itself. Isolated into its own test class/factory because it needs a controlled
/// <see cref="IClock"/> that every OTHER Pricing test must not share.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PricingOrganizationDateHttpIntegrationTests : IAsyncLifetime
{
    // 2026-06-15T01:30:00Z is 2026-06-14T22:30:00-03:00 in America/Sao_Paulo (no DST in Brazil
    // since 2019) — the UTC calendar date (June 15) is ONE DAY AHEAD of the organization's own
    // calendar date (June 14). The original bug (clock.UtcNow.Date) would resolve June 15 here.
    private static readonly DateTimeOffset BoundaryInstant = new(2026, 6, 15, 1, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly UtcDateAtBoundary = DateOnly.FromDateTime(BoundaryInstant.UtcDateTime); // 2026-06-15 (WRONG if used)
    private static readonly DateOnly OrganizationDateAtBoundary = new(2026, 6, 14); // CORRECT — what OrganizationToday() must return

    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;

    public PricingOrganizationDateHttpIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

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
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString,
            new Dictionary<string, string?> { ["Settings:SeedOnStartup"] = "true" },
            clockOverride: new FixedClock(BoundaryInstant));
        using var warmup = _factory.CreateHttpsClient();
        (await warmup.GetAsync("/health/live")).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public void The_fixed_clock_genuinely_disagrees_between_UTC_and_organization_dates()
    {
        // Sanity check on the test's own premise — if this ever fails, the boundary instant no
        // longer exercises the bug and must be re-picked, not the assertions below relaxed.
        UtcDateAtBoundary.Should().Be(new DateOnly(2026, 6, 15));
        OrganizationDateAtBoundary.Should().Be(new DateOnly(2026, 6, 14));
        UtcDateAtBoundary.Should().NotBe(OrganizationDateAtBoundary);
    }

    [Fact]
    public async Task Product_pricing_resolves_the_fee_version_valid_on_the_organization_date_not_the_UTC_date()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var channel = await ReadAsync<SalesChannelResponse>(await owner.PostAsync("/api/pricing/channels",
            new SalesChannelCreateRequest("BOUNDARY-CH", "Canal de fronteira", SalesChannelKind.Marketplace, null, null)));
        await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule", new FeeRuleCreateRequest("Regra"));

        // Version A covers ... up to and NOT including the UTC boundary date (June 15).
        // Version B starts exactly at the UTC boundary date. If the endpoint used UtcNow.Date, it
        // would select B (wrong); using OrganizationToday() (June 14) it must select A.
        await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule/versions", new FeeRuleVersionCreateRequest(
            new DateOnly(2026, 1, 1), UtcDateAtBoundary, 0.10m, 5m, FixedFeeApplication.PerUnit, null, null, "Version A", false));
        await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule/versions", new FeeRuleVersionCreateRequest(
            UtcDateAtBoundary, null, 0.20m, 8m, FixedFeeApplication.PerUnit, null, null, "Version B", false));

        var product = await SeedPricedProductAsync(owner);
        var price = await ReadAsync<ProductPriceResponse>(await owner.PostAsync($"/api/pricing/products/{product.Id}/price",
            new ProductPriceRequest(channel.Id, 0.20m)));

        price.OrganizationDate.Should().Be(OrganizationDateAtBoundary);
        price.CommissionPercent.Should().Be(0.10m, "the organization date (June 14) is still covered by Version A, not the UTC date's Version B");
        price.FixedFee.Should().Be(5m);
    }

    [Fact]
    public async Task Adjacent_versions_split_exactly_at_the_organization_date_boundary()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var channel = await ReadAsync<SalesChannelResponse>(await owner.PostAsync("/api/pricing/channels",
            new SalesChannelCreateRequest("ADJACENT-CH", "Canal adjacente", SalesChannelKind.Marketplace, null, null)));
        await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule", new FeeRuleCreateRequest("Regra"));

        var boundary = OrganizationDateAtBoundary; // 2026-06-14
        await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule/versions", new FeeRuleVersionCreateRequest(
            boundary.AddDays(-1), boundary, 0.10m, 5m, FixedFeeApplication.PerUnit, null, null, "Before boundary", false));
        await owner.PostAsync($"/api/pricing/channels/{channel.Id}/fee-rule/versions", new FeeRuleVersionCreateRequest(
            boundary, null, 0.20m, 8m, FixedFeeApplication.PerUnit, null, null, "At boundary", false));

        var product = await SeedPricedProductAsync(owner);
        // The fixed clock's organization date IS the boundary itself (2026-06-14) — [from, until)
        // semantics mean the boundary belongs to the version that STARTS there, not the one that
        // ends there.
        var price = await ReadAsync<ProductPriceResponse>(await owner.PostAsync($"/api/pricing/products/{product.Id}/price",
            new ProductPriceRequest(channel.Id, 0.10m)));
        price.CommissionPercent.Should().Be(0.20m, "ValidFrom is inclusive — the organization date at the boundary belongs to the version starting there");
    }

    private async Task<ProductResponse> SeedPricedProductAsync(AuthTestClient owner)
    {
        var supplyResponse = await owner.PostAsync("/api/supplies", new SupplyCreateRequest(
            "MAT-BOUNDARY-S5", "Material de fronteira", null, "FILAMENT", SupplyBaseUnit.Gram, null, null, null, null));
        var supply = await ReadAsync<SupplyResponse>(supplyResponse);
        supply = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(1000m, SupplyBaseUnit.Gram, BoundaryInstant, null, 100m, null, "NF-1", null, supply.Version)));
        var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("BOUNDARY-PROD", "Produto de fronteira", null)));
        return await ReadAsync<ProductResponse>(await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, null,
            [new ProductRecipeMaterialLineRequest(supply.Id, 100m, SupplyBaseUnit.Gram, null, null)], [], product.Version)));
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
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

/// <summary>Deterministic <see cref="IClock"/> test double — same real timezone-conversion logic
/// as <see cref="SystemClock"/>, but at a caller-chosen fixed instant instead of the real wall
/// clock, so a UTC/organization-date boundary can be exercised on demand.</summary>
internal sealed class FixedClock(DateTimeOffset utcNow) : IClock
{
    private readonly TimeZoneInfo _organizationTimeZone = TimeZoneInfo.FindSystemTimeZoneById(SystemClock.DefaultOrganizationTimeZoneId);

    public DateTimeOffset UtcNow { get; } = utcNow;
    public string OrganizationTimeZoneId => SystemClock.DefaultOrganizationTimeZoneId;

    public DateOnly OrganizationToday() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(UtcNow, _organizationTimeZone).DateTime);
}
