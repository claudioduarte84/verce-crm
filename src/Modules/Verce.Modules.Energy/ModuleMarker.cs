namespace Verce.Modules.Energy;

/// <summary>
/// Marker for the Energy module boundary (ARCHITECTURE.md §3 module map).
/// Owns: Machine, EnergyTariff(+versions), EnergyConsumptionSession, IEnergyProvider.
/// This module has no domain logic yet — it is scaffolded in S1 purely so that
/// module boundaries exist and are enforceable by Verce.Architecture.Tests.
/// Real aggregates, application services and infrastructure arrive in the sprint
/// that owns this module per docs/ROADMAP.md.
/// </summary>
public static class EnergyModuleMarker
{
    public const string ModuleName = "Energy";
}
