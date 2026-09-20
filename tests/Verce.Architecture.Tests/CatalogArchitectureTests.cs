using FluentAssertions;
using Verce.Modules.Catalog;

namespace Verce.Architecture.Tests;

/// <summary>S5 mission §54: Catalog must not leak Inventory/Costing internals into its pure
/// aggregate, and must define no cross-module dependency — the same boundary S4's Costing
/// module already enforces.</summary>
public class CatalogArchitectureTests
{
    [Fact]
    public void Catalog_module_does_not_reference_Inventory_or_Costing_module_assemblies()
    {
        var references = typeof(Product).Assembly.GetReferencedAssemblies().Select(item => item.Name).ToArray();
        references.Should().NotContain("Verce.Modules.Inventory",
            "unit normalization/Supply validation happens in the API composition root (ADR-0001 §3.1), never inside the pure Catalog aggregate");
        references.Should().NotContain("Verce.Modules.Costing",
            "Catalog persists recipe DATA; translating it into a CostEngine call is an API-layer concern");
        references.Should().NotContain("Verce.Modules.Pricing");
    }

    [Fact]
    public void Product_and_ProductRecipe_lines_declare_a_single_consistent_ownership_chain()
    {
        typeof(ProductRecipe).Should().Implement<Verce.SharedKernel.Domain.IOwnedBy<Product>>();
        typeof(ProductRecipeMaterialLine).Should().Implement<Verce.SharedKernel.Domain.IOwnedBy<ProductRecipe>>();
        typeof(ProductRecipeAdditionalCostLine).Should().Implement<Verce.SharedKernel.Domain.IOwnedBy<ProductRecipe>>();
        typeof(Product).Should().Implement<Verce.SharedKernel.Domain.IAggregateRoot>();
    }
}
