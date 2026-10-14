# ADR-0027 — S8C.2 Listing-Sync `requiredAction` Contract

- **Status:** Accepted (pending external review)
- **Date:** 2026-10-04
- **Sprint:** S8C.2 (Stream C1)
- **Builds on:** [ADR-0024](ADR-0024-s8c1-marketplace-connector-authorization-foundation.md) (RequiredAction
  vocabulary and precedence, not amended), [ADR-0025](ADR-0025-s8c2-mercado-livre-listing-read-integration.md)
  (§G.2, §G.4, §J, not amended), [ADR-0026](ADR-0026-s8c2-listing-sync-authority-continuation.md)
- **Decision source:** VERCE3D-S8C2-OPUS-C1-REQUIRED-ACTION-028, answering the Stream C1 stop
  `ARCHITECTURE_CLARIFICATION_REQUIRED_S8C2_C1_REQUIRED_ACTION`

## Context

ADR-0025 §J puts `requiredAction` in two listing-sync places:

- `availability` on `GET /marketplace-accounts/{id}/listing-sync`;
- the run DTO, used both by `GET /marketplace-listing-syncs/{runId}` and by the runs embedded in the
  §E.8 summary.

ADR-0025 does not define a mapping for either. What the frozen documents do say:

- §G.4: a provider 403 leads to `PROVIDER_ACCESS_FORBIDDEN` and `CHECK_PROVIDER_ACCOUNT` "on the sync
  status, never reauthorization".
- Authorization RequiredAction (on the connection) and listing-sync required action (on the sync
  status) are "separate fields and surfaces".
- ADR-0024 defines a closed connection vocabulary (`NONE`, `AUTHORIZE`, `REAUTHORIZE`,
  `REAUTHORIZE_AFTER_STORE_LOSS`, `RECONNECT_EXISTING_ACCOUNT`, `CONTACT_ADMIN`,
  `CHECK_PROVIDER_ACCOUNT`) and a precedence over it. Of note: CONTACT_ADMIN covers a safe
  configuration problem or a transient store outage; CHECK_PROVIDER_ACCOUNT covers a grant denial or
  a runtime failure.

Repository facts that constrain the answer:

- Runtime `UNAVAILABLE` never clears on its own. Only an Owner probe restores it (ADR-0024 §5).
- A listing-sync run that detects a rejected seller credential (`PROVIDER_CREDENTIAL_REJECTED`) or a
  confirmed credential loss (`CREDENTIAL_CONFIRMED_LOST`) stops the run but does not change the
  connection. The connection learns about it only through a probe or housekeeping. So for a while,
  the run is the only evidence.
- A PARTIAL run's `safe_result_code` is `LISTING_SYNC_PARTIAL`. If it stopped early, the stop code
  exists only as the `safe_error_code` of its `TRANSIENT_ERROR` items (ADR-0025 §E.3/§E.6).

## Decision

### 1. Meaning of `requiredAction`

`requiredAction` is **the explicit action a person must take outside listing sync itself, now, to let
listing sync make progress.** That person may act in VERCE account administration, in the seller's
provider account, or with the VERCE administrator. Rules that follow from this:

- It describes current state, never history. It must not tell anyone to do something that current
  state shows is no longer needed.
- It never means "wait", "retry" or "start a new sync". Waiting is conveyed by `blockedReason` and
  `retryAfterUntil`. Retrying failed items is the separate retry affordance (`retryable` items).
- It does not restate the status. Three fields stay separate:
  - `blockedReason`: why starting a sync is blocked now;
  - `safeResultCode`: what a run's outcome was;
  - `requiredAction`: what explicit action is needed, if any.

The two surfaces have distinct sources:

- **`availability.requiredAction` (surface A)** answers "what must happen before a sync can start
  now?". It derives only from the current admission decision (the §G.2 evaluator) and the current
  connection.
- **`run.requiredAction` (surface B)** carries only evidence that this run alone observed and that
  availability cannot see. It applies only while that evidence is still the latest and current state
  has not overtaken it. Conditions that admission re-evaluates are reported only on surface A, so
  surface B never duplicates or contradicts them.

### 2. Vocabulary

Exactly the seven ADR-0024 values. No listing-sync extension: no `WAIT`, `RETRY`, `ACTIVATE_ACCOUNT`
or `PROBE_PROVIDER`. Account inactivation, channel problems and an unknown grant are already
expressed precisely by `blockedReason`. The UI draws those affordances (activation, the Owner probe)
from `blockedReason`, so a second machine code would encode the same fact twice.

### 3. Surface A — `availability.requiredAction`

The admission evaluator returns at most one `blockedReason`, in the frozen §G.2 order, so surface A
needs no further precedence.

| blockedReason | requiredAction | Actor | Rationale |
|---|---|---|---|
| *(none — canStart)* | `NONE` | — | Nothing blocks. |
| `ACCOUNT_INACTIVE` | `NONE` | Owner (account admin) | Activation is an account toggle fully described by `blockedReason`; no repair action exists in the vocabulary. |
| `CHANNEL_INVALID` | `NONE` | Owner (pricing/channel config) | A business-data correction described by `blockedReason`; not provider- or credential-related. |
| `PROVIDER_NOT_CONFIGURED` | `CONTACT_ADMIN` | VERCE administrator | No listing reader is registered in this environment; deployment configuration (ADR-0024 "safe configuration"). |
| `CAPABILITY_UNSUPPORTED` | `NONE` | nobody (product scope) | Structural capability is fixed by the release; no runtime action helps. |
| `GRANT_UNKNOWN` | `NONE` | Owner (probe) | Not a failure, only missing evidence. `blockedReason` drives the probe affordance. ADR-0024 maps CHECK_PROVIDER_ACCOUNT to grant *denial*, not grant unknown. |
| `GRANT_DENIED` | `CHECK_PROVIDER_ACCOUNT` | person with seller provider access | ADR-0024: grant denial → CHECK_PROVIDER_ACCOUNT. Corrected at the provider, then confirmed by probe. |
| `AUTHORIZATION_NOT_USABLE` | **current connection RequiredAction** (§5) | Owner for REAUTHORIZE / AUTHORIZE / RECONNECT_EXISTING_ACCOUNT / REAUTHORIZE_AFTER_STORE_LOSS; VERCE admin for CONTACT_ADMIN | One authority for authorization guidance. A pending credential operation or provider callback fence on a CONNECTED account yields `NONE` (§5). |
| `RATE_LIMITED` | `NONE` | — | Expires on its own at `retryAfterUntil`. |
| `RUNTIME_UNAVAILABLE` | `CHECK_PROVIDER_ACCOUNT` | Owner (probe) / person with provider access | Not self-healing: only an explicit probe restores AVAILABLE (ADR-0024 §5), and ADR-0024 maps runtime failure to CHECK_PROVIDER_ACCOUNT. Same value as the connection surface for the same state. |
| `SYNC_ACTIVE` | `NONE` | — | Another run is in progress (`activeRunId`). |

### 4. Surface B — `run.requiredAction`

`RunStopCode(R)`:

- **FAILED:** `R.safeResultCode`.
- **PARTIAL:** the `safe_error_code` of R's `TRANSIENT_ERROR` items that belongs to the run-stop
  catalog (the codes listed in the table below other than `LISTING_SYNC_*`). By construction the
  finish transaction writes at most one such code. If more than one is ever present, the §6
  precedence picks.
- **Otherwise:** none.

R is **superseded** when a later run of the same account (strictly greater `requested_at`) has reached
SUCCEEDED, PARTIAL or FAILED.

| Run condition / code | requiredAction | Depends on current state? | Rationale |
|---|---|---|---|
| QUEUED, RUNNING | `NONE` | no | Work in progress. |
| SUCCEEDED (`LISTING_SYNC_COMPLETED`) | `NONE` | no | Nothing to do. |
| PARTIAL with no stop code (`LISTING_SYNC_PARTIAL`) | `NONE` | no | Item-level outcomes; the retry affordance covers them. |
| FAILED `LISTING_SYNC_NO_SUCCESSFUL_ITEMS` | `NONE` | no | Item-level outcomes only. |
| `LISTING_ENUMERATION_INCOMPLETE` | `NONE` | no | Provider scan incomplete; a new sync is the ordinary remedy. |
| `RUN_EXECUTION_ATTEMPTS_EXHAUSTED` | `NONE` | no | Execution interrupted; a new sync is the ordinary remedy. |
| `PROVIDER_UNAVAILABLE`, `PROVIDER_RATE_LIMITED` | `NONE` | no | Transient. The resulting runtime UNAVAILABLE / Retry-After appears on surface A. |
| Guard stops: `ACCOUNT_INACTIVE`, `CHANNEL_INVALID`, `PROVIDER_NOT_CONFIGURED`, `MARKETPLACE_PROVIDER_NOT_SUPPORTED`, `CAPABILITY_UNSUPPORTED`, `GRANT_UNKNOWN`, `GRANT_DENIED`, `AUTHORIZATION_NOT_USABLE`, `RUNTIME_UNAVAILABLE`, `RATE_LIMITED` | `NONE` | — (reported on surface A) | Admission re-evaluates these, so surface A carries the current action. Repeating it here would duplicate it, or go stale. |
| `PROVIDER_ACCESS_FORBIDDEN` | `CHECK_PROVIDER_ACCOUNT` unless superseded, then `NONE` | yes (supersession) | ADR-0025 §G.4 anchor. Provider-side evidence that only a run can observe; a later finished run replaces it. |
| `PROVIDER_CREDENTIAL_REJECTED` | credential rule (§4.1), base `REAUTHORIZE` | yes | The run rejected the token even after the forced refresh (§G.3). Re-consent is the remedy (ADR-0025 §F.3 maps the code to REAUTHORIZE). |
| `CREDENTIAL_CONFIRMED_LOST` | credential rule (§4.1), base `REAUTHORIZE_AFTER_STORE_LOSS` | yes | Confirmed store loss (NOT_FOUND/CORRUPTED), the distinction ADR-0024 makes. |
| `CREDENTIAL_STORE_UNAVAILABLE` | `CONTACT_ADMIN` unless superseded, then `NONE` | yes (supersession) | A transient protected-store outage, not a confirmed loss. ADR-0024 maps it to CONTACT_ADMIN; it never maps to a reauthorization action. |
| `LISTING_VOLUME_EXCEEDS_LIMIT` | `CONTACT_ADMIN` unless superseded, then `NONE` | yes (supersession) | The 200 000 ceiling is a VERCE limit; only an administrator or product change can address it. |
| `CAPABILITY_CODE_REQUIRED` | `CONTACT_ADMIN` unless superseded, then `NONE` | yes (supersession) | Internal programming/configuration defect. |

**4.1 Credential rule** (`PROVIDER_CREDENTIAL_REJECTED`, `CREDENTIAL_CONFIRMED_LOST`):

1. If the current connection RequiredAction (§5) is not `NONE`, return it. The connection's own,
   more current guidance wins, for example `CONTACT_ADMIN` for invalid application credentials.
2. Otherwise, if the account has re-consented since the run, return `NONE`. Re-consent means: the
   connection's current confirmed operation is a `CONNECT_NEW` or `RECONNECT` with
   `decided_at > R.finishedAt`. A later `REFRESH` is not re-consent (ADR-0026: same authorization).
3. Otherwise, if R is superseded, return `NONE`.
4. Otherwise, return the base action.

**`PROVIDER_APP_CREDENTIALS_INVALID`** is never a run code. A refresh that fails with it puts the
connection into `REAUTHORIZATION_REQUIRED`, and the connection rule (§5) already maps that to
`CONTACT_ADMIN`. It therefore appears on surface A (through `AUTHORIZATION_NOT_USABLE`) and through
step 1 of §4.1. A seller is never told to reauthorize because of VERCE's own application credentials.

### 5. Connection RequiredAction (reused, not redefined)

This is the existing ADR-0024 projection already implemented as `CommerceEndpoints.RequiredAction`.
Its inputs are the connection's authorization state, runtime availability and safe failure code, plus
`authorizationInProgress` (a PENDING or CLAIMED reauthorization session for the account). In order:

1. `authorizationInProgress` → `NONE`.
2. NOT_CONNECTED or REVOKED → `AUTHORIZE`.
3. REAUTHORIZATION_REQUIRED → by safe failure code:
   - `PROVIDER_APP_CREDENTIALS_INVALID` → `CONTACT_ADMIN`;
   - `RECONNECT_EXISTING_ACCOUNT` or `ACCOUNT_ALREADY_EXISTS_RECONNECT_REQUIRED` →
     `RECONNECT_EXISTING_ACCOUNT`;
   - `PROBE_CREDENTIAL_UNREADABLE` or `OPERATION_RECOVERY_FAIL_CLOSED` → `REAUTHORIZE_AFTER_STORE_LOSS`;
   - anything else → `REAUTHORIZE`.
4. CONNECTED with runtime UNAVAILABLE → `CHECK_PROVIDER_ACCOUNT`.
5. Otherwise `NONE`.

Listing sync must call the same function, never a copy.

A CONNECTED account that is blocked only by a pending credential operation, or by the provider-wide
callback fence (ADR-0026 §3, a temporary operation-safety interlock), therefore yields `NONE`. It never
yields reauthorization guidance.

### 6. Precedence

Whenever more than one action candidate exists (several PARTIAL stop codes, or a defensive merge), the
highest one in this order wins. It is the ADR-0024 order:

1. `CONTACT_ADMIN`
2. `REAUTHORIZE_AFTER_STORE_LOSS`
3. `RECONNECT_EXISTING_ACCOUNT`
4. `REAUTHORIZE`
5. `AUTHORIZE`
6. `CHECK_PROVIDER_ACCOUNT`
7. `NONE`

### 7. Derivation (Stream C, read-only)

The inputs are existing state only: no provider I/O, no secret access, no new persistence.

```text
DeriveAvailabilityRequiredAction(status = MarketplaceListingSyncService.GetStatusAsync(accountId),
                                 connection, authorizationInProgress):
  reason = status.Availability.BlockedReason
  if reason is null                      -> NONE
  switch reason:
    PROVIDER_NOT_CONFIGURED              -> CONTACT_ADMIN
    GRANT_DENIED, RUNTIME_UNAVAILABLE    -> CHECK_PROVIDER_ACCOUNT
    AUTHORIZATION_NOT_USABLE             -> ConnectionRequiredAction(connection, authorizationInProgress)
    ACCOUNT_INACTIVE, CHANNEL_INVALID, CAPABILITY_UNSUPPORTED,
    GRANT_UNKNOWN, RATE_LIMITED, SYNC_ACTIVE -> NONE
    otherwise                            -> UnknownCode(reason)        // §8

DeriveRunRequiredAction(run = GetRunAsync(runId).Run, connection, authorizationInProgress,
                        currentConfirmedOperation{Kind, DecidedAt}, supersededQuery, stopCodeQuery):
  if run.Status in {QUEUED, RUNNING, SUCCEEDED} -> NONE
  stops = run.Status == FAILED ? {run.SafeResultCode}
        : stopCodeQuery(run.RunId)  // DISTINCT safe_error_code of TRANSIENT_ERROR items ∩ RunStopCatalog
  candidates = stops.Select(code => ActionFor(code, run, ...))
  return HighestByPrecedence(candidates)  // empty -> NONE

  ActionFor(code):
    LISTING_SYNC_NO_SUCCESSFUL_ITEMS, LISTING_ENUMERATION_INCOMPLETE, RUN_EXECUTION_ATTEMPTS_EXHAUSTED,
    PROVIDER_UNAVAILABLE, PROVIDER_RATE_LIMITED, <all guard stops in §4 table> -> NONE
    PROVIDER_ACCESS_FORBIDDEN            -> Superseded(run) ? NONE : CHECK_PROVIDER_ACCOUNT
    CREDENTIAL_STORE_UNAVAILABLE, LISTING_VOLUME_EXCEEDS_LIMIT,
    CAPABILITY_CODE_REQUIRED             -> Superseded(run) ? NONE : CONTACT_ADMIN
    PROVIDER_CREDENTIAL_REJECTED         -> CredentialRule(run, REAUTHORIZE)
    CREDENTIAL_CONFIRMED_LOST            -> CredentialRule(run, REAUTHORIZE_AFTER_STORE_LOSS)
    otherwise                            -> UnknownCode(code)          // §8

  CredentialRule(run, base):
    c = ConnectionRequiredAction(connection, authorizationInProgress); if c != NONE -> c
    if currentConfirmedOperation.Kind in {CONNECT_NEW, RECONNECT}
       and currentConfirmedOperation.DecidedAt > run.FinishedAt -> NONE
    if Superseded(run) -> NONE
    -> base

  Superseded(run): EXISTS run2 of run.AccountId with run2.RequestedAt > run.RequestedAt
                   and run2.Status in {SUCCEEDED, PARTIAL, FAILED}
```

Stream C reads the connection, `authorizationInProgress`, the current confirmed operation's kind and
`decided_at`, later runs and item stop codes directly with read-only EF queries, as existing
`CommerceEndpoints` reads already do. Internal identifiers (operation IDs, run snapshots) are inputs
only and never serialized. Each run embedded in the status summary
(`activeRun`, `latestRun`, `latestFullRun`) uses `DeriveRunRequiredAction`.

### 8. Unknown codes

The mapping tables are explicit and exhaustive for every code listed in this ADR. A code that is not
listed maps to `NONE` and logs one Warning with the safe code only (`requiredAction unmapped code
{Code}`). Clients never see an exception. Tests pin the full catalog, so a new code cannot ship without
a deliberate entry here and in the mapping.

### 9. Actors (UI guidance only; API permissions unchanged)

| requiredAction | Who can act | Viewer / Operator display |
|---|---|---|
| `AUTHORIZE`, `REAUTHORIZE`, `REAUTHORIZE_AFTER_STORE_LOSS`, `RECONNECT_EXISTING_ACCOUNT` | Owner (`commerce:accounts:manage`) | informational, no button |
| `CHECK_PROVIDER_ACCOUNT` | someone with access to the seller's provider account; the Owner probe confirms recovery | informational |
| `CONTACT_ADMIN` | VERCE administrator (escalation, not an in-app operation) | informational for everyone |
| `NONE` | — | — |

## Supersedes / clarifies

This ADR fills the omission in ADR-0025 §J, which named `requiredAction` without defining it. It
supersedes nothing. ADR-0024's vocabulary, precedence and connection RequiredAction are reused
unchanged. ADR-0025's HTTP mapping (§G.2/§J), `blockedReason` catalog and §G.4 anchor are unchanged.
No Stream B change is required.

## Alternatives considered

1. **Project the connection RequiredAction everywhere.** Rejected: it cannot see provider 403, a
   transient store outage or the volume limit (§G.4 requires CHECK_PROVIDER_ACCOUNT on the sync
   status), and it would merge the two surfaces ADR-0025 keeps separate.
2. **Treat run `requiredAction` as a historical snapshot.** Rejected: it would tell a user to
   reauthorize after they already have (scenario Q), contradicting the "current, actionable" meaning.
3. **New values (`WAIT`, `RETRY`, `ACTIVATE_ACCOUNT`, `PROBE_PROVIDER`).** Rejected: `blockedReason`,
   `retryAfterUntil` and the retry affordance already express them, and no machine workflow consumes
   them.
4. **Unknown code → silent `NONE`.** Rejected in favour of explicit catalog tests plus a diagnostic.
5. **Add a stop-code field to the Stream B projection.** Not needed: the PARTIAL stop code is
   derivable read-only in Stream C.

## Consequences

- Each surface states one truthful, current, non-destructive action.
- Authorization guidance has a single source.
- Fences and transient conditions never trigger reauthorization or repair prompts.
- Accepted cost: provider-side evidence (403, store outage, volume) remains until a later run
  finishes. That is the latest knowledge available without provider polling.

## Compliance checks

Stream C1 tests (exact expectations are in the C1 implementation contract):

- table-driven tests over every row of §3 and §4, including every PARTIAL stop code;
- §G.4 anchor;
- credential rule steps 1–4;
- durable-fence → `NONE`;
- RUNTIME_UNAVAILABLE → CHECK_PROVIDER_ACCOUNT and RATE_LIMITED → NONE;
- store outage vs confirmed loss;
- application credentials → CONTACT_ADMIN;
- supersession;
- precedence;
- unknown code → NONE plus diagnostic.

Changing a row requires a superseding ADR.
