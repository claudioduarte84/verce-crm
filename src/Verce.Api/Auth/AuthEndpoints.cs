using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Verce.Platform.Cli;
using Verce.Platform.Identity;
using Verce.SharedKernel.Time;

namespace Verce.Api.Auth;

public sealed record LoginRequest(string Email, string Password);
public sealed record SetupAccountRequest(string Token, string Password);
public sealed record SessionResponse(Guid Id, string Email, string? DisplayName, IReadOnlyList<string> Roles);

/// <summary>
/// Same-origin cookie authentication endpoints (ADR-0009 §1-§7, SECURITY §2-§3). This is the
/// HTTP surface that was missing at the previous checkpoint — Identity, cookie configuration
/// and <see cref="OwnerBootstrapService"/>'s atomic setup-token consumption already existed in
/// Verce.Platform, but nothing in Verce.Api ever exposed them, and no authentication SCHEME was
/// even registered (see the S1 FINAL REPORT's IMPLEMENTATION DEVIATIONS / gap history).
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").RequireRateLimiting("auth");

        // Anonymous allow-list (SECURITY §3.2): /health/live, /health/ready, POST /api/auth/login,
        // GET/POST /setup-account, static assets. This endpoint is NOT on that list — it exists
        // only so the SPA can obtain the antiforgery double-submit cookie before it submits the
        // login form; it reveals nothing and mutates nothing.
        group.MapGet("/csrf", (IAntiforgery antiforgery, HttpContext context) =>
        {
            // GetAndStoreTokens sets the framework's OWN (HttpOnly) cookie token as a side
            // effect. The "request token" half it returns is the one the SPA must echo back in
            // X-XSRF-TOKEN — it is NOT the same value as the cookie, so it has to be exposed
            // here explicitly (SECURITY §2.4's "SPA reads the token from a XSRF-TOKEN cookie").
            var tokens = antiforgery.GetAndStoreTokens(context);
            context.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!, new CookieOptions
            {
                HttpOnly = false,
                Secure = true,
                SameSite = SameSiteMode.Lax,
            });
            return Results.NoContent();
        }).AllowAnonymous();

        group.MapPost("/login", async (
            LoginRequest request,
            HttpContext context,
            IAntiforgery antiforgery,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            IClock clock) =>
        {
            if (!await ValidateAntiforgeryAsync(context, antiforgery))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Requisição inválida.");

            var user = await userManager.FindByEmailAsync(request.Email);

            // ADR-0009 §7.1 (D-8, frozen): setup_status gate BEFORE password verification,
            // rejected with the SAME generic message as a wrong password — never distinguishable.
            if (user is null || !user.IsActive || user.SetupStatus != SetupStatus.Active)
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Credenciais inválidas.");

            var result = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
            if (!result.Succeeded)
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Credenciais inválidas.");

            await signInManager.SignInAsync(user, isPersistent: false);
            user.LastLoginAt = clock.UtcNow;
            await userManager.UpdateAsync(user);

            return Results.NoContent();
        }).AllowAnonymous();

        group.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery, SignInManager<ApplicationUser> signInManager) =>
        {
            if (!await ValidateAntiforgeryAsync(context, antiforgery))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Requisição inválida.");

            await signInManager.SignOutAsync();
            return Results.NoContent();
        });

        group.MapGet("/session", async (
            System.Security.Claims.ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();

            var roles = await userManager.GetRolesAsync(user);
            return Results.Ok(new SessionResponse(user.Id, user.Email!, user.DisplayName, roles.ToList()));
        });

        // Anonymous per SECURITY §3.2's "/setup-account" allow-list entry — interpreted here as
        // the JSON action behind the SPA's /setup-account page (the SPA route itself is static
        // content, never a backend route); see the FINAL REPORT for this reading.
        group.MapPost("/setup-account", async (
            SetupAccountRequest request,
            HttpContext context,
            IAntiforgery antiforgery,
            OwnerBootstrapService bootstrapService) =>
        {
            if (!await ValidateAntiforgeryAsync(context, antiforgery))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Requisição inválida.");

            if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.Password))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Requisição inválida.");

            var consumed = await bootstrapService.ConsumeSetupTokenAsync(request.Token, request.Password);
            if (!consumed)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Link inválido, expirado ou já utilizado.");

            return Results.NoContent();
        }).AllowAnonymous();

        return app;
    }

    private static async Task<bool> ValidateAntiforgeryAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
}
