using FluentAssertions;
using Verce.Platform.Ownership;
using Verce.SharedKernel.Domain;

namespace Verce.Platform.Tests;

/// <summary>
/// ADR-0011 §2.5: an <see cref="IOwnedBy{TParent}"/> chain must resolve, by reflection alone
/// (no naming convention), to exactly one <see cref="IAggregateRoot"/>, with no cycles and no
/// ambiguity — validated once at startup so a broken chain fails loudly before any request
/// touches it (B-7).
///
/// <see cref="AggregateOwnershipRegistry.BuildAndValidate"/> eagerly validates EVERY
/// IOwnedBy&lt;&gt; chain found anywhere in the scanned assemblies — not just the type the
/// caller is interested in. The deliberately-broken graphs below therefore live in their own
/// tiny sibling assemblies (Verce.Platform.Tests.Fixtures.Cyclic / .Orphan) so validating one
/// broken graph can never contaminate a test asserting on a well-formed one, or on the other
/// broken graph.
/// </summary>
public class AggregateOwnershipRegistryTests
{
    // --- well-formed graph: Root -> Child -> Grandchild (the only IOwnedBy graph in THIS assembly) ---
    private sealed class WellFormedRoot : AggregateRoot
    {
        public WellFormedRoot() { }
    }

    private sealed class WellFormedChild : Entity, IOwnedBy<WellFormedRoot>
    {
        public Guid ParentId { get; }
        public WellFormedChild(Guid parentId) { ParentId = parentId; }
    }

    private sealed class WellFormedGrandchild : Entity, IOwnedBy<WellFormedChild>
    {
        public Guid ParentId { get; }
        public WellFormedGrandchild(Guid parentId) { ParentId = parentId; }
    }

    private static AggregateOwnershipRegistry BuildFor(params Type[] types) =>
        AggregateOwnershipRegistry.BuildAndValidate(types.Select(t => t.Assembly).Distinct());

    [Fact]
    public void Direct_child_resolves_to_its_declared_root()
    {
        var registry = BuildFor(typeof(WellFormedRoot), typeof(WellFormedChild));

        registry.ResolveRoot(typeof(WellFormedChild)).Should().Be(typeof(WellFormedRoot));
    }

    [Fact]
    public void A_root_resolves_to_itself()
    {
        var registry = BuildFor(typeof(WellFormedRoot));

        registry.ResolveRoot(typeof(WellFormedRoot)).Should().Be(typeof(WellFormedRoot));
    }

    [Fact]
    public void Nested_grandchild_resolves_through_its_parent_to_the_ultimate_root()
    {
        var registry = BuildFor(typeof(WellFormedRoot), typeof(WellFormedChild), typeof(WellFormedGrandchild));

        registry.ResolveRoot(typeof(WellFormedGrandchild)).Should().Be(typeof(WellFormedRoot),
            "B-6: a grandchild declares only its IMMEDIATE parent — the registry walks the rest of the chain");
    }

    [Fact]
    public void IsTracked_is_true_for_roots_and_owned_children_false_for_unrelated_types()
    {
        var registry = BuildFor(typeof(WellFormedRoot), typeof(WellFormedChild));

        registry.IsTracked(typeof(WellFormedRoot)).Should().BeTrue();
        registry.IsTracked(typeof(WellFormedChild)).Should().BeTrue();
        registry.IsTracked(typeof(string)).Should().BeFalse();
    }

    [Fact]
    public void A_chain_that_never_reaches_an_IAggregateRoot_fails_validation_at_build_time()
    {
        // B-7: OrphanParent (in the isolated Fixtures.Orphan assembly) is a plain class, not an
        // IAggregateRoot — the chain dead-ends.
        var act = () => AggregateOwnershipRegistry.BuildAndValidate(
            new[] { typeof(Fixtures.Orphan.OrphanChild).Assembly });

        // The chain breaks at OrphanParent (no IAggregateRoot, no IOwnedBy<> declaration) —
        // that is the type the exception names, not the child that started the walk.
        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("does not implement IAggregateRoot");
        exception.Message.Should().Contain(nameof(Fixtures.Orphan.OrphanParent));
    }

    [Fact]
    public void A_cyclic_ownership_chain_fails_validation_at_build_time()
    {
        // B-7: CyclicA owned by CyclicB, CyclicB owned by CyclicA (Fixtures.Cyclic) — no root, ever.
        var act = () => AggregateOwnershipRegistry.BuildAndValidate(
            new[] { typeof(Fixtures.Cyclic.CyclicA).Assembly });

        act.Should().Throw<InvalidOperationException>().WithMessage("*Cyclic ownership chain*");
    }

    [Fact]
    public void An_empty_assembly_set_still_recognizes_a_type_as_a_root_by_the_IAggregateRoot_interface_alone()
    {
        // IsTracked's root branch checks the CLR interface directly — it never depends on
        // what was scanned. Only the "owned child" branch depends on registry contents.
        var registry = AggregateOwnershipRegistry.BuildAndValidate(Enumerable.Empty<System.Reflection.Assembly>());
        registry.IsTracked(typeof(WellFormedRoot)).Should().BeTrue();
    }

    [Fact]
    public void An_empty_assembly_set_never_recognizes_an_owned_child_it_never_scanned()
    {
        var registry = AggregateOwnershipRegistry.BuildAndValidate(Enumerable.Empty<System.Reflection.Assembly>());
        registry.IsTracked(typeof(WellFormedChild)).Should().BeFalse("no IOwnedBy<> declaration was ever scanned into this registry");
    }
}
