using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verce.Api.Costing;
using Verce.Api.Inventory;
using Verce.Api.Settings;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Costing;
using Verce.Modules.Inventory;
using Verce.Platform.Identity;

namespace Verce.IntegrationTests.Costing;

[Collection(PostgresCollection.Name)]
public sealed class CostingHttpIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;

    public CostingHttpIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE inventory.inventory_movement, inventory.supply, settings.app_setting,
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
    public async Task Owner_Operator_and_Viewer_can_read_and_calculate_but_anonymous_is_denied()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var (operatorClient, _) = await LoggedInAsAsync(Roles.Operator);
        var (viewer, _) = await LoggedInAsAsync(Roles.Viewer);
        var request = AdditionalOnly(12m);

        foreach (var client in new[] { owner, operatorClient, viewer })
        {
            (await client.GetAsync("/api/costing/supplies?includeInactive=false")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.PostAsync("/api/costing/calculate", request)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var anonymous = _factory.CreateHttpsClient();
        (await anonymous.GetAsync("/api/costing/supplies?includeInactive=false")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync("/api/costing/calculate", request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Authenticated_cost_calculation_without_antiforgery_is_denied()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var response = await owner.PostAsync("/api/costing/calculate", AdditionalOnly(12m), withAntiforgery: false);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(response)).Should().Be("ANTIFORGERY_VALIDATION_FAILED");
    }

    [Fact]
    public async Task Weighted_average_uses_only_purchase_receipts_and_calculation_has_no_inventory_side_effect()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var supply = await CreateSupplyAsync(owner, "MAT-WEIGHTED", "Material ponderado");
        supply = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(1000m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, 100m, null, "NF-1", null, supply.Version)));
        supply = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(500m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, 75m, null, "NF-2", null, supply.Version)));
        supply = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/adjustment",
            new InventoryAdjustmentRequest(InventoryAdjustmentKind.Increase, 100m, SupplyBaseUnit.Gram, "Ajuste sem custo", DateTimeOffset.UtcNow, supply.Version)));

        var basis = await ReadAsync<SupplyCostBasisResponse>(await owner.GetAsync($"/api/costing/supplies/{supply.Id}/cost-basis"));
        basis.WeightedAverageUnitCost.Should().Be(0.116667m);
        basis.EligibleQuantityBaseUnit.Should().Be(1500m);
        basis.EligibleReceiptCount.Should().Be(2);

        await using var beforeDb = _fixture.CreateContext();
        var before = await beforeDb.Set<Supply>().AsNoTracking().SingleAsync(item => item.Id == supply.Id);
        var beforeMovementCount = await beforeDb.Set<InventoryMovement>().CountAsync(item => item.SupplyId == supply.Id);
        var beforeStock = before.CurrentStockBaseUnit;
        var beforeVersion = before.Version;

        var response = await owner.PostAsync("/api/costing/calculate", new CostCalculationRequest(
            [new CostingMaterialRequest(supply.Id, 100m, SupplyBaseUnit.Gram, 5m, null)],
            null, null, null, [], 1, false));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadAsync<CostCalculationResult>(response);
        result.Materials.Should().ContainSingle();
        result.Materials[0].UnitCostBaseUnit.Should().Be(0.116667m);
        result.Materials[0].CostBeforeWastage.Should().Be(11.6667m);
        result.Materials[0].WastageCost.Should().Be(0.583335m);
        result.Materials[0].CostAfterWastage.Should().Be(12.250035m);
        result.Materials[0].CostSource.Should().Be(MaterialCostSource.WEIGHTED_AVERAGE_ACQUISITION);

        await using var afterDb = _fixture.CreateContext();
        var after = await afterDb.Set<Supply>().AsNoTracking().SingleAsync(item => item.Id == supply.Id);
        after.CurrentStockBaseUnit.Should().Be(beforeStock);
        after.Version.Should().Be(beforeVersion);
        (await afterDb.Set<InventoryMovement>().CountAsync(item => item.SupplyId == supply.Id)).Should().Be(beforeMovementCount);
    }

    [Fact]
    public async Task Inactive_and_missing_basis_paths_are_explicit_and_manual_override_remains_stateless()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var inactive = await CreateSupplyAsync(owner, "MAT-INACTIVE", "Material inativo");
        inactive = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{inactive.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(10m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, 10m, null, null, null, inactive.Version)));
        (await owner.PostAsync($"/api/supplies/{inactive.Id}/deactivate?version={inactive.Version}", (object?)null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var activePicker = await ReadAsync<IReadOnlyList<CostingSupplyListItemResponse>>(await owner.GetAsync("/api/costing/supplies?includeInactive=false"));
        activePicker.Should().NotContain(item => item.Id == inactive.Id);
        var historicalBasis = await ReadAsync<SupplyCostBasisResponse>(await owner.GetAsync($"/api/costing/supplies/{inactive.Id}/cost-basis"));
        historicalBasis.Active.Should().BeFalse();
        historicalBasis.Available.Should().BeTrue();

        var inactiveRequest = new CostCalculationRequest([new(inactive.Id, 1m, SupplyBaseUnit.Gram, null, null)], null, null, null, [], 1, false);
        (await owner.PostAsync("/api/costing/calculate", inactiveRequest)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PostAsync("/api/costing/calculate", inactiveRequest with { IncludeInactiveSupplies = true })).StatusCode.Should().Be(HttpStatusCode.OK);

        var noBasis = await CreateSupplyAsync(owner, "MAT-NO-BASIS", "Sem custo");
        var unavailable = await owner.PostAsync("/api/costing/calculate", new CostCalculationRequest(
            [new(noBasis.Id, 1m, SupplyBaseUnit.Gram, null, null)], null, null, null, [], 1, false));
        unavailable.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(unavailable)).Should().Be("COST_BASIS_UNAVAILABLE");

        var manual = await ReadAsync<CostCalculationResult>(await owner.PostAsync("/api/costing/calculate", new CostCalculationRequest(
            [new(noBasis.Id, 2m, SupplyBaseUnit.Gram, null, 3.5m)], null, null, null, [], 1, false)));
        manual.Materials[0].CostSource.Should().Be(MaterialCostSource.MANUAL_OVERRIDE);
        manual.Totals.TotalEstimatedCost.Should().Be(7m);
    }

    [Fact]
    public async Task Setting_defaults_and_override_precedence_are_typed_and_never_mutated_by_calculation()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        await UpdateSettingAsync(owner, "costing.default_wastage_rate", "10");
        await UpdateSettingAsync(owner, "costing.default_labor_hourly_rate", "40");
        var supply = await CreateSupplyAsync(owner, "MAT-DEFAULTS", "Padrões");
        supply = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(100m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, 1m, null, null, null, null, supply.Version)));

        async Task<CostCalculationResult> Calculate(decimal? scenario, decimal? line, decimal? laborRate) =>
            await ReadAsync<CostCalculationResult>(await owner.PostAsync("/api/costing/calculate", new CostCalculationRequest(
                [new(supply.Id, 10m, SupplyBaseUnit.Gram, line, null)], scenario,
                new CostingLaborRequest(30m, laborRate), null, [], 1, false)));

        var configured = await Calculate(null, null, null);
        configured.Materials[0].WastagePercent.Should().Be(10m);
        configured.Labor!.HourlyRate.Should().Be(40m);
        configured.Labor.RateSource.Should().Be(LaborRateSource.DEFAULT_SETTING);

        var scenario = await Calculate(5m, null, null);
        scenario.Materials[0].WastagePercent.Should().Be(5m);

        var explicitZero = await Calculate(5m, 0m, null);
        explicitZero.Materials[0].WastagePercent.Should().Be(0m);
        explicitZero.Materials[0].WastageCost.Should().Be(0m);

        var line = await Calculate(5m, 2m, 60m);
        line.Materials[0].WastagePercent.Should().Be(2m);
        line.Labor!.HourlyRate.Should().Be(60m);
        line.Labor.RateSource.Should().Be(LaborRateSource.MANUAL_OVERRIDE);

        var settings = await ReadAsync<AppSettingResponse[]>(await owner.GetAsync("/api/settings"));
        settings.Single(item => item.Key == "costing.default_wastage_rate").Value.Should().Be("10");
        settings.Single(item => item.Key == "costing.default_labor_hourly_rate").Value.Should().Be("40");

        await using (var db = _fixture.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE settings.app_setting SET value = {"x"} WHERE key = {"costing.default_labor_hourly_rate"}");
        }
        var invalidConfiguration = await owner.PostAsync("/api/costing/calculate", AdditionalOnly(1m) with { Labor = new(1m, null) });
        invalidConfiguration.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await ProblemCodeAsync(invalidConfiguration)).Should().Be("COSTING_CONFIGURATION_INVALID");
    }

    [Fact]
    public async Task Realistic_two_material_batch_is_explainable_and_leaves_both_ledgers_unchanged()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var black = await CreateSupplyAsync(owner, "PLA-BLACK-S4", "PLA Black");
        black = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{black.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(1000m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, 100m, null, "BLACK-1", null, black.Version)));
        var gold = await CreateSupplyAsync(owner, "PLA-GOLD-S4", "PLA Silk Gold");
        gold = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{gold.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(500m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, 100m, null, "GOLD-1", null, gold.Version)));

        await using var beforeDb = _fixture.CreateContext();
        var before = await beforeDb.Set<Supply>().AsNoTracking().Where(item => item.Id == black.Id || item.Id == gold.Id)
            .ToDictionaryAsync(item => item.Id);
        var movementsBefore = await beforeDb.Set<InventoryMovement>().CountAsync(item => item.SupplyId == black.Id || item.SupplyId == gold.Id);

        var response = await owner.PostAsync("/api/costing/calculate", new CostCalculationRequest(
            [
                new(black.Id, 120m, SupplyBaseUnit.Gram, null, null),
                new(gold.Id, 35m, SupplyBaseUnit.Gram, null, null),
            ],
            5m,
            new CostingLaborRequest(20m, 30m),
            new CostingMachineRequest(180m, 2m),
            [new("Packaging", 4m)],
            2,
            false));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadAsync<CostCalculationResult>(response);

        result.Materials.Should().HaveCount(2);
        result.Materials.Single(line => line.SupplyId == black.Id).Should().Match<MaterialCostBreakdown>(line =>
            line.EnteredQuantity == 120m && line.NormalizedQuantityBaseUnit == 120m
            && line.WastagePercent == 5m && line.EffectiveQuantityBaseUnit == 126m
            && line.UnitCostBaseUnit == 0.1m && line.CostAfterWastage == 12.6m
            && line.CostSource == MaterialCostSource.WEIGHTED_AVERAGE_ACQUISITION);
        result.Materials.Single(line => line.SupplyId == gold.Id).Should().Match<MaterialCostBreakdown>(line =>
            line.EnteredQuantity == 35m && line.NormalizedQuantityBaseUnit == 35m
            && line.WastagePercent == 5m && line.EffectiveQuantityBaseUnit == 36.75m
            && line.UnitCostBaseUnit == 0.2m && line.CostAfterWastage == 7.35m
            && line.CostSource == MaterialCostSource.WEIGHTED_AVERAGE_ACQUISITION);
        result.Totals.MaterialCostBeforeWastage.Should().Be(19m);
        result.Totals.MaterialWastageCost.Should().Be(0.95m);
        result.Totals.MaterialsTotalCost.Should().Be(19.95m);
        result.Totals.LaborCost.Should().Be(10m);
        result.Totals.MachineCost.Should().Be(6m);
        result.Totals.AdditionalDirectCosts.Should().Be(4m);
        result.Totals.TotalEstimatedCost.Should().Be(39.95m);
        result.Totals.EstimatedUnitCost.Should().Be(19.975m);

        await using var afterDb = _fixture.CreateContext();
        var after = await afterDb.Set<Supply>().AsNoTracking().Where(item => item.Id == black.Id || item.Id == gold.Id)
            .ToDictionaryAsync(item => item.Id);
        after[black.Id].CurrentStockBaseUnit.Should().Be(before[black.Id].CurrentStockBaseUnit);
        after[black.Id].Version.Should().Be(before[black.Id].Version);
        after[gold.Id].CurrentStockBaseUnit.Should().Be(before[gold.Id].CurrentStockBaseUnit);
        after[gold.Id].Version.Should().Be(before[gold.Id].Version);
        (await afterDb.Set<InventoryMovement>().CountAsync(item => item.SupplyId == black.Id || item.SupplyId == gold.Id))
            .Should().Be(movementsBefore);
    }

    private static CostCalculationRequest AdditionalOnly(decimal amount) =>
        new([], null, null, null, [new("Serviço", amount)], 1, false);

    private async Task UpdateSettingAsync(AuthTestClient owner, string key, string value)
    {
        var settings = await ReadAsync<AppSettingResponse[]>(await owner.GetAsync("/api/settings"));
        var setting = settings.Single(item => item.Key == key);
        (await owner.PutAsync($"/api/settings/{key}", new SettingUpdateRequest(value, setting.Version))).StatusCode.Should().Be(HttpStatusCode.NoContent);
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
        var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = role + " S4", IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow };
        (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
        (await users.AddToRoleAsync(user, role)).Succeeded.Should().BeTrue();
        var client = new AuthTestClient(_factory.CreateHttpsClient());
        await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new Verce.Api.Auth.LoginRequest(email, password))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await client.EnsureCsrfCookieAsync();
        return (client, user.Id);
    }
}
