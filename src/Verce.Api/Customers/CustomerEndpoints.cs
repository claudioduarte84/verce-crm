using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Verce.Api.Authorization;
using Verce.Modules.Customers;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Time;

namespace Verce.Api.Customers;

public sealed record CustomerRequest(PersonType PersonType, string Name, string? TradeName, string? Document, string? Email, string? Phone, string? Notes, long Version);
public sealed record AddressRequest(string Label, string ZipCode, string Street, string Number, string? Complement, string District, string City, string State, string? Country, bool IsPrimary, bool IsDefaultShipping, string? Notes, long CustomerVersion);
public sealed record CustomerResponse(Guid Id, PersonType PersonType, string Name, string? TradeName, string? Document, string? Email, string? Phone, string? Notes, bool IsActive, long Version, IReadOnlyList<AddressResponse> Addresses);
public sealed record AddressResponse(Guid Id, string Label, string ZipCode, string Street, string Number, string? Complement, string District, string City, string State, string Country, bool IsPrimary, bool IsDefaultShipping, string? Notes);
/// <summary>M-S2-005: a NAMED response shape for the list endpoint, replacing the anonymous
/// projection it used to return. Anonymous types erase to <c>object</c> through <c>Results.Ok</c>,
/// so the OpenAPI generator could not produce a schema for them at all — the generated frontend
/// contract would have been untyped. Field names are unchanged, so the JSON wire shape is identical.</summary>
public sealed record CustomerListItemResponse(Guid Id, string Name, string? TradeName, string? Document, string? Email, string? Phone, bool IsActive, long Version);
public sealed record CustomerListResponse(IReadOnlyList<CustomerListItemResponse> Items, int Page, int PageSize, int Total);

public static class CustomerEndpoints
{
    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/customers");
        group.MapGet("", async (string? search, bool? active, int page, int pageSize, VerceDbContext db, CancellationToken ct) =>
        {
            page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize == 0 ? 25 : pageSize, 1, 100);
            var query = db.Set<Customer>().AsNoTracking().Where(x => x.DeletedAt == null);
            if (active is not null) query = query.Where(x => x.IsActive == active);
            if (!string.IsNullOrWhiteSpace(search)) { var term = $"%{search.Trim()}%"; query = query.Where(x => EF.Functions.ILike(x.Name, term) || (x.TradeName != null && EF.Functions.ILike(x.TradeName, term)) || (x.Document != null && EF.Functions.ILike(x.Document, term)) || (x.Email != null && EF.Functions.ILike(x.Email, term)) || (x.Phone != null && EF.Functions.ILike(x.Phone, term))); }
            var total = await query.CountAsync(ct); var rows = await query.OrderBy(x => x.Name).ThenBy(x => EF.Property<DateTimeOffset>(x, "CreatedAt")).ThenBy(x => EF.Property<long>(x, CustomerConfiguration.CreationSequence)).Skip((page - 1) * pageSize).Take(pageSize).Select(x => new CustomerListItemResponse(x.Id, x.Name, x.TradeName, x.Document, x.Email, x.Phone, x.IsActive, x.Version)).ToListAsync(ct);
            return Results.Ok(new CustomerListResponse(rows, page, pageSize, total));
        }).RequireAuthorization(Permissions.CustomersRead).Produces<CustomerListResponse>();
        group.MapGet("/{id:guid}", async (Guid id, VerceDbContext db, CancellationToken ct) => { var customer = await db.Set<Customer>().AsNoTracking().Include(x => x.Addresses).SingleOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct); return customer is null ? Results.NotFound() : Results.Ok(ToResponse(customer)); }).RequireAuthorization(Permissions.CustomersRead).Produces<CustomerResponse>().Produces(StatusCodes.Status404NotFound);
        group.MapPost("", async (CustomerRequest request, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, VerceDbContext db, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
            try { var actor = await users.GetUserAsync(http.User); if (actor is null) return Results.Unauthorized(); ambient.SetActor(actor.Id, actor.DisplayName); Customer? created = null; await uow.ExecuteAsync((context, token) => { created = new Customer(request.PersonType, request.Name, request.TradeName, request.Document, request.Email, request.Phone, request.Notes); context.Add(created); return Task.CompletedTask; }, ct); return Results.Created($"/api/customers/{created!.Id}", ToResponse(created!)); }
            catch (ArgumentException ex) { return Problem(ex.Message); }
            catch (DbUpdateException ex) when (IsUnique(ex)) { return Problem("CUSTOMER_DOCUMENT_DUPLICATE", StatusCodes.Status409Conflict); }
        }).RequireAuthorization(Permissions.CustomersManage).Produces<CustomerResponse>(StatusCodes.Status201Created);
        group.MapPut("/{id:guid}", async (Guid id, CustomerRequest request, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, VerceDbContext db, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
            try { var actor = await users.GetUserAsync(http.User); if (actor is null) return Results.Unauthorized(); ambient.SetActor(actor.Id, actor.DisplayName); Customer? customer = null; await uow.ExecuteAsync(async (context, token) => { customer = await context.Set<Customer>().Include(x => x.Addresses).SingleOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, token) ?? throw new KeyNotFoundException(); if (customer.Version != request.Version) throw new DbUpdateConcurrencyException(); customer.Update(request.PersonType, request.Name, request.TradeName, request.Document, request.Email, request.Phone, request.Notes); }, ct); return Results.Ok(ToResponse(customer!)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException) { return Problem("CONCURRENCY_CONFLICT", StatusCodes.Status409Conflict); }
            catch (ArgumentException ex) { return Problem(ex.Message); }
            catch (DbUpdateException ex) when (IsUnique(ex)) { return Problem("CUSTOMER_DOCUMENT_DUPLICATE", StatusCodes.Status409Conflict); }
        }).RequireAuthorization(Permissions.CustomersManage).Produces<CustomerResponse>();
        group.MapDelete("/{id:guid}", async (Guid id, long version, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, IClock clock, CancellationToken ct) =>
        { if (!await IsValidCsrf(http, antiforgery)) return BadRequest(); try { var actor = await users.GetUserAsync(http.User); if (actor is null) return Results.Unauthorized(); ambient.SetActor(actor.Id, actor.DisplayName); await uow.ExecuteAsync(async (db, token) => { var customer = await db.Set<Customer>().SingleOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, token) ?? throw new KeyNotFoundException(); if (customer.Version != version) throw new DbUpdateConcurrencyException(); customer.SoftDelete(clock.UtcNow); }, ct); return Results.NoContent(); } catch (KeyNotFoundException) { return Results.NotFound(); } catch (DbUpdateConcurrencyException) { return Problem("CONCURRENCY_CONFLICT", StatusCodes.Status409Conflict); } }).RequireAuthorization(Permissions.CustomersManage).Produces(StatusCodes.Status204NoContent);
        group.MapPost("/{id:guid}/addresses", async (Guid id, AddressRequest request, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, CancellationToken ct) => await MutateAddress(id, null, request, http, antiforgery, users, ambient, uow, ct)).RequireAuthorization(Permissions.CustomersManage).Produces<AddressResponse>(StatusCodes.Status201Created);
        group.MapPut("/{id:guid}/addresses/{addressId:guid}", async (Guid id, Guid addressId, AddressRequest request, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, CancellationToken ct) => await MutateAddress(id, addressId, request, http, antiforgery, users, ambient, uow, ct)).RequireAuthorization(Permissions.CustomersManage).Produces<AddressResponse>();
        group.MapDelete("/{id:guid}/addresses/{addressId:guid}", async (Guid id, Guid addressId, long customerVersion, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, CancellationToken ct) => { if (!await IsValidCsrf(http, antiforgery)) return BadRequest(); try { var actor = await users.GetUserAsync(http.User); if (actor is null) return Results.Unauthorized(); ambient.SetActor(actor.Id, actor.DisplayName); await uow.ExecuteAsync(async (db, token) => { var customer = await db.Set<Customer>().Include(x => x.Addresses).SingleOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, token) ?? throw new KeyNotFoundException(); if (customer.Version != customerVersion) throw new DbUpdateConcurrencyException(); customer.RemoveAddress(addressId); }, ct); return Results.NoContent(); } catch (KeyNotFoundException) { return Results.NotFound(); } catch (DbUpdateConcurrencyException) { return Problem("CONCURRENCY_CONFLICT", StatusCodes.Status409Conflict); } catch (InvalidOperationException ex) { return Problem(ex.Message, StatusCodes.Status404NotFound); } }).RequireAuthorization(Permissions.CustomersManage).Produces(StatusCodes.Status204NoContent);
        return app;
    }
    private static async Task<IResult> MutateAddress(Guid customerId, Guid? addressId, AddressRequest request, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, CancellationToken ct)
    {
        if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
        try
        {
            var actor = await users.GetUserAsync(http.User);
            if (actor is null) return Results.Unauthorized();
            ambient.SetActor(actor.Id, actor.DisplayName);
            CustomerAddress? address = null;
            await uow.ExecuteAsync(async (db, token) =>
            {
                var customer = await db.Set<Customer>().Include(x => x.Addresses).SingleOrDefaultAsync(x => x.Id == customerId && x.DeletedAt == null, token) ?? throw new KeyNotFoundException();
                if (customer.Version != request.CustomerVersion) throw new StaleCustomerVersionException(request.CustomerVersion, customer.Version);
                if (addressId is null)
                    address = customer.AddAddress(request.Label, request.ZipCode, request.Street, request.Number, request.Complement, request.District, request.City, request.State, request.Country, request.IsPrimary, request.IsDefaultShipping, request.Notes);
                else
                {
                    customer.UpdateAddress(addressId.Value, request.Label, request.ZipCode, request.Street, request.Number, request.Complement, request.District, request.City, request.State, request.Country, request.IsPrimary, request.IsDefaultShipping, request.Notes);
                    address = customer.Addresses.Single(x => x.Id == addressId.Value);
                }
            }, ct);
            return addressId is null ? Results.Created($"/api/customers/{customerId}/addresses/{address!.Id}", ToAddress(address!)) : Results.Ok(ToAddress(address!));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (StaleCustomerVersionException) { return Problem("CONCURRENCY_CONFLICT", StatusCodes.Status409Conflict); }
        catch (DbUpdateConcurrencyException) { return Problem("CONCURRENCY_CONFLICT", StatusCodes.Status409Conflict); }
        catch (ArgumentException ex) { return Problem(ex.Message); }
        catch (DbUpdateException ex) when (IsUnique(ex)) { return Problem("ADDRESS_DEFAULT_CONFLICT", StatusCodes.Status409Conflict); }
    }
    private static CustomerResponse ToResponse(Customer c) => new(c.Id, c.PersonType, c.Name, c.TradeName, c.Document, c.Email, c.Phone, c.Notes, c.IsActive, c.Version, c.Addresses.OrderBy(x => x.Label).Select(ToAddress).ToList()); private static AddressResponse ToAddress(CustomerAddress x) => new(x.Id, x.Label, x.ZipCode, x.Street, x.Number, x.Complement, x.District, x.City, x.State, x.Country, x.IsPrimary, x.IsDefaultShipping, x.Notes);
    private static async Task<bool> IsValidCsrf(HttpContext http, IAntiforgery antiforgery) { try { await antiforgery.ValidateRequestAsync(http); return true; } catch (AntiforgeryValidationException) { return false; } }
    private static IResult BadRequest() => Results.Problem(statusCode: 400, title: "Requisição inválida."); private static IResult Problem(string code, int status = 400) => Results.Problem(statusCode: status, title: "Não foi possível concluir a operação.", extensions: new Dictionary<string, object?> { ["code"] = code }); private static bool IsUnique(DbUpdateException ex) => ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true;
}

file sealed class StaleCustomerVersionException(long requested, long actual) : Exception($"Requested version {requested}; current version {actual}");
