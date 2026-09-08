using Verce.SharedKernel.Domain;

namespace Verce.Platform.Identity;

public enum AccountSetupTokenPurpose
{
    Bootstrap,
    Reset,
    Recovery,
}

/// <summary>
/// Single-use, hashed setup/reset/recovery token (ADR-0009 §7). Only the SHA-256 hash is
/// persisted; the raw token is shown once at issue and never stored. An unsalted hash is
/// sufficient because the token is 256 bits of uniform randomness (ADR-0009 §7.3) — this
/// reasoning does NOT transfer to passwords, which use Identity's password hasher.
///
/// Domain entity (Category 1, ADR-0011 §1): has independent lifecycle, application-managed.
/// </summary>
public sealed class AccountSetupToken : Entity, IDomainEntity
{
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public AccountSetupTokenPurpose Purpose { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }
    public DateTimeOffset? InvalidatedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBySource { get; private set; } = string.Empty;
    public Guid? CreatedByUserId { get; private set; }

    private AccountSetupToken() { } // EF materialization

    public AccountSetupToken(
        Guid userId,
        string tokenHash,
        AccountSetupTokenPurpose purpose,
        DateTimeOffset createdAt,
        TimeSpan validFor,
        string createdBySource,
        Guid? createdByUserId)
    {
        Id = Guid.CreateVersion7();
        UserId = userId;
        TokenHash = tokenHash;
        Purpose = purpose;
        CreatedAt = createdAt;
        ExpiresAt = createdAt + validFor;
        CreatedBySource = createdBySource;
        CreatedByUserId = createdByUserId;
    }

    /// <summary>Marks the token consumed. Callers must have already performed the atomic
    /// conditional-update check (ADR-0009 §7.2) before calling this on a loaded/locked row.</summary>
    public void MarkConsumed(DateTimeOffset consumedAt) => ConsumedAt = consumedAt;

    /// <summary>Marks the token superseded by a newer one, without a human having used it.</summary>
    public void Invalidate(DateTimeOffset invalidatedAt) => InvalidatedAt = invalidatedAt;

    public bool IsUsable(DateTimeOffset now) =>
        ConsumedAt is null && InvalidatedAt is null && now < ExpiresAt;
}
