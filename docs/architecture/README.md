# Architecture Decision Records

An ADR records **why** a decision was made, what was rejected, and what it costs.

## Rules

1. ADRs are **append-only**. Never rewrite the decision of an accepted ADR — write a new ADR
   that supersedes it, and mark the old one `Superseded by ADR-XXXX`.
   **One exception:** an ADR that is *factually wrong* (it asserts behaviour the platform does
   not have) is corrected in place, because leaving a false statement standing would mislead
   every future reader. The correction carries a dated `> Corrected …` note stating what was
   wrong and why, so the history is preserved rather than erased. ADR-0011 (`xmin` cannot detect
   child mutations; "every table has a uuid PK" was false) and ADR-0012 (a single `SaveChanges`
   cannot persist handler-created aggregates; catching a unique violation aborts a PostgreSQL
   transaction) were corrected this way on 2026-09-07 after the external gate.
2. Any deviation from an accepted ADR requires a superseding ADR. Code that contradicts an ADR
   is a defect, not a new decision.
3. Every ADR states its rejected alternatives. An ADR without alternatives is a note, not a
   decision.
4. Every ADR ends with compliance checks — the tests or reviews that keep it true.

## Status values

`Proposed` · `Accepted` · `Superseded by ADR-XXXX` · `Deprecated`

All S0 ADRs are **Accepted (pending external review)**: the architecture agent does not approve
its own work; a different model reviews this delivery.

## Index

| ADR | Title | Status | Key rule |
|---|---|---|---|
| [0001](ADR-0001-system-architecture.md) | System Architecture | Accepted | Modular monolith, one DbContext, schema per module, boundaries enforced by tests |
| [0002](ADR-0002-money-precision-and-rounding.md) | Money, Precision and Rounding | Accepted | `decimal`/`numeric`, percent as fraction, half-up, engine is the only calculator |
| [0003](ADR-0003-cost-and-price-snapshots.md) | Cost and Price Snapshots | Accepted | Typed columns for reporting + JSONB for explanation; history never recomputed |
| [0004](ADR-0004-quote-numbering-and-revisioning.md) | Quote Numbering and Revisioning | Accepted | Atomic daily counter; bijective base-26 suffix; edit = clone + new revision |
| [0005](ADR-0005-marketplace-fee-rules.md) | Marketplace Fee Rules | Accepted | Fees are versioned data; direct sale is the zero-fee case of one formula |
| [0006](ADR-0006-estimated-vs-actual-cost.md) | Estimated vs Actual Cost | Accepted | Two storage locations, variance always derived |
| [0007](ADR-0007-document-template-engine.md) | Document Template Engine | Accepted | JSON template → HTML → Chromium → PDF; versions immutable once published |
| [0008](ADR-0008-ai-integration-and-secret-handling.md) | AI Integration and Secret Handling | Accepted | Key backend-only, encrypted, never logged; aggregated payload, no PII |
| [0009](ADR-0009-authentication-strategy.md) | Authentication Strategy | Accepted | Identity + cookies, single org, multi-user, permission constants |
| [0010](ADR-0010-audit-strategy.md) | Audit Strategy | Accepted | Business history + generic audit log; no event sourcing |
| [0011](ADR-0011-identifiers-and-concurrency.md) | Identifiers, Concurrency and Deletion | Accepted · **revised 2026-09-07** | PK by table category (B-006); explicit aggregate `version`, not `xmin` (B-002); soft delete only on master data |
| [0012](ADR-0012-domain-events-and-outbox.md) | Domain Events, Unit of Work and the Outbox | Accepted · **revised 2026-09-07** | Multi-wave UoW algorithm (B-001); full outbox lifecycle with lease, retry, terminal `FAILED`, idempotency (B-003) |
| [0013](ADR-0013-expense-inventory-double-counting.md) | Expense, Inventory and Double Counting | Accepted | Accounting treatment flag; one lot, one expense; two separate reports |
| [0014](ADR-0014-frontend-architecture-and-theming.md) | Frontend Architecture and Theming | Accepted | Frontend never computes money; themes are token sets, never behaviour |
| [0015](ADR-0015-brand-assets-and-application-branding.md) | Brand Assets and Application Branding | Accepted | Versioned assets; documents reference the version; eight-stage upload pipeline; no SVG in v1 |
| [0016](ADR-0016-document-render-snapshots.md) | Document Render Snapshots and Immutability | Accepted | Layout, branding and content frozen at issue; issued documents never regenerated |

## Template

```markdown
# ADR-XXXX — Title

- **Status:** Proposed | Accepted | Superseded by ADR-YYYY
- **Date:** YYYY-MM-DD
- **Sprint:** SN

## Context
The forces at play. What makes this decision necessary and non-obvious.

## Decision
What we will do, precisely enough to implement.

## Alternatives considered
Each rejected option and the specific reason it was rejected.

## Consequences
Positive, negative and neutral. Be honest about the negative.

## Compliance checks
The tests or reviews that keep this decision true.
```
