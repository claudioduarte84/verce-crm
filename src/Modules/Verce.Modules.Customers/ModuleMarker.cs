namespace Verce.Modules.Customers;

/// <summary>
/// Marker for the Customers module boundary (ARCHITECTURE.md §3 module map).
/// Owns: Customer, CustomerAddress.
/// This module has no domain logic yet — it is scaffolded in S1 purely so that
/// module boundaries exist and are enforceable by Verce.Architecture.Tests.
/// Real aggregates, application services and infrastructure arrive in the sprint
/// that owns this module per docs/ROADMAP.md.
/// </summary>
public static class CustomersModuleMarker
{
    public const string ModuleName = "Customers";
}
