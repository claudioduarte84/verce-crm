# ADR-0016 — Document Render Snapshots and Immutability

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-07
- **Sprint:** S0 (addendum)

## Context

Addendum §12 states the rule as **critical**: changing the company logo, phone, address,
template, colours or layout must **not** retroactively change a document already generated and
sent. A commercial proposal a customer received is evidence of what was offered.

Addendum §13 adds the distinction that makes this tractable: a **template** evolves; a
**generated document** is historical evidence. Once a proposal revision is formally issued, its
document is immutable, and a later quote revision produces a **new** document — never a silent
regeneration of the old one with a new template.

[ADR-0003](ADR-0003-cost-and-price-snapshots.md) already freezes the *numbers*. This ADR freezes
everything else the document depends on: layout, branding, company data, and the resolved text.

## Decision

### 1. Four things are frozen at issue, not three

| Layer | Frozen as | Without it |
|---|---|---|
| Money and cost | `quote_item_cost_snapshot` (ADR-0003) | prices drift |
| Layout | `document_template_version_id` | a template edit rewrites history |
| Branding | `brand_asset_version_id[]` (ADR-0015) | a logo replacement rewrites history |
| Resolved content | `render_data_snapshot` (jsonb) | a company phone change rewrites history |

`render_data_snapshot` is the **fully resolved `RenderContext`** — company data, customer data,
quote fields, items, totals, terms, technical highlights, and the resolved brand asset version
ids — exactly as handed to the renderer. It is the answer to "what did this document say?"
without joining to a single live table.

### 2. Rendered HTML is stored too

`GeneratedDocument` stores the produced **HTML** alongside the PDF, both content-addressed.

The PDF alone would be sufficient evidence, and storing HTML is a deliberate extra: it makes a
document diffable, greppable and re-renderable if a Chromium upgrade ever changes output, and it
lets a support question ("why did this field print blank?") be answered without re-running a
pipeline whose inputs have moved on. The cost is tens of kilobytes per document.

### 3. Content-addressed storage

Files are stored at `{sha256[0:2]}/{sha256}.{ext}` through `IDocumentStorage`.
Identical renders deduplicate naturally, integrity is verifiable, and no filename is ever
derived from user input.

**Files are never deleted in v1.** Reference counting was considered and rejected as premature:
at this volume the storage is trivial, and a deletion bug that removes a proposal a customer
received is unrecoverable. The addendum's own instruction applies — historical integrity over
storage optimization.

### 4. `purpose` separates previews from evidence

`generated_document.purpose ∈ { PREVIEW, ISSUED }`.

- **`PREVIEW`** — produced by "visualizar", disposable, prunable after 30 days. Not evidence.
- **`ISSUED`** — produced by issuing or sending a proposal. **Permanent and immutable**: no
  endpoint updates or deletes it, and its row carries `issued_at` and optional `sent_at`.

Without this split, either every preview click becomes permanent storage, or previews and
evidence share a lifecycle and previews get pruned along with real documents.

### 5. Re-rendering is an explicit new document, never a mutation

A revision may be rendered many times. Each render **inserts a new row**. The most recent per
`(quote_revision_id, document_type)` is flagged `is_current` for display, and every earlier
`ISSUED` document remains retrievable.

Explicitly forbidden: updating an existing `ISSUED` row's file, template version or snapshot.
The addendum's "never silently regenerate an old proposal with a new template version" is
enforced structurally — there is no code path that overwrites a generated document.

When an operator re-renders an already-issued revision (say, after a template fix), the UI must
state that the customer holds a different version, and the audit log records who re-rendered and
why.

### 6. Reproducibility requires self-contained rendering

A frozen snapshot is worthless if rendering it needs the internet. Therefore:

- **fonts are embedded or self-hosted**, never fetched from a CDN;
- **images are resolved from local storage** by version id;
- the render context has **network access blocked**
  ([SECURITY §8](../SECURITY.md#8-document-rendering-playwrightchromium));
- `chromium_version` and `render_engine_version` are recorded on each row, so a future output
  difference is attributable rather than mysterious.

### 7. Quote content defaults are copied, not referenced

`payment_terms`, `delivery_terms` and `warranty` default from settings but are **copied onto the
quote revision** at issue. Changing the default afterwards affects only new quotes. This keeps
the rule consistent with prices: the revision is the record, settings are only its source.

## Alternatives considered

- **Store only the PDF** — sufficient as evidence, rejected for the diagnostic and
  re-render reasons in §2. The marginal cost is small.
- **Store only IDs and re-render on demand** — the tempting normalized answer, and wrong: it
  makes a historical document a *function of current state*, which is exactly what the addendum
  forbids. It also breaks the moment a template version, an asset or a font is unavailable.
- **Version the template but not the branding** — the gap the addendum specifically calls out:
  the layout would be right and the logo wrong.
- **Immutability by convention** (`don't update generated documents`) — rejected; the guarantee
  is enforced by the absence of a write path plus append-only semantics, not by discipline.
- **Reference counting for file cleanup** — rejected as premature (§3).
- **Regenerating on template change to keep documents "up to date"** — rejected outright. It
  sounds helpful and destroys the record.

## Consequences

**Positive:** a proposal renders identically in five years; branding, layout, content and money
are all frozen by the same principle; storage deduplicates; previews do not pollute the archive;
audit answers who re-rendered what.

**Negative:**
- Storage grows monotonically (bounded by preview pruning and by the small size of the data).
- `render_data_snapshot` duplicates data already in `quote_revision` and `company_profile`. This
  is the same deliberate redundancy as ADR-0003 layer 2, and for the same reason.
- Correcting a genuine layout error in an already-sent proposal means issuing a new document and
  telling the customer — which is the honest behaviour, and the UI must make it feel like a
  normal action rather than a failure.
- Fonts must be licensed for embedding; S7's extraction task must confirm this before choosing
  typefaces.

## Compliance checks

- Test: generate a proposal; change the company phone, logo (new version) and template
  (new version); re-open the original document — HTML, PDF and snapshot are unchanged.
- Test: no endpoint can update or delete a `generated_document` row with `purpose = ISSUED`.
- Test: re-rendering a revision inserts a new row and leaves the previous one intact.
- Test: rendering with network access enabled fails the security test asserting the render
  context is offline.
- Test: two identical renders produce one stored file (same sha256) and two rows.
- Test: pruning previews never touches an `ISSUED` document.
