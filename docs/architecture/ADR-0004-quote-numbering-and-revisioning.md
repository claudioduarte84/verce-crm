# ADR-0004 — Quote Numbering and Revisioning

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-06
- **Sprint:** S0

## Context

Quotes are identified by `YYMMDD-N`, where `N` restarts at 1 every day:

```
06/09/2026 → 260906-1, 260906-2, 260906-3, 260906-4
```

Two requirements make this non-trivial:

1. **Concurrency.** Two simultaneous requests must never receive the same number.
2. **Revisions.** Editing never mutates the original. The original is semantically revision A
   but displays **no** suffix; the first visible revision is `B`:
   `260906-4` → `260906-4B` → `260906-4C` → … `Z` → `AA` → `AB` …

## Decision

### 1. Numbering: a counter table with an atomic upsert

```sql
CREATE TABLE quoting.quote_number_counter (
    series        text NOT NULL,          -- 'QUOTE' | 'PRODUCTION_ORDER'
    counter_date  date NOT NULL,          -- organization-timezone date
    last_sequence int  NOT NULL,
    PRIMARY KEY (series, counter_date)
);
```

Allocation is **one statement**, executed **inside the quote's transaction**:

```sql
INSERT INTO quoting.quote_number_counter (series, counter_date, last_sequence)
VALUES (:series, :date, 1)
ON CONFLICT (series, counter_date)
DO UPDATE SET last_sequence = quote_number_counter.last_sequence + 1
RETURNING last_sequence;
```

Why this is safe: `INSERT … ON CONFLICT DO UPDATE` takes a row-level lock on the conflicting
row. A concurrent transaction attempting the same date blocks until the first commits or rolls
back, then re-reads the updated value. Postgres guarantees this without an explicit
`SELECT … FOR UPDATE` and without a race between read and write.

Because the allocation shares the quote's transaction, a rolled-back quote also rolls back its
number — **no gaps**. The cost is that the counter row is locked for the (short) duration of
quote creation, serializing same-day quote creation. At this product's volume that is
irrelevant, and the trade is deliberate: a gap-free daily sequence is what an operator expects
from a document number.

**Defense in depth:** `UNIQUE (number_date, number_sequence)` and `UNIQUE (number_text)` on
`quoting.quote`. If the allocation logic is ever wrong, the database refuses the row rather
than issuing a duplicate document number.

**Date source:** `IClock.OrganizationToday()` — the current date in `America/Sao_Paulo`, not
UTC. A quote created at 22:00 BRT on 06/09 must be `260906-N`, not `260907-N`. Getting this
from `DateTime.UtcNow.Date` is the obvious bug and is called out in `CLAUDE.md`.

Production orders reuse the same table with `series = 'PRODUCTION_ORDER'`, so the mechanism is
built and tested once.

### 2. Revisioning: index + persisted suffix

`quote_revision.revision_index` is `1..N`. `revision_index = 1` is the original and renders
**no** suffix. The suffix is **bijective base-26** over `A..Z`:

| index | suffix | displayed |
|---|---|---|
| 1 | `A` (hidden) | `260906-4` |
| 2 | `B` | `260906-4B` |
| 26 | `Z` | `260906-4Z` |
| 27 | `AA` | `260906-4AA` |
| 28 | `AB` | `260906-4AB` |
| 53 | `BA` | `260906-4BA` |

```csharp
public static string ToSuffix(int revisionIndex)
{
    if (revisionIndex <= 1) return string.Empty;   // index 1 is "A", never displayed
    var n = revisionIndex;
    var sb = new StringBuilder();
    while (n > 0)
    {
        n--;                                       // bijective: no zero digit
        sb.Insert(0, (char)('A' + n % 26));
        n /= 26;
    }
    return sb.ToString();
}
```

The `n--` is the whole trick: spreadsheet columns are bijective base-26 (there is no digit
meaning zero), so index 27 is `AA`, not `BA`. A plain positional conversion gets this wrong.

**The suffix and the full display number are persisted** (`revision_suffix`, `display_number`),
not computed on read. A document already delivered to a customer has an identity; a future
change to the algorithm must not be able to rewrite it.

### 3. Editing = clone + change + new revision

Full procedure in [STATE-MACHINES §2](../STATE-MACHINES.md#2-revision-creation-edit-semantics).
Summary:

1. clone the current revision (items, snapshots, customer snapshot) into `index + 1`;
2. apply changes; re-price **only** the items the user touched;
3. new revision starts at `GENERATED` with a fresh `valid_until`;
4. the previous revision receives `superseded_by_revision_id`;
5. if the previous revision was still undecided it becomes `SUPERSEDED`; if it was terminal
   (`APPROVED`, `CANCELED`, `EXPIRED`) **it keeps its status**;
6. `quote.current_revision_id` moves to the new revision.

### 4. The `SUPERSEDED` state — an addition to the brief

The brief lists six statuses. A seventh is required, because two stated rules must hold at once:
"every revision must be preserved" and "only the current revision can be approved". Without a
distinct state, a replaced-but-undecided revision would have to either keep `GENERATED` (and
then appear in open-quote counts, in the expiration job, and as approvable) or be forced into
`CANCELED` (which asserts a commercial decision that never happened, corrupting the conversion
metric).

The pair (`status`, `superseded_by_revision_id`) therefore answers two different questions:
**how did this revision end** and **is it still current**. An approved revision that is later
superseded keeps `APPROVED` forever — the customer did approve it, on that date, and erasing
that would be a retroactive change of history.

## Alternatives considered

- **PostgreSQL sequence per day** — rejected: sequences cannot be reset per day without DDL or
  a scheduled job, they are non-transactional (a rollback leaves a gap by design), and they
  would need one sequence object per date.
- **`MAX(sequence) + 1` inside the transaction** — rejected: safe only under `SERIALIZABLE` or
  an explicit table lock, and both cost more than the upsert while being easier to get wrong.
- **Allocate the number in a separate committed transaction** — avoids holding the lock during
  quote creation, at the price of gaps whenever a quote creation fails. Rejected: contention is
  a non-problem here, and gaps in a customer-facing document number invite questions.
- **Application-side mutex / advisory lock** — rejected: solves at the application layer what
  the database already guarantees, and breaks the moment a second process exists.
- **Mutating the quote in place and keeping a separate revision-history table** — rejected: it
  makes the *current* row the truth and history a copy, which is exactly backwards for a
  product whose core promise is that issued documents are immutable.
- **Rendering the suffix from the index on read** — rejected as above (identity must be stable).

## Consequences

**Positive:** gap-free, human-meaningful daily numbers; duplicates impossible at two layers;
revision identity stable and complete; the same mechanism serves production orders.

**Negative:** same-day quote creation serializes on one row (irrelevant at this scale, but a
future high-volume scenario would need the separate-transaction variant, accepting gaps);
`SUPERSEDED` adds a state every consumer must handle — mitigated by the metric definitions in
[DATA-DICTIONARY §4.1](../DATA-DICTIONARY.md#41-conversion-rate-taxa-de-conversão) explicitly
excluding it as an outcome.

## Compliance checks

- Integration test: N parallel writers create quotes on the same date → N distinct sequential
  numbers, no gaps, no duplicates.
- Integration test: a failed quote creation leaves the counter unchanged.
- Unit test: suffix for indexes 1, 2, 26, 27, 28, 53, 702, 703.
- Integration test: creating a revision of an approved quote leaves the previous revision's
  status `APPROVED` and its monetary values untouched.
