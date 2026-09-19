# OPERATIONS — Verce 3D | Laboratório de Custos

Operational runbook: deployment secrets, first installation, key management, backup/restore,
rotation, and the outbox.

> ### These are architecture contracts, not existing features
>
> **S0 contains documentation only. No command described here exists yet.** Every CLI invocation
> below is an **S1 implementation contract**: S1 SHALL implement a command with that name and
> that behaviour. Read "the command verifies X" as "the command SHALL verify X".
>
> Built in **S1**: `bootstrap-owner`, `recover-owner`, `recover-data-protection`,
> `migrate`, the outbox jobs and the diagnostics endpoint.
> Built in **S16**: backup jobs, retention jobs, the restore drill and the audit/outbox UI.

Companion documents: [SECURITY](SECURITY.md) ·
[ADR-0008](architecture/ADR-0008-ai-integration-and-secret-handling.md) ·
[ADR-0009](architecture/ADR-0009-authentication-strategy.md) ·
[ADR-0012](architecture/ADR-0012-domain-events-and-outbox.md)

---

## 1. Deployment secrets

Four secrets exist outside the database. **None may ever be committed, logged, or placed in
`appsettings.json`.** File mounts are preferred over environment variables: environment
variables appear in `docker inspect`, in `/proc/<pid>/environ`, and in crash dumps.

| Secret | Purpose | Delivery | Required |
|---|---|---|---|
| `ConnectionStrings__Verce` | PostgreSQL | env or file | always |
| **Data Protection certificate ring** (`.pfx` + password each) | wraps the key ring (§3) | **file mount** | Production |
| `VERCE_BOOTSTRAP_SECRET_FILE` → a mounted file | the **expected** operand for `bootstrap-owner` | **file mount** | Production |
| `VERCE_RECOVERY_SECRET_FILE` → a mounted file | the **expected** operand for `recover-owner` | **file mount** | Production |

The bootstrap and recovery secrets are **deliberately different values in different files**. They
grant different capabilities at different times, and leaking one must not grant the other.

> **Each secret has two operands.** The file above holds the **expected** value. The
> **candidate** is typed by the administrator into a silent, no-echo prompt when the command
> runs. Comparing the file against itself would authenticate nobody — possession of the host
> would be sufficient. See
> [ADR-0009 §6.2](architecture/ADR-0009-authentication-strategy.md#62-the-secret-challenge--two-operands-from-two-sources).

Suggested layout:

```
/run/secrets/verce-db-connection
/run/secrets/verce-dataprotection.pfx        (0400, owned by the app user)
/run/secrets/verce-dataprotection-password
/run/secrets/verce-bootstrap-secret
/run/secrets/verce-recovery-secret
```

Configuration keys (**one vocabulary, used everywhere**):

| Key | Meaning |
|---|---|
| `DataProtection:CurrentCertificatePath` | the certificate **new** DP keys are wrapped with |
| `DataProtection:UnprotectCertificatePaths[]` | the **ring**: current + every retained predecessor |
| `DataProtection:CertificatePasswordFile` | password for the PKCS#12 files |
| — | *(the superseded names `DataProtection:CertificatePath` and `DataProtection:PreviousCertificatePaths` are **not** used anywhere)* |
| `DataProtection:CurrentCertificateThumbprint` | optional pin |

`CertificatePath` and `PreviousCertificatePaths` are **not** used — they were earlier names for
the same two concepts and are retired to avoid ambiguity.

---

## 2. First installation

No user exists on a fresh database, and **no default credential is seeded**
([ADR-0009 §6](architecture/ADR-0009-authentication-strategy.md)).

*S1 implementation contract — these commands do not exist yet.*

```bash
# 1. Bring up the database and apply migrations
docker compose up -d db
docker compose run --rm api dotnet Verce.Api.dll migrate

# 2. Create the first Owner (refuses if one already exists)
docker compose run --rm api dotnet Verce.Api.dll bootstrap-owner \
    --email owner@example.com --name "Nome do Proprietário"
```

Output — printed **once**, never logged:

```
Owner created: owner@example.com
Setup URL (valid for 30 minutes, single use):
  https://<host>/setup-account?token=<opaque-token>
```

3. Open the URL and set a password. The token is consumed, the `SecurityStamp` is regenerated,
   and any other outstanding tokens for that account are invalidated.

**Concurrency.** `bootstrap-owner` takes `pg_advisory_xact_lock(8401001)` before counting
Owners. Two simultaneous invocations on an empty database therefore serialize: one creates the
Owner, the other blocks, then observes the Owner and exits non-zero. **Two first Owners can
never be created.** Counting without the lock was the defect — there are no rows to lock on an
empty database ([ADR-0009 §6.1](architecture/ADR-0009-authentication-strategy.md#61-serialization-on-a-stable-resource)).

**The bootstrap secret is never a CLI argument, and never compared against itself.** The
**expected** value is read from the file at `VERCE_BOOTSTRAP_SECRET_FILE`; the **candidate** is
typed at a silent no-echo prompt (`Enter bootstrap secret:`) when the command runs. They are
compared with `FixedTimeEquals`, before any lock or database work. A rejection prints
`bootstrap secret rejected` — it does not distinguish missing from wrong — and is audited.

This means **Production bootstrap requires local interactive execution**. Non-interactive
bootstrap is deliberately not supported in v1.

**Verification after setup**

- `bootstrap-owner` now refuses (`an Owner already exists`) and exits non-zero.
- `platform.account_setup_token` holds only a SHA-256 hash with `consumed_at` set.
- An `audit_log` row exists with `source = CLI`.

**If the token expires before it is used**, `bootstrap-owner` will now refuse — the Owner row
exists, even though it has no usable password. Use `recover-owner` (§2.2) to issue a fresh setup
token for that account. This is deliberate: the bootstrap command's only guard is "does an Owner
exist", and weakening it to "does an Owner have a password" would leave a second window in which
anyone with shell access could seize an account.

### 2.1 Routine password reset

An `Owner` resets any user from the UI: a `RESET` token is issued, the user's `SecurityStamp` is
regenerated, and their sessions stop working within the validation interval (30 min). Another
`Owner` resets an `Owner` the same way.

### 2.2 Last-Owner lockout — break glass

When the only `Owner` cannot log in, recovery is **local and offline**. It is exposed through no
HTTP route at all.

```bash
# S1 implementation contract
docker compose run --rm api dotnet Verce.Api.dll recover-owner --email owner@example.com
```

Requires the recovery secret — **expected** from `VERCE_RECOVERY_SECRET_FILE`, **candidate** from
a silent prompt, a **different value** from the bootstrap secret. Inside
`pg_advisory_xact_lock(8401002)` it invalidates **every** prior unconsumed token for that account,
clears the password, sets `setup_status = PENDING_SETUP` and `recovery_started_at = now()`,
regenerates the `SecurityStamp` (terminating every active session), issues **exactly one**
`RECOVERY` token, prints the URL once, and writes an audit row.

**It works when there is only one Owner — that is what it is for.** The `Owner` role is
**never removed**, so the account remains the designated Owner while temporarily
non-login-capable. `LAST_OWNER_PROTECTED` guards the **API surface** and does not apply to this
local, secret-authenticated break-glass path
([ADR-0009 §9.1](architecture/ADR-0009-authentication-strategy.md#91-recovering-the-sole-owner--the-break-glass-exception)).
Consuming the token restores `ACTIVE` atomically, and the system is back to one active Owner.

Repeated or concurrent recoveries therefore leave **exactly one valid token — the newest**. Its
use is loud in the audit log by design.

**The system never permits removing, deactivating or demoting the last active Owner through the
API or UI** (`LAST_OWNER_PROTECTED`). `recover-owner` exists so that guard never has to be
relaxed for support reasons.

---

## 3. Data Protection key ring

Two different things protect two different assets, and they must not be conflated:

| Asset | Protected by |
|---|---|
| Auth cookies, the encrypted OpenAI API key, other protected settings | **Data Protection keys** (rows in `platform.data_protection_keys`) |
| Those Data Protection keys, at rest | **The wrapping certificate** (a file on the host) |

### 3.1 Configuration

```csharp
services.AddDataProtection()
        .SetApplicationName("Verce3D")                    // must be stable forever
        .PersistKeysToDbContext<VerceDbContext>()         // → platform.data_protection_keys
        .ProtectKeysWithCertificate(current)              // wraps NEW keys
        .UnprotectKeysWithAnyCertificate(ring)            // the RING: current + every predecessor
        .SetDefaultKeyLifetime(TimeSpan.FromDays(90));
```

**Model a ring, never a single certificate.** Historical DP keys may have been wrapped by
different certificates over time, and the application must hold enough material to unwrap
**every key still needed** — not merely the newest. `ring` is `[current] + every retained
predecessor`, from `DataProtection:UnprotectCertificatePaths[]` or a mounted ring directory.

`SetApplicationName` is load-bearing: Data Protection derives purposes from it, so changing it
invalidates every existing cookie and makes the encrypted API key undecryptable.

The table is `platform.data_protection_keys`, whose shape is imposed by the framework's EF
repository (`int` identity PK, `FriendlyName`, `Xml`) — a **Category 3 technical table** under
[ADR-0011](architecture/ADR-0011-identifiers-and-concurrency.md), exempt from the UUID rule, and
declared in [DATA-MODEL §1](DATA-MODEL.md#1-platform-schema).

### 3.2 Fail closed

If the wrapping certificate is missing, unreadable, or its password is wrong, the application
**must not start** in Production.

Specifically, the default behaviour of silently generating a fresh, unprotected key ring is
**disabled**. That default is dangerous here: the app would appear healthy, every user would be
logged out, and the stored API key would be silently undecryptable while the system reported
success. Instead:

- **startup validation fails**, with an explicit message naming the missing path and the
  recovery command;
- **the process exits non-zero. The web host never binds HTTP.**

> **Corrected 2026-09-07 (re-gate B-RG2-003).** The previous text also claimed
> `/health/ready` returns 503 in this scenario. **That is impossible** — if the host never
> started, nothing answers the probe at all; a caller sees a connection failure, not a 503.
> Startup validation and runtime readiness are different mechanisms and are no longer conflated.

| | Mechanism | Observable |
|---|---|---|
| Required certificate missing/unreadable **at startup** | startup validation | process exits non-zero; **no HTTP listener**; the orchestrator sees the container fail |
| A runtime dependency becomes unavailable **after** a successful start (database, storage, dispatcher stalled) | readiness probe | `/health/ready` → **503** |

`/health/ready` therefore has **no** certificate condition: if the certificate were unusable the
host would not be running to answer. Recovery is the offline command in §5.2, which is not the
web host and so is unaffected by the host refusing to start.

Development is the deliberate exception (§7).

---

## 4. Backup

A complete, restorable backup is **three artifacts**, and two of them are not the database:

| # | Artifact | Contains | Where |
|---|---|---|---|
| 1 | `pg_dump` (custom format) | all business data, encrypted API key, **encrypted** key-ring rows | backup target |
| 2 | Document/asset storage | generated PDFs, rendered HTML, brand asset files | backup target |
| 3 | **Wrapping certificate + password** | the ability to decrypt #1's key ring | **a separate secret store / password manager** |

> **Artifact 3 must not be stored with artifacts 1 and 2.** Putting the certificate in the same
> bucket as the database dump reassembles the plaintext for anyone who steals that bucket, which
> is exactly the security theatre [ADR-0008](architecture/ADR-0008-ai-integration-and-secret-handling.md)
> forbids.

Nightly job (S16), 30 daily + 12 monthly retained. The certificate is backed up **once, when it
is created or rotated** — it does not change nightly.

A backup is not a backup until a restore has been tested (§5.1 drill, S16).

### 4.1 Brand-asset storage consistency

Brand asset binaries live in the configured `BrandAssets:StorageRoot`, outside the web root;
their metadata and immutable version rows live in PostgreSQL. Upload first normalizes and writes
the content-addressed file, then commits the version metadata in the same Unit of Work as the
asset's active-version change. PostgreSQL and the filesystem are not one ACID resource. A request
always removes its private `.tmp` file, but never deletes a promoted canonical hash inline after a
database failure: another concurrent transaction may already reference that same hash. A failed
transaction can therefore leave a recoverable orphan canonical blob; this is safer than deleting
committed content. Future garbage collection must prove that no committed
`settings.brand_asset_version.file_path` references a hash before deleting it.

`BrandAssets:StorageRoot` is mandatory in Production, must be an absolute path and is validated
before HTTP is served. Development/Test may use the local application-directory fallback. The
Compose `api` service mounts the named volume `verce_brand_assets` at
`/var/lib/verce/brand-assets`; removing or recreating the application container must preserve that
volume. Never run `docker compose down -v` during an application-container restart or recovery.

The filesystem and PostgreSQL cannot share one transaction. Operators therefore back up and
restore both artifacts together, and should periodically report content-addressed files with no
`settings.brand_asset_version.file_path` reference as recoverable orphan candidates. Never delete
an asset-version row or a referenced file: versions are historical document evidence.

---

## 5. Restore

### 5.1 Full restore (database + files + certificate)

```bash
pg_restore --clean --if-exists -d verce verce-YYYYMMDD.dump
# restore document/asset storage to the configured path
# mount the SAME wrapping certificate
docker compose up -d api
```

Everything works: sessions issued before the dump remain valid, and the OpenAI API key decrypts.

### 5.2 Restore **without** the wrapping certificate

> **Corrected 2026-09-07 (re-gate E).** The previous text told the operator to "delete the
> undecryptable key-ring rows" as a casual step, while §3.2 requires the application to **fail
> closed** when it cannot read the ring. Those two instructions contradicted each other and left
> the operator with an application that refuses to start and no sanctioned way forward. Recovery
> is now an explicit offline command, and it archives rather than deletes.

**Expected behaviour on startup:** the application **fails closed**. It detects a key ring it
cannot unwrap and refuses to serve, naming the recovery command in the error. This is correct —
starting with a silently regenerated ring would log every user out and leave the API key
undecryptable while reporting success.

The operator is **not** stuck, because recovery runs offline, outside the web host:

```bash
# S1 implementation contract — the command does not exist yet
docker compose run --rm api dotnet Verce.Api.dll recover-data-protection     --confirm-destroy-secrets
```

Requires the recovery secret — **expected** from the mounted file at
`VERCE_RECOVERY_SECRET_FILE`, **candidate** from a silent no-echo prompt, compared in constant
time — plus local host access. It takes advisory lock `8401003` and:

1. **verifies the ring genuinely cannot unwrap the existing keys** — and refuses if it can, so
   the command cannot be used casually;
2. **copies** the unreadable rows into `platform.data_protection_key_archive`, then removes them
   from the live table — **archived, never deleted**, so a certificate found next week can still
   recover them;
3. initializes a new key ring wrapped with the current certificate;
4. clears `ai_settings.api_key_encrypted` and `api_key_last_four`, sets `is_enabled = false` and
   `api_key_recovery_required = true`;
5. regenerates `SecurityStamp` for every user;
6. writes an audit row (`source = CLI`) naming the operator and the archived key count.

The application then starts normally. An `Owner` logs in and re-enters the OpenAI key.

**Consequences, to state plainly during the incident:**

| Consequence | Detail |
|---|---|
| **All auth cookies become invalid** | Every user logs in again. **Passwords are unaffected** — they are Identity hashes, not Data Protection payloads. |
| **The OpenAI API key is unrecoverable** | An `Owner` obtains a key from the provider and re-enters it. The UI shows the re-entry prompt because `api_key_recovery_required = true`. |
| **Any other DP-protected value is unrecoverable** | Currently only the API key; any future protected setting inherits this. |
| Everything else is intact | Business data, documents and assets restore normally. |

**The API key is the only item a database-plus-files restore cannot recover.** That is the
accepted, documented cost of keeping the wrapping material out of the backup.

---

## 6. Rotation — two distinct procedures

These are frequently confused. They are not the same operation.

### 6.1 Data Protection key rotation (automatic, routine)

Data Protection creates a new key every 90 days on its own. **Old keys are never deleted** — they
are needed to decrypt anything protected while they were current. No intervention is required,
and pruning `platform.data_protection_keys` is never a maintenance action.

### 6.2 Wrapping certificate rotation (manual, rare)

> **Corrected 2026-09-07 (re-gate B-RG2-003).** Two claims were removed. The first — keeping the
> old certificate "for at least one full key lifetime (90 days)" — treated age as a dependency
> check, which it is not. The second — that `reprotect-secrets` moved payloads onto a DP key
> wrapped by the new certificate — was **factually wrong**: changing the current certificate does
> not make `Protect` select a *new* DP key. Protect uses the key ring's active key, chosen by key
> lifetime, so re-protecting immediately after a swap would most likely re-protect under the
> **same** key, still wrapped by the **old** certificate. `reprotect-secrets` is removed from v1.

**v1 retains every certificate.** There is no automatic retirement, no dependency garbage
collection, and no re-wrapping of existing DP keys — the framework offers no supported API for it.

```
1. Mount the NEW certificate ALONGSIDE all retained predecessors — never replacing them.
2. Configure:
       DataProtection:CurrentCertificatePath      = new
       DataProtection:UnprotectCertificatePaths[] = [new, …every predecessor]
3. Restart. Startup validation must pass — this proves the ring opens the existing keys.
4. Verify explicitly that a historical protected payload (the OpenAI key) still decrypts.
5. OPTIONAL, so new protections use a key wrapped by the new certificate:
       force a new Data Protection key with the supported key-manager API
       IKeyManager.CreateNewKey(activationDate, expirationDate)
   This CREATES a key. It does NOT re-encrypt existing keys, and nothing in v1 does.
6. KEEP every predecessor certificate. Update the secret store to hold all of them.
```

**Old certificates are never deleted in v1.** Any retained ciphertext may still depend on a DP
key one of them wraps, and v1 has no mechanism to prove otherwise. Retaining a few small files
indefinitely is cheap; deleting one that was still needed is unrecoverable.

Rotate when a certificate is expiring or may have been exposed. In the exposure case also revoke
and re-enter the OpenAI key at the provider — rotation changes what wraps future keys, not a
secret an attacker may already have read.

## 7. Development environment

Production-grade secret infrastructure must not be required to run the app locally.

```csharp
if (env.IsDevelopment())
    services.AddDataProtection()
            .SetApplicationName("Verce3D")
            .PersistKeysToFileSystem(new DirectoryInfo(".dataprotection"));
    // no certificate, keys unencrypted at rest — local only
```

- `.dataprotection/` is **git-ignored**.
- Keys persist across restarts, so a developer is not logged out on every rebuild.
- A locally entered OpenAI key stays decryptable across restarts.
- **The unprotected fallback is permitted only in `Development`.** In Production the absence of a
  certificate is a startup failure (§3.2), never a downgrade. An architecture/configuration test
  asserts the Production path cannot select the file-system provider.

| | Development | Production |
|---|---|---|
| Key ring storage | file system (`.dataprotection/`) | `platform.data_protection_keys` |
| Key encryption | none | X.509 certificate from a file mount |
| Missing wrapping material | starts normally | **fails to start** |
| Bootstrap secret | optional | required |

### 7.1 E2E harness — disposable PostgreSQL target

The Playwright suite under `tests/e2e/` is destructive (it creates/drops real databases and runs
real SQL) and requires its own isolated PostgreSQL container — **never** `verce-postgres`, and
never the `verce` database on any server. `VERCE_E2E_POSTGRES_CONTAINER` is mandatory (no
fallback), ambiguous connection strings are refused rather than guessed, and the protected
container is rejected by name, full Docker ID or short Docker ID alike. See
[`tests/e2e/README.md`](../tests/e2e/README.md) for the full local-workflow and CI contract.

---

## 8. Outbox operations

Full lifecycle in [ADR-0012 Part II](architecture/ADR-0012-domain-events-and-outbox.md).

### 8.1 Health impact — business failure vs infrastructure failure

> **Corrected 2026-09-07 (re-gate governance).** A single failed PDF render must **not** make the
> application unready: an orchestrator would kill and replace a perfectly healthy container, in a
> loop, and the PDF still would not render.

| Condition | Health | HTTP |
|---|---|---|
| One or more `FAILED` messages with `disposition = ACTIVE` | `degraded` | **200** |
| An **eligible** message waiting past `outbox.stuck_threshold_minutes` (default **30**) | `unhealthy` | **503** |

The second is different in kind: it means **nothing is draining the queue**, which is an
infrastructure fault in the dispatcher itself, not a business failure in one message.

> **Corrected 2026-09-07 (re-gate H-RG2-002).** "Oldest `PENDING`" produced **false stalls**: a
> message waiting out a 2-hour backoff, or scheduled for the future, is the system working as
> designed — but it would have driven readiness to 503 and triggered a container-replacement
> loop.

**Eligible** means the dispatcher should be able to take it *right now*:

```sql
status = 'PENDING' AND available_at <= now() AND attempt_count < max_attempts
```

and the metric is **how long it has waited past becoming eligible**, not its age:

```sql
SELECT max(now() - available_at)
FROM platform.outbox_message
WHERE status = 'PENDING' AND available_at <= now() AND attempt_count < max_attempts;
```

A future `available_at` contributes nothing. `created_at` is deliberately unused — it would make
a message look "old" merely because it has been retried.

Dismissed failures (§8.5) stop counting toward `degraded`, so triaged problems stop generating
noise without being erased.

### 8.2 Inspecting failures

`GET /api/platform/outbox?status=FAILED` (Owner only, audited) returns event type, attempt count,
`last_error`, `correlation_id` and the aggregate reference. Every entry is joinable to
`audit_log` by `correlation_id` to see the originating command.

### 8.3 Requeue

Fix the underlying cause **first**; requeuing an unfixed poison message simply fails again.

Requeue (`Owner`, reason required) **preserves message identity** — same `id`, same
`idempotency_key`, so consumer-side deduplication still holds. It opens a **new execution
generation**, which is what keeps the new round’s attempt history from colliding with the
previous one:

| Field | On requeue |
|---|---|
| `status` | → `PENDING`, `available_at = now()` |
| **`execution_generation`** | **incremented** — opens a new retry round |
| `attempt_count` | **reset to 0** — a fresh budget for that round |
| `max_attempts` | unchanged |
| `failure_disposition` | → `NULL` |
| `id`, `idempotency_key` | **unchanged** — the same business effect |
| `last_error` | **kept** |
| `failed_at`, `failure_disposition` | cleared — the row is no longer terminal |
| `outbox_message_attempt` rows | **all retained** |

**Failure evidence is never deleted.** A message requeued without fixing the cause fails again,
and the attempt history shows both rounds.

### 8.5 Dismissal — closing a failure without retrying it

Some failures should not be retried: a document for a quote that was since canceled, an AI run
for a period no longer of interest. `Owner` marks the message
`failure_disposition = DISMISSED` with `dismissed_by`, `dismissed_at` and a required
`dismissal_reason`.

Dismissal **does not delete anything** — not the message, not `last_error`, not the attempt
history. It records a human decision alongside the facts, and removes the message from the
`degraded` count. A `DISMISSED` message is `FAILED`, and the claim query only looks at `PENDING`,
so it is **not claimable**.

There is no `RESOLVED` disposition. A message that is requeued and then succeeds ends at
`status = PROCESSED` with `failure_disposition = NULL` — **success is what "resolved" means**,
and the history lives in `outbox_message_attempt`, keyed by
`(message, execution_generation, attempt_number)` so a requeued round never collides with the
previous one.

### 8.4 Retention

| State | Retention |
|---|---|
| `PROCESSED` | **90 days**, pruned with its attempt rows |
| `FAILED`, `disposition = ACTIVE` | **never auto-deleted** |
| `FAILED`, `disposition = DISMISSED` | 365 days from `dismissed_at` |
| `PENDING` / `PROCESSING` | never pruned |

> The retention job must never delete an unresolved failure. Deleting evidence of a problem
> nobody has looked at is worse than the storage it saves.

---

## 9. Health endpoints

| Endpoint | Auth | Returns |
|---|---|---|
| `/health/live` | anonymous | process is running |
| `/health/ready` | anonymous | **a status word only**: `healthy` / `degraded` / `unhealthy` |
| `/api/platform/health` | `Owner` | the component breakdown — database, key ring, Chromium, outbox age and failure counts |

`/health/ready` is the orchestrator's routing signal, so it cannot require authentication. For
that reason it exposes **no** component names, versions, error text or counts — that detail
would help an attacker and belongs behind auth
([SECURITY §3.2](SECURITY.md#32-mechanism)).

**Status → HTTP mapping (frozen):**

| Status | HTTP | Meaning | Orchestrator behaviour |
|---|---|---|---|
| `healthy` | **200** | everything nominal | route traffic |
| `degraded` | **200** | the app can serve every request, but something needs a human | **route traffic**; alert only |
| `unhealthy` | **503** | the app cannot serve correctly | stop routing / replace |

> **`degraded` returns 200 on purpose.** Mapping it to 503 would let one failed PDF trigger a
> container replacement loop that cannot possibly fix the PDF. Degradation is an *alerting*
> signal, not a *routing* signal.

| Condition | Status |
|---|---|
| Database unreachable | `unhealthy` |
| *(certificate/key ring unusable)* | **not a readiness condition** — startup fails and the host never binds HTTP (§3.2) |
| Document storage unwritable | `unhealthy` |
| Chromium unavailable | `unhealthy` |
| Outbox dispatcher stalled (an **eligible** message waiting > 30 min past eligibility) | `unhealthy` |
| Unresolved `FAILED` outbox messages | `degraded` |
| Certificate within 30 days of expiry | `degraded` |

---

## 10. Operational checklist for a new deployment

- [ ] All secrets mounted as **files**, permissions `0400`, owned by the app user; bootstrap and recovery secrets are **different values in different files**.
- [ ] `DataProtection:CurrentCertificatePath` and every entry of
      `DataProtection:UnprotectCertificatePaths[]` resolve; the app starts (proving §3.2).
- [ ] Migrations applied.
- [ ] `bootstrap-owner` run; setup URL consumed; token shows `consumed_at`.
- [ ] `bootstrap-owner` now refuses.
- [ ] Wrapping certificate stored in the secret store, **separately from database backups**.
- [ ] Backup job scheduled; **a restore drill executed** (S16).
- [ ] `/health/ready` green.
- [ ] Certificate expiry recorded, with a renewal reminder well ahead of expiry.
- [ ] **Every** certificate retained, current and predecessors alike. v1 never retires one.
