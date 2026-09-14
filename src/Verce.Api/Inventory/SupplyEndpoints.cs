using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Verce.Api.Authorization;
using Verce.Modules.Inventory;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Time;

namespace Verce.Api.Inventory;

public sealed record FilamentDetailsRequest(FilamentMaterialType MaterialType, string Brand, string ColorName, string? ColorCode, decimal DiameterMm, decimal SpoolNetWeightGrams);
public sealed record FilamentDetailsResponse(FilamentMaterialType MaterialType, string Brand, string ColorName, string? ColorCode, decimal DiameterMm, decimal SpoolNetWeightGrams)
{
    public static FilamentDetailsResponse From(Verce.Modules.Inventory.FilamentDetails details) => new(details.MaterialType, details.Brand, details.ColorName, details.ColorCode, details.DiameterMm, details.SpoolNetWeightGrams);
}

public sealed record SupplyCreateRequest(string Code, string Name, string? Description, string CategoryCode, SupplyBaseUnit BaseUnit, decimal? MinimumStock, string? PreferredSupplier, string? Notes, FilamentDetailsRequest? Filament);
public sealed record SupplyUpdateRequest(string Name, string? Description, string CategoryCode, decimal? MinimumStock, string? PreferredSupplier, string? Notes, FilamentDetailsRequest? Filament, long Version);

public sealed record SupplyResponse(Guid Id, string Code, string Name, string? Description, string CategoryCode, SupplyBaseUnit BaseUnit,
    decimal? MinimumStock, string? PreferredSupplier, string? Notes, bool Active, decimal CurrentStockBaseUnit, decimal? LatestPurchaseUnitCost,
    bool IsLowStock, bool HasRecordedMovement, FilamentDetailsResponse? Filament, long Version)
{
    public static SupplyResponse From(Supply supply) => new(supply.Id, supply.Code, supply.Name, supply.Description, supply.CategoryCode, supply.BaseUnit,
        supply.MinimumStock, supply.PreferredSupplier, supply.Notes, supply.Active, supply.CurrentStockBaseUnit, supply.LatestPurchaseUnitCost,
        supply.IsLowStock, supply.HasRecordedMovement, supply.FilamentDetails is null ? null : FilamentDetailsResponse.From(supply.FilamentDetails), supply.Version);
}

public sealed record SupplyListItemResponse(Guid Id, string Code, string Name, string CategoryCode, SupplyBaseUnit BaseUnit,
    decimal CurrentStockBaseUnit, decimal? MinimumStock, bool IsLowStock, decimal? LatestPurchaseUnitCost, bool Active, long Version);
public sealed record SupplyListResponse(IReadOnlyList<SupplyListItemResponse> Items, int Page, int PageSize, int Total);
public sealed record SupplyCategoryResponse(string Code, string Name, bool IsActive);

public sealed record InventorySummaryResponse(Guid SupplyId, SupplyBaseUnit BaseUnit, decimal CurrentStockBaseUnit,
    decimal? MinimumStock, bool IsLowStock, decimal? LatestPurchaseUnitCost, bool HasRecordedMovement, long SupplyVersion);
public sealed record InventoryMovementResponse(Guid Id, InventoryMovementType Type, decimal EnteredQuantity, SupplyBaseUnit EnteredUnit,
    decimal QuantityDeltaBaseUnit, SupplyBaseUnit BaseUnit, DateTimeOffset OccurredAt,
    string? Reason, string? Reference, string? Supplier, decimal? UnitCostSnapshot, decimal? TotalCostSnapshot, DateTimeOffset CreatedAt);
public sealed record InventoryMovementListResponse(IReadOnlyList<InventoryMovementResponse> Items, int Page, int PageSize, int Total);

[JsonConverter(typeof(JsonStringEnumConverter<SupplyListStatus>))]
public enum SupplyListStatus
{
    active,
    inactive,
    all,
}

/// <summary><see cref="Quantity"/> is understood in <see cref="EnteredUnit"/> (S3 mission §9/§19)
/// — it need not equal the Supply's own base unit as long as the two are dimensionally
/// compatible (e.g. entering "1" Kilogram against a Gram-denominated Supply); the server
/// preserves the original entered fact alongside the normalized base-unit quantity.</summary>
public sealed record InitialBalanceRequest(decimal Quantity, SupplyBaseUnit EnteredUnit, DateTimeOffset OccurredAt, string? Reference, string? Notes, long SupplyVersion);
/// <summary><see cref="UnitCost"/>, if given, is cost PER <see cref="EnteredUnit"/> (e.g. R$/kg
/// for a spool priced by the kilogram) — see <see cref="Quantity"/> remarks on <see cref="InitialBalanceRequest"/>.</summary>
public sealed record PurchaseReceiptRequest(decimal Quantity, SupplyBaseUnit EnteredUnit, DateTimeOffset OccurredAt, decimal? UnitCost, decimal? TotalCost, string? Supplier, string? Reference, string? Notes, long SupplyVersion);

/// <summary>M-S2-005-style named string enum: an increase/decrease/correction request are the
/// same shape (S3 mission §23/§13), distinguished by <see cref="Kind"/> alone.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InventoryAdjustmentKind>))]
public enum InventoryAdjustmentKind { Increase, Decrease, Correction }
/// <summary>For <see cref="InventoryAdjustmentKind.Correction"/>, <see cref="Quantity"/> is the
/// freshly COUNTED absolute quantity, not a delta — the server derives the signed movement.
/// For Increase/Decrease it is the magnitude to move. <see cref="EnteredUnit"/>: see the remarks
/// on <see cref="InitialBalanceRequest"/>.</summary>
public sealed record InventoryAdjustmentRequest(InventoryAdjustmentKind Kind, decimal Quantity, SupplyBaseUnit EnteredUnit, string Reason, DateTimeOffset OccurredAt, long SupplyVersion);

public static class SupplyEndpoints
{
    public static IEndpointRouteBuilder MapSupplyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/supplies");

        group.MapGet("/categories", async (VerceDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Set<SupplyCategory>().AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name)
                .Select(x => new SupplyCategoryResponse(x.Code, x.Name, x.IsActive)).ToListAsync(ct)))
            .RequireAuthorization(Permissions.SuppliesRead).Produces<IReadOnlyList<SupplyCategoryResponse>>();

        group.MapGet("", async (string? search, string? categoryCode, bool? lowStock, VerceDbContext db, CancellationToken ct, SupplyListStatus status = SupplyListStatus.active, int page = 0, int pageSize = 0) =>
        {
            page = Math.Max(page, 1);
            pageSize = Math.Clamp(pageSize == 0 ? 25 : pageSize, 1, 100);
            var query = db.Set<Supply>().AsNoTracking().AsQueryable();
            query = status switch
            {
                SupplyListStatus.active => query.Where(x => x.Active),
                SupplyListStatus.inactive => query.Where(x => !x.Active),
                SupplyListStatus.all => query,
                _ => throw new ArgumentException("SUPPLY_STATUS_INVALID"),
            };
            if (!string.IsNullOrWhiteSpace(categoryCode)) query = query.Where(x => x.CategoryCode == categoryCode.Trim().ToUpperInvariant());
            if (lowStock == true) query = query.Where(x => x.MinimumStock != null && x.CurrentStockBaseUnit <= x.MinimumStock);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = $"%{search.Trim()}%";
                query = query.Where(x => EF.Functions.ILike(x.Name, term) || EF.Functions.ILike(x.Code, term));
            }
            var total = await query.CountAsync(ct);
            var rows = await query
                .OrderBy(x => x.Name).ThenBy(x => EF.Property<DateTimeOffset>(x, "CreatedAt")).ThenBy(x => EF.Property<long>(x, SupplyConfiguration.CreationSequence))
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new SupplyListItemResponse(x.Id, x.Code, x.Name, x.CategoryCode, x.BaseUnit, x.CurrentStockBaseUnit, x.MinimumStock, x.MinimumStock != null && x.CurrentStockBaseUnit <= x.MinimumStock, x.LatestPurchaseUnitCost, x.Active, x.Version))
                .ToListAsync(ct);
            return Results.Ok(new SupplyListResponse(rows, page, pageSize, total));
        }).RequireAuthorization(Permissions.SuppliesRead).Produces<SupplyListResponse>();

        group.MapGet("/{id:guid}", async (Guid id, VerceDbContext db, CancellationToken ct) =>
        {
            var supply = await db.Set<Supply>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            return supply is null ? Results.NotFound() : Results.Ok(SupplyResponse.From(supply));
        }).RequireAuthorization(Permissions.SuppliesRead).Produces<SupplyResponse>().Produces(StatusCodes.Status404NotFound);

        group.MapPost("", async (SupplyCreateRequest request, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
            try
            {
                var actor = await users.GetUserAsync(http.User);
                if (actor is null) return Results.Unauthorized();
                ambient.SetActor(actor.Id, actor.DisplayName);
                Supply? created = null;
                await uow.ExecuteAsync(async (db, token) =>
                {
                    if (!await db.Set<SupplyCategory>().AnyAsync(x => x.Code == request.CategoryCode.Trim().ToUpperInvariant(), token)) throw new KeyNotFoundException();
                    created = new Supply(request.Code, request.Name, request.Description, request.CategoryCode, request.BaseUnit, request.MinimumStock, request.PreferredSupplier, request.Notes, ToDomain(request.Filament));
                    db.Add(created);
                }, ct);
                return Results.Created($"/api/supplies/{created!.Id}", SupplyResponse.From(created!));
            }
            catch (KeyNotFoundException) { return Problem("SUPPLY_CATEGORY_NOT_FOUND", StatusCodes.Status404NotFound); }
            catch (ArgumentException ex) { return Problem(ex.Message); }
            catch (DbUpdateException ex) when (IsUnique(ex)) { return Problem("SUPPLY_CODE_DUPLICATE", StatusCodes.Status409Conflict); }
        }).RequireAuthorization(Permissions.SuppliesManage).Produces<SupplyResponse>(StatusCodes.Status201Created);

        group.MapPut("/{id:guid}", async (Guid id, SupplyUpdateRequest request, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
            try
            {
                var actor = await users.GetUserAsync(http.User);
                if (actor is null) return Results.Unauthorized();
                ambient.SetActor(actor.Id, actor.DisplayName);
                Supply? supply = null;
                await uow.ExecuteAsync(async (db, token) =>
                {
                    supply = await db.Set<Supply>().SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException();
                    if (supply.Version != request.Version) throw new DbUpdateConcurrencyException();
                    if (!await db.Set<SupplyCategory>().AnyAsync(x => x.Code == request.CategoryCode.Trim().ToUpperInvariant(), token)) throw new SupplyCategoryNotFoundException();
                    supply.Update(request.Name, request.Description, request.CategoryCode, request.MinimumStock, request.PreferredSupplier, request.Notes, ToDomain(request.Filament));
                }, ct);
                return Results.Ok(SupplyResponse.From(supply!));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (SupplyCategoryNotFoundException) { return Problem("SUPPLY_CATEGORY_NOT_FOUND", StatusCodes.Status404NotFound); }
            catch (DbUpdateConcurrencyException) { return Problem("CONCURRENCY_CONFLICT", StatusCodes.Status409Conflict); }
            catch (ArgumentException ex) { return Problem(ex.Message); }
        }).RequireAuthorization(Permissions.SuppliesManage).Produces<SupplyResponse>();

        group.MapPost("/{id:guid}/deactivate", (Guid id, long version, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, CancellationToken ct) =>
            SetActive(id, version, active: false, http, antiforgery, users, ambient, uow, ct)).RequireAuthorization(Permissions.SuppliesManage).Produces(StatusCodes.Status204NoContent);
        group.MapPost("/{id:guid}/activate", (Guid id, long version, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, CancellationToken ct) =>
            SetActive(id, version, active: true, http, antiforgery, users, ambient, uow, ct)).RequireAuthorization(Permissions.SuppliesManage).Produces(StatusCodes.Status204NoContent);

        group.MapGet("/{id:guid}/inventory", async (Guid id, VerceDbContext db, CancellationToken ct) =>
        {
            var supply = await db.Set<Supply>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            return supply is null ? Results.NotFound() : Results.Ok(new InventorySummaryResponse(supply.Id, supply.BaseUnit, supply.CurrentStockBaseUnit, supply.MinimumStock, supply.IsLowStock, supply.LatestPurchaseUnitCost, supply.HasRecordedMovement, supply.Version));
        }).RequireAuthorization(Permissions.InventoryRead).Produces<InventorySummaryResponse>().Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/inventory/movements", async (Guid id, VerceDbContext db, CancellationToken ct, int page = 0, int pageSize = 0) =>
        {
            var baseUnit = await db.Set<Supply>().AsNoTracking().Where(x => x.Id == id).Select(x => (SupplyBaseUnit?)x.BaseUnit).SingleOrDefaultAsync(ct);
            if (baseUnit is null) return Results.NotFound();
            page = Math.Max(page, 1);
            pageSize = Math.Clamp(pageSize == 0 ? 25 : pageSize, 1, 100);
            var query = db.Set<InventoryMovement>().AsNoTracking().Where(x => x.SupplyId == id);
            var total = await query.CountAsync(ct);
            var rows = await query
                .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => EF.Property<DateTimeOffset>(x, "CreatedAt"))
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new InventoryMovementResponse(x.Id, x.Type, x.EnteredQuantity, x.EnteredUnit, x.QuantityDeltaBaseUnit, baseUnit.Value, x.OccurredAt, x.Reason, x.Reference, x.Supplier, x.UnitCostSnapshot, x.TotalCostSnapshot, EF.Property<DateTimeOffset>(x, "CreatedAt")))
                .ToListAsync(ct);
            return Results.Ok(new InventoryMovementListResponse(rows, page, pageSize, total));
        }).RequireAuthorization(Permissions.InventoryRead).Produces<InventoryMovementListResponse>().Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/inventory/initial-balance", async (Guid id, InitialBalanceRequest request, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
            try
            {
                var actor = await users.GetUserAsync(http.User);
                if (actor is null) return Results.Unauthorized();
                ambient.SetActor(actor.Id, actor.DisplayName);
                Supply? supply = null;
                await uow.ExecuteAsync(async (db, token) =>
                {
                    supply = await db.Set<Supply>().SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException();
                    if (supply.Version != request.SupplyVersion) throw new DbUpdateConcurrencyException();
                    var movement = supply.RecordInitialBalance(request.Quantity, request.EnteredUnit, request.OccurredAt, request.Reference, request.Notes);
                    db.Add(movement);
                }, ct);
                return Results.Ok(SupplyResponse.From(supply!));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException) { return Problem("CONCURRENCY_CONFLICT", StatusCodes.Status409Conflict); }
            catch (InvalidOperationException ex) { return Problem(ex.Message, StatusCodes.Status409Conflict); }
            catch (ArgumentException ex) { return Problem(ex.Message); }
        }).RequireAuthorization(Permissions.InventoryManage).Produces<SupplyResponse>();

        group.MapPost("/{id:guid}/inventory/purchase-receipt", async (Guid id, PurchaseReceiptRequest request, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
            try
            {
                var actor = await users.GetUserAsync(http.User);
                if (actor is null) return Results.Unauthorized();
                ambient.SetActor(actor.Id, actor.DisplayName);
                Supply? supply = null;
                await uow.ExecuteAsync(async (db, token) =>
                {
                    supply = await db.Set<Supply>().SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException();
                    if (supply.Version != request.SupplyVersion) throw new DbUpdateConcurrencyException();
                    var movement = supply.RecordPurchaseReceipt(request.Quantity, request.EnteredUnit, request.OccurredAt, request.UnitCost, request.TotalCost, request.Supplier, request.Reference, request.Notes);
                    db.Add(movement);
                }, ct);
                return Results.Ok(SupplyResponse.From(supply!));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException) { return Problem("CONCURRENCY_CONFLICT", StatusCodes.Status409Conflict); }
            catch (ArgumentException ex) { return Problem(ex.Message); }
        }).RequireAuthorization(Permissions.InventoryManage).Produces<SupplyResponse>();

        group.MapPost("/{id:guid}/inventory/adjustment", async (Guid id, InventoryAdjustmentRequest request, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
            try
            {
                var actor = await users.GetUserAsync(http.User);
                if (actor is null) return Results.Unauthorized();
                ambient.SetActor(actor.Id, actor.DisplayName);
                Supply? supply = null;
                await uow.ExecuteAsync(async (db, token) =>
                {
                    supply = await db.Set<Supply>().SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException();
                    if (supply.Version != request.SupplyVersion) throw new DbUpdateConcurrencyException();
                    var movement = request.Kind switch
                    {
                        InventoryAdjustmentKind.Increase => supply.RecordManualIncrease(request.Quantity, request.EnteredUnit, request.Reason, request.OccurredAt),
                        InventoryAdjustmentKind.Decrease => supply.RecordManualDecrease(request.Quantity, request.EnteredUnit, request.Reason, request.OccurredAt),
                        InventoryAdjustmentKind.Correction => supply.RecordCorrection(request.Quantity, request.EnteredUnit, request.Reason, request.OccurredAt),
                        _ => throw new ArgumentException("ADJUSTMENT_KIND_INVALID"),
                    };
                    db.Add(movement);
                }, ct);
                return Results.Ok(SupplyResponse.From(supply!));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException) { return Problem("CONCURRENCY_CONFLICT", StatusCodes.Status409Conflict); }
            catch (InsufficientStockException) { return Problem("INSUFFICIENT_STOCK", StatusCodes.Status409Conflict); }
            catch (ArgumentException ex) { return Problem(ex.Message); }
        }).RequireAuthorization(Permissions.InventoryManage).Produces<SupplyResponse>();

        return app;
    }

    private static async Task<IResult> SetActive(Guid id, long version, bool active, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, CancellationToken ct)
    {
        if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
        try
        {
            var actor = await users.GetUserAsync(http.User);
            if (actor is null) return Results.Unauthorized();
            ambient.SetActor(actor.Id, actor.DisplayName);
            await uow.ExecuteAsync(async (db, token) =>
            {
                var supply = await db.Set<Supply>().SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException();
                if (supply.Version != version) throw new DbUpdateConcurrencyException();
                if (active) supply.Activate(); else supply.Deactivate();
            }, ct);
            return Results.NoContent();
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (DbUpdateConcurrencyException) { return Problem("CONCURRENCY_CONFLICT", StatusCodes.Status409Conflict); }
    }

    private static Verce.Modules.Inventory.FilamentDetails? ToDomain(FilamentDetailsRequest? request) =>
        request is null ? null : new Verce.Modules.Inventory.FilamentDetails(request.MaterialType, request.Brand, request.ColorName, request.ColorCode, request.DiameterMm, request.SpoolNetWeightGrams);

    private static async Task<bool> IsValidCsrf(HttpContext http, IAntiforgery antiforgery) { try { await antiforgery.ValidateRequestAsync(http); return true; } catch (AntiforgeryValidationException) { return false; } }
    private static IResult BadRequest() => Results.Problem(statusCode: 400, title: "Requisição inválida.");
    private static IResult Problem(string code, int status = 400) => Results.Problem(statusCode: status, title: "Não foi possível concluir a operação.", extensions: new Dictionary<string, object?> { ["code"] = code });
    private static bool IsUnique(DbUpdateException ex) => ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true;
}

file sealed class SupplyCategoryNotFoundException : Exception;
