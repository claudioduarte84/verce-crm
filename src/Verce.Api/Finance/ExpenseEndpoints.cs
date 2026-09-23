using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Verce.Api.Authorization;
using Verce.Modules.Finance;
using Verce.Modules.Inventory;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;

namespace Verce.Api.Finance;

public sealed record ExpenseWriteRequest(Guid ExpenseCategoryId, string Description, decimal Amount, DateOnly IncurredOn,
    AccountingTreatment AccountingTreatment, DateOnly? PaidOn, string? PaymentMethod, string? SupplierName,
    string? DocumentNumber, Guid? InventoryMovementId, Guid? SalesChannelId, Guid? MachineId, string? AttachmentPath, string? Notes, long? Version);
public sealed record ExpenseCategoryResponse(Guid Id, string Name, AccountingTreatment DefaultTreatment, bool IsActive, long Version);
public sealed record ExpenseResponse(Guid Id, Guid ExpenseCategoryId, string Description, decimal Amount, DateOnly IncurredOn,
    DateOnly? PaidOn, AccountingTreatment AccountingTreatment, Guid? InventoryMovementId, Guid? SalesChannelId, string? SupplierName, string? Notes, long Version);
public sealed record ExpenseListResponse(IReadOnlyList<ExpenseResponse> Items, int Page, int PageSize, int Total);

public static class ExpenseEndpoints
{
    public static IEndpointRouteBuilder MapExpenseEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/expenses");
        group.MapGet("/categories", async (VerceDbContext db, CancellationToken ct) => Results.Ok(await db.Set<ExpenseCategory>().AsNoTracking()
            .OrderBy(x => x.Name).Select(x => new ExpenseCategoryResponse(x.Id, x.Name, x.DefaultTreatment, x.IsActive, x.Version)).ToListAsync(ct)))
            .RequireAuthorization(Permissions.ExpensesRead);
        group.MapGet("", async (VerceDbContext db, AccountingTreatment? treatment, Guid? category, Guid? channel, DateOnly? from, DateOnly? to,
            int page = 1, int pageSize = 25, CancellationToken ct = default) =>
        {
            page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
            var query = db.Set<Expense>().AsNoTracking().AsQueryable();
            if (treatment is not null) query = query.Where(x => x.AccountingTreatment == treatment);
            if (category is not null) query = query.Where(x => x.ExpenseCategoryId == category);
            if (channel is not null) query = query.Where(x => x.SalesChannelId == channel);
            if (from is not null) query = query.Where(x => x.IncurredOn >= from);
            if (to is not null) query = query.Where(x => x.IncurredOn <= to);
            var total = await query.CountAsync(ct);
            var rows = await query.OrderByDescending(x => x.IncurredOn).ThenByDescending(x => EF.Property<DateTimeOffset>(x, "CreatedAt")).Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => ToResponse(x)).ToListAsync(ct);
            return Results.Ok(new ExpenseListResponse(rows, page, pageSize, total));
        }).RequireAuthorization(Permissions.ExpensesRead);
        group.MapGet("/{id:guid}", async (Guid id, VerceDbContext db, CancellationToken ct) =>
        {
            var value = await db.Set<Expense>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            return value is null ? Results.NotFound() : Results.Ok(ToResponse(value));
        }).RequireAuthorization(Permissions.ExpensesRead);
        group.MapPost("", (ExpenseWriteRequest request, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users,
            AmbientOperationContext ambient, IUnitOfWork uow, VerceDbContext readDb, CancellationToken ct) => Write(null, request, http, antiforgery, users, ambient, uow, readDb, ct))
            .RequireAuthorization(Permissions.ExpensesManage);
        group.MapPut("/{id:guid}", (Guid id, ExpenseWriteRequest request, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users,
            AmbientOperationContext ambient, IUnitOfWork uow, VerceDbContext readDb, CancellationToken ct) => Write(id, request, http, antiforgery, users, ambient, uow, readDb, ct))
            .RequireAuthorization(Permissions.ExpensesManage);
        group.MapDelete("/{id:guid}", async (Guid id, long version, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users,
            AmbientOperationContext ambient, IUnitOfWork uow, CancellationToken ct) =>
        {
            if (!await Csrf(http, antiforgery)) return BadRequest();
            try
            {
                var actor = await users.GetUserAsync(http.User); if (actor is null) return Results.Unauthorized(); ambient.SetActor(actor.Id, actor.DisplayName);
                await uow.ExecuteAsync(async (db, token) =>
                {
                    var expense = await db.Set<Expense>().SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException();
                    if (expense.Version != version) throw new DbUpdateConcurrencyException();
                    db.Remove(expense);
                }, ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException) { return Problem("CONCURRENCY_CONFLICT", 409); }
        }).RequireAuthorization(Permissions.ExpensesManage);
        return app;
    }

    private static async Task<IResult> Write(Guid? id, ExpenseWriteRequest request, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users,
        AmbientOperationContext ambient, IUnitOfWork uow, VerceDbContext readDb, CancellationToken ct)
    {
        if (!await Csrf(http, antiforgery)) return BadRequest();
        try
        {
            var actor = await users.GetUserAsync(http.User); if (actor is null) return Results.Unauthorized(); ambient.SetActor(actor.Id, actor.DisplayName);
            if (!await readDb.Set<ExpenseCategory>().AnyAsync(x => x.Id == request.ExpenseCategoryId && x.IsActive, ct)) return Problem("EXPENSE_CATEGORY_NOT_FOUND", 404);
            if (request.InventoryMovementId is { } movementId && !await readDb.Set<InventoryMovement>().AnyAsync(x => x.Id == movementId && x.Type == InventoryMovementType.PurchaseReceipt, ct))
                return Problem("EXPENSE_INVENTORY_MOVEMENT_NOT_PURCHASE_RECEIPT", 422);
            Expense? saved = null;
            await uow.ExecuteAsync(async (db, token) =>
            {
                if (id is null) { saved = Create(request); db.Add(saved); }
                else
                {
                    saved = await db.Set<Expense>().SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException();
                    if (request.Version is null || saved.Version != request.Version) throw new DbUpdateConcurrencyException();
                    Apply(saved, request);
                }
            }, ct);
            return id is null ? Results.Created($"/api/expenses/{saved!.Id}", ToResponse(saved!)) : Results.Ok(ToResponse(saved!));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (DbUpdateConcurrencyException) { return Problem("CONCURRENCY_CONFLICT", 409); }
        catch (ArgumentException ex) { return Problem(ex.Message, 422); }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true) { return Problem("EXPENSE_INVENTORY_MOVEMENT_ALREADY_LINKED", 409); }
    }

    private static Expense Create(ExpenseWriteRequest r) => new(r.ExpenseCategoryId, r.Description, r.Amount, r.IncurredOn, r.AccountingTreatment, r.PaidOn, r.PaymentMethod, r.SupplierName, r.DocumentNumber, r.InventoryMovementId, r.SalesChannelId, r.MachineId, r.AttachmentPath, r.Notes);
    private static void Apply(Expense e, ExpenseWriteRequest r) => e.Update(r.ExpenseCategoryId, r.Description, r.Amount, r.IncurredOn, r.AccountingTreatment, r.PaidOn, r.PaymentMethod, r.SupplierName, r.DocumentNumber, r.InventoryMovementId, r.SalesChannelId, r.MachineId, r.AttachmentPath, r.Notes);
    private static ExpenseResponse ToResponse(Expense e) => new(e.Id, e.ExpenseCategoryId, e.Description, e.Amount, e.IncurredOn, e.PaidOn, e.AccountingTreatment, e.InventoryMovementId, e.SalesChannelId, e.SupplierName, e.Notes, e.Version);
    private static async Task<bool> Csrf(HttpContext h, IAntiforgery a) { try { await a.ValidateRequestAsync(h); return true; } catch (AntiforgeryValidationException) { return false; } }
    private static IResult BadRequest() => Results.Problem(statusCode: 400, title: "Requisição inválida.");
    private static IResult Problem(string code, int status) => Results.Problem(statusCode: status, title: "Não foi possível concluir a operação.", extensions: new Dictionary<string, object?> { ["code"] = code });
}
