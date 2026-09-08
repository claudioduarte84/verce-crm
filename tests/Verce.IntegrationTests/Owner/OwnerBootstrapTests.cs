using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Verce.Platform.Cli;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Owner;

/// <summary>
/// D-1, D-9 (ROADMAP S1 catalogue): concurrent bootstrap serializes to exactly one Owner via
/// the advisory lock; the normal API guard never lets the active-Owner population reach zero.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OwnerBootstrapTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    public OwnerBootstrapTests(PostgresFixture fixture) => _fixture = fixture;

    // Owner existence is a global, single-row-spanning concept (bootstrap checks "does ANY
    // Owner exist"), so each test needs a clean Identity slate — the collection shares one
    // Postgres instance across every test class for speed, and without this reset, tests would
    // observe Owners created by tests that happened to run earlier in the same collection.
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
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static OwnerBootstrapService CreateService(VerceDbContext context)
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

    [Fact]
    public async Task D1_concurrent_bootstrap_produces_exactly_one_owner()
    {
        var contextA = _fixture.CreateContext();
        var contextB = _fixture.CreateContext();
        await using var _a = contextA;
        await using var _b = contextB;

        var serviceA = CreateService(contextA);
        var serviceB = CreateService(contextB);

        var taskA = serviceA.BootstrapOwnerAsync("owner-race@example.com", "Owner Race");
        var taskB = serviceB.BootstrapOwnerAsync("owner-race@example.com", "Owner Race");

        var results = await Task.WhenAll(
            SafeRun(taskA),
            SafeRun(taskB));

        var successes = results.Count(r => r.HasValue && r.Value.Outcome == BootstrapOutcome.Created);
        successes.Should().Be(1, "the advisory lock must serialize the two attempts so exactly one Owner is created");

        await using var verifyContext = _fixture.CreateContext();
        var ownerRole = await verifyContext.Roles.FirstAsync(r => r.Name == Roles.Owner);
        var ownerCount = await verifyContext.UserRoles.CountAsync(ur => ur.RoleId == ownerRole.Id);
        ownerCount.Should().Be(1);
    }

    private static async Task<(BootstrapOutcome Outcome, string? RawSetupToken, Guid? UserId)?> SafeRun(
        Task<(BootstrapOutcome Outcome, string? RawSetupToken, Guid? UserId)> task)
    {
        try { return await task; }
        catch { return null; } // the loser may also throw (unique constraint on email) — both outcomes are acceptable
    }

    [Fact]
    public async Task D2_bootstrap_refuses_once_an_owner_already_exists()
    {
        await using var setupContext = _fixture.CreateContext();
        var setupService = CreateService(setupContext);
        var first = await setupService.BootstrapOwnerAsync("first-owner@example.com", "First Owner");
        first.Outcome.Should().Be(BootstrapOutcome.Created);

        await using var secondContext = _fixture.CreateContext();
        var secondService = CreateService(secondContext);
        var second = await secondService.BootstrapOwnerAsync("second-owner@example.com", "Second Owner");

        second.Outcome.Should().Be(BootstrapOutcome.OwnerAlreadyExists);
        second.RawSetupToken.Should().BeNull();
    }

    [Fact]
    public async Task D5_setup_token_is_stored_only_as_a_hash()
    {
        await using var context = _fixture.CreateContext();
        var service = CreateService(context);
        var result = await service.BootstrapOwnerAsync("hash-check@example.com", "Hash Check");

        result.RawSetupToken.Should().NotBeNullOrEmpty();

        await using var verifyContext = _fixture.CreateContext();
        var storedTokens = await verifyContext.AccountSetupTokens
            .Where(t => t.UserId == result.UserId).Select(t => t.TokenHash).ToListAsync();

        storedTokens.Should().NotBeEmpty();
        storedTokens.Should().NotContain(result.RawSetupToken, "the raw token must never be persisted, only its SHA-256 hash");
        storedTokens.Single().Length.Should().Be(64, "SHA-256 in hex is 64 characters");
    }
}
