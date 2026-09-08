namespace Verce.Modules.Quoting;

/// <summary>
/// Marker for the Quoting module boundary (ARCHITECTURE.md §3 module map).
/// Owns: Quote, QuoteRevision, QuoteItem, snapshots, status history, numbering.
/// This module has no domain logic yet — it is scaffolded in S1 purely so that
/// module boundaries exist and are enforceable by Verce.Architecture.Tests.
/// Real aggregates, application services and infrastructure arrive in the sprint
/// that owns this module per docs/ROADMAP.md.
/// </summary>
public static class QuotingModuleMarker
{
    public const string ModuleName = "Quoting";
}
