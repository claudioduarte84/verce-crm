# ADR-0024 — S8C.1 Marketplace Connector and Authorization Foundation

- **Status:** Accepted — approved and frozen; implemented in `01e5d6a` (S8C.1 COMPLETE).
- **Date:** 2026-09-24
- **Sprint:** S8C.1.ARCH
- **Correction:** consolidated G-01 through G-08 and final recovery RT-01 through RT-03;
  supersedes this candidate's earlier state, callback and fee-extension proposals.
  Five implementation passes exist (2026-09-25); the delivered S8C.1 implementation was
  independently certified against this ADR and is COMPLETE (see the S8C.1 entry in
  `docs/ROADMAP.md`). Every normative section below remains binding.

## Context and delivery boundary

S8B is shipped on baseline `143b562336cf01f51bc0472cf33cceee95262167`. Commerce owns durable
provider/account identities and observations; Pricing owns fees and Sales owns canonical Sale.
S8C.0 evidence is frozen: MLB's eight structural capabilities are SUPPORTED, Shopee BR's eight
are UNKNOWN, and TikTok Shop BR has only ORDERS_READ SUPPORTED. Structural evidence confers
neither account consent nor current availability. No seeds are promoted by this decision.

S8C.1 delivers a vertical foundation exercised through a test-host fake: authorization,
identity, protected credentials, grants, status, probe, refresh and disconnect. Mandatory work
is defined below and in [the handoff](../S8C1-IMPLEMENTATION-HANDOFF.md). No real provider OAuth,
HTTP adapter, listing/order ingestion, Sale creation, live fee, publication, inventory, shipping,
analytics, ads, webhook or provider polling is included. Optional deliverables: none.

## 1. Boundaries, registry and ports

One `Verce.Infrastructure.Marketplaces` project implements Commerce application ports and uses
Platform facilities. Api references both and composes them. Commerce must not reference that
project, provider SDKs, provider HTTP DTOs or OAuth wire models. Platform must not reference
Commerce. The new infrastructure assembly is not a sixteenth business module in
`ModuleAssemblyCatalog`; architecture tests must inspect it explicitly for dependency rules.
A later provider assembly split requires an actual SDK/isolation reason.

Commerce defines `IMarketplaceConnectorRegistry`, `IMarketplaceAuthorizationProvider` and
`IMarketplaceAccountInspector`. Infrastructure implements the immutable registry from injected
connector registrations, normalized exact provider codes, duplicate-registration startup failure
and typed unsupported-provider/operation results. No arbitrary `IServiceProvider` resolution.
The authorization port has Begin, CompleteOnce, RefreshOnce and optional supported-Revoke
operations. Inspection returns verified identity plus an observation for each known grant; Probe
is a safe identity/authorization inspection. Begin returns an allow-listed authorization URI
and optional protected transient-material handle. Complete/Refresh return only a staged protected
candidate handle, verified identity/grant facts and safe result metadata to Commerce, never tokens.
Any code used for CompleteOnce stays in request memory, never an entity or public response.

The adapter alone handles credential bytes within a controlled store-use callback. Inspection
uses a trusted account context or a candidate handle bound to the owning authorization session.
Registry consumers have typed resolvers for authorization and inspection only. Future listings,
orders, inventory, shipping, analytics and ads ports are conceptual boundaries: no interfaces,
request/response DTOs, stubs or dummy implementations for them are created in S8C.1.
`IChannelFeeProvider` is the existing exception and stays unchanged (section 8).

## 2. G-01 — durable session and callback ownership

Commerce owns `MarketplaceAuthorizationSession`, an independent technical workflow with UUID v7
ID and explicit optimistic `Version`. Fields are specified exactly in DATA-MODEL: provider,
requested channel, display name, initiating user, optional reconnect target and its expected
account Version, SHA-256 state hash,
status, creation/expiry/claim/finish timestamps, safe outcome code, protected transient handle
and version. No guessed external identity, raw state/code, verifier or tokens are stored.

New connection has null reconnect target. Reauthorization is bound to one existing account;
provider/channel/external identity are loaded by the server. Callback cannot select a different
account. The initiating user ID is mandatory, immutable and audited. At begin and before claim
and final commit, the user must still exist, be active and have `commerce:accounts:manage`.
HTTP callback may be anonymous; the persisted transaction supplies actor/context, not the
browser's current user. Begin/reconnect/disconnect/probe retain the existing cookie antiforgery
guard. A browser binding cookie containing an independent random nonce is also required at
callback: store only its SHA-256 hash in the session, use Secure/HttpOnly/SameSite=Lax and a
15-minute lifetime. The local E2E host uses HTTPS so this guard is exercised unchanged.
This binds state to the initiating browser without requiring an Owner cookie
on callback. Missing/mismatched binding rejects before provider exchange. Concurrent tabs use
session-specific cookie names; cookie values are never logged.

State and browser nonce each use 32 CSPRNG bytes encoded base64url; store lowercase SHA-256 hex.
Lifetime is exactly 15 minutes using IClock/UTC, with validity `now < ExpiresAt`. Session status
is `PENDING|CLAIMED|COMPLETED|FAILED|EXPIRED|REVOKED`; timestamps are constrained attributes,
not another authority. A claim is committed before external I/O by conditional UPDATE requiring
PENDING, matching provider/browser/expiry and expected Version. Exactly one callback gets the
claim; all others return a local conflict/invalid-session code without invoking the provider.
There is no lease reclaim that can repeat a code exchange. Claimed sessions are single-use forever.

Exact callback sequence:

1. Hash state; locate session; validate browser binding, provider route, pending status, expiry,
   actor and active Marketplace/non-DIRECT channel. A reconnect target must still match session
   provider/channel and be active. Reject invalid request without storing callback query values.
2. Commit the one-time CLAIMED transition and safe claim audit, then commit the single durable
   `MarketplaceAccountOperation` for CONNECT_NEW or RECONNECT before sending code. The operation
   row is the only durable marker; no independent connection marker is maintained. For reconnect,
   hold the shared account credential lock, check the captured account Version and reject a
   concurrent credential operation. The operation records the resulting root Version as its
   fencing input. Transition it to EXTERNAL_IN_FLIGHT durably before the call. While any callback
   operation remains PENDING in EXTERNAL_IN_FLIGHT or SECRET_PERSISTED, a provider-wide safety
   fence denies credential execution and other operation confirmations for that provider until
   identity/impact is resolved. The owning callback may confirm while holding its operation row;
   that same transaction resolves any affected accounts before releasing the fence. The fence is
   a query over operation rows, not another state authority.
3. Invoke CompleteOnce exactly once, with a 60-second total budget. The adapter may temporarily
   stage returned encrypted material bound only to the session/provider, before account resolution.
   Provider denial, rejection,
   temporary failure or uncertain after-send response terminates this session as FAILED. Never
   automatically replay the code, even when the adapter reports a pre-send failure. A new
   authorization session is required.
4. Inspect provider-authoritative identity and grants in adapter memory. A reconnect must match
   the target's exact immutable ProviderCode + ExternalAccountId and channel. Recheck actor,
   channel activity and expected account Version before installing credentials.
5. For a new connection allocate the future account ID and a server-generated credential
   reference; bind the encrypted STAGED candidate to session/account/provider and this reference through
   an internal store BindCandidate operation. Provisional candidates are session-only readable. A new
   session discovering an existing identity returns `ACCOUNT_ALREADY_EXISTS_RECONNECT_REQUIRED`
   and safe action `RECONNECT_EXISTING_ACCOUNT`. It never silently becomes an unbound reconnect.
   Resolve the impact on the existing account under the operation arbiter specified below.
6. Create/resolve the account identity in a DB transaction protected by the ordinary unique
   pair. Persist a candidate as described below, then commit account, connection, grants,
   session COMPLETED and audit together. For reconnect only the intended account is updated.
7. Redirect to the fixed internal `/commerce/marketplace-accounts` result view, with at most an
   opaque session ID. Owner-only session-result reads supply local outcome codes. No query
   returnUrl, provider text, code/state or credential material is returned.

Two new sessions racing the same identity have one DB winner. Serialize callback exchanges for a
provider in the single-instance coordinator. An async provider execution gate gives ordinary
credential-using operations a shared lease covering their last DB guard, credential acquisition
and provider send; the callback takes its exclusive lease before transitioning its operation to
EXTERNAL_IN_FLIGHT and holds it through terminal resolution. Acquire this provider gate before
account/reference locks, consistently, to avoid inversion. Existing sends complete before the
callback can start its exchange; no new send can pass after the callback fence is established.
The provider-wide durable safety fence blocks execution during each exchange, including the
interval before returned identity is known and across restart.
An ordinary response arriving after the lease is released may update runtime/grants only after
rechecking the same current confirmed operation/account Version; it never restores authorization
after a callback has made that account non-executable.
Database uniqueness remains final identity authority. A unique violation rolls back the whole
transaction; discard its DbContext and reload in a fresh scope. Never continue a PostgreSQL
transaction in aborted state. The losing session cannot adopt the winner's identity or credentials.
Its successful exchange may have superseded the winner's credential: under UNKNOWN or
MAY_SUPERSEDE_EXISTING impact, commit the winner's REAUTHORIZATION_REQUIRED and losing operation
FAIL_CLOSED together before releasing the fence. Under officially proven PRESERVES_EXISTING
impact, the winner may remain CONNECTED. Delete the losing candidate only after terminal
arbitration. A crash or after-send ambiguity with unknown returned identity retains the provider
fence; the live resolver or startup marks all accounts for that provider
REAUTHORIZATION_REQUIRED and fails that operation in one transaction before releasing the fence.
No provider request is replayed. A proven NOT_SENT failure releases
the fence without changing other accounts. New authorization and reconnect remain different intents.

For any successful exchange returning an identity already owned by an account other than the
explicit reconnect target, default to UNKNOWN impact: mark that existing account
REAUTHORIZATION_REQUIRED even when inactive, retain its history, fail the session/operation and
discard the candidate after terminal arbitration. If explicit RECONNECT(X) returns Y, never
rebind X to Y: X requires reauthorization after the sent exchange and any existing Y becomes
non-executable under UNKNOWN/MAY_SUPERSEDE_EXISTING. Owner must reconnect the correct account;
an inactive account must first be activated. The authorization adapter returns
`UNKNOWN|PRESERVES_EXISTING|MAY_SUPERSEDE_EXISTING` credential impact; UNKNOWN is the default.
Only separately reviewed official provider evidence can justify PRESERVES_EXISTING. This is a
provider-neutral safety assumption, not a claim about Mercado Livre behavior.

### One durable operation arbiter and cross-store compensation

`commerce.marketplace_account_operation` is the sole PostgreSQL authority for credential
operations: CONNECT_NEW, RECONNECT, REFRESH and DISCONNECT. It is created and committed before
any code or refresh-token request can be sent. Its phase is
`PREPARED|EXTERNAL_IN_FLIGHT|SECRET_PERSISTED`; its irreversible decision is
`PENDING|CONFIRMED|FAIL_CLOSED`; cleanup is separately `NOT_REQUIRED|PENDING|DONE`.
Phase records progress, not authorization. CONFIRMED and FAIL_CLOSED are terminal decisions;
cleanup may advance after either. No later transition changes one terminal decision into the
other. Only PostgreSQL operation CONFIRMED, committed atomically with the account connection
and (for callbacks) session/grants/audit, can make candidate material executable. The connection
keeps `ConfirmedOperationId` as a FK to that terminal operation; a currently referenced confirmed
operation is retained, even after ordinary operation-history retention would expire.

Every confirmer, compensator, startup resolver and Quartz worker first obtains the same operation
row using `SELECT ... FOR UPDATE` in a fresh transaction. Lock order is operation, account root,
then session. The confirmation transaction acquires this lock before store CAS and holds it
through final commit. A resolver therefore waits for any in-progress commit/rollback, then reads
the actual decision under the lock. It must never infer rollback from one unlocked negative read.
If the resolver wins and commits FAIL_CLOSED, the original completion can no longer commit
CONFIRMED. If confirmation wins, cleanup sees CONFIRMED and cannot delete its current credential.
DB unavailability leaves the operation unresolved and all affected execution denied; it never
licenses speculative compensation. Store I/O under the short confirmation transaction has a
five-second deadline; provider HTTP is outside that transaction.

CONNECT_NEW/RECONNECT confirmation is one transaction containing operation CONFIRMED, session
COMPLETED, current actor permission, verified provider identity and SalesChannel, account root
Version, credential reference/version and ConfirmedOperationId, CONNECTED/runtime UNKNOWN,
complete grant observations and safe audit. A receipt alone can never complete either callback
after restart. REFRESH confirmation
has different guards: same account/provider/current reference, operation ID, expected account
Version and previous confirmed version, exact persisted candidate version/receipt, no competing
callback fence, then advance connection confirmed credential version/ConfirmedOperationId,
CONNECTED, operation CONFIRMED and audit in one
transaction. It needs no authorization session or new grant observation. If the refresh call may
have consumed R1 but these guards cannot be proved, commit FAIL_CLOSED plus
REAUTHORIZATION_REQUIRED. DISCONNECT atomically commits REVOKED/runtime UNKNOWN and operation
CONFIRMED with cleanup PENDING before deletion; pending deletion cannot restore execution.

There is no distributed transaction. Candidates are encrypted STAGED versions, never executable
by account operations. Each has session/operation ID and provider; after binding it also has
target account, reference, expected base version, new version and creation time.
For an absent reference CAS expects version 0 and creates version 1; subsequent mutations require
the exact positive version and operation receipt. Normal read resolves only the version explicitly
confirmed by the account connection; staged handles cannot be passed through public APIs.
A store receipt contains operation ID, reference, version, content-integrity evidence and
persisted time. It proves durable material, not authorization. Confirmation writes its exact
identity/version into the operation and current connection in the same PostgreSQL commit. Normal
reads require the current connection plus that operation's CONFIRMED decision. If a commit response
is lost, a fresh resolver locks the operation row; this waits for the original transaction to
commit or roll back. CONFIRMED wins unchanged. A rolled-back CONNECT_NEW/RECONNECT fails closed,
sets the session FAILED and schedules candidate deletion; a matching receipt cannot complete it.
A rolled-back REFRESH may be completed only by the refresh-specific guards above; otherwise it
fails closed. If the failed operation physically replaced K1 with an unconfirmed V+1, the next
explicit reauthorization uses fresh K2: it cannot CAS from the older DB receipt V or reset K1.
Candidate deletion is allowed only after FAIL_CLOSED is committed, or after a
CONFIRMED operation proves a different candidate unused. A CONFIRMED DISCONNECT may delete its
previously confirmed reference only after REVOKED is committed; K1 may be retired after K2 is
confirmed. Never delete a current CONNECTED reference/version. For reconnect, its in-flight
operation blocks use of the previous version
once code was sent: provider exchange may have invalidated it. Failed/ambiguous reconnect after
send therefore requires reauthorization; proven pre-send failures preserve previously valid
authorization. Do not claim CONNECTED from an old token merely because DB update failed.

Deletion failure is safely audited and retained for housekeeping retry; STAGED/abandoned versions
cannot be read through account operations. Provider-side revoke of a discarded candidate is
attempted once only if a future adapter proves it is safe and supported; local inaccessibility
does not depend on that call. Identity/channel mismatch discards candidate material before any
account credential is installed, retains account/history, and fails the session.

### Cleanup

Use an internal Quartz job on startup and every 15 minutes, batch size 100. It expires PENDING
sessions immediately at their deadline and resolves CLAIMED sessions older than two minutes
through the same operation-row arbiter without re-exchange. The 60-second callback budget remains
below that threshold; completion requires current CLAIMED/version and operation PENDING.
Before staging a late response, recheck claim ownership. If a crash left CLAIMED before its
pre-call operation row was committed, a conditional session transition to FAILED is sufficient:
no provider call was permitted and no operation/candidate exists. Audit uses durable session ID/actor,
not state. Only after a terminal operation decision may the job delete unused candidates and
terminal sessions. It scans bounded candidate metadata for missing/terminal operation IDs too;
files with no corresponding durable operation/session may be removed only after a fresh database
absence check under the exclusive store-root owner, never while DB is unavailable.

Persist `next_cleanup_at` and `cleanup_attempt_count` on operation and terminal session rows;
set next_cleanup_at to terminalization time when deletion is due. Select due records by
`next_cleanup_at, created_at, id` with keyset pagination and at most 100 attempts per run. A
failed deletion advances retry eligibility with exponential 1, 2, 4... minute delay capped at
one hour; later healthy rows therefore progress. Use the same due/keyset discipline for terminal
session/transient cleanup and bounded store metadata scanning, retaining a durable scan cursor
in Quartz's persisted job data when a pass ends early. The cursor is an opaque ordering key, not
a secret or public file path; the job does not run concurrently with itself. Never delete a
reference/version held by a current CONNECTED connection; REVOKED disconnect cleanup and K1
retirement after confirmed K2 follow their terminal decisions.

Logical invalidity never waits for cleanup. Terminal sessions and expired pending sessions
are physically removed on the first eligible successful sweep after terminalization; target maximum is
24 hours during host/store availability. If deletion cannot be confirmed, keep the non-executable
cleanup record, emit an alert on the 24-hour breach and retry. After downtime, startup cleanup
runs before provider operations are enabled. Startup resolution uses the same operation arbiter:
lock operation, inspect session/account/store receipt, apply kind-specific rules, then process
due cleanup. No provider execution is enabled while relevant non-terminal operations remain
unresolved. This is internal housekeeping, not provider polling.

## 3. G-02 — protected credentials and local storage

`MarketplaceAccount.CredentialReference` is the sole current logical pointer for an account's
credential generation. Generate it server-side with 32 CSPRNG bytes/base64url. Normal refresh and
reconnect keep this reference and monotonically advance its version; an existing account without
a pointer receives one on its first successful callback. Never accept a reference from a public
request. Confirmed missing/corrupt/indecipherable material, or an unconfirmed higher store version
left by a FAIL_CLOSED operation, is a recovery exception:
retire K1, allocate fresh K2 and start K2's own version sequence at 1. Never reset K1 to version 1.
An unreadable old K1 remains non-executable even if its file, backup or old key ring reappears.
Only a CONFIRMED explicit bound recovery operation atomically switches the account pointer and
confirmed version to K2; candidates under K2 cannot execute before then.

`IProtectedCredentialStore` is an internal infrastructure contract, not a generic public KV
service. CreateStaged, BindCandidate, UseConfirmed, CompareAndSwapReplace, Revoke and DeleteCandidate return
typed `SUCCESS|NOT_FOUND|VERSION_CONFLICT|STORE_UNAVAILABLE|CORRUPTED_OR_UNDECRYPTABLE|WRITE_FAILED`.
Success returns only reference/version/operation receipts outside the adapter-use callback.
Revocation is idempotent: already absent is successful deletion. Failures expose no exception
text containing ciphertext, plaintext, filenames or callback material.

The trusted coordinator takes account ID/provider, loads the reference and confirmed metadata
from Commerce, validates ownership, then constructs the store context. The encrypted record also
binds account/provider/reference; mismatches fail closed. Candidate reads require the owning
session/operation context. Controllers never receive a raw-secret read API. Use buffers with
bounded lifetimes; clear owned buffers in finally, do not claim immutable runtime strings can be
reliably erased, and never cache or log raw secrets.

Store owns the monotonically increasing authoritative secret version within each reference
generation (first version 1).
Commerce keeps `ConfirmedCredentialVersion` as a last-confirmed receipt, not an independently
incremented counter. Mismatch requires reconciliation by matching operation receipt; arbitrary
newer versions are never trusted. Unrelated failed/staged versions cannot authorize requests.

Local Windows development uses one encrypted record per logical reference, plus per-operation
encrypted staged files; no global JSON rewrite. Configuration is:
`Marketplaces:CredentialStoreRoot` and existing `DataProtection:DevKeyDirectory`.
Both are mandatory absolute normalized paths when the local store is enabled; example defaults
resolved outside the repo are `%LOCALAPPDATA%/Verce3D/Marketplaces/Credentials` and
`%LOCALAPPDATA%/Verce3D/DataProtection`. Expand these to absolute paths before validation.
Validate against configured `Marketplaces:RepositoryRoot`, host ContentRootPath and WebRootPath;
RepositoryRoot must match the detected Git ancestor in development/test source runs.
Reject same/descendant paths and reparse-point escapes, not just suspicious filenames.
Reject overlap between credential root and key-ring root. Both directories grant access only to
the application Windows user and required system administrators; refuse insecure ACLs.
This overrides the relative `.dataprotection` fallback only when the local marketplace store is
enabled; it uses stable application name `Verce3D` and a versioned marketplace-specific DP
purpose. Existing cookies may require login again when migrating a development key-ring path.

Keys persist across restart and stay outside application/Git content. Repository relocation
works when absolute paths remain intact. Confirmed missing file, corrupt ciphertext, lost key
ring or missing credential root makes each affected account REAUTHORIZATION_REQUIRED/runtime
UNKNOWN independently; preserve account identity, channel, listings, history and grants. Owner
starts explicit bound `REAUTHORIZE_AFTER_STORE_LOSS`, using the existing reauthorize route and a
new K2 operation. Verify exact provider identity/channel/actor, persist candidate under K2,
then atomically confirm K2/version 1 and CONNECTED with session, grants and audit. Probe is the
ordinary post-reconnect availability check. Old K1 is retired and may be deleted only after
terminal arbitration; reappearance cannot change the DB pointer. A crash before K2 confirmation
fails closed with K1 retired for execution; after confirmation K2 remains authoritative.
Key-ring loss may affect many accounts without crashing the local host. A safely configured but
missing credential root may be recreated empty with validated ACLs before new-generation
recovery; while unavailable, provider execution is denied but metadata remains accessible.
At startup, before enabling provider execution, validate each CONNECTED account's current
reference/version and decryptability in bounded batches. Mark confirmed missing/corrupt/key-lost
accounts REAUTHORIZATION_REQUIRED/runtime UNKNOWN independently with safe audit. Until its check
finishes, an account status read projects unusable authorization and no secret is issued. This
validation does not copy or expose secret bytes. A restored old file is still excluded by the
current reference/confirmed-operation guards.
Unsafe configured paths/ACLs still fail startup closed. Existing Production wrapping-certificate
startup validation is unchanged. Transient STORE_UNAVAILABLE blocks use and calls for admin
repair; it does not assert permanent loss or rotate reference until loss is confirmed.

Every mutation takes the store's own per-reference lock and rechecks current version inside it;
the refresh coordinator lock alone does not implement CAS. Write complete encrypted contents
to an unpredictable temp file in the same directory, flush to disk/close, atomically replace the
destination (or atomic rename for creation); never edit current content in place. Readers open
a complete immutable file snapshot. Temp and staged files use identical ACLs. On startup,
uncommitted temp files are deleted; an uncertain replacement is inspected through its operation
ID/version receipt and resolved only by the PostgreSQL operation arbiter before use. Local store
holds an exclusive root ownership file handle for the
host lifetime to reject a second process using the root. Do not delete that file as a lock escape.

Cloud adapters remain future work and preserve these typed CAS/receipt/ownership semantics.
Production S8C.1 has no registered real/fake provider and no local-store activation. It continues
to serve legacy metadata/read workflows; authorization for unregistered providers is explicitly
unsupported. A future real-provider release must select a production store before enabling it.

## 4. G-03 — refresh, failures and recovery

S8C.1 provider execution assumes one application instance. A keyed async single-flight by
immutable account ID is shared by refresh, reconnect installation, generation replacement and
disconnect; store CAS separately uses the current reference's lock. Account ID stays the same
while K1 is replaced by K2, so no two lock identities can operate on one account. The provider
execution gate is always acquired before this account lock. Waiters await the flight,
then reload confirmed version and authorization before using credentials. They do not each
refresh after acquiring a serial lock. Re-evaluate expiry under lock; fake expiry/proactive
margin is deterministic through IClock. Real provider expiry rules remain adapter-owned.

Before a refresh can be sent, commit a REFRESH operation row with account, reference, previous
confirmed version and expected root Version, then durably advance its phase to
EXTERNAL_IN_FLIGHT. This row is the only durable marker; it prevents a crash or DB outage from
silently reusing R1. An active in-process owner may complete it; other provider work waits.
After restart or two-minute abandonment, lock this operation row. REFRESH alone may confirm an
exact same-operation store receipt when all refresh guards in section 2 still hold. RECONNECT
and CONNECT_NEW may not be completed from a receipt: their full callback transaction must have
committed, otherwise they fail closed. A DB outage denies execution; old CONNECTED fields alone
cannot authorize use.

The fake models ML's approximately six-hour access token and single-use rotating refresh token.
Read R1/version V; call RefreshOnce once; persist exactly R2 by CAS expected V -> V+1;
verify operation receipt; commit confirmed version/expiry, CONNECTED, operation CONFIRMED and
refresh audit together. Never return R2 to business/UI code before durable confirmation.

| Outcome | Frozen action |
|---|---|
| normal response and persistence | confirm V+1; complete flight; waiters reload |
| definitive invalid/revoked refresh | REAUTHORIZATION_REQUIRED; deny reads of credential; safe audit |
| failure proven before request send | no replay in this flight; operation FAIL_CLOSED, preserve prior authorization, runtime UNAVAILABLE; later explicit probe may retry |
| timeout/disconnect after send, unknown consumption | no replay of R1; REAUTHORIZATION_REQUIRED |
| success, first transient local write failure | one additional persistence attempt of the identical R2/operation ID, never a second provider call |
| success, persistence recovered | verify receipt, confirm V+1 and CONNECTED |
| success, unrecoverable/unconfirmed write | deny R1; REAUTHORIZATION_REQUIRED; dispose R2 buffers; audit and cleanup |
| CAS conflict after provider success | reload; only the identical operation ID and V+1 receipt confirms a previously completed write; any other version fails closed to REAUTHORIZATION_REQUIRED |

Send certainty is separate typed metadata `NOT_SENT|SENT_OR_UNKNOWN`; an error classification
alone cannot establish it. Exchange and refresh are excluded from generic HTTP retry handlers.
No automatic retry based only on HTTP 5xx/429 for one-time token POSTs. Ordinary safe inspection
can retry; a provider-specific reconciliation mechanism would require a later documented gate.

Disconnect is local-first: serialize, atomically persist REVOKED/runtime UNKNOWN and a CONFIRMED
DISCONNECT operation with cleanup PENDING, then revoke/delete all account credential versions;
clear pointer/confirmed metadata only after delete receipt. Account and history remain. If local
delete fails, the reference remains internally for housekeeping, but normal secret use is denied
immediately by REVOKED. API returns 202 with local cleanup-pending action until confirmed,
otherwise 204. A new reconnect waits until this deletion and pointer clear are confirmed;
it cannot race a pending delete against the old reference. Housekeeping resumes DISCONNECT by
deletion only; it never turns REVOKED into REAUTHORIZATION_REQUIRED or resurrects a stored
version. Future provider-side revoke
is best-effort supported-only and never re-enables local credentials. A disconnected account's
grants remain historical; they are non-executable.

Before horizontal scaling, implement a database lease/fencing coordinator, a shared store and
crash reconciliation for token operations, with cross-instance tests. CAS alone does not prevent
two nodes consuming R1. The same-database/different-credential-root deployment is prohibited:
the local root handle cannot fence it. No Redis/distributed coordinator is built in S8C.1.

## 5. G-04 — authorization and runtime truth

Account authorization is exactly `NOT_CONNECTED|CONNECTED|REAUTHORIZATION_REQUIRED|REVOKED`.
NOT_CONNECTED is unverified/unconfigured; CONNECTED requires verified identity and a confirmed
usable credential; REAUTHORIZATION_REQUIRED means a previously configured authorization can no
longer be safely used; REVOKED is explicit local disconnect. Provider-reported credential
revocation leads to REAUTHORIZATION_REQUIRED so the owner can consent again. No persistent
ERROR or AUTHORIZATION_PENDING. Pending reauthorization is derived from sessions.

Starting a reauthorization session retains current valid credentials and CONNECTED. Once a
reconnect exchange is durably marked EXTERNAL_IN_FLIGHT, its operation suspends use until
confirmed success/failure. A failed browser session proven before send does not invalidate them.

Runtime is exactly `UNKNOWN|AVAILABLE|UNAVAILABLE`; no DEGRADED or threshold counters.
UNKNOWN means no operational observation since connect/reconnect. A completed controlled probe
or operation succeeds -> AVAILABLE. A normalized terminal operational failure after its bounded
safe-read retries -> UNAVAILABLE, with failure classification/time. A later successful Owner
probe restores AVAILABLE. Connect/reconnect resets runtime to UNKNOWN; auth exchange is not
an operational availability observation. Runtime persists across restart without a freshness
timer; expose observation timestamps as historical, never claim live uptime.

For an ordinary capability operation, recheck the operation/provider fence while holding the
provider gate immediately before secret acquisition and send:
`SUPPORTED && GRANTED && account.Active && SalesChannel exists && SalesChannel.Active &&
SalesChannel.Kind == Marketplace && usableAuthorization && runtime != UNAVAILABLE`.
UNKNOWN permits the first controlled attempt. Usable authorization requires CONNECTED,
matching confirmed store material, a CONFIRMED operation, no unresolved account operation and no
provider-wide callback safety fence. Owner probe is a
recovery exception to runtime UNAVAILABLE: it still requires active account, usable authorization
(or its serialized refresh), an active Marketplace SalesChannel and a registered inspector, but no
unrelated business capability.
An expired access token with an eligible refresh enters the coordinator; expired access alone
does not immediately mean revoked consent. Probe honors persisted RuntimeRetryAfterUntil; it does not bypass 429, including after restart.

Structural/grant UNKNOWN blocks business capability execution, never the connect/inspect/probe
operations needed to obtain evidence. Grant observations occur on connect/reconnect and successful
probe; refresh alone preserves grants unless its adapter provides fresh authoritative evidence.
Manual scheduled grant refresh is deferred; probe is the mandatory explicit refresh operation.
Lack of eligibility evidence yields UNKNOWN, explicit verified denial yields DENIED; OAuth scope
alone is insufficient. Outages never rewrite grants or structural support.

## 6. G-05 — S8B upgrade and API

One S8C.1 migration adds session/connection/grant metadata and preserves every account ID,
ProviderCode, ExternalAccountId, SalesChannel, Active flag, listing, observation and offer.
Backfill every existing account connection as NOT_CONNECTED/runtime UNKNOWN regardless of old
ConnectionState. Legacy identity is retained but unverified; first successful bound reconnect
records IdentityVerifiedAt. Clear all legacy CredentialReference values without resolving or
copying arbitrary client-provided pointers into the store. Never infer protected credentials
from S8B configuration. No account/listing history is deleted.

If a legacy ExternalAccountId was mistyped, a bound reconnect returning another identity fails.
Preserve/deactivate the old account and its listings/history, then authorize the correct identity
as a separate account when permitted. Never rewrite the old identity or reassign history
automatically; history reassociation needs a separately approved administrative workflow.

Remove account `ConnectionState`, `LastError` and generic `LastFailureAt` columns/properties in
the single migration. New connection is sole auth/runtime authority. SyncState,
LastSyncAttemptAt and LastSuccessfulSyncAt remain unchanged for later resource sync. Do not copy
untrusted legacy free-text LastError into new diagnostics; prior audit history follows existing
retention. Document this deliberate metadata reset, not as a lossless error migration.
Connection is an account-owned entity via IOwnedBy; all child mutations bump MarketplaceAccount
Version once per UoW. It has no independent version counter competing with that root.

POST `/api/commerce/marketplace-accounts` is retired with Owner-authorized, antiforgery-protected
410 `ACCOUNT_CREATION_REQUIRES_AUTHORIZATION`, no body binding. Manual disconnected account
creation is not retained. PUT account accepts only DisplayName and expected Version; reject
unknown identity/channel/credential properties with 400 instead of silently ignoring them.
Existing activation/deactivation stays Owner-only; it changes intent, not credentials.
Provider/external identity/channel are immutable. Generated OpenAPI/frontend types and views
must be updated in the same implementation; old ConnectionState DTO field is removed, not
independently maintained as a compatibility projection.

Routes and contracts (all commands except callback require Owner + antiforgery):

| Method/path under /api/commerce | Input / safe result |
|---|---|
| POST /marketplace-authorizations | ProviderCode, SalesChannelId, DisplayName; returns session ID, validated redirect URI, expiry; never accepts external ID/reference |
| POST /marketplace-accounts/{id}/reauthorize | expected Version only; server resolves identity/channel |
| GET /marketplace-authorizations/{providerCode}/callback | in-memory state/code or denial; anonymous with persisted actor/browser/session guards |
| POST /marketplace-authorizations/{id}/cancel | initiating Owner, session Version; PENDING -> REVOKED only |
| GET /marketplace-authorizations/{id} | Owner initiating actor only; safe status/outcome, no hash/code/reference |
| GET /marketplace-accounts/{id}/connection | commerce:read; safe status DTO |
| POST /marketplace-accounts/{id}/disconnect | expected Version; local-first revoke |
| POST /marketplace-accounts/{id}/probe | expected Version; controlled safe identity/grant inspection |

Safe status DTO contains account/provider/Active, authorization/runtime states, IdentityVerifiedAt,
last runtime success/failure/classification, normalized RequiredAction, grants with provenance,
Version and derived authorizationInProgress/credentialOperationInProgress. Never expose reference,
secret version, state hashes or file paths. RequiredAction precedence is CONTACT_ADMIN for safe
configuration/transient store outage; REAUTHORIZE_AFTER_STORE_LOSS for confirmed credential/key
loss; RECONNECT_EXISTING_ACCOUNT for a duplicate new authorization; REAUTHORIZE for other unusable
authorization; AUTHORIZE for NOT_CONNECTED or REVOKED; CHECK_PROVIDER_ACCOUNT for grant denial/
runtime failure; otherwise NONE. Both specialized actions are local catalog codes with safe pt-BR
text. The store-loss action starts the existing bound reauthorize route and internally chooses a
new reference generation; public requests never choose or see K1/K2.
Public display text comes from local pt-BR messages. Operator/Viewer may read but cannot configure,
probe, disconnect or start/restart auth. Existing manual grant endpoint is retired with 410;
new grants come only from inspections, while migrated historical grants have LEGACY_MANUAL source.

## 7. G-06 — fake and production isolation

Fake code lives in shared test-support/harness projects only, not in the production Infrastructure
assembly. Test host registers it through the real registry/ports and inserts test-only FAKE
provider/capability fixtures into its disposable PostgreSQL. Normal seed has no FAKE row.
There is no Development enable-fake switch in the production app. The E2E harness uses ordinary
routes and real session/Commerce/store orchestration with isolated persistent paths/key ring.

Production startup rejects any connector registration marked test-only and any FAKE reference
row; no fake code is shipped in its project dependency graph, and fake resolution/start returns
unsupported when absent. Any deterministic fault/consent helper is mapped by the isolated test
host only; Production has no such routes. A Production-environment test verifies absence of
registration/routes/seed and fails on deliberately injected fake artifacts. Use repository
Production certificate setup for that test; do not weaken startup validation.

Fake simulates success/denial, fixed authoritative identities and grant observations, expiry,
single-use refresh rotation, revocation, before-send failure, after-send ambiguity, 429/5xx/4xx
and request IDs. It has a deterministic mode in which issuing a credential for identity X
invalidates its previous credential, plus a proven-preserving mode. The former proves duplicate
new-session and mismatch fail-closed behavior; neither mode claims real-provider behavior.
It uses barriers/completion sources and IClock, never sleeps. A fake HTTP
message handler additionally exercises the real common response mapper/retry pipeline without
sockets; returning a preclassified failure alone is not HTTP resilience coverage.

## 8. G-07 — fee authority

S8C.1 leaves IChannelFeeProvider, ChannelFeeQuote and LocalFeeRuleProvider unchanged and creates
no fake/live fee adapter. Current input is SalesChannel-based and current output requires local
FeeRule/FeeRuleVersion IDs. These cannot truthfully represent an account-scoped provider result
without contract evolution. Before S8C.4 live fees, a separate architecture gate must define
MarketplaceAccount context, category/logistics/price inputs, components, fallback selection,
freshness/cache and LIVE_API/CACHE/MANUAL/FALLBACK provenance through an evolution of this seam.
Never fabricate local FeeRule IDs for provider results or create a competing pricing authority.

## 9. G-08 — HTTP, diagnostics and audit

Named/typed IHttpClientFactory clients own fixed allow-listed base URLs, API versions, signing and
DTOs. Request context is provider/account/operation/correlation only. Redirect URI construction
uses adapter-configured allow-listed origins and fixed callback paths, never caller-supplied hosts.
No real provider URL/SDK contract is implemented in S8C.1.

Failure classification is `NON_RETRYABLE|RETRYABLE|RATE_LIMITED|AUTH_RENEWAL_REQUIRED|
USER_ACTION_REQUIRED|UNKNOWN`, plus send certainty for token operations. A VERCE-owned code
and message catalog supplies SafeMessage; raw provider message/body/error_description is never
persisted, returned or logged, including exceptions. Provider error code is retained only when
the adapter recognizes it and it matches ASCII [A-Za-z0-9_.:-], length 1..64; otherwise omit.
Provider request ID requires an adapter-declared diagnostic-only field, same characters, length
1..128, and no known secret/PII value; otherwise omit. A character regex alone is not proof that
an arbitrary header is safe. Unverified external fields are discarded. Internal correlation ID
is always server generated. API errors expose VERCE code, catalog message, correlation ID and
RequiredAction only; provider diagnostic identifiers are restricted internal fields.

Log callback route template only. Suppress entire query string and bodies on authorization,
callback and token paths across ASP.NET hosting/request logs, HTTP handlers, structured exception
sinks, tracing and any reverse-proxy config controlled by the app. Specifically exclude code,
state, error_description and any extra callback field; state hashes/browser nonces are also
excluded. Redact Authorization/Cookie/Set-Cookie and provider-declared signing headers. Do not
log outbound full token/authorization URLs or inbound raw exceptions containing request URLs.
UI callback immediately replaces URL with the fixed result route; use no-store and no-referrer.
No CPF/address/email/phone/buyer payload in integration diagnostics.

Idempotent reads have a frozen local default: at most 3 total attempts within a 30-second budget,
exponential delays 250ms, 500ms plus bounded 0..100ms injected jitter. These are VERCE defaults,
not provider quotas. Retry-After is a lower bound: if it exceeds remaining budget, return
RATE_LIMITED with safe RetryAfter instead of sleeping/retrying early. Honor valid future HTTP-date
or nonnegative seconds; invalid header gives no invented value. Only mapped retryable failures
retry; no write, auth-code exchange or refresh goes through this handler. No circuit breaker.
Runtime UNAVAILABLE blocks automatic work; explicit probe provides controlled recovery.

Audit begin/claim/completion/failure, identity conflict, credential confirmation/revocation,
grant changes, reauth, disconnect and cleanup failure using actor/session/account/provider,
UTC time, local code and correlation. Business changes and audit commit in the same DB
transaction; external write compensation follows section 2. DB outage never licenses use of
unconfirmed credentials. Logs/metrics are supplemental safe failure evidence until DB recovers.
Metrics labels are bounded provider/operation/classification/result, never account/session IDs.
Reuse existing logging/Activity infrastructure; no new tracing stack.

The existing generic `AuditSaveChangesInterceptor` redacts only selected property-name patterns;
do not mark authorization sessions or credential-operation rows `[Auditable]` and rely on that
generic scalar snapshot. The existing auditable MarketplaceAccount also contains
`CredentialReference`; the S8C.1 implementation must explicitly suppress that field from its
generic old/new JSON and changed-column payload when account pointer changes. Write explicit
allowlisted safe audit events in the same transaction:
opaque session/operation/account IDs for correlation, actor, provider, event, local outcome code,
UTC time and correlation ID only. Exclude `StateHash`, `BrowserBindingHash`, protected handles,
credential references/versions, code, tokens, provider text and PII from old/new JSON and changed
column payloads. Integration tests inspect actual persisted `AuditLog` rows, not only log sinks.

## Recovery acceptance matrices (normative)

`EXECUTE` below means a new business capability may acquire the current confirmed secret after
all structural, grant, channel and runtime guards. An in-flight provider fence always denies it.

| RT-01 scenario | Terminal operation decision | Connection | Candidate/secret action | EXECUTE |
|---|---|---|---|---|
| Normal RECONNECT commit | CONFIRMED | CONNECTED, new confirmed version | Retain current version | Yes after guard reload |
| Store persisted; callback commit succeeds but reply lost | CONFIRMED after row-lock wait | CONNECTED | Retain current version; no compensation | Yes after guard reload |
| Store persisted; callback commit rolls back | FAIL_CLOSED | Existing account REAUTHORIZATION_REQUIRED; no new account | Delete candidate after decision | No |
| Commit still pending when resolver starts; callback wins row lock | CONFIRMED | CONNECTED | Retain current version | Yes after guard reload |
| Resolver wins terminal row lock before callback commit | FAIL_CLOSED | Existing account REAUTHORIZATION_REQUIRED; no new account | Delete candidate; callback cannot confirm | No |
| Cleanup races confirmation | CONFIRMED if callback wins; otherwise FAIL_CLOSED | CONNECTED if confirmed; otherwise REAUTHORIZATION_REQUIRED/no new account | Retain confirmed material or delete failed candidate, never both | Only if confirmed |
| Startup finds CONNECT_NEW/RECONNECT receipt without committed session success | FAIL_CLOSED | Existing account REAUTHORIZATION_REQUIRED; no new account | Delete candidate after decision | No |
| Startup finds REFRESH receipt without DB confirmation, exact guards still valid | CONFIRMED by refresh-only resolver | CONNECTED at V+1 | Retain matching R2 | Yes after guard reload |
| REFRESH receipt with failed guards/foreign operation | FAIL_CLOSED | REAUTHORIZATION_REQUIRED | Delete unconfirmed candidate | No |
| Failed operation left unconfirmed K1 V+1 above DB V | FAIL_CLOSED | REAUTHORIZATION_REQUIRED | Retire K1; explicit next reconnect uses K2, never CAS from stale V | No |
| Actor loses permission before callback confirmation | FAIL_CLOSED | Existing account REAUTHORIZATION_REQUIRED after send; no new account | Delete candidate | No |
| Session already terminal FAILED before callback confirmation | FAIL_CLOSED | Existing account REAUTHORIZATION_REQUIRED after send; no new account | Delete candidate | No |
| Cleanup visits confirmed current operation | CONFIRMED unchanged | CONNECTED unchanged | Never delete current reference/version | Yes after guards |

The lock waits for an already-running commit or rollback; a resolver cannot turn one negative
snapshot into FAIL_CLOSED. The same table applies to normal callback compensation, startup and
Quartz; no separate receipt-only recovery path exists.

| RT-02 scenario | Impact evidence | Local result and Owner action |
|---|---|---|
| CONNECT_NEW returns existing active X | UNKNOWN/MAY_SUPERSEDE_EXISTING | Existing X -> REAUTHORIZATION_REQUIRED; new operation FAIL_CLOSED; delete candidate; RECONNECT_EXISTING_ACCOUNT |
| CONNECT_NEW returns existing inactive X | UNKNOWN/MAY_SUPERSEDE_EXISTING | Same, preserve inactive flag/history; Owner activates X before bound reconnect |
| Two CONNECT_NEW sessions resolve to new X | UNKNOWN/MAY_SUPERSEDE_EXISTING for later exchange | One account row only; later successful exchange makes X REAUTHORIZATION_REQUIRED; no false CONNECTED; bound reconnect required |
| Explicit RECONNECT X returns X | Exact identity/channel and all callback guards | Confirm X normally; no other account rebound |
| Explicit RECONNECT X returns identity owned by Y | UNKNOWN/MAY_SUPERSEDE_EXISTING | Reject; X and existing Y REAUTHORIZATION_REQUIRED; both histories retained |
| Duplicate CONNECT_NEW returns X | Officially proven PRESERVES_EXISTING | Fail new operation/delete candidate; existing X may remain CONNECTED; still require explicit reconnect to install new candidate |
| Duplicate CONNECT_NEW returns X | UNKNOWN | Conservative REAUTHORIZATION_REQUIRED as above |
| Duplicate CONNECT_NEW returns X | MAY_SUPERSEDE_EXISTING | Conservative REAUTHORIZATION_REQUIRED as above |
| Crash after exchange before identity is known | Unknown account and impact | Provider fence persists; startup fails operation and marks all accounts for provider REAUTHORIZATION_REQUIRED before release |

The fake must prove the MAY_SUPERSEDE_EXISTING path by invalidating X's previous credential when
it issues another one. Provider identity coordination is keyed by ProviderCode + ExternalAccountId
after inspection; the database unique pair remains final. A provider-wide pre-identity fence
covers the interval that a canonical account key cannot yet cover.

| RT-03 scenario | Durable/account result | Recovery and stale-material result |
|---|---|---|
| K1 v7 file missing | REAUTHORIZATION_REQUIRED/runtime UNKNOWN | Explicit bound recovery creates K2 v1; never reset K1 |
| K1 v7 ciphertext corrupt | Same | Quarantine K1 for cleanup; K2 after verified recovery |
| K1 has unconfirmed v8 after DB v7 confirmation rolled back | REAUTHORIZATION_REQUIRED | Fail operation closed and recover explicitly into K2; never reset/CAS stale K1 |
| Local key ring lost | Each affected account independently REAUTHORIZATION_REQUIRED | Host remains available for metadata; Owner consent produces fresh K2 per account |
| Entire safely configured credential root missing | Affected accounts REAUTHORIZATION_REQUIRED | Recreate secure empty root or await admin repair; recover each into K2, never recreate K1 v1 |
| K1 lost, K2 recovery confirms | K2 v1/current operation CONFIRMED and CONNECTED | Old K1 retired and never read by account operations |
| Old K1 v7 file/key backup reappears | K2 remains current in DB | Ignore/delete stale K1 only after safe arbiter check; never roll back pointer |
| Crash after K2 operation prepared | Operation FAIL_CLOSED on startup if no callback success | K1 stays unusable; K2 absent/unconfirmed |
| Crash after exchange or K2 store persistence before DB confirmation | FAIL_CLOSED unless the complete callback transaction actually committed | Delete K2 candidate; K1 stays unusable; receipt cannot promote K2 |
| Crash after K2 DB confirmation | CONFIRMED | K2 executable after guard reload; K1 never executable |
| One lost key ring affects many accounts | Per-account fail-closed decision | Each Owner recovery independently confirms a fresh reference; no account/history deletion |

## Alternatives and consequences

Rejected: plaintext/encrypted blobs in Commerce; pre-auth placeholder accounts; one giant
connector; per-provider assemblies without concrete need; replay of ambiguous token requests;
runtime UNKNOWN as a veto; pending/error authorization enums; mutable public credential pointers;
fake production switches; simultaneous database/secret-store version authorities; fees redesign
before S8C.4. The durable operation row and staged receipt add recovery metadata because
a filesystem/vault write cannot share a PostgreSQL transaction. Local development reauthorization
after key loss is intentional. Cleanup bounds require a running host/store; breaches are visible.

## Compliance checks — mandatory implementation acceptance

- Architecture: inward graph, only two new semantic provider ports, no future resource DTOs,
  unchanged fee seam; fake absent from production dependency graph/DI/seed/routes.
- Domain: exact auth/runtime/session transitions; UNKNOWN permits first probe/business attempt;
  UNAVAILABLE recovery; active/channel/identity guards; grants never rewritten by outages.
- PostgreSQL: unique state/identity, concurrent claim before one exchange, concurrent new sessions,
  version conflicts, correct rollback/reload, clock-driven expiry, Quartz fair/keyset cleanup,
  transaction audit and restart compensation. Use real PostgreSQL barriers for lost commit reply,
  still-pending commit, terminal row-lock races, callback versus cleanup/startup, actor loss,
  REFRESH-only receipt confirmation and RECONNECT receipt rejection. Prove exactly one terminal
  decision and no deletion of a confirmed version at every cross-store/DB boundary.
- Upgrade from committed S8B schema: preserve IDs/channel/history/Active/sync, all connections
  NOT_CONNECTED, pointers cleared, no arbitrary secret lookup, unsafe DTO/routes retired.
- Local store: path/ACL/reparse rejection, stable keys across restart, atomic replace/CAS,
  missing/corrupt/wrong-user key material, non-executable orphans, second-process root refusal.
  Start from K1 version >1 and prove missing file, corrupt file, lost ring/root, K2 recovery,
  old K1 backup reappearance and crash before/after K2 confirmation.
- Refresh: one call across parallel waiters, same-R2 persistence retry, unrecoverable failure,
  same-operation receipt recovery, alien CAS conflict, pre-send vs after-send ambiguity,
  operation-row recovery after crash and no R1 replay. Deterministic barriers, not sleeps.
- Security: actor revocation and browser/session/provider mismatch, forged/expired/replayed state,
  Owner/Operator/Viewer permissions, no code/state/hash/browser nonce/raw provider text/PII in
  captured logs/API/persisted AuditLog, catalog messages, bounded identifiers, no reference
  fields in public DTOs. Fake invalidates previous X credentials on duplicate issuance and tests
  active/inactive X, concurrent new sessions, explicit X reconnect and X->Y mismatch.
- HTTP: fake message handler proves 429/Retry-After, retryable 5xx, nonretryable 4xx, attempt budget,
  cancellation and zero replay of writes/token requests.
- Frontend/isolated E2E: fake begin -> restart -> callback -> CONNECTED/runtime UNKNOWN -> probe
  AVAILABLE -> concurrent refresh -> injected lost-write REAUTHORIZATION_REQUIRED -> reconnect
  -> disconnect; account/history survive and secrets are inaccessible/deleted.
- Full repository architecture/unit/integration/frontend gates, OpenAPI generation/check, EF
  drift check, fresh/upgrade DB and migration down/up in disposable DB. No external network or
  seller credentials for certification. Independent Astra final regate remains required.
