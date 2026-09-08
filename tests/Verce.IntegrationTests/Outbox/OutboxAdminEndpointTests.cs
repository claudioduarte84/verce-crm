using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verce.Api.Auth;
using Verce.Api.Outbox;
using Verce.IntegrationTests.Auth;
using Verce.Platform.Identity;
using Verce.Platform.Outbox;
using Verce.Platform.Persistence;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Outbox;

/// <summary>H-OUTBOX-002: <c>POST /api/platform/outbox/{id}/requeue</c> exercised through the
/// REAL host — Owner-only, CSRF-protected, actor always derived from the authenticated
/// session.</summary>
[Collection(PostgresCollection.Name)]
public class OutboxAdminEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;

    public OutboxAdminEndpointTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE platform.outbox_message_attempt, platform.outbox_message,
                           platform.account_setup_token, platform.user_role, platform.user_claim,
                           platform.user_login, platform.user_token, platform.audit_log, platform."user"
            RESTART IDENTITY CASCADE;
            """);
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<Guid> SeedFailedMessageAsync(string key)
    {
        await using var context = _fixture.CreateContext();
        var message = OutboxMessage.Enqueue(
            eventType: "ProbeIntegrationEvent", payloadJson: "{}", idempotencyKey: key,
            correlationId: Guid.CreateVersion7(), requestId: null, actorUserId: null,
            aggregateType: "Probe", aggregateId: null, now: DateTimeOffset.UtcNow, maxAttempts: 5);
        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync("""
            UPDATE platform.outbox_message
            SET status = 'Failed', failure_disposition = 'Active', failed_at = {0}, available_at = NULL
            WHERE id = {1};
            """, DateTimeOffset.UtcNow, message.Id);
        return message.Id;
    }

    private async Task<(string Email, string Password, Guid UserId)> CreateUserInRoleAsync(string role)
    {
        var email = $"{role.ToLowerInvariant()}@example.com";
        const string password = "a-perfectly-fine-12char-password";

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        if (!await roleManager.RoleExistsAsync(role))
            await roleManager.CreateAsync(new ApplicationRole(role));

        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            DisplayName = $"{role} Test User",
            IsActive = true,
            SetupStatus = SetupStatus.Active,
            SetupCompletedAt = DateTimeOffset.UtcNow,
        };
        var createResult = await userManager.CreateAsync(user, password);
        createResult.Succeeded.Should().BeTrue(string.Join("; ", createResult.Errors.Select(e => e.Description)));
        await userManager.AddToRoleAsync(user, role);

        return (email, password, user.Id);
    }

    private async Task<(AuthTestClient Client, Guid UserId)> LoggedInAsAsync(string role)
    {
        var (email, password, userId) = await CreateUserInRoleAsync(role);
        var client = new AuthTestClient(_factory.CreateHttpsClient());
        await client.EnsureCsrfCookieAsync();
        var login = await client.PostAsync("/api/auth/login", new LoginRequest(email, password));
        login.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The antiforgery token embeds the caller's identity at mint time — login just changed
        // it from anonymous to authenticated, so the pre-login token is no longer valid and must
        // be refreshed before any subsequent mutating call (mirrors what the real SPA also does).
        await client.EnsureCsrfCookieAsync();
        return (client, userId);
    }

    [Fact]
    public async Task Owner_can_requeue_a_FAILED_message_and_the_actor_is_the_authenticated_Owner()
    {
        var messageId = await SeedFailedMessageAsync("admin-http:owner");
        var (client, ownerId) = await LoggedInAsAsync(Roles.Owner);

        var response = await client.PostAsync($"/api/platform/outbox/{messageId}/requeue", new RequeueOutboxMessageRequest("customer escalated"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, body);

        await using var verify = _fixture.CreateContext();
        (await verify.OutboxMessages.SingleAsync(m => m.Id == messageId)).Status.Should().Be(OutboxStatus.Pending);
        var audit = await verify.AuditLog.SingleAsync(a => a.EntityId == messageId);
        audit.UserId.Should().Be(ownerId, "the actor must come from the authenticated session, never the request body");
        audit.Operation.Should().Be("OUTBOX_REQUEUE");
    }

    [Fact]
    public async Task Operator_and_Viewer_are_forbidden_from_requeuing()
    {
        var messageId = await SeedFailedMessageAsync("admin-http:forbidden");

        var (operatorClient, _) = await LoggedInAsAsync(Roles.Operator);
        (await operatorClient.PostAsync($"/api/platform/outbox/{messageId}/requeue", new RequeueOutboxMessageRequest("nope")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var (viewerClient, _) = await LoggedInAsAsync(Roles.Viewer);
        (await viewerClient.PostAsync($"/api/platform/outbox/{messageId}/requeue", new RequeueOutboxMessageRequest("nope")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await using var verify = _fixture.CreateContext();
        (await verify.OutboxMessages.SingleAsync(m => m.Id == messageId)).Status.Should().Be(OutboxStatus.Failed, "a forbidden request must never mutate the message");
    }

    [Fact]
    public async Task A_missing_CSRF_token_is_rejected_even_for_an_Owner()
    {
        var messageId = await SeedFailedMessageAsync("admin-http:csrf");
        var (client, _) = await LoggedInAsAsync(Roles.Owner);

        var response = await client.PostAsync($"/api/platform/outbox/{messageId}/requeue", new RequeueOutboxMessageRequest("no csrf"), withAntiforgery: false);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_blank_reason_is_rejected_with_400()
    {
        var messageId = await SeedFailedMessageAsync("admin-http:blank-reason");
        var (client, _) = await LoggedInAsAsync(Roles.Owner);

        var response = await client.PostAsync($"/api/platform/outbox/{messageId}/requeue", new RequeueOutboxMessageRequest("   "));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await using var verify = _fixture.CreateContext();
        (await verify.OutboxMessages.SingleAsync(m => m.Id == messageId)).Status.Should().Be(OutboxStatus.Failed);
    }

    [Fact]
    public async Task Requeuing_a_message_that_is_not_FAILED_returns_409()
    {
        var (client, _) = await LoggedInAsAsync(Roles.Owner);

        var response = await client.PostAsync($"/api/platform/outbox/{Guid.CreateVersion7()}/requeue", new RequeueOutboxMessageRequest("does not exist"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
