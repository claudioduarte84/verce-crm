# ADR-0008 — AI Integration and Secret Handling

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-06
- **Sprint:** S0

## Context

The product integrates OpenAI to produce business insights: most profitable products, margins
deteriorating, best channels, pricing opportunities, abnormal costs, consumption variation,
low-turnover items, kit opportunities.

Hard constraints from the brief:

- the API key **never** goes to the frontend;
- the key is **never** logged;
- the key is **never** stored in plaintext;
- only necessary data is sent; no secrets, no unnecessary personal data;
- the user can edit the API token, the model and the default prompt;
- initial models are configurable (`gpt-5.4`, `gpt-5.4-mini`).

## Decision

### 1. All AI calls are server-side

The browser never talks to OpenAI. The frontend calls `/api/ai/insights/run`; the backend
builds the payload, calls the provider, stores the result, and returns stored results. There is
no proxy endpoint that forwards an arbitrary user prompt to the provider — the prompt is the
configured template plus a system-built dataset, so the endpoint cannot be used as an open
relay.

### 2. Key storage

- `ai.ai_settings.api_key_encrypted bytea`, encrypted with ASP.NET Core Data Protection
  (`IDataProtector`, purpose `"Verce.AiApiKey"`).
- `api_key_last_four` stored separately in plaintext, so the UI can display `sk-…4f2a` and the
  operator can tell which key is configured.

> **Corrected 2026-09-07 (gate blocker B-005).** This ADR previously said the Data Protection key
> ring was "persisted in the database and protected at rest by a host-held key", without naming
> a repository, a protector, a backup procedure or a rotation procedure. As written it permitted
> the **security-theatre configuration the reviewer identified**: the database holding both the
> encrypted API key *and* an unprotected key ring capable of decrypting it, so a database dump
> alone would yield the secret. The operational model is now concrete and is specified in
> [SECURITY §5.1](../SECURITY.md#51-storage) and
> [OPERATIONS](../OPERATIONS.md).

The key ring is persisted with `PersistKeysToDbContext<VerceDbContext>()` into
`platform.data_protection_keys`, and **each key is itself encrypted** with
`ProtectKeysWithCertificate(...)` using an X.509 certificate whose private key lives **outside
the database**, mounted as a file secret on the host.

**The wrapping material must never be inside the artifact it protects.** A database backup
therefore contains encrypted key-ring rows and an encrypted API key, and nothing that can
decrypt either.

**Residual risk, stated honestly:** an attacker who obtains *both* the database *and* the
mounted certificate recovers the API key. Defending past that needs a KMS/HSM, which is
disproportionate for a single-container deployment. What changed with this correction is that a
**database-only** compromise — the realistic one, via a backup file, a dump, or a stolen
snapshot — no longer yields the key.

### 2.1 The dependency chain (re-gate correction E)

Recovering the API key requires **three** things, and stating the chain explicitly is what makes
the rotation and restore procedures checkable:

```
ai_settings.api_key_encrypted        (ciphertext, in the database)
        │  protected by
        ▼
a Data Protection KEY                (row in platform.data_protection_keys)
        │  wrapped by
        ▼
an X.509 WRAPPING CERTIFICATE        (file on the host, never in the database)
```

Every link must be present. A backup that restores the first two and not the third does not
restore the secret.

### 2.2 A certificate **ring**, not a single certificate

Historical Data Protection keys may have been wrapped by *different* certificates over time. At
any moment the application must hold enough unwrapping material to open **every DP key that is
still needed**, not merely the newest one.

| Configuration | Meaning |
|---|---|
| `DataProtection:CurrentCertificatePath` | the certificate **new** DP keys are wrapped with |
| `DataProtection:UnprotectCertificatePaths[]` (or a mounted ring directory) | **every** certificate that may still be required to unwrap an existing DP key — always includes the current one |

```csharp
services.AddDataProtection()
        .SetApplicationName("Verce3D")
        .PersistKeysToDbContext<VerceDbContext>()
        .ProtectKeysWithCertificate(current)
        .UnprotectKeysWithAnyCertificate(ring)      // current + every retained predecessor
        .SetDefaultKeyLifetime(TimeSpan.FromDays(90));
```

Modelling only `current_certificate` was the defect: it implies the previous certificate can be
discarded once the new one is in place, which destroys every DP key still wrapped by it.

### 2.3 Certificate rotation — v1 contract

> **Corrected 2026-09-07 (re-gate B-RG2-003).** The previous text introduced a `reprotect-secrets`
> command and used it to justify eventually retiring an old certificate. That reasoning was
> wrong on a factual point: changing the *current* certificate does **not** cause
> `IDataProtector.Protect` to start using a **new Data Protection key**. Protect uses the
> key ring's active key, which is chosen by key lifetime — not by which certificate is
> configured. Re-protecting a payload right after a certificate swap would very likely re-protect
> it under the **same** DP key, still wrapped by the **old** certificate, achieving nothing while
> appearing to license deleting that certificate. `reprotect-secrets` is **removed from v1**.

**What v1 does:**

| Concern | v1 |
|---|---|
| New DP keys | wrapped by the **current** certificate |
| Existing DP keys | unwrapped by the **ring** — current plus every retained predecessor |
| Re-wrapping existing DP keys | **not implemented** — no supported framework API exists |
| Retiring an old certificate | **not implemented** — predecessors are retained |
| Certificate dependency garbage collection | **not implemented** |

```
1. Mount the new certificate ALONGSIDE all retained predecessors — never replacing them.
2. Configure  DataProtection:CurrentCertificatePath      = new
              DataProtection:UnprotectCertificatePaths[] = [new, …every predecessor]
3. Start. Startup validation must pass (§2.4) — this proves the ring opens the existing keys.
4. Verify explicitly that a historical protected payload (the OpenAI key) still decrypts.
5. OPTIONAL: force a new DP key with the supported key-manager API
       IKeyManager.CreateNewKey(activationDate, expirationDate)
   so that new protections use a key wrapped by the new certificate.
6. KEEP every predecessor certificate.
```

> **Step 5 creates a new key. It does not re-encrypt old keys**, and nothing in v1 does. Any
> statement that rotation "migrates" or "re-wraps" existing key material would be false.

**Consequence, accepted deliberately:** old wrapping certificates may need to be retained
**indefinitely**, because any retained ciphertext may still depend on a DP key one of them wraps.
Retaining a few small files forever is cheap; losing one is unrecoverable. Certificate lifecycle
tidiness is cosmetic — decryptability is not.

### 2.4 Disaster recovery without the wrapping certificate

**In Production, a missing or unreadable required certificate means the web host does not
start** — the process exits non-zero, having never bound HTTP. Fail-closed startup and disaster
recoverability appear to conflict; they are reconciled by putting recovery in an **offline
command** that is not the web host. The application still refuses to serve, and the operator is
never locked out of fixing it.

**S1 SHALL implement** `verce recover-data-protection` — deliberately destructive, and it says
so before acting:

```
requires: local host access
        + the recovery secret: expected from the file at VERCE_RECOVERY_SECRET_FILE,
          candidate from a silent no-echo prompt, constant-time compared
        + explicit --confirm-destroy-secrets
takes   : pg_advisory_xact_lock(8401003)
does    :
  1. verify the existing key ring genuinely cannot be unwrapped by the configured ring
     (refuse if it can — this command must not be used casually);
  2. COPY the unreadable rows into platform.data_protection_key_archive, then remove them
     from the live table — archived, never deleted, so a certificate found later can still
     recover them;
  3. initialize a new key ring wrapped with the current certificate;
  4. clear ai_settings.api_key_encrypted and api_key_last_four,
     set is_enabled = false and api_key_recovery_required = true;
  5. regenerate SecurityStamp for every user (all sessions die; passwords are unaffected —
     they are Identity hashes, not DP payloads);
  6. write an audit row (source = CLI) naming the operator and the archived key count.
after   : the application starts normally; an Owner logs in and re-enters the OpenAI key.
```

Step 2 is what keeps this safe: the operation is recoverable in the other direction too. If the
certificate turns up next week, the archived rows are still there.

### 3. Never exposed

- **No endpoint returns the key.** The settings DTO carries `apiKeyLastFour` and
  `apiKeyUpdatedAt` only. The write endpoint accepts a key and returns a mask.
- An **architecture test** asserts no type reachable from an API response contract has a
  property named `ApiKey`, `ApiKeyEncrypted`, `Token`, `Secret`, `Password` or `ConnectionString`.
- The key is decrypted only inside `AiClient`, held in a local, and never stored in a field, a
  cache, an exception message or a log scope.

### 4. Never logged

Serilog carries a redaction enricher masking any property matching
`apikey|api_key|token|secret|password|authorization|connectionstring` (case-insensitive).
HTTP client logging for the OpenAI client has header logging disabled. Failed calls log status
code and request id — never headers, never the body.

### 5. Data minimization

`AiDatasetBuilder` produces an **aggregated** payload. Raw tables are never sent.

**Included:** period totals (revenue, cost, profit, margin by month); per product (name,
quantity sold, revenue, cost, profit, margin, estimated-vs-actual variance); per channel (name,
revenue, fees, profit, margin); per material (name, consumption, price movement); quote funnel
counts and conversion; low-turnover items with days since last sale.

**Excluded, always:** API keys, connection strings, any setting marked `is_secret`; CPF/CNPJ,
e-mail, phone, address; user accounts; free-text internal notes (`internal_notes`, expense
notes — they routinely contain things nobody intended to send to a third party).

**Excluded by default, opt-in:** customer names
(`ai_settings.include_customer_names`, default `false`). With it off, customers appear as
`Cliente #7` with a stable per-run pseudonym, which preserves the analytical value ("this
customer buys most") without exporting identity.

Every run persists exactly what was sent (`dataset_summary`) plus a sha256 fingerprint, so
"what did we send OpenAI?" always has a precise, auditable answer.

### 6. Model output is data, never instruction

Results are stored and rendered as text. They are never executed, never used to build a query,
never used to change a price, never fed into a code path. Prompt injection through business
data (a product named `"ignore previous instructions and …"`) is therefore a display concern,
not a control-flow one. Results are classified `FACT` / `HYPOTHESIS` / `RECOMMENDATION` — the
prompt requires the separation and the schema enforces it.

### 7. Operational controls

- Models are configuration (`gpt-5.4`, `gpt-5.4-mini`), never constants in business code.
- Timeout 60 s, driven by the outbox so a slow provider never holds a request or a transaction.
  The AI consumer sets `max_attempts = 3` on its own messages — lower than the platform default
  of 5, because a paid API call that has failed three times is very unlikely to succeed on the
  fourth and each attempt costs money. Backoff follows the standard ladder
  ([ADR-0012 §12](ADR-0012-domain-events-and-outbox.md#20-retry-policy)); the idempotency key is
  `ai-insight:{run_id}`.
- Optional `monthly_budget_amount`; exceeding it disables runs until the next month or an
  explicit override. Token counts and estimated cost are recorded per run.
- The feature ships **disabled** (`is_enabled = false`) and works only after an `Owner` enters
  a key. No key, no outbound calls, no silent degradation.

## Alternatives considered

- **Key in `appsettings.json` or an environment variable only** — simpler, and rejected because
  the brief requires the operator to manage the token in the UI. Environment configuration
  remains supported as an override for deployments that prefer it.
- **Key in the frontend, calling OpenAI directly from the browser** — forbidden by the brief and
  indefensible: any user could read it from devtools.
- **Column-level encryption in PostgreSQL (pgcrypto)** — moves the key management problem into
  the database without solving it; Data Protection integrates with the platform already in use
  for cookies.
- **Sending raw tables and letting the model aggregate** — rejected: larger payloads, higher
  cost, worse answers, and it exports personal and internal data with no benefit.
- **Streaming responses to the browser** — rejected in v1: it would require the request to stay
  open through the API, complicating the budget guard and the audit record for a feature whose
  output is read minutes later, not live.

## Consequences

**Positive:** the key has one storage location, one decryption point and one audit trail;
the payload is small, cheap and privacy-preserving; disabling AI removes every outbound call.

**Negative:** aggregation limits what the model can notice — it cannot spot a pattern in data it
never receives. This is an accepted trade; the dataset builder is the place to extend
deliberately, with each addition reviewed against the exclusion list. Data Protection key-ring
loss makes the stored key unrecoverable, requiring re-entry (an inconvenience, not a data loss).

## Compliance checks

- Test: no API response body, anywhere, contains the string `sk-`.
- Test: with a configured key, no log sink receives the key value.
- `RotationKeepsOldPayloadReadableWithCertificateRing` ·
  `RotationCreatesNewKeyWithoutClaimingRewrap` · `OldCertificateNotAutomaticallyRemoved` ·
  `ProductionMissingCertificatePreventsHostStartup` ·
  `ReadinessIsNotExpectedWhenStartupValidationFails` ·
  `RestoreWithoutCertificateRequiresOfflineRecovery` ·
  `RestoreWithCertificateDecryptsExistingSecret` ·
  `ExplicitCryptoRecoveryRestoresOperability` ·
  `CryptoRecoveryClearsUnreadableExternalSecrets` ·
  `CryptoRecoveryArchivesRatherThanDeletesKeys` ·
  `CryptoRecoveryRefusesWhenRingCanStillUnwrap` — see
  [ROADMAP S1](../ROADMAP.md#s1-acceptance-test-contracts-mandatory).
- Test: the dataset payload contains no `document`, `email`, `phone`, `address` or
  `internal_notes` field.
- Test: with `include_customer_names = false`, no customer name appears in the payload.
- Test: `is_enabled = false` produces zero outbound HTTP calls.
