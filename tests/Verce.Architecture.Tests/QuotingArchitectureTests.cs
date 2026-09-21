using FluentAssertions;
using Verce.Modules.Quoting;
using Verce.SharedKernel.Domain;

namespace Verce.Architecture.Tests;

/// <summary>S6 (ADR-0020): Quoting must never reference Production, Catalog, Pricing, Customers
/// or Costing directly — every cross-module read happens in the Verce.Api composition root
/// (mirrors <c>PricingArchitectureTests</c>). The generic
/// <see cref="ModuleBoundaryTests.No_module_assembly_references_another_module_assembly"/> check
/// already covers this over the whole model; these are named explicitly for S6's own regression
/// value.</summary>
public class QuotingArchitectureTests
{
    [Fact]
    public void Quoting_module_does_not_reference_Production_Catalog_Pricing_Customers_or_Costing_module_assemblies()
    {
        var references = typeof(Quote).Assembly.GetReferencedAssemblies().Select(item => item.Name).ToArray();
        references.Should().NotContain("Verce.Modules.Production");
        references.Should().NotContain("Verce.Modules.Catalog");
        references.Should().NotContain("Verce.Modules.Pricing");
        references.Should().NotContain("Verce.Modules.Customers");
        references.Should().NotContain("Verce.Modules.Costing");
    }

    [Fact]
    public void Quoting_domain_events_live_in_a_separate_Contracts_assembly()
    {
        // ADR-0020 §A.6 / CLAUDE.md rule 11: the cross-module contract Production consumes is
        // physically separate from the aggregates Production must never reference.
        typeof(Verce.Modules.Quoting.Contracts.QuoteApprovedEvent).Assembly.Should()
            .NotBeSameAs(typeof(Quote).Assembly);
    }

    [Fact]
    public void PerOrderFeeAllocator_and_QuoteOutcomeCalculator_are_pure_no_infrastructure_dependency()
    {
        var allocatorReferences = typeof(PerOrderFeeAllocator).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        allocatorReferences.Should().NotContain("Microsoft.AspNetCore.Http.Abstractions");

        var allocateMethod = typeof(PerOrderFeeAllocator).GetMethod(nameof(PerOrderFeeAllocator.Allocate));
        allocateMethod.Should().NotBeNull();
        allocateMethod!.GetParameters().Should().HaveCount(2, "a pure calculator takes only its resolved inputs, never a DbContext/IClock");
    }

    [Fact]
    public void Quote_aggregate_and_its_owned_children_declare_a_single_consistent_ownership_chain()
    {
        typeof(Quote).Should().Implement<IAggregateRoot>();
        typeof(QuoteRevision).Should().Implement<IOwnedBy<Quote>>();
        typeof(QuoteItem).Should().Implement<IOwnedBy<QuoteRevision>>();
        typeof(QuoteStatusHistory).Should().Implement<IOwnedBy<QuoteRevision>>();
    }
}
