using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Verce.Api.Authorization;
using Verce.Api.Catalog;
using Verce.Modules.Catalog;
using Verce.Modules.Costing;
using Verce.Modules.Pricing;
using Verce.Modules.Quoting;
using Verce.Modules.Sales;
using Verce.Modules.Settings;
using Verce.Platform.Identity;
using Verce.Platform.Numbering;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel;
using Verce.SharedKernel.Time;

namespace Verce.Api.Sales;

public sealed record ConvertQuoteRequest(Guid ConversionRequestId);
public sealed record ManualSaleItemRequest(Guid ProductId, decimal Quantity, decimal UnitPrice, decimal DiscountAmount);
public sealed record ManualSaleRequest(Guid SalesChannelId, Guid? CustomerId, string? CustomerNameSnapshot, DateTimeOffset? SoldAt,
    decimal ShippingAmount, string? Notes, IReadOnlyList<ManualSaleItemRequest> Items);
public sealed record CancelSaleRequest(string Reason, long Version);
public sealed record SaleItemResponse(Guid Id, Guid? ProductId, Guid? QuoteItemId, string ProductName, decimal Quantity, decimal UnitPrice,
    decimal DiscountAmount, decimal LineTotalAmount, decimal UnitCostAmount, decimal LineCostAmount, decimal ChannelFeeAmount, decimal GrossProfitAmount);
public sealed record SaleHistoryResponse(Guid Id, SaleStatus? FromStatus, SaleStatus ToStatus, string? Reason, DateTimeOffset ChangedAt, Guid? ChangedBy);
public sealed record SaleResponse(Guid Id, string SaleNumber, SaleSource Source, SaleStatus Status, Guid SalesChannelId, Guid? QuoteRevisionId,
    Guid? CustomerId, string? CustomerNameSnapshot, DateTimeOffset SoldAt, decimal GrossAmount, decimal DiscountAmount, decimal NetAmount,
    decimal ChannelFeeAmount, decimal ShippingAmount, decimal TotalCostAmount, decimal GrossProfitAmount, decimal EffectiveMarginPercent,
    SaleCostBasis CostBasis, long Version, IReadOnlyList<SaleItemResponse> Items, IReadOnlyList<SaleHistoryResponse> History);
public sealed record SaleListResponse(IReadOnlyList<SaleResponse> Items, int Page, int PageSize, int Total);

public static class SalesEndpoints
{
    public static IEndpointRouteBuilder MapSalesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sales");
        group.MapGet("", async (VerceDbContext db, SaleStatus? status, Guid? channel, Guid? customer, DateOnly? from, DateOnly? to,
            int page = 1, int pageSize = 25, CancellationToken ct = default) =>
        {
            page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, 100);
            var query = db.Set<Sale>().AsNoTracking().AsQueryable();
            if (status is not null) query = query.Where(x => x.Status == status);
            if (channel is not null) query = query.Where(x => x.SalesChannelId == channel);
            if (customer is not null) query = query.Where(x => x.CustomerId == customer);
            if (from is not null) query = query.Where(x => x.SoldDate >= from);
            if (to is not null) query = query.Where(x => x.SoldDate <= to);
            var total = await query.CountAsync(ct);
            var rows = await query.OrderByDescending(x => x.SoldDate).ThenByDescending(x => x.SaleNumber).Skip((page - 1) * pageSize).Take(pageSize)
                .Include(x => x.Items).Include(x => x.History).ToListAsync(ct);
            return Results.Ok(new SaleListResponse(rows.Select(ToResponse).ToArray(), page, pageSize, total));
        }).RequireAuthorization(Permissions.SalesRead);
        group.MapGet("/{id:guid}", async (Guid id, VerceDbContext db, CancellationToken ct) =>
        {
            var sale = await Load(db, id, ct); return sale is null ? Results.NotFound() : Results.Ok(ToResponse(sale));
        }).RequireAuthorization(Permissions.SalesRead);
        group.MapPost("", async (ManualSaleRequest request, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users,
            AmbientOperationContext ambient, IUnitOfWork uow, VerceDbContext readDb, ICostingInventoryReader inventory, AppSettingValueReader settings,
            IClock clock, CancellationToken ct) =>
        {
            if (!await Csrf(http, antiforgery)) return BadRequest();
            try
            {
                var actor = await users.GetUserAsync(http.User); if (actor is null) return Results.Unauthorized(); ambient.SetActor(actor.Id, actor.DisplayName);
                if (request.Items is null || request.Items.Count == 0) return Problem("SALE_ITEMS_REQUIRED", 422);
                var channel = await readDb.Set<SalesChannel>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.SalesChannelId && x.Active, ct);
                if (channel is null) return Problem("SALE_SALES_CHANNEL_NOT_FOUND", 404);
                var feeRule = await readDb.Set<FeeRule>().AsNoTracking().Include(x => x.Versions).ThenInclude(x => x.Brackets)
                    .SingleOrDefaultAsync(x => x.SalesChannelId == request.SalesChannelId && x.Active, ct);
                var feeVersion = feeRule?.ResolveVersionAt(clock.OrganizationToday());
                if (feeVersion is null) return Problem("FEE_RULE_NOT_FOUND", 422);
                var roundingPolicy = await settings.GetStringAsync("pricing.price_rounding_policy", ct);
                if (!Enum.TryParse<PriceRoundingPolicy>(roundingPolicy, out var policy)) return Problem("PRICING_ROUNDING_POLICY_INVALID", 422);
                var marginWarningDenominator = await settings.GetDecimalAsync("pricing.margin_warning_denominator", ct);
                var products = await readDb.Set<Product>().Include(x => x.Recipe).ThenInclude(x => x.MaterialLines)
                    .Include(x => x.Recipe).ThenInclude(x => x.AdditionalCostLines).Where(x => request.Items.Select(i => i.ProductId).Contains(x.Id)).ToListAsync(ct);
                if (products.Count != request.Items.Select(x => x.ProductId).Distinct().Count()) return Problem("PRODUCT_NOT_FOUND", 404);
                // Resolve every commercial fact first. An order-level fixed fee can only be
                // allocated after the complete Sale is known; calculating it inside this loop
                // would silently charge it once per quantity/line.
                var calculated = new List<(ManualSaleItemRequest Input, Product Product, CostCalculationResult Cost, CommercialPricingResult Commercial)>();
                foreach (var input in request.Items)
                {
                    var product = products.Single(x => x.Id == input.ProductId);
                    if (!product.Active) return Problem("PRODUCT_INACTIVE", 422);
                    CostCalculationResult cost;
                    try { cost = await ProductCostCalculator.CalculateAsync(product, inventory, settings, ct); }
                    catch (ArgumentException ex) when (ex.Message == "COST_BASIS_UNAVAILABLE") { return Problem("COST_BASIS_UNAVAILABLE", 422); }
                    if (input.Quantity <= 0) return Problem("SALE_ITEM_INVALID", 422);
                    var commercial = CommercialPricingEngine.Calculate(new CommercialPricingInput(cost.Totals.EstimatedUnitCost, 0m, policy,
                        marginWarningDenominator, input.UnitPrice, CommercialDiscountKind.Amount, input.DiscountAmount,
                        feeVersion.CommissionPercent, feeVersion.FixedFeeApplication == FixedFeeApplication.PerOrder ? 0m : feeVersion.FixedFee,
                        feeVersion.MinimumFee, feeVersion.MaximumFee, feeVersion.Brackets));
                    if (commercial.IsFailure) return Problem(commercial.ErrorCode!, 422);
                    calculated.Add((input, product, cost, commercial.Value));
                }

                var orderFeeAllocations = feeVersion.FixedFeeApplication == FixedFeeApplication.PerOrder
                    ? OrderFixedFeeAllocator.Allocate(feeVersion.FixedFee, calculated.Select((x, index) =>
                        new OrderFeeAllocationInput(index + 1, Verce.SharedKernel.Rounding.ToInternal(x.Cost.Totals.EstimatedUnitCost * x.Input.Quantity))).ToArray())
                    : calculated.Select((_, index) => new OrderFeeAllocationResult(index + 1, 0m)).ToArray();
                var snapshots = new List<SaleItemSnapshot>(calculated.Count);
                for (var index = 0; index < calculated.Count; index++)
                {
                    var (input, product, cost, commercial) = calculated[index];
                    var lineTotal = Verce.SharedKernel.Rounding.ToMoney(commercial.NetUnitPrice * input.Quantity);
                    var lineCost = Verce.SharedKernel.Rounding.ToMoney(cost.Totals.EstimatedUnitCost * input.Quantity);
                    var fixedFee = feeVersion.FixedFeeApplication == FixedFeeApplication.PerOrder
                        ? orderFeeAllocations[index].AllocatedAmount
                        : Verce.SharedKernel.Rounding.ToMoney(commercial.FixedFee * input.Quantity);
                    var fee = Verce.SharedKernel.Rounding.ToMoney(commercial.CommissionAmount * input.Quantity + fixedFee);
                    var profit = Verce.SharedKernel.Rounding.ToMoney(lineTotal - fee - lineCost);
                    snapshots.Add(new SaleItemSnapshot(product.Id, null, product.Name, input.Quantity, commercial.UnitPrice, commercial.DiscountAmount,
                        lineTotal, cost.Totals.EstimatedUnitCost, lineCost, fee, profit, lineTotal > 0 ? Verce.SharedKernel.Rounding.ToPercent(profit / lineTotal) : 0m));
                }
                Sale? sale = null;
                await uow.ExecuteAsync(async (db, token) =>
                {
                    var numberDate = clock.OrganizationToday(); var sequence = await SequentialNumberAllocator.AllocateAsync(db, SequentialNumberAllocator.SaleSeries, numberDate, token);
                    sale = new Sale(sequence, numberDate, SaleSource.MANUAL_ENTRY, request.SalesChannelId, null, null, null, null, SaleFeeSource.LOCAL_RULE,
                        request.CustomerId, request.CustomerNameSnapshot, request.SoldAt ?? clock.UtcNow, request.ShippingAmount, null, request.Notes, snapshots, actor.Id, clock.UtcNow);
                    db.Add(sale);
                }, ct);
                return Results.Created($"/api/sales/{sale!.Id}", ToResponse(sale!));
            }
            catch (ArgumentException ex) { return Problem(ex.Message, 422); }
        }).RequireAuthorization(Permissions.SalesManage);
        group.MapPost("/from-quote/{quoteRevisionId:guid}", async (Guid quoteRevisionId, ConvertQuoteRequest request, HttpContext http, IAntiforgery antiforgery,
            UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, IClock clock, CancellationToken ct) =>
        {
            if (!await Csrf(http, antiforgery)) return BadRequest();
            if (request.ConversionRequestId == Guid.Empty) return Problem("CONVERSION_REQUEST_ID_REQUIRED", 422);
            try
            {
                var actor = await users.GetUserAsync(http.User); if (actor is null) return Results.Unauthorized(); ambient.SetActor(actor.Id, actor.DisplayName);
                Sale? result = null;
                await uow.ExecuteAsync(async (db, token) =>
                {
                    await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended({0}, 0))", [quoteRevisionId.ToString()], token);
                    var replay = await db.Set<Sale>().Include(x => x.Items).Include(x => x.History).SingleOrDefaultAsync(x => x.ConversionRequestId == request.ConversionRequestId, token);
                    if (replay is not null) { result = replay; return; }
                    var existing = await db.Set<Sale>().AnyAsync(x => x.QuoteRevisionId == quoteRevisionId && x.Status != SaleStatus.CANCELED, token);
                    if (existing) throw new ArgumentException("SALE_ALREADY_EXISTS_FOR_REVISION");
                    var revision = await db.Set<QuoteRevision>().Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == quoteRevisionId, token) ?? throw new KeyNotFoundException();
                    if (revision.Status != QuoteRevisionStatus.APPROVED) throw new ArgumentException("QUOTE_REVISION_NOT_SALE_ELIGIBLE");
                    var numberDate = clock.OrganizationToday(); var sequence = await SequentialNumberAllocator.AllocateAsync(db, SequentialNumberAllocator.SaleSeries, numberDate, token);
                    var items = revision.Items.OrderBy(x => x.LineNumber).Select(x => new SaleItemSnapshot(x.ProductId, x.Id, x.ProductNameSnapshot, x.Quantity,
                        x.UnitPrice, x.DiscountAmount, x.LineTotalAmount, x.UnitTotalCost, x.LineCostAmount, x.LineFeeAmount, x.ExpectedProfitAmount, x.EffectiveMarginPercent)).ToArray();
                    result = new Sale(sequence, numberDate, SaleSource.QUOTE_CONVERSION, revision.SalesChannelId, revision.Id, request.ConversionRequestId, null, null,
                        SaleFeeSource.LOCAL_RULE, revision.CustomerId, revision.CustomerNameSnapshot, clock.UtcNow, 0m, null, revision.Notes, items, actor.Id, clock.UtcNow);
                    db.Add(result);
                }, ct);
                return Results.Ok(ToResponse(result!));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException ex) { return Problem(ex.Message, ex.Message is "SALE_ALREADY_EXISTS_FOR_REVISION" or "QUOTE_REVISION_NOT_SALE_ELIGIBLE" ? 409 : 422); }
        }).RequireAuthorization(Permissions.SalesManage);
        group.MapPost("/{id:guid}/cancel", async (Guid id, CancelSaleRequest request, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users,
            AmbientOperationContext ambient, IUnitOfWork uow, IClock clock, CancellationToken ct) =>
        {
            if (!await Csrf(http, antiforgery)) return BadRequest();
            try
            {
                var actor = await users.GetUserAsync(http.User); if (actor is null) return Results.Unauthorized(); ambient.SetActor(actor.Id, actor.DisplayName);
                await uow.ExecuteAsync(async (db, token) =>
                {
                    var sale = await Load(db, id, token) ?? throw new KeyNotFoundException();
                    if (sale.Version != request.Version) throw new DbUpdateConcurrencyException(); sale.Cancel(request.Reason, actor.Id, clock.UtcNow);
                }, ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException) { return Problem("CONCURRENCY_CONFLICT", 409); }
            catch (ArgumentException ex) { return Problem(ex.Message, 422); }
        }).RequireAuthorization(Permissions.SalesManage);
        return app;
    }

    private static Task<Sale?> Load(VerceDbContext db, Guid id, CancellationToken ct) => db.Set<Sale>().Include(x => x.Items).Include(x => x.History).SingleOrDefaultAsync(x => x.Id == id, ct);
    private static SaleResponse ToResponse(Sale s) => new(s.Id, s.SaleNumber, s.Source, s.Status, s.SalesChannelId, s.QuoteRevisionId, s.CustomerId, s.CustomerNameSnapshot,
        s.SoldAt, s.GrossAmount, s.DiscountAmount, s.NetAmount, s.ChannelFeeAmount, s.ShippingAmount, s.TotalCostAmount, s.GrossProfitAmount, s.EffectiveMarginPercent, s.CostBasis, s.Version,
        s.Items.OrderBy(x => x.LineNumber).Select(x => new SaleItemResponse(x.Id, x.ProductId, x.QuoteItemId, x.ProductNameSnapshot, x.Quantity, x.UnitPrice, x.DiscountAmount, x.LineTotalAmount, x.UnitCostAmount, x.LineCostAmount, x.ChannelFeeAmount, x.GrossProfitAmount)).ToArray(),
        s.History.OrderBy(x => x.ChangedAt).Select(x => new SaleHistoryResponse(x.Id, x.FromStatus, x.ToStatus, x.Reason, x.ChangedAt, x.ChangedBy)).ToArray());
    private static async Task<bool> Csrf(HttpContext h, IAntiforgery a) { try { await a.ValidateRequestAsync(h); return true; } catch (AntiforgeryValidationException) { return false; } }
    private static IResult BadRequest() => Results.Problem(statusCode: 400, title: "Requisição inválida.");
    private static IResult Problem(string code, int status) => Results.Problem(statusCode: status, title: "Não foi possível concluir a operação.", extensions: new Dictionary<string, object?> { ["code"] = code });
}
