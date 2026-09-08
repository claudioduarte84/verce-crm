using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Verce.Api.Auth;
using Verce.Platform.Cli;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Auth;

/// <summary>
/// D-6, D-7, D-8, D-11 (ROADMAP S1 catalogue) exercised through the REAL HTTP host — the login,
/// logout, session and setup-account endpoints introduced at this checkpoint (previously there
/// was no authentication scheme registered at all; see the S1 FINAL REPORT gap history).
/// </summary>
[Collection(PostgresCollection.Name)]
public class AuthEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;

    public AuthEndpointTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE platform.account_setup_token,
                           platform.user_role,
                           platform.user_claim,
                           platform.user_login,
                           platform.user_token,
                           platform.audit_log,
                           platform."user"
            RESTART IDENTITY CASCADE;
            """);
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static OwnerBootstrapService CreateBootstrapService(VerceDbContext context)
    {
        var userStore = new UserStore<ApplicationUser, ApplicationRole, VerceDbContext, Guid>(context);
        var identityOptions = Microsoft.Extensions.Options.Options.Create(new IdentityOptions());
        var userManager = new UserManager<ApplicationUser>(
            userStore, identityOptions, new PasswordHasher<ApplicationUser>(), Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(), new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), null!, new Microsoft.Extensions.Logging.Abstractions.NullLogger<UserManager<ApplicationUser>>());

        var roleStore = new RoleStore<ApplicationRole, VerceDbContext, Guid>(context);
        var roleManager = new RoleManager<ApplicationRole>(
            roleStore, Array.Empty<IRoleValidator<ApplicationRole>>(), new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), new Microsoft.Extensions.Logging.Abstractions.NullLogger<RoleManager<ApplicationRole>>());

        return new OwnerBootstrapService(context, userManager, roleManager, new SystemClock());
    }

    /// <summary>Bootstraps a fresh Owner and activates it via the REAL setup-account endpoint,
    /// returning credentials a test can log in with.</summary>
    private async Task<(string Email, string Password, Guid UserId)> CreateActiveOwnerAsync(AuthTestClient client)
    {
        const string email = "owner@example.com";
        const string password = "a-perfectly-fine-12char-password";

        await using var context = _fixture.CreateContext();
        var service = CreateBootstrapService(context);
        var bootstrap = await service.BootstrapOwnerAsync(email, "Test Owner");
        bootstrap.Outcome.Should().Be(BootstrapOutcome.Created);

        await client.EnsureCsrfCookieAsync();
        var setupResponse = await client.PostAsync("/api/auth/setup-account", new SetupAccountRequest(bootstrap.RawSetupToken!, password));
        setupResponse.StatusCode.Should().Be(HttpStatusCode.NoContent, "the setup-account endpoint must activate the freshly-bootstrapped account");

        return (email, password, bootstrap.UserId!.Value);
    }

    [Fact]
    public async Task Login_with_correct_credentials_establishes_a_session_reflected_by_GET_session()
    {
        var client = new AuthTestClient(_factory.CreateHttpsClient());
        var (email, password, _) = await CreateActiveOwnerAsync(client);

        await client.EnsureCsrfCookieAsync();
        var loginResponse = await client.PostAsync("/api/auth/login", new LoginRequest(email, password));
        loginResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var sessionResponse = await client.GetAsync("/api/auth/session");
        sessionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var session = await sessionResponse.Content.ReadFromJsonAsync<SessionResponse>();
        session!.Email.Should().Be(email);
        session.Roles.Should().Contain(Roles.Owner);
    }

    [Fact]
    public async Task GET_session_is_401_for_an_anonymous_caller()
    {
        var client = new AuthTestClient(_factory.CreateHttpsClient());
        var response = await client.GetAsync("/api/auth/session");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_state_changing_request_without_a_csrf_token_is_rejected()
    {
        // Regression net (mission §54): CSRF protection is not just "GET /csrf works" — a POST
        // that never fetched the token at all must be refused, never silently accepted.
        var client = new AuthTestClient(_factory.CreateHttpsClient());
        var (email, password, _) = await CreateActiveOwnerAsync(client);

        var freshClient = new AuthTestClient(_factory.CreateHttpsClient()); // never called EnsureCsrfCookieAsync
        var response = await freshClient.PostAsync("/api/auth/login", new LoginRequest(email, password));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "no antiforgery cookie/header pair was ever established for this client");
    }

    [Fact]
    public async Task Login_with_a_wrong_password_is_rejected_with_a_generic_message()
    {
        var client = new AuthTestClient(_factory.CreateHttpsClient());
        var (email, _, _) = await CreateActiveOwnerAsync(client);

        await client.EnsureCsrfCookieAsync();
        var response = await client.PostAsync("/api/auth/login", new LoginRequest(email, "definitely-the-wrong-password"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Credenciais inválidas");
    }

    [Fact]
    public async Task D8_a_PendingSetup_account_cannot_log_in_even_with_the_correct_password_and_the_rejection_is_generic()
    {
        var client = new AuthTestClient(_factory.CreateHttpsClient());
        var (email, password, userId) = await CreateActiveOwnerAsync(client);

        // Force the account back to PendingSetup, simulating an in-flight recovery, without
        // touching the password hash — proves the gate fires on setup_status alone.
        await using (var context = _fixture.CreateContext())
        {
            var user = await context.Users.SingleAsync(u => u.Id == userId);
            user.SetupStatus = SetupStatus.PendingSetup;
            await context.SaveChangesAsync();
        }

        await client.EnsureCsrfCookieAsync();
        var response = await client.PostAsync("/api/auth/login", new LoginRequest(email, password));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Credenciais inválidas", "D-8: identical to a wrong-password rejection, revealing nothing about setup_status");
    }

    [Fact]
    public async Task D7_a_consumed_setup_token_cannot_be_used_again()
    {
        await using var context = _fixture.CreateContext();
        var service = CreateBootstrapService(context);
        var bootstrap = await service.BootstrapOwnerAsync("reuse@example.com", "Reuse Test");

        var client = new AuthTestClient(_factory.CreateHttpsClient());
        await client.EnsureCsrfCookieAsync();

        var first = await client.PostAsync("/api/auth/setup-account", new SetupAccountRequest(bootstrap.RawSetupToken!, "first-password-12ch"));
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var second = await client.PostAsync("/api/auth/setup-account", new SetupAccountRequest(bootstrap.RawSetupToken!, "second-password-12ch"));
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest, "D-7: a consumed token must never succeed twice");
    }

    [Fact]
    public async Task D7_an_expired_setup_token_is_rejected_identically_to_a_consumed_one()
    {
        await using var context = _fixture.CreateContext();
        var service = CreateBootstrapService(context);
        var bootstrap = await service.BootstrapOwnerAsync("expired@example.com", "Expired Test");

        // Backdate expires_at directly — the only way to observe "expired" deterministically
        // without waiting 30 real minutes.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE platform.account_setup_token SET expires_at = now() - interval '1 minute' WHERE user_id = {bootstrap.UserId}");

        var client = new AuthTestClient(_factory.CreateHttpsClient());
        await client.EnsureCsrfCookieAsync();

        var response = await client.PostAsync("/api/auth/setup-account", new SetupAccountRequest(bootstrap.RawSetupToken!, "some-password-12ch"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("inválido, expirado ou já utilizado");
    }

    [Fact]
    public async Task D6_two_concurrent_setup_account_requests_with_the_same_token_yield_exactly_one_success()
    {
        await using var context = _fixture.CreateContext();
        var service = CreateBootstrapService(context);
        var bootstrap = await service.BootstrapOwnerAsync("concurrent-setup@example.com", "Concurrent Setup");

        var clientA = new AuthTestClient(_factory.CreateHttpsClient());
        var clientB = new AuthTestClient(_factory.CreateHttpsClient());
        await clientA.EnsureCsrfCookieAsync();
        await clientB.EnsureCsrfCookieAsync();

        var taskA = clientA.PostAsync("/api/auth/setup-account", new SetupAccountRequest(bootstrap.RawSetupToken!, "password-race-12ch-a"));
        var taskB = clientB.PostAsync("/api/auth/setup-account", new SetupAccountRequest(bootstrap.RawSetupToken!, "password-race-12ch-b"));
        var responses = await Task.WhenAll(taskA, taskB);

        responses.Count(r => r.StatusCode == HttpStatusCode.NoContent).Should().Be(1, "D-6: exactly one concurrent consumer may win");
        responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest).Should().Be(1);
    }

    [Fact]
    public async Task Logout_ends_the_session_so_a_subsequent_session_check_is_401()
    {
        var client = new AuthTestClient(_factory.CreateHttpsClient());
        var (email, password, _) = await CreateActiveOwnerAsync(client);

        await client.EnsureCsrfCookieAsync();
        await client.PostAsync("/api/auth/login", new LoginRequest(email, password));
        (await client.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.OK);

        // The antiforgery token embeds the caller's identity at mint time — login just changed
        // it from anonymous to authenticated, so a token fetched BEFORE login is no longer
        // valid afterward and must be refreshed (mirrors what the real SPA must also do).
        await client.EnsureCsrfCookieAsync();
        var logoutResponse = await client.PostAsync<object?>("/api/auth/logout", null);
        logoutResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task D11_a_SecurityStamp_change_invalidates_an_already_issued_session_cookie()
    {
        var client = new AuthTestClient(_factory.CreateHttpsClient());
        var (email, password, userId) = await CreateActiveOwnerAsync(client);

        await client.EnsureCsrfCookieAsync();
        await client.PostAsync("/api/auth/login", new LoginRequest(email, password));
        (await client.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Simulate a password reset/recovery happening elsewhere, invalidating every existing
        // session by regenerating the SecurityStamp — mirrors what RecoverOwnerAsync does. The
        // factory configures SecurityStampValidatorOptions.ValidationInterval = TimeSpan.Zero
        // so this takes effect on the very next request, not after a real 30-minute wait.
        await using (var context = _fixture.CreateContext())
        {
            var userStore = new UserStore<ApplicationUser, ApplicationRole, VerceDbContext, Guid>(context);
            var userManager = new UserManager<ApplicationUser>(
                userStore, Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
                new PasswordHasher<ApplicationUser>(), Array.Empty<IUserValidator<ApplicationUser>>(),
                Array.Empty<IPasswordValidator<ApplicationUser>>(), new UpperInvariantLookupNormalizer(),
                new IdentityErrorDescriber(), null!, new Microsoft.Extensions.Logging.Abstractions.NullLogger<UserManager<ApplicationUser>>());
            var user = await userManager.FindByIdAsync(userId.ToString());
            await userManager.UpdateSecurityStampAsync(user!);
        }

        // The SAME session cookie captured at login is still sent — its claims are unchanged —
        // but SecurityStampValidator must now reject it on revalidation.
        var response = await client.GetAsync("/api/auth/session");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "D-11: a SecurityStamp change must invalidate an already-issued cookie without waiting for its natural expiry");
    }
}
