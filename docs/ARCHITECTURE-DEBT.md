# ARCHITECTURE DEBT — Verce 3D | Laboratório de Custos

Tracked architectural decisions that are **known, deliberately deferred, and owed by a specific
sprint**. This is not a wish list: every entry blocks that sprint's Definition of Done.

**Rules**

1. An agent working sprint `SN` **must** resolve, as part of that delivery:
   **(a)** every row whose **decision deadline** is `SN`;
   **(b)** every row marked **`Before S(N+1)`** — it is an exit requirement of *this* sprint;
   **(c)** every row whose **implementation deadline** is `SN`.
   Resolution means updating the affected `docs/` files, and adding an ADR when the decision is
   architectural. A finding may appear twice, with the decision owed earlier than the
   implementation. The orchestrator and the external reviewer enforce the same three checks.
2. A row is closed only when the decision is written into `docs/` — not when code happens to
   imply an answer.
3. Nothing here may be silently resolved by implementation choice. If an implementation forces a
   decision early, record it here and in the relevant document.
4. Rows may be added by any sprint that discovers deferred work. Do not delete rows; mark them
   **Closed** with the delivery that closed them.

---

## 1. Deferred findings from the external architecture gate (Codex Sol, 2026-09-07)

The gate returned **NEEDS_FIXES** with 6 blockers and 10 HIGH findings. The blockers were closed
by mission `VERCE3D-M0-S0-ARCHITECTURE-GATE-CORRECTIONS-001`. The HIGH findings below were
**explicitly out of scope** for that mission and remain open.

> **Deadlines are authoritative.** They were set by the external re-gate
> (`VERCE3D-M0-S0-ARCHITECTURE-GATE-CORRECTIONS-002`, §61) and supersede the derived deadlines
> used in the first correction pass. Several findings carry **two** deadlines, because deciding
> the semantics and implementing them belong to different sprints.

**How to read the two columns**

| Notation | Exact meaning | Whose exit criterion |
|---|---|---|
| **Decision deadline = `SN`** | The semantics must be written into `docs/` (and an ADR where architectural) **during S(N)**, no later than that sprint's gate. | **S(N)** cannot be closed while it is open. |
| **Decision deadline = `Before SN`** | It must be settled **before S(N) begins** — S(N) is built on top of it. It is therefore an **exit requirement of S(N-1)** (or earlier). | **S(N-1)** cannot be closed while it is open, **and S(N) must not start.** |
| **Implementation deadline = `SN`** | The behaviour must be complete before **S(N)** can be closed. | **S(N)**. |

Worked example: **H-001 carries `Before S6`.** That means **S5 cannot be closed** while the
H-001 decision is open, and **S6 must not begin** until it is resolved. It is not an S6 task.

The two columns are independent: a decision may be owed several sprints before the
implementation that consumes it.

A decision deadline is binding even when nothing ships: an agent finishing that sprint with the
row unresolved has not met its Definition of Done, and the reviewer should reject the delivery.

| ID | Area | Decision required | **Decision deadline** | **Implementation deadline** | Status |
|---|---|---|---|---|---|
| **H-001** | Quoting / Production | Revision ↔ production lifecycle: behaviour when a superseded revision has an order in each state; whether `has_pending_revision` blocks queue actions; what happens to actual consumption already recorded against a canceled order | **Before S6** | S9 | Open |
| **H-002** | Sales | Sale lifecycle: when a sale may be created from a revision, whether cancellation reverses stock, interaction with a delivered production order, whether partial/multiple sales per revision are permitted | S8 | S8 | Open |
| **H-003** | Sales / Pricing | Mixed channel and fulfilment: an item-level channel override changes fees per line — how a single sale spanning two channels reports revenue, fees and margin, and whether it is permitted at all | S8 | S8 | Open |
| **H-004** | Pricing | `PER_ORDER` fixed-fee allocation: rounding of `fixedFee / quantity` across lines, residual-cent assignment, behaviour when quantity changes on a revision | S5 | S5 | Open |
| **H-005** | Pricing | Interaction of manual price override, item discount and bracket resolution: precedence, whether an override re-resolves the bracket, how effective margin is reported when all three apply | S5 | S5 | Open |
| **H-006** | Costing / Production | Actual total cost composition: manual lines with no actual counterpart, partial reconciliation, failed units, exact exclusion of wastage from the actual side | **Before S10** | S11 | Open |
| **H-007 A** | Inventory | Stock movement sign convention and `StockCount` concurrency (two counts of one material racing) | S3 | S3 | **Closed (S3)** — see [ADR-0017](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md) §1, §3 |
| **H-007 B** | Inventory / Production | Actual-consumption idempotency: preventing a production item's consumption being recorded twice | S11 | S11 | Open |
| **H-008 A** | Finance | Expense / double-count model: treatment boundaries and prevention of operator misclassification | S8 | S8 | Open |
| **H-008 B** | Reporting | Cash Result vs Product Margin consistency across every report surface | **Before S12** | S12 | Open |
| **H-009 A** | Quoting | **Commercial** conversion semantics: quotes reopened after a terminal state, quotes whose current revision returns to `GENERATED` | **Before S6** | S6 | Open |
| **H-009 B** | Reporting | Conversion reporting: cohort labelling of incomplete periods, `null` handling | S12 | S12 | Open |
| **H-010** | Energy / Costing | Energy tariff availability before the Energy module exists: S4–S9 must price energy from the seeded default tariff without hard-coding a value, and S10 must not retroactively change quotes priced earlier | **Before S4** | S4 | Open |

**Why some decisions precede their implementation.** H-001 and H-009 A are due *before S6*
because S6 builds the quote engine: if revision-vs-production behaviour or the commercial meaning
of a conversion outcome is settled afterwards, the quote state machine has to be rebuilt rather
than extended. H-006 is due before S10 for the same reason — the energy module writes the actual
side. H-008 B is due before S12 so the dashboard is not built on two silently different profit
definitions.

### Cross-cutting note for H-010

This one has the earliest deadline and is the easiest to get wrong. S4 ships the cost engine
while tariff versioning arrives only in S10. The constraint already stated in
[ROADMAP](ROADMAP.md#s10--energy) is that S4–S9 read the **seeded default tariff** and freeze its
price into the snapshot, so no sprint before S10 may embed a literal price per kWh. S4's delivery
must make that explicit rather than leaving it implied.

---

## 2. Product decisions deferred by design (not gate findings)

Recorded in S0 as deliberate non-goals. They are not defects and have no assigned sprint until
the product asks for them.

| Item | Where decided | Note |
|---|---|---|
| Weighted-average inventory costing | [DATA-DICTIONARY](DATA-DICTIONARY.md#purchase-cost-policy-deferred-to-s4) | Needs per-movement consumption tracking the shop floor does not record yet; S3 generalized this from filament-only to all of Inventory ([ADR-0017](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md) §5) |
| Global quote-level discount | [CR-08.2](CALCULATION-RULES.md) | Would need allocation back to items; a second rounding source |
| Production change orders (partial amendment) | [STATE-MACHINES §3](STATE-MACHINES.md#3-altering-an-approved-quote) | v1 blocks with `PRODUCTION_ORDER_IN_PROGRESS` |
| Item-level technical highlights | [DOMAIN-MODEL §8](DOMAIN-MODEL.md#8-quoting-module) | v1 holds them at revision level |
| SVG brand assets | [ADR-0015 §7](architecture/ADR-0015-brand-assets-and-application-branding.md) | Needs a sanitizing validator before enabling |
| Outbox file reference counting / deletion | [ADR-0016 §3](architecture/ADR-0016-document-render-snapshots.md) | Files are never deleted in v1 |
| Multi-tenancy | [ADR-0009 §2](architecture/ADR-0009-authentication-strategy.md) | No `organization_id`; migration path documented |
| Multi-currency | [ADR-0002 §7](architecture/ADR-0002-money-precision-and-rounding.md) | BRL implicit |
| Stock as a blocking constraint | [DOMAIN-MODEL §3.4](DOMAIN-MODEL.md#34-stock) | v1 warns, never refuses |
| KMS/HSM for the wrapping certificate | [SECURITY §5.1](SECURITY.md#51-storage) | Disproportionate for a single-container deployment |

---

## 3. Inputs still missing

| Item | Needed by | Impact if still missing |
|---|---|---|
| `VERCE_Proposta-Modelo_1.pdf` | **S7** | The default proposal's structure is fully specified; only the visual tokens (palette, typography, spacing, logo lockup) are pending — see [DEFAULT-PROPOSAL-TEMPLATE §0 and §8](DEFAULT-PROPOSAL-TEMPLATE.md) |
| Font licensing for document embedding | **S7** | Typeface choice cannot be finalized; renderer requires embedded/self-hosted fonts |
| Smart-plug device selection | **S10** | `IEnergyProvider` exists; no vendor code may be written before the device is chosen |
| Confirmation of marketplace fee structures (Shopee, Mercado Livre) | **S8** | Determines whether `PER_UNIT` remains the correct default and whether brackets are needed |
| The external gate report as a file | informational | Deadlines in §1 are now **authoritative**, taken from the re-gate mission text; a report file would only add rationale |
