# SECURITY — Verce 3D | Laboratório de Custos

Scope: a single-organization system, a handful of users, published on the internet. The threat
model is not "nation-state adversary"; it is **"an unauthenticated stranger finds the URL"**,
plus **"a secret leaks into a log, a repository or a browser"**. The controls below are sized
for that.

Related: [ADR-0008 AI Integration and Secret Handling](architecture/ADR-0008-ai-integration-and-secret-handling.md) ·
[ADR-0009 Authentication Strategy](architecture/ADR-0009-authentication-strategy.md) ·
[ADR-0010 Audit Strategy](architecture/ADR-0010-audit-strategy.md)

---

## 1. Threat model

| # | Threat | Likelihood | Control |
|---|---|---|---|
| T1 | Unauthenticated access to the deployed app | high | Everything behind auth by default (§2) |
| T2 | OpenAI API key leaking to the browser | high | Key never leaves the backend (§5) |
| T3 | Secret written to logs or to `appsettings.json` | high | Redaction + configuration rules (§6) |
| T4 | Customer PII sent to a third-party LLM | medium | Data minimization, off by default (§5.4) |
| T5 | Session theft via XSS | medium | HttpOnly cookies, CSP, no token in JS storage (§2.3) |
| T6 | CSRF on state-changing endpoints | medium | SameSite + antiforgery tokens (§2.4) |
| T7 | Silent tampering with costs, fees or approvals | medium | Audit trail + immutable revisions (§7) |
| T8 | Mass data extraction through export/report endpoints | low | Authorization + audit on export (§3) |
| T9 | SSRF / RCE through the PDF renderer | low | Chromium sandbox, no user-supplied URLs (§8) |
| T10 | SQL injection | low | EF Core parameterization; Dapper only with parameters (§9) |
| T11 | Data loss | medium | Backups (§10, [OPERATIONS §4](OPERATIONS.md#4-backup)) |
| T12 | Malicious file uploaded as a logo (polyglot, SVG with script, decompression bomb) | medium | Eight-stage upload pipeline, re-encoding, no SVG in v1 (§8.1) |
| T13 | A template printing internal notes, cost or margin to a customer | medium | Closed binding catalogue excludes those paths entirely (§8) |
| T14 | Unclaimed fresh installation — a stranger creates the first Owner | **high** | No HTTP bootstrap path; CLI + deployment secret (§2.5) |
| T15 | Stolen database backup yields the OpenAI key | medium | Key ring wrapped by a certificate held **outside** the database (§5.1) |
| T16 | Silent security downgrade — app starts with a fresh unprotected key ring | medium | Fail-closed startup in Production (§5.1) |
| T17 | Two simultaneous bootstraps seize a fresh installation | medium | Advisory-lock serialization (§2.5) |
| T18 | Concurrent Owner removals leave zero Owners | medium | Advisory-lock serialization plus in-lock recount (§2.5) |
| T19 | A stale outbox worker overwrites a live worker's result | medium | Lease fencing token ([ADR-0012 §15](architecture/ADR-0012-domain-events-and-outbox.md)) |

---

## 2. Authentication and session

### 2.1 Decision

**ASP.NET Core Identity with cookie authentication.** No JWT, no OAuth provider, no external
IdP in v1. Full rationale and rejected alternatives in
[ADR-0009](architecture/ADR-0009-authentication-strategy.md).

Short form: the frontend and the API are same-origin, so a cookie is both simpler and safer
than a bearer token — it cannot be read by JavaScript, it is revocable server-side, and it
removes the need for refresh-token machinery that is a common source of bugs at this scale.

### 2.2 Single organization, multi-user ready

- No `organization_id` column anywhere in v1. Adding one later to a system with a few thousand
  rows is a mechanical migration; carrying tenant plumbing now would cost every query, every
  index and every test for a feature that may never be needed.
- **Multi-user from day one**: real user accounts, real roles, per-user audit attribution.
  The system must never have a shared "admin/admin" login, because audit attribution is a
  stated requirement and a shared account destroys it.

### 2.3 Cookie configuration

```
HttpOnly       = true
Secure         = true            (Always; HTTP is rejected in production)
SameSite       = Lax
Name           = __Host-verce.auth
ExpireTimeSpan = 8 hours
SlidingExpiration = true
```
Cookie ticket data is protected with ASP.NET Core Data Protection; the key ring is persisted to
`platform.data_protection_keys` and wrapped by the certificate described in §5.1, so a restart
does not log everyone out and a second instance validates the same cookies.

### 2.4 Additional session controls

- **Antiforgery tokens** on every state-changing request. The SPA reads the token from a
  `XSRF-TOKEN` cookie and echoes it in a header.
- **Lockout** after 5 failed attempts for 15 minutes.
- **Password policy**: minimum 12 characters, no composition rules (length beats symbol
  theatre), checked against a small local list of trivially common passwords.
- **Rate limiting** on `/api/auth/*`: 10 requests/minute per IP.
- No public registration endpoint. Users are created by an `Owner`; the **first** Owner comes
  from the CLI bootstrap in §2.5, not from an HTTP route.
- Sessions are revocable: `SecurityStamp` validation runs every 30 minutes, so deactivating a
  user takes effect without waiting for cookie expiry.

### 2.5 First Owner bootstrap and account recovery

*(Added 2026-09-07, gate blocker B-004. Full decision in
[ADR-0009 §6–§10](architecture/ADR-0009-authentication-strategy.md); runbook in
[OPERATIONS §2](OPERATIONS.md#2-first-installation).)*

**No credential is ever seeded.** There is no `admin/admin`, no default password, and no
first-run HTTP setup wizard that would leave an anonymous administrator-creating endpoint
exposed on the internet between deployment and setup.

| Concern | Mechanism |
|---|---|
| Create the first Owner | `bootstrap-owner` **CLI on the host**, serialized by advisory lock `8401001` so concurrent invocations cannot both win; refuses once any Owner exists; requires the bootstrap secret in Production: **expected** from the mounted file at `VERCE_BOOTSTRAP_SECRET_FILE`, **candidate** from a silent no-echo prompt |
| Set the first password | Single-use setup token, **SHA-256 hashed in the database**, 30-minute expiry, consumed via `/setup-account?token=…` — the only anonymous endpoint that can set a password |
| Password never on a command line | No `--password` flag: shell history, `ps`, container logs |
| Secret challenge has **two operands** | **expected** from a mounted file (`VERCE_BOOTSTRAP_SECRET_FILE`), **candidate** typed at a silent no-echo prompt. ≥ 256 bits, `FixedTimeEquals`, neither ever logged. Comparing the file with itself would authenticate nobody |
| Cannot log in before setup | `setup_status = PENDING_SETUP` blocks authentication **before** password verification, regardless of whether a hash exists |
| Token consumption is atomic | one conditional `UPDATE … WHERE consumed_at IS NULL AND invalidated_at IS NULL AND expires_at > now()` — two simultaneous requests cannot both succeed |
| Routine reset | An `Owner` issues a `RESET` token; sessions die via `SecurityStamp` |
| **Last-Owner lockout** | `recover-owner` CLI, `VERCE_RECOVERY_SECRET_FILE` (**a different secret in a different file**), local access only, **exposed through no HTTP route**. It may recover the **sole** Owner: the `Owner` role is never removed, and `LAST_OWNER_PROTECTED` guards the API surface only ([ADR-0009 §9.1](architecture/ADR-0009-authentication-strategy.md#91-recovering-the-sole-owner--the-break-glass-exception)) |
| Session invalidation | Every reset, recovery, deactivation or role change regenerates `SecurityStamp` |
| Last-Owner protection | Every Owner-population-reducing operation takes advisory lock `8401002` and re-counts inside it, so two concurrent removals cannot both pass. Rejected with `LAST_OWNER_PROTECTED` |

`platform.account_setup_token` stores only the token **hash**, so a database read yields nothing
usable. Tokens are single-use, and a consumed token is rejected indistinguishably from an
expired one.

---

## 3. Authorization

### 3.1 Roles (v1)

| Role | Can |
|---|---|
| `Owner` | Everything, including settings, fee rules, AI configuration, user management |
| `Operator` | Customers, products, supplies and inventory, quotes, production, sales, expenses, documents |
| `Viewer` | Read-only across the app; may execute stateless analytical cost calculations; no exports of full customer lists |

### 3.2 Mechanism

Role checks are **never** written as string comparisons in handlers. Authorization uses
**permission constants** mapped to roles in one place:

```csharp
public static class Permissions
{
    public const string QuotesApprove   = "quotes:approve";
    public const string PricingManage   = "pricing:manage";
    public const string SettingsManage  = "settings:manage";
    public const string AiConfigure     = "ai:configure";
    // …
}
```

S4 maps `costing:read` and `costing:calculate` to Owner, Operator and Viewer. Calculation is
available to Viewer because it is a transient analytical operation: it creates no audit record,
does not write settings and cannot mutate stock. The state-changing HTTP verb still requires the
same-origin antiforgery token. Anonymous callers receive 401. Inactive supplies are absent from
the default picker and require an explicit request path/flag for historical analysis.
Endpoints require a permission policy. Adding a fourth role later means editing the mapping,
not hunting through endpoints. Every endpoint is authenticated **by default**
(`RequireAuthorization()` on the root route group); anonymous access is an explicit,
reviewable exception. The **complete** anonymous allow-list is:

| Route | Why |
|---|---|
| `/health/live` | orchestrator liveness probe |
| `/health/ready` | orchestrator readiness probe — **status word only** (see below) |
| `POST /api/auth/login` | rate-limited, lockout-protected |
| `GET`/`POST` `/setup-account` | consumes a single-use hashed token (§2.5); rate-limited |
| static assets | the SPA shell |

`/health/ready` must be anonymous because the orchestrator cannot authenticate, so it returns
**only** `healthy` / `degraded` / `unhealthy` — never component names, versions, error text or
counts. Which component is failing is operational detail an attacker would find useful;
the detailed breakdown lives at `/api/platform/health` and requires `Owner`.

An architecture test asserts no route outside this table is anonymous.

**`/api/settings/branding` is deliberately not on this list.** The Login page renders a static
`VERCE 3D · Laboratório de Custos` fallback instead of fetching branding anonymously, so no
Settings data — including branding — is ever readable pre-authentication. Full rationale, and
what a future public bootstrap endpoint would require, is in
[ADR-0015 §5.1](architecture/ADR-0015-brand-assets-and-application-branding.md#51-pre-authentication-branding-static-fallback-no-anonymous-fetch).

### 3.3 Sensitive operations

These require `Owner` **and** always write an audit entry with a reason:

- changing a fee rule version;
- changing an energy tariff version;
- changing AI settings (especially the API key);
- deactivating a user;
- restoring a backup;
- publishing a document template version.

---

## 4. Data classification

| Class | Data | Handling |
|---|---|---|
| **Secret** | OpenAI API key, data-protection keys, database connection string, SMTP credentials | Encrypted at rest, never logged, never in a response, never in the repository |
| **Confidential** | Customer name, document (CPF/CNPJ), e-mail, phone, address; cost and margin data | Auth required; excluded from AI payloads by default; audited on export |
| **Internal** | Products, materials, prices, quotes, production data | Auth required |
| **Public** | Nothing | — |

CPF/CNPJ is personal data under the LGPD. v1 obligations that are actually met:

- collected only when the operator chooses to (the field is optional);
- never sent to the AI provider (§5.4);
- deletable — a customer can be soft-deleted and their document nulled, while quotes keep the
  `customer_snapshot` needed for their own integrity. A future "anonymize customer" action must
  scrub the snapshot's document/e-mail/phone while keeping the name for document validity.

---

## 5. AI integration and secret handling

The controlling requirement: **the OpenAI key never goes to the frontend, is never logged, and
is never stored in plaintext.**

### 5.1 Storage

- Stored in `ai.ai_settings.api_key_encrypted bytea`, encrypted with ASP.NET Core Data
  Protection (`IDataProtector` with purpose `"Verce.AiApiKey"`).
- `api_key_last_four` is stored separately in plaintext so the UI can show `sk-…4f2a`.

**The Data Protection key ring** *(corrected 2026-09-07, gate blocker B-005)*:

| Layer | Where | Protected by |
|---|---|---|
| The API key | `ai.ai_settings.api_key_encrypted` | a Data Protection key |
| Data Protection keys | `platform.data_protection_keys` (`PersistKeysToDbContext`) | the wrapping certificate |
| The wrapping certificate **ring** | **files mounted on the host**, never in the database | file permissions (`0400`) |

The ring is `[current] + every retained predecessor`. Modelling only a single current
certificate would imply the previous one can be discarded once a new one is in place, which
destroys every Data Protection key still wrapped by it.

> **v1 removes no predecessor certificate — ever.** Not after a dependency check, not after
> 90 days, not by garbage collection. There is no supported API to re-wrap existing Data
> Protection keys, so any retained ciphertext may still depend on a key an old certificate
> wraps. **All predecessors stay mounted and configured indefinitely.** Safe retirement is
> possible future work, not a v1 operation
> ([OPERATIONS §6.2](OPERATIONS.md#62-wrapping-certificate-rotation-manual-rare)).

```csharp
services.AddDataProtection()
        .SetApplicationName("Verce3D")
        .PersistKeysToDbContext<VerceDbContext>()
        .ProtectKeysWithCertificate(current)
        .UnprotectKeysWithAnyCertificate(ring)   // current + EVERY retained predecessor
        .SetDefaultKeyLifetime(TimeSpan.FromDays(90));
```

> **The wrapping material is never inside the artifact it protects.** A database dump contains
> an encrypted API key and an encrypted key ring, and nothing capable of decrypting either.
> Persisting the key ring unprotected beside the ciphertext it unlocks would be security theatre.

**Fail closed.** If a required certificate is missing, unreadable, or its password is wrong, the
application **does not start** in Production: startup validation fails and the process exits
non-zero, **never binding HTTP**. `/health/ready` does not report this — nothing is listening to
answer it; startup validation and runtime readiness are separate mechanisms. The framework
default — silently generating a fresh, unprotected key ring — is explicitly disabled: it would
report a healthy system while logging every user out and leaving the stored API key
undecryptable. Development uses an unprotected file-system key ring, and that fallback is
permitted **only** there ([OPERATIONS §7](OPERATIONS.md#7-development-environment)).

**Residual risk, stated plainly:** an attacker holding *both* the database *and* the mounted
certificate recovers the key. Going further needs a KMS/HSM, disproportionate for a
single-container deployment. What this design buys is that a **database-only** compromise — a
stolen backup, a dump, a snapshot, the realistic case — yields nothing usable.

Backup composition, the consequences of restoring without the certificate, and the two distinct
rotation procedures are in [OPERATIONS §4–§6](OPERATIONS.md#4-backup).

### 5.2 Never exposed

- **No endpoint returns the key.** The settings DTO exposes `apiKeyLastFour` and
  `apiKeyUpdatedAt` only. The write endpoint accepts a key and returns nothing but a mask.
- An **architecture test** asserts that no type reachable from an API response contract has a
  property named `ApiKey`, `ApiKeyEncrypted`, `Token`, `Secret` or `Password`.
- The key is decrypted only inside `AiClient`, held in a local variable, and never placed in a
  field, a cache, an exception message or a log scope.

### 5.3 Never logged

Serilog is configured with a destructuring policy plus a redaction enricher that masks any
property whose name matches `apikey|api_key|token|secret|password|authorization|connectionstring`
(case-insensitive), replacing the value with `***REDACTED***`. HTTP client logging for the
OpenAI client has headers disabled. Failed AI calls log the status code and the request id —
never the request headers or body.

### 5.4 Data minimization for AI payloads

The AI never receives raw tables. `AiDatasetBuilder` produces an **aggregated** payload:

Included by default:
- period totals: revenue, cost, profit, margin, by month;
- per product: name, quantity sold, revenue, cost, profit, margin, estimated-vs-actual variance;
- per channel: name, revenue, fees, profit, margin;
- per material: name, consumption, price movement over the period;
- quote funnel counts and conversion rate;
- low-turnover items: product name, days since last sale.

Excluded, always:
- API keys, connection strings, any settings value marked `is_secret`;
- CPF/CNPJ, e-mail, phone, address of any customer;
- user names and login data;
- free-text internal notes (`internal_notes`, expense notes) — they routinely contain things
  nobody intended to send to a third party.

Excluded by default, opt-in only:
- **customer names** (`ai_settings.include_customer_names`, default `false`). With it off,
  customers appear as `Cliente #7` with a stable per-run pseudonym.

Every run persists exactly what was sent (`ai_insight_run.dataset_summary`) plus a sha256
fingerprint, so the question "what did we send OpenAI?" always has a precise answer.

### 5.5 Provider controls

- Model names are configuration (`gpt-5.4`, `gpt-5.4-mini`), never hard-coded constants in
  business code.
- Timeout 60 s, driven by the outbox. The AI consumer sets `max_attempts = 3` on its own
  messages — below the platform default of 5, because a paid call that failed three times is
  unlikely to succeed on the fourth and each attempt costs money.
- Optional monthly budget (`monthly_budget_amount`); exceeding it disables runs until the next
  month or an explicit override.
- The feature ships **disabled** (`is_enabled = false`). It works only after an Owner enters a key.
- The prompt instructs the model to separate `FACT` / `HYPOTHESIS` / `RECOMMENDATION`, and
  results are stored with that classification. Model output is **data, not instruction**: it is
  never executed, never used to build a query, and rendered as text only.

---

## 6. Secrets in configuration and code

- `appsettings.json` contains **no secrets** — only structure and non-sensitive defaults.
- Secrets come from environment variables (production) or .NET User Secrets (development).
- `.gitignore` excludes `appsettings.*.Local.json`, `.env`, `*.pfx`, `secrets.json`.
- A pre-merge check greps the diff for `sk-`, `postgres://`, `password=`, `BEGIN PRIVATE KEY`.
- The connection string is never written to a log, including at startup.

---

## 7. Audit

Two complementary layers ([ADR-0010](architecture/ADR-0010-audit-strategy.md)):

**1. Business history** — domain tables (`quote_status_history`,
`production_order_status_history`, `supply_cost_history`, `filament_price_history`,
`inventory_movement`). These are part of the domain: they have meaning, they are shown to the user,
and reports depend on them.

**2. Generic audit log** — `platform.audit_log`, written by an EF Core `SaveChanges`
interceptor for entities marked `[Auditable]`. Captures who, what, when, and the changed
columns with old/new values as JSONB.

Audited entities (v1): `Supply`, `InventoryMovement`, `Filament`, `FeeRule*`, `EnergyTariff*`, `Product`,
`ProductRecipe`, `Quote*`, `Sale`, `ProductionOrder`, `Expense`, `AppSetting`,
`DocumentTemplate*`, `AiSettings`, `User`/`Role`.

Explicitly **not** audited: `AuditLog` itself (already append-only — auditing an append-only table
doubles storage for zero information), and read operations except exports. `Supply` and
`InventoryMovement` are auditable: the latter's immutable ledger facts retain who/when posted
them through the normal audit interceptor.

Rules:
- `AiSettings.ApiKeyEncrypted` is audited as *"changed"* with the value replaced by `***`.
  The fact of the change is the security signal; the value is not.
- The audit log is append-only. No endpoint updates or deletes it.
- Retention: 24 months, then archived to a JSON export before pruning (S16).
- Event sourcing is **not** used. It was considered and rejected: the requirement is
  "who changed what and when", which a change log answers directly, while event sourcing would
  impose rebuild logic, projection versioning and migration pain on a small product.

---

## 8. Document rendering (Playwright/Chromium)

- Chromium runs **headless with the sandbox enabled**, as a non-root user.
- The renderer loads **only** locally generated HTML (`setContent`), never a user-supplied URL,
  and network access is blocked for the render context except for local assets. This closes the
  SSRF path that HTML-to-PDF converters classically open.
- Template definitions are JSON, and block renderers **HTML-encode every value** they emit.
  A template cannot inject raw script: the block schema has no "raw HTML" block in v1. If one
  is ever added it must be `Owner`-only and sanitized.
- Generated files are stored outside the web root and served through an authorized endpoint
  that checks permission on the *source* entity, never by direct static path.
- Render timeout 30 s; a failed render never blocks the business transaction (it runs from the
  outbox).
- **Fonts are embedded or self-hosted**, never fetched from a CDN. Beyond reproducibility
  ([ADR-0016](architecture/ADR-0016-document-render-snapshots.md)), a remote font URL would be an
  outbound request from the renderer — exactly what the blocked network context prevents.
- **Bindings are a closed catalogue.** A template cannot reference an arbitrary object graph;
  paths absent from the document type's catalogue do not resolve, and publishing a template with
  an unknown path fails. `internal_notes` and every cost/margin field are excluded from the
  `QUOTE` catalogue, so no template — including one an operator builds in the Studio — can print
  the shop's margin onto a customer-facing proposal.
- **Conditionals are declarative** (`visibleWhen` with a closed operator set). There is no
  expression language and no executable code inside a template.
- `RICH_TEXT` values pass an allow-list sanitizer (bold, italic, lists, paragraphs, breaks).
  Everything else is HTML-encoded.

### 8.1 File uploads (brand assets, expense attachments)

Browser-supplied `Content-Type` and filename are **never trusted**. Every upload passes this
pipeline, failing closed at the first violation
([ADR-0015 §6](architecture/ADR-0015-brand-assets-and-application-branding.md)):

| # | Check | Detail |
|---|---|---|
| 1 | Size | ≤ 5 MB, configurable (`uploads.max_image_bytes`) |
| 2 | Extension | allow-list — v1 images: `.png`, `.jpg`/`.jpeg`, `.webp` |
| 3 | Magic bytes | sniffed from the stream; must match the claimed type |
| 4 | Real decode | must parse as an image (ImageSharp); ≤ 8000 px per side, guarding decompression bombs; dimensions captured here |
| 5 | Re-encode | canonical re-encode strips EXIF and any appended payload — **the stored bytes are bytes this system produced** |
| 6 | Storage name | content-addressed `{sha256[0:2]}/{sha256}.{ext}`; the user's filename is retained as metadata only and never touches a path, closing path traversal by construction |
| 7 | Location | outside the web root; served by an authorized endpoint with explicit `Content-Type` and `X-Content-Type-Options: nosniff` |
| 8 | Metadata | sha256, size, dimensions, content type, uploader, timestamp persisted |

**SVG is deliberately not supported in v1.** It is XML that can carry `<script>`,
`<foreignObject>` and external references — a stored-XSS vector in the application and an SSRF
vector in the renderer. Support is *architected for* without being *forced in*: validation is an
`IUploadValidator` selected by content type, so enabling SVG means adding a validator that
parses the XML, strips scripting and external references against an allow-list, and rejects
anything unrecognized — then widening the `content_type` CHECK constraint. Shipping SVG now with
"we will sanitize it later" is how a logo upload becomes an account takeover.

Uploads require `Owner` (brand assets) or `Operator` (expense attachments), and are audited.

---

## 9. Application hardening

- **HTTPS enforced** (HSTS in production), HTTP redirected.
- **Security headers**: `Content-Security-Policy` (default-src 'self'; no inline script except
  a nonce), `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin`,
  `X-Frame-Options: DENY`, `Permissions-Policy` minimal.
- **No CORS** in production (same origin). Development origin is allow-listed explicitly.
- **Input validation** at the edge (FluentValidation) *and* invariants in the domain. The edge
  rejects malformed shapes; the domain rejects impossible states. Neither substitutes the other.
- **SQL**: EF Core parameterizes everything. Dapper queries (S12+) use parameters only; string
  concatenation into SQL is banned and checked by review.
- **File uploads**: see §8.1 — a dedicated pipeline, not an afterthought.
- **Error responses** use `ProblemDetails` with stable error codes and no stack traces in
  production.
- Dependencies scanned by `dotnet list package --vulnerable` and `npm audit` in CI.

---

## 10. Backup and recovery (designed in S0, implemented in S16)

Full runbook: [OPERATIONS §4–§6](OPERATIONS.md#4-backup).

A complete backup is **three artifacts**, and one of them is not in the backup target:

1. `pg_dump` (custom format) — business data, encrypted API key, **encrypted** key-ring rows;
2. document and asset storage — PDFs, rendered HTML, brand asset files;
3. **the Data Protection wrapping certificate** — kept in a **separate** secret store.

> Artifact 3 must never be stored with artifacts 1 and 2. Together they reconstitute every
> plaintext, so co-locating them defeats §5.1 entirely.

Restoring 1 + 2 **without** 3: all auth cookies become invalid (users log in again; passwords
are unaffected), and **the OpenAI API key is unrecoverable and must be re-entered**. That is the
accepted, documented cost of keeping the wrapping material out of the backup.

- Nightly `pg_dump` via a Quartz job; 30 daily + 12 monthly retained.
- Generated documents and uploads backed up alongside the database, since a PDF whose row exists
  but whose file is gone is a broken record.
- **A backup is not a backup until a restore has been tested.** S16 includes a documented,
  executed restore drill into a scratch database.
- Export (CSV/JSON) is a separate feature from backup: it is for the accountant, is
  permission-gated and audited; backup is for disaster recovery.
- Targets: RPO 24 h, RTO 4 h (ARCHITECTURE §13).

---

## 11. Security checklist for every sprint

- [ ] No secret in source, in `appsettings.json`, in a log, or in any response DTO.
- [ ] Every new endpoint has an explicit authorization policy (or a reviewed anonymous exception).
- [ ] Every new state-changing endpoint is covered by antiforgery.
- [ ] Every new entity that holds cost, price, approval or configuration is `[Auditable]`.
- [ ] Every new field that could contain PII is reviewed against the AI exclusion list (§5.4).
- [ ] No raw SQL built by concatenation.
- [ ] No user-supplied URL reaches the renderer or any HTTP client.
- [ ] Every new upload path goes through the §8.1 pipeline — no direct `SaveAs` of a posted file.
- [ ] Every new binding added to a catalogue is reviewed against "could this print on a
      customer-facing document?"
- [ ] No new code path updates or deletes a `generated_document` with `purpose = ISSUED`.
