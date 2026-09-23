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
| **H-001** | Quoting / Production | Revision ↔ production lifecycle: behaviour when a superseded revision has an order in each state; whether `has_pending_revision` blocks queue actions; what happens to actual consumption already recorded against a canceled order | **Before S6** | **S6 (Production Core) / S9 (operational)** | **Decision recorded (S6 entry, corrected)** — [ADR-0020 §A](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md); the minimum `ProductionOrder` persistence the `QuoteApproved` transactional invariant needs (§A.8) is now **S6** scope; the operational shop floor (items, material, printer/scheduling UX) remains **S9** |
| **H-002** | Sales | Sale lifecycle and conversion cardinality | S8 | S8 | **Closed (S8A)** — decision in [ADR-0021](architecture/ADR-0021-s8a-sale-conversion-and-expense-model.md), implemented and committed in `1eee9c5` |
| **H-003** | Sales / Pricing | Mixed-channel commercial order rule | S8 | S8 | **Closed (S8A)** — decision in [ADR-0022](architecture/ADR-0022-pricing-override-discount-bracket-precedence.md), implemented and committed in `1eee9c5` |
| **H-004** | Pricing | `PER_ORDER` fixed-fee allocation: rounding of `fixedFee / quantity` across lines, residual-cent assignment, behaviour when quantity changes on a revision | S5 (single-item case) / **S6** (cross-line case) | S5 (single-item case) / **S6** (cross-line case) | **Decision recorded (S6 entry)** — single-item case closed by S5; cross-line remainder by [ADR-0020 §C](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md) + [CR-07.7](CALCULATION-RULES.md#cr-077--per_order-fee-allocation-across-lines); implementation owed by S6 |
| **H-005** | Pricing | Override, discount and bracket precedence | **S8** | **S8** | **Closed (S8A)** — decision in [ADR-0022](architecture/ADR-0022-pricing-override-discount-bracket-precedence.md), implemented and committed in `1eee9c5` |
| **H-006** | Costing / Production | Actual total cost composition: manual lines with no actual counterpart, partial reconciliation, failed units, exact exclusion of wastage from the actual side | **Before S10** | S11 | Open |
| **H-007 A** | Inventory | Stock movement sign convention and `StockCount` concurrency (two counts of one material racing) | S3 | S3 | **Closed (S3)** — see [ADR-0017](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md) §1, §3 |
| **H-007 B** | Inventory / Production | Actual-consumption idempotency: preventing a production item's consumption being recorded twice | S9 | S9 | Open |
| **H-008 A** | Finance | Expense / double-count model | S8 | S8 | **Closed (S8A)** — decision in [ADR-0021](architecture/ADR-0021-s8a-sale-conversion-and-expense-model.md), implemented and committed in `1eee9c5` |
| **H-008 B** | Reporting | Cash Result vs Product Margin consistency across every report surface | **Before S12** | S12 | Open |
| **H-009 A** | Quoting | **Commercial** conversion semantics: quotes reopened after a terminal state, quotes whose current revision returns to `GENERATED` | **Before S6** | S6 | **Decision recorded (S6 entry)** — [ADR-0020 §B](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md) + [DATA-DICTIONARY §4.1](DATA-DICTIONARY.md#41-conversion-rate-taxa-de-conversão); implementation owed by S6 |
| **H-009 B** | Reporting | Conversion reporting: cohort labelling of incomplete periods, `null` handling | S12 | S12 | Open |
| **H-010** | Energy / Costing | Energy is absent before S10: the CostEngine has no energy component, the Energy module is a stub and no seeded tariff exists. Freeze how unavailable energy is represented without misreporting it as zero | **Before S10** | S10 | Open |
| **H-011** | Documents | S7 was frozen to persist the default proposal as `document_type`/`document_template`/`document_template_version` DATA and render it through a generic block-tree walker over a closed binding catalogue (ADR-0007 §7, S7/S14 scope authority gate 2026-09-21). | **Before S14** | S14 | **Closed (S7 template-engine final pass)** — see note below |

### Resolution note for H-011 (S7 template-engine final pass, 2026-09-21)

An earlier S7 correction pass (`VERCE3D-S7-FINAL-AUTHORITY-CORRECTION-MACRO-001`) fixed the
concrete defects an independent review found — terms/proposal content frozen at issue,
`GeneratedDocument` identity, the outbox render path, pagination proofs — but left the compiled
`QuotePdfHtmlTemplate` renderer in place and opened this row to defer the persisted template
engine to S14. Mission `VERCE3D-S7-TEMPLATE-ENGINE-FINAL-PASS-001` overruled that deferral as
contrary to the frozen S7/S14 scope authority decision and built the engine within S7:

- `documents.document_type` / `documents.document_template` / `documents.document_template_version`
  now persist the default proposal as data (`DocumentType`, `DocumentTemplate`,
  `DocumentTemplateVersion` in `Verce.Modules.Documents`), seeded idempotently by
  `DocumentsSeedService` on startup, matching the established `IHostedService` seeding
  convention.
- `QuoteBindingCatalogue` is a closed, structurally enforced binding surface for the `QUOTE`
  document type; unknown paths are rejected both by `QuoteRenderContext.ResolveScalar` at render
  time and by `DocumentTemplateValidator` at template-publish time — proven by
  `QuoteBindingCatalogueTests`.
- `BlockTreeRenderer` is a generic block-tree walker (zero Quoting-specific knowledge; it depends
  only on the `IRenderContext` interface) covering all 9 required block families, the closed
  `visibleWhen` predicate grammar (`VisibleWhenEvaluator`, depth-3 limit), and a genuinely
  repeating page header/footer via Playwright's native `headerTemplate`/`footerTemplate`
  mechanism — proven against real multi-page Chromium+PdfPig output in
  `QuotePdfPaginationTests.AssertDocumentHeaderRepeatsOnEveryPage`.
- `QuotePdfHtmlTemplate.cs` (the compiled renderer) is deleted; `QuotePdfService` now resolves
  the published default `DocumentTemplateVersion` and renders through `BlockTreeRenderer`
  exclusively.

S14 therefore starts from the persistence/binding-catalogue/block-renderer infrastructure already
in place and is scoped to the **authoring UI only**, as ADR-0007 §6 originally assumed — the
larger S14 scope this row previously flagged no longer applies.

### Resolution note for S8 findings (S8A completion, 2026-09-23)

S8A implemented and independently certified H-002, H-003, H-005 and H-008 A, including the
canonical Sale lifecycle/conversion, one-channel commercial order rule, override/discount/bracket
precedence and Expense double-count guards. Commit `1eee9c5` is the shipped baseline. S8B's
[ADR-0023](architecture/ADR-0023-s8b-commerce-foundation.md) consumes those authorities and does
not reopen them.

### Resolution note for H-004 and H-005 (S5)

S5 built `Verce.Modules.Pricing.PricingEngine`, `FeeRule`/`FeeRuleVersion` and the full P1–P10
golden corpus (see [ADR-0019](architecture/ADR-0019-s5-product-recipe-and-pricing-engine.md)).
That is the single-item, single-fee-version case both rows partly describe, and it is fully
decided and tested: CR-07.3's `PER_UNIT`/`PER_ORDER` split (unchanged since S0) is persisted on
`FeeRuleVersion.FixedFeeApplication`, and `PricingEngine` applies `PER_UNIT` correctly for every
golden case.

What S5 could **not** decide, and why the original S5 deadline was wrong for the rest of each row:

- **H-004's "across lines" / "quantity changes on a revision" scope** is a `QuoteItem`
  allocation problem — S5 has no `Quote`, `QuoteRevision` or `QuoteItem` (explicitly out of
  scope for this sprint). There is no "line" or "revision quantity" for `PER_ORDER` to allocate
  across yet. This remainder is retargeted to **S6**, the sprint that actually builds `QuoteItem`.
- **H-005** names three concepts — manual price override, item discount, bracket resolution —
  and S5 confirmed (ADR-0019 §3) that `PriceBracket` is deferred to **S8**, matching what
  CR-07.5 already said. Item discount is `QuoteItem` scope (S6). Deciding "how all three
  interact" before any of the three exists is not a decision S5 can make in good faith; it is
  retargeted to **S8**, when bracket resolution — the last of the three to be built — actually
  exists to interact with.

This is a deadline **correction**, not a deferral of convenience: the original S5 deadline for
both rows predates ADR-0019's own decision (made in this same delivery) that brackets are S8
scope, so S5 could not have met it without inventing bracket/discount semantics that S8 would
then have to redo. Flagged explicitly here, and in the S5 delivery report's Open Decisions
section, rather than silently left `Open` against a deadline this same delivery proved
unmeetable.

### Resolution note for H-001, H-009 A and the H-004 remainder (S6 entry, 2026-09-20)

Mission `VERCE3D-S6-ENTRY-ARCHITECTURE-001` resolved all three rows as a decision-only delivery
(no runtime code, no migration), recorded in
[ADR-0020](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md). Independent
review (`VERCE3D-S6-ENTRY-CODEX-SOL-REVIEW-001`) found three blocking contradictions in that first
draft; mission `VERCE3D-S6-ENTRY-ARCHITECTURE-CORRECTIONS-001` (same day) corrected all three plus
three secondary imprecisions, in place, in the same ADR. The description below is the
**corrected** state:

- **H-001** — §A gives the complete supersession × production-order matrix (including the
  `CANCELED` and `DELIVERED` rows that had no defined behaviour), decides that
  `has_pending_revision` is advisory and **never** blocks a queue action, and freezes that actual
  consumption recorded against a canceled order is never reversed, deleted or transferred. It
  also corrects the §1.5-vs-§3 contradiction about `DELIVERED` in
  [STATE-MACHINES](STATE-MACHINES.md). **Correction pass:** two more contradictions were found and
  fixed — (a) a stray STATE-MACHINES §2 guard blocked revision *creation* on production-order
  state, contradicting the A.2 matrix itself; removed, since only *approval* can be blocked
  (§A.7). (b) approval requires synchronous `ProductionOrder` persistence in the same transaction
  (ADR-0012 §2), which cannot hold against an aggregate assigned entirely to S9; the **minimum**
  Production Core (§A.8) — the aggregate, its full status enum, `QUEUED` creation, the
  `QUEUED → CANCELED (SUPERSEDED_BY_REVISION)` transition and `has_pending_revision` — now moves
  into **S6**; the *operational* shop floor (items, planned/actual material, printer/scheduling
  UX) stays **S9**. The *decision* deadline (`Before S6`) is met.
- **H-009 A** — §B derives the quote-level commercial outcome (`WON`/`LOST`/`OPEN`) from the
  append-only status history instead of the current revision's status, giving **non-retroactive**
  reporting: revising an approved quote no longer removes a won deal from a closed period, and
  reviving an expired one no longer removes the loss already recorded. **Correction pass:** the
  first draft called the whole outcome "monotonic", which is imprecise — only `hasEverWon` is
  absorbing; a never-won quote's *current* classification can legitimately cycle
  `OPEN → LOST → OPEN` as it expires and is revived. **Second correction pass (product decision,
  Option B):** the same-period cardinality is now explicit — for conversion KPIs a quote
  contributes **at most one decision per `QuoteId` + reporting period**, with precedence
  `WON > LOST > no contribution`, so expire-then-cancel inside one month is one loss (not two) and
  expire-then-approve inside one month is one win with zero losses; the append-only history is
  untouched, the deduplication is a read-time rule only (see
  [DATA-DICTIONARY §4.1](DATA-DICTIONARY.md#41-conversion-rate-taxa-de-conversão)). Approval makes
  a revision *sale-eligible*; it never creates a `Sale`, and S6 emits no commercial-conversion
  event. Implementation is owed by **S6**.
- **H-004 remainder** — §C freezes that a `PER_ORDER` fee is charged exactly once per revision and
  allocated across lines in proportion to line estimated cost, with floor + largest-remainder
  residual cents and a `line_number` tie-break ([CR-07.7](CALCULATION-RULES.md#cr-077--per_order-fee-allocation-across-lines)).
  This also corrects a latent double-count in CR-07.3/CR-08.3 that charged the order fee once per
  line. S5's single-line behaviour is unchanged. **Correction pass:** the allocation algorithm and
  vectors are unchanged; two overstated claims were qualified — "the customer pays the fee exactly
  once" is now stated as the exact partition invariant `Σ allocatedOrderFee_i = rawOrderFee`
  (§C.1), and "every line's price rises by the same percentage" is now scoped to the shared
  pricing basis, exact only when margin is uniform across the group (§C.3). A new §A.7/§C.6 also
  names precisely which lines recalculate when a revision is constructed (the "pricing-affected
  line" and dependency-closure concepts), closing the "editing a draft revision" ambiguity a
  reviewer flagged against ADR-0003's verbatim-copy rule. Implementation is owed by **S6**.

**S6 architecture entry gate: CLEAR.** Every `Before S6` decision deadline is met, and all three
independent-review blocking findings against the S6-entry package are resolved. This means S6
implementation may begin *after independent re-review of the corrected ADR-0020* — it does not
mean S6 is implemented, and it does not close the S6/S9 implementation deadlines above.

**S6 implementation deadline: MET (2026-09-20, post-correction).** `VERCE3D-S6-IMPLEMENTATION-MACRO-001`
delivered the runtime for all three rows, and `VERCE3D-S6-POST-SOL-CORRECTION-MACRO-001` corrected
five blocking findings an independent review raised against that delivery — most relevantly here,
B-01 (true clone-candidate revision construction, replacing a full re-resolve) and B-02 (the
`quote_item_cost_snapshot`/`quote_item_material_snapshot`/`quote_item_additional_cost_snapshot`
tables freeze CostEngine's full breakdown, not a collapsed scalar). `ProductionOrder` (H-001),
`PerOrderFeeAllocator` (H-004 remainder) and `QuoteOutcomeCalculator` (H-009 A/Option B) are all
implemented, unit- and integration-tested against a real PostgreSQL instance, and proven end to
end by a permanent `S5ToS6BootstrapUpgradeTests` upgrade certification. Remaining S9 scope (the
operational shop floor: items, planned/actual material, printer/scheduling UX) is untouched and
still owed by S9.

Deliberately **not** decided here, and still owed by their own sprints: **H-002** (sale
lifecycle, including whether more than one `Sale` may reference one approved revision),
**H-003** (mixed-channel fees), **H-005** (override × discount × bracket precedence) — all S8 —
and **H-006** (actual total cost composition, including how a canceled order's write-off is
classified), due before S10.

### Flag for orchestrator review: H-001 and H-009 A during S5

Per rule 1(b), a `Before S6` decision deadline is nominally an S5 exit requirement. Both H-001
(Quoting/Production revision lifecycle) and H-009 A (Quoting commercial conversion semantics) are
entirely about aggregates S5 does not touch (`Quote`, `QuoteRevision`, `ProductionOrder`) and
that this sprint's mission brief explicitly excluded. S5 has no mandate to design Quoting or
Production state-machine semantics from inside the Catalog/Pricing modules, and doing so here
would be exactly the kind of premature, unreviewed architectural decision CLAUDE.md §5 warns
against. Both rows are left **Open, unchanged**, and are called out again in the S5 report's
Open Decisions section for the orchestrator to route to whichever sprint is positioned to decide
them — most plausibly S6 itself, immediately before it needs the answer.

> **Outcome (2026-09-20).** The orchestrator routed both to a dedicated decision-only S6-entry
> mission, exactly as this note proposed. Both are now resolved — see the resolution note above
> and [ADR-0020](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md). This
> paragraph is kept for traceability of how they were routed.

**Why some decisions precede their implementation.** H-001 and H-009 A are due *before S6*
because S6 builds the quote engine: if revision-vs-production behaviour or the commercial meaning
of a conversion outcome is settled afterwards, the quote state machine has to be rebuilt rather
than extended. H-006 is due before S10 for the same reason — the energy module writes the actual
side. H-008 B is due before S12 so the dashboard is not built on two silently different profit
definitions.

### Cross-cutting note for H-010

Repository reality is that the current CostEngine has no energy component, the Energy module is
a stub and no seeded default tariff exists. Until S10, energy input and cost are explicitly
**unavailable/absent**, never implicitly `0`: pricing, cost breakdowns and margin explanations
must not suggest `R$ 0` energy merely because the capability is missing. S10 owns the Energy
implementation and must preserve the meaning of earlier immutable snapshots. H-010 remains open.

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
| Global UX/UI modernization pass | [ADR-0023 §13](architecture/ADR-0023-s8b-commerce-foundation.md#13-frontend-and-navigation) | S8B may build usable Commerce pages under current conventions; cohesive design system, collapsible sidebar, navigation groups, VERCE palette/type hierarchy, dashboard Home, breadcrumbs, state/empty/loading patterns, responsive tables and accessibility remain a cross-cutting S15 delivery |

---

## 3. Inputs still missing

| Item | Needed by | Impact if still missing |
|---|---|---|
| `VERCE_Proposta-Modelo_1.pdf` | **S7** | The default proposal's structure is fully specified; only the visual tokens (palette, typography, spacing, logo lockup) are pending — see [DEFAULT-PROPOSAL-TEMPLATE §0 and §8](DEFAULT-PROPOSAL-TEMPLATE.md) |
| Smart-plug device selection | **S10** | `IEnergyProvider` exists; no vendor code may be written before the device is chosen |
| Official provider capability and fee discovery (Mercado Livre, Shopee, TikTok Shop) | **S8C.0** | Validate auth, listings, orders, fees, analytics, ads, shipping, inventory sync, limits, sandbox and regional support from official sources; S8B represents UNKNOWN/unsupported capability and creates no provider cache/endpoint assumption |
| The external gate report as a file | informational | Deadlines in §1 are now **authoritative**, taken from the re-gate mission text; a report file would only add rationale |
