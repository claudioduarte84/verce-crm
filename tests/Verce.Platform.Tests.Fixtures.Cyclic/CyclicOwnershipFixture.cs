using Verce.SharedKernel.Domain;

namespace Verce.Platform.Tests.Fixtures.Cyclic;

/// <summary>
/// Deliberately broken IOwnedBy&lt;&gt; graph (A owned by B, B owned by A — no root, ever),
/// isolated in its OWN assembly so <see cref="Verce.Platform.Ownership.AggregateOwnershipRegistry"/>'s
/// eager, whole-assembly validation cannot see it while validating a well-formed graph in a
/// sibling test (ADR-0011 §2.5, B-7).
/// </summary>
public sealed class CyclicA : Entity, IOwnedBy<CyclicB>
{
    public Guid ParentId { get; }
    public CyclicA(Guid parentId) { ParentId = parentId; }
}

public sealed class CyclicB : Entity, IOwnedBy<CyclicA>
{
    public Guid ParentId { get; }
    public CyclicB(Guid parentId) { ParentId = parentId; }
}
