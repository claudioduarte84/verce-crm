using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Verce.Api.Authorization;
using Verce.Api.Catalog;
using Verce.Modules.Costing;
using Verce.Modules.Pricing;
using Verce.Modules.Settings;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Time;

namespace Verce.Api.Pricing;

public sealed record SalesChannelCreateRequest(string Code, string Name, SalesChannelKind Kind, decimal? DefaultMarginPercent, string? Notes);
public sealed record SalesChannelUpdateRequest(string Name, SalesChannelKind Kind, decimal? DefaultMarginPercent, string? Notes, long Version);
public sealed record SalesChannelResponse(Guid Id, string Code, string Name, SalesChannelKind Kind, decimal? DefaultMarginPercent, string? Notes, bool Active, long Version);

public sealed record FeeRuleVersionCreateRequest(DateOnly ValidFrom, DateOnly? ValidUntil, decimal CommissionPercent, decimal FixedFee,
    FixedFeeApplication FixedFeeApplication, decimal? MinimumFee, decimal? MaximumFee, string? Notes, bool CloseCurrentOpenVersion);
public sealed record FeeRuleVersionResponse(Guid Id, DateOnly ValidFrom, DateOnly? ValidUntil, decimal CommissionPercent, decimal FixedFee,
    FixedFeeApplication FixedFeeApplication, decimal? MinimumFee, decimal? MaximumFee, string? Notes);
public sealed record FeeRuleResponse(Guid Id, Guid SalesChannelId, string Name, bool Active, long Version, IReadOnlyList<FeeRuleVersionResponse> Versions);
public sealed record FeeRuleCreateRequest(string Name);

public sealed record PricingCalculateRequest(decimal UnitTotalCost, decimal CommissionPercent, decimal FixedFee, decimal? DesiredMargin,
    PriceRoundingPolicy? RoundingPolicy, decimal? MinimumFee, decimal? MaximumFee);
public sealed record ProductPriceRequest(Guid SalesChannelId, decimal? DesiredMarginOverride);
public sealed record PricingCalculateResponse(decimal UnitTotalCost, decimal CommissionPercent, decimal FixedFee, decimal DesiredMargin,
    decimal Denominator, decimal RawPrice, decimal SuggestedPrice, decimal CommissionAmount, string? FeeClampApplied, IReadOnlyList<string> Warnings);

/// <summary>Terra B-03: the authoritative, self-describing snapshot of ONE Product Pricing
/// decision — every identity and driver a future S6 QuoteRevision needs to freeze this exact
/// result without re-deriving which FeeRuleVersion or rounding policy produced it. Every field
/// here is resolved server-side from persisted state or a single <see cref="IClock.OrganizationToday"/>
/// call (mission §17/§34) — the client only ever supplies which Product/channel/margin-override
/// to price, never an authoritative cost or fee value. Deliberately a dedicated contract rather
/// than stretching <see cref="PricingCalculateResponse"/> (mission §18), which remains the
/// ad-hoc calculator's own response shape, unrelated to a real Product/channel resolution.</summary>
public sealed record ProductPriceResponse(
    Guid ProductId, string ProductCode, string ProductName,
    decimal UnitTotalCost,
    Guid SalesChannelId, string SalesChannelCode, SalesChannelKind SalesChannelKind, string SalesChannelName,
    decimal DesiredMargin,
    Guid FeeRuleId, Guid FeeRuleVersionId, decimal CommissionPercent, decimal FixedFee,
    DateOnly OrganizationDate,
    decimal Denominator, decimal RawPrice, PriceRoundingPolicy RoundingPolicy, decimal SuggestedPrice,
    decimal CommissionAmount, string? FeeClampApplied, IReadOnlyList<string> Warnings);

public static class PricingEndpoints
{
    public static IEndpointRouteBuilder MapPricingEndpoints(this IEndpointRouteBuilder app)
    {
        var channels = app.MapGroup("/api/pricing/channels");

        channels.MapGet("", async (VerceDbContext db, CancellationToken ct, bool includeInactive = false) =>
        {
            var query = db.Set<SalesChannel>().AsNoTracking().AsQueryable();
            if (!includeInactive) query = query.Where(x => x.Active);
            var rows = await query.OrderBy(x => x.Name).Select(x => ToResponse(x)).ToListAsync(ct);
            return Results.Ok(rows);
        }).RequireAuthorization(Permissions.PricingRead).Produces<IReadOnlyList<SalesChannelResponse>>();

        channels.MapGet("/{id:guid}", async (Guid id, VerceDbContext db, CancellationToken ct) =>
        {
            var channel = await db.Set<SalesChannel>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            return channel is null ? Results.NotFound() : Results.Ok(ToResponse(channel));
        }).RequireAuthorization(Permissions.PricingRead).Produces<SalesChannelResponse>().Produces(StatusCodes.Status404NotFound);

        channels.MapPost("", async (SalesChannelCreateRequest request, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users,
            AmbientOperationContext ambient, IUnitOfWork uow, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
            try
            {
                var actor = await users.GetUserAsync(http.User);
                if (actor is null) return Results.Unauthorized();
                ambient.SetActor(actor.Id, actor.DisplayName);
                SalesChannel? created = null;
                await uow.ExecuteAsync((db, token) =>
                {
                    created = new SalesChannel(request.Code, request.Name, request.Kind, request.DefaultMarginPercent, request.Notes);
                    db.Add(created);
                    return Task.CompletedTask;
                }, ct);
                return Results.Created($"/api/pricing/channels/{created!.Id}", ToResponse(created!));
            }
            catch (ArgumentException ex) { return Problem(ex.Message); }
            catch (DbUpdateException ex) when (IsUnique(ex)) { return Problem("SALES_CHANNEL_CODE_ALREADY_EXISTS", StatusCodes.Status409Conflict); }
        }).RequireAuthorization(Permissions.PricingManage).Produces<SalesChannelResponse>(StatusCodes.Status201Created);

        channels.MapPut("/{id:guid}", async (Guid id, SalesChannelUpdateRequest request, HttpContext http, IAntiforgery antiforgery,
            UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, VerceDbContext readDb, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
            try
            {
                var actor = await users.GetUserAsync(http.User);
                if (actor is null) return Results.Unauthorized();
                ambient.SetActor(actor.Id, actor.DisplayName);
                await uow.ExecuteAsync(async (db, token) =>
                {
                    var channel = await db.Set<SalesChannel>().SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException();
                    if (channel.Version != request.Version) throw new DbUpdateConcurrencyException();
                    channel.UpdateDetails(request.Name, request.Kind, request.DefaultMarginPercent, request.Notes);
                }, ct);
                var updated = await readDb.Set<SalesChannel>().AsNoTracking().SingleAsync(x => x.Id == id, ct);
                return Results.Ok(ToResponse(updated));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DbUpdateConcurrencyException) { return Problem("CONCURRENCY_CONFLICT", StatusCodes.Status409Conflict); }
            catch (ArgumentException ex) { return Problem(ex.Message); }
        }).RequireAuthorization(Permissions.PricingManage).Produces<SalesChannelResponse>().Produces(StatusCodes.Status404NotFound);

        channels.MapPost("/{id:guid}/activate", (Guid id, long version, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users,
            AmbientOperationContext ambient, IUnitOfWork uow, CancellationToken ct) => SetChannelActiveAsync(id, version, true, http, antiforgery, users, ambient, uow, ct))
            .RequireAuthorization(Permissions.PricingManage).Produces(StatusCodes.Status204NoContent);

        channels.MapPost("/{id:guid}/deactivate", (Guid id, long version, HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users,
            AmbientOperationContext ambient, IUnitOfWork uow, CancellationToken ct) => SetChannelActiveAsync(id, version, false, http, antiforgery, users, ambient, uow, ct))
            .RequireAuthorization(Permissions.PricingManage).Produces(StatusCodes.Status204NoContent);

        // ---- Fee rules (one per channel — see FeeRule.cs remarks) ----

        channels.MapGet("/{channelId:guid}/fee-rule", async (Guid channelId, VerceDbContext db, CancellationToken ct) =>
        {
            var rule = await db.Set<FeeRule>().AsNoTracking().Include(x => x.Versions).SingleOrDefaultAsync(x => x.SalesChannelId == channelId, ct);
            return rule is null ? Results.NotFound() : Results.Ok(ToResponse(rule));
        }).RequireAuthorization(Permissions.PricingRead).Produces<FeeRuleResponse>().Produces(StatusCodes.Status404NotFound);

        channels.MapPost("/{channelId:guid}/fee-rule", async (Guid channelId, FeeRuleCreateRequest request, HttpContext http, IAntiforgery antiforgery,
            UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, VerceDbContext readDb, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
            try
            {
                var actor = await users.GetUserAsync(http.User);
                if (actor is null) return Results.Unauthorized();
                ambient.SetActor(actor.Id, actor.DisplayName);
                if (!await readDb.Set<SalesChannel>().AnyAsync(x => x.Id == channelId, ct)) return Results.NotFound();
                FeeRule? created = null;
                await uow.ExecuteAsync((db, token) =>
                {
                    created = new FeeRule(channelId, request.Name);
                    db.Add(created);
                    return Task.CompletedTask;
                }, ct);
                return Results.Created($"/api/pricing/channels/{channelId}/fee-rule", ToResponse(created!));
            }
            catch (ArgumentException ex) { return Problem(ex.Message); }
            catch (DbUpdateException ex) when (IsUnique(ex)) { return Problem("FEE_RULE_ALREADY_EXISTS", StatusCodes.Status409Conflict); }
        }).RequireAuthorization(Permissions.PricingManage).Produces<FeeRuleResponse>(StatusCodes.Status201Created);

        channels.MapPost("/{channelId:guid}/fee-rule/versions", async (Guid channelId, FeeRuleVersionCreateRequest request, HttpContext http,
            IAntiforgery antiforgery, UserManager<ApplicationUser> users, AmbientOperationContext ambient, IUnitOfWork uow, VerceDbContext readDb,
            IClock clock, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return BadRequest();
            try
            {
                var actor = await users.GetUserAsync(http.User);
                if (actor is null) return Results.Unauthorized();
                ambient.SetActor(actor.Id, actor.DisplayName);
                var channel = await readDb.Set<SalesChannel>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == channelId, ct)
                    ?? throw new KeyNotFoundException();
                await uow.ExecuteAsync(async (db, token) =>
                {
                    var rule = await db.Set<FeeRule>().Include(x => x.Versions).SingleOrDefaultAsync(x => x.SalesChannelId == channelId, token)
                        ?? throw new KeyNotFoundException();
                    if (request.CloseCurrentOpenVersion) rule.CloseOpenVersion(request.ValidFrom);
                    // Terra N-03: a Direct-kind channel (the seeded "Venda Direta" included) can
                    // never gain non-zero commercial terms — enforced in the domain method itself
                    // so no future caller can bypass it.
                    rule.AddVersion(request.ValidFrom, request.ValidUntil, request.CommissionPercent, request.FixedFee,
                        request.FixedFeeApplication, request.MinimumFee, request.MaximumFee, request.Notes, channel.Kind);
                }, ct);
                var updated = await readDb.Set<FeeRule>().AsNoTracking().Include(x => x.Versions).SingleAsync(x => x.SalesChannelId == channelId, ct);
                return Results.Created($"/api/pricing/channels/{channelId}/fee-rule", ToResponse(updated));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException ex) { return Problem(ex.Message); }
            catch (DbUpdateException ex) when (IsExclusionViolation(ex)) { return Problem("FEE_RULE_VERSION_OVERLAPS_EXISTING", StatusCodes.Status409Conflict); }
        }).RequireAuthorization(Permissions.PricingManage).Produces<FeeRuleResponse>(StatusCodes.Status201Created).Produces(StatusCodes.Status404NotFound);

        // ---- Calculation ----

        var pricing = app.MapGroup("/api/pricing");

        pricing.MapPost("/calculate", async (PricingCalculateRequest request, HttpContext http, IAntiforgery antiforgery, AppSettingValueReader settings, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return Problem("ANTIFORGERY_VALIDATION_FAILED");
            var desiredMargin = request.DesiredMargin ?? await settings.GetDecimalAsync("pricing.default_margin_percent", ct);
            var roundingPolicy = request.RoundingPolicy ?? await GetRoundingPolicyAsync(settings, ct);
            var marginWarningDenominator = await settings.GetDecimalAsync("pricing.margin_warning_denominator", ct);
            var input = new PricingCalculationInput(request.UnitTotalCost, request.CommissionPercent, request.FixedFee, desiredMargin,
                roundingPolicy, marginWarningDenominator, request.MinimumFee, request.MaximumFee);
            var result = PricingEngine.Calculate(input);
            return result.IsSuccess ? Results.Ok(ToResponse(result.Value)) : Problem(result.ErrorCode!, StatusCodes.Status422UnprocessableEntity);
        }).RequireAuthorization(Permissions.PricingCalculate).Produces<PricingCalculateResponse>().Produces(StatusCodes.Status422UnprocessableEntity);

        pricing.MapPost("/products/{productId:guid}/price", async (Guid productId, ProductPriceRequest request, HttpContext http, IAntiforgery antiforgery,
            VerceDbContext db, ICostingInventoryReader inventory, AppSettingValueReader settings, IClock clock, CancellationToken ct) =>
        {
            if (!await IsValidCsrf(http, antiforgery)) return Problem("ANTIFORGERY_VALIDATION_FAILED");
            var product = await ProductEndpoints.LoadProductAsync(db, productId, ct);
            if (product is null) return Problem("PRODUCT_NOT_FOUND", StatusCodes.Status404NotFound);
            if (!product.Active) return Problem("PRODUCT_INACTIVE", StatusCodes.Status422UnprocessableEntity);

            var channel = await db.Set<SalesChannel>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.SalesChannelId, ct);
            if (channel is null) return Problem("MARKETPLACE_NOT_FOUND", StatusCodes.Status404NotFound);
            if (!channel.Active) return Problem("MARKETPLACE_INACTIVE", StatusCodes.Status422UnprocessableEntity);

            CostCalculationResult cost;
            try { cost = await ProductCostCalculator.CalculateAsync(product, inventory, settings, ct); }
            catch (ArgumentException ex) when (ex.Message == "COST_BASIS_UNAVAILABLE") { return Problem("COST_BASIS_UNAVAILABLE", StatusCodes.Status422UnprocessableEntity); }
            catch (ArgumentException ex) when (ex.Message is "RECIPE_EMPTY" or "RECIPE_MACHINE_RATE_REQUIRED") { return Problem("RECIPE_INVALID", StatusCodes.Status422UnprocessableEntity); }

            // Terra B-02: the ORGANIZATION business date, not clock.UtcNow.Date — resolved once
            // per request (mission §33/§34) and reused everywhere "today" matters below, so a
            // request handled near UTC midnight can never resolve a fee version against the
            // wrong calendar day for America/Sao_Paulo.
            var organizationDate = clock.OrganizationToday();
            var feeRule = await db.Set<FeeRule>().AsNoTracking().Include(x => x.Versions)
                .SingleOrDefaultAsync(x => x.SalesChannelId == request.SalesChannelId && x.Active, ct);
            var feeVersion = feeRule?.ResolveVersionAt(organizationDate);
            if (feeRule is null || feeVersion is null) return Problem("FEE_RULE_NOT_FOUND", StatusCodes.Status422UnprocessableEntity);

            var desiredMargin = request.DesiredMarginOverride ?? channel.DefaultMarginPercent ?? await settings.GetDecimalAsync("pricing.default_margin_percent", ct);
            var roundingPolicy = await GetRoundingPolicyAsync(settings, ct);
            var marginWarningDenominator = await settings.GetDecimalAsync("pricing.margin_warning_denominator", ct);

            // One resolved FeeRuleVersion feeds the entire calculation (mission §16) — never
            // re-queried "current" after the fact, so the response below is guaranteed to
            // describe the SAME context that actually produced the price.
            var input = new PricingCalculationInput(cost.Totals.EstimatedUnitCost, feeVersion.CommissionPercent, feeVersion.FixedFee,
                desiredMargin, roundingPolicy, marginWarningDenominator, feeVersion.MinimumFee, feeVersion.MaximumFee);
            var result = PricingEngine.Calculate(input);
            if (result.IsFailure) return Problem(result.ErrorCode!, StatusCodes.Status422UnprocessableEntity);

            // Terra B-03: every identity/driver a future S6 snapshot needs, all from this one
            // resolved context — never re-derived from a second, possibly-drifted query.
            return Results.Ok(new ProductPriceResponse(
                product.Id, product.Code, product.Name,
                result.Value.UnitTotalCost,
                channel.Id, channel.Code, channel.Kind, channel.Name,
                result.Value.DesiredMargin,
                feeRule.Id, feeVersion.Id, result.Value.CommissionPercent, result.Value.FixedFee,
                organizationDate,
                result.Value.Denominator, result.Value.RawPrice, roundingPolicy, result.Value.SuggestedPrice,
                result.Value.CommissionAmount, result.Value.FeeClampApplied, result.Value.Warnings));
        }).RequireAuthorization(Permissions.PricingCalculate).Produces<ProductPriceResponse>()
            .Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status422UnprocessableEntity);

        return app;
    }

    private static async Task<PriceRoundingPolicy> GetRoundingPolicyAsync(AppSettingValueReader settings, CancellationToken ct)
    {
        var raw = await settings.GetStringAsync("pricing.price_rounding_policy", ct);
        return Enum.TryParse<PriceRoundingPolicy>(raw, out var policy) ? policy : throw new InvalidOperationException("SETTING_VALUE_INVALID");
    }

    private static async Task<IResult> SetChannelActiveAsync(Guid id, long version, bool active, HttpContext http, IAntiforgery antiforgery,
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
                var channel = await db.Set<SalesChannel>().SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException();
                if (channel.Version != version) throw new DbUpdateConcurrencyException();
                if (active) channel.Activate(); else channel.Deactivate();
            }, ct);
            return Results.NoContent();
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (DbUpdateConcurrencyException) { return Problem("CONCURRENCY_CONFLICT", StatusCodes.Status409Conflict); }
    }

    private static SalesChannelResponse ToResponse(SalesChannel channel) =>
        new(channel.Id, channel.Code, channel.Name, channel.Kind, channel.DefaultMarginPercent, channel.Notes, channel.Active, channel.Version);

    private static FeeRuleResponse ToResponse(FeeRule rule) => new(rule.Id, rule.SalesChannelId, rule.Name, rule.Active, rule.Version,
        rule.Versions.OrderBy(v => v.ValidFrom).Select(v => new FeeRuleVersionResponse(v.Id, v.ValidFrom, v.ValidUntil, v.CommissionPercent,
            v.FixedFee, v.FixedFeeApplication, v.MinimumFee, v.MaximumFee, v.Notes)).ToList());

    private static PricingCalculateResponse ToResponse(PricingCalculationResult result) => new(result.UnitTotalCost, result.CommissionPercent,
        result.FixedFee, result.DesiredMargin, result.Denominator, result.RawPrice, result.SuggestedPrice, result.CommissionAmount,
        result.FeeClampApplied, result.Warnings);

    private static bool IsUnique(DbUpdateException ex) => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    private static bool IsExclusionViolation(DbUpdateException ex) => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation };

    private static async Task<bool> IsValidCsrf(HttpContext http, IAntiforgery antiforgery)
    {
        try { await antiforgery.ValidateRequestAsync(http); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    private static IResult BadRequest() => Results.Problem(statusCode: 400, title: "Requisição inválida.");
    private static IResult Problem(string code, int status = StatusCodes.Status400BadRequest) =>
        Results.Problem(statusCode: status, title: "Não foi possível concluir a operação.", extensions: new Dictionary<string, object?> { ["code"] = code });
}
