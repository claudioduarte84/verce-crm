using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verce.Api.Inventory;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Inventory;
using Verce.Platform.Identity;
using Verce.Platform.UnitOfWork;

namespace Verce.IntegrationTests.Inventory;

/// <summary>Real-host S3 acceptance tests: PostgreSQL, the production authorization pipeline,
/// antiforgery and the audit interceptor are all exercised together, mirroring
/// <c>Verce.IntegrationTests.S2.S2HttpIntegrationTests</c>'s conventions.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SupplyHttpIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;

    public SupplyHttpIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE inventory.inventory_movement, inventory.supply,
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

    // ---- Authorization matrix (mission §47) ----

    [Fact]
    public async Task Owner_and_Operator_can_manage_supplies_Viewer_can_only_read()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var (operatorClient, _) = await LoggedInAsAsync(Roles.Operator);
        var (viewer, _) = await LoggedInAsAsync(Roles.Viewer);

        (await owner.PostAsync("/api/supplies", CreateRequest("FIL-OWNER"))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await operatorClient.PostAsync("/api/supplies", CreateRequest("FIL-OPERATOR"))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await viewer.PostAsync("/api/supplies", CreateRequest("FIL-VIEWER"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await viewer.GetAsync("/api/supplies")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await operatorClient.GetAsync("/api/supplies")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Anonymous_requests_are_rejected()
    {
        using var anonymous = _factory.CreateHttpsClient();
        (await anonymous.GetAsync("/api/supplies")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- CRUD, search, pagination, active/inactive (mission §26/§47) ----

    [Fact]
    public async Task Create_read_update_and_duplicate_code_is_rejected()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var created = await CreateSupplyAsync(owner, "FIL-PLA-PRETO", "PLA Preto");

        var getResponse = await owner.GetAsync($"/api/supplies/{created.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var fetched = await ReadAsync<SupplyResponse>(getResponse);
        fetched.Code.Should().Be("FIL-PLA-PRETO");

        var update = new SupplyUpdateRequest("PLA Preto Fosco", "Atualizado", "FILAMENT", 50, "Fornecedor Y", null, null, created.Version);
        var updateResponse = await owner.PutAsync($"/api/supplies/{created.Id}", update);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync<SupplyResponse>(updateResponse)).Name.Should().Be("PLA Preto Fosco");

        var duplicate = await owner.PostAsync("/api/supplies", CreateRequest("fil-pla-preto"));
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict, "codes must be unique case-insensitively");
    }

    [Fact]
    public async Task Update_with_stale_version_is_rejected()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var created = await CreateSupplyAsync(owner, "FIL-STALE", "Stale");
        var stale = new SupplyUpdateRequest("Renomeado", null, "FILAMENT", null, null, null, null, created.Version);
        await owner.PutAsync($"/api/supplies/{created.Id}", stale);
        var conflict = await owner.PutAsync($"/api/supplies/{created.Id}", stale);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Supply_status_filter_defaults_to_active_and_explicitly_supports_inactive_and_all()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var active = await CreateSupplyAsync(owner, "FIL-ACTIVE", "Ativo");
        var inactive = await CreateSupplyAsync(owner, "FIL-INACTIVE", "Inativo");
        (await owner.PostAsync($"/api/supplies/{inactive.Id}/deactivate?version={inactive.Version}", (object?)null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var defaultList = await ReadAsync<SupplyListResponse>(await owner.GetAsync("/api/supplies"));
        defaultList.Items.Should().Contain(x => x.Id == active.Id);
        defaultList.Items.Should().NotContain(x => x.Id == inactive.Id);

        var activeList = await ReadAsync<SupplyListResponse>(await owner.GetAsync("/api/supplies?status=active"));
        activeList.Items.Should().Contain(x => x.Id == active.Id);
        activeList.Items.Should().NotContain(x => x.Id == inactive.Id);

        var inactiveList = await ReadAsync<SupplyListResponse>(await owner.GetAsync("/api/supplies?status=inactive"));
        inactiveList.Items.Should().ContainSingle(x => x.Id == inactive.Id);

        var allList = await ReadAsync<SupplyListResponse>(await owner.GetAsync("/api/supplies?status=all"));
        allList.Items.Should().Contain(x => x.Id == active.Id).And.Contain(x => x.Id == inactive.Id);

        var detail = await ReadAsync<SupplyResponse>(await owner.GetAsync($"/api/supplies/{inactive.Id}"));
        detail.Active.Should().BeFalse();

        var reactivate = await owner.PostAsync($"/api/supplies/{inactive.Id}/activate?version={detail.Version}", (object?)null);
        reactivate.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Search_and_category_filter_narrow_the_list()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        await CreateSupplyAsync(owner, "FIL-SEARCHABLE", "Filamento Pesquisável");
        await CreateSupplyAsync(owner, "EMB-CAIXA", "Caixa de papelão", categoryCode: "PACKAGING", unit: SupplyBaseUnit.Unit);

        var bySearch = await ReadAsync<SupplyListResponse>(await owner.GetAsync("/api/supplies?search=Pesquis"));
        bySearch.Items.Should().ContainSingle(x => x.Code == "FIL-SEARCHABLE");

        var byCategory = await ReadAsync<SupplyListResponse>(await owner.GetAsync("/api/supplies?categoryCode=PACKAGING"));
        byCategory.Items.Should().OnlyContain(x => x.CategoryCode == "PACKAGING");
    }

    [Fact]
    public async Task Pagination_is_stable_and_covers_every_row_exactly_once()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        for (var i = 0; i < 12; i++) await CreateSupplyAsync(owner, $"FIL-PAGE-{i:00}", $"Página {i:00}");

        var seen = new HashSet<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            var response = await ReadAsync<SupplyListResponse>(await owner.GetAsync($"/api/supplies?pageSize=5&page={page}&search=FIL-PAGE"));
            foreach (var item in response.Items) seen.Add(item.Id).Should().BeTrue($"page {page} must never repeat an item from an earlier page");
        }
        seen.Should().HaveCount(12);
    }

    // ---- Inventory: initial balance, purchase receipt, adjustment, insufficient stock ----

    [Fact]
    public async Task Initial_balance_can_only_be_recorded_once()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var created = await CreateSupplyAsync(owner, "FIL-INITIAL", "Saldo inicial");

        var first = await owner.PostAsync($"/api/supplies/{created.Id}/inventory/initial-balance",
            new InitialBalanceRequest(500, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, "NF-1", null, created.Version));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterFirst = await ReadAsync<SupplyResponse>(first);
        afterFirst.CurrentStockBaseUnit.Should().Be(500);

        var second = await owner.PostAsync($"/api/supplies/{created.Id}/inventory/initial-balance",
            new InitialBalanceRequest(100, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, null, afterFirst.Version));
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Purchase_receipt_converts_kilograms_to_grams_and_records_a_movement()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var created = await CreateSupplyAsync(owner, "FIL-PURCHASE", "Compra");

        var receipt = await owner.PostAsync($"/api/supplies/{created.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(1, SupplyBaseUnit.Kilogram, DateTimeOffset.UtcNow, null, 89.90m, "Fornecedor X", "NF-42", null, created.Version));
        receipt.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterReceipt = await ReadAsync<SupplyResponse>(receipt);
        afterReceipt.CurrentStockBaseUnit.Should().Be(1000);
        afterReceipt.LatestPurchaseUnitCost.Should().Be(0.0899m);

        var movements = await ReadAsync<InventoryMovementListResponse>(await owner.GetAsync($"/api/supplies/{created.Id}/inventory/movements"));
        movements.Items.Should().ContainSingle(x => x.Type == InventoryMovementType.PurchaseReceipt && x.QuantityDeltaBaseUnit == 1000);
        var movement = movements.Items.Single(x => x.Type == InventoryMovementType.PurchaseReceipt);
        movement.EnteredQuantity.Should().Be(1);
        movement.EnteredUnit.Should().Be(SupplyBaseUnit.Kilogram);
        movement.BaseUnit.Should().Be(SupplyBaseUnit.Gram);
        movement.TotalCostSnapshot.Should().Be(89.90m);
        movement.UnitCostSnapshot.Should().Be(0.0899m);
    }

    [Fact]
    public async Task Zero_after_normalization_is_a_bad_request_for_every_inventory_command_and_never_posts()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var created = await CreateSupplyAsync(owner, "MAT-PRECISION", "Precisão", unit: SupplyBaseUnit.Kilogram);
        var now = DateTimeOffset.UtcNow;

        var responses = await Task.WhenAll(
            owner.PostAsync($"/api/supplies/{created.Id}/inventory/initial-balance", new InitialBalanceRequest(0.01m, SupplyBaseUnit.Gram, now, null, null, created.Version)),
            owner.PostAsync($"/api/supplies/{created.Id}/inventory/purchase-receipt", new PurchaseReceiptRequest(0.01m, SupplyBaseUnit.Gram, now, null, 1m, null, null, null, created.Version)),
            owner.PostAsync($"/api/supplies/{created.Id}/inventory/adjustment", new InventoryAdjustmentRequest(InventoryAdjustmentKind.Increase, 0.01m, SupplyBaseUnit.Gram, "Contagem", now, created.Version)),
            owner.PostAsync($"/api/supplies/{created.Id}/inventory/adjustment", new InventoryAdjustmentRequest(InventoryAdjustmentKind.Decrease, 0.01m, SupplyBaseUnit.Gram, "Contagem", now, created.Version)),
            owner.PostAsync($"/api/supplies/{created.Id}/inventory/adjustment", new InventoryAdjustmentRequest(InventoryAdjustmentKind.Correction, 0.01m, SupplyBaseUnit.Gram, "Contagem", now, created.Version)));

        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.BadRequest);
        var detail = await ReadAsync<SupplyResponse>(await owner.GetAsync($"/api/supplies/{created.Id}"));
        detail.CurrentStockBaseUnit.Should().Be(0);
        detail.HasRecordedMovement.Should().BeFalse();
        var movements = await ReadAsync<InventoryMovementListResponse>(await owner.GetAsync($"/api/supplies/{created.Id}/inventory/movements"));
        movements.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Purchase_cost_forms_are_canonical_and_a_mismatch_is_rejected_without_a_movement()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var unitOnly = await CreateSupplyAsync(owner, "FIL-UNIT-COST", "Unitário");
        var totalOnly = await CreateSupplyAsync(owner, "FIL-TOTAL-COST", "Total");
        var both = await CreateSupplyAsync(owner, "FIL-BOTH-COST", "Ambos");
        var mismatch = await CreateSupplyAsync(owner, "FIL-MISMATCH-COST", "Inconsistente");
        var now = DateTimeOffset.UtcNow;

        (await owner.PostAsync($"/api/supplies/{unitOnly.Id}/inventory/purchase-receipt", new PurchaseReceiptRequest(1, SupplyBaseUnit.Kilogram, now, 89.90m, null, null, null, null, unitOnly.Version))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.PostAsync($"/api/supplies/{totalOnly.Id}/inventory/purchase-receipt", new PurchaseReceiptRequest(1, SupplyBaseUnit.Kilogram, now, null, 89.90m, null, null, null, totalOnly.Version))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.PostAsync($"/api/supplies/{both.Id}/inventory/purchase-receipt", new PurchaseReceiptRequest(1, SupplyBaseUnit.Kilogram, now, 89.90m, 89.90m, null, null, null, both.Version))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.PostAsync($"/api/supplies/{mismatch.Id}/inventory/purchase-receipt", new PurchaseReceiptRequest(1, SupplyBaseUnit.Kilogram, now, 89.90m, 1m, null, null, null, mismatch.Version))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        foreach (var supply in new[] { unitOnly, totalOnly, both })
        {
            var movements = await ReadAsync<InventoryMovementListResponse>(await owner.GetAsync($"/api/supplies/{supply.Id}/inventory/movements"));
            movements.Items.Should().ContainSingle(x => x.UnitCostSnapshot == 0.0899m && x.TotalCostSnapshot == 89.90m);
        }
        var rejected = await ReadAsync<SupplyResponse>(await owner.GetAsync($"/api/supplies/{mismatch.Id}"));
        rejected.CurrentStockBaseUnit.Should().Be(0);
        (await ReadAsync<InventoryMovementListResponse>(await owner.GetAsync($"/api/supplies/{mismatch.Id}/inventory/movements"))).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Inactive_supply_allows_inventory_reconciliation_for_inventory_managers()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var created = await CreateSupplyAsync(owner, "FIL-INACTIVE-LEDGER", "Inativo conciliável");
        (await owner.PostAsync($"/api/supplies/{created.Id}/deactivate?version={created.Version}", (object?)null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var inactive = await ReadAsync<SupplyResponse>(await owner.GetAsync($"/api/supplies/{created.Id}"));

        var receipt = await owner.PostAsync($"/api/supplies/{created.Id}/inventory/purchase-receipt",
            new PurchaseReceiptRequest(10, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, null, "Fornecedor", "NF", null, inactive.Version));
        receipt.StatusCode.Should().Be(HttpStatusCode.OK);
        var movements = await ReadAsync<InventoryMovementListResponse>(await owner.GetAsync($"/api/supplies/{created.Id}/inventory/movements"));
        movements.Items.Should().ContainSingle(x => x.Type == InventoryMovementType.PurchaseReceipt);
    }

    [Fact]
    public async Task Manual_decrease_beyond_stock_is_rejected_and_stock_is_unchanged()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var created = await CreateSupplyAsync(owner, "FIL-INSUFFICIENT", "Estoque insuficiente");
        var afterInitial = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{created.Id}/inventory/initial-balance",
            new InitialBalanceRequest(50, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, null, created.Version)));

        var decrease = await owner.PostAsync($"/api/supplies/{created.Id}/inventory/adjustment",
            new InventoryAdjustmentRequest(InventoryAdjustmentKind.Decrease, 100, SupplyBaseUnit.Gram, "Amostra", DateTimeOffset.UtcNow, afterInitial.Version));
        decrease.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var detail = await ReadAsync<SupplyResponse>(await owner.GetAsync($"/api/supplies/{created.Id}"));
        detail.CurrentStockBaseUnit.Should().Be(50, "a rejected decrease must never mutate stock");
    }

    [Fact]
    public async Task Manual_adjustment_without_a_reason_is_rejected()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var created = await CreateSupplyAsync(owner, "FIL-NOREASON", "Sem motivo");
        var response = await owner.PostAsync($"/api/supplies/{created.Id}/inventory/adjustment",
            new InventoryAdjustmentRequest(InventoryAdjustmentKind.Increase, 10, SupplyBaseUnit.Gram, " ", DateTimeOffset.UtcNow, created.Version));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Low_stock_flips_true_at_or_below_minimum()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var created = await CreateSupplyAsync(owner, "FIL-LOWSTOCK", "Estoque baixo", minimumStock: 100);
        var afterInitial = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{created.Id}/inventory/initial-balance",
            new InitialBalanceRequest(100, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, null, created.Version)));
        afterInitial.IsLowStock.Should().BeTrue();

        var summary = await ReadAsync<InventorySummaryResponse>(await owner.GetAsync($"/api/supplies/{created.Id}/inventory"));
        summary.IsLowStock.Should().BeTrue();
    }

    // ---- Audit (mission §30) ----

    [Fact]
    public async Task Supply_creation_and_inventory_mutation_produce_audit_entries()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var created = await CreateSupplyAsync(owner, "FIL-AUDIT", "Auditoria");
        await owner.PostAsync($"/api/supplies/{created.Id}/inventory/initial-balance",
            new InitialBalanceRequest(10, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, null, created.Version));

        await using var db = _fixture.CreateContext();
        var entries = await db.AuditLog.Where(x => x.EntityTable == "supply" || x.EntityTable == "inventory_movement").ToListAsync();
        entries.Should().NotBeEmpty();
        entries.Should().Contain(x => x.EntityTable == "supply" && x.Operation == "ADDED");
        entries.Should().Contain(x => x.EntityTable == "inventory_movement" && x.Operation == "ADDED");
    }

    // ---- Mandatory concurrency proof (mission §46) ----

    [Fact]
    public async Task Two_simultaneous_decreases_never_both_succeed_and_stock_never_goes_negative()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var created = await CreateSupplyAsync(owner, "FIL-RACE", "Corrida de concorrência");
        var afterInitial = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{created.Id}/inventory/initial-balance",
            new InitialBalanceRequest(100, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, null, created.Version)));
        afterInitial.CurrentStockBaseUnit.Should().Be(100);

        var (clientA, _) = await LoggedInAsAsync(Roles.Owner);
        var (clientB, _) = await LoggedInAsAsync(Roles.Owner);
        var requestA = new InventoryAdjustmentRequest(InventoryAdjustmentKind.Decrease, 80, SupplyBaseUnit.Gram, "Corrida A", DateTimeOffset.UtcNow, afterInitial.Version);
        var requestB = new InventoryAdjustmentRequest(InventoryAdjustmentKind.Decrease, 80, SupplyBaseUnit.Gram, "Corrida B", DateTimeOffset.UtcNow, afterInitial.Version);

        var results = await Task.WhenAll(
            clientA.PostAsync($"/api/supplies/{created.Id}/inventory/adjustment", requestA),
            clientB.PostAsync($"/api/supplies/{created.Id}/inventory/adjustment", requestB));

        var statusCodes = results.Select(r => r.StatusCode).ToList();
        statusCodes.Should().ContainSingle(s => s == HttpStatusCode.OK, "exactly one of the two simultaneous decreases must succeed");
        statusCodes.Should().ContainSingle(s => s == HttpStatusCode.Conflict, "the other must fail safely with a concurrency conflict, never silently corrupt stock");

        var final = await ReadAsync<SupplyResponse>(await owner.GetAsync($"/api/supplies/{created.Id}"));
        final.CurrentStockBaseUnit.Should().Be(20, "only ONE 80g decrease may have applied against the 100g starting stock");
        final.CurrentStockBaseUnit.Should().BeGreaterThanOrEqualTo(0, "stock must never go negative");

        var movements = await ReadAsync<InventoryMovementListResponse>(await owner.GetAsync($"/api/supplies/{created.Id}/inventory/movements"));
        movements.Items.Count(x => x.Type == InventoryMovementType.ManualDecrease).Should().Be(1, "only the winning request may have posted a movement");
    }

    [Fact]
    public async Task Two_independent_postgresql_contexts_loaded_at_the_same_version_allow_only_one_decrease_commit()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var created = await CreateSupplyAsync(owner, "FIL-RACE-BARRIER", "Corrida com barreira");
        var afterInitial = await ReadAsync<SupplyResponse>(await owner.PostAsync($"/api/supplies/{created.Id}/inventory/initial-balance",
            new InitialBalanceRequest(100, SupplyBaseUnit.Gram, DateTimeOffset.UtcNow, null, null, created.Version)));
        using var barrier = new Barrier(2);
        var loadedVersions = new System.Collections.Concurrent.ConcurrentBag<long>();

        async Task<bool> AttemptAsync(string reason)
        {
            using var scope = _factory.Services.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            try
            {
                await unitOfWork.ExecuteAsync(async (db, ct) =>
                {
                    var supply = await db.Set<Supply>().SingleAsync(x => x.Id == created.Id, ct);
                    loadedVersions.Add(supply.Version);
                    barrier.SignalAndWait(TimeSpan.FromSeconds(20));
                    var movement = supply.RecordManualDecrease(80, SupplyBaseUnit.Gram, reason, DateTimeOffset.UtcNow);
                    db.Add(movement);
                });
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(Task.Run(() => AttemptAsync("Barreira A")), Task.Run(() => AttemptAsync("Barreira B")));
        loadedVersions.Should().HaveCount(2).And.OnlyContain(version => version == afterInitial.Version);
        results.Should().ContainSingle(result => result).And.ContainSingle(result => !result);

        await using var verification = _fixture.CreateContext();
        var final = await verification.Set<Supply>().SingleAsync(x => x.Id == created.Id);
        final.CurrentStockBaseUnit.Should().Be(20).And.BeGreaterThanOrEqualTo(0);
        (await verification.Set<InventoryMovement>().CountAsync(x => x.SupplyId == created.Id && x.Type == InventoryMovementType.ManualDecrease)).Should().Be(1);
    }

    // ---- Helpers ----

    private SupplyCreateRequest CreateRequest(string code, string categoryCode = "FILAMENT", SupplyBaseUnit unit = SupplyBaseUnit.Gram, decimal? minimumStock = null) =>
        new(code, "Supply " + code, null, categoryCode, unit, minimumStock, null, null, null);

    private async Task<SupplyResponse> CreateSupplyAsync(AuthTestClient client, string code, string name, string categoryCode = "FILAMENT", SupplyBaseUnit unit = SupplyBaseUnit.Gram, decimal? minimumStock = null)
    {
        var response = await client.PostAsync("/api/supplies", new SupplyCreateRequest(code, name, null, categoryCode, unit, minimumStock, null, null, null));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await ReadAsync<SupplyResponse>(response);
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
        var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = role + " S3", IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow };
        (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
        (await users.AddToRoleAsync(user, role)).Succeeded.Should().BeTrue();
        var client = new AuthTestClient(_factory.CreateHttpsClient());
        await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new Verce.Api.Auth.LoginRequest(email, password))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await client.EnsureCsrfCookieAsync();
        return (client, user.Id);
    }
}
