using FluentAssertions;
using Verce.Platform.Identity;

namespace Verce.Platform.Tests.Identity;

/// <summary>
/// Pure state logic of <see cref="AccountSetupToken.IsUsable"/> (ADR-0009 §7) — the single-use,
/// expiring token predicate behind D-7 (`SetupTokenSingleUseAndExpiry`). The atomic
/// claim-then-consume race itself (D-6) requires a real database and is covered by
/// Verce.IntegrationTests; this protects the predicate's own boundary conditions.
/// </summary>
public class AccountSetupTokenTests
{
    private static AccountSetupToken NewToken(DateTimeOffset createdAt, TimeSpan validFor) =>
        new(
            userId: Guid.CreateVersion7(),
            tokenHash: "deadbeef",
            purpose: AccountSetupTokenPurpose.Bootstrap,
            createdAt: createdAt,
            validFor: validFor,
            createdBySource: "cli",
            createdByUserId: null);

    [Fact]
    public void ExpiresAt_is_CreatedAt_plus_ValidFor()
    {
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var token = NewToken(createdAt, TimeSpan.FromHours(24));

        token.ExpiresAt.Should().Be(createdAt.AddHours(24));
    }

    [Fact]
    public void A_fresh_unconsumed_unexpired_token_is_usable()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var token = NewToken(createdAt, TimeSpan.FromHours(24));

        token.IsUsable(createdAt.AddHours(1)).Should().BeTrue();
    }

    [Fact]
    public void A_consumed_token_is_never_usable_again()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var token = NewToken(createdAt, TimeSpan.FromHours(24));

        token.MarkConsumed(createdAt.AddMinutes(5));

        token.IsUsable(createdAt.AddMinutes(10)).Should().BeFalse("D-7: a consumed token is single-use");
    }

    [Fact]
    public void An_invalidated_token_is_never_usable_even_before_expiry()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var token = NewToken(createdAt, TimeSpan.FromHours(24));

        token.Invalidate(createdAt.AddMinutes(5));

        token.IsUsable(createdAt.AddMinutes(10)).Should().BeFalse("D-10: superseded by a newer token, without ever being consumed");
    }

    [Fact]
    public void A_token_evaluated_at_or_after_its_expiry_instant_is_not_usable()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var token = NewToken(createdAt, TimeSpan.FromHours(24));

        token.IsUsable(token.ExpiresAt).Should().BeFalse("expiry is exclusive: now < ExpiresAt, never <=");
        token.IsUsable(token.ExpiresAt.AddSeconds(-1)).Should().BeTrue();
    }

    [Fact]
    public void Purpose_and_actor_are_recorded_exactly_as_constructed()
    {
        var actorId = Guid.CreateVersion7();
        var token = new AccountSetupToken(
            userId: Guid.CreateVersion7(), tokenHash: "abc123",
            purpose: AccountSetupTokenPurpose.Recovery,
            createdAt: DateTimeOffset.UtcNow, validFor: TimeSpan.FromHours(1),
            createdBySource: "recover-owner-cli", createdByUserId: actorId);

        token.Purpose.Should().Be(AccountSetupTokenPurpose.Recovery);
        token.CreatedBySource.Should().Be("recover-owner-cli");
        token.CreatedByUserId.Should().Be(actorId);
    }
}
