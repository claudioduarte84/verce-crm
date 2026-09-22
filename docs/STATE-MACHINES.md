# STATE MACHINES — Verce 3D | Laboratório de Custos

Transitions listed here are the **complete** set. Any transition not listed is invalid and must
be rejected by the domain with a stable error code — never silently ignored, never allowed by
an "update status" endpoint that takes an arbitrary target.

Implementation rule: transitions are domain methods on the aggregate (`revision.Approve(user)`),
never a settable `Status` property. Every transition appends a history row.

---

## 1. Quote revision status

Status lives on **`QuoteRevision`**, not on `Quote`. A quote does not have a status; it has a
current revision that has one.

### 1.1 States

| State | Meaning | Terminal | Expires |
|---|---|---|---|
| `GENERATED` | Created, not yet sent to the customer | no | yes |
| `SENT` | Delivered to the customer | no | yes |
| `NEGOTIATING` | Customer responded, terms under discussion | no | yes |
| `APPROVED` | Customer accepted this revision | **yes** | **no** |
| `CANCELED` | Deal dropped (by us or the customer) | **yes** | **no** |
| `EXPIRED` | Validity elapsed without a decision | **yes** | — |
| `SUPERSEDED` | Replaced by a newer revision before being decided | **yes** | **no** |

`SUPERSEDED` is an **addition to the six states in the brief**. It is required to satisfy two
stated rules simultaneously — "every revision must be preserved" and "only the current revision
can be approved" — without corrupting the meaning of the other states. Justification and the
rejected alternatives are in
[ADR-0004](architecture/ADR-0004-quote-numbering-and-revisioning.md).

### 1.2 The supersession pointer

Every revision has `SupersededByRevisionId`. It is set on the previous revision whenever a new
revision is created, **regardless of status**. Two different things are therefore recorded:

- **Is this revision current?** → `SupersededByRevisionId IS NULL`.
- **How did this revision end?** → `Status`.

This distinction matters: when an **approved** revision is replaced, its status stays
`APPROVED` forever — the fact that the customer approved it on that date is history and must
not be erased. Only revisions that were still undecided (`GENERATED`, `SENT`, `NEGOTIATING`)
transition to `SUPERSEDED` when replaced. Terminal revisions keep their status and merely
receive the pointer.

### 1.3 Transition table

| From | To | Trigger | Guards |
|---|---|---|---|
| — | `GENERATED` | create quote / create revision | numbering allocated in the same transaction |
| `GENERATED` | `SENT` | user sends | is current revision |
| `GENERATED` | `NEGOTIATING` | user marks | is current revision |
| `GENERATED` | `APPROVED` | user approves | is current; `quote.allow_direct_approval = true`; §1.5 guards |
| `GENERATED` | `CANCELED` | user cancels | is current; reason required |
| `GENERATED` | `EXPIRED` | `ExpireQuotesJob` | is current; `ValidUntil < today(org tz)` |
| `GENERATED` | `SUPERSEDED` | new revision created | automatic |
| `SENT` | `NEGOTIATING` | user marks | is current |
| `SENT` | `APPROVED` | user approves | is current; §1.5 guards |
| `SENT` | `CANCELED` | user cancels | is current; reason required |
| `SENT` | `EXPIRED` | job | is current; `ValidUntil < today` |
| `SENT` | `SUPERSEDED` | new revision created | automatic |
| `NEGOTIATING` | `SENT` | user re-sends | is current |
| `NEGOTIATING` | `APPROVED` | user approves | is current; §1.5 guards |
| `NEGOTIATING` | `CANCELED` | user cancels | is current; reason required |
| `NEGOTIATING` | `EXPIRED` | job | is current; `ValidUntil < today` |
| `NEGOTIATING` | `SUPERSEDED` | new revision created | automatic |
| `APPROVED` | — | — | terminal; never expires, never superseded (pointer only) |
| `CANCELED` | — | — | terminal |
| `EXPIRED` | — | — | terminal |
| `SUPERSEDED` | — | — | terminal |

### 1.4 Diagram

```
                  ┌──────────────────────────────────────────────┐
                  │                                              │
          ┌───────▼────────┐   send    ┌──────────┐  respond  ┌──▼───────────┐
  create  │   GENERATED    ├──────────►│   SENT   ├──────────►│ NEGOTIATING  │
 ────────►│                │           │          │◄──────────┤              │
          └──┬──┬──┬───┬───┘           └──┬──┬──┬─┘  re-send  └──┬───┬───┬───┘
             │  │  │   │                  │  │  │                │   │   │
     approve │  │  │   │ new revision     │  │  │                │   │   │
             │  │  │   └──────────┐       │  │  └────────────────┼───┼───┼──┐
             │  │  │              │       │  │                   │   │   │  │
             │  │  │ cancel       │       │  │ cancel            │   │   │  │
             │  │  └──────────────┼───────┼──┼───────────────────┼───┘   │  │
             │  │                 │       │  │                   │       │  │
             │  │ expire (job)    │       │  │                   │       │  │
             │  └─────────────────┼───────┼──┴───────────────────┼───────┘  │
             │                    │       │                      │          │
             ▼                    ▼       ▼                      ▼          ▼
        ┌─────────┐        ┌────────────┐   ┌───────────┐   ┌─────────┐
        │APPROVED │        │ SUPERSEDED │   │ CANCELED  │   │ EXPIRED │
        │(terminal│        │ (terminal) │   │(terminal) │   │(terminal│
        │ no exp.)│        └────────────┘   └───────────┘   └─────────┘
        └────┬────┘
             │ domain event QuoteApproved
             ▼
       ProductionOrder (idempotent)
```

### 1.5 Approval guards

`Approve()` is rejected when any of the following holds:

| Guard | Error code |
|---|---|
| Revision is not the current one | `QUOTE_REVISION_NOT_CURRENT` |
| Revision status is terminal | `QUOTE_REVISION_ALREADY_DECIDED` |
| `ValidUntil < today(org tz)` | `QUOTE_REVISION_EXPIRED` |
| The quote has no items | `QUOTE_HAS_NO_ITEMS` |
| Any item has a non-positive final price | `QUOTE_ITEM_INVALID_PRICE` |
| A production order for a **previous** revision of this quote is in `IN_PRODUCTION`, `READY` or `SHIPPED` — i.e. **non-terminal and not `QUEUED`** ([ADR-0020 §A.3](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md)) | `PRODUCTION_ORDER_IN_PROGRESS` (see §3) |

Approval, production-order creation and the status history row all happen in **one transaction**
— spanning several save/dispatch waves, since the `QuoteApproved` handler creates an aggregate
that a later wave persists ([ADR-0012 §2](architecture/ADR-0012-domain-events-and-outbox.md#3-the-unit-of-work-algorithm)).
If the production order cannot be created, **the approval rolls back**.

### 1.6 Expiration

`ExpireQuotesJob` runs hourly and selects, in one query:

```sql
SELECT r.id
FROM quoting.quote_revision r
JOIN quoting.quote q ON q.current_revision_id = r.id
WHERE r.superseded_by_revision_id IS NULL
  AND r.status IN ('GENERATED','SENT','NEGOTIATING')
  AND r.valid_until < :today_in_org_timezone
```

Properties:
- `APPROVED` and `CANCELED` are excluded by the status filter — they never expire.
- Superseded revisions are excluded — they already ended.
- `valid_until` is a `date` computed in the organization timezone at issue time and stored on
  the revision, so changing `quote.default_validity_days` never moves an existing deadline.
- The job is idempotent: re-running it changes nothing.
- Each expiration writes a history row with `Trigger = SYSTEM_JOB`.
- A quote expiring is a domain event (`QuoteExpired`), so a future notification is additive.

**Reviving an expired or canceled quote** is done by creating a new revision, which starts at
`GENERATED` with a fresh `ValidUntil`. There is no "un-expire" transition — that would mutate
history, which rule 5 of CLAUDE.md forbids.

---

## 2. Revision creation (edit semantics)

> **BLOCKING-01 correction ([ADR-0020 §A.1](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md)).**
> An earlier draft of step 2 below gated revision **creation** on the state of a previous
> production order. That directly contradicted §3.0's matrix, where *creating* `R(n+1)` is
> `allowed` in every single row. The guard never belonged here: **creating a revision is never
> blocked by the state of a previous revision's `ProductionOrder`.** Only **approving** the new
> revision can be blocked, and only by the §1.5 guard (`PRODUCTION_ORDER_IN_PROGRESS`, restated in
> §3.2 step 2). Removed accordingly.

Editing a quote **never** mutates a persisted revision — there is no mutable "draft revision" in
this domain. What the UI calls "editing a quote" is always **constructing a new revision**: clone
the current one into an in-memory candidate, apply the change to that candidate, recalculate only
what the change actually affects, then persist the candidate as `R(n+1)` in one atomic step. The
persisted `R(n)` is never touched except for the two fields §1.2 already allows
(`Status`, `SupersededByRevisionId`).

`EditQuote` is:

```
1. Load current, persisted revision R(n). It is immutable and stays untouched throughout.
2. Construct a revision candidate by deep-cloning R(n): items, snapshots, customer snapshot,
   notes. The candidate exists only in memory; it has no identity of its own until step 7 and is
   never itself persisted before the requested change is applied.
3. Apply the requested commercial changes to the candidate (quantity, product, manual cost,
   discount, line add/remove, customer/content fields, ...).
4. Determine the candidate's pricing-affected lines and recalculate ONLY their derived pricing
   fields (see ADR-0020 §A.7 for the exact dependency-closure rule — it is not simply "the lines
   the user touched", because a PER_ORDER fee group couples lines together). Every other field of
   every other line — and every field of a pricing-affected line that is not itself derived from
   a changed input — is copied verbatim from R(n).
5. Validate the complete candidate (CR-07.1's denominator guard and the other item/revision
   invariants).
6. Persist the validated candidate as R(n+1) — this is the only point at which it becomes a real,
   immutable revision:
   R(n+1).RevisionIndex = R(n).RevisionIndex + 1
   R(n+1).Status        = GENERATED
   R(n+1).SourceRevisionId = R(n).Id
   R(n+1).ValidUntil    = today(org tz) + validityDays
7. R(n).SupersededByRevisionId = R(n+1).Id
   if R(n).Status in (GENERATED, SENT, NEGOTIATING) → R(n).Status = SUPERSEDED
   (terminal statuses are preserved unchanged)
8. Quote.CurrentRevisionId = R(n+1).Id
9. Raise QuoteRevised.
```

There is **no production-order guard anywhere in this sequence** — see the correction box above.
`Quote.Version` (CLAUDE.md rules 21/28) is the only thing that can make step 1's load stale, and
that is ordinary optimistic concurrency (`CONCURRENCY_CONFLICT`), not a business rule.

Step 4 is deliberate and narrow: recalculating a line the operator did not touch, and that no
changed input feeds, would violate the snapshot rule from the operator's point of view ("I only
changed the quantity of item 2 and item 1 got more expensive"). A line's **own** cost is never
silently re-resolved against a current filament price or recipe just because a sibling line
changed — that remains the explicit, user-triggered *"Atualizar preços para os valores atuais"*
action. The one narrow, automatic exception is the `PER_ORDER` allocation dependency defined in
[ADR-0020 §A.7](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md): lines
that share a fee group are commercially coupled by construction, so a change to one line's basis
necessarily changes every other line's allocated share, whether the operator touched that other
line or not.

### Revision suffix

`RevisionIndex` 1 is the original and renders **no suffix**; index 2 renders `B`.
The suffix is bijective base-26 over `A..Z`:

```
index 1  → "A"  (never displayed)
index 2  → "B"
index 26 → "Z"
index 27 → "AA"
index 28 → "AB"
index 53 → "BA"
```

```csharp
public static string ToSuffix(int revisionIndex)   // 1 => "", 2 => "B", 27 => "AA"
{
    if (revisionIndex <= 1) return string.Empty;
    var n = revisionIndex;
    var sb = new StringBuilder();
    while (n > 0)
    {
        n--;                                    // bijective, not positional
        sb.Insert(0, (char)('A' + n % 26));
        n /= 26;
    }
    return sb.ToString();
}
```

Displayed number: `260906-4` (index 1), `260906-4B` (index 2), `260906-4C` (index 3).
`RevisionSuffix` is **persisted**, not computed on read, so a future change to the algorithm
cannot rewrite the identity of documents already issued.

---

## 3. Altering an APPROVED quote

This is the rule the brief asked to be defined explicitly. Behaviour depends on how far the
linked production order has gone.

> **H-001 closed by [ADR-0020](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md).**
> §3.0 below states the complete per-state matrix (the two grouped cases that follow left
> `CANCELED` undefined and disagreed with §1.5 about `DELIVERED`), §3.3 decides that
> `has_pending_revision` never blocks queue actions, and §3.4 decides what happens to actual
> consumption recorded against a canceled order.

### 3.0 The complete matrix

`R(n)` is the approved revision that owns the production order; `R(n+1)` is the newer revision.

| `R(n)` order state | Creating `R(n+1)` | Approving `R(n+1)` |
|---|---|---|
| *(no order)* | allowed | create order for `R(n+1)` |
| `QUEUED` | allowed; set `HasPendingRevision` | cancel `R(n)`'s order (`SUPERSEDED_BY_REVISION`, `SupersededByOrderId` = new order); create order for `R(n+1)` |
| `IN_PRODUCTION` | allowed; set `HasPendingRevision` | **rejected** — `PRODUCTION_ORDER_IN_PROGRESS` |
| `READY` | allowed; set `HasPendingRevision` | **rejected** — `PRODUCTION_ORDER_IN_PROGRESS` |
| `SHIPPED` | allowed; set `HasPendingRevision` | **rejected** — `PRODUCTION_ORDER_IN_PROGRESS` |
| `DELIVERED` | allowed; **no flag** | allowed; create order for `R(n+1)`; the delivered order is **left untouched** |
| `CANCELED` | allowed; **no flag** | allowed; create order for `R(n+1)`; the canceled order is **left untouched** |

Two rules govern every row: **creating** a revision is never blocked by the shop floor, and a
**terminal** order (`DELIVERED`, `CANCELED`) never blocks approval and is never mutated by it.
`SupersededByOrderId` is set **only** on an order actually canceled because it was superseded —
never on a delivered one, which was not replaced but fulfilled.

Because a new revision is a clone (§2), approving one after a `DELIVERED` order creates an order
for the new revision's **full** quantity; the system does not diff against what was already
delivered. Repeat business is usually better modelled as a **new quote**.

### 3.1 Case A — no production order, or the order is still `QUEUED`

**Automatic supersession.**

```
1. New revision R(n+1) is created (status GENERATED). R(n) keeps status APPROVED and
   receives the supersession pointer.
2. The existing ProductionOrder is flagged HasPendingRevision = true and shown as
   "aguardando nova aprovação" in the production queue. It is NOT canceled yet — the new
   revision may never be approved.
3. When R(n+1) is APPROVED:
     - the old order transitions to CANCELED with
       CancellationReason = SUPERSEDED_BY_REVISION and SupersededByOrderId = new order id;
     - a new ProductionOrder is created for R(n+1).
4. If R(n+1) is instead CANCELED or EXPIRED, the flag is cleared and the original order
   returns to the normal queue, still linked to the still-APPROVED R(n).
```

### 3.2 Case B — the production order is `IN_PRODUCTION`, `READY` or `SHIPPED`

**Blocking, not silent mutation.**

*(`DELIVERED` was listed here originally and is not: it is terminal, so by step 3 below approval
proceeds. Corrected in [ADR-0020 §A.3](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md);
see the matrix in §3.0.)*

```
1. Creating a new revision is ALLOWED — the commercial conversation must never be blocked by
   the shop floor. The new revision is created as GENERATED.
2. APPROVING that new revision is REJECTED with PRODUCTION_ORDER_IN_PROGRESS until the
   operator explicitly resolves the existing order:
       - cancel it (work in progress is written off, recorded with a reason), or
       - complete it (DELIVERED), accepting that it delivered the previous scope.
3. Once the old order reaches a terminal state, approval proceeds and creates a new order.
4. Every step is recorded: quote status history, production order status history, audit log.
```

Rationale: the alternatives are worse. Silently editing an order already being printed
misleads the shop floor; auto-canceling in-progress work destroys real material without a
decision; partially amending an order (a "change order" that diffs the two revisions and emits
a delta) is genuinely useful but is a multi-sprint feature with its own reconciliation rules.
It is recorded as a **deferred capability**, not as an omission.

### 3.3 `has_pending_revision` is advisory — it never blocks

The flag **blocks nothing**. It does not gate `QUEUED → IN_PRODUCTION` or any other production
transition; it drives the *"aguardando nova aprovação"* badge and a non-blocking warning when an
operator starts work while a revision is pending.

The flag means *a revision is pending approval*, and Case A step 2 is explicit that it may never
be approved. Idling a printer for a negotiation that may be abandoned costs real throughput; and
if the operator does start, Case B is exactly the safety valve — the **approval** is then blocked
until the order is resolved. This is the same warn-never-refuse posture stock already uses
([CR-13.11](CALCULATION-RULES.md#cr-1311--stock-is-advisory): "stock is advisory").

It is a **cached projection**, maintained in the same transaction as the revision transition that
changes it, and defined by:

```
order.HasPendingRevision ⇔ the order is non-terminal
                           AND its quote has a revision newer than the order's own source
                               revision whose status is non-terminal
                               (GENERATED | SENT | NEGOTIATING)
```

So it is set when a newer revision appears over a non-terminal order; cleared when that revision
reaches `CANCELED`, `EXPIRED` or `SUPERSEDED` and no other non-terminal newer revision remains
(Case A step 4 — the order returns to the queue with **no** state transition, having never been
canceled); consumed when the newer revision is `APPROVED` (§3.0 then applies); and never set on a
terminal order. `QuoteRevised`, `QuoteCanceled` and `QuoteExpired` each re-evaluate it
synchronously ([DOMAIN-MODEL §15](DOMAIN-MODEL.md#15-domain-events)).

### 3.4 Actual consumption recorded against a canceled order

Recorded actual consumption is an **immutable physical fact**. Canceling an order — whether by
the operator or by supersession — never reverses it, never deletes it and never re-points it:

- `production_order_item_actual_material` rows and the `InventoryMovement(Consumption)` they emitted stand
  permanently; the ledger is append-only ([ADR-0017](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md))
  and `unit_cost_at_consumption` was frozen at consumption time
  ([ADR-0006 §2](architecture/ADR-0006-estimated-vs-actual-cost.md)). Material really left the
  shelf. This is §4.2's "consumed material stays consumed", extended to the supersession path.
- Consumption is **never transferred** to the superseding order, which starts at zero actual
  consumption with its own planned BOM, so its variance measures only its own work.
- A canceled order can therefore carry real actual cost with no delivered output. That cost stays
  attributable to that order and must never be absorbed into the superseding order or into a
  `Sale` referencing a different revision.

How that write-off is **classified** in the actual-vs-estimated report — the same question as a
`FAILED` item's wastage (§4.4) — is **H-006**'s scope (due before S10), which must honour the
invariant above.

---

## 4. Production order status

> **Ownership split ([ADR-0020 §A.8](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md), BLOCKING-03 correction).**
> The `QuoteApproved` guard in §1.5 and the idempotent creation in §4.3 are part of the Quote
> approval transaction, so the minimum `ProductionOrder` persistence and state values they depend
> on are **S6** scope, not S9. Concretely, S6 owns: the aggregate, the `QUEUED` creation and the
> `QUEUED → CANCELED (SUPERSEDED_BY_REVISION)` transition, the full status **enum** (so the §1.5
> guard can type-check against `IN_PRODUCTION`/`READY`/`SHIPPED`/`DELIVERED`), and
> `has_pending_revision`. **S9** owns everything that moves an order through the rest of this
> state machine operationally — `QUEUED → IN_PRODUCTION → READY → SHIPPED → DELIVERED`, the
> printer/scheduling UX, items, planned/actual material and the shop-floor document. Until S9
> ships, an S6 order can be created and (if superseded while `QUEUED`) canceled, but nothing moves
> it past `QUEUED` under its own power.

### 4.1 States

| State | Meaning | Terminal |
|---|---|---|
| `QUEUED` | Created, waiting for the printer | no |
| `IN_PRODUCTION` | Printing/finishing started | no |
| `READY` | Finished and packed, awaiting dispatch | no |
| `SHIPPED` | Handed to the carrier / customer notified | no |
| `DELIVERED` | Confirmed with the customer | **yes** |
| `CANCELED` | Abandoned or superseded | **yes** |

### 4.2 Transitions

| From | To | Trigger | Guards |
|---|---|---|---|
| — | `QUEUED` | `QuoteApproved` event | idempotent on `quote_revision_id` |
| `QUEUED` | `IN_PRODUCTION` | user starts | sets `StartedAt` |
| `QUEUED` | `CANCELED` | user cancels / supersession | reason required |
| `IN_PRODUCTION` | `READY` | user completes | sets `CompletedAt`; actual consumption should be recorded (warn if absent) |
| `IN_PRODUCTION` | `CANCELED` | user cancels | reason required; consumed material stays consumed |
| `READY` | `SHIPPED` | user ships | sets `ShippedAt` |
| `READY` | `IN_PRODUCTION` | user reopens | correction path; recorded |
| `READY` | `CANCELED` | user cancels | reason required |
| `SHIPPED` | `DELIVERED` | user confirms | sets `DeliveredAt` |
| `SHIPPED` | `CANCELED` | user cancels | reason required (return) |
| `DELIVERED` | — | — | terminal |
| `CANCELED` | — | — | terminal |

```
QuoteApproved
     │
     ▼
 ┌────────┐  start  ┌───────────────┐ complete ┌───────┐  ship  ┌─────────┐ confirm ┌───────────┐
 │ QUEUED ├────────►│ IN_PRODUCTION ├─────────►│ READY ├───────►│ SHIPPED ├────────►│ DELIVERED │
 └───┬────┘         └───────┬───────┘◄─────────┴───┬───┘        └────┬────┘         └───────────┘
     │                      │          reopen      │                 │
     └──────────────────────┴──────────────────────┴─────────────────┘
                                  cancel
                                    ▼
                               ┌──────────┐
                               │ CANCELED │
                               └──────────┘
```

### 4.3 Idempotency of creation

`production.production_order.quote_revision_id` carries a **unique constraint**. The
`QuoteApproved` handler performs:

```sql
INSERT INTO production.production_order (...) VALUES (...)
ON CONFLICT (quote_revision_id) DO NOTHING;
-- then SELECT the row and return it
```

so a retried request, a replayed event, a double-clicked button and a concurrent approval all
converge on exactly one order. The handler returns the existing order rather than failing —
approval is naturally idempotent from the user's point of view.

Three layers, in the order they take effect:

1. **The status guard plus the row lock.** Approval updates the revision row, so two concurrent
   approvals serialize on it and the second is rejected with `QUOTE_REVISION_ALREADY_DECIDED`
   before any insert is attempted.
2. **`UNIQUE (quote_revision_id)`** — defense in depth.
3. **`ON CONFLICT DO NOTHING`** — the replay path.

> **Never catch the unique violation and continue.** In PostgreSQL a statement error aborts the
> transaction (`25P02`); every subsequent command fails until `ROLLBACK`, so the following
> `SELECT` and the `COMMIT` would both fail. Only `ON CONFLICT` (used here) or an explicit
> `SAVEPOINT` / `ROLLBACK TO SAVEPOINT` is valid —
> [ADR-0012 §8](architecture/ADR-0012-domain-events-and-outbox.md#11-the-quote-approval-invariant).

### 4.4 Item status

`ProductionOrderItem.Status ∈ {PENDING, PRINTING, DONE, FAILED}`. Item states are advisory and
do not drive the order state machine in v1 — the operator moves the order. `FAILED` items
justify a re-print and are the natural place where wastage becomes visible as actual
consumption without a corresponding delivered unit.

---

## 5. Other lifecycles

### 5.1 Cost experiment (Laboratory)

```
DRAFT ──save──► SAVED ──convert──► CONVERTED (terminal for editing)
  │               │
  └──clone────────┴──► new DRAFT (SourceExperimentId set)
SAVED ──archive──► ARCHIVED
```
`CONVERTED` records `ConvertedToProductId`. A converted experiment is read-only; cloning it
produces a new `DRAFT`.

### 5.2 Document template version

```
DRAFT ──publish──► PUBLISHED ──archive──► ARCHIVED
```
- Only `PUBLISHED` versions may render production documents.
- A `PUBLISHED` version is **immutable**; editing creates a new `DRAFT` at `VersionNumber + 1`.
- Archiving a version never invalidates documents already generated from it: every
  `GeneratedDocument` keeps `DocumentTemplateVersionId`, so any past PDF remains reproducible.

### 5.3 Sale

```
CONFIRMED ──cancel──► CANCELED
```
Sales are financial facts; they are canceled, never deleted, and a canceled sale is excluded
from every revenue and profit metric while remaining visible in listings. Cancellation requires a
nonblank reason, actor, timestamp, status history and audit; it does not mutate ProductionOrder.
Only an `APPROVED` QuoteRevision is explicitly sale-eligible. Supersession does not change an
approved revision's eligibility, and approval itself never creates a Sale.

### 5.4 AI insight run

```
PENDING ──dispatch──► RUNNING ──► SUCCEEDED
                          └─────► FAILED (ErrorMessage recorded, no partial results kept)
```
Driven by the outbox; retried with backoff up to the configured attempt limit.

### 5.5 Outbox message

*(Added 2026-09-07, gate blocker B-003. Full specification in
[ADR-0012 Part II](architecture/ADR-0012-domain-events-and-outbox.md#part-ii--the-transactional-outbox).)*

```
                    ┌──────────────────────────────────────────┐
                    │  retryable failure: available_at += backoff
                    ▼                                          │
   enqueue     ┌─────────┐   claim (lease)   ┌────────────┐    │
  ───────────► │ PENDING │ ────────────────► │ PROCESSING │ ───┤
  (in the      └─────────┘  FOR UPDATE       └─────┬──────┘    │
   business         ▲        SKIP LOCKED           │ success   │
   transaction)     │                              ▼           │
                    │ lease expired          ┌───────────┐     │
                    │ (crash) → reclaim      │ PROCESSED │     │
                    │                        └───────────┘     │
                    │  manual requeue (Owner)                  │
                    │                          attempts exhausted
                    │                          or non-retryable
                    │                                          ▼
                    │                                    ┌────────┐
                    └────────────────────────────────────┤ FAILED │
                                                         └────────┘
```

| Transition | Trigger | Guards |
|---|---|---|
| — → `PENDING` | enqueued **inside** the business transaction | commits with the business row or not at all |
| `PENDING` → `PROCESSING` | dispatcher claim | **eligible only**: `available_at <= now()` **and** `attempt_count < max_attempts`; sets `processing_token` + `lease_until`, increments `attempt_count` |
| `PROCESSING` → `PROCESSED` | consumer success **with a matching `processing_token`** | terminal; prunable after 90 days |
| `PROCESSING` → `PENDING` | retryable failure **and** `attempt_count < max_attempts` | `available_at = now + backoff` (1 m / 5 m / 30 m / 2 h) |
| `PROCESSING` → `FAILED` | retryable failure **and** `attempt_count >= max_attempts` | `RETRY_BUDGET_EXHAUSTED` → **never** returned to `PENDING` |
| `PROCESSING` → `PENDING` | **lease expired** and `attempt_count < max_attempts` | reclaim sweep; the attempt is **not** refunded |
| `PROCESSING` → `FAILED` | **lease expired** and `attempt_count >= max_attempts` | `LEASE_EXPIRED_ON_FINAL_ATTEMPT` |
| `PROCESSING` → `FAILED` | `NonRetryableOutboxException` | immediate, budget irrelevant |
| `FAILED` → `PENDING` | **manual requeue**, `Owner` only, reason required | **`execution_generation` incremented**, `attempt_count` **reset to 0**, `failure_disposition` cleared; `id` and `idempotency_key` **unchanged**; `last_error` and attempt history **preserved**; audit row written |

**Every worker-driven transition carries `AND processing_token = :token`** — the lease fence.
0 rows affected means the caller is stale (its lease expired and another worker now owns the
message): it logs and abandons, and **must not** overwrite the new owner's state
([ADR-0012 §15](architecture/ADR-0012-domain-events-and-outbox.md#15-lease-fencing--the-correction)).

`PROCESSED` and `FAILED` are terminal; only an explicit administrative action moves `FAILED`
back. On entering `FAILED` the message also receives a **disposition**:

| `failure_disposition` | Meaning | Set by |
|---|---|---|
| `ACTIVE` | unresolved; needs a human; counts toward `degraded` health | the system |
| `DISMISSED` | a human decided this effect must never be retried; **not claimable** | `Owner`, reason required |

There is **no `RESOLVED` disposition**. A requeued message that succeeds ends at
`status = PROCESSED` with `failure_disposition = NULL` → success is what "resolved" means, and
the schema forbids a disposition outside `FAILED`. History survives in
`outbox_message_attempt`, keyed by generation.

**Retry rounds.** `attempt_count` is the budget of the **current** round and resets on requeue;
historical attempts are identified by `(message, execution_generation, attempt_number)`, so
generation 2 attempt 1 never collides with generation 1 attempt 1. A requeue opens a new
generation and leaves `idempotency_key` untouched — it retries the *same* business effect
([ADR-0012 §19](architecture/ADR-0012-domain-events-and-outbox.md#19-attempt-history-and-execution-generations)).

**One terminal-attempt rule:** `attempt_count >= max_attempts` yields `FAILED`, identically for
an explicit retryable failure and for a crash detected by lease expiry.

Dismissal **never deletes** the message, its `last_error` or its attempt history — it records a
decision alongside the facts and stops the noise.

Retry delivery is at-least-once, so **every consumer must be idempotent** — document renders key
on `render_request_id` so a retry cannot produce a second `ISSUED` document.

---

## 6. Error codes emitted by transitions

| Code | HTTP | Meaning |
|---|---|---|
| `QUOTE_REVISION_NOT_CURRENT` | 409 | Attempted transition on a superseded revision |
| `QUOTE_REVISION_ALREADY_DECIDED` | 409 | Revision is terminal |
| `QUOTE_REVISION_EXPIRED` | 409 | Validity elapsed |
| `QUOTE_INVALID_TRANSITION` | 409 | Target state not reachable from current state |
| `QUOTE_HAS_NO_ITEMS` | 422 | Approval/sending of an empty quote |
| `QUOTE_ITEM_INVALID_PRICE` | 422 | Non-positive final price |
| `PRODUCTION_ORDER_IN_PROGRESS` | 409 | §3 case B block |
| `PRODUCTION_INVALID_TRANSITION` | 409 | Target state not reachable |
| `DOCUMENT_TEMPLATE_NOT_PUBLISHED` | 409 | Render attempted with a draft template |
| `PRICING_INVALID_DENOMINATOR` | 422 | commission + margin ≥ 1 |
| `CONCURRENCY_CONFLICT` | 409 | The aggregate changed since it was loaded ([ADR-0011 §2](architecture/ADR-0011-identifiers-and-concurrency.md)) |
| `LAST_OWNER_PROTECTED` | 409 | Attempt to remove, deactivate or demote the last active Owner |
| `DOMAIN_EVENT_WAVE_LIMIT_EXCEEDED` | 500 | Event cycle detected; programming error, transaction rolled back |

Codes are stable strings; the frontend maps them to pt-BR messages. Message text is never
matched programmatically.
