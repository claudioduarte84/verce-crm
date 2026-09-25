using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Verce.IntegrationTests.Commerce;
using Verce.Modules.Commerce;

namespace Verce.Marketplaces.E2EHost;

/// <summary>
/// ADR-0024 G-06: "Any deterministic fault/consent helper is mapped by the isolated test host
/// only; Production has no such routes." This maps exactly one such route —
/// <c>POST /__e2e__/marketplace/scenarios/{sessionId}</c>, letting a real-browser Playwright test
/// tell the in-process <see cref="FakeMarketplaceControlPlane"/> what external identity/grants a
/// session's provider exchange should resolve to before the browser is navigated through the
/// real callback route. Registered ONLY by this test-owned host's own Program.cs (via
/// <see cref="Microsoft.Extensions.DependencyInjection.ServiceCollectionDescriptorExtensions"/>-style
/// <c>IStartupFilter</c> registration) — never by <c>Verce.Api</c>'s own composition, which has no
/// reference to this project or to <c>Verce.IntegrationTests</c> at all.
///
/// Deliberately raw middleware (short-circuiting before routing/authorization ever run), not a
/// mapped minimal-API endpoint: it intercepts its own fixed path prefix and returns directly, so
/// it can never collide with, shadow, or reorder any of Verce.Api's own real
/// <c>/api/commerce/...</c> endpoint mappings. Every call in these three journeys is made from an
/// already browser-authenticated Owner Playwright context in practice, so no anonymous-access
/// concern arises even though this path is intentionally outside Verce.Api's own auth-guarded
/// `/api` surface.
/// </summary>
public sealed class MarketplaceE2EControlEndpointsStartupFilter(FakeMarketplaceControlPlane control) : IStartupFilter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            var path = context.Request.Path.Value ?? "";
            const string prefix = "/__e2e__/marketplace/scenarios/";
            if (context.Request.Method == HttpMethods.Post && path.StartsWith(prefix, StringComparison.Ordinal)
                && Guid.TryParse(path[prefix.Length..], out var sessionId))
            {
                var body = await JsonSerializer.DeserializeAsync<ScenarioRequest>(context.Request.Body, JsonOptions, context.RequestAborted)
                    ?? throw new InvalidOperationException("E2E_SCENARIO_BODY_REQUIRED");
                var grants = (body.Grants ?? [])
                    .ToDictionary(kv => kv.Key, kv => Enum.Parse<AccountCapabilityState>(kv.Value), StringComparer.OrdinalIgnoreCase);
                control.Configure(sessionId, new FakeMarketplaceScenario(
                    body.ExternalAccountId,
                    grants,
                    Enum.Parse<FakeMarketplaceOutcome>(body.Outcome ?? nameof(FakeMarketplaceOutcome.Success)),
                    Enum.Parse<CredentialImpact>(body.Impact ?? nameof(CredentialImpact.MAY_SUPERSEDE_EXISTING))));
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            await nextMiddleware(context);
        });
        next(app);
    };

    private sealed class ScenarioRequest
    {
        public string ExternalAccountId { get; set; } = "";
        public Dictionary<string, string>? Grants { get; set; }
        public string? Outcome { get; set; }
        public string? Impact { get; set; }
    }
}
