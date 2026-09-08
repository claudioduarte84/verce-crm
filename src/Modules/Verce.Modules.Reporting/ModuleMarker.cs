namespace Verce.Modules.Reporting;

/// <summary>
/// Marker for the Reporting module boundary (ARCHITECTURE.md §3 module map).
/// Owns: Read models, analytical queries, dashboard endpoints.
/// This module has no domain logic yet — it is scaffolded in S1 purely so that
/// module boundaries exist and are enforceable by Verce.Architecture.Tests.
/// Real aggregates, application services and infrastructure arrive in the sprint
/// that owns this module per docs/ROADMAP.md.
/// </summary>
public static class ReportingModuleMarker
{
    public const string ModuleName = "Reporting";
}
