namespace Verce.SharedKernel.Domain;

/// <summary>
/// An aggregate root: the transactional consistency boundary and the unit of optimistic
/// concurrency (ADR-0011 §2). Always a <see cref="IDomainEntity"/> or <see cref="IMasterData"/>.
/// </summary>
public interface IAggregateRoot : IDomainEntity
{
    long Version { get; }
}
