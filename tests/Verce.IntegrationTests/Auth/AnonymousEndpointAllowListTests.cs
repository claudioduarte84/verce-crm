using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Verce.IntegrationTests.Auth;

/// <summary>
/// SECURITY §3.2: "An architecture test asserts no route outside this table is anonymous."
/// The complete anonymous allow-list is: <c>/health/live</c>, <c>/health/ready</c>,
/// <c>POST /api/auth/login</c>, <c>GET/POST /setup-account</c> (read here as the JSON action
/// behind the SPA's /setup-account page — see AuthEndpoints.cs and the FINAL REPORT),
/// <c>GET /api/commerce/marketplace-authorizations/{providerCode}/callback</c> (ADR-0024 §2 —
/// the browser carries no Verce session cookie on a provider redirect; the hashed one-time
/// state/browser-binding pair is the real boundary), and static assets (none served by this
/// API). Every other endpoint must require authentication — enumerated from the REAL running
/// app's route table, not re-declared by hand.
/// </summary>
/// <summary>
/// M-TESTHOST-001: every test that constructs a <see cref="VerceWebApplicationFactory"/> must
/// belong to this collection — the factory serializes ALL its process-environment mutation on a
/// process-wide lock (defense in depth), but xUnit's own sequential-within-a-collection guarantee
/// is what stops a test in a DIFFERENT collection from being scheduled concurrently with one held
/// here in the first place. This class was the one instance found running outside it.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AnonymousEndpointAllowListTests
{
    // No real database work happens for this test — DbContext is never opened, only the
    // endpoint/authorization metadata that Program.cs builds is inspected — so a syntactically
    // valid but unreachable connection string is enough; spinning up a Testcontainers Postgres
    // for this would be pure overhead.
    private const string UnreachableConnectionString = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";

    private static readonly HashSet<string> AnonymousAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET /health/live",
        "GET /health/ready",
        // Not in the SECURITY §3.2 table verbatim: it exists only so an anonymous visitor can
        // obtain the antiforgery double-submit cookie before submitting the login form. It
        // reveals nothing and mutates nothing — see AuthEndpoints.cs and the FINAL REPORT's
        // IMPLEMENTATION DEVIATIONS for this addition to the documented allow-list.
        "GET /api/auth/csrf",
        "POST /api/auth/login",
        "POST /api/auth/setup-account",
        // ADR-0024 §2 (S8C.1): "HTTP callback may be anonymous; the persisted transaction
        // supplies actor/context, not the browser's current user." The real provider redirects
        // the browser back here with no session cookie of its own; the one-time state/browser-
        // binding hashes plus the durable session row (not ASP.NET auth) are this route's actual
        // security boundary — see MarketplaceAuthorizationWorkflow.CompleteCallbackAsync.
        "GET /api/commerce/marketplace-authorizations/{providerCode}/callback",
    };

    [Fact]
    public async Task No_endpoint_outside_the_documented_allow_list_is_anonymous()
    {
        await using var factory = new VerceWebApplicationFactory(UnreachableConnectionString);
        using var scope = factory.Services.CreateScope();
        var dataSource = scope.ServiceProvider.GetRequiredService<EndpointDataSource>();

        var anonymousEndpoints = new List<string>();

        foreach (var endpoint in dataSource.Endpoints)
        {
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null) continue;

            var pattern = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? endpoint.DisplayName ?? "<unknown>";
            var methods = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods
                          ?? new[] { "GET" };

            foreach (var method in methods)
                anonymousEndpoints.Add($"{method} {pattern}");
        }

        anonymousEndpoints.Should().BeEquivalentTo(AnonymousAllowList,
            "SECURITY §3.2's anonymous allow-list is exhaustive — a new endpoint is protected " +
            "by default (RequireAuthorization() FallbackPolicy) unless it deliberately opts out here AND is added to this table");
    }
}
