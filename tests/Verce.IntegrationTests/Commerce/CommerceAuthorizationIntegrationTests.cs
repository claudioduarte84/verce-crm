using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Verce.Api.Auth;
using Verce.Api.Commerce;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Commerce;
using Verce.Platform.Identity;

namespace Verce.IntegrationTests.Commerce;

[Collection(PostgresCollection.Name)]
public sealed class CommerceAuthorizationIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;

    public CommerceAuthorizationIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData(Roles.Owner)]
    [InlineData(Roles.Operator)]
    [InlineData(Roles.Viewer)]
    public async Task Every_commerce_role_can_read_commerce_surfaces(string role)
    {
        var client = await LoggedInAsAsync(role);
        foreach (var path in new[]
        {
            "/api/commerce/offers", "/api/commerce/providers",
            "/api/commerce/marketplace-accounts", "/api/commerce/published-items",
            "/api/commerce/catalog", "/api/commerce/tags", "/api/commerce/product-image-assets"
        })
        {
            var response = await client.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"{role} must read {path}; response: {body}");
        }
    }

    [Theory]
    [InlineData(Roles.Owner, false, false)]
    [InlineData(Roles.Operator, false, true)]
    [InlineData(Roles.Viewer, true, true)]
    public async Task Mutation_matrix_is_enforced_by_backend_policies(
        string role, bool commerceMutationForbidden, bool accountMutationForbidden)
    {
        var client = await LoggedInAsAsync(role);
        var product = Guid.NewGuid();
        var offer = await client.PostAsync("/api/commerce/offers", new ChannelOfferWriteRequest(
            product, Guid.NewGuid(), 10m, ChannelOfferPriceSource.MANUAL, null, null));
        AssertForbidden(offer, commerceMutationForbidden, role, "offer");

        var reconciliation = await client.PostAsync($"/api/commerce/published-items/{Guid.NewGuid()}/link",
            new LinkListingRequest(product, null, 0));
        AssertForbidden(reconciliation, commerceMutationForbidden, role, "reconciliation");

        var profileTag = await client.PostAsync(
            $"/api/commerce/profiles/{product}/tags/{Guid.NewGuid()}?version=0", new { });
        AssertForbidden(profileTag, commerceMutationForbidden, role, "commercial profile");

        var image = await client.PostAsync($"/api/commerce/profiles/{product}/images",
            new CommercialProfileImageRequest(Guid.NewGuid(), CommercialImageRole.PRIMARY, 0, null, 0));
        AssertForbidden(image, commerceMutationForbidden, role, "image");

        var tag = await client.PostAsync("/api/commerce/tags",
            new CommercialTagWriteRequest("AUTH-" + Guid.NewGuid().ToString("N"), "Autorização", true, null));
        AssertForbidden(tag, commerceMutationForbidden, role, "tag");

        var authorization = await client.PostAsync("/api/commerce/marketplace-authorizations",
            new BeginMarketplaceAuthorizationRequest("SHOPEE", Guid.NewGuid(), "Conta auth"));
        AssertForbidden(authorization, accountMutationForbidden, role, "marketplace authorization");
    }

    private static void AssertForbidden(HttpResponseMessage response, bool expected, string role, string surface)
    {
        if (expected)
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{role} must not mutate {surface}");
        else
            response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, $"{role} is authorized for {surface}; domain validation may still reject the dummy request");
    }

    private async Task<AuthTestClient> LoggedInAsAsync(string role)
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        const string password = "a-perfectly-fine-12char-password";
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!await roles.RoleExistsAsync(role))
            (await roles.CreateAsync(new ApplicationRole(role))).Succeeded.Should().BeTrue();
        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = role + " Commerce",
            IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow
        };
        (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
        (await users.AddToRoleAsync(user, role)).Succeeded.Should().BeTrue();

        var client = new AuthTestClient(_factory.CreateHttpsClient());
        await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new LoginRequest(email, password))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await client.EnsureCsrfCookieAsync();
        return client;
    }
}
