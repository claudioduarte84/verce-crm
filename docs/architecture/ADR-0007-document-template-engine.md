# ADR-0007 — Document Template Engine

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-06
- **Sprint:** S0

## Context

The system must produce three kinds of printable documents, and more later:

- **Quote** — the customer-facing proposal;
- **Production order** — the shop-floor sheet (items, quantities, filaments, colors, supplies,
  notes, due date, separation list);
- **Shipping label** — in `SIMPLE` (logo, recipient, address) and `FULL` (company data plus
  configurable fields) variants, at configurable page sizes.

The operator must be able to change layouts **without a deploy**, and the brief is explicit that
raw HTML is not an acceptable authoring format for a non-technical user. Documents already
issued must remain reproducible.

## Decision

### The pipeline

```
Block editor (React, S14)
        │  produces
        ▼
DocumentTemplateVersion.definition  (jsonb, schema-versioned)
        │  +  RenderContext (typed, built per DocumentType)
        ▼
Block renderers  →  HTML + CSS   (server-side, values HTML-encoded)
        ▼
Playwright / Chromium  →  PDF bytes
        ▼
GeneratedDocument (stored file + sha256 + template_version_id)
```

### 1. JSON is the authoring format, HTML is an artifact

`DocumentTemplateVersion.definition` is a `jsonb` document describing a tree of blocks
(text, field binding, table, image, spacer, divider, page break, conditional section, totals),
plus `page_setup` (size, orientation, margins — the same field serves an A4 quote and a
100×150 mm label). `schema_version` allows the block schema to evolve with a documented
migration.

HTML/CSS is generated from that JSON by server-side renderers. Users never edit HTML; the
renderer never receives HTML from a user. There is **no raw-HTML block in v1** — adding one
later would be an `Owner`-only, sanitized feature, because a raw-HTML block is a script
injection vector into a rendering engine (SECURITY §8).

### 2. Templates are versioned and published

```
DRAFT ──publish──► PUBLISHED ──archive──► ARCHIVED
```

- Only `PUBLISHED` versions render production documents.
- A `PUBLISHED` version is **immutable**; editing creates a new `DRAFT` at `version_number + 1`.
- At most one open `DRAFT` per template (partial unique index).
- `GeneratedDocument` stores `document_template_version_id`, so any past PDF states exactly
  which layout produced it, and archiving a version never invalidates documents made from it.

Reproducibility is the point: "re-print quote 260906-4B" must produce the document the customer
received, not today's layout applied to old data.

### 3. Render context and the binding catalogue

Each document type has an `IRenderContextResolver` living in the **owning** module (`Quoting`
resolves the quote context, `Production` the order context, `Settings` contributes company and
branding data). The Documents module knows nothing about quotes; it knows how to turn a block
tree plus a context into HTML.

Adding a document type is: register a `DocumentType` row, implement a resolver, publish a
binding catalogue, seed a template. No change to the pipeline.

**Bindings are a controlled catalogue, not string replacement.** Each document type declares its
available paths:

```csharp
public sealed record BindingDescriptor(
    string  Path,            // "quote.number", "items[].unitPrice"
    BindingKind Kind,        // SCALAR | COLLECTION | IMAGE | MONEY | DATE | PERCENT | RICH_TEXT
    string  Label,           // shown in the Studio picker (pt-BR)
    string? FormatHint);
```

- A block stores `binding.path`; the renderer **walks the block tree and resolves each path
  against a typed `RenderContext`**. It never runs a regular expression over generated HTML.
- `{{quote.number}}` is **display syntax in the editor only**. It is never the storage format
  and never the resolution mechanism.
- **Publishing validates every path** against the catalogue. An unknown or misspelled path fails
  with `DOCUMENT_TEMPLATE_INVALID_BINDING` at publish time, not as a blank space on a document a
  customer already received.
- **Formatting is server-side and driven by `BindingKind`** — money as pt-BR currency, dates as
  `dd/MM/yyyy`, percent from its stored fraction. Templates never supply format code.
- **Every resolved value is HTML-encoded** on output. `RICH_TEXT` passes through a fixed
  allow-list sanitizer (bold, italic, lists, paragraphs, line breaks) — never raw HTML.

The catalogue is also a **security boundary**: fields absent from it cannot be printed by any
template. `quote.internalNotes` and every cost/margin field are deliberately excluded from the
`QUOTE` catalogue, so no template — including one an operator builds in the Studio — can leak
the shop's margin onto a customer-facing proposal. This is enforcement by construction rather
than by reviewer vigilance.

### 3.1 Conditional content

Blocks carry an optional `visibleWhen` using a **closed predicate grammar**:

```jsonc
{ "path": "quote.tolerance", "operator": "IS_NOT_EMPTY" }
{ "all": [ { "path": "discount", "operator": "GT", "value": 0 },
           { "path": "quote.status", "operator": "EQUALS", "value": "APPROVED" } ] }
```

Operators: `IS_NULL`, `IS_NOT_NULL`, `IS_EMPTY`, `IS_NOT_EMPTY`, `EQUALS`, `NOT_EQUALS`, `GT`,
`GTE`, `LT`, `LTE`. Grouping: `all` / `any`, nesting depth ≤ 3.

**No expression language, no arbitrary executable code inside templates** — a template is data
that a reviewer can read, not a program. This is the same reasoning that rejected a formula
language for fee rules in [ADR-0005](ADR-0005-marketplace-fee-rules.md).

### 3.2 Repeatable content

An `ItemsTable` block binds to a `COLLECTION` path and declares its columns (binding, label,
alignment, format), plus `repeatHeaderOnEachPage` and an `emptyBehavior`
(`HIDE_BLOCK` | `SHOW_EMPTY_MESSAGE`). Nesting is limited to one level in v1.

The renderer must be correct at **1, 5 and 25+ items**; a single-page assumption anywhere in the
pipeline is a defect.

### 3.3 Pagination

`page_setup` carries size (A4 default, or custom millimetres for labels), orientation, margins,
and header/footer regions with `repeatOn ∈ { ALL, ALL_EXCEPT_FIRST, FIRST_ONLY }`.

| Requirement | Mechanism |
|---|---|
| Automatic page breaks | Chromium layout |
| Repeating table headers | `<thead>`, repeated natively |
| Rows not split across pages | `break-inside: avoid` per `<tr>` |
| Totals never orphaned | `break-before: avoid` on the totals block |
| Safe margins | `@page { margin }` from `page_setup` |
| Page numbering | Playwright `headerTemplate`/`footerTemplate` with `pageNumber`/`totalPages` |

The reference proposal happening to fit one page is a property of that content, never of the
architecture.

### 3.4 Logo blocks

A `Logo` block stores intent — `INHERIT_DEFAULT`, `SPECIFIC_ASSET` (with a `brand_asset_id`), or
`NONE` — and is resolved to a concrete `brand_asset_version_id` at render time, then frozen into
the snapshot. See [ADR-0015](ADR-0015-brand-assets-and-application-branding.md) and
[ADR-0016](ADR-0016-document-render-snapshots.md).

### 3.5 Block families

`Logo` · `Text` · `Header` · `DynamicField` · `RichText` · `TechnicalHighlight` · `ItemsTable` ·
`Totals` · `Terms` · `Notes` · `Image` · `QrCode` · `Separator` · `PageNumber` · `Spacer` ·
`ConditionalSection`.

`TechnicalHighlight` renders a generic collection of `{label, value}` pairs — `Material / PETG`,
`Tolerância / ±0,2 mm`, `Cor / Acabamento`. It is a **presentation** concept: no domain entity is
created for material, tolerance or finish, and no such field is mandatory on a quote.

### 4. Rendering runs off the outbox

PDF generation is slow (hundreds of ms to seconds) and depends on an external process. It must
never be able to fail a business transaction. Approving a quote commits, then an outbox message
triggers the render ([ADR-0012](ADR-0012-domain-events-and-outbox.md)). A user-initiated
"download PDF" renders synchronously with a timeout, since the user is waiting.

### 5. Storage

Files are written through `IDocumentStorage` (local filesystem in v1, path abstracted for a
future object store), outside the web root, and served by an authorized endpoint that checks
permission on the **source entity**. A sha256 is stored so file corruption or substitution is
detectable.

### 6. Before S14

S7 and S9 seed template definitions as JSON written by hand. The storage format is identical to
what the editor will produce, so S14 adds an authoring surface without migrating anything.
This is deliberate sequencing: the pipeline is proven with real documents long before the
editor exists.

### 7. The default VERCE proposal is seed data, not renderer code

The system bootstraps with `VERCE | Proposta Comercial Padrão` — a normal
`document_template` + `document_template_version` row pair, `is_default = true` for type
`QUOTE`. Its full block tree, bindings and conditionals are specified in
[DEFAULT-PROPOSAL-TEMPLATE](../DEFAULT-PROPOSAL-TEMPLATE.md).

Because it is ordinary template data it can be edited, duplicated, renamed, versioned, replaced
as default and extended in the Studio. **No part of it may be compiled into the renderer** — a
hard-coded default template would be a second, privileged code path that the Studio could never
reach, and every later change to it would be a deploy.

Its visual values (palette, typography, spacing) live in `definition.theme` as document tokens,
kept separate from the application theme tokens of
[ADR-0014](ADR-0014-frontend-architecture-and-theming.md): switching the app to a dark theme
must not change a PDF.

## Alternatives considered

- **Razor/Handlebars templates edited as text** — simplest to build; rejected because a
  non-technical operator cannot safely edit them, a template becomes code (deploy to change),
  and user-editable templates in a text language are a code-injection surface.
- **A reporting engine (QuestPDF, iText, Crystal-style designer)** — QuestPDF in particular is
  excellent and produces smaller, faster PDFs than Chromium. Rejected as the primary path
  because layout authoring becomes C#, which defeats the low-code requirement, and because the
  team's CSS knowledge transfers directly to the HTML pipeline. Worth revisiting only if
  Chromium's operational weight becomes a real problem.
- **Client-side PDF generation (jsPDF, print-to-PDF in the browser)** — rejected: output depends
  on the viewer's browser and fonts, so two users get different documents; server-side rendering
  is reproducible and auditable.
- **Storing generated HTML instead of PDF** — rejected: HTML rendered years later by a different
  browser is not the same document. The PDF is the archived artifact.
- **A general expression language inside templates** — rejected for the same reason as in
  [ADR-0005](ADR-0005-marketplace-fee-rules.md): it converts data into code. Conditional
  sections are limited to simple, declarative predicates over context fields.

## Consequences

**Positive:** layout changes without deploys; one engine serves quotes, shop-floor sheets and
labels; every issued document is reproducible; CSS skills apply directly.

**Negative:** Chromium is a heavy dependency — hundreds of megabytes in the container, memory
per render, and a version to keep patched. It must be pinned, health-checked
(`/health/ready`) and sandboxed. Rendering is slower than a native PDF library; acceptable
against the < 3 s target. The block schema is a small internal format that must be documented
and versioned — a real, ongoing maintenance obligation, accepted as the cost of low-code
authoring.

## Related decisions

Brand assets and logo resolution: [ADR-0015](ADR-0015-brand-assets-and-application-branding.md).
Render snapshots, document immutability and re-rendering rules:
[ADR-0016](ADR-0016-document-render-snapshots.md).
The default template's block tree: [DEFAULT-PROPOSAL-TEMPLATE](../DEFAULT-PROPOSAL-TEMPLATE.md).

## Compliance checks

- Integration test: rendering with a `DRAFT` template fails with `DOCUMENT_TEMPLATE_NOT_PUBLISHED`.
- Integration test: re-rendering an old revision with an archived template version reproduces
  the same monetary values as the stored snapshot.
- Test: publishing a template with an unknown binding path fails with
  `DOCUMENT_TEMPLATE_INVALID_BINDING`.
- Test: no binding path in the `QUOTE` catalogue resolves to `internal_notes`, a unit cost, or a
  margin field.
- Test: the default template renders correctly with 1, 5 and 25 items, repeating the table
  header and numbering pages `x / y`.
- Test: a block whose `visibleWhen` is unsatisfied emits nothing, including its section heading.
- Security test: a template field containing `<script>` renders escaped, not executed.
- Security test: `RICH_TEXT` content containing `<img onerror=…>` is stripped by the sanitizer.
- Security test: the renderer refuses a context containing an external URL.
