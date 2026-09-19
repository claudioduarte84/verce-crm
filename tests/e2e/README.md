# E2E harness — safe PostgreSQL targeting

The Playwright suite in this directory is authenticated, browser-driven and **destructive**: it
creates and drops real databases, applies real migrations, and runs real SQL against them. This
document is the contract for how it decides *which* PostgreSQL server it is allowed to touch.
Read this before running any script in `scripts/` locally, and before wiring the suite into CI.

## The one rule

**Every process this harness spawns resolves its PostgreSQL target through
`resolveE2ePostgresTarget()` in [`e2e-env.cjs`](./e2e-env.cjs), and nothing else ever parses a
connection string a second time.** The application's own connection string
(`ConnectionStrings__Verce`, handed to `dotnet run`/the webServer) and every destructive
`docker exec`/`CREATE`/`DROP`/migration are built from that *same* resolved object. There is no
code path in this harness where the application and a destructive control command can disagree
about which server they mean.

## `VERCE_E2E_POSTGRES_CONTAINER` — mandatory, no fallback

```bash
export VERCE_E2E_POSTGRES_CONTAINER=my-disposable-postgres
```

This environment variable names the Docker container the harness is allowed to run destructive
commands against. **It is required** — there is no default, and in particular **there is no
fallback to `verce-postgres`** (the shared development container). If it is unset, every script
fails immediately with:

```
E2E_POSTGRES_CONTAINER_REQUIRED: set VERCE_E2E_POSTGRES_CONTAINER to a disposable PostgreSQL
container. There is no fallback to a shared container.
```

The container must:

- actually exist and be running (`docker inspect` must succeed and report `State.Running`);
- publish PostgreSQL on `5432/tcp` to a loopback host port (`127.0.0.1` or `localhost`);
- **not** be the protected container — see below.

## Protected resources

Two things are permanently off-limits to every destructive operation this harness performs:

- the Docker container named **`verce-postgres`** — by name, by its full Docker ID, by any short
  ID prefix Docker accepts, or by any other selector that `docker inspect` resolves to the same
  container. The harness inspects the *configured* selector and the *protected* container (when
  it exists) and compares their immutable `.Id` — a selector cannot evade this by spelling the
  protected container's identity differently.
- the database named **`verce`** — the shared development database, rejected by name regardless
  of which server it lives on.

Both checks are independent and both are enforced: a disposable container name paired with the
database name `verce`, or the protected container paired with a disposable-looking database name,
are each rejected on their own.

Every disposable database name must start with `verce_e2e`, `verce_test`, or `verce_s4`.

## Ambiguous connection strings are rejected, not guessed

If you override the target with `ConnectionStrings__Verce`, that string is parsed once for its
recognized endpoint properties (`Host`/`Server`, `Port`, `Username`/`User ID`/`UserID`/`User Name`,
`Database`/`Initial Catalog`, `Password`/`Pwd`). **Naming the same property twice — even under a
different alias, and even when both occurrences hold the identical value — is a hard failure**,
never resolved by picking a first or last occurrence and never accepted just because the two
values happen to agree today:

```
Host=127.0.0.1;Host=evil.example;...        → E2E_AMBIGUOUS_CONNECTION_TARGET
Host=127.0.0.1;Host=127.0.0.1;...           → E2E_AMBIGUOUS_CONNECTION_TARGET (equal values, still rejected)
Host=127.0.0.1;Server=evil.example;...      → E2E_AMBIGUOUS_CONNECTION_TARGET
Port=61198;Port=5432;...                    → E2E_AMBIGUOUS_CONNECTION_TARGET
Username=test;User ID=other;...             → E2E_AMBIGUOUS_CONNECTION_TARGET
Database=verce_e2e;Database=verce;...       → E2E_AMBIGUOUS_CONNECTION_TARGET
Database=verce_e2e;Database=verce_e2e;...   → E2E_AMBIGUOUS_CONNECTION_TARGET (equal values, still rejected)
Password=a;Pwd=b;...                        → E2E_AMBIGUOUS_CONNECTION_TARGET
```

This is deliberately stricter than a general-purpose driver: real ADO.NET-family parsers
(Npgsql included) commonly resolve a repeated key by taking the *last* occurrence, while a naive
custom parser might take the *first*. A harness that can `DROP DATABASE` must never let its own
notion of "the target" diverge from the one the .NET application actually connects to, so instead
of re-implementing Npgsql's precedence rules, an ambiguous string is refused before anything is
inspected, connected to, or executed. The check is a raw occurrence count, never a value
comparison — two occurrences that agree today are still two sources of truth that could disagree
the moment either one is edited.

**The diagnostic never includes a credential value.** Whichever property is duplicated — host,
port, user, database or password — the thrown `E2E_AMBIGUOUS_CONNECTION_TARGET` message names
only the semantic property (e.g. `'password'`) and the alias spellings that produced the
conflict (e.g. `Password, Pwd`); it never echoes a raw `key=value` pair, a value, or the full
connection string. This applies uniformly, not only to `Password`/`Pwd`, so there is exactly one
sanitized code path to audit.

The application's declared host/port are also cross-checked against the *actual* published port
of the inspected container. A mismatch fails closed as `E2E_DATABASE_TARGET_MISMATCH` before any
SQL runs.

## The run lock — the entire destructive lifecycle, not just the middle

Every mutating operation this harness can perform — `CREATE DATABASE`, `DROP DATABASE`, applying
migrations, and any `psql` invocation against a resolved target — requires the E2E run lock
(`acquireE2eRunLock()`/`e2e-env.cjs`) to already be held by the calling process or one of its
ancestors; each of those functions asserts this itself; there is no way to call them without the
lock and have them silently proceed. A destructive script therefore always follows the same
order:

```
acquire run lock
  → resolve/validate the target (read-only)
  → pre-drop / provision / test work / post-drop
release run lock
```

A script that spawns Playwright as a sub-step (see `scripts/verify-custom-owner-email.cjs`) holds
the lock across the *whole* lifecycle, including the pre- and post-drop either side of it — the
spawned Playwright process inherits the same ownership as an authenticated reentrant descendant
(the mechanism `run-lock-holder.cjs`/`verify-run-lock.cjs` already implement and which this
document does not change) rather than contending for a second, independent lock. See
[`run-lock-holder.cjs`](./run-lock-holder.cjs) for the lock's own cryptographic protocol
(exclusive loopback ownership, per-session capability, nonce/HMAC authentication) — nothing in
this document alters it.

## Local workflow

```bash
# Start a disposable PostgreSQL 17 the harness may destroy freely — tmpfs storage, a dynamic
# host port, never the shared verce-postgres container or its persistent volume.
docker run -d --name my-e2e-postgres \
  -e POSTGRES_DB=postgres -e POSTGRES_USER=verce -e POSTGRES_PASSWORD=verce_dev_only \
  --tmpfs /var/lib/postgresql/data \
  -p 127.0.0.1::5432 \
  postgres:17-alpine

export VERCE_E2E_POSTGRES_CONTAINER=my-e2e-postgres

# Full browser suite (provisions verce_e2e, applies migrations, runs Playwright end to end).
npm test

# Harness safety self-test (negative-case proofs: ambiguous connection strings, the protected
# container by name/full ID/short ID, target mismatch, role-identifier validation, and the full
# destructive lifecycle serializing under concurrent contention).
npm run test:harness-safety

# Run-lock cryptographic protocol certification (unaffected by any of the above).
npm run test:run-lock

# Custom-Owner-email regression (spawns Playwright as a lock-held reentrant descendant).
npm run test:custom-owner-email

# When finished, remove the disposable container — it owns no persistent volume, so this
# discards all its data.
docker rm -f my-e2e-postgres
```

If `ConnectionStrings__Verce` is left unset, the harness targets database `verce_e2e` (or
`$VERCE_E2E_DATABASE`) on the configured container using the default `verce`/`verce_dev_only`
credentials — override any of those fields explicitly if your disposable container was started
with different ones.

## CI configuration

CI must provision its own disposable PostgreSQL 17 container (a service container or an
equivalent ephemeral instance) for every E2E run and export its name as
`VERCE_E2E_POSTGRES_CONTAINER` before invoking any script in this directory — exactly the same
contract a local developer follows above. CI must never point this variable at a shared,
long-lived container, and must tear its disposable container down at the end of the job
regardless of outcome.

## Why the harness fails closed

This suite runs real `CREATE DATABASE`/`DROP DATABASE`/migration/SQL commands with no
confirmation prompt. Every guard in this document exists because a harness with that much power
and a wrong or ambiguous target is a data-loss incident, not a test failure. Consequently:

- a missing target is a hard error, never a silent default;
- an ambiguous connection string is a hard error, never a best-effort guess;
- the protected container and the protected database are each checked on their own terms, so
  evading one check by satisfying the other is not sufficient;
- every destructive command uses the container's own immutable Docker ID, resolved once, so a
  container replaced or renamed between validation and execution cannot silently redirect a
  command that already passed its checks;
- nothing destructive runs without the run lock already held, enforced in the functions
  themselves rather than left to each caller's discipline.

See [`docs/architecture/ADR-0018-s4-stateless-cost-laboratory-and-acquisition-basis.md`](../../docs/architecture/ADR-0018-s4-stateless-cost-laboratory-and-acquisition-basis.md)
for the unrelated `costing.default_wastage_rate` migration this harness also exercises in S4
certification, and `docs/OPERATIONS.md` §7 for the analogous fail-closed posture the production
composition root takes toward secrets.
