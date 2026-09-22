using System.Text;
using Verce.Modules.Quoting.Contracts;
using Verce.Platform.Audit;
using Verce.SharedKernel;
using Verce.SharedKernel.Domain;

namespace Verce.Modules.Quoting;

/// <summary>Customer identity frozen onto a revision at issue (ADR-0003) — never re-resolved
/// from the live <c>Customer</c> record later. <see cref="ContactsJson"/>/<see cref="AddressesJson"/>
/// are pre-serialized by the composition root (Quoting has no reference to the Customers module
/// assembly, CLAUDE.md rule 11) and stored as <c>jsonb</c>.</summary>
public sealed record CustomerSnapshotInput(Guid? CustomerId, string? Name, string? Document, string? ContactsJson, string? AddressesJson);

/// <summary>
/// The S7 proposal-content snapshot (DOMAIN-MODEL §8, ADR-0016 §7). All fields are optional and
/// free-form. Frozen onto the revision AT ISSUE — QuoteRevision creation, inside the same
/// transaction — never read live by any later render (S7/S14 scope authority gate, §5: OPTION A).
/// <see cref="TechnicalHighlightsJson"/> is a pre-serialized <c>jsonb</c> array of
/// <c>{label, value}</c> pairs — deliberately generic (DOMAIN-MODEL §8 rule 1: no material/
/// tolerance/colour/finish column exists on this or any quote table).
/// </summary>
public sealed record ProposalContentInput(
    string? Title,
    string? Scope,
    string? TechnicalHighlightsJson,
    string? TechnicalNotes,
    string? OutOfScope,
    string? PaymentTerms,
    string? DeliveryTerms,
    string? Warranty,
    string? Notes,
    string? InternalNotes)
{
    public static readonly ProposalContentInput Empty = new(null, null, null, null, null, null, null, null, null, null);
}

/// <summary>
/// S6 Quote aggregate root (DOMAIN-MODEL §8, ADR-0004, ADR-0020). The container: owns identity,
/// the atomic number, and the pointer to the current revision. Holds NO money and NO status —
/// status lives on <see cref="QuoteRevision"/> (STATE-MACHINES §1). Every write to a revision,
/// item or history row goes through this root (CLAUDE.md rules 21/28).
///
/// The commercial outcome (WON/LOST/OPEN) is deliberately NOT modeled here: it is derived on read
/// from the append-only history by <see cref="QuoteOutcomeCalculator"/> (ADR-0020 §B.2).
///
/// Cross-module note: the <c>PRODUCTION_ORDER_IN_PROGRESS</c> approval guard (STATE-MACHINES
/// §1.5) is NOT enforced here — it depends on Production's `ProductionOrder` state, which this
/// module must never reference directly (ADR-0001 §3). The composition root
/// (<c>Verce.Api</c>'s Quoting endpoints) checks it BEFORE calling <see cref="Approve"/>, exactly
/// like <c>ProductEndpoints</c> resolves Supply lookups before calling into Catalog.
/// </summary>
[Auditable]
public sealed class Quote : AggregateRoot
{
    private readonly List<QuoteRevision> _revisions = [];
    private Quote() { }

    public Quote(int numberSequence, DateOnly numberDate, CustomerSnapshotInput customer, Guid salesChannelId,
        IReadOnlyList<QuoteItemSnapshot> items, int validityDays, ProposalContentInput proposalContent,
        Guid? actorUserId, Guid correlationId, DateTimeOffset now)
    {
        if (numberSequence <= 0) throw new ArgumentException("QUOTE_NUMBER_SEQUENCE_INVALID");
        NumberDate = numberDate;
        NumberSequence = numberSequence;
        Number = FormatNumber(numberDate, numberSequence);
        CustomerId = customer.CustomerId;

        var firstRevision = new QuoteRevision(Id, 1, customer, salesChannelId, items, validityDays, proposalContent, numberDate, now);
        _revisions.Add(firstRevision);
        CurrentRevisionId = firstRevision.Id;
        firstRevision.AppendHistory(null, QuoteRevisionStatus.GENERATED, now, actorUserId, QuoteHistoryTrigger.USER, null);

        Raise(new QuoteCreatedEvent(correlationId, null, now, Id, firstRevision.Id));
    }

    public string Number { get; private set; } = string.Empty;
    public DateOnly NumberDate { get; private set; }
    public int NumberSequence { get; private set; }
    public Guid? CustomerId { get; private set; }
    public Guid CurrentRevisionId { get; private set; }
    public IReadOnlyList<QuoteRevision> Revisions => _revisions.AsReadOnly();
    public QuoteRevision CurrentRevision => _revisions.Single(r => r.Id == CurrentRevisionId);

    /// <summary><c>YYMMDD-N</c> (ADR-0004 §1) — never a revision suffix; every revision of one
    /// quote shares this.</summary>
    public static string FormatNumber(DateOnly date, int sequence) => $"{date:yyMMdd}-{sequence}";

    /// <summary>Display number for a specific revision — the quote number plus that revision's
    /// persisted suffix (ADR-0004 §2). Never recomputed from a live algorithm at read time
    /// beyond simple concatenation of two already-persisted values.</summary>
    public string DisplayNumberFor(QuoteRevision revision) => Number + revision.RevisionSuffix;

    /// <summary>
    /// ADR-0020 §A.7: clone-then-recalculate, never an in-place edit. The composition root has
    /// ALREADY determined the complete next-revision item set — which lines are pricing-affected
    /// (its own change, or a PER_ORDER sibling's, ADR-0020 §C.6) and recalculated them, copying
    /// every other field verbatim from the current revision. This method's job is only to freeze
    /// that candidate as an immutable <see cref="QuoteRevision"/> and manage the supersession
    /// pointer — it is NEVER gated on a previous revision's production-order state
    /// (BLOCKING-01 correction; STATE-MACHINES §2).
    /// </summary>
    public QuoteRevision ConstructNextRevision(CustomerSnapshotInput customer, Guid salesChannelId,
        IReadOnlyList<QuoteItemSnapshot> items, int validityDays, ProposalContentInput proposalContent, DateOnly organizationToday,
        Guid? actorUserId, Guid correlationId, DateTimeOffset now)
    {
        var current = CurrentRevision;
        var next = new QuoteRevision(Id, current.RevisionIndex + 1, customer, salesChannelId, items, validityDays, proposalContent, organizationToday, now)
        {
            SourceRevisionId = current.Id,
        };
        _revisions.Add(next);

        var previousStatus = current.Status;
        current.MarkSupersededBy(next.Id);
        if (current.Status != previousStatus)
            current.AppendHistory(previousStatus, current.Status, now, actorUserId, QuoteHistoryTrigger.USER, null);

        CurrentRevisionId = next.Id;
        next.AppendHistory(null, QuoteRevisionStatus.GENERATED, now, actorUserId, QuoteHistoryTrigger.USER, null);

        Raise(new QuoteRevisedEvent(correlationId, null, now, Id, current.Id, next.Id));
        return next;
    }

    /// <summary>STATE-MACHINES §1.3: GENERATED or NEGOTIATING -&gt; SENT.</summary>
    public void Send(Guid? actorUserId, Guid correlationId, DateTimeOffset now)
    {
        var rev = CurrentRevision;
        var from = rev.Status;
        rev.TransitionTo(QuoteRevisionStatus.SENT, [QuoteRevisionStatus.GENERATED, QuoteRevisionStatus.NEGOTIATING]);
        rev.AppendHistory(from, rev.Status, now, actorUserId, QuoteHistoryTrigger.USER, null);
        Raise(new QuoteSentEvent(correlationId, null, now, Id, rev.Id));
    }

    /// <summary>STATE-MACHINES §1.3: GENERATED or SENT -&gt; NEGOTIATING.</summary>
    public void MarkNegotiating(Guid? actorUserId, Guid correlationId, DateTimeOffset now)
    {
        var rev = CurrentRevision;
        var from = rev.Status;
        rev.TransitionTo(QuoteRevisionStatus.NEGOTIATING, [QuoteRevisionStatus.GENERATED, QuoteRevisionStatus.SENT]);
        rev.AppendHistory(from, rev.Status, now, actorUserId, QuoteHistoryTrigger.USER, null);
    }

    /// <summary>STATE-MACHINES §1.3: GENERATED/SENT/NEGOTIATING -&gt; CANCELED. Reason required.</summary>
    public void Cancel(string reason, Guid? actorUserId, Guid correlationId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("QUOTE_CANCEL_REASON_REQUIRED");
        var rev = CurrentRevision;
        var from = rev.Status;
        rev.TransitionTo(QuoteRevisionStatus.CANCELED, [QuoteRevisionStatus.GENERATED, QuoteRevisionStatus.SENT, QuoteRevisionStatus.NEGOTIATING]);
        rev.AppendHistory(from, rev.Status, now, actorUserId, QuoteHistoryTrigger.USER, reason);
        Raise(new QuoteCanceledEvent(correlationId, null, now, Id, rev.Id));
    }

    /// <summary>
    /// STATE-MACHINES §1.5. Guards enforced HERE: not-terminal, not-expired, has items, every
    /// item has a positive final price, and (only for a still-<c>GENERATED</c> revision that was
    /// never sent) <paramref name="allowDirectApproval"/> — STATE-MACHINES §1.3's transition
    /// table gates only <c>GENERATED -&gt; APPROVED</c> on <c>quote.allow_direct_approval</c>;
    /// <c>SENT</c>/<c>NEGOTIATING -&gt; APPROVED</c> carry no such guard. The
    /// <c>PRODUCTION_ORDER_IN_PROGRESS</c> guard is deliberately absent — see the class remarks;
    /// the caller must have already checked it.
    /// </summary>
    public void Approve(Guid? approvedBy, Guid correlationId, DateTimeOffset now, DateOnly organizationToday, bool allowDirectApproval)
    {
        var rev = CurrentRevision;
        if (rev.IsTerminal) throw new ArgumentException("QUOTE_REVISION_ALREADY_DECIDED");
        if (rev.Status == QuoteRevisionStatus.GENERATED && !allowDirectApproval) throw new ArgumentException("QUOTE_DIRECT_APPROVAL_NOT_ALLOWED");
        if (rev.ValidUntil < organizationToday) throw new ArgumentException("QUOTE_REVISION_EXPIRED");
        if (rev.Items.Count == 0) throw new ArgumentException("QUOTE_HAS_NO_ITEMS");
        if (rev.Items.Any(i => i.UnitPrice <= 0)) throw new ArgumentException("QUOTE_ITEM_INVALID_PRICE");

        var from = rev.Status;
        rev.TransitionTo(QuoteRevisionStatus.APPROVED, [QuoteRevisionStatus.GENERATED, QuoteRevisionStatus.SENT, QuoteRevisionStatus.NEGOTIATING]);
        rev.SetApprovalMetadata(now, approvedBy);
        rev.AppendHistory(from, rev.Status, now, approvedBy, QuoteHistoryTrigger.USER, null);

        Raise(new QuoteApprovedEvent(correlationId, null, now, Id, rev.Id, now, approvedBy));
        // ADR-0012 §1: the integration half of the SAME business moment — dispatched only after
        // commit, from the outbox, so a slow/failed PDF render can never touch this transaction
        // (S7/S14 scope authority gate §12). Both events are raised directly by THIS command (wave
        // 1), so — exactly like every sibling event above — CausationId is null, not each other's
        // EventId. RenderRequestId is minted once, here, and becomes the consumer's idempotency key.
        Raise(new GenerateQuotePdfRequestedEvent(correlationId, null, now, Id, rev.Id, Guid.CreateVersion7()));
    }

    /// <summary>
    /// STATE-MACHINES §1.6, called only by <c>ExpireQuotesJob</c>. Idempotent: a no-op if the
    /// current revision is not eligible (already terminal, superseded, or not yet past
    /// <see cref="QuoteRevision.ValidUntil"/>) — re-running the job changes nothing.
    /// </summary>
    public void ExpireCurrentRevision(Guid correlationId, DateTimeOffset now, DateOnly organizationToday)
    {
        var rev = CurrentRevision;
        if (rev.SupersededByRevisionId is not null) return;
        if (rev.IsTerminal) return;
        if (rev.ValidUntil >= organizationToday) return;

        var from = rev.Status;
        rev.TransitionTo(QuoteRevisionStatus.EXPIRED, [QuoteRevisionStatus.GENERATED, QuoteRevisionStatus.SENT, QuoteRevisionStatus.NEGOTIATING]);
        rev.AppendHistory(from, rev.Status, now, null, QuoteHistoryTrigger.SYSTEM_JOB, null);

        Raise(new QuoteExpiredEvent(correlationId, null, now, Id, rev.Id));
    }
}

/// <summary>
/// One immutable-once-created commercial snapshot (DOMAIN-MODEL §8, ADR-0003). Owned by
/// <see cref="Quote"/>. Only <see cref="Status"/>, <see cref="SupersededByRevisionId"/> and
/// appended <see cref="History"/> rows ever change after construction (ADR-0020 §A.1/§A.7).
/// </summary>
public sealed class QuoteRevision : Entity, IOwnedBy<Quote>
{
    private static readonly QuoteRevisionStatus[] TerminalStatuses =
        [QuoteRevisionStatus.APPROVED, QuoteRevisionStatus.CANCELED, QuoteRevisionStatus.EXPIRED, QuoteRevisionStatus.SUPERSEDED];

    private readonly List<QuoteItem> _items = [];
    private readonly List<QuoteStatusHistory> _history = [];

    private QuoteRevision() { }

    internal QuoteRevision(Guid quoteId, int revisionIndex, CustomerSnapshotInput customer, Guid salesChannelId,
        IReadOnlyList<QuoteItemSnapshot> items, int validityDays, ProposalContentInput proposalContent,
        DateOnly organizationToday, DateTimeOffset now)
    {
        if (revisionIndex < 1) throw new ArgumentException("QUOTE_REVISION_INDEX_INVALID");
        // M-03/F-05: the only authoritative rules are "positive" and "technically representable
        // as a DateOnly" — no arbitrary business-length maximum (an earlier correction's 3650-day
        // cap had no architectural authority and is removed). maxRepresentableDays is computed
        // from DayNumber arithmetic and checked BEFORE ever calling AddDays, so an extreme
        // override/setting value is rejected with a stable code instead of AddDays throwing an
        // unhandled ArgumentOutOfRangeException.
        if (validityDays <= 0) throw new ArgumentException("QUOTE_VALIDITY_DAYS_INVALID");
        var maxRepresentableDays = DateOnly.MaxValue.DayNumber - organizationToday.DayNumber;
        if (validityDays > maxRepresentableDays) throw new ArgumentException("QUOTE_VALIDITY_DAYS_INVALID");
        if (salesChannelId == Guid.Empty) throw new ArgumentException("QUOTE_SALES_CHANNEL_REQUIRED");

        QuoteId = quoteId;
        RevisionIndex = revisionIndex;
        RevisionSuffix = ToSuffix(revisionIndex);
        Status = QuoteRevisionStatus.GENERATED;
        SalesChannelId = salesChannelId;
        IssuedAt = now;
        ValidityDays = validityDays;
        ValidUntil = organizationToday.AddDays(validityDays);

        CustomerId = customer.CustomerId;
        CustomerNameSnapshot = customer.Name;
        CustomerDocumentSnapshot = customer.Document;
        CustomerContactsSnapshot = customer.ContactsJson;
        CustomerAddressesSnapshot = customer.AddressesJson;

        // ADR-0016 §7 / DOMAIN-MODEL §8: the proposal-content snapshot is frozen HERE, at
        // revision construction ("issue"), never re-read live by any later render. The
        // composition root is responsible for having already resolved PaymentTerms/
        // DeliveryTerms/Warranty defaults (from Settings, once, only for a brand-new Quote's
        // R1) or cloned every field verbatim from the source revision (on revise) BEFORE
        // calling in — this constructor only freezes whatever it is given.
        Title = Trim(proposalContent.Title);
        Scope = Trim(proposalContent.Scope);
        TechnicalHighlightsJson = string.IsNullOrWhiteSpace(proposalContent.TechnicalHighlightsJson) ? null : proposalContent.TechnicalHighlightsJson;
        TechnicalNotes = Trim(proposalContent.TechnicalNotes);
        OutOfScope = Trim(proposalContent.OutOfScope);
        PaymentTerms = Trim(proposalContent.PaymentTerms);
        DeliveryTerms = Trim(proposalContent.DeliveryTerms);
        Warranty = Trim(proposalContent.Warranty);
        Notes = Trim(proposalContent.Notes);
        InternalNotes = Trim(proposalContent.InternalNotes);

        for (var i = 0; i < items.Count; i++)
        {
            _items.Add(new QuoteItem(Id, i + 1, items[i]));
        }

        RecomputeTotals();
    }

    public Guid QuoteId { get; private set; }
    public Guid ParentId => QuoteId;
    public int RevisionIndex { get; private set; }
    public string RevisionSuffix { get; private set; } = string.Empty;
    public QuoteRevisionStatus Status { get; private set; }
    public Guid SalesChannelId { get; private set; }
    public DateTimeOffset IssuedAt { get; private set; }
    public int ValidityDays { get; private set; }
    public DateOnly ValidUntil { get; private set; }

    public Guid? CustomerId { get; private set; }
    public string? CustomerNameSnapshot { get; private set; }
    public string? CustomerDocumentSnapshot { get; private set; }
    public string? CustomerContactsSnapshot { get; private set; }
    public string? CustomerAddressesSnapshot { get; private set; }

    public Guid? SupersededByRevisionId { get; private set; }
    public Guid? SourceRevisionId { get; internal set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public Guid? ApprovedBy { get; private set; }

    /// <summary>S7 proposal-content snapshot (DOMAIN-MODEL §8), all optional and frozen at
    /// construction — ADR-0016 §7. <see cref="InternalNotes"/> has no binding in the QUOTE
    /// document catalogue and must never be printed on a customer-facing document.</summary>
    public string? Title { get; private set; }
    public string? Scope { get; private set; }
    /// <summary>jsonb array of <c>{label, value}</c> — DOMAIN-MODEL §8 rule 1: deliberately
    /// generic, never a typed material/tolerance/colour/finish column.</summary>
    public string? TechnicalHighlightsJson { get; private set; }
    public string? TechnicalNotes { get; private set; }
    public string? OutOfScope { get; private set; }
    public string? PaymentTerms { get; private set; }
    public string? DeliveryTerms { get; private set; }
    public string? Warranty { get; private set; }
    public string? Notes { get; private set; }
    public string? InternalNotes { get; private set; }

    /// <summary>The complete proposal-content snapshot, ready to clone verbatim onto a revise
    /// candidate (STATE-MACHINES §2 step 2) before the caller's explicit changes are applied.</summary>
    public ProposalContentInput ProposalContent => new(Title, Scope, TechnicalHighlightsJson, TechnicalNotes,
        OutOfScope, PaymentTerms, DeliveryTerms, Warranty, Notes, InternalNotes);

    public decimal SubtotalAmount { get; private set; }
    public decimal DiscountAmount { get; private set; }
    public decimal TotalAmount { get; private set; }
    public decimal TotalCostAmount { get; private set; }
    public decimal ExpectedProfitAmount { get; private set; }
    public decimal EffectiveMarginPercent { get; private set; }

    public IReadOnlyList<QuoteItem> Items => _items.AsReadOnly();
    public IReadOnlyList<QuoteStatusHistory> History => _history.AsReadOnly();

    public bool IsTerminal => TerminalStatuses.Contains(Status);

    /// <summary>CR-08.6 — totals are sums of already-rounded line values, never a
    /// recomputation from unrounded inputs.</summary>
    private void RecomputeTotals()
    {
        SubtotalAmount = Rounding.ToMoney(_items.Sum(i => Rounding.ToMoney(i.UnitPrice * i.Quantity)));
        DiscountAmount = Rounding.ToMoney(_items.Sum(i => i.DiscountAmount * i.Quantity));
        TotalAmount = Rounding.ToMoney(_items.Sum(i => i.LineTotalAmount));
        TotalCostAmount = Rounding.ToMoney(_items.Sum(i => i.LineCostAmount));
        ExpectedProfitAmount = Rounding.ToMoney(_items.Sum(i => i.ExpectedProfitAmount));
        EffectiveMarginPercent = TotalAmount > 0 ? Rounding.ToPercent(ExpectedProfitAmount / TotalAmount) : 0m;
    }

    internal void TransitionTo(QuoteRevisionStatus target, QuoteRevisionStatus[] allowedFrom)
    {
        if (!allowedFrom.Contains(Status)) throw new ArgumentException("QUOTE_INVALID_TRANSITION");
        Status = target;
    }

    /// <summary>STATE-MACHINES §1.2: the pointer is set regardless of status; the STATUS
    /// transition to SUPERSEDED happens only for a still-undecided revision. A terminal
    /// revision (APPROVED/CANCELED/EXPIRED) keeps its status forever and only gains the
    /// pointer.</summary>
    internal void MarkSupersededBy(Guid newRevisionId)
    {
        SupersededByRevisionId = newRevisionId;
        if (Status is QuoteRevisionStatus.GENERATED or QuoteRevisionStatus.SENT or QuoteRevisionStatus.NEGOTIATING)
            Status = QuoteRevisionStatus.SUPERSEDED;
    }

    internal void SetApprovalMetadata(DateTimeOffset approvedAt, Guid? approvedBy)
    {
        ApprovedAt = approvedAt;
        ApprovedBy = approvedBy;
    }

    internal void AppendHistory(QuoteRevisionStatus? fromStatus, QuoteRevisionStatus toStatus, DateTimeOffset changedAt,
        Guid? changedBy, QuoteHistoryTrigger trigger, string? reason) =>
        _history.Add(new QuoteStatusHistory(Id, fromStatus, toStatus, changedAt, changedBy, trigger, reason));

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>ADR-0004 §2: revision index 1 is "A" and renders NO suffix; index 2 renders "B";
    /// bijective base-26 thereafter. Persisted at construction, never recomputed on read, so a
    /// future change to this algorithm cannot rewrite the identity of a revision already
    /// issued.</summary>
    public static string ToSuffix(int revisionIndex)
    {
        if (revisionIndex < 1) throw new ArgumentException("QUOTE_REVISION_INDEX_INVALID");
        if (revisionIndex == 1) return string.Empty;
        var n = revisionIndex;
        var sb = new StringBuilder();
        while (n > 0)
        {
            n--; // bijective, not positional
            sb.Insert(0, (char)('A' + n % 26));
            n /= 26;
        }
        return sb.ToString();
    }
}

/// <summary>Append-only business history (DOMAIN-MODEL §8), distinct from the generic audit
/// log. The authoritative source for <see cref="QuoteOutcomeCalculator"/>.</summary>
public sealed class QuoteStatusHistory : Entity, IOwnedBy<QuoteRevision>
{
    private QuoteStatusHistory() { }

    internal QuoteStatusHistory(Guid quoteRevisionId, QuoteRevisionStatus? fromStatus, QuoteRevisionStatus toStatus,
        DateTimeOffset changedAt, Guid? changedBy, QuoteHistoryTrigger trigger, string? reason)
    {
        QuoteRevisionId = quoteRevisionId;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        ChangedAt = changedAt;
        ChangedBy = changedBy;
        Trigger = trigger;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }

    public Guid QuoteRevisionId { get; private set; }
    public Guid ParentId => QuoteRevisionId;
    public QuoteRevisionStatus? FromStatus { get; private set; }
    public QuoteRevisionStatus ToStatus { get; private set; }
    public DateTimeOffset ChangedAt { get; private set; }
    public Guid? ChangedBy { get; private set; }
    public QuoteHistoryTrigger Trigger { get; private set; }
    public string? Reason { get; private set; }
}
