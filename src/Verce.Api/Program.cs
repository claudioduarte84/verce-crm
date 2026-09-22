using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Verce.Api.Auth;
using Verce.Api.Authorization;
using Verce.Api.Catalog;
using Verce.Api.Cli;
using Verce.Api.Customers;
using Verce.Api.Costing;
using Verce.Api.Inventory;
using Verce.Api.Outbox;
using Verce.Api.Pricing;
using Verce.Api.Quoting;
using Verce.Api.Settings;
using Verce.Modules.Production;
using Verce.Modules.Quoting;
using Verce.Modules.Quoting.Contracts;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;

var builder = WebApplication.CreateBuilder(args);

// ---- Platform (ADR-0001 §5.1): DbContext, Identity, Data Protection, UoW, outbox, audit ----
// Every module assembly is referenced here so the ownership registry (and future architecture
// tests) can see them — the composition root wires every module; modules never reference
// each other directly (ADR-0001 §3).
var allModuleAssemblies = Verce.Api.ModuleAssemblyCatalog.All;

builder.Services.AddVercePlatform(builder.Configuration, builder.Environment, allModuleAssemblies);
var configuredBrandAssetRoot = builder.Configuration["BrandAssets:StorageRoot"];
if (builder.Environment.IsProduction()
    && (string.IsNullOrWhiteSpace(configuredBrandAssetRoot) || !Path.IsPathRooted(configuredBrandAssetRoot)))
{
    throw new InvalidOperationException(
        "Production Brand Asset storage requires BrandAssets:StorageRoot to be an explicit absolute durable path.");
}
var effectiveBrandAssetRoot = configuredBrandAssetRoot
    ?? Path.Combine(AppContext.BaseDirectory, "data", "brand-assets");
if (builder.Environment.IsProduction())
{
    try
    {
        Directory.CreateDirectory(effectiveBrandAssetRoot);
        var probePath = Path.Combine(effectiveBrandAssetRoot, ".verce-write-probe-" + Guid.CreateVersion7().ToString("N"));
        File.WriteAllBytes(probePath, []);
        File.Delete(probePath);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
    {
        throw new InvalidOperationException(
            "Production BrandAssets:StorageRoot must exist or be creatable and writable before HTTP starts.", ex);
    }
}
builder.Services.Configure<Verce.Modules.Settings.BrandAssetStorageOptions>(options =>
    options.StorageRoot = effectiveBrandAssetRoot);
builder.Services.AddScoped<Verce.Modules.Settings.AppSettingValueReader>();
builder.Services.AddScoped<Verce.Modules.Settings.BrandAssetStorage>();

// ---- S7: Quote PDF V1 (ADR-0016) — content-addressed generated-document storage, mirroring
// BrandAssets:StorageRoot's exact production-hardening shape one directory over. ----
var configuredDocumentStorageRoot = builder.Configuration["Documents:StorageRoot"];
if (builder.Environment.IsProduction()
    && (string.IsNullOrWhiteSpace(configuredDocumentStorageRoot) || !Path.IsPathRooted(configuredDocumentStorageRoot)))
{
    throw new InvalidOperationException(
        "Production Documents storage requires Documents:StorageRoot to be an explicit absolute durable path.");
}
var effectiveDocumentStorageRoot = configuredDocumentStorageRoot
    ?? Path.Combine(AppContext.BaseDirectory, "data", "generated-documents");
if (builder.Environment.IsProduction())
{
    try
    {
        Directory.CreateDirectory(effectiveDocumentStorageRoot);
        var probePath = Path.Combine(effectiveDocumentStorageRoot, ".verce-write-probe-" + Guid.CreateVersion7().ToString("N"));
        File.WriteAllBytes(probePath, []);
        File.Delete(probePath);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
    {
        throw new InvalidOperationException(
            "Production Documents:StorageRoot must exist or be creatable and writable before HTTP starts.", ex);
    }
}
builder.Services.Configure<Verce.Modules.Documents.DocumentStorageOptions>(options =>
    options.StorageRoot = effectiveDocumentStorageRoot);
builder.Services.AddSingleton<Verce.Modules.Documents.IDocumentStorage, Verce.Modules.Documents.DocumentStorage>();
builder.Services.AddScoped<Verce.Modules.Documents.QuotePdfService>();
// mission §45: production always binds 30s (SECURITY §8); a test overrides
// "Documents:RenderTimeoutSeconds" through the SAME configuration path to prove the timeout
// contract deterministically, without ever actually waiting 30 real seconds.
builder.Services.Configure<Verce.Modules.Documents.PdfRenderTimeoutOptions>(options =>
{
    var configuredSeconds = builder.Configuration.GetValue<double?>("Documents:RenderTimeoutSeconds");
    if (configuredSeconds is { } seconds) options.Timeout = TimeSpan.FromSeconds(seconds);
});
// F-01 (S7 final-findings correction): a SEPARATE disposable process renders every PDF — this
// singleton owns no long-lived browser of its own, only the configured timeout and the worker's
// path. Still registered once as both the renderer AND the hosted service that runs its startup
// health probe (Verce.Modules.Documents.PlaywrightHtmlToPdfRenderer's own doc comment); ASP.NET
// Core resolves an IHostedService registered this way to the SAME singleton instance.
builder.Services.AddSingleton<Verce.Modules.Documents.PlaywrightHtmlToPdfRenderer>();
builder.Services.AddSingleton<Verce.Modules.Documents.IHtmlToPdfRenderer>(sp => sp.GetRequiredService<Verce.Modules.Documents.PlaywrightHtmlToPdfRenderer>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<Verce.Modules.Documents.PlaywrightHtmlToPdfRenderer>());
builder.Services.AddScoped<Verce.Modules.Costing.ICostingInventoryReader, Verce.Api.Costing.InventoryCostSourceReader>();
builder.Services.AddHostedService<Verce.Modules.Settings.SettingsSeedService>();
builder.Services.AddHostedService<Verce.Modules.Inventory.InventorySeedService>();
builder.Services.AddHostedService<Verce.Modules.Pricing.PricingSeedService>();
// ADR-0007 §7 (S7/S14 scope authority gate, OPTION A): seeds document_type + the default VERCE
// proposal template as ordinary rows — never compiled renderer code.
builder.Services.AddHostedService<Verce.Modules.Documents.DocumentsSeedService>();

// ---- S6 (ADR-0020 §A.6/§A.8): the QuoteApproved/-Revised/-Canceled/-Expired synchronous
// contract, handled by Production. Registered explicitly — there is no assembly-scanning
// auto-registration for domain event handlers (Verce.Platform.UnitOfWork.DomainEventDispatcher
// resolves IServiceProvider.GetServices<IDomainEventHandler<T>>() in registration order). ----
builder.Services.AddScoped<IDomainEventHandler<QuoteApprovedEvent>, CreateProductionOrderOnQuoteApproved>();
builder.Services.AddScoped<IDomainEventHandler<QuoteRevisedEvent>, MaintainHasPendingRevisionOnQuoteRevised>();
builder.Services.AddScoped<IDomainEventHandler<QuoteCanceledEvent>, ClearHasPendingRevisionOnQuoteCanceled>();
builder.Services.AddScoped<IDomainEventHandler<QuoteExpiredEvent>, ClearHasPendingRevisionOnQuoteExpired>();

// ADR-0012 §1/§12: the integration half of approval — dispatched only after commit, from the
// outbox, so a slow/failed PDF render can never touch the approval transaction (S7/S14 scope
// authority gate §12). Scoped, so the outbox dispatcher's per-cycle DI scope resolves a fresh
// VerceDbContext, exactly like every other scoped service it invokes.
builder.Services.AddScoped<Verce.Platform.Outbox.IIntegrationEventConsumer, Verce.Api.Quoting.GenerateQuotePdfRequestedConsumer>();

builder.Services.AddScoped<Verce.Modules.Quoting.ExpireQuotesService>();
builder.Services.AddVerceQuotingScheduling(builder.Configuration);

// ---- Authentication: same-origin cookie, no bearer/JWT (ADR-0009 §1, SECURITY §2) ----
// The cookie scheme(s) must be explicitly ADDED, not merely configured — ConfigureApplicationCookie
// alone only post-configures options for a scheme that has to already exist. AddIdentityCookies()
// registers all four standard Identity schemes (Application/External/TwoFactorRememberMe/
// TwoFactorUserId); S1 uses none of the 2FA/external ones, but SecurityStampValidator
// unconditionally signs out of ALL of them when invalidating a stale principal — omitting them
// throws "No sign-out authentication handler is registered" the first time a stamp actually
// changes (D-11), which only a genuine end-to-end test with time-forced revalidation caught.
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "__Host-verce.auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.LoginPath = "/api/auth/login";
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    // This is an API, not a browser app with an AccessDenied page — an authenticated-but-not-
    // authorized caller (e.g. Operator/Viewer hitting the Owner-only health detail, H-4) must
    // get a plain 403, never a redirect to a page this host doesn't serve (which a redirect-
    // following HttpClient would otherwise turn into a confusing 404).
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
    // SECURITY §2.4 / D-11: re-validates SecurityStamp roughly every 30 minutes so a reset,
    // recovery, deactivation or role change takes effect without waiting for cookie expiry —
    // AddIdentityCore registers ISecurityStampValidator, but only AddIdentity (not AddIdentityCore)
    // wires this event by default, so it must be set explicitly here.
    options.Events.OnValidatePrincipal = Microsoft.AspNetCore.Identity.SecurityStampValidator.ValidatePrincipalAsync;
});

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    Permissions.AddPolicies(options);
});

// SECURITY §2.4: the SPA reads the token from a non-HttpOnly XSRF-TOKEN cookie and echoes it
// in the X-XSRF-TOKEN header on every state-changing request.
// The framework's OWN antiforgery cookie holds the secret "cookie token" half and stays
// HttpOnly — the SPA never reads it directly. GET /api/auth/csrf separately writes the
// "request token" half into the non-HttpOnly XSRF-TOKEN cookie (Microsoft's documented SPA
// double-submit pattern); the two halves are NOT interchangeable, so they must not share a name.
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "Verce.Antiforgery";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.HeaderName = "X-XSRF-TOKEN";
});

// SECURITY §2.4 / §3.2: 10 requests/minute per client IP on /api/auth/* in production.
// Configurable (never hard-coded per CLAUDE.md §10) so the E2E test host — the only caller with
// a legitimate reason to exceed real-user auth-check volume, since every Playwright test shares
// one "client IP" (localhost) and each page load re-checks the session — can raise it without
// touching the security-mandated production default.
var authRateLimitPermitLimit = builder.Configuration.GetValue("RateLimiting:Auth:PermitLimit", 10);
var authRateLimitWindow = TimeSpan.FromSeconds(builder.Configuration.GetValue("RateLimiting:Auth:WindowSeconds", 60));
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authRateLimitPermitLimit,
            Window = authRateLimitWindow,
            QueueLimit = 0,
        }));
    options.OnRejected = (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return ValueTask.CompletedTask;
    };
});

builder.Services.AddOpenApi();

var app = builder.Build();

// ---- CLI mode: bootstrap-owner / recover-owner / migrate (OPERATIONS §2, ADR-0009 §6/§9) ----
// Branches BEFORE Kestrel binds, matching "dotnet Verce.Api.dll <verb>" exactly.
if (CliCommands.IsCliInvocation(args))
{
    return await CliCommands.RunAsync(args, app.Services);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapAuthEndpoints();
app.MapOutboxAdminEndpoints();
app.MapCustomerEndpoints();
app.MapSettingsEndpoints();
app.MapSupplyEndpoints();
app.MapCostingEndpoints();
app.MapProductEndpoints();
app.MapPricingEndpoints();
app.MapQuotingEndpoints();
app.MapQuotePdfEndpoints();

// ---- Health endpoints (ADR-0012 §25, OPERATIONS §9): status word only, anonymous ----
app.MapGet("/health/live", () => Results.Text("healthy")).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK, // ADR-0012 §25: degraded must NOT be 503
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
    },
    // Anonymous readiness exposes a status word ONLY — no component detail (SECURITY §3.2).
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "text/plain";
        var word = report.Status switch
        {
            HealthStatus.Healthy => "healthy",
            HealthStatus.Degraded => "degraded",
            _ => "unhealthy",
        };
        await context.Response.WriteAsync(word);
    },
}).AllowAnonymous();

// Owner-only detailed diagnostics (SECURITY §3.2, H-4) — full component breakdown; 403 for
// Operator/Viewer, 200 for Owner. Plain RequireAuthorization() would only require SOME
// authenticated user, not specifically Owner — that was the H-4 gap this closes.
app.MapHealthChecks("/api/platform/health", new HealthCheckOptions
{
    // Owner-only: the full per-component breakdown SECURITY §3.2 reserves for this route —
    // unlike /health/ready's bare status word, an Owner is trusted with component names.
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString(),
            components = report.Entries.ToDictionary(
                e => e.Key,
                e => new { status = e.Value.Status.ToString(), description = e.Value.Description }),
        };
        await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(payload));
    },
}).RequireAuthorization(policy => policy.RequireRole(Roles.Owner));

app.Run();
return 0;

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program { }
