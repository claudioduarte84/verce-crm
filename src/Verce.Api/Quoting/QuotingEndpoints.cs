using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Verce.Api.Authorization;
using Verce.Api.Catalog;
using Verce.Modules.Catalog;
using Verce.Modules.Costing;
using Verce.Modules.Customers;
using Verce.Modules.Pricing;
using Verce.Modules.Production;
using Verce.Modules.Quoting;
using Verce.Modules.Settings;
using Verce.Platform.Identity;
using Verce.Platform.Numbering;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel;
using Verce.SharedKernel.Results;
using Verce.SharedKernel.Time;

namespace Verce.Api.Quoting;

/// <summary>
/// One requested line. <see cref="SourceQuoteItemId"/> (B-01 correction) is the client's ONLY way
/// to say "this is the SAME commercial line as R(n) item X" — omitted (null) means a genuinely
/// new line. It is rejected outright on <c>POST /api/quotes</c> (there is no R(n) yet). For a
/// sourced line on <c>/revise</c>, <see cref="ProductId"/>/<see cref="AdHocDescription"/> are
/// NEVER trusted as historical truth — the server ignores them and copies the source snapshot's
/// own identity/description verbatim; only <see cref="Quantity"/>, <see cref="DesiredMarginPercent"/>,
/// <see cref="ManualPriceOverride"/> and the discount fields are read from the request for a
/// sourced line (STATE-MACHINES/ADR-0020 §A.7's "explicitly operator-controlled commercial
/// inputs"). If a caller supplies a non-null <see cref="ProductId"/> for a sourced line that
/// disagrees with the source's own Product, the request is rejected
/// (<c>QUOTE_ITEM_SOURCE_PRODUCT_IMMUTABLE</c>) rather than silently changing the line's identity.
/// </summary>
public sealed record QuoteItemRequest(
    Guid? SourceQuoteItemId, Guid? ProductId, string? AdHocDescription, decimal? ManualUnitCost, decimal Quantity,
    decimal DesiredMarginPercent, decimal? ManualPriceOverride, QuoteDiscountKind DiscountKind, decimal DiscountValue);

public sealed record QuoteCreateRequest(Guid? CustomerId, Guid SalesChannelId, IReadOnlyList<QuoteItemRequest> Items, int? ValidityDaysOverride);
public sealed record QuoteReviseRequest(Guid? CustomerId, Guid SalesChannelId, IReadOnlyList<QuoteItemRequest> Items, int? ValidityDaysOverride, long QuoteVersion);
public sealed record QuoteCancelRequest(string Reason, long QuoteVersion);
public sealed record QuoteVersionedRequest(long QuoteVersion);

public sealed record QuoteItemMaterialResponse(Guid SupplyId, string SupplyCode, string SupplyName, decimal EnteredQuantity, string EnteredUnit,
    decimal NormalizedQuantityBaseUnit, string BaseUnit, decimal WastagePercent, decimal EffectiveQuantityBaseUnit, string CostSource,
    string CostPolicy, decimal UnitCostBaseUnit, decimal CostBeforeWastage, decimal WastageCost, decimal CostAfterWastage,
    decimal CurrentStockBaseUnitAtIssue, bool ExceededCurrentStockAtIssue);

public sealed record QuoteItemAdditionalCostResponse(string Description, decimal Amount);

/// <summary>B-02: every cost component S4's CostEngine returned when this line was frozen — never
/// just the scalar unit cost.</summary>
public sealed record QuoteItemCostSnapshotResponse(
    string EngineVersion, decimal MaterialCostBeforeWastage, decimal MaterialWastageCost, decimal MaterialsTotalCost,
    decimal? LaborMinutes, decimal? LaborHourlyRate, string? LaborRateSource, decimal LaborCost,
    decimal? MachineMinutes, decimal? MachineHourlyRate, decimal MachineCost, decimal AdditionalDirectCostsTotal,
    decimal TotalEstimatedCost, int OutputQuantity, decimal EstimatedUnitCost,
    IReadOnlyList<QuoteItemMaterialResponse> Materials, IReadOnlyList<QuoteItemAdditionalCostResponse> AdditionalCosts);

public sealed record QuoteItemResponse(Guid Id, int LineNumber, Guid? SourceQuoteItemId, Guid? ProductId, string ProductNameSnapshot, string? Description,
    decimal Quantity, decimal UnitTotalCost, string CostEngineVersion, decimal DesiredMarginPercent,
    Guid SalesChannelId, Guid? FeeRuleVersionId, decimal CommissionPercent, QuoteFixedFeeApplication FixedFeeApplication,
    decimal RawFixedFee, decimal AllocatedOrderFee, decimal FixedFeePerUnit, string RoundingPolicyApplied,
    decimal SuggestedUnitPrice, decimal? ManualPriceOverride, bool PriceOverridden, decimal UnitPrice,
    QuoteDiscountKind DiscountKind, decimal DiscountValue, decimal DiscountAmount, decimal NetUnitPrice,
    decimal LineTotalAmount, decimal LineCostAmount, decimal LineFeeAmount, decimal ExpectedProfitAmount, decimal EffectiveMarginPercent,
    QuoteItemCostSnapshotResponse CostSnapshot);

public sealed record QuoteRevisionResponse(Guid Id, int RevisionIndex, string RevisionSuffix, string DisplayNumber,
    QuoteRevisionStatus Status, Guid SalesChannelId, DateTimeOffset IssuedAt, DateOnly ValidUntil,
    Guid? SupersededByRevisionId, Guid? SourceRevisionId, DateTimeOffset? ApprovedAt, Guid? ApprovedBy,
    decimal SubtotalAmount, decimal DiscountAmount, decimal TotalAmount, decimal TotalCostAmount,
    decimal ExpectedProfitAmount, decimal EffectiveMarginPercent, IReadOnlyList<QuoteItemResponse> Items);

public sealed record QuoteResponse(Guid Id, string Number, Guid? CustomerId, long Version,
    QuoteRevisionResponse CurrentRevision, string CommercialOutcome, bool HasEverWon);

public sealed record QuoteListItemResponse(Guid Id, string Number, Guid? CustomerId, QuoteRevisionStatus CurrentStatus,
    decimal CurrentTotalAmount, string CommercialOutcome, long Version);
public sealed record QuoteListResponse(IReadOnlyList<QuoteListItemResponse> Items, int Page, int PageSize, int Total);

public sealed record ConversionRatePeriodResponse(DateOnly PeriodStart, DateOnly PeriodEndExclusive, int Won, int Lost, int Decided, decimal? ConversionRate);

/// <summary>Fully-resolved line, independent of whether it came from a fresh Product/CostEngine
/// lookup (a new line) or was cloned verbatim from R(n) (a sourced line) — the shared
/// allocation/pricing step below treats both identically from here on.</summary>
internal sealed record ResolvedLine(Guid? SourceQuoteItemId, Guid? ProductId, Guid? ProductRecipeId, string Name, string? Description,
    QuoteItemCostSnapshotInput CostSnapshot, QuoteItemRequest Request);

/// <summary>
/// S6 Quoting composition root (ADR-0020). Every cross-module read (Customer snapshot, Product
/// cost via S4's <see cref="ProductCostCalculator"/>, Sales-channel fee context via S5's
/// <see cref="PricingEngine"/>) happens HERE, never inside the Quoting module itself
/// (CLAUDE.md rule 11) — exactly like <see cref="ProductEndpoints"/> resolves Supply lookups
/// before calling into Catalog. The <c>PRODUCTION_ORDER_IN_PROGRESS</c> approval guard
/// (STATE-MACHINES §1.5) is likewise resolved here, reading Production's own table directly
/// (Verce.Api references every module — ADR-0001 §3), since <c>Verce.Modules.Quoting</c> must
/// never reference <c>Verce.Modules.Production</c>.
///
/// B-01 correction: <c>/revise</c> is a REAL clone-candidate construction, never a "re-resolve
/// everything the client happened to resend" endpoint. See <see cref="ResolveReviseItemsAsync"/>.
/// </summary>
public static class QuotingEndpoints
{
    public static IEndpointRouteBuilder MapQuotingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/quotes");

        group.MapGet("", async (VerceDbContext db, IClock clock, CancellationToken ct, int page = 0, int pageSize = 0) =>
        {
            page = Math.Max(page, 1);
            pageSize = Math.Clamp(pageSize == 0 ? 25 : pageSize, 1, 100);
            var quotes = await db.Set<Quote>().AsNoTracking()
                .Include(x => x.Revisions).ThenInclude(x => x.History)
                .OrderByDescending(x => x.NumberDate).ThenByDescending(x => x.NumberSequence)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .ToListAsync(ct);
            var total = await db.Set<Quote>().CountAsync(ct);
            var items = quotes.Select(q =>
            {
                var current = q.CurrentRevision;
                var history = q.Revisions.SelectMany(r => r.History.Select(h => new QuoteHistoryEvent(h.ToStatus.ToString(), h.ChangedAt)));
                var firstApprovalAt = QuoteOutcomeCalculator.FirstApprovalAt(history);
                var outcome = QuoteOutcomeCalculator.CurrentCommercialOutcome(firstApprovalAt,
                    current.Status is QuoteRevisionStatus.CANCELED or QuoteRevisionStatus.EXPIRED);
                return new QuoteListItemResponse(q.Id, q.Number, q.CustomerId, current.Status, current.TotalAmount, outcome, q.Version);
            }).ToList();
            return Results.Ok(new QuoteListResponse(items, page, pageSize, total));
        }).RequireAuthorization(Permissions.QuotingRead).Produces<QuoteListResponse>();

        group.MapGet("/{id:guid}", async (Guid id, VerceDbContext db, CancellationToken ct) =>
        {
            var quote = await LoadQuoteAsync(db, id, ct);
            return quote is null ? Results.NotFound() : Results.Ok(ToResponse(quote));
        }).RequireAuthorization(Permissions.QuotingRead).Produces<QuoteResponse>().Produces(StatusCodes.Status404NotFound);

        group.MapGet("/conversion-rate", async (DateOnly from, DateOnly to, VerceDbContext db, IClock clock, CancellationToken ct) =>
        {
            if (to <= from) return Problem("QUOTE_PERIOD_RANGE_INVALID", StatusCodes.Status422UnprocessableEntity);
            var quotes = await db.Set<Quote>().AsNoTracking().Include(x => x.Revisions).ThenInclude(x => x.History).ToListAsync(ct);
            var outcomes = quotes.Select(q =>
            {
                var history = q.Revisions.SelectMany(r => r.History.Select(h => new QuoteHistoryEvent(h.ToStatus.ToString(), h.ChangedAt))).ToArray();
                var firstApprovalAt = QuoteOutcomeCalculator.FirstApprovalAt(history);
                var eligibleLosses = QuoteOutcomeCalculator.EligiblePreWinLossTransitions(history, firstApprovalAt);
                return QuoteOutcomeCalculator.PeriodOutcome(from, to, clock.OrganizationTimeZoneId, firstApprovalAt, eligibleLosses);
            });
            var result = QuoteOutcomeCalculator.ConversionRate(outcomes);
            return Results.Ok(new ConversionRatePeriodResponse(from, to, result.Won, result.Lost, result.Decided, result.ConversionRate));
        }).RequireAuthorization(Permissions.QuotingRead).Produces<ConversionRatePeriodResponse>();

        group.MapPost("", async (QuoteCreateRequest request, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users,
            AmbientOperationContext ambient, IUnitOfWork uow, VerceDbContext readDb, ICostingInventoryReader inventory,
            AppSettingValueReader settings, IClock clock, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
            var actor = await users.GetUserAsync(http.User);
            if (actor is null) return Results.Unauthorized();
            ambient.SetActor(actor.Id, actor.DisplayName);

            if (request.Items.Any(i => i.SourceQuoteItemId is not null))
                return Problem("QUOTE_ITEM_SOURCE_NOT_ALLOWED_ON_CREATE", StatusCodes.Status422UnprocessableEntity);

            var organizationToday = clock.OrganizationToday();
            CustomerSnapshotInput customerSnapshot;
            try { customerSnapshot = await ResolveCustomerSnapshotAsync(request.CustomerId, readDb, ct); }
            catch (ArgumentException ex) { return Problem(ex.Message, StatusCodes.Status404NotFound); }

            IReadOnlyList<QuoteItemSnapshot> itemSnapshots;
            try
            {
                var resolvedLines = new List<ResolvedLine>(request.Items.Count);
                foreach (var item in request.Items) resolvedLines.Add(await ResolveNewLineAsync(item, readDb, inventory, settings, ct));
                itemSnapshots = await BuildSnapshotsAsync(request.SalesChannelId, resolvedLines, readDb, settings, organizationToday, null, ct);
            }
            catch (ArgumentException ex) { return Problem(ex.Message, StatusCodeFor(ex.Message)); }

            var validityDays = await ResolveValidityDaysAsync(request.ValidityDaysOverride, settings, ct);

            Quote? created = null;
            try
            {
                await uow.ExecuteAsync(async (db, token) =>
                {
                    var sequence = await SequentialNumberAllocator.AllocateAsync(db, SequentialNumberAllocator.QuoteSeries, organizationToday, token);
                    created = new Quote(sequence, organizationToday, customerSnapshot, request.SalesChannelId, itemSnapshots,
                        validityDays, actor.Id, ambient.CorrelationId, clock.UtcNow);
                    db.Add(created);
                }, ct);
            }
            catch (ArgumentException ex) { return Problem(ex.Message, StatusCodeFor(ex.Message)); }

            var loaded = await LoadQuoteAsync(readDb, created!.Id, ct);
            return Results.Created($"/api/quotes/{created.Id}", ToResponse(loaded!));
        }).RequireAuthorization(Permissions.QuotingManage).Produces<QuoteResponse>(StatusCodes.Status201Created);

        group.MapPost("/{id:guid}/revise", async (Guid id, QuoteReviseRequest request, HttpContext http, IAntiforgery antiforgery,
            UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, VerceDbContext readDb,
            ICostingInventoryReader inventory, AppSettingValueReader settings, IClock clock, CancellationToken ct) =>
        {
            // B-01: TRUE clone-candidate construction.
            //   1. Load Quote + CURRENT persisted revision + full item snapshots (below).
            //   2. Version is validated inside the transaction (unchanged).
            //   3-6. ResolveReviseItemsAsync deep-copies each sourced line's snapshot verbatim
            //        and applies ONLY the explicitly-editable fields from the request.
            //   7. Current external data (Product/CostEngine) is resolved ONLY for a line with
            //      no SourceQuoteItemId — a genuinely new line.
            //   8-9. The shared allocation/pricing step recomputes dependent fields for the
            //        whole fee group (ADR-0020 §C.2: S6 has exactly one group per revision).
            //   10-12. Quote.ConstructNextRevision persists R(n+1) and never mutates R(n).
            // NEVER gated on production-order state (BLOCKING-01 — that guard exists only on
            // /approve below).
            if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
            var actor = await users.GetUserAsync(http.User);
            if (actor is null) return Results.Unauthorized();
            ambient.SetActor(actor.Id, actor.DisplayName);

            var organizationToday = clock.OrganizationToday();
            CustomerSnapshotInput customerSnapshot;
            try { customerSnapshot = await ResolveCustomerSnapshotAsync(request.CustomerId, readDb, ct); }
            catch (ArgumentException ex) { return Problem(ex.Message, StatusCodes.Status404NotFound); }

            var currentQuote = await readDb.Set<Quote>().AsNoTracking()
                .Include(x => x.Revisions).ThenInclude(x => x.Items).ThenInclude(x => x.CostSnapshot)
                .Include(x => x.Revisions).ThenInclude(x => x.Items).ThenInclude(x => x.Materials)
                .Include(x => x.Revisions).ThenInclude(x => x.Items).ThenInclude(x => x.AdditionalCosts)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (currentQuote is null) return Results.NotFound();
            var currentItems = currentQuote.CurrentRevision.Items;

            IReadOnlyList<QuoteItemSnapshot> itemSnapshots;
            try
            {
                var resolvedLines = await ResolveReviseItemsAsync(currentItems, request.Items, readDb, inventory, settings, ct);
                itemSnapshots = await BuildSnapshotsAsync(request.SalesChannelId, resolvedLines, readDb, settings, organizationToday, currentQuote.CurrentRevision, ct);
            }
            catch (ArgumentException ex) { return Problem(ex.Message, StatusCodeFor(ex.Message)); }

            var validityDays = await ResolveValidityDaysAsync(request.ValidityDaysOverride, settings, ct);

            try
            {
                await uow.ExecuteAsync(async (db, token) =>
                {
                    var quote = await db.Set<Quote>().Include(x => x.Revisions).ThenInclude(x => x.Items)
                        .Include(x => x.Revisions).ThenInclude(x => x.History)
                        .SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException();
                    if (quote.Version != request.QuoteVersion) throw new DbUpdateConcurrencyException();
                    quote.ConstructNextRevision(customerSnapshot, request.SalesChannelId, itemSnapshots, validityDays,
                        organizationToday, actor.Id, ambient.CorrelationId, clock.UtcNow);
                }, ct);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException) { return Problem("CONCURRENCY_CONFLICT", StatusCodes.Status409Conflict); }
            catch (ArgumentException ex) { return Problem(ex.Message, StatusCodeFor(ex.Message)); }

            var updated = await LoadQuoteAsync(readDb, id, ct);
            return Results.Ok(ToResponse(updated!));
        }).RequireAuthorization(Permissions.QuotingManage).Produces<QuoteResponse>()
            .Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/send", (Guid id, QuoteVersionedRequest request, HttpContext http, IAntiforgery antiforgery,
            UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, VerceDbContext readDb, IClock clock, CancellationToken ct) =>
            TransitionAsync(id, request.QuoteVersion, http, antiforgery, users, ambient, uow, readDb, clock, ct,
                (quote, actorId, correlationId, now) => quote.Send(actorId, correlationId, now)))
            .RequireAuthorization(Permissions.QuotingManage).Produces<QuoteResponse>();

        group.MapPost("/{id:guid}/negotiate", (Guid id, QuoteVersionedRequest request, HttpContext http, IAntiforgery antiforgery,
            UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, VerceDbContext readDb, IClock clock, CancellationToken ct) =>
            TransitionAsync(id, request.QuoteVersion, http, antiforgery, users, ambient, uow, readDb, clock, ct,
                (quote, actorId, correlationId, now) => quote.MarkNegotiating(actorId, correlationId, now)))
            .RequireAuthorization(Permissions.QuotingManage).Produces<QuoteResponse>();

        group.MapPost("/{id:guid}/cancel", (Guid id, QuoteCancelRequest request, HttpContext http, IAntiforgery antiforgery,
            UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, VerceDbContext readDb, IClock clock, CancellationToken ct) =>
            TransitionAsync(id, request.QuoteVersion, http, antiforgery, users, ambient, uow, readDb, clock, ct,
                (quote, actorId, correlationId, now) => quote.Cancel(request.Reason, actorId, correlationId, now)))
            .RequireAuthorization(Permissions.QuotingManage).Produces<QuoteResponse>();

        group.MapPost("/{id:guid}/approve", async (Guid id, QuoteVersionedRequest request, HttpContext http, IAntiforgery antiforgery,
            UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, VerceDbContext readDb, IClock clock,
            AppSettingValueReader settingsForApproval, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
            var actor = await users.GetUserAsync(http.User);
            if (actor is null) return Results.Unauthorized();
            ambient.SetActor(actor.Id, actor.DisplayName);

            // STATE-MACHINES §1.5's PRODUCTION_ORDER_IN_PROGRESS guard: cross-module, so it is
            // resolved HERE (composition root), never inside Quote.Approve (ADR-0001 §3). A
            // previous order in IN_PRODUCTION/READY/SHIPPED rejects approval outright — checked
            // BEFORE the transaction even starts, so a doomed approval never touches the revision.
            // This is an OPTIMIZATION only: H-05's authoritative, race-proof recheck lives inside
            // the transaction (CreateProductionOrderOnQuoteApproved), and maps to the SAME stable
            // 409 contract via StatusCodeFor below if a race slips past this pre-check.
            var quoteForGuard = await readDb.Set<global::Verce.Modules.Quoting.Quote>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (quoteForGuard is null) return Results.NotFound();
            var blockingOrder = await readDb.Set<ProductionOrder>().AsNoTracking()
                .Where(o => o.QuoteId == id && o.QuoteRevisionId != quoteForGuard.CurrentRevisionId)
                .FirstOrDefaultAsync(o => o.Status == ProductionOrderStatus.IN_PRODUCTION
                    || o.Status == ProductionOrderStatus.READY || o.Status == ProductionOrderStatus.SHIPPED, ct);
            if (blockingOrder is not null) return Problem("PRODUCTION_ORDER_IN_PROGRESS", StatusCodes.Status409Conflict);

            try
            {
                var organizationToday = clock.OrganizationToday();
                var allowDirectApproval = await settingsForApproval.GetBoolAsync("quote.allow_direct_approval", ct);
                await uow.ExecuteAsync(async (db, token) =>
                {
                    var quote = await db.Set<global::Verce.Modules.Quoting.Quote>().Include(x => x.Revisions).ThenInclude(x => x.Items)
                        .Include(x => x.Revisions).ThenInclude(x => x.History)
                        .SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException();
                    if (quote.Version != request.QuoteVersion) throw new DbUpdateConcurrencyException();
                    quote.Approve(actor.Id, ambient.CorrelationId, clock.UtcNow, organizationToday, allowDirectApproval);
                }, ct);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException) { return Problem("CONCURRENCY_CONFLICT", StatusCodes.Status409Conflict); }
            catch (ArgumentException ex) { return Problem(ex.Message, StatusCodeFor(ex.Message)); }

            var updated = await LoadQuoteAsync(readDb, id, ct);
            return Results.Ok(ToResponse(updated!));
        }).RequireAuthorization(Permissions.QuotingManage).Produces<QuoteResponse>()
            .Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> TransitionAsync(Guid id, long version, HttpContext http, IAntiforgery antiforgery,
        UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, VerceDbContext readDb, IClock clock,
        CancellationToken ct, Action<global::Verce.Modules.Quoting.Quote, Guid?, Guid, DateTimeOffset> transition)
    {
        if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
        var actor = await users.GetUserAsync(http.User);
        if (actor is null) return Results.Unauthorized();
        ambient.SetActor(actor.Id, actor.DisplayName);
        try
        {
            await uow.ExecuteAsync(async (db, token) =>
            {
                var quote = await db.Set<global::Verce.Modules.Quoting.Quote>().Include(x => x.Revisions).ThenInclude(x => x.Items)
                    .Include(x => x.Revisions).ThenInclude(x => x.History)
                    .SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException();
                if (quote.Version != version) throw new DbUpdateConcurrencyException();
                transition(quote, actor.Id, ambient.CorrelationId, clock.UtcNow);
                await Task.CompletedTask;
            }, ct);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (DbUpdateConcurrencyException) { return Problem("CONCURRENCY_CONFLICT", StatusCodes.Status409Conflict); }
        catch (ArgumentException ex) { return Problem(ex.Message, StatusCodeFor(ex.Message)); }

        var updated = await LoadQuoteAsync(readDb, id, ct);
        return Results.Ok(ToResponse(updated!));
    }

    /// <summary>STATE-MACHINES §6's stable code -&gt; HTTP status table, extended for the S6
    /// codes it does not yet list: a state/transition guard (the revision or a previous
    /// production order is in the wrong state for what was asked) is 409 Conflict; a content or
    /// resolution failure (missing input, an unresolved reference) is 422 or 404.</summary>
    private static int StatusCodeFor(string errorCode) => errorCode switch
    {
        "QUOTE_REVISION_NOT_CURRENT" or "QUOTE_REVISION_ALREADY_DECIDED" or "QUOTE_REVISION_EXPIRED"
            or "QUOTE_INVALID_TRANSITION" or "QUOTE_DIRECT_APPROVAL_NOT_ALLOWED" or "PRODUCTION_ORDER_IN_PROGRESS" => StatusCodes.Status409Conflict,
        // Matches PricingEndpoints' own precedent for the identical code: an unresolved
        // FeeRuleVersion is a content/configuration problem for THIS request, not a missing
        // resource in the REST sense — 422, not 404.
        "FEE_RULE_NOT_FOUND" => StatusCodes.Status422UnprocessableEntity,
        _ when errorCode.EndsWith("_NOT_FOUND", StringComparison.Ordinal) => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status422UnprocessableEntity,
    };

    private static async Task<CustomerSnapshotInput> ResolveCustomerSnapshotAsync(Guid? customerId, VerceDbContext db, CancellationToken ct)
    {
        if (customerId is null) return new CustomerSnapshotInput(null, null, null, null, null);
        var customer = await db.Set<Customer>().AsNoTracking().Include(x => x.Addresses).SingleOrDefaultAsync(x => x.Id == customerId, ct)
            ?? throw new ArgumentException("CUSTOMER_NOT_FOUND");
        var contacts = JsonSerializer.Serialize(new { customer.Email, customer.Phone });
        var addresses = JsonSerializer.Serialize(customer.Addresses.Select(a => new
        {
            a.Label,
            a.ZipCode,
            a.Street,
            a.Number,
            a.Complement,
            a.District,
            a.City,
            a.State,
            a.Country,
            a.IsPrimary,
            a.IsDefaultShipping,
        }));
        return new CustomerSnapshotInput(customer.Id, customer.Name, customer.Document, contacts, addresses);
    }

    /// <summary>B-01/B-02: a genuinely NEW line — the only case that resolves CURRENT Product/
    /// CostEngine data. Freezes the FULL cost breakdown (materials, labor, machine, additional
    /// direct costs), never just the scalar unit cost.</summary>
    private static async Task<ResolvedLine> ResolveNewLineAsync(QuoteItemRequest request, VerceDbContext db,
        ICostingInventoryReader inventory, AppSettingValueReader settings, CancellationToken ct)
    {
        if (request.Quantity <= 0) throw new ArgumentException("QUOTE_ITEM_QUANTITY_INVALID");

        if (request.ProductId is { } productId)
        {
            var product = await ProductEndpoints.LoadProductAsync(db, productId, ct) ?? throw new ArgumentException("PRODUCT_NOT_FOUND");
            CostCalculationResult cost;
            // Same stable codes ProductEndpoints/PricingEndpoints already use for this exact
            // failure (repository convention: one error code, one meaning, everywhere) — never
            // a second, differently-spelled code for the same underlying condition.
            try { cost = await ProductCostCalculator.CalculateAsync(product, inventory, settings, ct); }
            catch (ArgumentException ex) when (ex.Message is "RECIPE_EMPTY" or "RECIPE_MACHINE_RATE_REQUIRED") { throw new ArgumentException("RECIPE_INVALID"); }
            catch (ArgumentException ex) when (ex.Message is "COST_BASIS_UNAVAILABLE" or "SUPPLY_NOT_FOUND") { throw new ArgumentException(ex.Message); }
            // B-16: Product DESCRIPTION snapshot is Product.Description, never the client's
            // ad-hoc text — those are two distinct concepts (mission §16).
            return new ResolvedLine(null, product.Id, product.Recipe.Id, product.Name, product.Description, ToCostSnapshotInput(cost), request);
        }

        if (request.ManualUnitCost is not { } manualCost || manualCost < 0) throw new ArgumentException("QUOTE_ITEM_MANUAL_COST_REQUIRED");
        var adHocName = string.IsNullOrWhiteSpace(request.AdHocDescription) ? "Item avulso" : request.AdHocDescription!;
        return new ResolvedLine(null, null, null, adHocName, request.AdHocDescription, ManualCostSnapshotInput(manualCost), request);
    }

    /// <summary>
    /// B-01: the real clone-candidate step. Every request line with a <see cref="QuoteItemRequest.SourceQuoteItemId"/>
    /// is matched to its R(n) source, its Product/description/cost-breakdown snapshot copied
    /// VERBATIM (never re-resolved against current Product/Supply/Recipe state — mission §7/§11),
    /// and only the explicitly editable commercial inputs are taken from the request. A line with
    /// no source identity is resolved as brand new (<see cref="ResolveNewLineAsync"/>). A source
    /// item omitted from the request is simply not carried into R(n+1) — removed.
    /// </summary>
    private static async Task<IReadOnlyList<ResolvedLine>> ResolveReviseItemsAsync(IReadOnlyList<QuoteItem> currentItems,
        IReadOnlyList<QuoteItemRequest> requests, VerceDbContext db, ICostingInventoryReader inventory, AppSettingValueReader settings, CancellationToken ct)
    {
        var currentById = currentItems.ToDictionary(i => i.Id);
        var usedSourceIds = new HashSet<Guid>();
        var resolved = new List<ResolvedLine>(requests.Count);

        foreach (var request in requests)
        {
            if (request.SourceQuoteItemId is { } sourceId)
            {
                if (!usedSourceIds.Add(sourceId)) throw new ArgumentException("QUOTE_ITEM_SOURCE_DUPLICATE");
                if (!currentById.TryGetValue(sourceId, out var source)) throw new ArgumentException("QUOTE_ITEM_SOURCE_NOT_FOUND");
                // §6: a sourced line's Product/ad-hoc identity is immutable. Changing Product
                // means removing this source line and submitting a new one without SourceQuoteItemId.
                if (request.ProductId is { } requestedProductId && requestedProductId != source.ProductId)
                    throw new ArgumentException("QUOTE_ITEM_SOURCE_PRODUCT_IMMUTABLE");

                // Verbatim: identity, description, and the FULL cost breakdown — never re-resolved.
                resolved.Add(new ResolvedLine(source.Id, source.ProductId, source.ProductRecipeId,
                    source.ProductNameSnapshot, source.Description, FromExistingCostSnapshot(source), request));
            }
            else
            {
                resolved.Add(await ResolveNewLineAsync(request, db, inventory, settings, ct));
            }
        }

        return resolved;
    }

    /// <summary>ADR-0020 §C.2/§A.7: S6 has exactly one PER_ORDER fee group per revision, so
    /// allocating/pricing "the whole group" and "the whole candidate item set" are the same
    /// operation — this is the shared tail of both create and revise, after each line's identity
    /// and cost breakdown has already been settled (verbatim or freshly resolved).
    ///
    /// F-01 correction: the fee CONTEXT (which <see cref="FeeRuleVersion"/> prices the group) is
    /// itself part of the immutable per-revision snapshot, exactly like cost — never re-resolved
    /// merely because the organization date advanced or a newer version became effective.
    /// <paramref name="currentRevisionForFeeInheritance"/> is null on create (nothing to inherit)
    /// and the CURRENT revision on revise. When its <c>SalesChannelId</c> matches the request and
    /// it has at least one priced item, the group inherits that exact frozen
    /// <c>FeeRuleVersionId</c> verbatim (re-read by id — a <see cref="FeeRuleVersion"/> row is
    /// never mutated after creation, only superseded by a new one via <c>AddVersion</c>/
    /// <c>CloseOpenVersion</c>, so reading it back by id always returns its original terms). An
    /// explicit channel change (a different <c>SalesChannelId</c> than the current revision, or
    /// create, where there is no "current" at all) resolves the effective version for that
    /// channel fresh, exactly as before.</summary>
    private static async Task<IReadOnlyList<QuoteItemSnapshot>> BuildSnapshotsAsync(Guid salesChannelId, IReadOnlyList<ResolvedLine> resolved,
        VerceDbContext db, AppSettingValueReader settings, DateOnly organizationToday, QuoteRevision? currentRevisionForFeeInheritance, CancellationToken ct)
    {
        if (resolved.Count == 0) return [];

        var channel = await db.Set<SalesChannel>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == salesChannelId, ct)
            ?? throw new ArgumentException("QUOTE_SALES_CHANNEL_NOT_FOUND");
        if (!channel.Active) throw new ArgumentException("QUOTE_SALES_CHANNEL_INACTIVE");

        var inheritedFeeRuleVersionId = currentRevisionForFeeInheritance is { Items.Count: > 0 } current
            && current.SalesChannelId == salesChannelId
            ? current.Items[0].FeeRuleVersionId
            : null;

        FeeRuleVersion feeVersion;
        if (inheritedFeeRuleVersionId is { } inheritedId)
        {
            // Same sales-channel context as R(n): inherit its frozen fee terms verbatim, never
            // re-resolved against "today" or whichever version is now effective.
            feeVersion = await db.Set<FeeRuleVersion>().AsNoTracking().SingleAsync(x => x.Id == inheritedId, ct);
        }
        else
        {
            // Create, or an explicit sales-channel context change on revise: resolve the
            // currently-effective version for THIS (possibly new) channel, exactly as before.
            var feeRule = await db.Set<FeeRule>().AsNoTracking().Include(x => x.Versions)
                .SingleOrDefaultAsync(x => x.SalesChannelId == salesChannelId && x.Active, ct);
            feeVersion = feeRule?.ResolveVersionAt(organizationToday) ?? throw new ArgumentException("FEE_RULE_NOT_FOUND");
        }

        var roundingPolicy = await GetRoundingPolicyAsync(settings, ct);
        var marginWarningDenominator = await settings.GetDecimalAsync("pricing.margin_warning_denominator", ct);
        var fixedFeeApplication = feeVersion.FixedFeeApplication == FixedFeeApplication.PerOrder ? QuoteFixedFeeApplication.PerOrder : QuoteFixedFeeApplication.PerUnit;

        // ADR-0020 §C.2: S6 supports exactly one fee group per revision — every item resolves to
        // the SAME FeeRuleVersion, so the allocation runs once, over the whole set. Cost is
        // FROZEN per line (verbatim for a sourced line, freshly resolved for a new one) —
        // recomputing the allocation on a quantity change never refreshes cost (mission §8).
        var allocationInputs = resolved.Select((r, i) => new AllocationLineInput(i + 1, Rounding.ToInternal(r.CostSnapshot.EstimatedUnitCost * r.Request.Quantity))).ToArray();
        var allocations = fixedFeeApplication == QuoteFixedFeeApplication.PerOrder
            ? PerOrderFeeAllocator.Allocate(feeVersion.FixedFee, allocationInputs)
            : allocationInputs.Select(a => new AllocationLineResult(a.LineNumber, 0m)).ToArray();

        var snapshots = new List<QuoteItemSnapshot>(resolved.Count);
        for (var i = 0; i < resolved.Count; i++)
        {
            var line = resolved[i];
            var allocatedOrderFee = allocations[i].AllocatedAmount;
            var fixedFeePerUnit = fixedFeeApplication == QuoteFixedFeeApplication.PerUnit
                ? feeVersion.FixedFee
                : Rounding.ToInternal(allocatedOrderFee / line.Request.Quantity);

            var pricingResult = PricingEngine.Calculate(new PricingCalculationInput(
                line.CostSnapshot.EstimatedUnitCost, feeVersion.CommissionPercent, fixedFeePerUnit, line.Request.DesiredMarginPercent,
                roundingPolicy, marginWarningDenominator, feeVersion.MinimumFee, feeVersion.MaximumFee));
            if (pricingResult.IsFailure) throw new ArgumentException(pricingResult.ErrorCode);

            snapshots.Add(new QuoteItemSnapshot(
                line.SourceQuoteItemId, line.ProductId, line.ProductRecipeId, line.Name, line.Description, line.Request.Quantity,
                line.CostSnapshot, line.Request.DesiredMarginPercent,
                channel.Id, feeVersion.Id, feeVersion.CommissionPercent, fixedFeeApplication, feeVersion.FixedFee,
                allocatedOrderFee, roundingPolicy.ToString(), pricingResult.Value.SuggestedPrice, pricingResult.Value.CommissionAmount,
                pricingResult.Value.FeeClampApplied, line.Request.ManualPriceOverride, line.Request.DiscountKind, line.Request.DiscountValue));
        }
        return snapshots;
    }

    private static QuoteItemCostSnapshotInput ToCostSnapshotInput(CostCalculationResult cost) => new(
        cost.EngineVersion,
        cost.Totals.MaterialCostBeforeWastage, cost.Totals.MaterialWastageCost, cost.Totals.MaterialsTotalCost,
        cost.Labor?.Minutes, cost.Labor?.HourlyRate, cost.Labor?.RateSource.ToString(), cost.Totals.LaborCost,
        cost.Machine?.Minutes, cost.Machine?.HourlyRate, cost.Totals.MachineCost,
        cost.Totals.AdditionalDirectCosts, cost.Totals.TotalEstimatedCost, cost.Totals.OutputQuantity, cost.Totals.EstimatedUnitCost,
        cost.Materials.Select(m => new QuoteItemMaterialSnapshotInput(m.SupplyId, m.SupplyCode, m.SupplyName, m.EnteredQuantity, m.EnteredUnit,
            m.NormalizedQuantityBaseUnit, m.BaseUnit, m.WastagePercent, m.EffectiveQuantityBaseUnit, m.CostSource.ToString(), m.CostPolicy,
            m.UnitCostBaseUnit, m.CostBeforeWastage, m.WastageCost, m.CostAfterWastage, m.CurrentStockBaseUnit, m.ExceedsCurrentStock)).ToArray(),
        cost.AdditionalDirectCosts.Select(a => new QuoteItemAdditionalCostSnapshotInput(a.Description, a.Amount)).ToArray());

    private static QuoteItemCostSnapshotInput ManualCostSnapshotInput(decimal manualCost) => new(
        "MANUAL", 0m, 0m, 0m, null, null, null, 0m, null, null, 0m, 0m, manualCost, 1, manualCost, [], []);

    /// <summary>B-01/B-02: reconstructs the exact input shape from an already-persisted R(n)
    /// line's own frozen snapshot — the verbatim-copy path for a sourced line on <c>/revise</c>.</summary>
    private static QuoteItemCostSnapshotInput FromExistingCostSnapshot(QuoteItem source) => new(
        source.CostSnapshot.EngineVersion, source.CostSnapshot.MaterialCostBeforeWastage, source.CostSnapshot.MaterialWastageCost,
        source.CostSnapshot.MaterialsTotalCost, source.CostSnapshot.LaborMinutes, source.CostSnapshot.LaborHourlyRate,
        source.CostSnapshot.LaborRateSource, source.CostSnapshot.LaborCost, source.CostSnapshot.MachineMinutes,
        source.CostSnapshot.MachineHourlyRate, source.CostSnapshot.MachineCost, source.CostSnapshot.AdditionalDirectCostsTotal,
        source.CostSnapshot.TotalEstimatedCost, source.CostSnapshot.OutputQuantity, source.CostSnapshot.EstimatedUnitCost,
        source.Materials.OrderBy(m => m.LineNumber).Select(m => new QuoteItemMaterialSnapshotInput(m.SupplyId, m.SupplyCodeSnapshot, m.SupplyNameSnapshot,
            m.EnteredQuantity, m.EnteredUnit, m.NormalizedQuantityBaseUnit, m.BaseUnit, m.WastagePercent, m.EffectiveQuantityBaseUnit, m.CostSource,
            m.CostPolicy, m.UnitCostBaseUnit, m.CostBeforeWastage, m.WastageCost, m.CostAfterWastage, m.CurrentStockBaseUnitAtIssue, m.ExceededCurrentStockAtIssue)).ToArray(),
        source.AdditionalCosts.OrderBy(a => a.LineNumber).Select(a => new QuoteItemAdditionalCostSnapshotInput(a.Description, a.Amount)).ToArray());

    /// <summary><c>quote.default_validity_days</c> (S2-seeded, default 15) — ADR-0004: "stored
    /// per revision so a settings change never moves an existing deadline."</summary>
    private static async Task<int> ResolveValidityDaysAsync(int? overrideDays, AppSettingValueReader settings, CancellationToken ct)
    {
        if (overrideDays is { } days) return days;
        return await settings.GetIntAsync("quote.default_validity_days", ct);
    }

    private static async Task<PriceRoundingPolicy> GetRoundingPolicyAsync(AppSettingValueReader settings, CancellationToken ct)
    {
        var raw = await settings.GetStringAsync("pricing.price_rounding_policy", ct);
        return Enum.TryParse<PriceRoundingPolicy>(raw, out var policy) ? policy : throw new InvalidOperationException("SETTING_VALUE_INVALID");
    }

    internal static Task<global::Verce.Modules.Quoting.Quote?> LoadQuoteAsync(VerceDbContext db, Guid id, CancellationToken ct) =>
        db.Set<global::Verce.Modules.Quoting.Quote>().AsNoTracking()
            .Include(x => x.Revisions).ThenInclude(x => x.Items).ThenInclude(x => x.CostSnapshot)
            .Include(x => x.Revisions).ThenInclude(x => x.Items).ThenInclude(x => x.Materials)
            .Include(x => x.Revisions).ThenInclude(x => x.Items).ThenInclude(x => x.AdditionalCosts)
            .Include(x => x.Revisions).ThenInclude(x => x.History)
            .SingleOrDefaultAsync(x => x.Id == id, ct)!;

    internal static QuoteResponse ToResponse(global::Verce.Modules.Quoting.Quote quote)
    {
        var current = quote.CurrentRevision;
        var history = quote.Revisions.SelectMany(r => r.History.Select(h => new QuoteHistoryEvent(h.ToStatus.ToString(), h.ChangedAt)));
        var firstApprovalAt = QuoteOutcomeCalculator.FirstApprovalAt(history);
        var outcome = QuoteOutcomeCalculator.CurrentCommercialOutcome(firstApprovalAt,
            current.Status is QuoteRevisionStatus.CANCELED or QuoteRevisionStatus.EXPIRED);

        var items = current.Items.OrderBy(i => i.LineNumber).Select(i => new QuoteItemResponse(
            i.Id, i.LineNumber, i.SourceQuoteItemId, i.ProductId, i.ProductNameSnapshot, i.Description, i.Quantity, i.UnitTotalCost, i.CostEngineVersion,
            i.DesiredMarginPercent, i.SalesChannelId, i.FeeRuleVersionId, i.CommissionPercent, i.FixedFeeApplication, i.RawFixedFee,
            i.AllocatedOrderFee, i.FixedFeePerUnit, i.RoundingPolicyApplied, i.SuggestedUnitPrice, i.ManualPriceOverride, i.PriceOverridden,
            i.UnitPrice, i.DiscountKind, i.DiscountValue, i.DiscountAmount, i.NetUnitPrice, i.LineTotalAmount, i.LineCostAmount,
            i.LineFeeAmount, i.ExpectedProfitAmount, i.EffectiveMarginPercent,
            new QuoteItemCostSnapshotResponse(i.CostSnapshot.EngineVersion, i.CostSnapshot.MaterialCostBeforeWastage, i.CostSnapshot.MaterialWastageCost,
                i.CostSnapshot.MaterialsTotalCost, i.CostSnapshot.LaborMinutes, i.CostSnapshot.LaborHourlyRate, i.CostSnapshot.LaborRateSource,
                i.CostSnapshot.LaborCost, i.CostSnapshot.MachineMinutes, i.CostSnapshot.MachineHourlyRate, i.CostSnapshot.MachineCost,
                i.CostSnapshot.AdditionalDirectCostsTotal, i.CostSnapshot.TotalEstimatedCost, i.CostSnapshot.OutputQuantity, i.CostSnapshot.EstimatedUnitCost,
                i.Materials.OrderBy(m => m.LineNumber).Select(m => new QuoteItemMaterialResponse(m.SupplyId, m.SupplyCodeSnapshot, m.SupplyNameSnapshot,
                    m.EnteredQuantity, m.EnteredUnit, m.NormalizedQuantityBaseUnit, m.BaseUnit, m.WastagePercent, m.EffectiveQuantityBaseUnit,
                    m.CostSource, m.CostPolicy, m.UnitCostBaseUnit, m.CostBeforeWastage, m.WastageCost, m.CostAfterWastage,
                    m.CurrentStockBaseUnitAtIssue, m.ExceededCurrentStockAtIssue)).ToList(),
                i.AdditionalCosts.OrderBy(a => a.LineNumber).Select(a => new QuoteItemAdditionalCostResponse(a.Description, a.Amount)).ToList())))
            .ToList();

        var revisionResponse = new QuoteRevisionResponse(current.Id, current.RevisionIndex, current.RevisionSuffix,
            quote.DisplayNumberFor(current), current.Status, current.SalesChannelId, current.IssuedAt, current.ValidUntil,
            current.SupersededByRevisionId, current.SourceRevisionId, current.ApprovedAt, current.ApprovedBy,
            current.SubtotalAmount, current.DiscountAmount, current.TotalAmount, current.TotalCostAmount,
            current.ExpectedProfitAmount, current.EffectiveMarginPercent, items);

        return new QuoteResponse(quote.Id, quote.Number, quote.CustomerId, quote.Version, revisionResponse, outcome,
            QuoteOutcomeCalculator.HasEverWon(firstApprovalAt));
    }

    private static async Task<bool> IsValidCsrf(HttpContext http, IAntiforgery antiforgery)
    {
        try { await antiforgery.ValidateRequestAsync(http); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    private static IResult BadRequest() => Results.Problem(statusCode: 400, title: "Requisição inválida.");
    private static IResult Problem(string code, int status = StatusCodes.Status400BadRequest) =>
        Results.Problem(statusCode: status, title: "Não foi possível concluir a operação.", extensions: new Dictionary<string, object?> { ["code"] = code });
}
