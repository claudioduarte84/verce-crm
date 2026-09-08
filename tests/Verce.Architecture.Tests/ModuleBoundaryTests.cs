using System.Reflection;
using FluentAssertions;
using Verce.Modules.AI;
using Verce.Modules.Catalog;
using Verce.Modules.Costing;
using Verce.Modules.Customers;
using Verce.Modules.Documents;
using Verce.Modules.Energy;
using Verce.Modules.Finance;
using Verce.Modules.Inventory;
using Verce.Modules.Pricing;
using Verce.Modules.Production;
using Verce.Modules.Quoting;
using Verce.Modules.Reporting;
using Verce.Modules.Sales;
using Verce.Modules.Settings;

namespace Verce.Architecture.Tests;

/// <summary>ADR-0001 §3.1: modules depend only downward, never on each other directly — a
/// module may reference SharedKernel and Platform, but not another module's assembly.</summary>
public class ModuleBoundaryTests
{
    private static readonly Dictionary<string, Assembly> ModuleAssemblies = new()
    {
        [nameof(Verce.Modules.Customers)] = typeof(CustomersModuleMarker).Assembly,
        [nameof(Verce.Modules.Catalog)] = typeof(CatalogModuleMarker).Assembly,
        [nameof(Verce.Modules.Inventory)] = typeof(InventoryModuleMarker).Assembly,
        [nameof(Verce.Modules.Costing)] = typeof(CostingModuleMarker).Assembly,
        [nameof(Verce.Modules.Pricing)] = typeof(PricingModuleMarker).Assembly,
        [nameof(Verce.Modules.Quoting)] = typeof(QuotingModuleMarker).Assembly,
        [nameof(Verce.Modules.Sales)] = typeof(SalesModuleMarker).Assembly,
        [nameof(Verce.Modules.Production)] = typeof(ProductionModuleMarker).Assembly,
        [nameof(Verce.Modules.Energy)] = typeof(EnergyModuleMarker).Assembly,
        [nameof(Verce.Modules.Documents)] = typeof(DocumentsModuleMarker).Assembly,
        [nameof(Verce.Modules.Finance)] = typeof(FinanceModuleMarker).Assembly,
        [nameof(Verce.Modules.Reporting)] = typeof(ReportingModuleMarker).Assembly,
        [nameof(Verce.Modules.AI)] = typeof(AIModuleMarker).Assembly,
        [nameof(Verce.Modules.Settings)] = typeof(SettingsModuleMarker).Assembly,
    };

    [Fact]
    public void No_module_assembly_references_another_module_assembly()
    {
        var violations = new List<string>();

        foreach (var (name, assembly) in ModuleAssemblies)
        {
            var referencedNames = assembly.GetReferencedAssemblies().Select(a => a.Name).ToHashSet();

            foreach (var (otherName, _) in ModuleAssemblies)
            {
                if (otherName == name) continue;
                var otherAssemblyName = $"Verce.Modules.{otherName}";
                if (referencedNames.Contains(otherAssemblyName))
                    violations.Add($"Verce.Modules.{name} references {otherAssemblyName} directly");
            }
        }

        violations.Should().BeEmpty("modules communicate only through Contracts/domain events — never a direct assembly reference to another module");
    }

    [Fact]
    public void Every_module_assembly_exists_and_is_distinct()
    {
        ModuleAssemblies.Should().HaveCount(14, "ARCHITECTURE §3 lists fourteen modules");
        ModuleAssemblies.Values.Distinct().Should().HaveCount(14, "each module must be its own assembly, not merged with another");
    }

    [Fact]
    public void Composition_root_Api_may_reference_every_module()
    {
        // The inverse of the boundary rule: Verce.Api IS allowed to see every module, because
        // it is the composition root (ADR-0001 §3.1) — this test documents that asymmetry
        // explicitly rather than leaving it implicit.
        var apiAssembly = typeof(Verce.Api.Cli.CliCommands).Assembly;
        var referencedNames = apiAssembly.GetReferencedAssemblies().Select(a => a.Name).ToHashSet();

        foreach (var (name, _) in ModuleAssemblies)
        {
            referencedNames.Should().Contain($"Verce.Modules.{name}", $"the composition root must wire up the {name} module");
        }
    }
}
