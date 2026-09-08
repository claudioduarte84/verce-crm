using Verce.SharedKernel.Events;

namespace Verce.SharedKernel.Domain;

/// <summary>
/// Base type for every application-owned domain entity. PK is UUID v7, generated on
/// construction (ADR-0011 §1) so the id is available before SaveChanges — which the domain
/// event and outbox flows depend on.
/// </summary>
public abstract class Entity : IDomainEntity, IEquatable<Entity>
{
    public Guid Id { get; protected set; }

    protected Entity()
    {
        Id = Guid.CreateVersion7();
    }

    /// <summary>For rehydration (EF materialization) or explicit-id scenarios (tests, seeds).</summary>
    protected Entity(Guid id)
    {
        Id = id;
    }

    public bool Equals(Entity? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (GetType() != other.GetType()) return false;
        return Id == other.Id;
    }

    public override bool Equals(object? obj) => Equals(obj as Entity);
    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    public static bool operator ==(Entity? left, Entity? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(Entity? left, Entity? right) => !(left == right);
}

/// <summary>
/// Base type for a Category 4 (<see cref="ITechnicalTable"/>) application-owned technical
/// table (ADR-0011 §1): same uuid-identity/equality shape as <see cref="Entity"/>, but
/// deliberately does NOT implement <see cref="IDomainEntity"/> — a technical table's PK exists
/// for insertion locality or an algorithm, never for domain identity, and the two markers must
/// stay mutually exclusive (ADR-0011 §1, enforced by Verce.Architecture.Tests).
/// </summary>
public abstract class TechnicalEntity : IEquatable<TechnicalEntity>
{
    public Guid Id { get; protected set; }

    protected TechnicalEntity()
    {
        Id = Guid.CreateVersion7();
    }

    protected TechnicalEntity(Guid id)
    {
        Id = id;
    }

    public bool Equals(TechnicalEntity? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (GetType() != other.GetType()) return false;
        return Id == other.Id;
    }

    public override bool Equals(object? obj) => Equals(obj as TechnicalEntity);
    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    public static bool operator ==(TechnicalEntity? left, TechnicalEntity? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(TechnicalEntity? left, TechnicalEntity? right) => !(left == right);
}

/// <summary>
/// Declares the IMMEDIATE parent of an owned child entity (ADR-0011 §2.5). Nested children
/// declare their immediate parent only; the platform's AggregateOwnershipRegistry walks the
/// chain to the ultimate <see cref="IAggregateRoot"/> at startup. No naming-convention magic.
/// </summary>
/// <typeparam name="TParent">The immediate parent type (may itself be owned by another parent).</typeparam>
public interface IOwnedBy<TParent> where TParent : class
{
    Guid ParentId { get; }
}

/// <summary>
/// Base type for every aggregate root (ADR-0011 §2). Owns the pending-event buffer and the
/// optimistic-concurrency <see cref="Version"/> token.
/// </summary>
public abstract class AggregateRoot : Entity, IAggregateRoot
{
    private readonly List<IEvent> _pending = new();

    /// <summary>
    /// Committed revision number of the aggregate. Starts at 1 and advances by exactly one
    /// per Unit of Work, regardless of how many children changed or how many event waves ran
    /// (ADR-0011 §2.3-2.4). Mutated ONLY by Verce.Platform's AggregateVersionInterceptor —
    /// never by domain/application code.
    /// </summary>
    public long Version { get; private set; } = 1;

    protected AggregateRoot()
    {
    }

    protected AggregateRoot(Guid id) : base(id)
    {
    }

    /// <summary>
    /// Read-only view of events not yet drained. For diagnostics/tests only — the Unit of Work
    /// is the sole legitimate consumer via <see cref="DrainPendingEvents"/>.
    /// </summary>
    public IReadOnlyList<IEvent> PendingEvents => _pending.AsReadOnly();

    /// <summary>Domain behaviour calls this to record that something happened.</summary>
    protected void Raise(IEvent @event) => _pending.Add(@event);

    /// <summary>
    /// Drains and clears the pending buffer atomically, returning a frozen snapshot for this
    /// wave. Only the platform's Unit of Work may call this — never business code, never a
    /// handler, never a test asserting on live state without also verifying the buffer emptied.
    /// </summary>
    internal IReadOnlyList<IEvent> DrainPendingEvents()
    {
        if (_pending.Count == 0) return Array.Empty<IEvent>();
        var drained = _pending.ToArray();
        _pending.Clear();
        return drained;
    }

    /// <summary>
    /// Sets the version to a fresh insert value (1), used only when the platform recognizes
    /// this root as newly Added in the current Unit of Work. Idempotent within one UoW because
    /// the platform's VersionHandledAggregates set ensures it is only called once.
    /// </summary>
    internal void InitializeVersionForInsert() => Version = 1;

    /// <summary>
    /// Advances the version by exactly one. Called once per Unit of Work by the platform's
    /// AggregateVersionInterceptor for an EXISTING root that has at least one mutation in this
    /// UoW (ADR-0011 §2.4). Never called more than once per UoW for the same root.
    /// </summary>
    internal void BumpVersion() => Version++;

    /// <summary>Used by EF Core value comparers / test fixtures that need to force a version.</summary>
    internal void SetVersionForRehydration(long version) => Version = version;
}
