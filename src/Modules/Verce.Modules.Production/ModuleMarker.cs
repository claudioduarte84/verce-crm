namespace Verce.Modules.Production;

/// <summary>
/// Marker for the Production module boundary (ARCHITECTURE.md §3 module map).
/// S6 (ADR-0020 §A.8) owns the minimum <c>ProductionOrder</c> core the <c>QuoteApproved</c>
/// transactional invariant needs (aggregate, full status enum, QUEUED creation, QUEUED-&gt;
/// CANCELED supersession, HasPendingRevision). <c>ProductionOrderItem</c>, planned/actual
/// material and every operator-driven transition beyond QUEUED are S9's operational expansion
/// of this same aggregate.
/// </summary>
public static class ProductionModuleMarker
{
    public const string ModuleName = "Production";
}
