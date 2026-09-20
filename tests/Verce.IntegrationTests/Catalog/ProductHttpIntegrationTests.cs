using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verce.Api.Catalog;
using Verce.Api.Inventory;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Catalog;
using Verce.Modules.Inventory;
using Verce.Platform.Identity;

namespace Verce.IntegrationTests.Catalog;

/// <summary>
/// S5 mission §55: Product/Recipe HTTP surface — persistence, code uniqueness, active/inactive
/// filtering, multi-material recipes, costing reuse through the persisted recipe (never a
/// reimplementation of S4's CostEngine), authorization, CSRF and optimistic concurrency.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProductHttpIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;

    public ProductHttpIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

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
    public async Task Owner_and_Operator_can_manage_but_Viewer_and_anonymous_are_denied_writes()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var (operatorClient, _) = await LoggedInAsAsync(Roles.Operator);
        var (viewer, _) = await LoggedInAsAsync(Roles.Viewer);

        foreach (var client in new[] { owner, operatorClient, viewer })
            (await client.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await owner.PostAsync("/api/products", new ProductCreateRequest("OWNER-PROD", "Produto do Owner", null))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await operatorClient.PostAsync("/api/products", new ProductCreateRequest("OPERATOR-PROD", "Produto do Operator", null))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await viewer.PostAsync("/api/products", new ProductCreateRequest("VIEWER-PROD", "Produto do Viewer", null))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var anonymous = _factory.CreateHttpsClient();
        (await anonymous.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync("/api/products", new ProductCreateRequest("ANON-PROD", "Anônimo", null))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_without_antiforgery_is_denied()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var response = await owner.PostAsync("/api/products", new ProductCreateRequest("NO-CSRF", "Sem CSRF", null), withAntiforgery: false);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_read_and_update_round_trip_and_code_is_normalized()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var created = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("mini-vase", "Mini Vaso", "Descrição inicial")));
        created.Code.Should().Be("MINI-VASE");
        created.Active.Should().BeTrue();
        created.Recipe.MaterialLines.Should().BeEmpty();

        var fetched = await ReadAsync<ProductResponse>(await owner.GetAsync($"/api/products/{created.Id}"));
        fetched.Should().BeEquivalentTo(created);

        var updated = await ReadAsync<ProductResponse>(await owner.PutAsync($"/api/products/{created.Id}",
            new ProductUpdateRequest("Mini Vaso Grande", "Nova descrição", created.Version)));
        updated.Name.Should().Be("Mini Vaso Grande");
        updated.Description.Should().Be("Nova descrição");
        updated.Code.Should().Be("MINI-VASE");
        updated.Version.Should().Be(created.Version + 1);
    }

    [Fact]
    public async Task Duplicate_code_is_rejected_with_a_stable_conflict_code()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        (await owner.PostAsync("/api/products", new ProductCreateRequest("DUP-CODE", "Primeiro", null))).StatusCode.Should().Be(HttpStatusCode.Created);
        var conflict = await owner.PostAsync("/api/products", new ProductCreateRequest("dup-code", "Segundo", null));
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(conflict)).Should().Be("PRODUCT_CODE_ALREADY_EXISTS");
    }

    [Fact]
    public async Task Active_and_inactive_listing_filters_correctly()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var active = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("ACTIVE-1", "Ativo", null)));
        var toDeactivate = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("INACTIVE-1", "Inativo", null)));
        (await owner.PostAsync($"/api/products/{toDeactivate.Id}/deactivate?version={toDeactivate.Version}", (object?)null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var activeList = await ReadAsync<ProductListResponse>(await owner.GetAsync("/api/products?status=active"));
        activeList.Items.Should().Contain(x => x.Id == active.Id).And.NotContain(x => x.Id == toDeactivate.Id);

        var inactiveList = await ReadAsync<ProductListResponse>(await owner.GetAsync("/api/products?status=inactive"));
        inactiveList.Items.Should().Contain(x => x.Id == toDeactivate.Id).And.NotContain(x => x.Id == active.Id);

        var allList = await ReadAsync<ProductListResponse>(await owner.GetAsync("/api/products?status=all"));
        allList.Items.Should().Contain(x => x.Id == active.Id).And.Contain(x => x.Id == toDeactivate.Id);

        // Reactivation reverses the state.
        (await owner.PostAsync($"/api/products/{toDeactivate.Id}/activate?version={toDeactivate.Version + 1}", (object?)null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReadAsync<ProductListResponse>(await owner.GetAsync("/api/products?status=active"))).Items.Should().Contain(x => x.Id == toDeactivate.Id);
    }

    [Fact]
    public async Task Update_with_stale_version_is_rejected_as_a_concurrency_conflict()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("STALE-VER", "Concorrência", null)));
        (await owner.PutAsync($"/api/products/{product.Id}", new ProductUpdateRequest("Primeira edição", null, product.Version))).StatusCode.Should().Be(HttpStatusCode.OK);

        var stale = await owner.PutAsync($"/api/products/{product.Id}", new ProductUpdateRequest("Segunda edição, versão velha", null, product.Version));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(stale)).Should().Be("CONCURRENCY_CONFLICT");
    }

    [Fact]
    public async Task Multi_material_recipe_persists_and_bumps_product_version_exactly_once()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var black = await CreateSupplyAsync(owner, "PLA-BLACK-S5", "PLA Black");
        black = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{black.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(1000m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, 100m, null, "BLACK-1", null, black.Version)));
        var gold = await CreateSupplyAsync(owner, "PLA-GOLD-S5", "PLA Silk Gold");
        gold = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{gold.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(500m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, 100m, null, "GOLD-1", null, gold.Version)));

        var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("MULTI-MAT", "Multi Material", null)));
        var recipeRequest = new ProductRecipeUpdateRequest(null, 20m, 30m, 180m, 2m, 1, "Nota",
            [
                new ProductRecipeMaterialLineRequest(black.Id, 120m, SupplyBaseUnit.Gram, null, null),
                new ProductRecipeMaterialLineRequest(gold.Id, 35m, SupplyBaseUnit.Gram, null, null),
                new ProductRecipeMaterialLineRequest(black.Id, 5m, SupplyBaseUnit.Gram, 10m, null), // duplicate Supply, independent line — S4 CostEngine behavior
            ],
            [new ProductRecipeAdditionalCostLineRequest("Embalagem", 4m)],
            product.Version);
        var withRecipe = await ReadAsync<ProductResponse>(await owner.PutAsync($"/api/products/{product.Id}/recipe", recipeRequest));

        withRecipe.Version.Should().Be(product.Version + 1, "mutating an owned child bumps the root's version exactly once per Unit of Work");
        withRecipe.Recipe.MaterialLines.Should().HaveCount(3);
        withRecipe.Recipe.MaterialLines.Select(x => x.SupplyId).Should().Contain([black.Id, gold.Id]);
        withRecipe.Recipe.MaterialLines.Single(x => x.SupplyId == gold.Id).SupplyCode.Should().Be("PLA-GOLD-S5");
        withRecipe.Recipe.AdditionalCostLines.Should().ContainSingle(x => x.Description == "Embalagem" && x.Amount == 4m);
        withRecipe.Recipe.LaborMinutes.Should().Be(20m);
        withRecipe.Recipe.MachineMinutes.Should().Be(180m);

        var refetched = await ReadAsync<ProductResponse>(await owner.GetAsync($"/api/products/{product.Id}"));
        refetched.Recipe.MaterialLines.Should().HaveCount(3);
    }

    [Fact]
    public async Task Recipe_update_with_unknown_supply_is_rejected()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("BAD-SUPPLY", "Insumo inexistente", null)));
        var response = await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, null, [new ProductRecipeMaterialLineRequest(Guid.NewGuid(), 1m, SupplyBaseUnit.Gram, null, null)], [], product.Version));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemCodeAsync(response)).Should().Be("SUPPLY_NOT_FOUND");
    }

    [Fact]
    public async Task Recipe_update_rejects_a_newly_introduced_inactive_supply()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var supply = await CreateSupplyAsync(owner, "INACTIVE-NEW-S5", "Nunca usado, agora inativo");
        (await owner.PostAsync($"/api/supplies/{supply.Id}/deactivate?version={supply.Version}", (object?)null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("INACTIVE-NEW-PROD", "Produto", null)));
        var response = await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, null, [new ProductRecipeMaterialLineRequest(supply.Id, 1m, SupplyBaseUnit.Gram, null, null)], [], product.Version));
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(response)).Should().Be("SUPPLY_INACTIVE");
    }

    [Fact]
    public async Task Recipe_referencing_a_supply_that_later_becomes_inactive_remains_readable_and_editable_unchanged()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var supply = await CreateSupplyAsync(owner, "GOES-INACTIVE-S5", "Fica inativo depois");
        // Give the supply a real acquisition basis WHILE it is still active — a supply
        // legitimately accumulates purchase history before ever being retired.
        supply = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(1000m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, 100m, null, "NF-1", null, supply.Version)));

        var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("HIST-INACTIVE-PROD", "Produto", null)));
        product = await ReadAsync<ProductResponse>(await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, null, [new ProductRecipeMaterialLineRequest(supply.Id, 10m, SupplyBaseUnit.Gram, null, null)], [], product.Version)));

        (await owner.PostAsync($"/api/supplies/{supply.Id}/deactivate?version={supply.Version}", (object?)null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Reading the recipe never fails merely because a referenced Supply later went inactive.
        var afterDeactivation = await ReadAsync<ProductResponse>(await owner.GetAsync($"/api/products/{product.Id}"));
        afterDeactivation.Recipe.MaterialLines.Should().ContainSingle(x => x.SupplyId == supply.Id && !x.SupplyActive);

        // Editing an UNRELATED recipe parameter while preserving the existing inactive reference
        // (same SupplyId, just a different quantity) must not be blocked — only a NEW reference
        // to an inactive Supply is rejected.
        var edited = await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, "nota atualizada",
            [new ProductRecipeMaterialLineRequest(supply.Id, 25m, SupplyBaseUnit.Gram, null, null)], [], afterDeactivation.Version));
        edited.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await ReadAsync<ProductResponse>(edited);
        updated.Recipe.MaterialLines.Should().ContainSingle(x => x.SupplyId == supply.Id && x.EnteredQuantity == 25m);

        // Current-cost calculation still works against the historically-referenced inactive
        // Supply, using the basis it accumulated while active — matching S4's own
        // inactive-Supply consistency stance (ADR-0018/ADR-0019).
        var cost = await owner.GetAsync($"/api/products/{product.Id}/cost");
        cost.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Recipe_update_rejects_a_new_inactive_supply_line_added_alongside_a_preserved_one()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var preserved = await CreateSupplyAsync(owner, "PRESERVED-S5", "Preservado");
        var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("MIXED-INACTIVE-PROD", "Produto", null)));
        product = await ReadAsync<ProductResponse>(await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, null, [new ProductRecipeMaterialLineRequest(preserved.Id, 10m, SupplyBaseUnit.Gram, null, null)], [], product.Version)));
        (await owner.PostAsync($"/api/supplies/{preserved.Id}/deactivate?version={preserved.Version}", (object?)null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var newInactive = await CreateSupplyAsync(owner, "NEW-INACTIVE-S5", "Novo e inativo");
        (await owner.PostAsync($"/api/supplies/{newInactive.Id}/deactivate?version={newInactive.Version}", (object?)null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, null,
            [
                new ProductRecipeMaterialLineRequest(preserved.Id, 10m, SupplyBaseUnit.Gram, null, null),
                new ProductRecipeMaterialLineRequest(newInactive.Id, 5m, SupplyBaseUnit.Gram, null, null),
            ], [], product.Version));
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(response)).Should().Be("SUPPLY_INACTIVE");
    }

    /// <summary>Creates a fresh product whose recipe references <paramref name="supplyId"/>
    /// exactly <paramref name="count"/> times (all lines otherwise identical), for the Terra
    /// B-04-R cardinality regressions below.</summary>
    private async Task<ProductResponse> CreateProductWithRepeatedSupplyLineAsync(AuthTestClient owner, string productCode, Guid supplyId, int count)
    {
        var lines = Enumerable.Range(0, count).Select(_ => new ProductRecipeMaterialLineRequest(supplyId, 1m, SupplyBaseUnit.Gram, null, null)).ToList();
        var created = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest(productCode, productCode, null)));
        var response = await owner.PutAsync($"/api/products/{created.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, null, lines, [], created.Version));
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"a recipe with {count} line(s) against the same Supply must be accepted (duplicate lines are legal, ADR-0019 §1)");
        return await ReadAsync<ProductResponse>(response);
    }

    [Fact]
    public async Task Recipe_cardinality_regression_one_existing_inactive_line_rejects_a_submission_with_two()
    {
        // The exact regression Terra identified: a HashSet<SupplyId>-only preservation check
        // would let this through, since the submitted Supply "existed before" — but the SECOND
        // line is a genuinely NEW inactive reference the cardinality rule must catch.
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var supply = await CreateSupplyAsync(owner, "CARD-X1-S5", "Cardinalidade X1");
        var product = await CreateProductWithRepeatedSupplyLineAsync(owner, "CARD-X1-PROD", supply.Id, count: 1);
        (await owner.PostAsync($"/api/supplies/{supply.Id}/deactivate?version={supply.Version}", (object?)null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, null,
            [
                new ProductRecipeMaterialLineRequest(supply.Id, 1m, SupplyBaseUnit.Gram, null, null),
                new ProductRecipeMaterialLineRequest(supply.Id, 1m, SupplyBaseUnit.Gram, null, null),
            ], [], product.Version));
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(response)).Should().Be("SUPPLY_INACTIVE");
    }

    [Fact]
    public async Task Recipe_cardinality_regression_two_existing_inactive_lines_allow_two_or_fewer_but_reject_three()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var supply = await CreateSupplyAsync(owner, "CARD-X2-S5", "Cardinalidade X2");
        var product = await CreateProductWithRepeatedSupplyLineAsync(owner, "CARD-X2-PROD", supply.Id, count: 2);
        (await owner.PostAsync($"/api/supplies/{supply.Id}/deactivate?version={supply.Version}", (object?)null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // 2 -> 3: rejected — increases the inactive reference count.
        var toThree = await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, null,
            [
                new ProductRecipeMaterialLineRequest(supply.Id, 1m, SupplyBaseUnit.Gram, null, null),
                new ProductRecipeMaterialLineRequest(supply.Id, 1m, SupplyBaseUnit.Gram, null, null),
                new ProductRecipeMaterialLineRequest(supply.Id, 1m, SupplyBaseUnit.Gram, null, null),
            ], [], product.Version));
        toThree.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(toThree)).Should().Be("SUPPLY_INACTIVE");

        // 2 -> 2: allowed — cardinality unchanged (proves this is a real cardinality rule, not a
        // one-off "count > 1" patch that would have also rejected this).
        var stillTwo = await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, "sem alteração de contagem",
            [
                new ProductRecipeMaterialLineRequest(supply.Id, 2m, SupplyBaseUnit.Gram, null, null),
                new ProductRecipeMaterialLineRequest(supply.Id, 2m, SupplyBaseUnit.Gram, null, null),
            ], [], product.Version));
        stillTwo.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterStillTwo = await ReadAsync<ProductResponse>(stillTwo);
        afterStillTwo.Recipe.MaterialLines.Should().HaveCount(2).And.OnlyContain(x => x.SupplyId == supply.Id && x.EnteredQuantity == 2m);

        // 2 -> 1: allowed — removing an inactive reference must never be blocked.
        var toOne = await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, null,
            [new ProductRecipeMaterialLineRequest(supply.Id, 1m, SupplyBaseUnit.Gram, null, null)], [], afterStillTwo.Version));
        toOne.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync<ProductResponse>(toOne)).Recipe.MaterialLines.Should().ContainSingle(x => x.SupplyId == supply.Id);
    }

    [Fact]
    public async Task Recipe_cardinality_removal_of_an_inactive_supply_reference_down_to_zero_is_allowed()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var supply = await CreateSupplyAsync(owner, "CARD-X-ZERO-S5", "Cardinalidade zero");
        var product = await CreateProductWithRepeatedSupplyLineAsync(owner, "CARD-X-ZERO-PROD", supply.Id, count: 1);
        (await owner.PostAsync($"/api/supplies/{supply.Id}/deactivate?version={supply.Version}", (object?)null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, null, [], [], product.Version));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync<ProductResponse>(response)).Recipe.MaterialLines.Should().BeEmpty();
    }

    [Fact]
    public async Task Recipe_cardinality_does_not_restrict_duplicate_lines_against_an_active_supply()
    {
        // Duplicate lines are intentionally legal (ADR-0019 §1) — the cardinality rule must apply
        // ONLY to inactive Supplies, never accidentally throttle an active one.
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var supply = await CreateSupplyAsync(owner, "CARD-ACTIVE-S5", "Ativo com duplicatas");
        var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("CARD-ACTIVE-PROD", "Produto", null)));

        var response = await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            null, null, null, null, null, 1, null,
            [
                new ProductRecipeMaterialLineRequest(supply.Id, 1m, SupplyBaseUnit.Gram, null, null),
                new ProductRecipeMaterialLineRequest(supply.Id, 1m, SupplyBaseUnit.Gram, null, null),
                new ProductRecipeMaterialLineRequest(supply.Id, 1m, SupplyBaseUnit.Gram, null, null),
            ], [], product.Version));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync<ProductResponse>(response)).Recipe.MaterialLines.Should().HaveCount(3);
    }

    [Fact]
    public async Task Cost_endpoint_reuses_the_S4_CostEngine_and_leaves_inventory_untouched()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var supply = await CreateSupplyAsync(owner, "MAT-COST-S5", "Material para custo");
        supply = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(1000m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, 100m, null, "NF-1", null, supply.Version)));

        var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("COST-PROD", "Produto custeado", null)));
        var withRecipe = await ReadAsync<ProductResponse>(await owner.PutAsync($"/api/products/{product.Id}/recipe", new ProductRecipeUpdateRequest(
            5m, 20m, 30m, 180m, 2m, 2, null,
            [new ProductRecipeMaterialLineRequest(supply.Id, 100m, SupplyBaseUnit.Gram, null, null)],
            [new ProductRecipeAdditionalCostLineRequest("Embalagem", 4m)],
            product.Version)));

        await using var beforeDb = _fixture.CreateContext();
        var before = await beforeDb.Set<Supply>().AsNoTracking().SingleAsync(x => x.Id == supply.Id);

        var costResponse = await owner.GetAsync($"/api/products/{withRecipe.Id}/cost");
        costResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var cost = await ReadAsync<Verce.Modules.Costing.CostCalculationResult>(costResponse);

        // 100g @ weighted-average 0.10/g, 5% wastage => 10.50; labor 20min@30/h = 10; machine 180min@2/h = 6; additional 4.
        cost.Materials.Should().ContainSingle();
        cost.Materials[0].CostAfterWastage.Should().Be(10.5m);
        cost.Totals.LaborCost.Should().Be(10m);
        cost.Totals.MachineCost.Should().Be(6m);
        cost.Totals.AdditionalDirectCosts.Should().Be(4m);
        cost.Totals.TotalEstimatedCost.Should().Be(30.5m);
        cost.Totals.EstimatedUnitCost.Should().Be(15.25m); // output quantity = 2

        await using var afterDb = _fixture.CreateContext();
        var after = await afterDb.Set<Supply>().AsNoTracking().SingleAsync(x => x.Id == supply.Id);
        after.CurrentStockBaseUnit.Should().Be(before.CurrentStockBaseUnit);
        after.Version.Should().Be(before.Version);
    }

    [Fact]
    public async Task Cost_endpoint_on_an_empty_recipe_is_rejected_as_unprocessable()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var product = await ReadAsync<ProductResponse>(await owner.PostAsync("/api/products", new ProductCreateRequest("EMPTY-RECIPE", "Sem receita", null)));
        var response = await owner.GetAsync($"/api/products/{product.Id}/cost");
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(response)).Should().Be("RECIPE_INVALID");
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
