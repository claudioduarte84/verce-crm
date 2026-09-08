using Verce.SharedKernel.Domain;

namespace Verce.Platform.Tests.Fixtures.Orphan;

/// <summary>
/// Deliberately broken IOwnedBy&lt;&gt; graph (the chain dead-ends at a plain class, never
/// reaching an IAggregateRoot), isolated in its OWN assembly so
/// <see cref="Verce.Platform.Ownership.AggregateOwnershipRegistry"/>'s eager, whole-assembly
/// validation cannot see it while validating a well-formed graph in a sibling test
/// (ADR-0011 §2.5, B-7).
/// </summary>
public sealed class OrphanParent
{
}

public sealed class OrphanChild : Entity, IOwnedBy<OrphanParent>
{
    public Guid ParentId { get; }
    public OrphanChild(Guid parentId) { ParentId = parentId; }
}
