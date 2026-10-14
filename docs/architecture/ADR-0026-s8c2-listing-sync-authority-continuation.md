# ADR-0026 — S8C.2 Listing-Sync Authority Continuation and Item-Derived Terminal Outcome

- **Status:** Accepted (pending external review)
- **Date:** 2026-10-03
- **Sprint:** S8C.2 (Stream B2)
- **Builds on:** [ADR-0024](ADR-0024-s8c1-marketplace-connector-authorization-foundation.md) (not
  amended), [ADR-0025](ADR-0025-s8c2-mercado-livre-listing-read-integration.md) (narrowly clarified,
  see "Supersedes / clarifies")
- **Decision source:** architecture adjudication VERCE3D-S8C2-OPUS-AUTHORITY-ADJUDICATION-015
  (C-1), implemented by VERCE3D-S8C2-SONNET-AUTHORITY-FIX-016

## Context

ADR-0025 §E.6 says the run's runtime update is written "only if `ConfirmedOperationId` still equals
`confirmed_operation_id_at_start`". Read literally, this is exact operation-ID equality. But the same
ADR requires runs to survive their own credential refresh: §A.7 (forced refresh after a 401), §A.8
(proactive refresh inside the margin) and §G.3 (the credential executor refreshes and retries inside a
run's provider calls). Each S8C.1 refresh confirms a **new** operation (ADR-0024 §2), so a run that
refreshes can never satisfy literal equality. §G.4/§E.3 also require a rate-limit stop to persist
Retry-After and a provider failure to mark runtime UNAVAILABLE, and literal equality would silently
drop both after any in-run refresh.

ADR-0025 also never spells out the authority check applied when a provider-derived effect is
persisted (DATA-MODEL only calls the snapshot column a "late-response guard"). It does not say
whether an authority change that lands after the last effect but before terminalization may change
the run's terminal outcome.

ADR-0024 supplies the facts this decision relies on:

- Only an operation CONFIRMED atomically with the connection pointer is authority, and normal reads
  require the current connection plus that operation's CONFIRMED decision (§2).
- Confirmation writes the exact credential reference/version into both the operation and the
  connection (§2/§3).
- Refresh keeps the reference, advances the version from the previous confirmed version and
  preserves grants, so it is the same consent. Reconnect is a new consent: runtime is reset and
  grants are observed again (§3–§5).

## Decision

### 1. One shared authority predicate

Let **O1** = `marketplace_listing_sync_run.confirmed_operation_id_at_start` and **C** =
`marketplace_account_connection.confirmed_operation_id`. Authority is valid only if **all** of the
following hold.

**Base requirements (both branches):**

- O1 is non-null;
- the connection's `authorization_state` is `CONNECTED`;
- C is non-null and the connection's `confirmed_credential_version` is non-null;
- C belongs to the run's MarketplaceAccount and its decision is `CONFIRMED`;
- C's candidate credential reference and version are non-null;
- C's candidate reference equals `marketplace_account.credential_reference`, and its candidate
  version equals the connection's `confirmed_credential_version`.

**Then exactly one branch:**

- **Identity:** `C == O1`. Operation-ID equality alone is never sufficient; the base checks still
  apply.
- **Direct refresh continuation:** `C != O1`, and all of the following:
  - C's kind is `REFRESH`;
  - O1 exists, belongs to the same account and is `CONFIRMED`, with a non-null candidate reference
    and version;
  - C's `previous_credential_reference` equals O1's candidate reference;
  - C's `previous_confirmed_credential_version` equals O1's candidate version.

No other continuation is valid. An unrelated replacement, reconnect or any non-`REFRESH` C fails the
predicate.

### 2. Single hop only

For one run snapshot O1, only `O1` or `O1 → direct REFRESH O2` satisfies the predicate.
`O1 → O2 → O3` does **not** satisfy it relative to O1. The predicate never scans operation history to
reconstruct a chain: operations carry no predecessor-operation pointer, and failing closed is safe. A
new claim or reclaim captures the then-current operation under ADR-0025 §E.2.

### 3. The connection pointer is the authority root

The predicate reads only the connection row, operations C and O1, and the account row. Other
operation rows are **not** authority inputs. A PENDING operation, one whose candidate secret is
persisted but not authoritative, a FAIL_CLOSED operation, or a CONFIRMED operation the connection
does not reference neither replaces nor invalidates O1 merely by existing.

This governs **persistence authority** only. **Provider-call eligibility** is a separate guard and
is unchanged: the credential executor's existing ADR-0024 §5 / ADR-0025 §G.2–§G.3 checks (no
unresolved account operation, CONNECTED, runtime, grants, Retry-After) still block **new** provider
calls.

### 4. Null snapshot

`confirmed_operation_id_at_start = NULL` grants **no** provider-derived persistence authority. The
execution never substitutes the connection's current authority for a missing run snapshot.

### 5. Effect-time authority

Every provider-derived persistence transaction evaluates the predicate in the **same** PostgreSQL
transaction that writes the effect. This covers run-item registration from enumeration, detail
results, listing and observation writes, linkage decisions, and outcomes derived from provider
responses.

Lock order:

1. the lease guard on the run row (ADR-0025 §E.2), as the first statement;
2. `marketplace_account_connection … FOR UPDATE`;
3. plain reads of C, O1 and the account.

Every confirmation, revocation or reauthorization updates the connection row, so holding that lock
freezes the authority inputs until commit. No connection lock spans provider I/O, more than one
transaction, or the whole run.

A failing predicate rolls back that transaction, and the run stops through the existing
`AUTHORIZATION_NOT_USABLE` path. Effects committed earlier under valid authority stay valid. They are
not rolled back or reclassified because a later operation becomes current, since ADR-0025 §E.4 says
terminal item outcomes never change.

### 6. Terminal outcome is item-derived (Model B3)

The terminal outcome and `safe_result_code` are computed by ADR-0025 §E.6 from durable run items
(plus the stop reason, if any). **Authorization state at finish is not an input.** If every required
item is FOUND, the run is `SUCCEEDED` / `LISTING_SYNC_COMPLETED`. This holds even when another
authorization becomes current after the final legitimate effect and before terminalization. This is
intentional: the outcome then depends only on the commit order the connection lock establishes, not
on scheduler timing.

If the authority change lands **before** a later provider-derived write, that write fails the
predicate (§5). The run stops with `AUTHORIZATION_NOT_USABLE`, and finish turns the remaining PENDING
items into `TRANSIENT_ERROR/AUTHORIZATION_NOT_USABLE`. The outcome can then legitimately be
`PARTIAL` (≥ 1 FOUND) or `FAILED`, because the items themselves carry blocking outcomes.

### 7. Runtime write at finish is authority-gated

Finish is one lease-fenced transaction. The run's runtime effect (`MarkAvailable`, or
`UNAVAILABLE`/Retry-After per ADR-0025 §E.6/§G.4) is applied **only if the shared predicate holds in
that transaction**. Otherwise the runtime write is skipped silently, and the outcome is unchanged.

A skipped write never overwrites newer authorization state, runtime, failure classification, safe
failure code or Retry-After. ADR-0025 §E.6's rule that guard and credential stops do not touch
runtime is unchanged.

### 8. Finish failure

If the finish transaction itself fails, a lost lease propagates under the existing lease semantics,
and any other exception propagates too. Neither is converted into a provider stop reason, and neither
is followed by a second finish attempt. The run stays `RUNNING` until the existing reclaim or
attempt-exhaustion rules (ADR-0025 §E.2) resolve it. No new state is introduced.

### 9. Canonical cases

| Case | Run outcome | Runtime written by the run |
|---|---|---|
| All items FOUND under O1, then an unrelated RECONNECT O2 becomes current before finish | `SUCCEEDED` / `LISTING_SYNC_COMPLETED`, run failure classification null, nothing to retry | **No**: O2's connection and runtime are untouched |
| All items FOUND under O1, then O1 is set to REAUTHORIZATION_REQUIRED or REVOKED before finish | `SUCCEEDED` / `LISTING_SYNC_COMPLETED` | **No**: the reauthorization `safe_failure_code` is preserved |
| All items FOUND, then a valid direct REFRESH O1 → O2 becomes current before finish | `SUCCEEDED` / `LISTING_SYNC_COMPLETED` | **Yes**, under existing runtime semantics (e.g. AVAILABLE with failure and Retry-After fields cleared) |
| All items FOUND, then O1 → O2 → O3 (two valid refreshes) before finish | `SUCCEEDED` / `LISTING_SYNC_COMPLETED` | **No**: O3's connection and runtime are untouched |
| An unrelated O2 becomes current before a later provider-derived write | items-derived (`PARTIAL` or `FAILED`), blocked items `TRANSIENT_ERROR/AUTHORIZATION_NOT_USABLE` (retryable by FAILED_ONLY) | No |
| Run snapshot is null | no provider-derived effect commits; `AUTHORIZATION_NOT_USABLE` stop | No |

## Supersedes / clarifies

**Clarifies and narrowly supersedes** one sentence of ADR-0025 §E.6: "only if `ConfirmedOperationId`
still equals `confirmed_operation_id_at_start`". For S8C.2 listing-sync authority decisions (the
run's runtime write and provider-derived persistence), "still equals" now means **same
authorization**: the identity branch or exactly one direct, fully proven REFRESH continuation, under
the complete predicate in §1. The handoff's "runtime update guarded by
`confirmed_operation_id_at_start`" (S8C.2 handoff B5) is read the same way.

It also fills ADR-0025's unstated persistence-time authority check (§5 here) and confirms that the
§E.6 outcome is item-derived and independent of authorization state at finish.

Nothing else in ADR-0025 changes. Listing semantics, SKU mappings, the target ceiling,
reconciliation batches, observations, lease fencing, provider scope, the retry model, the outcome
formula itself, the H.1 schema and live validation all stay as they are.

**ADR-0024 is not superseded or amended.** It remains authoritative for the operation arbiter,
candidate-secret non-executability, connection-confirmed authority, credential reference/version,
refresh lineage and confirmation guards, reauthorization/revocation and runtime truth. This ADR only
states how S8C.2 listing-sync runs consume that authority. It does not weaken operation identity
anywhere else.

## Alternatives considered

1. **Literal operation-ID equality (keep §E.6 as worded).** Rejected: a run's own §A.7/§A.8/§G.3
   refresh would forbid its runtime update, so a rate limit after an in-run refresh would never
   persist Retry-After (contradicting §G.4). If applied to persistence, every write after an in-run
   refresh would also fail.
2. **Authority as an input to the terminal outcome (Model B2).** Rejected: when every item is FOUND
   this yields `FAILED`, or `PARTIAL` with zero blocking items, which contradicts §E.6's own
   definitions and FAILED_ONLY retry (nothing to retry). The outcome would also depend on whether
   the authority change landed just before or just after finish.
3. **Claim-time authority only (Model B1).** Rejected: provider-derived effects could commit after
   an unrelated replacement. This is exactly the stale effect the late-response guard exists to
   stop.
4. **Transitive refresh chains (O1 → … → On).** Rejected for S8C.2: proving intermediate links
   needs history discovery the operation model gives no pointer for. Failing closed is safe; a
   reclaim or FAILED_ONLY retry recovers.
5. **Treat any newer operation row (pending, failed, unreferenced confirmed) as invalidating O1.**
   Rejected: it is not authority under ADR-0024 §2, and it would need an ambiguous scan of operation
   history. New provider calls are already blocked by the executor guard.
6. **Hold the connection lock for the whole run.** Rejected: a row lock across provider I/O would
   block authorization changes (including the run's own refresh) for the run's duration.

## Consequences

Positive:

- Runs survive their own refresh.
- Stale or unrelated authority can never commit provider-derived effects or overwrite newer runtime.
- The terminal outcome is deterministic and always matches the items.
- A run cannot write runtime over a REAUTHORIZATION_REQUIRED or REVOKED connection, or over an
  incoherent connection state.

Negative / accepted:

- A run that crosses two refreshes stops with `AUTHORIZATION_NOT_USABLE` for its remaining items,
  and needs a FAILED_ONLY retry or reclaim.
- A run whose effects all committed before an unrelated reconnect reports `SUCCEEDED` while
  leaving runtime to the new authorization.
- The final runtime value after a valid refresh depends on whether the refresh confirmation or the
  run's finish commits last. Both orders produce a correct last observation.

## Compliance checks

PostgreSQL integration tests in
`tests/Verce.IntegrationTests/S8C2/MarketplaceListingSyncExecutionIntegrationTests.cs`:

- **Direct refresh lineage:** T-F03-A..F (malformed lineage fails closed); the valid refresh-lineage
  controls.
- **Identity-branch coherence:** T-F03-G-VERSION and T-F03-G-REFERENCE; T-F03-H for
  REAUTHORIZATION_REQUIRED with the failure code preserved.
- **G1 controls:** CTRL-O2 (a pending, persisted-pending, FAIL_CLOSED or unreferenced CONFIRMED O2
  does not invalidate O1).
- **Finish-time races:** RACE-1 (unrelated reconnect after the final effect), RACE-1c
  (REAUTHORIZATION_REQUIRED / REVOKED), RACE-2 (valid direct refresh), RACE-2b (second hop not
  authorized).
- **Null snapshot and finish failure:** the null-snapshot proof; the finish-failure proof (single
  finish attempt, no provider-stop reclassification).
- **Pre-existing proofs:** the B2-19 connection-row-lock proof; operation replacement and stale
  authority tests.

Review: any change to the predicate's inputs, to the hop count or to the outcome's inputs requires a
superseding ADR.
