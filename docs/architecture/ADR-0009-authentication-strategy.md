# ADR-0009 — Authentication Strategy

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-06
- **Sprint:** S0

## Context

The system serves one small operation — an owner and a few helpers. It will be published on the
internet, so it must be protected against the realistic threat: **an unauthenticated stranger
finding the URL**. It must also attribute every change to a person, because auditability is a
stated requirement.

The brief asks for: single organization initially, multi-user ready, future roles, no
SaaS multi-tenancy in this version, and an evaluation of ASP.NET Core Identity with cookies.

## Decision

### 1. ASP.NET Core Identity with cookie authentication

The frontend is served **same-origin** with the API (Vite dev proxy in development, static files
from the API host in production). In a same-origin architecture a cookie is both simpler and
safer than a bearer token:

- `HttpOnly` means JavaScript cannot read it, so an XSS bug cannot exfiltrate the session;
- it is revocable server-side (`SecurityStamp` validation), so deactivating a user takes effect
  without waiting for a token to expire;
- no refresh-token rotation machinery — a well-known source of subtle bugs — is needed;
- Identity brings password hashing, lockout, and user/role storage that would otherwise be
  hand-written, which is exactly the kind of code a small project should not write.

Configuration:

```
HttpOnly = true · Secure = Always · SameSite = Lax
Name = __Host-verce.auth · ExpireTimeSpan = 8h · SlidingExpiration = true
```

Ticket data is protected by ASP.NET Core Data Protection with the key ring persisted in the
database, so restarts do not log everyone out and a second instance can validate the same
cookies.

### 2. Multi-user from day one, single-organization by omission

- **Real accounts, real roles, per-user audit attribution.** There is never a shared
  `admin/admin` login: a shared account destroys audit attribution, which is a stated
  requirement, and it is the credential that inevitably ends up in a chat message.
- **No `organization_id` column anywhere.** Tenant plumbing would touch every query, every
  index, every test and every seed, permanently, for a capability that may never be needed.
  Adding it later to a database with a few thousand rows is a mechanical migration plus a
  global query filter — a contained change, done once, when there is a second organization to
  justify it.

### 3. Roles and permissions

Roles: `Owner`, `Operator`, `Viewer` (SECURITY §3.1).

Authorization is expressed as **permission constants** mapped to roles in one place, not as
role-name comparisons scattered through handlers:

```csharp
public static class Permissions
{
    public const string QuotesApprove  = "quotes:approve";
    public const string PricingManage  = "pricing:manage";
    public const string SettingsManage = "settings:manage";
    public const string AiConfigure    = "ai:configure";
}
```

Adding a fourth role later means editing the mapping table, not auditing every endpoint. This is
the concrete meaning of "roles futuras": the seam exists now, the roles come later.

### 4. Secure by default

The root route group carries `RequireAuthorization()`. Anonymous access is an explicit,
reviewable exception — `/health/live`, the login endpoint, static assets. A new endpoint is
protected unless someone deliberately opts out, rather than exposed unless someone remembers to
protect it.

### 5. Supporting controls

Antiforgery tokens on every state-changing request (`XSRF-TOKEN` cookie echoed in a header);
lockout after 5 failures for 15 minutes; minimum 12-character passwords with no composition
rules; rate limiting on `/api/auth/*`; no public registration — users are created by an `Owner`.

### 6. First Owner bootstrap

> **Added 2026-09-07 (gate blocker B-004).** Public registration is disabled and only an `Owner`
> can create users — but a fresh installation has no `Owner`, so the system was unreachable.
> **No default credential is seeded.** `admin/admin`, `admin@verce`, or any well-known password
> would be a permanent backdoor on every deployment that forgot to change it, and would appear
> in this public specification.

> **Re-gate correction D (2026-09-07).** The original said the command "locks the role membership
> rows". **There are no rows to lock on an empty database**, so two simultaneous invocations
> could both observe zero Owners and both create one. Serialization now uses a stable lock
> resource that exists whether or not any Owner does.

Bootstrap is an **administrative CLI command on the API host**, never an HTTP endpoint.
**S1 SHALL implement:**

```
dotnet Verce.Api.dll bootstrap-owner --email owner@example.com [--name "Nome"]
```

#### 6.1 Serialization on a stable resource

```
BEGIN TRANSACTION
  SELECT pg_advisory_xact_lock(8401001);        -- VERCE_LOCK_OWNER_BOOTSTRAP
  -- the lock key is a constant, not a row: it exists on an empty database
  count := SELECT count(*) FROM active users holding the Owner role;
  IF count > 0 THEN
      ROLLBACK; exit code 3 ("an Owner already exists")
  END IF;
  create Owner identity (setup_status = PENDING_SETUP, password_hash = NULL)
  create single-use BOOTSTRAP setup token
  write audit row (source = CLI)
COMMIT
print the setup URL once
```

`pg_advisory_xact_lock` is transaction-scoped: it is released automatically on commit or
rollback, including if the process is killed, so a crashed bootstrap cannot wedge the system.

**Concurrent behaviour is therefore defined:** process A takes the lock and creates the Owner;
process B blocks on `pg_advisory_xact_lock`, acquires it after A commits, observes
`count > 0`, and exits non-zero. **Exactly one first Owner, always.**

**Advisory lock key registry** — keys are constants, documented in one place so two features
never collide:

| Key | Purpose |
|---|---|
| `8401001` | Owner bootstrap (§6.1) |
| `8401002` | Owner role mutation / last-Owner guard (§10) |
| `8401003` | Data Protection crypto recovery ([ADR-0008](ADR-0008-ai-integration-and-secret-handling.md)) |

#### 6.2 The secret challenge — two operands from two sources

> **Corrected 2026-09-07 (re-gate B-RG2-002).** The previous text named one value,
> `VERCE_BOOTSTRAP_SECRET`, and said the command "reads it from the same mounted file" and
> compares it. Comparing a value with itself is not authentication — it always succeeds for
> anyone who can run the process. The **expected** secret and the **candidate** secret must come
> from genuinely different sources.

| Operand | Source | Never |
|---|---|---|
| **Expected** | a **mounted secret file**, path from `VERCE_BOOTSTRAP_SECRET_FILE` — provisioned by whoever deploys the system | source literal · the database · a CLI argument |
| **Candidate** | typed by the administrator at execution time into a **silent, no-echo stdin prompt** (`Enter bootstrap secret:`) | a CLI argument · an environment variable · the expected file |

The security property is therefore: *possession of the host is not enough; the operator must also
**know** the secret.* An attacker with shell access but without the secret cannot seize a fresh
installation.

| Aspect | Contract |
|---|---|
| Entropy | expected secret **≥ 256 bits** of randomness (≥ 43 base64url chars), validated **at startup**, not at use |
| Candidate validation | length and charset checked before comparison, to fail fast on a truncated paste |
| Comparison | `CryptographicOperations.FixedTimeEquals` over UTF-8 bytes — never `==` |
| Logging | **neither operand** is ever logged, echoed, or placed in an exception message. Only the outcome (`accepted` / `rejected`) is logged |
| On mismatch | exit code 2, generic message `bootstrap secret rejected` (it does not distinguish missing from wrong), a `Warning` log, and an audit row with `source = CLI`, outcome `REJECTED` |
| Environment | required in `Production`; optional in `Development`, so a developer needs no secret infrastructure |

**Order of operations** — the challenge happens *before* any lock or database work, so a failed
attempt touches nothing:

```
1. load expected secret from the mounted file      (fail if absent/short → exit 2)
2. read candidate from no-echo stdin               (fail if empty → exit 2)
3. validate candidate length/format
4. FixedTimeEquals(expected, candidate)            (fail → audit + exit 2)
5. BEGIN TRANSACTION; pg_advisory_xact_lock(8401001)
6. … the bootstrap transaction (§6.1)
```

**Non-interactive bootstrap is deliberately not supported in v1.** Production bootstrap and
recovery require local interactive administrative execution. If an automated deployment ever
needs it, that is a separate, separately-reviewed mechanism — not a flag that reintroduces a
secret on a command line.

#### 6.3 What the command creates

- Owner identity with **`password_hash = NULL`** *and* **`setup_status = PENDING_SETUP`** (§7.1),
  so no guessable credential exists at any moment;
- exactly one `BOOTSTRAP` setup token;
- an audit row with `source = CLI`, the e-mail, and the host user that ran it.

**No password is ever accepted as a command-line argument.** `--password "…"` would land in
shell history, in `ps` output, and in container logs.

### 7. The setup-token flow

`platform.account_setup_token`: `user_id`, `token_hash char(64)` (**unique**),
`purpose {BOOTSTRAP, RESET, RECOVERY}`, `expires_at`, `consumed_at`, `invalidated_at`,
`created_at`, `created_by_source`, `created_by_user_id`.

- The token is **256 bits** of cryptographically secure randomness, base64url-encoded, shown
  **once** and **never stored** — only its SHA-256 hash is persisted.
- **Expires in 30 minutes.** The operator is at the console when the command runs.
- `/setup-account?token=…` is the **only** anonymous endpoint that can set a password. It is
  rate-limited and never reveals whether a token exists.
- `consumed_at` and `invalidated_at` are **different facts**: consumed = a human used it;
  invalidated = it was superseded by a newer token or by a role change. Both make the token
  unusable; keeping them distinct preserves what actually happened.

#### 7.1 `setup_status` — an explicit lifecycle, not an inference

> **Re-gate correction D.** Relying on `password_hash IS NULL` alone was ambiguous: it is a
> provider-internal detail, and a future Identity change or an external login provider could
> leave it null on a perfectly usable account.

`platform.user` carries `setup_status {PENDING_SETUP, ACTIVE}` and `setup_completed_at`.

```
bootstrap / recovery creates → PENDING_SETUP, setup_completed_at = NULL
setup token consumed         → ACTIVE,        setup_completed_at = now()
```

**Login guard (frozen):** authentication is rejected for any user whose `setup_status` is
`PENDING_SETUP`, **regardless of whether a password hash exists**, with a generic failure
indistinguishable from a wrong password. The check happens before password verification.

#### 7.2 Token consumption is a single atomic statement

Two requests presenting the same token must not both succeed:

```sql
UPDATE platform.account_setup_token
SET consumed_at = now()
WHERE token_hash = :hash
  AND consumed_at    IS NULL
  AND invalidated_at IS NULL
  AND expires_at > now()
RETURNING id, user_id, purpose;
```

**0 rows → rejected**, with one generic message for consumed, invalidated, expired and unknown
alike. Exactly one caller can win, because the conditional update takes the row lock and the
loser re-evaluates `consumed_at IS NULL` as false.

The remainder of the flow (set password, `setup_status = ACTIVE`, regenerate `SecurityStamp`,
invalidate the user's other open tokens) runs in the **same transaction** as that statement.

#### 7.3 Why an unsalted SHA-256 is sufficient here

Password hashing needs salting and stretching because passwords are low-entropy and
human-chosen. This token is **256 bits of uniform randomness**: brute force is infeasible
regardless of how fast the hash is, and there is no dictionary to precompute, so a salt would add
nothing. SHA-256 is used solely to make a database read useless to an attacker. **This reasoning
does not transfer to passwords**, which use ASP.NET Core Identity's password hasher.

### 8. Password reset and session invalidation

| Situation | Mechanism |
|---|---|
| Non-Owner user forgets their password | An `Owner` triggers a reset → new `RESET` token, sessions invalidated |
| An Owner forgets their password | **Another** `Owner` resets them, same flow |
| The **last** Owner is locked out | Offline `recover-owner` CLI (§9) |

Every reset, recovery and role change **regenerates the ASP.NET Core Identity
`SecurityStamp`**. Because validation runs on an interval (30 minutes, SECURITY §2.4), existing
cookies stop working without waiting for cookie expiry. Deactivating a user works the same way.

### 9. Last-Owner recovery

A single-Owner operation is the normal case here, so last-Owner lockout is a realistic incident,
not a theoretical one. Recovery is **offline and local**:

```
dotnet Verce.Api.dll recover-owner --email owner@example.com
```

- Requires the **recovery** secret, subject to the same two-operand protocol as §6.2:
  **expected** from the mounted file at `VERCE_RECOVERY_SECRET_FILE`, **candidate** from a silent
  stdin prompt, fixed-time comparison, neither ever logged.
- The recovery secret and the bootstrap secret **must be different values**, provisioned as
  separate files. They authorize different capabilities at different times, so leaking one must
  not grant the other.
- Requires local process access on the host. **It is never exposed over HTTP, anonymous or
  otherwise.**
- Works whether or not other Owners exist — a break-glass tool, loud in the audit log.

**Recovery is serialized and newest-token-only.** Repeated or concurrent recoveries must not
leave several valid tokens for one account:

```
BEGIN TRANSACTION
  SELECT pg_advisory_xact_lock(8401002);            -- owner role mutation lock
  SELECT ... FROM platform.user WHERE id = :id FOR UPDATE;
  UPDATE platform.account_setup_token
     SET invalidated_at = now()
   WHERE user_id = :id AND consumed_at IS NULL AND invalidated_at IS NULL;   -- kill all prior
  UPDATE platform.user
     SET password_hash = NULL,
         setup_status = 'PENDING_SETUP', setup_completed_at = NULL,
         security_stamp = :new_stamp;                -- terminates every active session
  INSERT INTO platform.account_setup_token (... purpose = 'RECOVERY' ...);   -- exactly one
  INSERT INTO platform.audit_log (... source = 'CLI' ...);
COMMIT
print the setup URL once
```

Two concurrent recoveries therefore serialize: the second invalidates the first's token before
issuing its own, so **exactly one recovery token is valid at any moment** — the newest.

#### 9.1 Recovering the **sole** Owner — the break-glass exception

> **Corrected 2026-09-07 (re-gate B-RG2-002).** The two rules contradicted each other: recovery
> moves the Owner to `PENDING_SETUP`, a `PENDING_SETUP` Owner does not count as active, and
> `LAST_OWNER_PROTECTED` forbids any operation that brings the active-Owner count to zero — so
> the one scenario `recover-owner` exists for was forbidden by the guard. Resolved by scoping the
> guard to the surface it was written for.

**`LAST_OWNER_PROTECTED` is an API-surface guard, not a database invariant.** Its purpose is to
stop a routine authenticated request from locking everyone out by accident. `recover-owner` is a
local, offline, secret-authenticated break-glass command — the deliberate act of an operator who
is already locked out — and it is **not** subject to that rejection.

| | Normal API / UI | `recover-owner` (local CLI) |
|---|---|---|
| May deactivate, delete or demote the last active Owner | **No** — `LAST_OWNER_PROTECTED` | n/a — it does none of these |
| May move the sole Owner to `PENDING_SETUP` | **No** | **Yes** |
| Removes the `Owner` role | Never in this flow | **Never** |
| Requires the recovery secret + local host access | — | Yes |

Two things make this safe rather than a hole in the invariant:

1. **The `Owner` role is never removed.** The account remains *the designated Owner*; it is
   temporarily non-login-capable (`setup_status = PENDING_SETUP`). No role membership is
   destroyed, so there is no state from which the system cannot return.
2. **Only this path can produce zero *active* Owners**, and only transiently. The regular API
   cannot reach that state at all, and `recovery_started_at` on the identity records that the
   condition was entered deliberately, by whom, and when.

`platform.user.recovery_started_at timestamptz null` is set when recovery begins and cleared when
the token is consumed. A non-null value on an Owner means "a break-glass recovery is in flight",
which the audit log and any future admin view can surface.

**Closing the window atomically.** Consuming the recovery token performs, in **one transaction**:
token consumption (the conditional update of §7.2) → password set → `setup_status = ACTIVE` →
`setup_completed_at = now()` → `recovery_started_at = NULL` → `SecurityStamp` regenerated. The
system returns to exactly one active Owner, or the whole thing rolls back and the recovery token
remains usable.

### 10. Owner invariants

**The system must never allow the last active `Owner` to be deleted, deactivated, or demoted
through the API or UI.**

> **Re-gate correction D.** A plain "count, then act" guard has a lost-update race: with Owners
> A and B, request 1 removes A and request 2 removes B; both counted 2 before either committed,
> and the result is **zero Owners**. Counting must be serialized against every other
> Owner-role mutation.

The **guarded API operations** are exactly: **deactivate an Owner · delete an Owner · remove the
`Owner` role** (and any equivalent HTTP or admin business mutation added later). Each takes the
**same** advisory lock and re-counts inside it:

```
BEGIN TRANSACTION
  SELECT pg_advisory_xact_lock(8401002);        -- VERCE_LOCK_OWNER_ROLE_MUTATION
  activeOwners := SELECT count(*) FROM users u JOIN user_role ur ...
                  WHERE role = 'Owner' AND u.is_active AND u.deleted_at IS NULL
                    AND u.setup_status = 'ACTIVE';
  IF the operation would bring activeOwners to 0 THEN
      ROLLBACK → 409 LAST_OWNER_PROTECTED
  END IF;
  apply the mutation; regenerate SecurityStamp; write audit row
COMMIT
```

> **`recover-owner` is deliberately NOT in that list.** It is a local, offline,
> secret-authenticated break-glass command; it removes no role and performs none of the three
> guarded mutations. It takes the same lock `8401002` for serialization, but it is **not**
> subject to the `LAST_OWNER_PROTECTED` rejection (§9.1). Listing it among the guarded
> operations is what created the earlier contradiction, and the API guard must not be weakened to
> accommodate it.

With the lock, request 2 blocks until request 1 commits, then re-counts and sees `1`, so removing
B would reach zero and is rejected. **The population can never reach zero through the API.**

A database constraint cannot express "at least one row matching a join", so the guard is
application-side plus an integration test. `recover-owner` (§9) exists precisely so this guard
never has to be relaxed for support reasons.

An Owner in `PENDING_SETUP` does **not** count toward the population: an account nobody can log
into is not a safeguard against lockout.

**Scope of this guard: the API and UI only.** The local break-glass `recover-owner` command is
the single documented exception (§9.1); it never removes the `Owner` role, and the zero-active
window it opens is deliberate, secret-authenticated, audited, and closed atomically by token
consumption. No authenticated HTTP request can reach that state.

## Alternatives considered

- **JWT bearer tokens in `localStorage`** — the reflexive choice for an SPA, and the wrong one
  here. Tokens in JS-readable storage are XSS-exfiltratable, revocation requires a denylist
  (i.e. server state, which was the thing tokens were supposed to avoid), and refresh rotation
  adds a whole subsystem. Same-origin deployment removes the only real advantage bearer tokens
  would have offered.
- **JWT in an HttpOnly cookie** — this is a cookie with extra steps: the same transport, plus
  self-managed signing keys and expiry semantics that Identity already handles.
- **External IdP (Auth0, Entra ID, Google)** — good for organizations that already have one, and
  rejected here: an external dependency, a bill and an onboarding flow for a handful of users,
  plus a hard dependency on internet reachability of a third party for the shop to work.
- **ASP.NET Core Identity API endpoints (`MapIdentityApi`)** — bearer/token-oriented and shaped
  around a generic contract; the flows here are few and benefit from explicit endpoints.
- **Full multi-tenancy now** — explicitly excluded by the brief, and the right exclusion.
- **HTTP Basic auth behind a reverse proxy** — cheap, and rejected: no per-user attribution, no
  lockout, no logout, no roles.

Bootstrap alternatives (B-004):

- **Seeded default credential** (`admin/admin`) — rejected outright. It is a published backdoor,
  and this specification is the publication.
- **A random password generated at first startup and written to the log** — better, still
  rejected: logs are aggregated, shipped and retained, so the credential spreads to wherever
  logs go and stays there.
- **A first-run HTTP setup wizard, open until the first Owner exists** — the common pattern, and
  rejected here: between deployment and setup there is an anonymous, internet-reachable endpoint
  that creates an administrator. A slow first login, a crashed setup, or an attacker who finds
  the host first all lose the system. The CLI keeps that capability on the host.
- **`--password` on the command line** — rejected: shell history, `ps`, container logs.
- **Interactive password prompt only** — good on a real console, rejected as the *only* option
  because `docker exec` in a pipeline is not interactive. The token flow works in both, so it is
  the primary mechanism; an interactive prompt may be added as a convenience.

## Consequences

**Positive:** small, well-trodden implementation; XSS cannot steal the session; sessions are
revocable; audit attribution is real from day one; the path to more roles is a mapping edit.

**Negative:**
- Cookies require CSRF defense, which bearer tokens would not — handled by SameSite plus
  antiforgery, and this trade is favourable.
- Same-origin deployment becomes an architectural constraint: a future separately-hosted
  frontend or a native mobile client would need either a proxy or a revisit of this ADR.
  Recorded as an accepted constraint rather than a surprise.
- Adding multi-tenancy later is real work. Bounded, deliberate, and cheaper than carrying it
  unused.

## Compliance checks

- Test: every endpoint either requires authorization or appears on the reviewed anonymous
  allow-list (`/health/live`, login, `/setup-account`, static assets).
- Test: an unauthenticated request to any `/api/*` business endpoint returns 401.
- Test: a `Viewer` receives 403 on every write endpoint.
- Test: deactivating a user invalidates their session within the security-stamp interval.
Full Given/When/Then in [ROADMAP S1](../ROADMAP.md#s1-acceptance-test-contracts-mandatory):
`ConcurrentBootstrapSingleWinner`, `BootstrapRefusedOnceOwnerExists`,
`BootstrapSecretRejectedWhenWrong`, `BootstrapSecretNeverLogged`,
`ConcurrentSetupTokenSingleConsumer`, `SetupTokenSingleUseAndExpiry`,
`SetupTokenStoredOnlyAsHash`, `PendingSetupCannotLogin`,
`ConcurrentLastOwnerRemovalNeverZero`, `RecoveryInvalidatesPreviousRecoveryToken`,
`BootstrapExpectedAndCandidateSecretsAreDistinctSources`, `RecoverOnlyOwner`,
`ApiCannotDeactivateOnlyOwner`,
`RecoveryInvalidatesSessions`, `NoSeededUserInAnyMigration`, plus the endpoint
authorization tests above.
