using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Verce.Api.Authorization;
using Verce.Modules.Catalog;
using Verce.Modules.Commerce;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;

namespace Verce.Api.Commerce;

// ---- S8C.2 residual C1/C2: explicit SKU authority (ADR-0025 §D, §J). Provider-neutral; no user/actor ids, credentials or provider data in any DTO. ----

/// <summary>Request bodies are strict (ADR-0025 §J / handoff C1): an unknown property is rejected, never ignored.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SkuMappingConfirmRequest(string? ExternalSku, Guid ProductId, Guid? ReplaceMappingId, long? ReplaceMappingVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SkuMappingDeactivateRequest(long? Version);

/// <summary>Product code/name are composed from Catalog at read time; the mapping table stores only the logical product id.</summary>
public sealed record SkuMappingResponse(Guid Id, Guid MarketplaceAccountId, string ExternalSku, Guid ProductId, string? ProductCode, string? ProductName, bool Active,
    DateTimeOffset ConfirmedAt, DateTimeOffset? InvalidatedAt,
    [property: JsonConverter(typeof(JsonStringEnumConverter<MarketplaceAccountSkuMappingInvalidatedReason>))] MarketplaceAccountSkuMappingInvalidatedReason? InvalidatedReason,
    long Version);

public static class SkuMappingEndpoints
{
    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/marketplace-accounts/{id:guid}/sku-mappings", ListAsync).RequireAuthorization(Permissions.CommerceRead)
            .Produces<PagedResponse<SkuMappingResponse>>().Produces(StatusCodes.Status404NotFound);
        group.MapPost("/marketplace-accounts/{id:guid}/sku-mappings", ConfirmAsync).RequireAuthorization(Permissions.CommerceManage)
            .Accepts<SkuMappingConfirmRequest>("application/json")
            .Produces<SkuMappingResponse>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        group.MapPost("/sku-mappings/{mappingId:guid}/deactivate", DeactivateAsync).RequireAuthorization(Permissions.CommerceManage)
            .Accepts<SkuMappingDeactivateRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<bool> Csrf(HttpContext h, IAntiforgery a) { try { await a.ValidateRequestAsync(h); return true; } catch (AntiforgeryValidationException) { return false; } }

    /// <summary>Reads the JSON body strictly; a missing, malformed or unknown-property body yields null (HTTP 400).</summary>
    private static async Task<T?> ReadBodyAsync<T>(HttpRequest request, CancellationToken ct) where T : class
    {
        try { return await JsonSerializer.DeserializeAsync<T>(request.Body, StrictJson, ct); }
        catch (JsonException) { return null; }
    }

    private static IResult Refusal(string code, int status) =>
        Results.Problem(statusCode: status, title: code, extensions: new Dictionary<string, object?> { ["code"] = code });

    private static IResult Refused(string code) => Refusal(code, code switch
    {
        MarketplaceSkuMappingService.SkuInvalid => StatusCodes.Status422UnprocessableEntity,
        MarketplaceSkuMappingService.ProductIneligible => StatusCodes.Status422UnprocessableEntity,
        MarketplaceSkuMappingService.Conflict => StatusCodes.Status409Conflict,
        MarketplaceSkuMappingService.ConcurrencyConflict => StatusCodes.Status409Conflict,
        _ => throw new InvalidOperationException("SKU_MAPPING_RESULT_UNMAPPED"),
    });

    private static async Task<IReadOnlyList<SkuMappingResponse>> ComposeAsync(VerceDbContext db, IReadOnlyList<MarketplaceAccountSkuMapping> rows, CancellationToken ct)
    {
        var productIds = rows.Select(x => x.ProductId).Distinct().ToArray();
        var products = await db.Set<Product>().AsNoTracking().Where(x => productIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        return rows.Select(x =>
        {
            var product = products.GetValueOrDefault(x.ProductId);
            return new SkuMappingResponse(x.Id, x.MarketplaceAccountId, x.ExternalSku, x.ProductId, product?.Code, product?.Name, x.Active, x.ConfirmedAt, x.InvalidatedAt, x.InvalidatedReason, x.Version);
        }).ToList();
    }

    private static async Task<IResult> ListAsync(Guid id, VerceDbContext db, CancellationToken ct, bool? active = null, int page = 1, int pageSize = 25)
    {
        if (!await db.Set<MarketplaceAccount>().AsNoTracking().AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
        var query = db.Set<MarketplaceAccountSkuMapping>().AsNoTracking().Where(x => x.MarketplaceAccountId == id);
        if (active is { } flag) query = query.Where(x => x.Active == flag);
        var safePage = Math.Max(1, page); var size = Math.Clamp(pageSize, 1, 100); var total = await query.CountAsync(ct);
        // Identity is never an ordering key (ADR-0011): SKU, then most recently confirmed first, then product keep equal-SKU history pages stable.
        var rows = await query.OrderBy(x => x.ExternalSku).ThenByDescending(x => x.ConfirmedAt).ThenBy(x => x.ProductId).Skip((safePage - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(new PagedResponse<SkuMappingResponse>(await ComposeAsync(db, rows, ct), safePage, size, total));
    }

    private static async Task<IResult> ConfirmAsync(Guid id, HttpContext h, IAntiforgery csrf, UserManager<ApplicationUser> users, AmbientOperationContext ambient,
        MarketplaceSkuMappingService service, VerceDbContext db, CancellationToken ct)
    {
        if (!await Csrf(h, csrf)) return Refusal("ANTIFORGERY_VALIDATION_FAILED", StatusCodes.Status400BadRequest);
        var request = await ReadBodyAsync<SkuMappingConfirmRequest>(h.Request, ct);
        if (request is null) return Refusal("REQUEST_BODY_INVALID", StatusCodes.Status400BadRequest);
        var actor = await users.GetUserAsync(h.User); if (actor is null) return Results.Unauthorized();
        ambient.SetActor(actor.Id, actor.DisplayName);
        try
        {
            var result = await service.ConfirmAsync(id, request.ExternalSku, request.ProductId, request.ReplaceMappingId, request.ReplaceMappingVersion, actor.Id, ct);
            if (!result.Succeeded) return Refused(result.Code!);
            db.ChangeTracker.Clear();
            var created = await db.Set<MarketplaceAccountSkuMapping>().AsNoTracking().SingleAsync(x => x.Id == result.MappingId, ct);
            return Results.Json((await ComposeAsync(db, [created], ct))[0], statusCode: StatusCodes.Status201Created);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> DeactivateAsync(Guid mappingId, HttpContext h, IAntiforgery csrf, UserManager<ApplicationUser> users, AmbientOperationContext ambient,
        MarketplaceSkuMappingService service, CancellationToken ct)
    {
        if (!await Csrf(h, csrf)) return Refusal("ANTIFORGERY_VALIDATION_FAILED", StatusCodes.Status400BadRequest);
        var request = await ReadBodyAsync<SkuMappingDeactivateRequest>(h.Request, ct);
        if (request?.Version is not { } version) return Refusal("REQUEST_BODY_INVALID", StatusCodes.Status400BadRequest);
        var actor = await users.GetUserAsync(h.User); if (actor is null) return Results.Unauthorized();
        ambient.SetActor(actor.Id, actor.DisplayName);
        try
        {
            var result = await service.DeactivateAsync(mappingId, version, actor.Id, ct);
            return result.Succeeded ? Results.NoContent() : Refused(result.Code!);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }
}
