using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verce.Api.Finance;
using Verce.Api.Inventory;
using Verce.Api.Pricing;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Finance;
using Verce.Modules.Inventory;
using Verce.Modules.Pricing;
using Verce.Platform.Identity;

namespace Verce.IntegrationTests.Finance;

[Collection(PostgresCollection.Name)]
public sealed class ExpenseHttpIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;
    public ExpenseHttpIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE finance.expense, finance.expense_category, pricing.sales_channel,
                           platform.account_setup_token, platform.user_role, platform.user_claim, platform.user_login,
                           platform.user_token, platform.audit_log, platform."user"
            RESTART IDENTITY CASCADE;
            """);
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, new Dictionary<string, string?> { ["Settings:SeedOnStartup"] = "true" });
        using var warmup = _factory.CreateHttpsClient();
        (await warmup.GetAsync("/health/live")).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Expense_CRUD_treatments_validation_concurrency_and_read_permissions_are_enforced()
    {
        var owner = await LoggedInAsync(Roles.Owner);
        var categories = await ReadAsync<IReadOnlyList<ExpenseCategoryResponse>>(await owner.GetAsync("/api/expenses/categories"));
        categories.Should().NotBeEmpty();
        var category = categories.First();
        var today = new DateOnly(2026, 9, 22);

        var operating = await CreateAsync(owner, category.Id, "Anúncios", AccountingTreatment.OPERATING_EXPENSE, today);
        var inventory = await CreateAsync(owner, category.Id, "Compra de estoque", AccountingTreatment.INVENTORY_PURCHASE, today);
        var asset = await CreateAsync(owner, category.Id, "Impressora", AccountingTreatment.ASSET_ACQUISITION, today);
        operating.AccountingTreatment.Should().Be(AccountingTreatment.OPERATING_EXPENSE);
        inventory.AccountingTreatment.Should().Be(AccountingTreatment.INVENTORY_PURCHASE);
        asset.AccountingTreatment.Should().Be(AccountingTreatment.ASSET_ACQUISITION);

        var updated = await owner.PutAsync($"/api/expenses/{operating.Id}", Request(category.Id, "Anúncios atualizados", AccountingTreatment.OPERATING_EXPENSE, today, operating.Version));
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        var current = await ReadAsync<ExpenseResponse>(updated);
        var staleUpdate = await owner.PutAsync($"/api/expenses/{operating.Id}", Request(category.Id, "Stale", AccountingTreatment.OPERATING_EXPENSE, today, operating.Version));
        staleUpdate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(staleUpdate)).Should().Be("CONCURRENCY_CONFLICT");
        var staleDelete = await owner.DeleteAsync($"/api/expenses/{operating.Id}?version={operating.Version}");
        staleDelete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await owner.DeleteAsync($"/api/expenses/{operating.Id}?version={current.Version}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var invalidMovement = await owner.PostAsync("/api/expenses", Request(category.Id, "Movimento inválido", AccountingTreatment.INVENTORY_PURCHASE, today, null, Guid.NewGuid()));
        invalidMovement.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(invalidMovement)).Should().Be("EXPENSE_INVENTORY_MOVEMENT_NOT_PURCHASE_RECEIPT");

        var viewer = await LoggedInAsync(Roles.Viewer);
        (await viewer.GetAsync("/api/expenses")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PostAsync("/api/expenses", Request(category.Id, "Bloqueada", AccountingTreatment.OPERATING_EXPENSE, today))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Filtered_unique_inventory_movement_constraint_rejects_a_second_persisted_expense_and_operator_can_manage()
    {
        var owner = await LoggedInAsync(Roles.Owner);
        var category = (await ReadAsync<IReadOnlyList<ExpenseCategoryResponse>>(await owner.GetAsync("/api/expenses/categories"))).First();
        var movement = Guid.NewGuid();
        await using (var db = _fixture.CreateContext())
        {
            db.Add(new Expense(category.Id, "Primeiro vínculo", 10m, new DateOnly(2026, 9, 22), AccountingTreatment.INVENTORY_PURCHASE, inventoryMovementId: movement));
            await db.SaveChangesAsync();
        }
        await using (var db = _fixture.CreateContext())
        {
            db.Add(new Expense(category.Id, "Vínculo duplicado", 10m, new DateOnly(2026, 9, 22), AccountingTreatment.INVENTORY_PURCHASE, inventoryMovementId: movement));
            await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
        }

        var op = await LoggedInAsync(Roles.Operator);
        (await op.GetAsync("/api/expenses")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await op.PostAsync("/api/expenses", Request(category.Id, "Operador", AccountingTreatment.OPERATING_EXPENSE, new DateOnly(2026, 9, 22)))).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Only_an_actual_PurchaseReceipt_can_be_linked_to_an_inventory_purchase_expense()
    {
        var owner = await LoggedInAsync(Roles.Owner);
        var category = (await ReadAsync<IReadOnlyList<ExpenseCategoryResponse>>(await owner.GetAsync("/api/expenses/categories"))).First();
        var supply = await ReadAsync<SupplyResponse>(await owner.PostAsync("/api/supplies",
            new SupplyCreateRequest("S8A-EXPENSE", "Insumo Expense", null, "FILAMENT", SupplyBaseUnit.Gram, null, null, null, null)));
        supply = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(100m, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, 25m, "Fornecedor", null, null, supply.Version)));
        var movements = await ReadAsync<InventoryMovementListResponse>(await owner.GetAsync($"/api/supplies/{supply.Id}/inventory/movements"));
        var receipt = movements.Items.Single(x => x.Type == InventoryMovementType.PurchaseReceipt);
        var linked = await owner.PostAsync("/api/expenses", Request(category.Id, "Compra vinculada", AccountingTreatment.INVENTORY_PURCHASE, new DateOnly(2026, 9, 22), movement: receipt.Id));
        linked.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadAsync<ExpenseResponse>(linked)).InventoryMovementId.Should().Be(receipt.Id);

        supply = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{supply.Id}/inventory/adjustment",
            new InventoryAdjustmentRequest(InventoryAdjustmentKind.Correction, 110m, SupplyBaseUnit.Gram, "Contagem", DateTimeOffset.UtcNow.AddMinutes(1), supply.Version)));
        movements = await ReadAsync<InventoryMovementListResponse>(await owner.GetAsync($"/api/supplies/{supply.Id}/inventory/movements"));
        var correction = movements.Items.Single(x => x.Type == InventoryMovementType.Correction);
        var rejectedCorrection = await owner.PostAsync("/api/expenses", Request(category.Id, "Correção", AccountingTreatment.INVENTORY_PURCHASE, new DateOnly(2026, 9, 22), movement: correction.Id));
        rejectedCorrection.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(rejectedCorrection)).Should().Be("EXPENSE_INVENTORY_MOVEMENT_NOT_PURCHASE_RECEIPT");

        var consumption = Guid.NewGuid();
        await using (var db = _fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.inventory_movement (id, supply_id, type, entered_quantity, entered_unit, quantity_delta_base_unit, occurred_at, created_at)
                VALUES ({consumption}, {supply.Id}, {"Consumption"}, {1m}, {"Gram"}, {-1m}, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow});
                """);
        var rejectedConsumption = await owner.PostAsync("/api/expenses", Request(category.Id, "Consumo", AccountingTreatment.INVENTORY_PURCHASE, new DateOnly(2026, 9, 22), movement: consumption));
        rejectedConsumption.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(rejectedConsumption)).Should().Be("EXPENSE_INVENTORY_MOVEMENT_NOT_PURCHASE_RECEIPT");

        var channel = await ReadAsync<SalesChannelResponse>(await owner.PostAsync("/api/pricing/channels",
            new SalesChannelCreateRequest("S8A-EXPENSE", "Canal Expense", SalesChannelKind.Direct, null, null)));
        var attributed = await owner.PostAsync("/api/expenses", Request(category.Id, "Marketing do canal", AccountingTreatment.OPERATING_EXPENSE, new DateOnly(2026, 9, 22), channel: channel.Id));
        attributed.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadAsync<ExpenseResponse>(attributed)).SalesChannelId.Should().Be(channel.Id);
    }

    [Fact]
    public async Task Expense_category_seed_is_idempotent_and_never_overwrites_an_existing_category()
    {
        Dictionary<Guid, string> before;
        Guid changedId;
        await using (var db = _fixture.CreateContext())
        {
            before = await db.Set<ExpenseCategory>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name);
            var category = await db.Set<ExpenseCategory>().SingleAsync(x => x.Name == "Marketing");
            changedId = category.Id;
            category.Update(category.Name, category.DefaultTreatment, false);
            await db.SaveChangesAsync();
        }
        var seed = _factory.Services.GetServices<IHostedService>().OfType<FinanceSeedService>().Single();
        await seed.StartAsync(CancellationToken.None);
        await using var reloaded = _fixture.CreateContext();
        var after = await reloaded.Set<ExpenseCategory>().AsNoTracking().ToListAsync();
        after.Should().HaveCount(before.Count);
        after.Select(x => x.Id).Should().BeEquivalentTo(before.Keys);
        after.Single(x => x.Id == changedId).IsActive.Should().BeFalse();
    }

    private static ExpenseWriteRequest Request(Guid categoryId, string description, AccountingTreatment treatment, DateOnly date, long? version = null, Guid? movement = null, Guid? channel = null) =>
        new(categoryId, description, 10m, date, treatment, null, null, null, null, movement, channel, null, null, null, version);

    private static async Task<ExpenseResponse> CreateAsync(AuthTestClient client, Guid categoryId, string description, AccountingTreatment treatment, DateOnly date)
    {
        var response = await client.PostAsync("/api/expenses", Request(categoryId, description, treatment, date));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await ReadAsync<ExpenseResponse>(response);
    }

    private async Task<AuthTestClient> LoggedInAsync(string role)
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        const string password = "a-perfectly-fine-12char-password";
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!await roles.RoleExistsAsync(role)) (await roles.CreateAsync(new ApplicationRole(role))).Succeeded.Should().BeTrue();
        var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = role, IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow };
        (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
        (await users.AddToRoleAsync(user, role)).Succeeded.Should().BeTrue();
        var client = new AuthTestClient(_factory.CreateHttpsClient());
        await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new Verce.Api.Auth.LoginRequest(email, password))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await client.EnsureCsrfCookieAsync();
        return client;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
