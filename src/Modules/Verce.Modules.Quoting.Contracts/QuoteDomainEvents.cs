using Verce.SharedKernel.Events;

namespace Verce.Modules.Quoting.Contracts;

/// <summary>
/// S6 domain events (DOMAIN-MODEL §15, ADR-0020 §A.6). All synchronous — dispatched between
/// save waves, inside the business transaction (ADR-0012 Part I). Payloads carry IDs and the
/// minimum data a handler needs, never whole aggregates.
/// </summary>
public sealed class QuoteCreatedEvent : DomainEventBase
{
    public Guid QuoteId { get; }
    public Guid RevisionId { get; }

    public QuoteCreatedEvent(Guid correlationId, Guid? causationId, DateTimeOffset now, Guid quoteId, Guid revisionId)
        : base(correlationId, causationId, now)
    {
        QuoteId = quoteId;
        RevisionId = revisionId;
    }
}

/// <summary>Raised whenever a new revision is constructed (STATE-MACHINES §2/§1.3). Production's
/// handler re-evaluates <c>HasPendingRevision</c> on a previous approved revision's order, if
/// any (ADR-0020 §A.4/§A.7) — it never blocks the construction that already happened.</summary>
public sealed class QuoteRevisedEvent : DomainEventBase
{
    public Guid QuoteId { get; }
    public Guid PreviousRevisionId { get; }
    public Guid NewRevisionId { get; }

    public QuoteRevisedEvent(Guid correlationId, Guid? causationId, DateTimeOffset now, Guid quoteId, Guid previousRevisionId, Guid newRevisionId)
        : base(correlationId, causationId, now)
    {
        QuoteId = quoteId;
        PreviousRevisionId = previousRevisionId;
        NewRevisionId = newRevisionId;
    }
}

public sealed class QuoteSentEvent : DomainEventBase
{
    public Guid QuoteId { get; }
    public Guid RevisionId { get; }

    public QuoteSentEvent(Guid correlationId, Guid? causationId, DateTimeOffset now, Guid quoteId, Guid revisionId)
        : base(correlationId, causationId, now)
    {
        QuoteId = quoteId;
        RevisionId = revisionId;
    }
}

/// <summary>The production-conversion contract (ADR-0020 §A.6). Handled synchronously by
/// Production, which creates the <c>ProductionOrder</c> idempotently in the SAME transaction —
/// approval rolls back if that invariant cannot be established (ADR-0012 §2, ADR-0020 §A.8).</summary>
public sealed class QuoteApprovedEvent : DomainEventBase
{
    public Guid QuoteId { get; }
    public Guid QuoteRevisionId { get; }
    public DateTimeOffset ApprovedAt { get; }
    public Guid? ApprovedBy { get; }

    public QuoteApprovedEvent(Guid correlationId, Guid? causationId, DateTimeOffset now, Guid quoteId, Guid quoteRevisionId,
        DateTimeOffset approvedAt, Guid? approvedBy)
        : base(correlationId, causationId, now)
    {
        QuoteId = quoteId;
        QuoteRevisionId = quoteRevisionId;
        ApprovedAt = approvedAt;
        ApprovedBy = approvedBy;
    }
}

/// <summary>Production's handler re-evaluates (clears) <c>HasPendingRevision</c> on the prior
/// approved revision's order, if any — it can never itself cancel an order, since only an
/// APPROVED (terminal) revision ever has one (ADR-0020 §A.6).</summary>
public sealed class QuoteCanceledEvent : DomainEventBase
{
    public Guid QuoteId { get; }
    public Guid RevisionId { get; }

    public QuoteCanceledEvent(Guid correlationId, Guid? causationId, DateTimeOffset now, Guid quoteId, Guid revisionId)
        : base(correlationId, causationId, now)
    {
        QuoteId = quoteId;
        RevisionId = revisionId;
    }
}

/// <summary>Raised by <c>ExpireQuotesJob</c> (STATE-MACHINES §1.6). Production's handler
/// re-evaluates (clears) <c>HasPendingRevision</c> exactly as for <see cref="QuoteCanceledEvent"/>.</summary>
public sealed class QuoteExpiredEvent : DomainEventBase
{
    public Guid QuoteId { get; }
    public Guid RevisionId { get; }

    public QuoteExpiredEvent(Guid correlationId, Guid? causationId, DateTimeOffset now, Guid quoteId, Guid revisionId)
        : base(correlationId, causationId, now)
    {
        QuoteId = quoteId;
        RevisionId = revisionId;
    }
}

/// <summary>
/// S7/S14 scope authority gate §12/§54-59: the integration-event half of approval (ADR-0012 §1 —
/// "approving a quote raises QuoteApproved (synchronous, creates the production order) AND
/// GenerateQuotePdfRequested (integration)"). Raised in the SAME transaction as
/// <see cref="QuoteApprovedEvent"/>, but dispatched only AFTER commit, from the outbox — a PDF
/// render must never be able to fail (or even delay) the approval transaction. <see cref="RenderRequestId"/>
/// is minted once, HERE, and is the outbox consumer's idempotency key (<c>document:{RenderRequestId}</c>,
/// ADR-0012 §22): a retried delivery of this exact message must converge on one document, never
/// produce a second one.
/// </summary>
public sealed class GenerateQuotePdfRequestedEvent : IIntegrationEvent
{
    public Guid EventId { get; }
    public string EventType => nameof(GenerateQuotePdfRequestedEvent);
    public DateTimeOffset OccurredAtUtc { get; }
    public Guid CorrelationId { get; }
    public Guid? CausationId { get; }
    public string IdempotencyKey => $"document:{RenderRequestId:D}";

    public Guid QuoteId { get; }
    public Guid QuoteRevisionId { get; }
    public Guid RenderRequestId { get; }

    public GenerateQuotePdfRequestedEvent(Guid correlationId, Guid? causationId, DateTimeOffset now, Guid quoteId, Guid quoteRevisionId, Guid renderRequestId)
    {
        EventId = Guid.CreateVersion7();
        OccurredAtUtc = now;
        CorrelationId = correlationId;
        CausationId = causationId;
        QuoteId = quoteId;
        QuoteRevisionId = quoteRevisionId;
        RenderRequestId = renderRequestId;
    }
}
