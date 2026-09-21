using System.Text.Json.Serialization;
using Verce.Platform.Audit;
using Verce.SharedKernel.Domain;

namespace Verce.Modules.Production;

/// <summary>The full frozen status set (STATE-MACHINES §4.1). S6 (ADR-0020 §A.8, the "minimum
/// Production Core") persists the WHOLE enum so the approval guard can type-check against
/// <c>IN_PRODUCTION</c>/<c>READY</c>/<c>SHIPPED</c>/<c>DELIVERED</c> from day one, but only
/// implements the <c>QUEUED</c> creation and <c>QUEUED -&gt; CANCELED</c> (supersession)
/// transitions itself — every other transition is S9's operational scope.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProductionOrderStatus>))]
public enum ProductionOrderStatus { QUEUED, IN_PRODUCTION, READY, SHIPPED, DELIVERED, CANCELED }

/// <summary>
/// S6 minimum Production Core (ADR-0020 §A.8, BLOCKING-03 correction): the slice of the
/// <c>ProductionOrder</c> aggregate the <c>QuoteApproved</c> transactional invariant needs
/// (ADR-0012 §2) — created synchronously and idempotently, in the SAME transaction as approval.
/// No item, material or shop-floor-document concept exists yet; those are S9's operational
/// expansion of this same aggregate, added without redesigning it.
/// </summary>
[Auditable]
public sealed class ProductionOrder : AggregateRoot
{
    private ProductionOrder() { }

    private ProductionOrder(int numberSequence, DateOnly numberDate, Guid quoteId, Guid quoteRevisionId)
    {
        if (quoteRevisionId == Guid.Empty) throw new ArgumentException("PRODUCTION_ORDER_QUOTE_REVISION_REQUIRED");
        NumberDate = numberDate;
        NumberSequence = numberSequence;
        OrderNumber = $"{numberDate:yyMMdd}-{numberSequence}";
        QuoteId = quoteId;
        QuoteRevisionId = quoteRevisionId;
        Status = ProductionOrderStatus.QUEUED;
    }

    /// <summary>The only legitimate way to create an order — always starts <c>QUEUED</c>, from
    /// <c>QuoteApproved</c> (STATE-MACHINES §4.2, §4.3).</summary>
    public static ProductionOrder CreateQueued(int numberSequence, DateOnly numberDate, Guid quoteId, Guid quoteRevisionId) =>
        new(numberSequence, numberDate, quoteId, quoteRevisionId);

    public string OrderNumber { get; private set; } = string.Empty;
    public DateOnly NumberDate { get; private set; }
    public int NumberSequence { get; private set; }

    /// <summary>Denormalized so Production can find "another order belonging to the same Quote"
    /// (the ADR-0020 §A.2 matrix) without ever reading Quoting's own tables — a plain ID
    /// reference, never a cross-module navigation property (CLAUDE.md rule 11).</summary>
    public Guid QuoteId { get; private set; }

    /// <summary>The idempotency key (STATE-MACHINES §4.3): UNIQUE, <c>ON CONFLICT DO NOTHING</c>.</summary>
    public Guid QuoteRevisionId { get; private set; }

    public ProductionOrderStatus Status { get; private set; }

    /// <summary>Advisory only — never blocks anything (ADR-0020 §A.4). Set when a newer,
    /// non-terminal revision is constructed over this (still non-terminal) order; cleared when
    /// that revision resolves without being approved.</summary>
    public bool HasPendingRevision { get; private set; }

    public Guid? SupersededByOrderId { get; private set; }
    public string? CancellationReason { get; private set; }

    /// <summary>Non-terminal per STATE-MACHINES §4.1 (everything except DELIVERED/CANCELED).</summary>
    public bool IsNonTerminal => Status is not (ProductionOrderStatus.DELIVERED or ProductionOrderStatus.CANCELED);

    /// <summary>ADR-0020 §A.3: the approval guard set — non-terminal and not QUEUED.</summary>
    public bool BlocksApproval => Status is ProductionOrderStatus.IN_PRODUCTION or ProductionOrderStatus.READY or ProductionOrderStatus.SHIPPED;

    public void SetHasPendingRevision(bool value) => HasPendingRevision = value;

    /// <summary>ADR-0020 §A.2: a QUEUED order is canceled, not silently mutated, when a newer
    /// revision is approved. Only reachable from QUEUED — a defensive guard, since the
    /// composition root must already have rejected approval for any other non-terminal state
    /// before this is ever called.</summary>
    public void CancelAsSupersededByRevision(Guid supersedingOrderId)
    {
        if (Status != ProductionOrderStatus.QUEUED) throw new ArgumentException("PRODUCTION_ORDER_NOT_QUEUED");
        Status = ProductionOrderStatus.CANCELED;
        CancellationReason = "SUPERSEDED_BY_REVISION";
        SupersededByOrderId = supersedingOrderId;
        HasPendingRevision = false;
    }
}
