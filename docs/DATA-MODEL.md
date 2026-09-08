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
  | **Master data** — operator-managed catalogues | `id uuid` (UUID v7) | `IMasterData` | `supply_category`, `product_category`, `expense_category`, `filament_material`, `filament_brand`, `sales_channel`, `machine`, `energy_tariff` |
  | **Reference data** — closed, system-owned enumeration | stable textual `code` | `IReferenceData` | `document_type`, `brand_asset_type` |
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
  > `stock_movement.occurred_at`, `outbox_message_attempt.started_at`) do not duplicate it.
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

- **U** partial: `(document) WHERE document IS NOT NULL AND deleted_at IS NULL`.
- **IX** `(name)` — trigram index `gin (name gin_trgm_ops)` for the home search box.
- **IX** partial `(is_active) WHERE deleted_at IS NULL`.

### `customers.customer_address`
`id` **PK**, `customer_id` **FK**→customer (cascade), `label`, `zip_code`, `street`, `number`,
`complement`, `district`, `city`, `state char(2)`, `country char(2) default 'BR'`,
`is_primary boolean`, `is_default_shipping boolean`, `notes`.

- **U** partial `(customer_id) WHERE is_primary` — at most one primary.
- **U** partial `(customer_id) WHERE is_default_shipping`.
- **IX** `(customer_id)`.

---

## 4. `inventory` schema

### `inventory.supply_category`
`id` **PK**, `name` **U**, `kind text` **CHECK** (`CONSUMABLE|COMPONENT|PACKAGING|LABEL|ACCESSORY|OTHER`),
`is_active boolean`.

### `inventory.supply` — **SD**
| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** |
| code | text | null, **U** partial where not null and not deleted |
| name | text | not null |
| supply_category_id | uuid | **FK** restrict |
| unit | text | **CHECK** in the `SupplyUnit` set |
| current_unit_cost | numeric(18,6) | not null, **CHECK** `>= 0` |
| description | text | null |
| tracks_stock | boolean | not null default true |
| is_active | boolean | not null |
| deleted_at | timestamptz | null |

**IX** `(supply_category_id)`, `gin (name gin_trgm_ops)`.

### `inventory.supply_cost_history`
`id` **PK**, `supply_id` **FK**, `unit_cost numeric(18,6)`, `valid_from date not null`,
`valid_until date null`, `source text` (`MANUAL|PURCHASE|IMPORT`), `source_lot_id uuid null`,
`notes`.

- **EXCLUDE USING gist** `(supply_id WITH =, daterange(valid_from, valid_until, '[)') WITH &&)`
  — windows for one supply can never overlap.
- **IX** `(supply_id, valid_from desc)`.

### `inventory.filament_material`, `inventory.filament_brand`
Lookup tables: `id` **PK**, `name` **U**, `is_active`. Materials are data, not a C# enum, so
adding PETG-CF requires no deploy.

### `inventory.filament` — **SD**
| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** |
| filament_material_id | uuid | **FK** restrict |
| filament_brand_id | uuid | **FK** restrict |
| commercial_name | text | not null (e.g. "Preto Eclipse") |
| color_name | text | not null |
| color_hex | char(7) | null |
| diameter | text | **CHECK** `MM_175` / `MM_285` |
| current_price_per_kg | numeric(18,6) | not null, **CHECK** `> 0` |
| density_g_cm3 | numeric(8,4) | null |
| notes | text | null |
| is_active | boolean | not null |
| deleted_at | timestamptz | null |

**U** partial `(filament_material_id, filament_brand_id, commercial_name, color_name, diameter)
WHERE deleted_at IS NULL`.
**IX** `gin ((commercial_name || ' ' || color_name) gin_trgm_ops)`.

### `inventory.filament_price_history`
Same shape and same exclusion constraint as `supply_cost_history`, keyed by `filament_id`,
column `price_per_kg numeric(18,6)`.

### `inventory.filament_lot`
`id` **PK**, `filament_id` **FK**, `lot_code text null`, `purchased_at date not null`,
`purchased_weight_grams numeric(12,3) not null CHECK > 0`,
`purchase_amount numeric(18,2) not null CHECK >= 0`, `supplier_name text null`,
`notes text null`.
Derived `price_per_kg` is **not stored** — it is `purchase_amount / (purchased_weight_grams/1000)`,
exposed as a generated column `price_per_kg numeric(18,6) GENERATED ALWAYS AS (...) STORED`
so reports can index it.
**IX** `(filament_id, purchased_at desc)`.

### `inventory.supply_lot`
Analogous: `supply_id`, `purchased_quantity numeric(14,4)`, `purchase_amount`, `purchased_at`,
`lot_code`, `supplier_name`, generated `unit_cost numeric(18,6)`.

### `inventory.stock_movement` — append-only
| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** |
| material_kind | text | **CHECK** `FILAMENT` / `SUPPLY` |
| material_id | uuid | polymorphic, **no FK** (see note) |
| lot_id | uuid | null |
| direction | text | **CHECK** `IN` / `OUT` / `ADJUSTMENT` |
| quantity | numeric(14,4) | signed; grams for filament |
| unit_cost_at_movement | numeric(18,6) | not null |
| occurred_at | timestamptz | not null |
| reason | text | **CHECK** enumerated |
| reference_type | text | null (`PRODUCTION_ORDER_ITEM`, `FILAMENT_LOT`, `STOCK_COUNT`) |
| reference_id | uuid | null |
| notes | text | null |

**IX** `(material_kind, material_id, occurred_at desc)`, `(reference_type, reference_id)`.

*Polymorphic FK note:* `material_id` cannot have a declarative FK because it targets two
tables. This is the one place the design accepts a soft reference. It is protected by
(a) a `CHECK` pairing `material_kind` with the allowed `reference_type` values, and
(b) an integration test asserting every `material_id` resolves. The alternative — two nullable
FK columns with a XOR check — was rejected because it makes every stock query branch.

### `inventory.stock_count`
`id` **PK**, `material_kind`, `material_id`, `counted_at timestamptz`,
`counted_quantity numeric(14,4)`, `system_quantity_at_count numeric(14,4)`,
`adjustment_movement_id uuid` **FK**→stock_movement, `notes`.

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

### `quoting.quote`
`id` **PK**, `number_date date not null`, `number_sequence int not null`,
`number_text text not null` (`260906-4`, denormalized for search),
`customer_id uuid null` (**FK** restrict), `current_revision_id uuid null` (deferrable **FK**).

- **U** `(number_date, number_sequence)`.
- **U** `(number_text)`.
- **IX** `(customer_id)`, `(number_date desc)`.

### `quoting.quote_revision`
| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** |
| quote_id | uuid | **FK** cascade |
| revision_index | int | not null **CHECK** `>= 1` |
| revision_suffix | text | not null (`''`, `B`, `AA`) — persisted, not computed |
| display_number | text | not null (`260906-4B`) |
| status | text | not null **CHECK** enumerated (7 states) |
| sales_channel_id | uuid | **FK** restrict |
| issued_at | timestamptz | not null |
| issued_date | date | not null (org tz) |
| validity_days | int | not null |
| valid_until | date | not null |
| customer_snapshot | jsonb | not null |
| subtotal_amount | numeric(18,2) | not null |
| discount_amount | numeric(18,2) | not null |
| total_amount | numeric(18,2) | not null |
| total_cost_amount | numeric(18,2) | not null |
| expected_profit_amount | numeric(18,2) | not null |
| effective_margin_percent | numeric(9,6) | not null |
| notes | text | null (printed) |
| internal_notes | text | null (**never printed** — absent from the binding catalogue) |
| title | text | null — project/proposal title |
| scope | text | null |
| technical_highlights | jsonb | not null default `[]` — array of `{label, value}` |
| technical_notes | text | null |
| out_of_scope | text | null |
| payment_terms | text | null — copied from settings at issue |
| delivery_terms | text | null — copied from settings at issue |
| warranty | text | null — copied from settings at issue |
| source_revision_id | uuid | null **FK** self |
| superseded_by_revision_id | uuid | null **FK** self |
| calculation_engine_version | text | not null |

- **U** `(quote_id, revision_index)`.
- **U** `(display_number)`.
- **IX** partial `(status, valid_until) WHERE superseded_by_revision_id IS NULL AND status IN
  ('GENERATED','SENT','NEGOTIATING')` — the expiration job's covering index.
- **IX** `(quote_id, revision_index desc)`, `(sales_channel_id)`, `(issued_date desc)`.

### `quoting.quote_item`
`id` **PK**, `quote_revision_id` **FK** cascade, `line_number int`, `product_id uuid null`,
`product_recipe_id uuid null`, `product_name_snapshot text not null`, `description text null`,
`quantity numeric(14,4) not null CHECK > 0`,
`unit_cost_amount numeric(18,6)`, `suggested_unit_price numeric(18,2)`,
`unit_price numeric(18,2) not null CHECK > 0`, `price_overridden boolean not null default false`,
`discount_kind text` **CHECK** (`NONE|PERCENT|AMOUNT`), `discount_value numeric(18,6)`,
`discount_amount numeric(18,2)`, `net_unit_price numeric(18,2)`,
`line_total_amount numeric(18,2)`, `line_cost_amount numeric(18,2)`,
`line_fee_amount numeric(18,2)`, `desired_margin_percent numeric(9,6)`,
`sales_channel_id uuid` **FK**, `expected_profit_amount numeric(18,2)`,
`effective_margin_percent numeric(9,6)`, `sort_order int`.

- **U** `(quote_revision_id, line_number)`.
- **IX** `(product_id)`.

### `quoting.quote_item_cost_snapshot` — 1:1 with `quote_item`
| Column | Type | Notes |
|---|---|---|
| quote_item_id | uuid | **PK**, **FK** cascade |
| filament_cost | numeric(18,6) | not null |
| supplies_cost | numeric(18,6) | not null |
| packaging_cost | numeric(18,6) | not null |
| manual_cost | numeric(18,6) | not null |
| energy_kwh | numeric(12,4) | not null |
| energy_price_per_kwh | numeric(18,6) | not null |
| energy_cost | numeric(18,6) | not null |
| energy_source | text | `ESTIMATED|ESTIMATED_OVERRIDE|MANUAL|SMART_PLUG` |
| energy_tariff_version_id | uuid | null (reference only, value already frozen) |
| machine_id | uuid | null |
| machine_hours | numeric(12,6) | not null |
| machine_hourly_rate | numeric(18,6) | not null |
| machine_cost | numeric(18,6) | not null |
| labor_minutes | int | not null |
| labor_hourly_rate | numeric(18,6) | not null |
| labor_cost | numeric(18,6) | not null |
| direct_cost | numeric(18,6) | not null |
| wastage_rate | numeric(9,6) | not null |
| wastage_cost | numeric(18,6) | not null |
| unit_total_cost | numeric(18,6) | not null |
| commission_percent | numeric(9,6) | not null |
| fixed_fee | numeric(18,2) | not null |
| fixed_fee_application | text | not null |
| fee_clamp_applied | text | null (`MIN`/`MAX`) |
| fee_rule_version_id | uuid | null (reference only) |
| price_rounding_policy | text | not null |
| calculation_engine_version | text | not null |
| breakdown | jsonb | not null — full explanation tree |

**IX** `gin (breakdown jsonb_path_ops)` only if "explain" search is ever needed; not created in v1.
The typed columns above carry every value reports need, so the JSONB is never queried in
aggregate ([ADR-0003](architecture/ADR-0003-cost-and-price-snapshots.md)).

### `quoting.quote_status_history` — append-only
`id` **PK**, `quote_revision_id` **FK** cascade, `from_status text null`, `to_status text not null`,
`changed_at timestamptz`, `changed_by uuid null`, `trigger text` **CHECK** (`USER|SYSTEM_JOB|EVENT`),
`reason text null`, `notes text null`.
**IX** `(quote_revision_id, changed_at)`.

### `quoting.quote_document`
`id` **PK**, `quote_revision_id` **FK**, `generated_document_id uuid` **FK**,
`document_type_code text`, `is_current boolean`.
**U** partial `(quote_revision_id, document_type_code) WHERE is_current`.

---

## 10. `production` schema

### `production.production_order`
`id` **PK**, `order_number text` **U**, `number_date date`, `number_sequence int`,
`quote_revision_id uuid not null` **U** ← *the idempotency key*, **FK** restrict,
`customer_id uuid null`, `customer_name_snapshot text`, `status text` **CHECK** (6 states),
`due_date date null`, `total_quantity numeric(14,4)`, `shipping_address_snapshot jsonb null`,
`notes text null`, `has_pending_revision boolean not null default false`,
`superseded_by_order_id uuid null` **FK** self, `cancellation_reason text null`,
`started_at`, `completed_at`, `shipped_at`, `delivered_at` (all `timestamptz null`).

- **U** `(quote_revision_id)` — guarantees at most one order per approved revision.
- **IX** partial `(status, due_date) WHERE status IN ('QUEUED','IN_PRODUCTION')` — the queue view.
- **U** `(number_date, number_sequence)`.

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

### `documents.document_type` — lookup
`code text` **PK** (`QUOTE`, `PRODUCTION_ORDER`, `SHIPPING_LABEL`), `name`, `is_active`.

### `documents.document_template` — **SD**
`id` **PK**, `document_type_code text` **FK**, `name text`, `is_default boolean`,
`is_active`, `deleted_at`.
**U** partial `(document_type_code) WHERE is_default AND deleted_at IS NULL`.

### `documents.document_template_version`
`id` **PK**, `document_template_id` **FK** cascade, `version_number int`,
`status text` **CHECK** (`DRAFT|PUBLISHED|ARCHIVED`), `schema_version int not null`,
`definition jsonb not null` (block tree + `theme` tokens), `page_setup jsonb not null`
(size, orientation, margins, header/footer `repeatOn`), `published_at`, `published_by`, `notes`.
- **U** `(document_template_id, version_number)`.
- **U** partial `(document_template_id) WHERE status = 'DRAFT'` — at most one open draft.

Every binding path inside `definition` is validated against the document type's catalogue at
publish time ([ADR-0007 §3](architecture/ADR-0007-document-template-engine.md)). A `PUBLISHED`
row is immutable.

### `documents.generated_document` — historical evidence
| Column | Type | Notes |
|---|---|---|
| id | uuid | **PK** |
| document_type_code | text | **FK** |
| source_type | text | `QUOTE_REVISION`, `PRODUCTION_ORDER`, … |
| source_id | uuid | not null |
| purpose | text | **CHECK** `PREVIEW` / `ISSUED` |
| render_request_id | uuid | not null **U** — outbox idempotency key ([ADR-0012 §13](architecture/ADR-0012-domain-events-and-outbox.md#22-consumer-idempotency)) |
| document_template_version_id | uuid | **FK** restrict |
| pdf_path | text | content-addressed |
| pdf_sha256 | char(64) | not null |
| pdf_size_bytes | bigint | not null |
| rendered_html_path | text | null (content-addressed) |
| rendered_html_sha256 | char(64) | null |
| render_data_snapshot | jsonb | **not null** — the fully resolved render context |
| brand_asset_version_ids | uuid[] | resolved at render time |
| generated_at | timestamptz | not null |
| generated_by | uuid | null |
| issued_at | timestamptz | null |
| sent_at | timestamptz | null |
| render_duration_ms | int | not null |
| chromium_version | text | not null |
| render_engine_version | text | not null |
| is_current | boolean | not null |

- **IX** `(source_type, source_id, generated_at desc)`.
- **IX** partial `(generated_at) WHERE purpose = 'PREVIEW'` — the pruning job.
- **U** partial `(source_type, source_id, document_type_code) WHERE is_current`.
- **U** `(render_request_id)`.
- **IX** `(pdf_sha256)`.

The consumer inserts with `ON CONFLICT (render_request_id) DO NOTHING`, so a retried outbox
delivery cannot create a second `ISSUED` document. The key identifies the **request**, not the
content: a retry reuses it and deduplicates, while a deliberate re-issue is a new request and
correctly produces a new document, even against the same template version.

Rules enforced by [ADR-0016](architecture/ADR-0016-document-render-snapshots.md): rows with
`purpose = 'ISSUED'` are **append-only — never updated, never deleted**; re-rendering inserts a
new row; `PREVIEW` rows are prunable after `documents.preview_retention_days`.

Files live in `IDocumentStorage` (local filesystem in v1) at
`{sha256[0:2]}/{sha256}.{ext}`, so identical renders deduplicate and no filename derives from
user input. **Files are never deleted in v1** — historical integrity over storage optimization.

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
| `quoting.quote_revision.sales_channel_id` | `pricing.sales_channel.id` |
| `quoting.quote_item.product_id` | `catalog.product.id` |
| `production.production_order.quote_revision_id` | `quoting.quote_revision.id` |
| `sales.sale.quote_revision_id` | `quoting.quote_revision.id` |
| `finance.expense.filament_lot_id` | `inventory.filament_lot.id` |
| `energy.energy_consumption_session.production_order_item_id` | `production.production_order_item.id` |
| `settings.branding_assignment.brand_asset_id` | `settings.brand_asset.id` |
| `documents.generated_document.brand_asset_version_ids[]` | `settings.brand_asset_version.id` (array — **no declarative FK**, see note) |

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
| `documents.document_type` | QUOTE, PRODUCTION_ORDER, SHIPPING_LABEL |
| `documents.document_template` (+ version 1, `PUBLISHED`) | **`VERCE \| Proposta Comercial Padrão`**, `is_default` for `QUOTE` (S7) — see [DEFAULT-PROPOSAL-TEMPLATE](DEFAULT-PROPOSAL-TEMPLATE.md) |
| `platform.role` | `Owner`, `Operator`, `Viewer` — **roles only** |

> **No user is ever seeded.** No default account, no default password, no `admin/admin`. The
> first `Owner` is created by the `bootstrap-owner` CLI command
> ([ADR-0009 §6](architecture/ADR-0009-authentication-strategy.md),
> [OPERATIONS §2](OPERATIONS.md#2-first-installation)). An architecture test asserts that no
> migration or seed path inserts a row into `platform.user`.

The "Venda Direta" channel with a zero fee rule is **mandatory** seed data: the pricing engine
has no special case for direct sales, so the row must exist for the direct formula to work
(DOMAIN-MODEL §7).
