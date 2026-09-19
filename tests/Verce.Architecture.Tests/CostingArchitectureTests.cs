using FluentAssertions;
using Verce.Api.Costing;
using Verce.Modules.Costing;
using Verce.SharedKernel.Domain;

namespace Verce.Architecture.Tests;

public class CostingArchitectureTests
{
    [Fact]
    public void Costing_engine_has_no_database_or_inventory_dependency()
    {
        var references = typeof(CostEngine).Assembly.GetReferencedAssemblies().Select(item => item.Name).ToArray();
        references.Should().NotContain("Microsoft.EntityFrameworkCore");
        references.Should().NotContain("Verce.Modules.Inventory");
        references.Should().NotContain("Verce.Modules.Settings");
    }

    [Fact]
    public void S4_costing_is_stateless_and_defines_no_aggregate_or_entity()
    {
        var persistentTypes = typeof(CostEngine).Assembly.DefinedTypes
            .Where(type => typeof(Entity).IsAssignableFrom(type.AsType()))
            .Select(type => type.FullName)
            .ToArray();
        persistentTypes.Should().BeEmpty("S4 laboratory calculations are transient and must not create scenarios, snapshots or audit rows");
    }

    [Fact]
    public void Api_inventory_adapter_implements_the_deliberate_Costing_read_port()
    {
        typeof(InventoryCostSourceReader).Should().Implement<ICostingInventoryReader>();
    }
}
