namespace Verce.Modules.Quoting;

/// <summary>
/// Marker for the Quoting module boundary (ARCHITECTURE.md §3 module map).
/// Owns: Quote, QuoteRevision, QuoteItem, snapshots, status history, numbering, PER_ORDER
/// allocation and the derived commercial outcome (S6, ADR-0020). Domain events are published
/// as a separate <c>Verce.Modules.Quoting.Contracts</c> assembly (CLAUDE.md rule 11's
/// "*.Contracts interfaces") so Production can consume them without referencing this module's
/// aggregates.
/// </summary>
public static class QuotingModuleMarker
{
    public const string ModuleName = "Quoting";
}
