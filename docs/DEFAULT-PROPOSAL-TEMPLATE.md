# DEFAULT PROPOSAL TEMPLATE — `VERCE | Proposta Comercial Padrão`

Specification of the template the system bootstraps with. It is **seed data**, not renderer
code: it can be edited, duplicated, renamed, versioned, replaced as default, and extended in the
Document Studio (S14) like any other template.

Reference: `VERCE_Proposta-Modelo_1.pdf`.
Implemented in **S7**. Storage format and pipeline per
[ADR-0007](architecture/ADR-0007-document-template-engine.md).

---

## 0. Status of the visual reference

> **The reference PDF was not available to the S0 architecture agent.** It was named in the
> addendum but did not reach the workspace.
>
> Consequently this document specifies the **structure, blocks, bindings and conditional
> behaviour** — which the addendum describes in full — and **not** the exact visual values
> (palette, typography, spacing scale, logo lockup, rule weights).
>
> **S7 must begin with a visual extraction task** (§8 below) against the real PDF, filling the
> brand token table. Nothing else in this document depends on that extraction: the block tree,
> the bindings and the pagination rules are complete as written, and the tokens are referenced
> symbolically throughout.
>
> No colour, font or measurement in this document is a design decision by the architecture
> agent. Every one is a placeholder marked `‹extract›`.

---

## 1. Identity

| Property | Value |
|---|---|
| Template name | `VERCE \| Proposta Comercial Padrão` |
| Document type | `QUOTE` |
| `is_default` | `true` |
| Initial version | `1`, status `PUBLISHED` |
| Page setup | A4 portrait, margins `‹extract›` (fallback 18 mm) |
| Logo source | `INHERIT_DEFAULT` → `branding_assignment[DOCUMENT_DEFAULT_LOGO]` |

Seeded in the S7 migration as a normal `document_template` +
`document_template_version` row pair. **No part of it is compiled into the renderer.**

---

## 2. Block tree

Blocks in document order. `visibleWhen` uses the closed predicate grammar of
[ADR-0007 §4](architecture/ADR-0007-document-template-engine.md); every `binding` path must
exist in the `QUOTE` binding catalogue or the template fails to publish.

### 2.1 Page header — `repeatOn: ALL`

| Block | Type | Binding / content |
|---|---|---|
| Brand logo | `Logo` | `logoSource: INHERIT_DEFAULT`, height `‹extract›` |
| Document title | `Text` | literal `PROPOSTA COMERCIAL`, style `title` |
| Proposal number | `DynamicField` | `quote.number` |
| Proposal date | `DynamicField` | `quote.date`, format `DATE_SHORT` |

### 2.2 Project

| Block | Type | Binding | Conditional |
|---|---|---|---|
| Section | `Header` | literal `PROJETO` | `visibleWhen: quote.title IS_NOT_EMPTY` |
| Project title | `DynamicField` | `quote.title` | same |

### 2.3 Customer

| Block | Type | Binding | Conditional |
|---|---|---|---|
| Section | `Header` | literal `CLIENTE` | — |
| Name | `DynamicField` | `customer.name` | — |
| Document | `DynamicField` | `customer.document` | `IS_NOT_EMPTY` |
| Contact | `DynamicField` | `customer.contact` | `IS_NOT_EMPTY` |
| E-mail | `DynamicField` | `customer.email` | `IS_NOT_EMPTY` |
| Phone | `DynamicField` | `customer.phone` | `IS_NOT_EMPTY` |

Every customer field except the name is conditional: the domain makes documents, e-mail and
phone optional ([DOMAIN-MODEL §2](DOMAIN-MODEL.md#2-customers-module)), and a proposal must not
print an empty labelled row.

### 2.4 Scope

| Block | Type | Binding | Conditional |
|---|---|---|---|
| Section | `Header` | literal `ESCOPO` | `visibleWhen: quote.scope IS_NOT_EMPTY` |
| Scope body | `RichText` | `quote.scope` | same |

### 2.5 Technical information

| Block | Type | Binding | Conditional |
|---|---|---|---|
| Section | `Header` | literal `INFORMAÇÕES TÉCNICAS` | `visibleWhen: quote.technicalHighlights IS_NOT_EMPTY` |
| Highlight grid | `TechnicalHighlight` | collection `quote.technicalHighlights` (`label` / `value` pairs), 2 columns | same |
| Technical notes | `RichText` | `quote.technicalNotes` | `IS_NOT_EMPTY` |

This is the block that answers addendum §11 without polluting the domain: `MATERIAL / PETG` and
`TOLERÂNCIA / ±0,2 mm` are **two rows of a generic label/value collection**, not two columns on
`quote_revision`. The same block renders `Cor / Acabamento`, `Dimensões / Peso` or
`Material / Altura de camada` with no schema change.

### 2.6 Investment

| Block | Type | Binding | Notes |
|---|---|---|---|
| Section | `Header` | literal `INVESTIMENTO` | — |
| Items table | `ItemsTable` | collection `items` | `repeatHeaderOnEachPage: true` |
| Totals | `Totals` | `subtotal`, `discount`, `total` | `discount` row `visibleWhen: discount GT 0` |

Item table columns:

| Column | Binding | Align | Format |
|---|---|---|---|
| Item | `item.name` | left | — |
| Descrição / inclusos | `item.description` | left | wraps; `visibleWhen` column non-empty across the set |
| Qtd. | `item.quantity` | right | `QUANTITY` |
| Valor unit. | `item.unitPrice` | right | `MONEY` |
| Total | `item.lineTotal` | right | `MONEY` |

"Included items" from the reference maps to `item.description` — an existing field. No new
column was added to the domain for a presentation concern.

### 2.7 Terms

| Block | Type | Binding | Conditional |
|---|---|---|---|
| Section | `Header` | literal `CONDIÇÕES` | any child visible |
| Prazo de entrega | `DynamicField` | `deliveryTerms` | `IS_NOT_EMPTY` |
| Condições de pagamento | `DynamicField` | `paymentTerms` | `IS_NOT_EMPTY` |
| Validade | `DynamicField` | `quote.validUntil`, format `DATE_SHORT` | — |
| Garantia | `DynamicField` | `warranty` | `IS_NOT_EMPTY` |

### 2.8 Out of scope / notes

| Block | Type | Binding | Conditional |
|---|---|---|---|
| Section | `Header` | literal `NÃO INCLUSO` | `visibleWhen: outOfScope IS_NOT_EMPTY` |
| Body | `RichText` | `outOfScope` | same |
| Observations | `RichText` | `quote.notes` | `IS_NOT_EMPTY` |

`quote.notes` is the customer-facing note. **`internal_notes` has no binding at all** — it is
absent from the `QUOTE` binding catalogue, so no template can print it, by construction rather
than by discipline.

### 2.9 Page footer — `repeatOn: ALL`

| Block | Type | Binding |
|---|---|---|
| Compact logo | `Logo` | `logoSource: SPECIFIC_ASSET` → `COMPACT_LOGO`, fallback `INHERIT_DEFAULT` |
| Website | `DynamicField` | `company.website` |
| E-mail | `DynamicField` | `company.email` |
| Instagram | `DynamicField` | `company.instagram` |
| Phone | `DynamicField` | `company.phone` |
| Page number | `PageNumber` | `page.number` / `page.total` |

Footer contact fields are each `visibleWhen … IS_NOT_EMPTY`, so a company profile without an
Instagram handle does not print a stray separator.

---

## 3. Pagination behaviour

The reference happens to be one page. **The template is not a one-page template.**

| Rule | Implementation |
|---|---|
| Page size | `page_setup.size = A4`, overridable per template |
| Header repeat | `repeatOn: ALL` on the header region |
| Footer repeat | `repeatOn: ALL` on the footer region |
| Table header repeat | `<thead>` — Chromium repeats it natively across page breaks |
| Row splitting | `break-inside: avoid` on each `<tr>` |
| Section splitting | `break-inside: avoid` on Technical Highlight pairs and the Totals block |
| Orphan totals | Totals block carries `break-before: avoid` so it never lands alone on a page |
| Page numbers | Playwright `footerTemplate` with `pageNumber` / `totalPages` |
| Safe margins | `@page { margin: … }` from `page_setup` |

Acceptance for S7: the template must render correctly with **1, 5 and 25 items**, the last
producing multiple pages with a repeated table header and correct `x / y` numbering.

---

## 4. Bindings used

All from the `QUOTE` binding catalogue
([ADR-0007 §3](architecture/ADR-0007-document-template-engine.md)):

```
company.name  company.legalName  company.document  company.email  company.phone
company.website  company.instagram  company.whatsapp  company.address  company.logo

quote.number  quote.date  quote.validUntil  quote.title  quote.status
quote.scope  quote.notes  quote.technicalHighlights[]  quote.technicalNotes

customer.name  customer.document  customer.contact  customer.email  customer.phone

items[]  →  item.name  item.description  item.quantity  item.unitPrice
            item.discount  item.lineTotal
subtotal  discount  total

paymentTerms  deliveryTerms  warranty  outOfScope

page.number  page.total
```

Deliberately **absent** from the catalogue, so no template can ever print them:
`quote.internalNotes`, every cost and margin field (`unitCost`, `expectedProfit`,
`effectiveMargin`, `commissionPercent`), and all AI/settings secrets. A customer-facing document
that could print the shop's margin is a business risk, and the binding catalogue is where that
risk is closed.

---

## 5. Content sources

The proposal content fields are optional and live on `quote_revision`
([DOMAIN-MODEL §8](DOMAIN-MODEL.md#8-quoting-module)):

| Field | Default source |
|---|---|
| `title` | typed per quote |
| `scope` | typed per quote |
| `technical_highlights` (jsonb array of `{label, value}`) | typed per quote; may be pre-filled from the product |
| `technical_notes`, `out_of_scope` | typed per quote |
| `payment_terms`, `delivery_terms`, `warranty` | default from `app_setting`, copied onto the revision at issue |

Defaults are **copied onto the revision**, never read live — changing the default payment terms
must not alter a proposal already sent ([ADR-0003](architecture/ADR-0003-cost-and-price-snapshots.md)).

None of these are mandatory. A quote with no scope, no highlights and no warranty renders a
shorter but valid proposal, because every one of those blocks is conditional.

---

## 6. Brand tokens

Visual values live in `document_template_version.definition.theme` — one place, editable in the
Studio, versioned with the template:

| Token | Value | Source |
|---|---|---|
| `--doc-color-primary` | `‹extract›` | PDF |
| `--doc-color-text` | `‹extract›` | PDF |
| `--doc-color-muted` | `‹extract›` | PDF |
| `--doc-color-rule` | `‹extract›` | PDF |
| `--doc-font-heading` | `‹extract›` + a generic (never host-specific) fallback | PDF |
| `--doc-font-body` | `‹extract›` + a generic (never host-specific) fallback | PDF |
| `--doc-size-title/section/body/small` | `‹extract›` | PDF |
| `--doc-space-*` | `‹extract›` | PDF |

Document tokens are **separate from application theme tokens**
([ADR-0014](architecture/ADR-0014-frontend-architecture-and-theming.md)). Switching the app to a
dark theme must not change a PDF.

Fonts must be **embedded or self-hosted**, never fetched from a CDN at render time: the renderer
runs with network access blocked ([SECURITY §8](SECURITY.md#8-document-rendering-playwrightchromium)),
and a document whose typography depends on internet reachability is not reproducible.

---

## 7. Immutability

Once a proposal is issued, its `generated_document` row is permanent and immutable
([ADR-0016](architecture/ADR-0016-document-render-snapshots.md)). Editing this template, changing
the logo, or updating the company phone number changes **future** documents only. A later quote
revision produces a **new** document; it never regenerates an old one.

---

## 8. S7 entry task — visual extraction

Before implementing, and against the real `VERCE_Proposta-Modelo_1.pdf`:

1. Extract the palette (primary, text, muted, rules, any accent) into the token table in §6.
2. Identify the typefaces and obtain licensed, self-hostable files; define fallback stacks.
3. Measure the type scale, spacing rhythm, margins and rule weights.
4. Extract the logo lockups and register them as `BrandAsset` seed rows
   (`PRIMARY_LOGO`, `COMPACT_LOGO`, `SYMBOL`) per
   [ADR-0015](architecture/ADR-0015-brand-assets-and-application-branding.md).
5. Confirm the section order and labels against §2 and correct this document if the PDF differs.
6. Render side by side with the reference and iterate until the difference is cosmetic.

Step 5 matters: §2 was written from the addendum's textual description, not from the artwork.
**If the PDF and this document disagree, the PDF wins**, and this file must be updated in the
same delivery.
