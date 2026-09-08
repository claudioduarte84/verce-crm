using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Verce.Platform.Identity;
using Verce.Platform.Outbox;

namespace Verce.Api.Outbox;

public sealed record RequeueOutboxMessageRequest(string Reason);

/// <summary>
/// H-OUTBOX-002 (S1 Gate Corrections): the only HTTP surface for a manual outbox requeue.
/// Owner-only, CSRF-protected exactly like the mutating <c>/api/auth</c> endpoints, and the actor
/// is always derived from the authenticated session — never accepted from the request body.
/// </summary>
public static class OutboxAdminEndpoints
{
    public static IEndpointRouteBuilder MapOutboxAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/platform/outbox").RequireAuthorization(policy => policy.RequireRole(Roles.Owner));

        group.MapPost("/{messageId:guid}/requeue", async (
            Guid messageId,
            RequeueOutboxMessageRequest request,
            HttpContext context,
            IAntiforgery antiforgery,
            UserManager<ApplicationUser> userManager,
            OutboxAdministrationService administrationService) =>
        {
            if (!await ValidateAntiforgeryAsync(context, antiforgery))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Requisição inválida.");

            if (string.IsNullOrWhiteSpace(request.Reason))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Um motivo é obrigatório.");

            var actor = await userManager.GetUserAsync(context.User);
            if (actor is null) return Results.Unauthorized();

            var outcome = await administrationService.RequeueAsync(messageId, actor.Id, request.Reason);
            return outcome switch
            {
                OutboxRequeueOutcome.Requeued => Results.NoContent(),
                _ => Results.Problem(statusCode: StatusCodes.Status409Conflict,
                    title: "A mensagem não existe ou não está em estado FAILED."),
            };
        });

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
