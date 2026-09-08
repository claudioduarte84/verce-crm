# ADR-0014 — Frontend Architecture and Theming

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-06
- **Sprint:** S0

## Context

The frontend is React + TypeScript + Vite. It must be desktop-first, responsive, accessible,
fast, and support **ten visual themes**. It must also never become a second, divergent
implementation of the domain — the most likely failure mode for a system whose entire value is
that every surface shows the same number.

## Decision

### 1. Same-origin, cookie session

The SPA is served by the API host in production and proxied by Vite in development. No CORS in
production, no token in JavaScript-accessible storage
([ADR-0009](ADR-0009-authentication-strategy.md)).

### 2. Structure mirrors the backend modules

```
frontend/src/
├── app/          router, providers, error boundaries
├── features/<module>/   api/ · components/ · hooks/ · routes/
├── ui/           design system primitives (Button, Field, Table, Money, …)
├── lib/          formatting, validation, http client
└── themes/       token sets
```

A developer who knows where something lives on the backend knows where it lives on the frontend.

### 3. The frontend never computes money

**The rule that matters most.** Prices, costs, margins, totals and variances are computed by the
backend engines and displayed as received.

- Money crosses the wire as a **string** (`"128.40"`), never a JSON number, so no JavaScript
  `number` — with its binary floating point — ever holds a currency value.
- A `<Money>` component formats via `Intl.NumberFormat('pt-BR')`. It is the only place currency
  is rendered.
- Percentages arrive as fractions and are multiplied by 100 for display in one helper,
  divided by 100 on input in one helper.
- **Live preview** in the Laboratory and the quote builder calls the backend
  (`POST /api/costing/preview`, debounced), rather than reimplementing the formula in TypeScript
  for responsiveness. A local reimplementation would drift from the engine within one sprint,
  and the divergence would appear as "the screen said R$ 40,52 and the PDF says R$ 40,51" —
  precisely the defect the product exists to eliminate.

### 4. Types are generated, never hand-written

API types are generated from the backend OpenAPI document into `lib/api/schema.ts`. Hand-written
duplicates of backend DTOs are forbidden: they are the standard source of silent drift when a
field changes meaning (`commission: 0.18` versus `18`).

### 5. State

- **Server state:** TanStack Query. Caching, invalidation and optimistic updates live there.
- **Form state:** React Hook Form + Zod, with schemas derived from the generated types.
- **Global state:** only session, theme and organization settings, via context. No Redux, no
  global store. Almost everything in this product is server state wearing a disguise.

### 6. Theming by design tokens

A theme is a set of CSS custom properties, not a set of components:

```css
:root {
  --color-bg, --color-surface, --color-border,
  --color-text, --color-text-muted,
  --color-primary, --color-primary-contrast,
  --color-success, --color-warning, --color-danger,
  --radius-sm/md/lg, --space-*, --font-sans, --shadow-*
}
```

- `ThemeDefinition` = `{ id, name, mode: 'light'|'dark', tokens }`, registered in
  `themes/registry.ts`.
- Applied via `data-theme="<id>"` on the root element; the user's choice persists in a profile
  setting (server-side, so it follows them across devices) with `localStorage` as a
  first-paint fallback to avoid a flash.
- Components consume **only tokens**. A hard-coded hex value in a component is a defect — a
  lint rule enforces it.
- **A theme never changes behaviour.** No conditional logic on the active theme, no
  theme-dependent formatting, no theme-dependent field visibility. A theme changes color,
  radius and density; nothing else.
- Every theme must pass a contrast check (≥ 4.5:1 for text, ≥ 3:1 for UI boundaries) as an
  automated test over the token set, not as a manual review.

Only one theme ships before S15. The token layer exists from S1 so that adding nine more is a
data exercise rather than a refactor.

### 7. Accessibility baseline

WCAG 2.1 AA on the main flows: keyboard reachable, visible focus, labeled inputs, correct
heading order, ARIA only where semantics are genuinely missing, `prefers-reduced-motion`
respected. Checked by automated axe runs in the E2E suite plus a manual pass in S15.

### 8. Performance

Route-level code splitting; virtualization for long lists (quotes, movements); debounced
preview calls; `staleTime` tuned per query so master data is not refetched constantly. Target:
home dashboard interactive in under 1 s on a normal connection.

## Alternatives considered

- **Server-rendered Razor/Blazor** — fewer moving parts, and rejected: the brief fixes React,
  and the Laboratory's interactivity (rows added and removed with live recalculation) is a
  natural SPA workload.
- **Computing prices client-side for instant feedback** — tempting for UX, rejected in §3. A
  debounced backend call is fast enough and cannot drift.
- **Money as a JSON number** — rejected: `0.1 + 0.2` in JavaScript is the canonical example of
  why not, and any client-side sum would inherit it.
- **A component library with baked-in theming (MUI, Chakra, Ant)** — fast to start, and rejected
  for ten custom themes: theming through a library's own system means fighting its opinions and
  inheriting its bundle. Headless primitives (Radix or similar) plus own tokens give full
  control of the token layer with accessible behaviour supplied.
- **Tailwind alone** — usable, but ten runtime-switchable themes are better served by CSS custom
  properties; Tailwind may still be layered on top of the token variables if a sprint finds it
  worthwhile.
- **Theme as a set of component variants** — rejected: it puts theming inside components, so
  adding a theme touches every component instead of one file.

## Consequences

**Positive:** one implementation of every calculation; ten themes are ten token files; the type
generator catches contract changes at build time; the structure is predictable across the stack.

**Negative:**
- Live preview requires a network round trip; on a slow connection the Laboratory feels less
  immediate than a local calculation would. Accepted deliberately — correctness over
  perceived latency — and mitigated by debouncing and optimistic UI for non-monetary fields.
- Money as a string requires discipline at every boundary (a developer who writes
  `Number(price)` reintroduces the problem); a lint rule and code review carry this.
- The type generator must run whenever the API changes, and a stale generated file produces
  confusing errors. It runs in CI and is checked in.

## Compliance checks

- Lint rule: no hard-coded color literals in `features/**`; colors come from tokens.
- Lint rule: no arithmetic operators applied to values typed as `MoneyString`.
- Test: every registered theme passes the contrast assertions.
- Test: switching theme produces no change in any rendered numeric value.
- E2E: the price shown in the quote builder equals the price in the generated PDF.
