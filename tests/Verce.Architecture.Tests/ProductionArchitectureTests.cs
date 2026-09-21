using FluentAssertions;
using Verce.Modules.Production;
using Verce.SharedKernel.Domain;

namespace Verce.Architecture.Tests;

/// <summary>ADR-0020 §A.8: Production may depend on Quoting's Contracts (the event types) but
/// must NEVER reference the Quoting module itself (Quote/QuoteRevision/QuoteItem).</summary>
public class ProductionArchitectureTests
{
    [Fact]
    public void Production_module_references_Quoting_Contracts_but_never_the_Quoting_module_itself()
    {
        var references = typeof(ProductionOrder).Assembly.GetReferencedAssemblies().Select(item => item.Name).ToArray();
        references.Should().NotContain("Verce.Modules.Quoting");
        references.Should().Contain("Verce.Modules.Quoting.Contracts",
            "the QuoteApproved/-Revised/-Canceled/-Expired handlers need the event TYPES, never the aggregates");
    }

    [Fact]
    public void ProductionOrder_is_its_own_aggregate_root_never_owned_by_anything()
    {
        typeof(ProductionOrder).Should().Implement<IAggregateRoot>();
    }
}
