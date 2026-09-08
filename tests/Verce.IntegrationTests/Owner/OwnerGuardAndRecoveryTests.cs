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
/// D-9, D-10, D-14, D-15 (ROADMAP S1 catalogue, ADR-0009 §9-§10) against the REAL
/// <see cref="OwnerGuard"/> and <see cref="OwnerBootstrapService"/>, a real advisory lock and a
/// real PostgreSQL transaction.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OwnerGuardAndRecoveryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    public OwnerGuardAndRecoveryTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE platform.account_setup_token, platform.user_role, platform.user_claim,
                           platform.user_login, platform.user_token, platform.audit_log, platform."user"
            RESTART IDENTITY CASCADE;
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static UserManager<ApplicationUser> CreateUserManager(VerceDbContext context) =>
        new(new UserStore<ApplicationUser, ApplicationRole, VerceDbContext, Guid>(context),
            Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(), Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(), new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), null!, new Microsoft.Extensions.Logging.Abstractions.NullLogger<UserManager<ApplicationUser>>());

    private static RoleManager<ApplicationRole> CreateRoleManager(VerceDbContext context) =>
        new(new RoleStore<ApplicationRole, VerceDbContext, Guid>(context),
            Array.Empty<IRoleValidator<ApplicationRole>>(), new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), new Microsoft.Extensions.Logging.Abstractions.NullLogger<RoleManager<ApplicationRole>>());

    /// <summary>Test-only direct creation of an ACTIVE Owner, bypassing the CLI-only bootstrap
    /// flow — legitimate here because D-9/D-15 need two pre-existing Owners as GIVEN state, not
    /// because production may ever create an Owner this way (D-12 forbids that).</summary>
    private static async Task<Guid> CreateActiveOwnerDirectlyAsync(VerceDbContext context, string email)
    {
        var roleManager = CreateRoleManager(context);
        if (!await roleManager.RoleExistsAsync(Roles.Owner))
            await roleManager.CreateAsync(new ApplicationRole(Roles.Owner));

        var userManager = CreateUserManager(context);
        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            DisplayName = email,
            IsActive = true,
            SetupStatus = SetupStatus.Active,
            SetupCompletedAt = DateTimeOffset.UtcNow,
        };
        await userManager.CreateAsync(user, "a-perfectly-fine-12char-password");
        await userManager.AddToRoleAsync(user, Roles.Owner);
        return user.Id;
    }

    [Fact]
    public async Task D9_concurrently_removing_the_only_two_active_owners_never_reaches_zero()
    {
        Guid ownerAId, ownerBId;
        await using (var setup = _fixture.CreateContext())
        {
            ownerAId = await CreateActiveOwnerDirectlyAsync(setup, "d9-a@example.com");
            ownerBId = await CreateActiveOwnerDirectlyAsync(setup, "d9-b@example.com");
        }

        await using var contextA = _fixture.CreateContext();
        await using var contextB = _fixture.CreateContext();
        var guardA = new OwnerGuard(contextA);
        var guardB = new OwnerGuard(contextB);

        async Task RemoveOwnerRoleAsync(OwnerGuard guard, VerceDbContext context, Guid userId) =>
            await guard.ExecuteGuardedAsync(async () =>
            {
                var role = await context.Roles.SingleAsync(r => r.Name == Roles.Owner);
                var userRole = await context.UserRoles.SingleAsync(ur => ur.UserId == userId && ur.RoleId == role.Id);
                context.UserRoles.Remove(userRole);
            });

        var taskA = SafeRun(() => RemoveOwnerRoleAsync(guardA, contextA, ownerAId));
        var taskB = SafeRun(() => RemoveOwnerRoleAsync(guardB, contextB, ownerBId));
        var results = await Task.WhenAll(taskA, taskB);

        results.Count(succeeded => succeeded).Should().Be(1, "exactly one concurrent removal may win");
        results.Count(succeeded => !succeeded).Should().Be(1, "the other must be rejected with LAST_OWNER_PROTECTED");

        await using var verify = _fixture.CreateContext();
        var ownerRoleId = await verify.Roles.Where(r => r.Name == Roles.Owner).Select(r => r.Id).SingleAsync();
        var remainingOwners = await verify.UserRoles.CountAsync(ur => ur.RoleId == ownerRoleId);
        remainingOwners.Should().Be(1, "at least one active Owner must always remain");
    }

    private static async Task<bool> SafeRun(Func<Task> action)
    {
        try { await action(); return true; }
        catch (LastOwnerProtectedException) { return false; }
    }

    [Fact]
    public async Task D15_the_guard_rejects_removing_the_sole_remaining_owner()
    {
        Guid ownerId;
        await using (var setup = _fixture.CreateContext())
            ownerId = await CreateActiveOwnerDirectlyAsync(setup, "d15@example.com");

        await using var context = _fixture.CreateContext();
        var guard = new OwnerGuard(context);

        var act = async () => await guard.ExecuteGuardedAsync(async () =>
        {
            var role = await context.Roles.SingleAsync(r => r.Name == Roles.Owner);
            var userRole = await context.UserRoles.SingleAsync(ur => ur.UserId == ownerId && ur.RoleId == role.Id);
            context.UserRoles.Remove(userRole);
        });

        await act.Should().ThrowAsync<LastOwnerProtectedException>();

        await using var verify = _fixture.CreateContext();
        var ownerRoleId = await verify.Roles.Where(r => r.Name == Roles.Owner).Select(r => r.Id).SingleAsync();
        (await verify.UserRoles.AnyAsync(ur => ur.UserId == ownerId && ur.RoleId == ownerRoleId)).Should().BeTrue(
            "the rejected mutation must roll back completely — the sole Owner keeps the role");
    }

    private static OwnerBootstrapService CreateBootstrapService(VerceDbContext context) =>
        new(context, CreateUserManager(context), CreateRoleManager(context), new SystemClock());

    [Fact]
    public async Task D10_recovering_again_invalidates_the_previous_outstanding_recovery_token()
    {
        await using var setup = _fixture.CreateContext();
        var setupService = CreateBootstrapService(setup);
        var bootstrap = await setupService.BootstrapOwnerAsync("d10@example.com", "D10 Test");

        await using var context = _fixture.CreateContext();
        var service = CreateBootstrapService(context);

        var firstRecovery = await service.RecoverOwnerAsync("d10@example.com");
        var secondRecovery = await service.RecoverOwnerAsync("d10@example.com");

        firstRecovery.Outcome.Should().Be(RecoveryOutcome.Recovered);
        secondRecovery.Outcome.Should().Be(RecoveryOutcome.Recovered);
        firstRecovery.RawSetupToken.Should().NotBe(secondRecovery.RawSetupToken);

        await using var verify = _fixture.CreateContext();
        var tokens = await verify.AccountSetupTokens
            .Where(t => t.UserId == bootstrap.UserId && t.Purpose == AccountSetupTokenPurpose.Recovery)
            .ToListAsync();

        var firstHash = SecretChallenge.HashToken(firstRecovery.RawSetupToken!);
        var secondHash = SecretChallenge.HashToken(secondRecovery.RawSetupToken!);

        tokens.Single(t => t.TokenHash == firstHash).InvalidatedAt.Should().NotBeNull("the older outstanding token must be invalidated");
        tokens.Single(t => t.TokenHash == secondHash).InvalidatedAt.Should().BeNull("only the newest recovery token remains valid");
        tokens.Count(t => t.IsUsable(DateTimeOffset.UtcNow)).Should().Be(1, "exactly one valid recovery token must exist — the newest");
    }

    [Fact]
    public async Task D14_recovering_the_sole_owner_preserves_the_Owner_role_end_to_end()
    {
        await using var setup = _fixture.CreateContext();
        var setupService = CreateBootstrapService(setup);
        var bootstrap = await setupService.BootstrapOwnerAsync("d14@example.com", "D14 Test");
        var initialSetup = await setupService.ConsumeSetupTokenAsync(bootstrap.RawSetupToken!, "first-password-12ch");
        initialSetup.Should().BeTrue();

        // Recovery: the Owner role is preserved, setup_status flips back to PendingSetup, a
        // fresh recovery token is issued, and every prior session/token dies.
        await using var recoverContext = _fixture.CreateContext();
        var recoverService = CreateBootstrapService(recoverContext);
        var recovery = await recoverService.RecoverOwnerAsync("d14@example.com");
        recovery.Outcome.Should().Be(RecoveryOutcome.Recovered);

        await using (var midCheck = _fixture.CreateContext())
        {
            var user = await midCheck.Users.SingleAsync(u => u.Id == bootstrap.UserId);
            user.SetupStatus.Should().Be(SetupStatus.PendingSetup);
            user.RecoveryStartedAt.Should().NotBeNull();

            var ownerRoleId = await midCheck.Roles.Where(r => r.Name == Roles.Owner).Select(r => r.Id).SingleAsync();
            (await midCheck.UserRoles.AnyAsync(ur => ur.UserId == bootstrap.UserId && ur.RoleId == ownerRoleId)).Should().BeTrue(
                "D-14: the Owner role is PRESERVED throughout recovery — never removed");
        }

        // Consuming the new recovery token returns the account to Active — one active Owner again.
        await using var consumeContext = _fixture.CreateContext();
        var consumeService = CreateBootstrapService(consumeContext);
        var consumed = await consumeService.ConsumeSetupTokenAsync(recovery.RawSetupToken!, "second-password-12ch");
        consumed.Should().BeTrue();

        await using var verify = _fixture.CreateContext();
        var finalUser = await verify.Users.SingleAsync(u => u.Id == bootstrap.UserId);
        finalUser.SetupStatus.Should().Be(SetupStatus.Active);
        finalUser.RecoveryStartedAt.Should().BeNull();

        var guard = new OwnerGuard(verify);
        (await guard.CountActiveOwnersAsync()).Should().Be(1, "the system is back to exactly one active Owner");
    }
}
