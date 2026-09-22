# DATA MODEL — Verce 3D | Laboratório de Custos

Conceptual/physical model. **No migrations are produced in S0.** This document is the input for
the migration work in S1–S3.

Conventions ([ADR-0001](architecture/ADR-0001-system-architecture.md),
[ADR-0011](architecture/ADR-0011-identifiers-and-concurrency.md)):

- `snake_case`, plural table names, one schema per module.
- **PK depends on the table's category.** Five categories, matching
  [ADR-0011 §1](architecture/ADR-0011-identifiers-and-concurrency.md) exactly:

  | Category | PK | Classified by | Examples here |
  |---|---|---|---|
  | **Domain entity** — independent domain identity/lifecycle | `id uuid` (UUID v7) | `IDomainEntity` | `customer`, `quote`, `product`, `sale`, `expense`, `audit_log` |
  | **Master data** — operator-managed catalogues | `id uuid` (UUID v7) | `IMasterData` | `product_category`, `expense_category`, `sales_channel`, `machine`, `energy_tariff` |
  | **Reference data** — closed, system-owned enumeration | stable textual `code` | `IReferenceData` | `document_type`, `brand_asset_type`, `supply_category` |
  | **Technical / framework** | whatever the algorithm or framework needs | `ITechnicalTable`, or the technical registry for framework-owned types | `quote_number_counter`, `outbox_message`, `data_protection_keys`, Identity, Quartz |
  | **Join table** — identity *is* the relationship | composite FK pair | `IJoinTable` | none yet |

  Domain entity and master data are **not** collapsed: both use a uuid PK, but the categories
  differ in who owns the lifecycle, and the distinction drives seeding, deletion and UI.
  Framework-owned types (Identity, Quartz, Data Protection) **cannot** implement application
  marker interfaces and are classified by the technical registry instead.
  *(Corrected 2026-09-07 — this header previously listed four categories and merged the first
  two, contradicting the normative ADR.)*
- **UUID v7 is identity and index locality, never business ordering. Never `ORDER BY id`** —
  order by `created_at`, a business date, an explicit sequence or `sort_order`.
- **Audit metadata columns** — `created_at timestamptz not null`, `created_by uuid null`,
  `updated_at timestamptz null`, `updated_by uuid null` — are carried by **application-owned
  domain and master-data tables**, populated by an interceptor from `IClock` and
  `AmbientOperationContext`. Omitted from the listings below.

  > **Framework-owned tables keep their framework schema.** Identity, Quartz and
  > `data_protection_keys` are **not** altered to add these columns: mutating a framework table
  > to satisfy an architectural slogan risks breaking the framework's own migrations for no
  > benefit. Append-only tables that record their own instant (`audit_log.occurred_at`,
  > `inventory_movement.occurred_at`, `outbox_message_attempt.started_at`) do not duplicate it.
  > The **business** audit trail is `platform.audit_log` plus the interceptor
  > ([ADR-0010](architecture/ADR-0010-audit-strategy.md)) — not these convenience columns.
- **Concurrency: `version bigint not null default 1` on every aggregate root**, mapped as the EF
  concurrency token and advancing **once per Unit of Work** when **any** entity inside the
  aggregate changes — root, child or grandchild. Omitted from the listings below.
  A newly created aggregate is inserted at **1** and stays at 1 for the whole creating Unit of
  Work, even if a later wave modifies it
  ([ADR-0011 §2.3](architecture/ADR-0011-identifiers-and-concurrency.md#23-added-roots--created-at-1-and-registered-as-handled)).
  *(Corrected 2026-09-07: this said `default 0`, contradicting the normative rule.)*
  `xmin` is not used anywhere: it cannot detect child-only mutations.
  Exempt: append-only tables, immutable snapshots, and technical tables — see
  [ADR-0011 §2.8](architecture/ADR-0011-identifiers-and-concurrency.md#28-scope-and-exemptions).
- Soft delete (`deleted_at timestamptz null` + partial unique indexes + EF global filter) only
  where marked **SD**.
- All instants are `timestamptz` in UTC. Business dates are `date` in the organization timezone.
- Monetary: `numeric(18,2)` for presented amounts, `numeric(18,6)` for intermediate/unit costs.
  Percent: `numeric(9,6)` as a fraction. See
  [CALCULATION-RULES §0](CALCULATION-RULES.md#0-precision-and-rounding-contract).
- Required extensions: `btree_gist` (exclusion constraints for non-overlapping validity ranges).

Legend: **PK** primary key · **FK** foreign key · **U** unique · **IX** index · **SD** soft delete.

---

## 1. `platform` schema

### `platform.user` (ASP.NET Core Identity `AspNetUsers`, renamed)
Identity tables (`user`, `role`, `user_role`, `user_claim`, `user_login`, `user_token`,
`role_claim`) are created by ASP.NET Core Identity in this schema. See [SECURITY](SECURITY.md).

Additional columns on `user`: `display_name text not null`, `is_active boolean not null`,
`last_login_at timestamptz null`, and *(added 2026-09-07, re-gate correction D)*:

| Column | Type | Notes |
|---|---|---|
| setup_status | text | not null **CHECK** (`PENDING_SETUP`\|`ACTIVE`) default `PENDING_SETUP` |
| setup_completed_at | timestamptz | null |
| recovery_started_at | timestamptz | null — non-null while a local break-glass `recover-owner` is in flight |

**Login is rejected for `setup_status = 'PENDING_SETUP'` regardless of whether a password hash
exists**, and the check runs before password verification, returning a failure indistinguishable
from a wrong password. Inferring "not yet set up" from `password_hash IS NULL` alone was
ambiguous — that column is a provider-internal detail an external login provider could leave null
on a perfectly usable account
([ADR-0009 §7.1](architecture/ADR-0009-authentication-strategy.md)).

An Owner in `PENDING_SETUP` does **not** count toward the active-Owner population used by the
last-Owner guard.

### `platform.audit_log`
| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** |
| occurred_at | timestamptz | not null |
| user_id | uuid | null (system actions) |
| user_display_name | text | denormalized, survives user deletion |
| entity_schema | text | not null |
| entity_table | text | not null |
| entity_id | uuid | not null |
| operation | text | `INSERT` / `UPDATE` / `DELETE` |
| changed_columns | text[] | null on insert/delete |
| old_values | jsonb | null on insert |
| new_values | jsonb | null on delete |
| operation_started_at | timestamptz | not null — the command's start; identical on every row of the correlation |
| correlation_id | uuid | not null — one value per command, **shared across all UoW waves** |
| request_id | uuid | null — the HTTP request or job execution |
| wave_index | int | not null default 1 — which save wave produced this row |
| source | text | **CHECK** `API` / `JOB` / `CLI` / `SYSTEM` |

**IX** `(entity_table, entity_id, occurred_at desc)`, `(occurred_at desc)`,
`(user_id, occurred_at desc)`, `(correlation_id)`.
Append-only. No FK to `user` (audit must survive user removal).

`id` is the row's own `audit_id` (UUID v7). `correlation_id` + `wave_index` exist because one
command runs over several save/dispatch waves
([ADR-0010](architecture/ADR-0010-audit-strategy.md#multi-wave-alignment-re-gate-correction-g-2026-09-07)).
Rows from all waves share a correlation id and are grouped as one operation in the audit UI; an
entity genuinely modified in two waves yields two rows, distinguished by `wave_index`, because
two real state changes occurred.

`MIGRATION` is **not** a valid `source`: migrations do not pass through the `SaveChanges`
interceptor, so claiming it would describe a capability that does not exist.

**Audit rows are written inside the business transaction. If the audit write fails, the command
rolls back.**

### `platform.outbox_message`

*(Rewritten 2026-09-07, gate blocker B-003. The original had only `processed_at`,
`attempt_count` and `last_error` — no terminal state, no lease, no claim strategy, no
idempotency contract, so a poison message retried forever and two workers could double-process.)*

| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** |
| event_type | text | not null |
| message_schema_version | int | not null default 1 |
| payload | jsonb | not null |
| idempotency_key | text | null, **U** where not null — consumer dedup contract |
| status | text | not null **CHECK** (`PENDING`\|`PROCESSING`\|`PROCESSED`\|`FAILED`) |
| created_at | timestamptz | not null |
| available_at | timestamptz | null — next eligible instant; **NULL while `PROCESSED` or `FAILED`** |
| **processing_token** | uuid | null — **the lease fencing token**, fresh per claim |
| processing_started_at | timestamptz | null |
| lease_until | timestamptz | null — crash recovery deadline |
| worker_id | text | null — diagnostic only, **never** a fence |
| processed_at | timestamptz | null |
| failed_at | timestamptz | null |
| failure_reason | text | null — e.g. `LEASE_EXPIRED_ON_FINAL_ATTEMPT`, `NON_RETRYABLE` |
| **failure_disposition** | text | null **CHECK** (`ACTIVE`\|`DISMISSED`) — non-null **iff** `status = FAILED` |
| dismissed_at | timestamptz | null |
| dismissed_by | uuid | null |
| dismissal_reason | text | null |
| **execution_generation** | int | not null default **1** — the current retry **round**; incremented by manual requeue |
| attempt_count | int | not null default 0 — attempts consumed **in the current generation**; incremented **at claim**; **reset to 0 on manual requeue** |
| max_attempts | int | not null default 5 — per-message budget **per generation** |
| last_error | text | null — **never cleared**, even on requeue |
| last_error_at | timestamptz | null |
| correlation_id | uuid | not null — ties back to `audit_log` |
| request_id | uuid | null |
| actor_user_id | uuid | null |
| aggregate_type | text | not null |
| aggregate_id | uuid | not null |

- **IX** partial `(available_at, created_at) WHERE status = 'PENDING'` — the claim and
  stall queries (both add `attempt_count < max_attempts` as a filter).
- **IX** partial `(lease_until) WHERE status = 'PROCESSING'` — the reclaim sweep.
- **IX** partial `(failed_at desc) WHERE status = 'FAILED' AND failure_disposition = 'ACTIVE'` —
  health and diagnostics count only unresolved failures.
- **U** partial `(idempotency_key) WHERE idempotency_key IS NOT NULL`.
- **IX** `(correlation_id)`.
- **CHECK** `(status = 'FAILED') = (failure_disposition IS NOT NULL)` — no disposition outside
  `FAILED`, and never a `FAILED` row without one.
- **CHECK** `failure_disposition IS NULL OR failure_disposition IN ('ACTIVE','DISMISSED')`.
- **CHECK** `status <> 'PROCESSING' OR (processing_token IS NOT NULL AND lease_until IS NOT NULL)`.
- **CHECK** `status <> 'PENDING' OR available_at IS NOT NULL`.
- **CHECK** `attempt_count <= max_attempts`.
- **CHECK** `execution_generation >= 1`.

`requeue_count` is **not stored** — it is derived as `execution_generation - 1`. Two mutable
counters tracking the same fact could drift; one cannot.

*(Corrected 2026-09-07, re-gate B-RG2-001: `RESOLVED` is removed. A requeued message that
succeeds ends at `status = PROCESSED` with **no** disposition — success is what "resolved" meant,
and the schema forbade a disposition there anyway, so the value was unreachable.)*

Rows are written **inside the business transaction** and claimed after commit with
`FOR UPDATE SKIP LOCKED`. A message is **eligible** only while

```sql
status = 'PENDING' AND available_at <= now() AND attempt_count < max_attempts
```

(the budget is per **generation**, so a requeued message is eligible again with a fresh count)
so an exhausted message can never be claimed, and a `DISMISSED` one cannot either (it is
`FAILED`). The same predicate defines which rows participate in dispatcher-stall health
detection — a message waiting out its backoff is not a stall
([ADR-0012 §25.1](architecture/ADR-0012-domain-events-and-outbox.md#251-stall-detection-counts-only-eligible-messages)).

> **Every worker-driven transition must carry `AND processing_token = :token`** in its `WHERE`
> clause. `0 rows affected` means the caller's lease expired and another worker owns the message:
> it logs and abandons, and **must not** overwrite the new owner's state. `worker_id` is a
> diagnostic label and must never be used as the fence.
> ([ADR-0012 §15](architecture/ADR-0012-domain-events-and-outbox.md#15-lease-fencing--the-correction))

No `version` column — concurrency is the lease plus the token, not optimistic locking.

### `platform.outbox_message_attempt` — append-only

*(Added 2026-09-07, re-gate correction C: requeue must be able to reset forward-looking counters
without erasing what already happened, so the past lives here rather than in mutable columns.)*

| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** |
| outbox_message_id | uuid | **FK** cascade |
| **execution_generation** | int | not null — the message's generation **at claim time** |
| attempt_number | int | not null — `attempt_count` as incremented at claim, **scoped to that generation** |
| processing_token | uuid | not null |
| worker_id | text | null |
| started_at | timestamptz | not null |
| finished_at | timestamptz | null (null = the worker never reported back) |
| outcome | text | **CHECK** `SUCCEEDED` \| `RETRYABLE_FAILURE` \| `NON_RETRYABLE_FAILURE` \| `LEASE_EXPIRED` |
| error_type | text | null |
| error_message | text | null |

- **U** `(outbox_message_id, execution_generation, attempt_number)` — historical attempt
  identity.
- **IX** `(outbox_message_id, execution_generation, started_at)`.

> **The generation is part of the key on purpose** *(corrected 2026-09-07, re-gate
> B-RG3-001)*. With `UNIQUE (outbox_message_id, attempt_number)` alone, a manual requeue —
> which resets `attempt_count` to 0 — made the next claim insert `attempt_number = 1` a second
> time and **collide with the previous round's history**, so the message could never be
> retried. `attempt_count` is a *round* budget; history identity is
> `(message, generation, attempt)`.

Never updated after the outcome is written; pruned only with its parent message.

**Requeue is one transaction:** `execution_generation += 1`, `attempt_count = 0`,
`status = PENDING`, `failure_disposition = NULL`, `available_at = now()`, lease and token
cleared — with `id` and **`idempotency_key` unchanged**, because a requeue retries the *same*
business effect. Prior attempt rows are never deleted.

### `platform.account_setup_token`

*(Added 2026-09-07, gate blocker B-004.)*

| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** |
| user_id | uuid | **FK** → `platform.user`, cascade |
| token_hash | char(64) | not null **U** — SHA-256; **the raw token is never stored** |
| purpose | text | not null **CHECK** (`BOOTSTRAP`\|`RESET`\|`RECOVERY`) |
| expires_at | timestamptz | not null |
| consumed_at | timestamptz | null — a human used it |
| invalidated_at | timestamptz | null — superseded by a newer token or a role change |
| created_by_source | text | `CLI` / `API` |
| created_by_user_id | uuid | null (null for CLI bootstrap/recovery) |

- **IX** partial `(user_id) WHERE consumed_at IS NULL AND invalidated_at IS NULL`.
- `consumed_at` and `invalidated_at` are **different facts** and both are kept: one records that
  someone used the token, the other that it was replaced.
- Consumption is a **single atomic conditional update** so two simultaneous requests cannot both
  win ([ADR-0009 §7.2](architecture/ADR-0009-authentication-strategy.md#72-token-consumption-is-a-single-atomic-statement)):

```sql
UPDATE platform.account_setup_token
SET consumed_at = now()
WHERE token_hash = :hash
  AND consumed_at IS NULL AND invalidated_at IS NULL AND expires_at > now()
RETURNING id, user_id, purpose;      -- 0 rows ⇒ rejected
```

- Consumed, invalidated, expired and unknown tokens are rejected identically, revealing nothing.
- Only the SHA-256 hash is stored. An unsalted hash is sufficient **because the token is 256 bits
  of uniform randomness** — there is no dictionary to precompute and brute force is infeasible
  regardless of hash speed. This reasoning does **not** transfer to passwords, which use
  Identity's password hasher.

### `platform.data_protection_keys`

*(Added 2026-09-07, gate blocker B-005 — previously referenced in SECURITY.md as
`platform.data_protection_key` but absent from this document.)*

**Category 4 technical table.** Its shape is imposed by the ASP.NET Core Data Protection EF
repository and must not be altered:

| Column | Type | Notes |
|---|---|---|
| id | int | **PK**, identity — framework-defined, *not* a uuid |
| friendly_name | text | null |
| xml | text | not null — the key element, **encrypted** by the wrapping certificate |

Written by `PersistKeysToDbContext<VerceDbContext>()`; each key is encrypted with
`ProtectKeysWithCertificate`, whose private key lives **outside the database** as a host file
mount, and unwrapped with `UnprotectKeysWithAnyCertificate(ring)` — the **ring** holds every
certificate that may still be needed, not just the current one. **Rows are never deleted by the
application** — old keys are required to decrypt anything protected while they were current.
See [SECURITY §5.1](SECURITY.md#51-storage) and
[OPERATIONS §3](OPERATIONS.md#3-data-protection-key-ring).

### `platform.data_protection_key_archive`

*(Added 2026-09-07, re-gate correction E.)* **Category 4 technical table**, uuid PK.

`id uuid` **PK**, `original_key_id int`, `friendly_name text`, `xml text` (still encrypted, still
unreadable), `archived_at timestamptz`, `archived_by uuid null`, `archive_reason text`,
`recovery_operation_id uuid`.

The `recover-data-protection` command (ADR-0008 §2.4) moves unreadable key rows here rather than
deleting them, so a wrapping certificate located later can still recover them. Disaster recovery
must not be a one-way door.

### `platform.qrtz_*`
Quartz.NET PostgreSQL job store tables, created by the Quartz schema script. Category 4
technical tables. Not modeled here.

---

## 2. `settings` schema

### `settings.company_profile` — single row
`id uuid` **PK**, `legal_name`, `trade_name`, `document`, `email`, `phone`,
`website`, `instagram`, `whatsapp`, `zip_code`, `street`, `number`, `complement`, `district`,
`city`, `state char(2)`, `country char(2)`,
`timezone text not null default 'America/Sao_Paulo'`, `currency char(3) not null default 'BRL'`.
**CHECK** enforcing a single row (`id = '00000000-...-0001'`).

Supplies the `company.*` document bindings. *(Named `organization_profile` before the branding
addendum; the rename lands in S2 before any code exists.)*

### `settings.app_setting`
`id uuid` **PK**, `key text` **U**, `value text`, `value_type text`
(`STRING|INT|DECIMAL|BOOL|JSON`), `scope text`, `description text`, `is_secret boolean`.
Audited. Seeded keys are listed in [DOMAIN-MODEL §14](DOMAIN-MODEL.md#14-settings-module).

### `settings.brand_asset_type` — lookup
`code text` **PK** (`PRIMARY_LOGO`, `COMPACT_LOGO`, `NEGATIVE_LOGO`, `SYMBOL`, `FAVICON`,
`DOCUMENT_LOGO`, `OTHER`), `name text`, `is_active boolean`.

### `settings.brand_asset` — **SD**
`id uuid` **PK**, `brand_asset_type_code text` **FK** restrict, `name text not null`,
`current_version_id uuid null` (deferrable **FK** → `brand_asset_version`),
`is_active boolean not null`, `deleted_at timestamptz null`.
**IX** `(brand_asset_type_code) WHERE deleted_at IS NULL`.

### `settings.brand_asset_version` — immutable, never deleted
| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** |
| brand_asset_id | uuid | **FK** cascade |
| version_number | int | not null |
| file_path | text | not null — content-addressed `{sha256[0:2]}/{sha256}.{ext}` |
| sha256 | char(64) | not null |
| content_type | text | **CHECK** in (`image/png`, `image/jpeg`, `image/webp`) |
| file_size_bytes | bigint | not null **CHECK** `> 0` |
| width_px | int | not null |
| height_px | int | not null |
| original_file_name | text | **metadata only — never used to build a path** |
| uploaded_at | timestamptz | not null |
| uploaded_by | uuid | null |
| is_current | boolean | not null |

- **U** `(brand_asset_id, version_number)`.
- **U** partial `(brand_asset_id) WHERE is_current`.
- **IX** `(sha256)` — deduplication lookups.

`image/svg+xml` is **absent from the CHECK on purpose**
([ADR-0015 §7](architecture/ADR-0015-brand-assets-and-application-branding.md)); enabling SVG
means adding a sanitizing validator *and* widening this constraint, deliberately.

### `settings.branding_assignment`
`id uuid` **PK**, `role text` **U** **CHECK** (`SYSTEM_LOGO|SYSTEM_LOGO_COMPACT|FAVICON|
DOCUMENT_DEFAULT_LOGO`), `brand_asset_id uuid` **FK** restrict.

One row per role. `RESTRICT` means an asset in active use cannot be removed out from under the
application or a template that inherits it.

---

## 3. `customers` schema

### `customers.customer` — **SD**
| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** |
| person_type | text | `INDIVIDUAL` / `COMPANY`, **CHECK** |
| name | text | not null |
| trade_name | text | null |
| document | text | null, digits only |
| email | text | null |
| phone | text | null |
| notes | text | null |
| is_active | boolean | not null default true |
| deleted_at | timestamptz | null |
| creation_sequence | bigint | not null, database default from `customers.customer_creation_sequence_seq`; internal and immutable |

- **U** partial: `(document) WHERE document IS NOT NULL AND deleted_at IS NULL`.
- **U** `(creation_sequence)` — final deterministic pagination tie-breaker, never a public number.
- **IX** `(name)` — trigram index `gin (name gin_trgm_ops)` for the home search box.
- **IX** partial `(is_active) WHERE deleted_at IS NULL`.

`customers.customer_creation_sequence_seq` is a `bigint` PostgreSQL sequence (`START 1`,
`INCREMENT 1`, `NO CYCLE`) owned by `customer.creation_sequence`. Values are allocated by the
database on insert, may contain rollback gaps and are never reused. The canonical list order is
`name ASC, created_at ASC, creation_sequence ASC`.

### `customers.customer_address`
`id` **PK**, `customer_id` **FK**→customer (cascade), `label`, `zip_code`, `street`, `number`,
`complement`, `district`, `city`, `state char(2)`, `country char(2) default 'BR'`,
`is_primary boolean`, `is_default_shipping boolean`, `notes`.

- **U** partial `(customer_id) WHERE is_primary` — at most one primary.
- **U** partial `(customer_id) WHERE is_default_shipping`.
- **IX** `(customer_id)`.

---

## 4. `inventory` schema

> **S3 supersedes the schema previously documented here** (a separate `Filament` aggregate with
> per-lot cost history and a `StockMovement`/`StockCount` pair keyed by a polymorphic
> `MaterialKind`). See [ADR-0017](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md)
> for why; what follows is the schema actually migrated in `AddS3SuppliesAndInventory`.

### `inventory.supply_category` — Category 3 reference data
| Column | Type | Notes |
|---|---|---|
| code | varchar(40) | **PK** (`FILAMENT`, `RESIN`, `PACKAGING`, `HARDWARE`, `ELECTRONICS`, `FINISHING`, `CONSUMABLE`, `OTHER`) |
| name | varchar(100) | not null |
| is_active | boolean | not null |

Seeded idempotently at startup by `InventorySeedService`, mirroring `SettingsSeedService`'s
gate (`Settings:SeedOnStartup` config + no-pending-migrations check).

### `inventory.supply` — **SD** (Category 1 domain entity, aggregate root)
| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** |
| code | varchar(40) | not null, **U**, immutable after creation |
| name | varchar(200) | not null |
| description | varchar(2000) | null |
| category_code | varchar(40) | **FK** restrict → `supply_category.code` |
| base_unit | varchar(16) | not null, **CHECK** ∈ `SupplyBaseUnit`, immutable after creation |
| minimum_stock | numeric(14,4) | null |
| preferred_supplier | varchar(200) | null |
| notes | varchar(2000) | null |
| active | boolean | not null |
| current_stock_base_unit | numeric(14,4) | not null; **cached projection**, written only alongside a movement insert (§4.5) |
| latest_purchase_unit_cost | numeric(18,6) | null; informational snapshot (§4.7), not a costing policy |
| has_recorded_movement | boolean | not null; gates the one-time initial balance |
| filament_material_type | varchar(16) | null, **CHECK** ∈ `FilamentMaterialType` when present |
| filament_brand | varchar(100) | null |
| filament_color_name | varchar(100) | null |
| filament_color_code | varchar(20) | null |
| filament_diameter_mm | numeric(6,4) | null |
| filament_spool_net_weight_grams | numeric(12,3) | null |
| creation_sequence | bigint | shadow-only, never in DTOs/OpenAPI — final pagination tie-breaker, same pattern as `customer.creation_sequence` (ADR-0011 §1.2.1) |
| created_at / created_by | timestamptz / uuid | via `[Auditable]` |
| updated_at / updated_by | timestamptz / uuid | via `[Auditable]`, null until first update |
| version | bigint | not null; optimistic-concurrency token (ADR-0011 §2) — the same token that makes non-negative-stock concurrency work (ADR-0017 §3) |

The six `filament_*` columns are **scalar, not an EF owned type** — see ADR-0017 §7. All six are
null for a non-filament supply.

**IX** `(code)` unique, `(creation_sequence)` unique, `(active)`, `(category_code)`,
`gin (name gin_trgm_ops)`.

### `inventory.inventory_movement` — append-only (*E* of `supply`, `IOwnedBy<Supply>`)
| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** |
| supply_id | uuid | **FK** restrict → `supply.id` |
| type | varchar(16) | not null, **CHECK** ∈ `PurchaseReceipt, ManualIncrease, ManualDecrease, Consumption, ReturnIn, ReturnOut, InitialBalance, Correction` |
| entered_quantity | numeric(18,8) | not null; immutable operator-entered fact, canonicalized to eight decimal places |
| entered_unit | varchar(16) | not null, **CHECK** ∈ `SupplyBaseUnit`; immutable operator-entered unit |
| quantity_delta_base_unit | numeric(14,4) | not null, **signed** — positive for increases, negative for decreases |
| occurred_at | timestamptz | not null |
| reason | varchar(1000) | null |
| reference | varchar(200) | null |
| supplier | varchar(200) | null |
| unit_cost_snapshot | numeric(18,6) | null |
| total_cost_snapshot | numeric(18,2) | null |
| created_at / created_by | timestamptz / uuid | via `[Auditable]` — "posted at/by" for a ledger row |
| updated_at / updated_by | timestamptz / uuid | via `[Auditable]`; never populated — movements are never updated |

**IX** `(supply_id, occurred_at)`. `entered_quantity`/`entered_unit` preserve what the operator
reported, while `quantity_delta_base_unit` is the separately authoritative normalized stock delta.
No `Update`/`Delete` endpoint or domain method exists for this
table — a correction is a new row, never an edit of an existing one.

`Consumption`, `ReturnIn`, `ReturnOut` are valid per the `CHECK` constraint (so a later sprint's
migration does not need to widen it) but are not producible by any S3 endpoint.

> **Forward-reference note for later sections.** Sections below this point (Catalog, Costing,
> Production, Finance, Reporting) were drafted before S3 and still reference a pre-S3 inventory
> shape that no longer exists: `inventory.filament`, `inventory.filament_lot`,
> `inventory.supply_lot`, `inventory.stock_movement`, `inventory.stock_count`, and the
> `material_kind` discriminator. Every such reference is **stale** and must be reconciled by the
> sprint that actually builds that module, against the real S3 shapes above
> (`inventory.supply`, `inventory.supply_category`, `inventory.inventory_movement`) and
> [ADR-0017](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md). This note is left
> here deliberately rather than redesigning those unbuilt modules now, which is out of S3's scope.

---

## 5. `catalog` schema

### `catalog.product_category`
`id` **PK**, `name` **U**, `is_active`.

### `catalog.product` — **SD**
`id` **PK**, `sku text null`, `name text not null`, `description`, `product_category_id` **FK** null,
`image_path`, `default_sales_channel_id uuid null` (cross-module, restrict),
`default_margin_percent numeric(9,6) null`, `current_recipe_id uuid null`,
`is_active`, `deleted_at`.

- **U** partial `(sku) WHERE sku IS NOT NULL AND deleted_at IS NULL`.
- **IX** `gin (name gin_trgm_ops)`, `(product_category_id)`.
- `current_recipe_id` is a deferrable FK to `product_recipe` (circular pair) — created as
  `DEFERRABLE INITIALLY DEFERRED`.

### `catalog.product_recipe`
| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** |
| product_id | uuid | **FK** cascade |
| revision_number | int | not null |
| is_current | boolean | not null |
| effective_from | date | not null |
| print_duration_seconds | int | not null **CHECK** `>= 0` |
| machine_id | uuid | null (cross-module) |
| estimated_energy_kwh | numeric(12,4) | null |
| labor_minutes | int | not null default 0 |
| labor_hourly_rate | numeric(18,6) | null |
| wastage_rate | numeric(9,6) | not null default 0, **CHECK** `>= 0 AND < 1` |
| post_processing_notes | text | null |
| notes | text | null |

- **U** `(product_id, revision_number)`.
- **U** partial `(product_id) WHERE is_current` — exactly one current recipe.

### `catalog.product_filament_component`
`id` **PK**, `product_recipe_id` **FK** cascade, `filament_id` **FK** restrict,
`grams_used numeric(12,3) not null CHECK > 0`, `allow_substitution boolean default false`,
`note`, `sort_order int`.
**IX** `(product_recipe_id, sort_order)`, `(filament_id)`.
**No unique constraint on `(recipe, filament)`** — the same filament may legitimately appear
twice (two parts, two notes).

### `catalog.product_supply_component`
`id` **PK**, `product_recipe_id` **FK** cascade, `supply_id` **FK** restrict,
`quantity numeric(14,4) not null CHECK > 0`, `note`, `sort_order`.

### `catalog.product_cost_line`
`id` **PK**, `product_recipe_id` **FK** cascade, `description text not null`,
`amount numeric(18,6) not null CHECK >= 0`, `cost_kind text`, `sort_order`.

---

## 6. `energy` schema

### `energy.machine` — **SD**
`id` **PK**, `name` **U** (where not deleted), `model`, `serial_number`,
`nominal_power_watts int not null CHECK > 0`, `average_power_watts int null`,
`acquisition_cost numeric(18,2) null`, `acquisition_date date null`,
`expected_lifetime_hours int null CHECK > 0`, `maintenance_cost_per_hour numeric(18,6) null`,
`hourly_rate_override numeric(18,6) null`, `energy_provider_kind text not null default 'ESTIMATED'`,
`is_active`, `deleted_at`.

### `energy.energy_tariff`
`id` **PK**, `name` **U**, `utility text`, `is_default boolean`, `is_active`.
**U** partial `(is_default) WHERE is_default` — one default tariff.

### `energy.energy_tariff_version`
`id` **PK**, `energy_tariff_id` **FK** cascade, `price_per_kwh numeric(18,6) not null CHECK > 0`,
`includes_taxes boolean not null`, `valid_from date not null`, `valid_until date null`, `notes`.

- **EXCLUDE USING gist** `(energy_tariff_id WITH =, daterange(valid_from, valid_until, '[)') WITH &&)`.
- **IX** `(energy_tariff_id, valid_from desc)`.

### `energy.energy_consumption_session`
`id` **PK**, `machine_id` **FK** restrict, `source text` **CHECK**
(`ESTIMATED|MANUAL|SMART_PLUG`), `started_at timestamptz`, `ended_at timestamptz null`,
`kwh numeric(12,4) not null CHECK >= 0`, `average_power_watts int null`,
`production_order_item_id uuid null` (cross-module), `external_device_id text null`,
`external_payload jsonb null`, `notes`.
**IX** `(production_order_item_id)`, `(machine_id, started_at desc)`.

---

## 7. `pricing` schema

### `pricing.sales_channel` — **SD**
`id` **PK**, `name` **U** (where not deleted), `kind text` **CHECK** (`DIRECT|MARKETPLACE|OTHER`),
`code text null`, `default_margin_percent numeric(9,6) null`, `is_active`, `deleted_at`, `notes`.

### `pricing.fee_rule`
`id` **PK**, `sales_channel_id` **FK** cascade, `name text`, `applies_to text` **CHECK**
(`ALL_PRODUCTS|PRODUCT_CATEGORY|PRODUCT`), `target_id uuid null`, `priority int not null default 0`,
`is_active boolean`.
**CHECK** `(applies_to = 'ALL_PRODUCTS') = (target_id IS NULL)`.
**IX** `(sales_channel_id, is_active, priority desc)`.

### `pricing.fee_rule_version`
| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** |
| fee_rule_id | uuid | **FK** cascade |
| valid_from | date | not null |
| valid_until | date | null |
| commission_percent | numeric(9,6) | not null **CHECK** `>= 0 AND < 1` |
| fixed_fee | numeric(18,2) | not null **CHECK** `>= 0` |
| fixed_fee_application | text | **CHECK** `PER_UNIT` / `PER_ORDER`, default `PER_UNIT` |
| minimum_fee | numeric(18,2) | null |
| maximum_fee | numeric(18,2) | null |
| shipping_component | numeric(18,2) | null (reserved) |
| notes | text | null |

- **EXCLUDE USING gist** `(fee_rule_id WITH =, daterange(valid_from, valid_until, '[)') WITH &&)`.
- **CHECK** `minimum_fee IS NULL OR maximum_fee IS NULL OR minimum_fee <= maximum_fee`.

### `pricing.price_bracket`
`id` **PK**, `fee_rule_version_id` **FK** cascade, `min_price numeric(18,2) not null`,
`max_price numeric(18,2) null`, `commission_percent numeric(9,6)`, `fixed_fee numeric(18,2)`,
`minimum_fee`, `maximum_fee`, `sort_order int`.
- **EXCLUDE USING gist** `(fee_rule_version_id WITH =, numrange(min_price, max_price, '[)') WITH &&)`.
- **CHECK** `max_price IS NULL OR max_price > min_price`.

---

## 8. `costing` schema

### `costing.cost_experiment`
`id` **PK**, `name text not null`, `description`, `status text` **CHECK**
(`DRAFT|SAVED|CONVERTED|ARCHIVED`), `print_duration_seconds int`, `machine_id uuid null`,
`energy_tariff_version_id uuid null`, `estimated_energy_kwh numeric(12,4) null`,
`labor_minutes int`, `labor_hourly_rate numeric(18,6) null`, `wastage_rate numeric(9,6)`,
`quantity numeric(14,4) not null default 1`,
`result_total_cost numeric(18,6) null`, `result_snapshot jsonb null`,
`calculation_engine_version text null`, `source_experiment_id uuid null` **FK** self,
`converted_to_product_id uuid null`, `converted_at timestamptz null`.
**IX** `(status, created_at desc)`.

### `costing.cost_experiment_component`
`id` **PK**, `cost_experiment_id` **FK** cascade, `kind text` **CHECK** (`FILAMENT|SUPPLY|MANUAL`),
`filament_id uuid null`, `supply_id uuid null`, `grams_used numeric(12,3) null`,
`quantity numeric(14,4) null`, `unit text null`,
`ad_hoc_name text null`, `ad_hoc_price_per_kg numeric(18,6) null`,
`ad_hoc_unit_cost numeric(18,6) null`, `description text null`,
`amount numeric(18,6) null`, `sort_order int`.

**CHECK** per kind (the discriminator constraint):
```
kind='FILAMENT' → grams_used IS NOT NULL AND (filament_id IS NOT NULL OR ad_hoc_price_per_kg IS NOT NULL)
kind='SUPPLY'   → quantity   IS NOT NULL AND (supply_id  IS NOT NULL OR ad_hoc_unit_cost   IS NOT NULL)
kind='MANUAL'   → description IS NOT NULL AND amount IS NOT NULL
```
Ad-hoc components are what makes the Laboratory a laboratory: material that is not registered
yet can still be simulated.

---

## 9. `quoting` schema

### `quoting.quote_number_counter`
| Column | Type | Notes |
|---|---|---|
| counter_date | date | **PK** (organization-timezone date) |
| series | text | **PK** part, `QUOTE` / `PRODUCTION_ORDER` |
| last_sequence | int | not null |

Composite **PK** `(series, counter_date)`. Allocation is one statement
([ADR-0004](architecture/ADR-0004-quote-numbering-and-revisioning.md)):
```sql
INSERT INTO quoting.quote_number_counter (series, counter_date, last_sequence)
VALUES (:series, :date, 1)
ON CONFLICT (series, counter_date)
DO UPDATE SET last_sequence = quote_number_counter.last_sequence + 1
RETURNING last_sequence;
```

> **F-06 correction (2026-09-20).** The `quote`/`quote_revision` sections below previously
> described an earlier architecture-phase sketch (`number_text`, a computed `display_number`
> column, a single `customer_snapshot jsonb`, and a set of proposal-document fields — `notes`,
> `title`, `scope`, `technical_highlights`, `payment_terms`, `delivery_terms`, `warranty`,
> `calculation_engine_version` on the revision itself) that materially disagreed with the actually
> shipped S6 migration/model. The tables below document the REAL physical schema — the
> migration (`20260920200219_AddS6QuotingAndProductionCore`) and the EF model are the source of
> truth for naming/layout, [ADR-0020](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md)
> remains the source of truth for semantics.
>
> **F-06 correction, part 2 (S7/S14 scope authority gate, 2026-09-21).** The final sentence above
> — "a future document-rendering sprint... would add its own snapshot table, never retrofit them
> onto `quote_revision`" — was itself wrong: it misread [ADR-0016 §7](architecture/ADR-0016-document-render-snapshots.md),
> which is explicit that `payment_terms`, `delivery_terms` and `warranty` "default from settings
> but are **copied onto the quote revision** at issue" — i.e. onto `quote_revision` itself, the
> same table ADR-0003 already uses for every other frozen commercial value
> (`product_name_snapshot`, the customer snapshot columns). [DOMAIN-MODEL §8](DOMAIN-MODEL.md#8-quoting-module)
> and [DEFAULT-PROPOSAL-TEMPLATE §5](DEFAULT-PROPOSAL-TEMPLATE.md#5-content-sources) agree. S7
> adds exactly these nine nullable columns to `quoting.quote_revision` (migration
> `20260921101926_AddS7QuotePdfDocuments`): `title`, `scope`, `technical_highlights` (jsonb array
> of `{label, value}`), `technical_notes`, `out_of_scope`, `payment_terms`, `delivery_terms`,
> `warranty`, `notes` — plus `internal_notes` (never bound in any document catalogue). All are
> frozen at revision construction ("issue") and never re-read from Settings afterward; on revise
> they clone verbatim from the source revision unless the caller explicitly changes them.

### `quoting.quote`
| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** (UUID v7 — identity only, never `ORDER BY id`) |
| number | text(20) | not null (`260906-4`) |
| number_date | date | not null (organization-timezone business date) |
| number_sequence | int | not null |
| customer_id | uuid | null — plain ID reference, no FK (CLAUDE.md rule 11) |
| current_revision_id | uuid | not null — **FK** to `quote_revision.id`, `DEFERRABLE INITIALLY DEFERRED` (raw SQL; EF cannot express deferrable constraints, so left unmodeled in the fluent config) |
| created_at / created_by / updated_at / updated_by | timestamptz / uuid | application metadata |
| version | bigint | optimistic concurrency token (ADR-0011 §2) |

- **U** `ix_quote_number` on `(number)`.
- **U** `ix_quote_number_date_number_sequence` on `(number_date, number_sequence)`.

### `quoting.quote_revision`
| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** |
| quote_id | uuid | **FK** cascade to `quote.id` |
| revision_index | int | not null **CHECK** `ck_quote_revision_index_positive` (`>= 1`) |
| revision_suffix | text(8) | not null (`''`, `B`, `AA`) — persisted, not computed |
| status | text(16) | not null **CHECK** `ck_quote_revision_status` (7 states) |
| sales_channel_id | uuid | not null — plain ID reference, no FK |
| issued_at | timestamptz | not null |
| validity_days | int | not null |
| valid_until | date | not null |
| customer_id | uuid | null — plain ID reference, no FK |
| customer_name_snapshot | text(200) | null |
| customer_document_snapshot | text(32) | null |
| customer_contacts_snapshot | jsonb | null |
| customer_addresses_snapshot | jsonb | null |
| superseded_by_revision_id | uuid | null **FK** self, restrict |
| source_revision_id | uuid | null **FK** self, restrict |
| approved_at | timestamptz | null |
| approved_by | uuid | null |
| subtotal_amount | numeric(18,2) | not null |
| discount_amount | numeric(18,2) | not null |
| total_amount | numeric(18,2) | not null |
| total_cost_amount | numeric(18,2) | not null |
| expected_profit_amount | numeric(18,2) | not null |
| effective_margin_percent | numeric(9,6) | not null |
| title | text(200) | null — S7, proposal content, frozen at issue |
| scope | text(4000) | null — S7, proposal content, frozen at issue |
| technical_highlights | jsonb | null — S7, array of `{label, value}` (DOMAIN-MODEL §8 rule 1: deliberately generic, never a typed material/tolerance column) |
| technical_notes | text(4000) | null — S7, proposal content, frozen at issue |
| out_of_scope | text(4000) | null — S7, proposal content, frozen at issue |
| payment_terms | text(2000) | null — S7; defaults from `documents.default_payment_terms` **only on create**, then frozen (ADR-0016 §7) |
| delivery_terms | text(2000) | null — S7; defaults from `documents.default_delivery_terms` **only on create**, then frozen |
| warranty | text(2000) | null — S7; defaults from `documents.default_warranty` **only on create**, then frozen |
| notes | text(4000) | null — S7, customer-facing note, bound in the `QUOTE` catalogue as `quote.notes` |
| internal_notes | text(4000) | null — S7, operator-only; deliberately absent from every document binding catalogue |
| created_at / created_by / updated_at / updated_by | timestamptz / uuid | application metadata |

S7's proposal-content columns are all optional and, like every other revision field, immutable
once the revision is persisted: frozen in the SAME transaction as revision construction ("issue" —
STATE-MACHINES §2), never re-read from Settings by any later render (S7/S14 scope authority gate
§5, OPTION A). On revise, every field clones verbatim from the source revision unless the caller
supplies an explicit override (STATE-MACHINES §2 step 2/4).

The customer identity is frozen as SEPARATE scalar/jsonb snapshot columns (ADR-0003) — never a
single combined `customer_snapshot jsonb`. There is no persisted `display_number`: it is computed
at read time as `quote.number + revision.revision_suffix` ([`Quote.DisplayNumberFor`](../src/Modules/Verce.Modules.Quoting/Quote.cs)).

- **U** `ix_quote_revision_quote_id_revision_index` on `(quote_id, revision_index)`.
- **IX** `ix_quote_revision_expiration_eligible` — partial on `(valid_until)` filtered
  `WHERE superseded_by_revision_id IS NULL AND status IN ('GENERATED','SENT','NEGOTIATING')` —
  the expiration job's covering index (H-02).
- **IX** `ix_quote_revision_source_revision_id`, `ix_quote_revision_superseded_by_revision_id`.

### `quoting.quote_item`
`id` **PK**, `quote_revision_id` **FK** cascade, `line_number int`,
`source_quote_item_id uuid null` (provenance only, no FK — B-01: the client's only way to say
"this is the same commercial line as R(n) item X"; null means a genuinely new line),
`product_id uuid null`, `product_recipe_id uuid null`, `product_name_snapshot text not null`,
`description text null`, `quantity numeric(14,4) not null CHECK > 0`,
`unit_total_cost numeric(18,6) not null` (= `quote_item_cost_snapshot.estimated_unit_cost`,
denormalized for cheap reads), `cost_engine_version text not null`,
`desired_margin_percent numeric(9,6) not null`, `sales_channel_id uuid not null`
(plain cross-module ID reference to Pricing `SalesChannel`; no database FK),
`fee_rule_version_id uuid null` (reference only), `commission_percent numeric(9,6) not null`,
`fixed_fee_application text not null` **CHECK** (`PerUnit|PerOrder`),
`raw_fixed_fee numeric(18,6) not null`, `allocated_order_fee numeric(18,2) not null`,
`fixed_fee_per_unit numeric(18,6) not null`, `rounding_policy_applied text not null`,
`suggested_unit_price numeric(18,2) not null`, `commission_amount_per_unit numeric(18,2) not null`,
`fee_clamp_applied text null` (`MIN`/`MAX`), `manual_price_override numeric(18,2) null`
**CHECK** (null or `>= 0`), `price_overridden boolean not null`, `unit_price numeric(18,2) not null`,
`discount_kind text not null` **CHECK** (`None|Percent|Amount`), `discount_value numeric(18,6) not null`,
`discount_amount numeric(18,2) not null`, `net_unit_price numeric(18,2) not null`,
`line_total_amount numeric(18,2) not null`, `line_cost_amount numeric(18,2) not null`,
`line_fee_amount numeric(18,2) not null`, `expected_profit_amount numeric(18,2) not null`,
`effective_margin_percent numeric(9,6) not null`.

- **U** `(quote_revision_id, line_number)`.
- **IX** `(product_id)` (plain ID reference, no FK — see §16).

### `quoting.quote_item_cost_snapshot` — 1:1 with `quote_item`

> **B-02 correction (2026-09-20).** An earlier S6 draft collapsed this to a scalar
> `unit_total_cost`/`calculation_engine_version` pair directly on `quote_item`, deferring the
> whole breakdown as "needs S9/S10 inputs." That was wrong: every field below is exactly what S4's
> `CostEngine`/`ProductCostCalculator` already returns TODAY (materials with per-supply wastage
> breakdown, labor, machine, additional direct costs) — none of it needs Energy (S10) or
> per-machine costing (S9) inputs. Only a genuinely future concept (a real `energy_tariff_version_id`
> reference, smart-plug metered energy, a `machine_id` FK to a real Machine aggregate) remains
> deferred to those sprints; this table freezes everything CostEngine can compute right now, in
> full, per ADR-0003.

| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** |
| quote_item_id | uuid | **U**, **FK** cascade |
| engine_version | text | not null (`CostCalculationResult.EngineVersion`) |
| material_cost_before_wastage | numeric(18,6) | not null |
| material_wastage_cost | numeric(18,6) | not null |
| materials_total_cost | numeric(18,6) | not null |
| labor_minutes | numeric(14,4) | null (recipe has no labor step) |
| labor_hourly_rate | numeric(18,6) | null |
| labor_rate_source | text | null (`RECIPE_OVERRIDE|SETTINGS_DEFAULT`) |
| labor_cost | numeric(18,6) | not null |
| machine_minutes | numeric(14,4) | null (recipe has no machine step) |
| machine_hourly_rate | numeric(18,6) | null |
| machine_cost | numeric(18,6) | not null |
| additional_direct_costs_total | numeric(18,6) | not null |
| total_estimated_cost | numeric(18,6) | not null |
| output_quantity | int | not null |
| estimated_unit_cost | numeric(18,6) | not null — mirrored onto `quote_item.unit_total_cost` |

### `quoting.quote_item_material_snapshot` — 1:N per `quote_item` (B-02)
One row per recipe material line, frozen at issue exactly as `MaterialCostBreakdown` computed it —
never re-resolved against the Supply's current cost/stock later.

| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** |
| quote_item_id | uuid | **FK** cascade |
| line_number | int | not null, **U** with `quote_item_id` (ordering) |
| supply_id | uuid | not null (plain ID reference, no FK — CLAUDE.md rule 11) |
| supply_code_snapshot | text | not null |
| supply_name_snapshot | text | not null |
| entered_quantity | numeric(18,8) | not null |
| entered_unit | text | not null |
| normalized_quantity_base_unit | numeric(14,4) | not null |
| base_unit | text | not null |
| wastage_percent | numeric(9,6) | not null |
| effective_quantity_base_unit | numeric(14,4) | not null |
| cost_source | text | not null (`WEIGHTED_AVERAGE_ACQUISITION|MANUAL_OVERRIDE|...`) |
| cost_policy | text | not null |
| unit_cost_base_unit | numeric(18,6) | not null |
| cost_before_wastage | numeric(18,6) | not null |
| wastage_cost | numeric(18,6) | not null |
| cost_after_wastage | numeric(18,6) | not null |
| current_stock_base_unit_at_issue | numeric(14,4) | not null |
| exceeded_current_stock_at_issue | boolean | not null |

### `quoting.quote_item_additional_cost_snapshot` — 1:N per `quote_item` (B-02)
One row per recipe additional-cost line (e.g. packaging), frozen the same way.

| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** |
| quote_item_id | uuid | **FK** cascade |
| line_number | int | not null, **U** with `quote_item_id` |
| description | text | not null |
| amount | numeric(18,6) | not null |

### `quoting.quote_status_history` — append-only
`id` **PK**, `quote_revision_id` **FK** cascade, `from_status text null`, `to_status text not null`,
`changed_at timestamptz`, `changed_by uuid null`, `trigger text` **CHECK** (`USER|SYSTEM_JOB|EVENT`),
`reason text null`, `notes text null`.
**IX** `(quote_revision_id, changed_at)`.

### `quoting.quote_document` — planned (not yet built)
`id` **PK**, `quote_revision_id` **FK**, `generated_document_id uuid` **FK**,
`document_type_code text`, `is_current boolean`.
**U** partial `(quote_revision_id, document_type_code) WHERE is_current`.

> **S7 note (2026-09-21, corrected 2026-09-21 by the S7/S14 scope authority gate).** S7 does not
> create this join table. It ships the ADR-0016 §5 semantic directly on `documents.generated_document`
> instead (see §13): `render_request_id` is the row identity (ADR-0012 §22 idempotency key — a
> retried request converges, a deliberate re-render always inserts a new row), and a partial
> unique index on `(source_id, document_kind) WHERE is_current` guarantees at most one CURRENT
> document per revision at a time, with every earlier row retained and retrievable. A
> `GeneratedDocument` is found by querying that table with the QuoteRevision's own id, with no
> separate join row. (An earlier version of this note described a now-rejected `(source_id,
> document_kind, template_version)` unique identity — DATA-DICTIONARY names exactly that key as
> the trap that would block a legitimate re-issue; it was never shipped.) This table remains a
> genuine future option (e.g. if a revision ever needs
> more than one *kind* of current document) but nothing in S7 requires it.

---

## 10. `production` schema

> **S6/S9 split (ADR-0020 §A.8, 2026-09-20; naming corrected F-06).** Only
> `production.production_order` is created by S6 — the minimum slice the `QuoteApproved`
> transactional invariant needs (aggregate, full status enum, `QUEUED` creation,
> `QUEUED → CANCELED` supersession, `has_pending_revision`). The table below is S6's REAL physical
> schema (migration `20260920200219_AddS6QuotingAndProductionCore` + the EF model) — it carries
> only identity, numbering, the two plain ID references (`quote_id`/`quote_revision_id`, never a
> cross-module FK), status, the supersession pointer and application metadata. It has NO
> `customer_id`, `customer_name_snapshot`, `due_date`, `total_quantity`,
> `shipping_address_snapshot`, `notes`, or `started_at`/`completed_at`/`shipped_at`/`delivered_at`
> columns — those, along with `production_order_item`, `production_order_item_planned_material`,
> `production_order_item_actual_material` and `production_order_status_history` below, are S9's
> still-unbuilt operational expansion of this same aggregate (the shop floor: items, planned/
> actual material, printer/scheduling UX) — a **planned extension**, presented here as a sketch of
> the target shape, never as though it already exists in the S6 table.

### `production.production_order` — S6 current physical schema
| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** (UUID v7) |
| order_number | text(20) | not null (`260920-1`) |
| number_date | date | not null |
| number_sequence | int | not null |
| quote_id | uuid | not null — plain ID reference, no FK (same-quote lookup for the ADR-0020 §A.2 supersession matrix) |
| quote_revision_id | uuid | not null — plain ID reference, no FK; **the idempotency key** |
| status | text(16) | not null **CHECK** `ck_production_order_status` (6 states: `QUEUED\|IN_PRODUCTION\|READY\|SHIPPED\|DELIVERED\|CANCELED`) |
| has_pending_revision | bool | not null — advisory only, never a guard (ADR-0020 §A.4) |
| superseded_by_order_id | uuid | null **FK** self, restrict |
| cancellation_reason | text(64) | null (e.g. `SUPERSEDED_BY_REVISION`) |
| created_at / created_by / updated_at / updated_by | timestamptz / uuid | application metadata |
| version | bigint | optimistic concurrency token |

- **U** `ix_production_order_order_number` on `(order_number)`.
- **U** `ix_production_order_quote_revision_id` on `(quote_revision_id)` — guarantees at most one order per approved revision; also the `ON CONFLICT` target for the idempotent creation insert (B-04).
- **U** `ix_production_order_number_date_number_sequence` on `(number_date, number_sequence)`.
- **IX** `ix_production_order_quote_id`, `ix_production_order_superseded_by_order_id`.
- **IX** `ix_production_order_status_number_date` on `(status, number_date)`.

### S9 planned extension (sketch — none of the following exist yet)

### `production.production_order_item`
`id` **PK**, `production_order_id` **FK** cascade, `quote_item_id uuid null`,
`product_id uuid null`, `product_recipe_id uuid null`, `product_name_snapshot text`,
`quantity numeric(14,4)`, `quantity_produced numeric(14,4) default 0`,
`status text` **CHECK** (`PENDING|PRINTING|DONE|FAILED`), `notes`, `sort_order`.

### `production.production_order_item_planned_material`
`id` **PK**, `production_order_item_id` **FK** cascade, `material_kind text`,
`material_id uuid`, `material_name_snapshot text`, `color_name_snapshot text null`,
`planned_quantity numeric(14,4)`, `unit text`, `sort_order`.
The exploded BOM the shop floor prints — snapshotted so a later recipe edit cannot change a
document already on the bench.

### `production.production_order_item_actual_material`
`id` **PK**, `production_order_item_id` **FK** cascade, `material_kind text`, `material_id uuid`,
`actual_quantity numeric(14,4) not null CHECK >= 0`,
`unit_cost_at_consumption numeric(18,6) not null`, `recorded_at timestamptz`,
`recorded_by uuid null`, `stock_movement_id uuid null` **FK**, `notes`.
**IX** `(production_order_item_id)`.

### `production.production_order_status_history`
Same shape as `quote_status_history`, keyed by `production_order_id`.

---

## 11. `sales` schema

### `sales.sale`
`id` **PK**, `sale_number text` **U**, `customer_id uuid null`, `sales_channel_id uuid` **FK**,
`quote_revision_id uuid null` **U** (nullable-unique: one sale per revision at most),
`sold_at timestamptz`, `sold_date date`, `status text` **CHECK** (`CONFIRMED|CANCELED`),
`gross_amount numeric(18,2)`, `discount_amount numeric(18,2)`, `net_amount numeric(18,2)`,
`channel_fee_amount numeric(18,2)`, `shipping_amount numeric(18,2)`,
`total_cost_amount numeric(18,2)`, `gross_profit_amount numeric(18,2)`,
`effective_margin_percent numeric(9,6)`, `cost_basis text` **CHECK** (`ESTIMATED|ACTUAL|MIXED`),
`external_order_code text null`, `notes`.

- **IX** partial `(sold_date desc) WHERE status = 'CONFIRMED'` — every revenue report.
- **IX** `(sales_channel_id, sold_date)`, `(customer_id)`.

`cost_basis` records whether `total_cost_amount` is still the quote estimate or has been
reconciled with actual consumption ([ADR-0006](architecture/ADR-0006-estimated-vs-actual-cost.md)).

### `sales.sale_item`
`id` **PK**, `sale_id` **FK** cascade, `line_number int`, `product_id uuid null`,
`product_name_snapshot text`, `quantity numeric(14,4)`, `unit_price numeric(18,2)`,
`discount_amount numeric(18,2)`, `line_total_amount numeric(18,2)`,
`unit_cost_amount numeric(18,6)`, `line_cost_amount numeric(18,2)`,
`channel_fee_amount numeric(18,2)`, `gross_profit_amount numeric(18,2)`,
`quote_item_id uuid null`.
**IX** `(product_id)`, **U** `(sale_id, line_number)`.

---

## 12. `finance` schema

### `finance.expense_category`
`id` **PK**, `name` **U**, `default_treatment text` **CHECK**
(`OPERATING_EXPENSE|INVENTORY_PURCHASE|ASSET_ACQUISITION`), `is_active`.

### `finance.expense`
| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** |
| expense_category_id | uuid | **FK** restrict |
| description | text | not null |
| amount | numeric(18,2) | not null **CHECK** `> 0` |
| incurred_on | date | not null |
| paid_on | date | null |
| payment_method | text | null |
| supplier_name | text | null |
| document_number | text | null |
| accounting_treatment | text | not null **CHECK** (3 values) |
| filament_lot_id | uuid | null **FK** |
| supply_lot_id | uuid | null **FK** |
| machine_id | uuid | null |
| attachment_path | text | null |
| notes | text | null |

**The double-counting guards** ([ADR-0013](architecture/ADR-0013-expense-inventory-double-counting.md)):
- **U** `(filament_lot_id)` where not null — one lot, at most one expense.
- **U** `(supply_lot_id)` where not null.
- **CHECK** `NOT (filament_lot_id IS NOT NULL AND supply_lot_id IS NOT NULL)`.
- **CHECK** `(filament_lot_id IS NOT NULL OR supply_lot_id IS NOT NULL)
  → accounting_treatment = 'INVENTORY_PURCHASE'`.
- **IX** partial `(incurred_on desc) WHERE accounting_treatment = 'OPERATING_EXPENSE'` — the
  dashboard's cost query.

---

## 13. `documents` schema

> **S7 template-engine final pass (2026-09-21).** An earlier S7 correction pass fixed
> `documents.generated_document`'s identity (`render_request_id`, ADR-0012 §22, replacing the
> defective `UNIQUE (source_id, document_kind, template_version)` an independent review found)
> but left the default proposal rendered by a compiled C# string builder, with `document_type`,
> `document_template` and `document_template_version` undocumented as an "S14 sketch." Per the
> S7/S14 scope authority gate's OPTION A decision, this pass built that persisted template engine
> **within S7**: the four tables below are the actual, current schema — not a sketch — and the
> compiled renderer (`QuotePdfHtmlTemplate`) no longer exists. S14's remaining scope is the
> **authoring UI only** (draft → publish workflow); every table, the binding catalogue, the
> generic block-tree renderer and the seeded default proposal already exist.

### `documents.document_type` — reference data
`code text` **PK** (`QUOTE` shipped and default-templated in S7; `PRODUCTION_ORDER` and
`SHIPPING_LABEL` identity rows seeded for future sprints), `name text`, `is_active boolean`.

### `documents.document_template` — **SD**, master data
`id uuid` **PK** (v7), `document_type_code text` **FK** restrict, `name text`,
`is_default boolean`, `is_active boolean`, `deleted_at`, application metadata
(`created_at`/`created_by`/`updated_at`/`updated_by`/`version`).
- **U** partial `ix_document_template_default_per_type` on `document_type_code
  WHERE is_default AND deleted_at IS NULL` — at most one default template per type.

### `documents.document_template_version`
`id uuid` **PK** (v7), `document_template_id` **FK** cascade, `version_number int`,
`status text` **CHECK** `ck_document_template_version_status` (`DRAFT|PUBLISHED|ARCHIVED`),
`schema_version int not null`, `definition jsonb not null` (the block tree + `theme` tokens),
`page_setup jsonb not null` (size, margins, header/footer `repeatOn`), `published_at`,
`published_by`, application metadata.
- **U** `ix_document_template_version_document_template_id_version_numb` on
  `(document_template_id, version_number)`.
- **U** partial `ix_document_template_version_one_open_draft` on `document_template_id
  WHERE status = 'DRAFT'` — at most one open draft (no draft/publish workflow exists yet to
  create one — S7 always mints version 1 already `PUBLISHED`, at seed time).

Every binding path inside `definition` is validated against the document type's closed catalogue
(`QuoteBindingCatalogue` for `QUOTE`) at construction time
([ADR-0007 §3](architecture/ADR-0007-document-template-engine.md),
`DocumentTemplateValidator`). A `PUBLISHED` row is immutable — the entity exposes no mutation
method at all (proven structurally, not just by convention, in `DocumentTemplateTests`).

### `documents.generated_document`
| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** (UUID v7) |
| render_request_id | uuid | not null **U** — ADR-0012 §22 idempotency key, minted once per deliberate render request (at approval, or at an operator's explicit generate/reissue click); a retried delivery of the SAME request converges here, a deliberate re-render mints a new one |
| document_type_code | text | not null **FK** restrict → `document_type.code` |
| source_type | text | not null (`QUOTE_REVISION` today) |
| source_id | uuid | not null — plain ID reference to the source aggregate, no FK across modules (CLAUDE.md rule 11) |
| document_template_version_id | uuid | not null **FK** restrict → `document_template_version.id` — the exact immutable layout used, frozen forever (ADR-0016 §1) |
| purpose | text | not null **CHECK** `ck_generated_document_purpose` (`PREVIEW`/`ISSUED`) — always `ISSUED` today; `PREVIEW` generation is not wired up yet |
| is_current | boolean | not null — the most recent row for `(source_type, source_id, document_type_code)`; the ONLY field ever mutated on an existing row (`MarkSuperseded()`, ADR-0016 §5) |
| render_data_snapshot_json | jsonb | not null — the frozen `QuotePdfInput` (company/brand/proposal-content snapshot, resolved once per render, never re-read afterward) |
| html_sha256 / html_storage_key | text | not null — content-addressed HTML alongside the PDF |
| pdf_sha256 / pdf_storage_key | text | not null |
| pdf_size_bytes | bigint | not null **CHECK** `ck_generated_document_pdf_size_positive` (`> 0`) |
| chromium_version | text | null |
| render_engine_version | text | not null |
| brand_asset_version_ids | uuid[] | not null — every distinct `BrandAssetVersion` actually used (header AND footer logo roles may differ); never a scalar |
| generated_by_user_id | uuid | null (null for an outbox-driven render — no interactive actor) |
| issued_at | timestamptz | not null |
| reissue_reason | text | null — the operator-typed "why" for an explicit re-issue (ADR-0016 §5); null for a first-ever render |
| created_at / created_by / updated_at / updated_by | timestamptz / uuid | application metadata |
| version | bigint | optimistic concurrency token |

- **U** on `render_request_id` — the real idempotency identity (ADR-0012 §22); a concurrent race
  on the SAME request converges under an advisory lock keyed `(source_id, document_type_code)`,
  the loser reading back the winner's row.
- **U** partial `ix_generated_document_current_per_source` on
  `(source_type, source_id, document_type_code) WHERE is_current` — at most one current document
  at a time, enforced by Postgres itself.
- **IX** on `document_template_version_id`.

No file is ever deleted. Files live in `IDocumentStorage` (local filesystem, content-addressed at
`{sha256[0:2]}/{sha256}.{ext}`) so identical renders deduplicate and no filename derives from
user input; `Documents:StorageRoot` is a dedicated persistent volume in production
(`verce_documents`, see [OPERATIONS §4.2](OPERATIONS.md#42-document-storage-consistency)),
mirroring brand-asset storage's exact consistency model.

---

## 14. `ai` schema

### `ai.ai_settings` — single row
`id` **PK**, `provider text`, `api_key_encrypted bytea null`, `api_key_last_four char(4) null`,
`api_key_updated_at timestamptz null`,
`api_key_recovery_required boolean not null default false` *(set by `recover-data-protection`
when the stored ciphertext became unreadable; the UI shows "reinsira a chave da API")*,
`default_model text`, `fallback_model text`,
`default_prompt text`, `max_output_tokens int`, `temperature numeric(4,2)`,
`include_customer_names boolean not null default false`,
`monthly_budget_amount numeric(18,2) null`, `is_enabled boolean not null default false`.
**CHECK** single row. `api_key_encrypted` is protected with ASP.NET Core Data Protection and is
**excluded from every projection** by convention plus an architecture test.

### `ai.ai_insight_run`
`id` **PK**, `triggered_by uuid null`, `trigger_kind text`, `model text`,
`prompt_template_used text`, `period_from date`, `period_to date`,
`dataset_fingerprint char(64)`, `dataset_summary jsonb`, `status text`,
`started_at`, `completed_at`, `prompt_tokens int null`, `completion_tokens int null`,
`estimated_cost numeric(18,4) null`, `error_message text null`.
**IX** `(status, started_at desc)`.

### `ai.ai_insight_result`
`id` **PK**, `ai_insight_run_id` **FK** cascade, `kind text` **CHECK**
(`FACT|HYPOTHESIS|RECOMMENDATION`), `priority int`, `title text`, `body text`,
`related_entity_type text null`, `related_entity_id uuid null`, `metrics jsonb null`,
`sort_order int`.

---

## 15. `reporting` schema

**No tables in v1.** Only views, added from S12:

| View | Purpose |
|---|---|
| `reporting.v_sales_monthly` | revenue, cost, profit, margin per month/channel |
| `reporting.v_product_profitability` | profit and margin per product per period |
| `reporting.v_quote_funnel` | quotes by outcome per period (CR-12.1) |
| `reporting.v_material_consumption` | filament grams and supply units consumed per period |
| `reporting.v_cost_variance` | estimated vs actual per production order item |

Materialized tables are deliberately **not** created. Every metric in
[DATA-DICTIONARY](DATA-DICTIONARY.md) is derivable from the transactional tables above; the
expected data volume (ARCHITECTURE §13) does not justify a second copy of the truth.
Materialization becomes justified only when a measured query exceeds ~500 ms.

---

## 16. Cross-module foreign keys

These FKs cross schema boundaries. They are created at database level with `ON DELETE RESTRICT`
and generate **no** EF navigation property (ARCHITECTURE §3.1).

| From | To |
|---|---|
| `catalog.product_filament_component.filament_id` | `inventory.filament.id` |
| `catalog.product_supply_component.supply_id` | `inventory.supply.id` |
| `catalog.product_recipe.machine_id` | `energy.machine.id` |
| `catalog.product.default_sales_channel_id` | `pricing.sales_channel.id` |
| `quoting.quote.customer_id` | `customers.customer.id` |
| `quoting.quote_item.product_id` | `catalog.product.id` |
| `production.production_order.quote_revision_id` | `quoting.quote_revision.id` |
| `sales.sale.quote_revision_id` | `quoting.quote_revision.id` |
| `finance.expense.filament_lot_id` | `inventory.filament_lot.id` |
| `energy.energy_consumption_session.production_order_item_id` | `production.production_order_item.id` |
| `settings.branding_assignment.brand_asset_id` | `settings.brand_asset.id` |
| `documents.generated_document.brand_asset_version_ids[]` | `settings.brand_asset_version.id` (array — **no declarative FK**, see note) |

Logical cross-module reference (no physical FK):

- `quoting.quote_revision.sales_channel_id` is a required UUID reference to Pricing `SalesChannel`.
  Pricing owns `SalesChannel`; Quoting persists its identity as part of the immutable commercial
  snapshot. No database FK is defined. Application/composition logic validates channel existence
  and activity when constructing the snapshot.

Deliberate exceptions:

- `inventory.stock_movement.material_id` (§4, polymorphic — two possible target tables).
- `documents.generated_document.brand_asset_version_ids` — a `uuid[]`, which PostgreSQL cannot
  constrain with a declarative FK. Integrity is preserved by the fact that
  **`brand_asset_version` rows are never deleted** (ADR-0015), plus an integration test
  asserting every referenced version resolves. A junction table was considered and rejected: it
  adds a join and a write for an array read only when explaining one document.

---

## 17. Seed data (S1/S2)

| Table | Seed |
|---|---|
| `settings.app_setting` | the key list in DOMAIN-MODEL §14 |
| `settings.company_profile` | one row, VERCE defaults where known |
| `settings.brand_asset_type` | the seven types in DOMAIN-MODEL §14 |
| `settings.brand_asset` (+ version 1) | VERCE `PRIMARY_LOGO`, `COMPACT_LOGO`, `SYMBOL`, `FAVICON` (S2) |
| `settings.branding_assignment` | the four roles, pointing at the seeded VERCE assets |
| `pricing.sales_channel` | "Venda Direta" (`DIRECT`) with a 0% / R$ 0,00 fee rule version valid from today |
| `inventory.supply_category` | Consumíveis, Componentes, Embalagens, Etiquetas, Acessórios |
| `inventory.filament_material` | PLA, PETG, TPU, ABS, ASA |
| `finance.expense_category` | the ten categories in DOMAIN-MODEL §11 |
| `documents.document_type` | `QUOTE`, `PRODUCTION_ORDER`, `SHIPPING_LABEL` (`DocumentsSeedService`) |
| `documents.document_template` (+ version 1, `PUBLISHED`) | the default `VERCE \| Proposta Comercial Padrão` — the exact block tree/page-setup/theme specified in [DEFAULT-PROPOSAL-TEMPLATE](DEFAULT-PROPOSAL-TEMPLATE.md), seeded as ordinary data (`DefaultProposalTemplateSeedData`), validated at seed time by the same `DocumentTemplateValidator` a future S14 publish action will use |
| `platform.role` | `Owner`, `Operator`, `Viewer` — **roles only** |

> **No user is ever seeded.** No default account, no default password, no `admin/admin`. The
> first `Owner` is created by the `bootstrap-owner` CLI command
> ([ADR-0009 §6](architecture/ADR-0009-authentication-strategy.md),
> [OPERATIONS §2](OPERATIONS.md#2-first-installation)). An architecture test asserts that no
> migration or seed path inserts a row into `platform.user`.

The "Venda Direta" channel with a zero fee rule is **mandatory** seed data: the pricing engine
has no special case for direct sales, so the row must exist for the direct formula to work
(DOMAIN-MODEL §7).
