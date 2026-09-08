# ADR-0012 — Domain Events, Unit of Work and the Outbox

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-06
- **Revised:** 2026-09-07 (gate blockers B-001, B-003) · **2026-09-07 (re-gate corrections A, C)**
- **Sprint:** S0

> **Scope note.** Everything here is an **architecture contract for S1 to implement**. No code
> exists. Where this document says "the dispatcher does X", read "S1 SHALL implement a dispatcher
> that does X".

## Context

Modules must react to each other without depending on each other. `Quoting` must not reference
`Production`, yet approving a quote must create a production order — and must create **exactly
one**, atomically.

Other reactions are different in kind: rendering a PDF is slow and involves an external process;
calling OpenAI can fail for reasons unrelated to the business action. Neither should be able to
roll back an approval.

The brief forbids adding RabbitMQ, Kafka or Redis without objective need.

> **Revision history**
>
> - **2026-09-07 (gate B-001, B-003).** The protocol described only "dispatch after
>   `SaveChanges`". With a *single* `SaveChanges`, a handler creating a new aggregate would never
>   have it persisted. The ADR also said the handler "swallows a unique violation", invalid in
>   PostgreSQL. The outbox had no terminal state, lease, claim strategy or retention.
> - **2026-09-07 (re-gate A, C).** The multi-wave loop existed but left the **event buffer and
>   acknowledgement** undefined (when an event enters the buffer, when it is consumed, how
>   duplicate dispatch is prevented), and used one indistinguishable event marker for two very
>   different lifecycles. The outbox lease had **no fencing token**, so a frozen worker waking
>   after its lease expired could overwrite a newer worker's result; the attempt boundary allowed
>   a hidden extra attempt after a crash on the final attempt; and `FAILED` had no administrative
>   disposition or honest retention. All corrected below.

---

# Part I — Events, the buffer, and the Unit of Work

## 1. Two event types, two interfaces, never one

```csharp
public interface IEvent                     // common envelope only
{
    Guid     EventId       { get; }         // UUID v7, assigned at construction
    string   EventType     { get; }
    DateTime OccurredAtUtc { get; }
    Guid     CorrelationId { get; }
    Guid?    CausationId   { get; }
}

public interface IDomainEvent      : IEvent { }   // synchronous, in-transaction
public interface IIntegrationEvent : IEvent { }   // post-commit, via the outbox
```

A concrete event implements **exactly one** of the two. An architecture test fails a type
implementing both, or implementing `IEvent` directly.

| | `IDomainEvent` | `IIntegrationEvent` |
|---|---|---|
| Runs | between save waves, **inside** the business transaction | after commit, from `platform.outbox_message` |
| Delivery | exactly once **within this Unit of Work** (§4) | at-least-once |
| Failure | **rolls back the business action** | retried; never affects the business action |
| May do | database work through the ambient `DbContext` only | HTTP, Chromium, filesystem, external APIs |
| Use when | the effect is part of the same invariant | the effect may lag or fail independently |
| Examples | `QuoteApproved`, `ActualMaterialRecorded` | `GenerateQuotePdfRequested`, `AiInsightRequested`, `SendEmailRequested` |

*(Renamed from `IPostCommitEvent` in this revision. `IIntegrationEvent` states what the thing
**is** — an event crossing a transactional boundary — rather than when it happens to run.)*

> **Rule.** An operation required to maintain a transactional invariant must **never** be an
> `IIntegrationEvent`. The outbox guarantees eventual delivery, not atomicity.

One business moment may raise both: approving a quote raises `QuoteApproved` (synchronous,
creates the production order) **and** `GenerateQuotePdfRequested` (integration).

## 2. The pending-event buffer

Every aggregate root owns a buffer:

```csharp
public abstract class AggregateRoot : Entity, IAggregateRoot
{
    private readonly List<IEvent> _pending = new();
    public IReadOnlyList<IEvent> PendingEvents => _pending;

    protected void Raise(IEvent e) => _pending.Add(e);       // domain code calls this
    internal IReadOnlyList<IEvent> DrainPending()            // ONLY the UoW calls this
    {
        var drained = _pending.ToArray();
        _pending.Clear();
        return drained;
    }
}
```

- `Raise` is **`protected`** — only the aggregate's own behaviour raises its events.
- `DrainPending` is **`internal`** to the platform assembly — only the Unit of Work drains.
  Nothing else can consume, re-read or replay the buffer. An architecture test asserts no call
  site outside `Verce.Platform` invokes it.
- The buffer is **in-memory only**. It is never persisted, so a rolled-back transaction leaves
  no residue: the `DbContext` and its tracked graph are request-scoped and discarded.

### Envelope field assignment

| Field | Assigned | By |
|---|---|---|
| `EventId` | at construction, inside the aggregate | `Guid.CreateVersion7()` |
| `EventType` | at construction | the CLR type name |
| `OccurredAtUtc` | at construction | `IClock.UtcNow` — the moment the domain decided, not the moment of dispatch |
| `CorrelationId` | at construction | ambient `AmbientOperationContext.CorrelationId` |
| `CausationId` | at construction | the `EventId` of the event currently being dispatched, or `null` in wave 1 (§5) |
| `WaveIndex` | **at dispatch**, not at construction | the Unit of Work |

`WaveIndex` is deliberately not on `IEvent`: an event does not know which wave will carry it.
It is recorded on the dispatch log record and on any audit or outbox row the wave produces.

## 3. The Unit of Work algorithm

One command = one transaction = one `VerceDbContext` = N save/dispatch **waves**.

```text
────────────────────────────────────────────────────────────────────────────────
UnitOfWork.ExecuteAsync(command)
────────────────────────────────────────────────────────────────────────────────
 1  ctx       := the request-scoped VerceDbContext                (exactly one)
 2  op        := new AmbientOperationContext(correlationId, requestId,
                        actorUserId, actorDisplayName, source)     (§10)
 3  BEGIN TRANSACTION (READ COMMITTED)
 4
 5  await commandHandler(ctx)          // wave 0: mutates aggregates, which Raise() events
 6
 7  waveIndex   := 0
 8  integration := []                  // IIntegrationEvent accumulator
 9
10  loop:
11      // ---- ACKNOWLEDGE: drain every tracked aggregate's buffer, atomically ----
12      drained := ctx.ChangeTracker.Aggregates().SelectMany(a => a.DrainPending())
13      waveSnapshot := drained.OfType<IDomainEvent>().ToList()      // FIFO, §6
14      integration += drained.OfType<IIntegrationEvent>()
15
16      hasPendingWrites := ctx.ChangeTracker.HasChanges()
17      if waveSnapshot.IsEmpty AND NOT hasPendingWrites:
18          break                                                    // quiescent
19
20      waveIndex := waveIndex + 1
21      if waveIndex > MAX_EVENT_WAVES:                              // §7
22          throw DomainEventWaveLimitExceeded(chain)                // → ROLLBACK
23      op.WaveIndex := waveIndex
24
25      // ---- SAVE ----
26      await ctx.SaveChangesAsync()        // version + audit interceptors run here
27
28      // ---- DISPATCH: the snapshot only; never the live buffer ----
29      foreach e in waveSnapshot:
30          op.CurrentEvent := e            // makes e.EventId the CausationId of anything raised
31          await dispatcher.DispatchAsync(e, ctx)
32      op.CurrentEvent := null
33      // events raised by handlers are now in aggregate buffers → drained by the NEXT iteration
34
35  // ---- quiescent: no pending domain events, no pending writes ----
36  outboxWriter.Write(integration, op)     // §12 — adds rows to ctx
37  await ctx.SaveChangesAsync()            // persists outbox rows + final audit
38
39  COMMIT
────────────────────────────────────────────────────────────────────────────────
Any exception at any line → ROLLBACK. There is no partial commit and no partial
acknowledgement.
```

### 3.1 The order is COLLECT → SAVE → DISPATCH. One rule, everywhere.

This was re-examined as the re-gate required. **COLLECT → SAVE → DISPATCH is correct and is
frozen.** No document may state a different order.

Why not COLLECT → DISPATCH → SAVE: the `QuoteApproved` handler creates a `ProductionOrder` that
references `quote_revision_id`. Dispatching before the save would run the handler while the
revision's `status = APPROVED` is still only in the change tracker — the handler could not read
its own cause through the context in a consistent state, and any handler issuing raw SQL
(`INSERT … ON CONFLICT`, §11) would violate the FK because the row it depends on is not yet in
the transaction. Saving first puts the cause in the transaction; the handler observes it; and
because the transaction is still open, **everything remains rollback-able**. Both requirements
of the re-gate are satisfied simultaneously.

### 3.2 Why the drain happens before the save

Line 12 runs before line 26 so that:

- the wave's event set is **frozen** before any interceptor or handler can perturb it;
- an event raised by an interceptor during `SaveChanges` cannot sneak into the wave it is
  already dispatching — it lands in the buffer and is picked up by the next iteration.

### 3.3 The exit condition tests writes as well as events

Line 17 breaks only when there are **neither** pending domain events **nor** pending writes. A
handler that mutates an aggregate without raising an event would otherwise leave those changes
unsaved. (`PendingWriteWithoutEventStillSaved` is an S1 contract.)

## 4. "Exactly once" — scope of the claim

> **Within one Unit of Work, each `IDomainEvent` *instance* is dispatched exactly once.**

This is guaranteed by construction: draining removes the instance from the buffer, and dispatch
reads only the drained snapshot. There is no path that returns a drained instance to a buffer.

This is **not** distributed exactly-once, and **not** exactly-once across retries:

- If the transaction rolls back and the client retries, that is a **new** Unit of Work. The
  aggregates are re-loaded from the database and the domain raises **new event instances** with
  **new `EventId`s**. Handlers therefore run again, and must be correct under that — which they
  are, because they are inside a transaction that either commits wholly or not at all.
- `IIntegrationEvent` delivery is **at-least-once** by design (§14).

## 5. Causation

```
Command  ──►  E1 (causation = null)
              └── handler ──►  E2 (causation = E1.EventId)
                               └── handler ──► E3 (causation = E2.EventId)
```

During dispatch the Unit of Work sets `op.CurrentEvent` (line 30). `AggregateRoot.Raise` reads
it and stamps `CausationId`. Wave-1 events have `CausationId = null` because their cause is the
command, identified by `CorrelationId`.

Every dispatch logs `correlation_id`, `causation_id`, `event_id`, `event_type`, `wave_index`, so
a multi-wave chain is reconstructable from logs alone — which is what makes a wave-limit failure
(§7) diagnosable.

## 6. Dispatch order

Within a wave: events in the order they were raised (FIFO per aggregate; aggregates in
change-tracker order). Handlers for one event run in **explicit registration order**, never
assembly-scan order. A test that passes because two handlers happened to run in a lucky order is
not a passing test.

## 7. `MAX_EVENT_WAVES = 8`

The deepest *legitimate* chain in this domain is four waves:

```
wave 1  QuoteRevision.Approve()          → QuoteApproved
wave 2  create ProductionOrder           → ProductionOrderCreated
wave 3  write initial status history     → (future) StockReservationRequested
wave 4  write stock reservation movement → (quiescent)
```

Eight is double that, leaving headroom for two future links. Beyond it is a cycle, not depth.
`DomainEventWaveLimitExceeded` rolls back and logs the full causation chain. HTTP 500 — a
programming error, not a user error.

## 8. Handler failure — no partial acknowledgement

Any exception from a handler propagates, the transaction rolls back, nothing is persisted:
not the command's writes, not earlier waves' writes, not the outbox rows.

Because the buffer is in-memory and the `DbContext` is discarded, there is **no partially
acknowledged state to reconcile**. A retry starts a completely fresh business transaction with
freshly loaded aggregates. Handlers must not catch exceptions to "keep going"; an effect that is
genuinely optional is an `IIntegrationEvent`, not a swallowed exception.

## 9. One `DbContext`, one transaction

> **No `IDomainEvent` handler may open its own `DbContext`, begin its own transaction, or
> resolve a new service scope.**

Banned constructor dependencies: `IDbContextFactory<>`, `IServiceScopeFactory`,
`IServiceProvider`, `IDbConnection`, `HttpClient`, `IDocumentRenderer`, `IAiClient`. Banned
call: `Database.BeginTransaction()`. Enforced by an architecture test over every
`IDomainEventHandler<>` implementation.

A handler with its own context would write **outside** the transaction and survive a rollback —
producing exactly the orphaned production order this design exists to prevent.

## 10. `AmbientOperationContext`

Created once per command, scoped to the request, carried through every wave:

`CorrelationId` · `RequestId` · `ActorUserId` · `ActorDisplayName` · `Source`
(`API` / `JOB` / `CLI` / `SYSTEM`) · `OperationStartedAtUtc` · `WaveIndex` (mutable) ·
`CurrentEvent` (mutable) · `VersionHandledAggregates` (set, see
[ADR-0011 §2](ADR-0011-identifiers-and-concurrency.md)).

## 11. The quote-approval invariant

> **An `APPROVED` `QuoteRevision` has exactly one `ProductionOrder`, created in the same
> transaction. If the production order cannot be created, the approval does not happen.**

Three layers:

1. **The status guard plus the row lock.** Approval updates the revision row, so two concurrent
   approvals serialize on it; the second re-reads, finds the revision terminal, and fails with
   `QUOTE_REVISION_ALREADY_DECIDED` before any insert is attempted.
2. **`UNIQUE (quote_revision_id)`** on `production.production_order` — defense in depth.
3. **`INSERT … ON CONFLICT (quote_revision_id) DO NOTHING`** then `SELECT`, for the replay path.

### Why "catch the unique violation and continue" is forbidden

When a statement raises an error inside a PostgreSQL transaction, the transaction enters the
*aborted* state. Every subsequent command fails with `25P02 — current transaction is aborted`,
and the only legal next step is `ROLLBACK`. Catching the .NET exception does **not** un-abort
it; the following `SELECT` and the `COMMIT` both fail. The code would appear to handle the race
while destroying the entire transaction.

Only two forms are correct: `ON CONFLICT … DO NOTHING` (used here — no error is raised at all),
or an explicit `SAVEPOINT` before the insert with `ROLLBACK TO SAVEPOINT` on conflict.

---

# Part II — The transactional outbox

## 12. When outbox rows are written

Integration events accumulate during the waves (line 14) and are written at line 36 — **inside
the business transaction**.

> The business row and the outbox row commit together or not at all.

That property is what makes "no message broker" defensible: a message cannot be lost if the
process dies after commit, and cannot fire for a rolled-back transaction.

## 13. State machine

```
                     ┌───────────────────────────────────────────────────┐
                     │ retryable failure AND attempt_count < max_attempts │
                     │ available_at := now + backoff                      │
                     ▼                                                    │
    enqueue     ┌─────────┐   claim (token + lease)   ┌────────────┐      │
   ───────────► │ PENDING │ ────────────────────────► │ PROCESSING │ ─────┤
   (in the      └─────────┘  eligible only:           └─────┬──────┘      │
    business         ▲        status = PENDING              │ success     │
    transaction)     │        available_at <= now()         ▼             │
                     │        attempt_count < max     ┌───────────┐       │
                     │                                │ PROCESSED │       │
                     │                                └───────────┘       │
                     │  manual requeue (Owner)                            │
                     │  attempt_count := 0                                │
                     │                            ANY of:                 │
                     │                              • retryable failure   │
                     │                                AND attempt_count   │
                     │                                >= max_attempts     │
                     │                              • non-retryable       │
                     │                              • lease expired AND   │
                     │                                attempt_count       │
                     │                                >= max_attempts     │
                     │                                    ▼               │
                     │                              ┌────────┐            │
                     └──────────────────────────────┤ FAILED ├────────────┘
                                                    └────┬───┘
                                                         │ failure_disposition
                                                   ACTIVE ──► DISMISSED
```

| State | Meaning | Terminal |
|---|---|---|
| `PENDING` | awaiting `available_at`; claimable **only while `attempt_count < max_attempts`** | no |
| `PROCESSING` | leased by a worker holding `processing_token` until `lease_until` | no |
| `PROCESSED` | consumer succeeded — **this is what "resolved" means** | yes |
| `FAILED` | budget exhausted or non-retryable; carries `failure_disposition ∈ {ACTIVE, DISMISSED}` | yes until requeued |

> **Corrected 2026-09-07 (re-gate B-RG2-001).** Three contradictions are removed here:
> a retryable failure on the **final** attempt previously returned the message to `PENDING`
> (creating a phantom attempt N+1); the claim query did not check the attempt budget, so an
> exhausted message could be claimed anyway; and `RESOLVED` was modelled as a *failure
> disposition* even though a requeued message that succeeds ends at `status = PROCESSED`, where
> the schema forbids a disposition. `RESOLVED` is gone — **success is represented by
> `PROCESSED`**, which is what it always meant.

## 14. Claim, with a fencing token

Claiming is a short transaction, separate from the consumer's work:

```sql
WITH claimed AS (
    SELECT id
    FROM platform.outbox_message
    WHERE status = 'PENDING'
      AND available_at <= now()
      AND attempt_count < max_attempts        -- an exhausted message is never claimable
    ORDER BY available_at, created_at
    FOR UPDATE SKIP LOCKED
    LIMIT :batch_size                                  -- default 20
)
UPDATE platform.outbox_message m
SET status                = 'PROCESSING',
    processing_token      = :new_token,                -- UUID v7, fresh per claim
    processing_started_at = now(),
    lease_until           = now() + :lease_duration,   -- default 5 minutes
    worker_id             = :worker_id,
    attempt_count         = m.attempt_count + 1
FROM claimed
WHERE m.id = claimed.id
RETURNING m.*;
```

`FOR UPDATE SKIP LOCKED` means concurrent workers skip rows another worker is claiming rather
than blocking. The claim commits immediately; the consumer then runs outside that transaction.

Each claim also inserts a row into `platform.outbox_message_attempt` (§19) carrying the
message's **current `execution_generation`** and `attempt_number = attempt_count` as just
incremented.

## 15. Lease fencing — the correction

**Every state transition made by a worker must carry its `processing_token`.** The token is the
fence: it proves the caller still holds the lease it thinks it holds.

```sql
-- completion
UPDATE platform.outbox_message
SET status = 'PROCESSED', processed_at = now(), lease_until = NULL, processing_token = NULL
WHERE id = :id AND status = 'PROCESSING' AND processing_token = :token;

-- retryable failure — branches on the remaining budget (§16)
UPDATE platform.outbox_message
SET status = CASE WHEN attempt_count >= max_attempts THEN 'FAILED' ELSE 'PENDING' END,
    failure_disposition = CASE WHEN attempt_count >= max_attempts THEN 'ACTIVE' END,
    failed_at           = CASE WHEN attempt_count >= max_attempts THEN now() END,
    failure_reason      = CASE WHEN attempt_count >= max_attempts
                               THEN 'RETRY_BUDGET_EXHAUSTED' END,
    available_at        = CASE WHEN attempt_count >= max_attempts
                               THEN NULL ELSE now() + :backoff END,
    last_error = :err, last_error_at = now(),
    lease_until = NULL, processing_token = NULL
WHERE id = :id AND status = 'PROCESSING' AND processing_token = :token;

-- heartbeat (§17)
UPDATE platform.outbox_message
SET lease_until = now() + :lease_duration
WHERE id = :id AND status = 'PROCESSING' AND processing_token = :token;
```

**0 rows affected means the caller is stale.** It must log at `Warning` with the message id and
its token, abandon the message, and **must not** retry the write or raise a business error. The
message now belongs to another worker.

### 15.1 The stale-worker scenario, stated normatively

```
t0  Worker A claims message M      → token = TA, lease_until = t0 + 5min
t1  Worker A freezes (GC pause, host suspend, network partition)
t2  lease expires
t3  Reclaim sweep returns M to PENDING
t4  Worker B claims M              → token = TB   (TB ≠ TA)
t5  Worker A wakes and attempts completion WHERE processing_token = TA
    → 0 rows affected
    → A is stale. It logs and abandons. It does NOT overwrite B's state.
```

Without the token, A's `WHERE id = @id AND status = 'PROCESSING'` would match B's row and mark
as `PROCESSED` work that B is still doing — silently losing B's outcome. This is the defect the
re-gate identified.

## 16. Reclaim sweep and the attempt boundary

`attempt_count` increments **at claim** (§14). The sweep therefore must not grant a hidden extra
attempt:

```sql
-- expired leases
UPDATE platform.outbox_message
SET status           = CASE WHEN attempt_count >= max_attempts THEN 'FAILED' ELSE 'PENDING' END,
    failed_at        = CASE WHEN attempt_count >= max_attempts THEN now() ELSE NULL END,
    failure_disposition = CASE WHEN attempt_count >= max_attempts THEN 'ACTIVE' ELSE NULL END,
    failure_reason   = CASE WHEN attempt_count >= max_attempts
                            THEN 'LEASE_EXPIRED_ON_FINAL_ATTEMPT' ELSE NULL END,
    available_at     = CASE WHEN attempt_count >= max_attempts THEN NULL
                            ELSE now() + :backoff END,
    lease_until      = NULL,
    processing_token = NULL
WHERE status = 'PROCESSING' AND lease_until < now();
```

> **One terminal-attempt rule, regardless of how the attempt ended.**
>
> ```
> attempt_count >= max_attempts  ⇒  FAILED
> ```
>
> This holds identically for an explicit retryable exception (§15) and for a worker crash
> detected by lease expiry (here). There is exactly one budget rule, so the two paths can never
> disagree — the re-gate found them disagreeing, with the explicit path returning an exhausted
> message to `PENDING`.

`available_at` is set to `NULL` on the `FAILED` branch: a terminal message is not scheduled for
anything, and leaving a stale future timestamp there would make it look pending to a reader.

The attempt is deliberately **not refunded** when a worker crashes: a process that dies on a
given message will die on it again, and refunding the attempt would let a crash-loop run
forever. This is stated so nobody "fixes" it later.

## 17. Heartbeat

**Heartbeat is part of the contract and is fenced** (§15): `ExtendLease(id, token)`.

**The extension is not caller-supplied.** It resets the lease to
`now() + configured lease_duration` — the caller cannot request an arbitrary or unbounded
window, so a buggy consumer cannot hold a message indefinitely. A worker that stops heart-beating
loses the lease after one lease period, whatever it intended.

**v1 consumers do not use it.** Every v1 consumer has a hard internal timeout well below the
5-minute lease: document render 30 s, AI call 60 s, smart-plug poll 10 s. A startup validation
asserts `consumer.Timeout < lease_duration` for every registered consumer, so a consumer cannot
silently outlive its lease.

A future consumer that genuinely needs longer than the lease must call `ExtendLease`; the API
exists and is fenced so that adding one is not a platform change.

## 18. `FAILED` disposition — administrative, not destructive

`FAILED` alone is not an outcome an operator can act on, so the terminal state carries a
disposition. It is meaningful **only** while `status = 'FAILED'`, and there are exactly two
values:

| `failure_disposition` | Meaning | Set by |
|---|---|---|
| `ACTIVE` | unresolved; needs a human; counts toward `degraded` health | the system, on entering `FAILED` |
| `DISMISSED` | a human decided this effect must never be retried | `Owner`, reason required |

> **`RESOLVED` was removed (re-gate B-RG2-001).** It tried to describe "requeued and later
> succeeded" — but such a message ends at `status = 'PROCESSED'`, where the schema forbids a
> disposition at all. The state was unreachable and the model self-contradictory.
> **Success is `PROCESSED`.** That already means resolution; nothing else is needed to express it.

Dismissal records `dismissed_at`, `dismissed_by`, `dismissal_reason`. **It never deletes the
message, its error, or its attempt history** — the disposition is added alongside those facts.

**A `DISMISSED` message is not claimable.** The claim query filters on `status = 'PENDING'`, and
a dismissed message is `FAILED`, so this follows from the state machine rather than from an extra
predicate.

`/health/ready` and the diagnostics endpoint count only `failure_disposition = 'ACTIVE'`, so
triaged failures stop generating noise without being erased.

## 19. Attempt history and execution generations

> **Corrected 2026-09-07 (re-gate B-RG3-001).** Manual requeue reset `attempt_count` to 0 while
> `outbox_message_attempt` was keyed `UNIQUE (outbox_message_id, attempt_number)`. The first
> claim after a requeue therefore tried to insert `attempt_number = 1` again and **collided with
> the previous round's history** — the insert would fail and the message could never be retried.
> Fixed by separating the *retry budget of the current round* from the *historical identity of
> an attempt*.

### 19.1 Three separate concepts

| Concept | Where | Meaning |
|---|---|---|
| **Business identity** | `outbox_message.id` + `idempotency_key` | *which effect* — never changes |
| **Current retry round** | `outbox_message.execution_generation` | *which attempt round* — increments on requeue |
| **Budget within the round** | `attempt_count` vs `max_attempts` | how many attempts this round has consumed |
| **Historical attempt identity** | `(outbox_message_id, execution_generation, attempt_number)` | *which specific attempt ever* — unique forever |

`attempt_count` means **attempts consumed in the current round**, and resetting it on requeue is
correct. What was wrong was letting a *round-scoped* counter serve as *lifetime* history identity.

### 19.2 `execution_generation`

`outbox_message.execution_generation int not null default 1`.

- A newly enqueued message starts at **generation 1**. (One base, used in every document.)
- Manual requeue increments it: `1 → 2 → 3 …`
- It is **stored on the message**, never derived at runtime from the history table.

**`requeue_count` is removed as a stored column.** It was a second mutable counter tracking the
same fact and could drift from the generation. Where a requeue count is wanted, it is
**derived**: `requeue_count = execution_generation - 1`. One counter, one source of truth.

### 19.3 The attempt row

`platform.outbox_message_attempt` is append-only, one row per claim:

`id` (uuid PK), `outbox_message_id`, **`execution_generation`**, **`attempt_number`**,
`processing_token`, `worker_id`, `started_at`, `finished_at`,
`outcome` (`SUCCEEDED` | `RETRYABLE_FAILURE` | `NON_RETRYABLE_FAILURE` | `LEASE_EXPIRED`),
`error_type`, `error_message`.

```sql
UNIQUE (outbox_message_id, execution_generation, attempt_number)
```

The row keeps its own uuid PK; the unique constraint protects the **semantic** history. Because
`execution_generation` participates, generation 2's attempt 1 can never collide with generation
1's attempt 1.

### 19.4 Worked example — the flow that used to break

```
Message M, idempotency_key = K, max_attempts = 5

generation 1:  A1 fail · A2 fail · A3 fail · A4 fail · A5 fail
               → status = FAILED, failure_disposition = ACTIVE

Owner requeues (reason recorded, audited):
               execution_generation 1 → 2
               attempt_count 5 → 0
               status → PENDING, failure_disposition → NULL, available_at → now()
               idempotency_key: STILL K          ← unchanged

next claim:    attempt_count 0 → 1
               history insert: (M, generation 2, attempt 1)   ← no collision
consumer succeeds:
               status = PROCESSED, failure_disposition = NULL

outbox_message_attempt now holds:
   G1/A1  G1/A2  G1/A3  G1/A4  G1/A5   (all retained, untouched)
   G2/A1                                (succeeded)
```

### 19.5 The generation is operational, never business

> **A requeue does not change `idempotency_key`.** Requeue means "retry *this same* logical
> effect", so the consumer's deduplication must still recognize it. `execution_generation`
> records *operational retry history* and must never be mixed into a business key. A render
> requeued in generation 2 still keys on the same `render_request_id` and therefore still cannot
> produce a second `ISSUED` document.

### 19.6 Why not a lifetime attempt counter

A monotonic lifetime `attempt_sequence` alongside a round-scoped number would also avoid the
collision, but it means two counters to keep consistent for no added expressiveness:
`(generation, attempt_number)` already identifies every attempt uniquely and reads naturally in
diagnostics ("generation 2, attempt 1"). One mechanism, kept understandable.

## 20. Retry policy

| Attempt | On retryable failure | Next `available_at` |
|---|---|---|
| 1 | retry | `now + 1 min` |
| 2 | retry | `now + 5 min` |
| 3 | retry | `now + 30 min` |
| 4 | retry | `now + 2 h` |
| 5 | — | → **`FAILED`** (`disposition = ACTIVE`) |

`max_attempts` defaults to 5 and is a **per-message column**, so a consumer may set its own
budget without a schema change (the AI consumer sets 3: a paid call that failed three times is
unlikely to succeed on the fourth). Total window ≈ 2 h 36 min — enough to ride out a Chromium
restart, a database failover or a provider outage; beyond that the fault is not transient.

`NonRetryableOutboxException` (invalid payload, unknown `message_schema_version`, template
version missing, source entity deleted) goes straight to `FAILED` — retrying something that can
never succeed only delays discovery.

## 21. Manual requeue

`FAILED → PENDING`, `Owner` only, reason required. Permitted from **either** disposition:
`ACTIVE` (the normal case) or `DISMISSED` (an explicit revival, which the audit record makes
visible).

It **preserves message identity** — `id` and `idempotency_key` unchanged, so consumer-side
deduplication still holds — and grants a clean budget:

| Field | On requeue |
|---|---|
| `status` | → `PENDING` |
| **`execution_generation`** | **incremented** (`N → N+1`) — opens a new attempt round |
| **`attempt_count`** | **→ `0`** — a fresh retry budget for that round |
| `failure_disposition` | → `NULL` (the row is no longer terminal) |
| `failed_at`, `failure_reason` | → `NULL` |
| `available_at` | → `now()` |
| `processing_token`, `lease_until` | → `NULL` |
| `max_attempts` | unchanged |
| `id`, `idempotency_key` | **unchanged** — same business effect (§19.5) |
| `last_error` | **kept** |
| `outbox_message_attempt` rows | **all retained**, under their original generation |

All of the above happens in **one transaction**, so a message can never be left with a bumped
generation and an unreset budget, or vice versa.

**Why `attempt_count` resets rather than accumulates.** The counter's only job is to bound the
*current* round against `max_attempts`; carrying it forward would make each requeue progressively
shorter until a message could not be retried at all, which is not what an operator requesting a
retry means. Nothing is lost: the full history lives in the append-only
`outbox_message_attempt` table under its own generation, `execution_generation - 1` gives the
requeue count, and `last_error` still shows why it failed last time.

**Requeue from `DISMISSED`** follows exactly the same rule — a deliberate revival, audited like
any other requeue, opening a new generation.

Requeuing without fixing the cause simply fails again, and both rounds remain visible.

## 22. Consumer idempotency

At-least-once delivery makes idempotency mandatory. `idempotency_key` is **unique**, so the same
logical effect cannot even be enqueued twice.

| Consumer | `idempotency_key` |
|---|---|
| `GenerateQuoteDocument` | `document:{render_request_id}` |
| `GenerateProductionOrderDocument` | `document:{render_request_id}` |
| `RunAiInsight` | `ai-insight:{run_id}` |
| Future e-mail | `email:{business_event_id}:{recipient}:{template_version}` |

**Document generation** is the case that could corrupt history. `render_request_id` is a UUID
generated when the render is *requested*, carried in the payload, and stored on
`documents.generated_document.render_request_id` with a **unique index**. The consumer inserts
with `ON CONFLICT (render_request_id) DO NOTHING`; if no row is inserted the document already
exists and the consumer reports success without re-rendering. The key identifies the **request**,
so a retry deduplicates while a deliberate re-issue (a new request id) correctly produces a new
document, even against the same template version.

## 23. Retention

| State | Retention |
|---|---|
| `PROCESSED` | **90 days**, then pruned with its attempt rows |
| `FAILED`, `disposition = ACTIVE` | **never auto-deleted** |
| `FAILED`, `disposition = DISMISSED` | 365 days from `dismissed_at` |
| `PENDING` / `PROCESSING` | never pruned |

> The retention job must never delete an unresolved failure. Deleting evidence of a problem
> nobody has looked at is worse than the storage it saves.

## 24. Message schema versioning

`message_schema_version int` accompanies every payload. A consumer either handles the version or
throws `NonRetryableOutboxException`. Outbox rows survive deployments: a message enqueued by the
old build may be consumed by the new one, and silently deserializing a changed shape is how a
retry corrupts data.

## 25. Health and visibility

Full policy in [OPERATIONS §9](../OPERATIONS.md#9-health-endpoints). In summary:

- **A business message in `FAILED` does not make the application unready.** Returning 503 for a
  failed PDF render would make an orchestrator kill and restart a perfectly healthy container,
  repeatedly, and would not fix the PDF. Unresolved failures surface as **`degraded`**
  (HTTP 200) and as metrics.
- **The dispatcher not running is different** — that is infrastructure, and it returns 503.

### 25.1 Stall detection counts only *eligible* messages

> **Corrected 2026-09-07 (re-gate H-RG2-002).** The previous rule — "oldest `PENDING` older than
> 30 minutes" — produced **false stalls**. A message is legitimately `PENDING` while waiting out
> its backoff (up to 2 h on the fourth attempt), and a future-scheduled message may wait far
> longer. Under the old rule a single retrying message would drive `/health/ready` to 503 and
> trigger a container-replacement loop, while the dispatcher was working perfectly.

A message is **eligible** when the dispatcher *should* be able to process it right now:

```sql
status = 'PENDING'
  AND available_at <= now()
  AND attempt_count < max_attempts
```

The stall metric is **how long an eligible message has been waiting past the moment it became
eligible** — not its age since creation:

```sql
SELECT max(now() - available_at)
FROM platform.outbox_message
WHERE status = 'PENDING'
  AND available_at <= now()
  AND attempt_count < max_attempts;
```

If that exceeds `outbox.stuck_threshold_minutes` (default **30**), the dispatcher is not draining
the queue and `/health/ready` returns **503**.

A message with `available_at` in the future contributes nothing, because a backoff is the system
working as designed, not a fault. `created_at` is deliberately not used: it would make a message
that failed three times look "old" purely because it has been retried.

## 26. What is deliberately not built

No message broker, no distributed transactions, no saga orchestrator, no abstraction over a
hypothetical transport. Events are C# records dispatched in-process; the outbox is two tables
and three jobs. If a broker becomes necessary, the outbox is already the correct integration
point and only its dispatcher changes.

## Alternatives considered

- **Direct cross-module calls** — creates the dependency the module structure prevents.
- **A single `SaveChanges` with dispatch afterwards** — the original B-001 defect: handler writes
  never persisted.
- **COLLECT → DISPATCH → SAVE** — re-examined for this revision and rejected in §3.1.
- **Dispatch entirely after commit** — loses atomicity for `QuoteApproved`.
- **Clearing the buffer *after* dispatch instead of before** — a handler that raises an event on
  the *same* aggregate would append to a list being iterated, and a naive "clear all" afterwards
  would silently discard it. Draining up-front makes that structurally impossible.
- **One event interface for both lifecycles** — the pre-revision state. It let a reviewer (and
  would let an implementer) route an invariant-critical effect through the outbox by accident.
- **Catch-and-continue on unique violation** — invalid in PostgreSQL (§11).
- **Recursive dispatch with no wave limit** — a mutual cycle spins holding a transaction and its
  locks.
- **Lease without a fencing token** — the C defect: a stale worker overwrites a live worker's
  result. Rejected.
- **Refunding the attempt on lease expiry** — lets a crash-loop retry forever (§16).
- **Deleting failed messages on a timer** — destroys evidence (§23).
- **A message broker / `LISTEN`-`NOTIFY`** — rejected for scale; 15 s polling is within
  requirements.

## Consequences

**Positive:** the event lifecycle is fully specified — buffer, acknowledgement, causation,
ordering and failure; two distinct interfaces make a category error a compile-time question;
stale workers cannot corrupt state; a crash on the final attempt terminates; failures are
triageable without being erasable; a failed PDF never restarts the container.

**Negative:**
- Multiple `SaveChanges` per command means more round trips and a longer transaction. Acceptable
  at this scale; the alternative is incorrect.
- `DrainPending` being `internal` means tests that want to assert on pending events must go
  through the platform assembly (`InternalsVisibleTo`), which is a small friction accepted in
  exchange for the buffer having exactly one legitimate consumer.
- Every outbox write now carries a token predicate; a developer who writes an unfenced `UPDATE`
  reintroduces the stale-worker bug. Mitigated by routing all transitions through one repository
  method and an architecture test banning direct `outbox_message` updates elsewhere.
- The attempt-history table grows with retries. Bounded by retention (§23).

## Compliance checks

Full Given/When/Then contracts in [ROADMAP S1](../ROADMAP.md#s1-acceptance-test-contracts-mandatory).
Summary: `EventSingleDispatchPerWave`, `EventCreatedDuringHandlerGoesNextWave`,
`HandlerFailureRollsBackAcknowledgement`, `PendingWriteWithoutEventStillSaved`,
`EventCausationChainPreserved`, `WaveLimitExceededRollsBack`,
`IdempotentApprovalNeverAborts25P02`, `StaleLeaseOwnerCannotComplete`,
`StaleLeaseOwnerCannotHeartbeat`, `LastAttemptCrashBecomesFailed`,
`FailureDismissalPreservesHistory`, `UnresolvedFailedNotPurged`,
`ManualRequeuePreservesIdempotency`, `OutboxClaimSkipsLocked`,
`DuplicateDeliveryProducesOneDocument`, `OutboxRowInRolledBackTransactionNeverDispatched`,
`RetryableFailureOnFinalAttemptBecomesFailed`, `ExhaustedMessageCannotBeClaimed`,
`RequeuedSuccessfulMessageEndsProcessedWithoutDisposition`, `DismissedFailureIsNotClaimable`,
`FutureAvailableMessageDoesNotCauseStall`, `EligibleOldMessageCanCauseStall`,
plus architecture tests on handler dependencies and on direct `outbox_message` updates.
