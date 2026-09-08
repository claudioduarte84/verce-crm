# ADR-0002 — Money, Precision and Rounding

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-06
- **Sprint:** S0

## Context

The product exists to make cost trustworthy. The failure mode it must eliminate is a number
that differs between the screen, the PDF, the report and the database. That failure has three
classic causes:

1. binary floating point (`double`/`float`) used for money;
2. rounding applied at inconsistent points, or with inconsistent modes;
3. the same value recomputed independently in more than one place (typically the frontend and
   the PDF renderer both "helpfully" recalculating a total).

Costs here are also genuinely small: 72 g of filament at R$ 89,90/kg is R$ 6,4728. Rounding
that to two decimals at the component level and then multiplying by quantity produces visible
error, so intermediate precision must exceed presentation precision.

## Decision

### 1. Types
- **C#:** `decimal` for every monetary, percentage, weight, energy and quantity value.
  `double`/`float` are **banned** in domain assemblies, enforced by an architecture test.
- **PostgreSQL:** `numeric` with explicit precision and scale. Never `money`, never
  `double precision`, never `real`.

### 2. Scales

| Concept | PostgreSQL | Scale | Why |
|---|---|---|---|
| Presented money | `numeric(18,2)` | 2 | what a person pays |
| Intermediate money | `numeric(18,6)` | 6 | component costs, unit costs, cost per gram |
| Price per kg | `numeric(18,6)` | 6 | |
| Percent | `numeric(9,6)` | 6 | stored as a **fraction** |
| Grams | `numeric(12,3)` | 3 | milligram resolution |
| kWh | `numeric(12,4)` | 4 | |
| Quantity | `numeric(14,4)` | 4 | |
| Duration | `integer` | — | seconds |

### 3. Percentages are fractions
`0.175000` means 17,5%. Never `17.50`. The conversion happens once, at the UI edge.
A percent stored as `17.5` that reaches the pricing formula produces a denominator of
`1 - 17.5 = -16.5` — a negative price. The fraction convention plus the `CHECK (>= 0 AND < 1)`
constraint makes that class of bug unrepresentable in the database.

### 4. Rounding mode: `MidpointRounding.AwayFromZero`
Half-up, everywhere, no exceptions. Rejected: banker's rounding (`ToEven`, the .NET default).
It is statistically better and commercially surprising: an operator who computes R$ 12,345 by
hand expects R$ 12,35, not R$ 12,34. "Surprising" is precisely the defect this product exists
to remove. The mode is a single shared constant so it cannot drift.

### 5. Where rounding happens
Rounding is applied **only** at the points enumerated in
[CALCULATION-RULES §0.3](../CALCULATION-RULES.md#cr-003--where-rounding-happens): each cost
component (6), each subtotal (6), unit total cost (6), suggested price (2 + policy), then all
line-level money (2), and effective margin (6) computed **from already-rounded money**.

Two consequences are load-bearing:

- **Unit cost is never rounded to 2.** It is an internal quantity; rounding it would inject
  up to half a cent of error into every unit before multiplication.
- **Totals are sums of rounded lines**, never a recomputation from unrounded inputs. This is
  what makes the PDF footer equal the sum of the printed lines.

### 6. Single source of computed truth
The engine computes; everyone else displays. The frontend and the document renderer **never**
perform monetary arithmetic. Money crosses the API as a **string** (`"128.40"`), not a JSON
number, so no JavaScript `number` ever touches a currency value. Formatting uses
`Intl.NumberFormat('pt-BR')`.

This is the actual mechanism that guarantees `calculation = PDF = report = database = frontend`.
Scales and rounding modes only make the engine correct; forbidding recomputation is what keeps
everyone agreeing with it.

### 7. Currency and timezone
BRL is implicit in v1 — `Money` carries no currency code, and the database has no currency
column beyond `company_profile.currency`. Multi-currency is a deliberate non-goal; adding
it later means adding a column and a `Currency` field to the value object, which is a
contained change. Timezone: instants in UTC (`timestamptz`), business dates in
`America/Sao_Paulo` via `IClock.OrganizationToday()`.

### 8. Engine versioning
`CalculationEngineVersion` is persisted with every snapshot and experiment result. Old records
are never recomputed; a version mismatch is surfaced, not silently reinterpreted.

## Alternatives considered

- **Integer cents.** Immune to decimal representation issues and popular in payment systems.
  Rejected because this domain multiplies and divides constantly (grams ÷ 1000 × price/kg,
  cost ÷ denominator) and integer arithmetic would force explicit scaling at every step —
  more code, more places to get it wrong, and `decimal` already gives exact base-10 behaviour
  with sufficient precision.
- **`numeric` without precision.** PostgreSQL allows unconstrained `numeric`. Rejected: the
  constraint is documentation and a guard rail; unbounded scale lets an application bug persist
  a value with 15 decimals that then rounds differently on read.
- **Banker's rounding.** Rejected above.
- **Rounding everything to 2 immediately.** Rejected: R$ 6,4728 → R$ 6,47 per component, times
  many components, times quantity, drifts visibly.

## Consequences

**Positive:** one rounding contract, testable; no floating-point surprises; a stated,
verifiable equality between every surface that shows a number.

**Negative:** developers must remember that percent is a fraction and that money is a string on
the wire. Both are enforced (CHECK constraints, generated API types), but both are learnable
surprises for a new agent — hence their prominence in `CLAUDE.md`.

**Testing obligation:** an integration test must assert that a given quote reports byte-identical
monetary values through the API payload, the database rows and the rendered PDF text.
