using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verce.Api.Auth;
using Verce.IntegrationTests.Auth;
using Verce.Platform.Identity;
using Verce.Platform.Outbox;
using Verce.Platform.Persistence;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Health;

/// <summary>
/// H-1, H-2, H-2a, H-3, H-4 (ROADMAP S1 catalogue): the outbox dispatcher health contributor
/// and the readiness/detail endpoint HTTP contract, exercised through the REAL host.
/// </summary>
[Collection(PostgresCollection.Name)]
public class HealthEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;

    public HealthEndpointTests(PostgresFixture fixture) => _fixture = fixture;

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

    private OutboxMessage SeedMessage(int maxAttempts, DateTimeOffset now) =>
        OutboxMessage.Enqueue(
            eventType: "HealthProbeEvent", payloadJson: "{}", idempotencyKey: null,
            correlationId: Guid.CreateVersion7(), requestId: null, actorUserId: null,
            aggregateType: "HealthProbe", aggregateId: null, now: now, maxAttempts: maxAttempts);

    [Fact]
    public async Task H1_an_unresolved_FAILED_message_makes_ready_degraded_not_unready()
    {
        // Drive a message to FAILED/ACTIVE through the REAL processor: max_attempts = 1, claim
        // once, then a retryable failure exhausts the budget on the very first attempt.
        await using (var seedContext = _fixture.CreateContext())
        {
            var message = SeedMessage(maxAttempts: 1, now: DateTimeOffset.UtcNow);
            seedContext.OutboxMessages.Add(message);
            await seedContext.SaveChangesAsync();

            var processor = new OutboxProcessor(seedContext, new SystemClock());
            var claimed = await processor.ClaimBatchAsync(batchSize: 1, workerId: "health-test-worker");
            claimed.Should().HaveCount(1);
            await processor.FailRetryableAsync(claimed[0].MessageId, claimed[0].ProcessingToken, "boom", TimeSpan.FromMinutes(1));
        }

        await using (var verifyContext = _fixture.CreateContext())
        {
            var message = await verifyContext.OutboxMessages.SingleAsync();
            message.Status.Should().Be(OutboxStatus.Failed);
            message.FailureDispositionValue.Should().Be(FailureDisposition.Active);
        }

        var client = new AuthTestClient(_factory.CreateHttpsClient());
        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "H-1: a business failure must return 200, never 503");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("degraded");
    }

    [Fact]
    public async Task H2_an_eligible_message_stalled_past_the_threshold_makes_ready_unhealthy()
    {
        await using var context = _fixture.CreateContext();
        // Eligible (Pending, budget remaining) but its available_at has been due for 31 minutes
        // — past the health check's 30-minute stall threshold.
        var message = SeedMessage(maxAttempts: 5, now: DateTimeOffset.UtcNow.AddMinutes(-31));
        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync();

        var client = new AuthTestClient(_factory.CreateHttpsClient());
        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "H-2: nothing is draining an eligible message stuck past the threshold");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("unhealthy");
    }

    [Fact]
    public async Task H2a_a_message_scheduled_for_the_future_backoff_does_not_cause_a_stall()
    {
        await using var seedContext = _fixture.CreateContext();
        var message = SeedMessage(maxAttempts: 5, now: DateTimeOffset.UtcNow);
        seedContext.OutboxMessages.Add(message);
        await seedContext.SaveChangesAsync();

        // Drive it through a real retryable failure with a 1-hour backoff — available_at moves
        // an hour into the future; budget still has 4 attempts remaining, so it stays PENDING.
        var processor = new OutboxProcessor(seedContext, new SystemClock());
        var claimed = await processor.ClaimBatchAsync(batchSize: 1, workerId: "health-test-worker");
        await processor.FailRetryableAsync(claimed[0].MessageId, claimed[0].ProcessingToken, "transient", TimeSpan.FromHours(1));

        var client = new AuthTestClient(_factory.CreateHttpsClient());
        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "H-2a: a message serving its backoff is the system working as designed, never a stall");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("healthy");
    }

    [Fact]
    public async Task H3_the_anonymous_ready_probe_body_is_a_status_word_only()
    {
        var client = new AuthTestClient(_factory.CreateHttpsClient());
        var response = await client.GetAsync("/health/ready");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().BeOneOf("healthy", "degraded", "unhealthy");
        body.Should().NotContain("outbox", "H-3: no component name may leak to an anonymous probe");
        body.Should().NotContain("database");
        body.Should().NotContain("{", "the body must be a bare word, never a JSON structure with counts or error text");
    }

    private async Task<(string Email, string Password)> CreateUserInRoleAsync(string role)
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

        return (email, password);
    }

    private async Task<AuthTestClient> LoggedInAsAsync(string role)
    {
        var (email, password) = await CreateUserInRoleAsync(role);
        var client = new AuthTestClient(_factory.CreateHttpsClient());
        await client.EnsureCsrfCookieAsync();
        var login = await client.PostAsync("/api/auth/login", new LoginRequest(email, password));
        login.StatusCode.Should().Be(HttpStatusCode.NoContent);
        return client;
    }

    [Fact]
    public async Task H4_the_detailed_health_endpoint_is_forbidden_for_Operator_and_Viewer()
    {
        var operatorClient = await LoggedInAsAsync(Roles.Operator);
        (await operatorClient.GetAsync("/api/platform/health")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var viewerClient = await LoggedInAsAsync(Roles.Viewer);
        (await viewerClient.GetAsync("/api/platform/health")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task H4_the_detailed_health_endpoint_returns_200_with_the_breakdown_for_Owner()
    {
        var ownerClient = await LoggedInAsAsync(Roles.Owner);
        var response = await ownerClient.GetAsync("/api/platform/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("outbox", "the Owner-only endpoint DOES expose per-component detail, unlike /health/ready");
    }
}
