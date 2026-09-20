using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Verce.Api.Authorization;
using Verce.Modules.Catalog;
using Verce.Modules.Costing;
using Verce.Modules.Inventory;
using Verce.Modules.Settings;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel;

namespace Verce.Api.Catalog;

public sealed record ProductCreateRequest(string Code, string Name, string? Description);
public sealed record ProductUpdateRequest(string Name, string? Description, long Version);

public sealed record ProductRecipeMaterialLineRequest(Guid SupplyId, decimal Quantity, SupplyBaseUnit EnteredUnit,
    decimal? WastagePercentOverride, decimal? ManualUnitCostOverride);
public sealed record ProductRecipeAdditionalCostLineRequest(string Description, decimal Amount);
public sealed record ProductRecipeUpdateRequest(
    decimal? WastagePercentOverride, decimal? LaborMinutes, decimal? LaborHourlyRateOverride,
    decimal? MachineMinutes, decimal? MachineHourlyRate, int OutputQuantity, string? Notes,
    IReadOnlyList<ProductRecipeMaterialLineRequest> MaterialLines,
    IReadOnlyList<ProductRecipeAdditionalCostLineRequest> AdditionalCostLines,
    long ProductVersion);

public sealed record ProductRecipeMaterialLineResponse(Guid Id, Guid SupplyId, string SupplyCode, string SupplyName, bool SupplyActive,
    decimal EnteredQuantity, SupplyBaseUnit EnteredUnit, decimal NormalizedQuantityBaseUnit, SupplyBaseUnit BaseUnit,
    decimal? WastagePercentOverride, decimal? ManualUnitCostOverride, int SortOrder);
public sealed record ProductRecipeAdditionalCostLineResponse(Guid Id, string Description, decimal Amount, int SortOrder);
public sealed record ProductRecipeResponse(
    Guid Id, Guid ProductId, int RevisionNumber, decimal? WastagePercentOverride, decimal? LaborMinutes, decimal? LaborHourlyRateOverride,
    decimal? MachineMinutes, decimal? MachineHourlyRate, int OutputQuantity, string? Notes,
    IReadOnlyList<ProductRecipeMaterialLineResponse> MaterialLines, IReadOnlyList<ProductRecipeAdditionalCostLineResponse> AdditionalCostLines);

public sealed record ProductResponse(Guid Id, string Code, string Name, string? Description, bool Active, long Version, ProductRecipeResponse Recipe);
public sealed record ProductListItemResponse(Guid Id, string Code, string Name, bool Active, long Version);
public sealed record ProductListResponse(IReadOnlyList<ProductListItemResponse> Items, int Page, int PageSize, int Total);

[JsonConverter(typeof(JsonStringEnumConverter<ProductListStatus>))]
public enum ProductListStatus { active, inactive, all }

public static class ProductEndpoints
{
    /// <summary>Domain codes that are legitimate, stable, machine-readable API errors — never
    /// an arbitrary exception message (mission §51/§80, mirroring S4's L8 correction in
    /// CostingEndpoints). Everything else collapses to a generic, still-stable code.</summary>
    private static readonly HashSet<string> ControlledRequestCodes = new(StringComparer.Ordinal)
    {
        "PRODUCT_CODE_INVALID_CHARACTERS", "FIELD_TOO_LONG", "PRODUCT_INACTIVE",
        "RECIPE_WASTAGE_PERCENT_INVALID", "RECIPE_LABOR_MINUTES_INVALID", "RECIPE_LABOR_RATE_INVALID",
        "RECIPE_MACHINE_MINUTES_INVALID", "RECIPE_MACHINE_RATE_INVALID", "RECIPE_OUTPUT_QUANTITY_INVALID",
        "RECIPE_LINE_SUPPLY_REQUIRED", "RECIPE_LINE_QUANTITY_MUST_BE_POSITIVE", "RECIPE_LINE_QUANTITY_BELOW_BASE_PRECISION",
        "RECIPE_LINE_UNIT_REQUIRED", "RECIPE_LINE_MANUAL_COST_INVALID", "RECIPE_ADDITIONAL_COST_AMOUNT_INVALID",
        "UNIT_CONVERSION_NOT_SUPPORTED", "QUANTITY_BELOW_BASE_PRECISION", "QUANTITY_BELOW_ENTERED_PRECISION",
        "QUANTITY_MUST_BE_POSITIVE",
    };

    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/products");

        group.MapGet("", async (string? search, VerceDbContext db, CancellationToken ct, ProductListStatus status = ProductListStatus.active, int page = 0, int pageSize = 0) =>
        {
            page = Math.Max(page, 1);
            pageSize = Math.Clamp(pageSize == 0 ? 25 : pageSize, 1, 100);
            var query = db.Set<Product>().AsNoTracking().AsQueryable();
            query = status switch
            {
                ProductListStatus.active => query.Where(x => x.Active),
                ProductListStatus.inactive => query.Where(x => !x.Active),
                ProductListStatus.all => query,
                _ => throw new ArgumentException("PRODUCT_STATUS_INVALID"),
            };
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = $"%{search.Trim()}%";
                query = query.Where(x => EF.Functions.ILike(x.Name, term) || EF.Functions.ILike(x.Code, term));
            }
            var total = await query.CountAsync(ct);
            var rows = await query
                .OrderBy(x => x.Name).ThenBy(x => EF.Property<DateTimeOffset>(x, "CreatedAt")).ThenBy(x => EF.Property<long>(x, ProductConfiguration.CreationSequence))
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new ProductListItemResponse(x.Id, x.Code, x.Name, x.Active, x.Version))
                .ToListAsync(ct);
            return Results.Ok(new ProductListResponse(rows, page, pageSize, total));
        }).RequireAuthorization(Permissions.CatalogRead).Produces<ProductListResponse>();

        group.MapGet("/{id:guid}", async (Guid id, VerceDbContext db, ICostingInventoryReader inventory, CancellationToken ct) =>
        {
            var product = await LoadProductAsync(db, id, ct);
            return product is null ? Results.NotFound() : Results.Ok(await ToResponseAsync(product, inventory, ct));
        }).RequireAuthorization(Permissions.CatalogRead).Produces<ProductResponse>().Produces(StatusCodes.Status404NotFound);

        group.MapPost("", async (ProductCreateRequest request, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users,
            AmbientOperationContext ambient, IUnitOfWork uow, ICostingInventoryReader inventory, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
            try
            {
                var actor = await users.GetUserAsync(http.User);
                if (actor is null) return Results.Unauthorized();
                ambient.SetActor(actor.Id, actor.DisplayName);
                Product? created = null;
                await uow.ExecuteAsync(async (db, token) =>
                {
                    created = new Product(request.Code, request.Name, request.Description);
                    db.Add(created);
                    await Task.CompletedTask;
                }, ct);
                return Results.Created($"/api/products/{created!.Id}", await ToResponseAsync(created!, inventory, ct));
            }
            catch (ArgumentException ex) { return Problem(ex.Message); }
            catch (DbUpdateException ex) when (IsUnique(ex)) { return Problem("PRODUCT_CODE_ALREADY_EXISTS", StatusCodes.Status409Conflict); }
        }).RequireAuthorization(Permissions.CatalogManage).Produces<ProductResponse>(StatusCodes.Status201Created);

        group.MapPut("/{id:guid}", async (Guid id, ProductUpdateRequest request, HttpContext http, IAntiforgery antiforgery,
            UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, VerceDbContext readDb, ICostingInventoryReader inventory, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
            try
            {
                var actor = await users.GetUserAsync(http.User);
                if (actor is null) return Results.Unauthorized();
                ambient.SetActor(actor.Id, actor.DisplayName);
                await uow.ExecuteAsync(async (db, token) =>
                {
                    var product = await db.Set<Product>().SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException();
                    if (product.Version != request.Version) throw new DbUpdateConcurrencyException();
                    product.UpdateDetails(request.Name, request.Description);
                }, ct);
                var updated = await LoadProductAsync(readDb, id, ct);
                return Results.Ok(await ToResponseAsync(updated!, inventory, ct));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException) { return Problem("CONCURRENCY_CONFLICT", StatusCodes.Status409Conflict); }
            catch (ArgumentException ex) { return Problem(ex.Message); }
        }).RequireAuthorization(Permissions.CatalogManage).Produces<ProductResponse>().Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/activate", (Guid id, long version, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users,
            AmbientOperationContext ambient, IUnitOfWork uow, CancellationToken ct) => SetActiveAsync(id, version, true, http, antiforgery, users, ambient, uow, ct))
            .RequireAuthorization(Permissions.CatalogManage).Produces(StatusCodes.Status204NoContent);

        group.MapPost("/{id:guid}/deactivate", (Guid id, long version, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users,
            AmbientOperationContext ambient, IUnitOfWork uow, CancellationToken ct) => SetActiveAsync(id, version, false, http, antiforgery, users, ambient, uow, ct))
            .RequireAuthorization(Permissions.CatalogManage).Produces(StatusCodes.Status204NoContent);

        group.MapPut("/{id:guid}/recipe", async (Guid id, ProductRecipeUpdateRequest request, HttpContext http, IAntiforgery antiforgery,
            UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, VerceDbContext readDb, ICostingInventoryReader inventory, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
            try
            {
                var actor = await users.GetUserAsync(http.User);
                if (actor is null) return Results.Unauthorized();
                ambient.SetActor(actor.Id, actor.DisplayName);

                // Terra B-04 / B-04-R: an inactive Supply may remain referenced by an EXISTING
                // recipe line (preserved history — reading/costing a recipe never cares whether a
                // referenced Supply later went inactive, matching S4's own inactive-Supply
                // consistency stance), but a recipe write may never INCREASE how many lines
                // reference an inactive Supply. Duplicate lines against the same Supply are
                // deliberately legal (ADR-0019 §1), so "already referenced" cannot be a
                // Contains(id) set check — that lets a submission with MORE inactive lines than
                // existed slip through, since every one of those lines individually "existed
                // before." The correct rule is per-Supply CARDINALITY: for an inactive Supply,
                // submittedCount must never exceed existingCount.
                var existingProduct = await LoadProductAsync(readDb, id, ct);
                if (existingProduct is null) return Results.NotFound();
                var existingSupplyCounts = existingProduct.Recipe.MaterialLines
                    .GroupBy(x => x.SupplyId).ToDictionary(g => g.Key, g => g.Count());

                // Cross-module unit normalization happens HERE, in the composition root
                // (ADR-0001 §3.1) — Catalog's pure aggregate never references Inventory.
                var supplyIds = request.MaterialLines.Select(x => x.SupplyId).Distinct().ToArray();
                var supplies = await readDb.Set<Supply>().AsNoTracking().Where(x => supplyIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
                if (supplies.Count != supplyIds.Length) return Problem("SUPPLY_NOT_FOUND", StatusCodes.Status404NotFound);

                var submittedSupplyCounts = request.MaterialLines.GroupBy(x => x.SupplyId).ToDictionary(g => g.Key, g => g.Count());
                foreach (var (supplyId, submittedCount) in submittedSupplyCounts)
                {
                    if (supplies[supplyId].Active) continue; // normal path — no cardinality restriction on an active Supply
                    var existingCount = existingSupplyCounts.GetValueOrDefault(supplyId);
                    if (submittedCount > existingCount) return Problem("SUPPLY_INACTIVE", StatusCodes.Status422UnprocessableEntity);
                }

                var normalizedLines = request.MaterialLines.Select(line =>
                {
                    var supply = supplies[line.SupplyId];
                    var normalized = SupplyUnitConversion.NormalizePositive(line.Quantity, line.EnteredUnit, supply.BaseUnit);
                    return new ProductRecipeMaterialLine(default, line.SupplyId, normalized.EnteredQuantity, line.EnteredUnit.ToString(),
                        normalized.QuantityBaseUnit, line.WastagePercentOverride, line.ManualUnitCostOverride);
                }).ToList();
                var additionalLines = request.AdditionalCostLines
                    .Select(line => new ProductRecipeAdditionalCostLine(default, line.Description, line.Amount)).ToList();

                await uow.ExecuteAsync(async (db, token) =>
                {
                    var product = await db.Set<Product>().Include(x => x.Recipe).ThenInclude(x => x.MaterialLines)
                        .Include(x => x.Recipe).ThenInclude(x => x.AdditionalCostLines)
                        .SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException();
                    if (product.Version != request.ProductVersion) throw new DbUpdateConcurrencyException();
                    product.Recipe.UpdateParameters(request.WastagePercentOverride, request.LaborMinutes, request.LaborHourlyRateOverride,
                        request.MachineMinutes, request.MachineHourlyRate, request.OutputQuantity, request.Notes);
                    product.Recipe.ReplaceMaterialLines(normalizedLines);
                    product.Recipe.ReplaceAdditionalCostLines(additionalLines);
                }, ct);

                var updated = await LoadProductAsync(readDb, id, ct);
                return Results.Ok(await ToResponseAsync(updated!, inventory, ct));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException) { return Problem("CONCURRENCY_CONFLICT", StatusCodes.Status409Conflict); }
            catch (ArgumentException ex) { return Problem(ControlledRequestCodes.Contains(ex.Message) ? ex.Message : "PRODUCT_RECIPE_REQUEST_INVALID"); }
        }).RequireAuthorization(Permissions.CatalogManage).Produces<ProductResponse>().Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status409Conflict);

        group.MapGet("/{id:guid}/cost", async (Guid id, VerceDbContext db, ICostingInventoryReader inventory, AppSettingValueReader settings, CancellationToken ct) =>
        {
            var product = await LoadProductAsync(db, id, ct);
            if (product is null) return Results.NotFound();
            try
            {
                var result = await ProductCostCalculator.CalculateAsync(product, inventory, settings, ct);
                return Results.Ok(result);
            }
            catch (ArgumentException ex) when (ex.Message == "COST_BASIS_UNAVAILABLE") { return Problem("COST_BASIS_UNAVAILABLE", StatusCodes.Status422UnprocessableEntity); }
            catch (ArgumentException ex) when (ex.Message is "RECIPE_EMPTY" or "RECIPE_MACHINE_RATE_REQUIRED") { return Problem("RECIPE_INVALID", StatusCodes.Status422UnprocessableEntity); }
            catch (ArgumentException ex) when (ex.Message == "SUPPLY_NOT_FOUND") { return Problem("SUPPLY_NOT_FOUND", StatusCodes.Status404NotFound); }
            catch (ArgumentException ex) when (ex.Message.StartsWith("SETTING_", StringComparison.Ordinal)) { return Problem("COSTING_CONFIGURATION_INVALID", StatusCodes.Status500InternalServerError); }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("SETTING_", StringComparison.Ordinal)) { return Problem("COSTING_CONFIGURATION_INVALID", StatusCodes.Status500InternalServerError); }
            catch (ArgumentException ex) { return Problem(ControlledRequestCodes.Contains(ex.Message) ? ex.Message : "COSTING_REQUEST_INVALID"); }
        }).RequireAuthorization(Permissions.CatalogRead).Produces<CostCalculationResult>()
            .Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status422UnprocessableEntity);

        return app;
    }

    private static async Task<IResult> SetActiveAsync(Guid id, long version, bool active, HttpContext http, IAntiforgery antiforgery,
        UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, CancellationToken ct)
    {
        if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
        try
        {
            var actor = await users.GetUserAsync(http.User);
            if (actor is null) return Results.Unauthorized();
            ambient.SetActor(actor.Id, actor.DisplayName);
            await uow.ExecuteAsync(async (db, token) =>
            {
                var product = await db.Set<Product>().SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException();
                if (product.Version != version) throw new DbUpdateConcurrencyException();
                if (active) product.Activate(); else product.Deactivate();
                await Task.CompletedTask;
            }, ct);
            return Results.NoContent();
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (DbUpdateConcurrencyException) { return Problem("CONCURRENCY_CONFLICT", StatusCodes.Status409Conflict); }
    }

    internal static Task<Product?> LoadProductAsync(VerceDbContext db, Guid id, CancellationToken ct) =>
        db.Set<Product>().AsNoTracking().Include(x => x.Recipe).ThenInclude(x => x.MaterialLines)
            .Include(x => x.Recipe).ThenInclude(x => x.AdditionalCostLines)
            .SingleOrDefaultAsync(x => x.Id == id, ct)!;

    internal static async Task<ProductResponse> ToResponseAsync(Product product, ICostingInventoryReader inventory, CancellationToken ct)
    {
        var supplyIds = product.Recipe.MaterialLines.Select(x => x.SupplyId).Distinct().ToArray();
        var supplies = await inventory.GetAsync(supplyIds, ct);
        var materialLines = product.Recipe.MaterialLines.Select(line =>
        {
            supplies.TryGetValue(line.SupplyId, out var supply);
            var baseUnit = supply is null ? SupplyBaseUnit.Gram : ParseUnit(supply.BaseUnit);
            return new ProductRecipeMaterialLineResponse(line.Id, line.SupplyId, supply?.Code ?? "?", supply?.Name ?? "(insumo removido)",
                supply?.Active ?? false, line.EnteredQuantity, ParseUnit(line.EnteredUnit), line.NormalizedQuantityBaseUnit, baseUnit,
                line.WastagePercentOverride, line.ManualUnitCostOverride, line.SortOrder);
        }).ToList();
        var additionalLines = product.Recipe.AdditionalCostLines
            .Select(x => new ProductRecipeAdditionalCostLineResponse(x.Id, x.Description, x.Amount, x.SortOrder)).ToList();
        var recipe = new ProductRecipeResponse(product.Recipe.Id, product.Id, product.Recipe.RevisionNumber, product.Recipe.WastagePercentOverride,
            product.Recipe.LaborMinutes, product.Recipe.LaborHourlyRateOverride, product.Recipe.MachineMinutes, product.Recipe.MachineHourlyRate,
            product.Recipe.OutputQuantity, product.Recipe.Notes, materialLines, additionalLines);
        return new ProductResponse(product.Id, product.Code, product.Name, product.Description, product.Active, product.Version, recipe);
    }

    private static SupplyBaseUnit ParseUnit(string value) =>
        Enum.TryParse<SupplyBaseUnit>(value, out var unit) ? unit : throw new InvalidOperationException("SUPPLY_BASE_UNIT_INVALID");

    private static bool IsUnique(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };

    private static async Task<bool> IsValidCsrf(HttpContext http, IAntiforgery antiforgery)
    {
        try { await antiforgery.ValidateRequestAsync(http); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    private static IResult BadRequest() => Results.Problem(statusCode: 400, title: "Requisição inválida.");
    private static IResult Problem(string code, int status = StatusCodes.Status400BadRequest) =>
        Results.Problem(statusCode: status, title: "Não foi possível concluir a operação.", extensions: new Dictionary<string, object?> { ["code"] = code });
}
