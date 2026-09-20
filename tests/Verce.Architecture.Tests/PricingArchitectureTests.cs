using FluentAssertions;
using Verce.Modules.Pricing;

namespace Verce.Architecture.Tests;

/// <summary>S5 mission §54: PricingEngine must remain a pure calculator (no DbContext,
/// HttpContext, clock, randomness), and Pricing must define no cross-module dependency.</summary>
public class PricingArchitectureTests
{
    [Fact]
    public void Pricing_module_does_not_reference_Catalog_Inventory_or_Costing_module_assemblies()
    {
        var references = typeof(SalesChannel).Assembly.GetReferencedAssemblies().Select(item => item.Name).ToArray();
        references.Should().NotContain("Verce.Modules.Catalog");
        references.Should().NotContain("Verce.Modules.Inventory");
        references.Should().NotContain("Verce.Modules.Costing");
    }

    [Fact]
    public void Pricing_module_has_no_web_dependency()
    {
        // SalesChannel/FeeRule/FeeRuleVersion are real persisted aggregates (unlike S4's Costing,
        // which is deliberately pure end to end) — Pricing legitimately references EF Core for
        // their IEntityTypeConfiguration<T> classes. What must stay pure is PricingEngine itself
        // (checked below), and the module must never see ASP.NET Core.
        var references = typeof(PricingEngine).Assembly.GetReferencedAssemblies().Select(item => item.Name).ToArray();
        references.Should().NotContain("Microsoft.AspNetCore.Http.Abstractions");
    }

    [Fact]
    public void PricingEngine_Calculate_takes_only_the_resolved_input_record_no_infrastructure_type()
    {
        var method = typeof(PricingEngine).GetMethod(nameof(PricingEngine.Calculate));
        method.Should().NotBeNull();
        var parameters = method!.GetParameters();
        parameters.Should().HaveCount(1);
        parameters[0].ParameterType.Should().Be<PricingCalculationInput>(
            "the engine must take a single fully-resolved input, never a DbContext/HttpContext/IClock/random source");
    }

    [Fact]
    public void FeeRule_and_FeeRuleVersion_declare_a_single_consistent_ownership_chain()
    {
        typeof(FeeRuleVersion).Should().Implement<Verce.SharedKernel.Domain.IOwnedBy<FeeRule>>();
        typeof(FeeRule).Should().Implement<Verce.SharedKernel.Domain.IAggregateRoot>();
        typeof(SalesChannel).Should().Implement<Verce.SharedKernel.Domain.IAggregateRoot>();
    }
}
