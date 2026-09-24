// Single canonical source for E2E harness configuration (M-S2-002). Every
// process the suite spawns resolves its database connection and Owner identity here.
'use strict'

const { execFileSync, spawn } = require('node:child_process')
const { createHmac, randomBytes, randomUUID, timingSafeEqual } = require('node:crypto')
const { readFileSync, rmSync } = require('node:fs')
const { tmpdir } = require('node:os')
const { join, resolve } = require('node:path')

const API_PROJECT_DIR = resolve(__dirname, '..', '..', 'src', 'Verce.Api')
const CONTROL_DATABASE_NAME = 'postgres'
const DEV_DATABASE_NAME = 'verce'
const PROTECTED_POSTGRES_CONTAINERS = new Set(['verce-postgres'])
const DEFAULT_E2E_DATABASE_NAME = 'verce_e2e'
const DEFAULT_E2E_USER = 'verce'
const DEFAULT_E2E_PASSWORD = 'verce_dev_only'
const DEFAULT_OWNER_EMAIL = 'e2e-owner@example.test'
const DEFAULT_OWNER_NAME = 'E2E Owner'
const DEFAULT_LOCK_TIMEOUT_MS = 300_000
const DATABASE_NAME_PATTERN = /^[A-Za-z0-9_]+$/
const ROLE_NAME_PATTERN = /^[A-Za-z_][A-Za-z0-9_]{0,62}$/

function splitConnectionString(connectionString) {
  if (typeof connectionString !== 'string') throw new Error('E2E connection string must be a string')
  const segments = []
  let start = 0
  let quote
  for (let index = 0; index < connectionString.length; index += 1) {
    const character = connectionString[index]
    if (quote) {
      if (character === quote) {
        if (connectionString[index + 1] === quote) index += 1
        else quote = undefined
      }
      continue
    }
    if (character === '\'' || character === '"') quote = character
    else if (character === ';') {
      segments.push(connectionString.slice(start, index))
      start = index + 1
    }
  }
  if (quote) throw new Error('Invalid E2E connection string: unterminated quoted value')
  segments.push(connectionString.slice(start))
  return segments
}

function parseConnectionString(connectionString) {
  return splitConnectionString(connectionString)
    .map((raw) => raw.trim())
    .filter(Boolean)
    .map((raw) => {
      const separator = raw.indexOf('=')
      if (separator < 1) throw new Error(`Invalid E2E connection string segment: "${raw}"`)
      const key = raw.slice(0, separator).trim()
      const value = raw.slice(separator + 1).trim()
      if (!key) throw new Error(`Invalid E2E connection string segment: "${raw}"`)
      return { raw, key, normalizedKey: key.toLowerCase(), value }
    })
}

function normalizeDatabaseName(value) {
  if (typeof value !== 'string') throw new Error('E2E database name must be a string')
  const trimmed = value.trim()
  if (!trimmed) throw new Error('E2E database name must not be empty')
  if (trimmed.length > 63 || !DATABASE_NAME_PATTERN.test(trimmed)) {
    throw new Error(`Unsafe E2E database name "${value}". Names must match ${DATABASE_NAME_PATTERN} and be at most 63 characters.`)
  }
  return trimmed.toLowerCase()
}

function assertDisposableDatabaseName(value) {
  const databaseName = normalizeDatabaseName(value)
  if (databaseName === DEV_DATABASE_NAME) {
    throw new Error(
      `E2E harness refused to target the shared development database "${DEV_DATABASE_NAME}". ` +
        `Set ConnectionStrings__Verce (or VERCE_E2E_DATABASE) to a disposable E2E database, ` +
        `e.g. "${DEFAULT_E2E_DATABASE_NAME}".`,
    )
  }
  if (!/^(verce_e2e|verce_test|verce_s4)(?:_[a-z0-9_]+)?$/.test(databaseName)) {
    throw new Error(`Unsafe E2E database name "${value}". Disposable databases must start with verce_e2e, verce_test, or verce_s4.`)
  }
  return databaseName
}

/** Rejects a PostgreSQL role identifier the harness does not deliberately support (H-05/H-23).
 * The harness only ever needs plain, ASCII, unquoted-in-source role names (e.g.
 * `verce_s4_harness`); anything else is refused outright rather than partially escaped, so there
 * is no identifier grammar this function accepts that could still be read as a SQL expression. */
function assertSafeRoleName(value) {
  if (typeof value !== 'string' || !ROLE_NAME_PATTERN.test(value)) {
    throw new Error(`Unsafe PostgreSQL role identifier "${value}". E2E role names must match ${ROLE_NAME_PATTERN} (ASCII letters, digits and underscore; may not start with a digit).`)
  }
  return value
}

/** Delimited-identifier form of a validated role name, for `OWNER <role>` — never a string
 * literal, and never string-concatenated without going through {@link assertSafeRoleName} first. */
function quotePostgresRoleIdentifier(value) {
  return `"${assertSafeRoleName(value).replaceAll('"', '""')}"`
}

function quotePostgresIdentifier(identifier) {
  const safeIdentifier = assertDisposableDatabaseName(identifier)
  return `"${safeIdentifier.replaceAll('"', '""')}"`
}

/** Semantic groups for EVERY connection-string endpoint property this harness recognizes,
 * database/catalog included — there is exactly one place that decides what counts as a
 * duplicate, not one for database and a separately-maintained one for everything else (R-01).
 * Anything not listed here (SSL options, etc.) passes through unexamined and is deliberately
 * dropped when the canonical connection string is rebuilt from a resolved target (see
 * {@link resolveE2ePostgresTarget}) — the harness only ever re-emits fields it has itself
 * validated. */
const ENDPOINT_ALIAS_TO_SEMANTIC = new Map([
  ['host', 'host'], ['server', 'host'],
  ['port', 'port'],
  ['username', 'user'], ['user id', 'user'], ['userid', 'user'], ['user name', 'user'], ['user', 'user'],
  ['database', 'database'], ['initial catalog', 'database'],
  ['password', 'password'], ['pwd', 'password'],
])

/**
 * H-01/R-01/§3-§9: an E2E connection configuration that names the SAME endpoint semantic
 * property more than once — whether through the identical key or a different alias of it
 * (`Host=a;Host=b`, `Host=a;Server=b`, `Port=1;Port=2`, `Username=a;User ID=b`,
 * `Database=a;Initial Catalog=b`, `Password=a;Pwd=b`) — is rejected outright rather than
 * resolved by picking a first or last occurrence. **Equal values are rejected exactly the same
 * as conflicting ones** (R-01): this is a count check on raw occurrences, never a value
 * comparison, because two occurrences that happen to agree today are still two sources of truth
 * that could disagree after either one is edited, and a harness that can `DROP DATABASE` must
 * never let that be possible. A real ADO.NET-family parser (Npgsql included) commonly takes the
 * LAST occurrence; a naive custom parser might take the FIRST; refusing ambiguity outright is
 * simpler and strictly safer than re-implementing Npgsql's precedence rules for a test harness —
 * see docs/OPERATIONS.md §7.1 and tests/e2e/README.md.
 *
 * R-02: the thrown diagnostic identifies only the semantic property name and the literal alias
 * *spellings* involved (`Password`, `Pwd`) — never a raw `key=value` pair, never a value, and
 * never the connection string itself. This applies uniformly to every property, not only
 * `password`/`pwd`, so there is exactly one sanitized code path to audit rather than a sanitized
 * one for secrets and a leaky one for everything else.
 */
function extractConnectionEndpoint(connectionString) {
  const pairs = parseConnectionString(connectionString)
  const bySemantic = new Map()
  for (const pair of pairs) {
    const semantic = ENDPOINT_ALIAS_TO_SEMANTIC.get(pair.normalizedKey)
    if (!semantic) continue
    if (!bySemantic.has(semantic)) bySemantic.set(semantic, [])
    bySemantic.get(semantic).push(pair)
  }
  const endpoint = {}
  for (const [semantic, occurrences] of bySemantic) {
    if (occurrences.length > 1) {
      const aliasesUsed = [...new Set(occurrences.map((pair) => pair.key.trim()))]
      throw new Error(
        `E2E_AMBIGUOUS_CONNECTION_TARGET: duplicate semantic property '${semantic}' ` +
        `(specified ${occurrences.length} times via: ${aliasesUsed.join(', ')}). Provide exactly one ` +
        'value per endpoint property — even equal values are rejected, never compared, because this ' +
        'harness can destroy databases and must never guess which one Npgsql would use.',
      )
    }
    endpoint[semantic] = occurrences[0].value
  }
  return endpoint
}

function resolveE2eRunId() {
  const configured = String(process.env.VERCE_E2E_RUN_ID || '').trim().toLowerCase()
  if (configured && /^[a-z0-9_]{1,40}$/.test(configured)) return configured
  const generated = randomUUID().replaceAll('-', '')
  process.env.VERCE_E2E_RUN_ID = generated
  return generated
}

function resolveE2eDatabaseName() {
  return process.env.VERCE_E2E_DATABASE || `${DEFAULT_E2E_DATABASE_NAME}_${resolveE2eRunId()}`
}

/** Delegates its duplicate-alias detection entirely to {@link extractConnectionEndpoint} (R-01)
 * — there is no second, independently-maintained notion of "conflicting database aliases"
 * anymore. Equal-value duplicates (`Database=a;Database=a`, `Database=a;Initial Catalog=a`) are
 * rejected exactly like conflicting ones, by the same occurrence-count check every other
 * semantic property uses. */
function resolveDatabaseNameFromConnectionString(connectionString) {
  const database = extractConnectionEndpoint(connectionString).database
  return database === undefined ? normalizeDatabaseName(resolveE2eDatabaseName()) : normalizeDatabaseName(database)
}

/** Fails fast before database creation, migrations, setup or any provisioning SQL. */
function assertDisposableE2eDatabase(connectionString) {
  return assertDisposableDatabaseName(resolveDatabaseNameFromConnectionString(connectionString))
}

/** `docker inspect <selector>` with a clean, harness-shaped failure instead of a raw exec
 * stack trace — used only for read-only identity/state inspection, never for a destructive
 * command. `selector` may be a container name, a full ID, a short ID, or anything else Docker
 * itself accepts; whichever form is given, the immutable `.Id` on the result is what every
 * later comparison and every later `docker exec` uses (H-06). */
function inspectDockerContainer(selector) {
  let stdout
  try {
    stdout = execFileSync('docker', ['inspect', selector], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] })
  } catch (error) {
    throw new Error(`E2E_DATABASE_TARGET_UNAVAILABLE: could not inspect Docker target "${selector}" (${String(error.message || error).split('\n')[0]}).`)
  }
  let parsed
  try { parsed = JSON.parse(stdout) } catch { parsed = [] }
  const inspected = parsed[0]
  if (!inspected) throw new Error(`E2E_DATABASE_TARGET_UNAVAILABLE: Docker selector "${selector}" did not resolve to a container.`)
  return inspected
}

/** Fast, Docker-independent rejection of the protected container's literal name(s) — correct
 * even if the protected container cannot currently be inspected at all, which the ID-based check
 * below cannot guarantee on its own. */
function assertSelectorIsNotProtectedByName(selector) {
  const normalized = String(selector).trim().toLowerCase()
  for (const protectedName of PROTECTED_POSTGRES_CONTAINERS) {
    if (normalized === protectedName.toLowerCase()) {
      throw new Error(`E2E_PROTECTED_DATABASE_TARGET: "${selector}" is the protected container "${protectedName}".`)
    }
  }
}

/** Read-only: resolves the protected container's own canonical, immutable ID right now, if it
 * exists. Used only as a comparison value — this never becomes a target, and nothing here mutates
 * or even connects to it. */
function resolveProtectedContainerId() {
  for (const protectedName of PROTECTED_POSTGRES_CONTAINERS) {
    try { return inspectDockerContainer(protectedName).Id } catch { /* protected container not inspectable right now; the name check still applies */ }
  }
  return undefined
}

/**
 * H-02: rejects the protected container by literal name (see {@link assertSelectorIsNotProtectedByName})
 * AND by full Docker ID, short Docker ID, or any other Docker-supported alias that resolves to the
 * SAME canonical container identity. The pre-hardening guard compared selector strings only, which
 * a selector of the protected container's own Docker ID could sail straight through; `docker
 * inspect` resolves any of those spellings to one immutable `.Id`, so comparing THAT is what
 * actually closes the gap, independent of which name or ID happened to be configured.
 */
function assertContainerIsNotProtected(selector, inspected) {
  assertSelectorIsNotProtectedByName(selector)
  const protectedId = resolveProtectedContainerId()
  if (protectedId && inspected.Id === protectedId) {
    throw new Error(`E2E_PROTECTED_DATABASE_TARGET: "${selector}" resolves to the protected container (id ${protectedId}) under a different name or ID.`)
  }
}

/**
 * THE single authoritative E2E PostgreSQL target (H-01). Resolved fresh on every call — never
 * cached — from:
 *
 *   1. the mandatory `VERCE_E2E_POSTGRES_CONTAINER` Docker selector: inspected for its immutable
 *      identity, protection status (H-02), running state and published port, using that same
 *      immutable ID for every later `docker exec` (H-06) — there is deliberately no fallback to
 *      any shared container;
 *   2. the optional `ConnectionStrings__Verce` override: parsed once for its endpoint aliases,
 *      with any duplicate semantic key rejected outright as `E2E_AMBIGUOUS_CONNECTION_TARGET`
 *      rather than approximated (H-01/§7-§9) — this harness never re-implements Npgsql's own
 *      alias-precedence rules, it simply refuses to guess which one Npgsql would have picked;
 *
 * cross-validated so the application's declared host/port PROVABLY match the inspected
 * container's published port before this returns. Every caller that ever talks to this
 * PostgreSQL server — building the application's own connection string, or issuing a
 * `docker exec` for CREATE/DROP/migrate/psql — goes through this one function; nothing downstream
 * parses a connection string a second time or keeps its own notion of "the target".
 *
 * @returns {{containerId: string, containerName: string, host: string, publishedPort: string,
 *   internalPort: string, database: string, user: string, password: string, connectionString: string}}
 */
function resolveE2ePostgresTarget() {
  const configured = process.env.ConnectionStrings__Verce
  // Parsed exactly ONCE: the ambiguity check (R-01) and every field below (host, port, user,
  // password, database) all read from this same extraction — there is no second parse that
  // could disagree with it.
  const endpoint = configured ? extractConnectionEndpoint(configured) : {}
  const database = assertDisposableDatabaseName(endpoint.database ?? resolveE2eDatabaseName())

  const selector = String(process.env.VERCE_E2E_POSTGRES_CONTAINER ?? '').trim()
  if (!selector) throw new Error('E2E_POSTGRES_CONTAINER_REQUIRED: set VERCE_E2E_POSTGRES_CONTAINER to a disposable PostgreSQL container. There is no fallback to a shared container.')
  assertSelectorIsNotProtectedByName(selector)

  const inspected = inspectDockerContainer(selector)
  assertContainerIsNotProtected(selector, inspected)
  if (!inspected.State?.Running) throw new Error(`E2E_DATABASE_TARGET_UNAVAILABLE: "${selector}" is not running.`)
  const published = inspected.NetworkSettings?.Ports?.['5432/tcp']?.[0]
  if (!published?.HostPort) throw new Error(`E2E_DATABASE_TARGET_UNAVAILABLE: "${selector}" does not publish PostgreSQL port 5432.`)

  const host = endpoint.host ?? '127.0.0.1'
  if (!['localhost', '127.0.0.1'].includes(String(host).toLowerCase())) {
    throw new Error(`E2E_DATABASE_TARGET_MISMATCH: application host "${host}" is not a loopback address; the E2E harness only targets a local disposable container.`)
  }
  const port = String(endpoint.port ?? published.HostPort)
  if (port !== published.HostPort) {
    throw new Error(`E2E_DATABASE_TARGET_MISMATCH: application port ${port} does not match "${selector}" (container ${inspected.Id}) published PostgreSQL port ${published.HostPort}.`)
  }

  const user = endpoint.user || DEFAULT_E2E_USER
  const password = endpoint.password ?? DEFAULT_E2E_PASSWORD

  return {
    containerId: inspected.Id,
    containerName: String(inspected.Name || selector).replace(/^\//, ''),
    host,
    publishedPort: published.HostPort,
    internalPort: '5432',
    database,
    user,
    password,
    connectionString: `Host=${host};Port=${published.HostPort};Database=${database};Username=${user};Password=${password}`,
  }
}

/** The application's own connection string — always literally `resolveE2ePostgresTarget()`'s
 * canonical string (H-01): there is no separate code path that could re-derive a different
 * value from the same inputs. */
function resolveE2eConnectionString() {
  return resolveE2ePostgresTarget().connectionString
}

function controlPsql(argumentsAfterPsql, target = resolveE2ePostgresTarget()) {
  assertE2eRunLockHeld()
  return execFileSync(
    'docker',
    ['exec', target.containerId, 'psql', '-v', 'ON_ERROR_STOP=1', '-U', target.user, '-d', CONTROL_DATABASE_NAME, ...argumentsAfterPsql],
    { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] },
  ).trim()
}

function controlPsqlFromInput(sql, argumentsAfterPsql, target = resolveE2ePostgresTarget()) {
  assertE2eRunLockHeld()
  return execFileSync(
    'docker',
    ['exec', '-i', target.containerId, 'psql', '-v', 'ON_ERROR_STOP=1', '-U', target.user, '-d', CONTROL_DATABASE_NAME, ...argumentsAfterPsql],
    { input: sql, encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'] },
  ).trim()
}

function runE2ePsql(databaseName, argumentsAfterPsql, target = resolveE2ePostgresTarget()) {
  assertE2eRunLockHeld()
  const database = assertDisposableDatabaseName(databaseName)
  return execFileSync('docker',
    ['exec', target.containerId, 'psql', '-v', 'ON_ERROR_STOP=1', '-U', target.user, '-d', database, ...argumentsAfterPsql],
    { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim()
}

/** Creates only a validated, disposable database. The control connection is PostgreSQL's neutral
 * maintenance database, never the protected application database. H-03: requires the E2E run
 * lock to already be held — there is no path through this function that can run unlocked.
 * H-05: the owner role is a validated, delimited identifier, never a string literal. */
function ensureDatabaseExists(databaseName, target = resolveE2ePostgresTarget()) {
  assertE2eRunLockHeld()
  const safeDatabaseName = assertDisposableDatabaseName(databaseName)
  const exists = controlPsqlFromInput("SELECT 1 FROM pg_database WHERE datname = :'target_database';\n", ['-v', `target_database=${safeDatabaseName}`, '-tA'], target)
  if (exists === '1') return safeDatabaseName
  controlPsql(['-c', `CREATE DATABASE ${quotePostgresIdentifier(safeDatabaseName)} OWNER ${quotePostgresRoleIdentifier(target.user)}`], target)
  return safeDatabaseName
}

/** H-03: DROP requires the E2E run lock to already be held — enforced here, not merely by caller
 * discipline, so no current or future call site can destroy a database outside the lock. */
function dropE2eDatabaseIfExists(databaseName, target = resolveE2ePostgresTarget()) {
  assertE2eRunLockHeld()
  const safeDatabaseName = assertDisposableDatabaseName(databaseName)
  // Playwright-owned API/browser processes can still be closing pooled connections when global
  // teardown runs. FORCE terminates only sessions on this validated disposable database.
  controlPsql(['-c', `DROP DATABASE IF EXISTS ${quotePostgresIdentifier(safeDatabaseName)} WITH (FORCE)`], target)
}

/** Applies EF Core migrations after the same disposable-database guard used for provisioning.
 * H-03: also requires the E2E run lock. */
function applyMigrations(target = resolveE2ePostgresTarget()) {
  assertE2eRunLockHeld()
  execFileSync('dotnet', ['run', '--no-build', '--configuration', 'Release', '--project', API_PROJECT_DIR, '--', 'migrate'], {
    cwd: API_PROJECT_DIR,
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, ConnectionStrings__Verce: target.connectionString },
  })
}

/** Resolves the target ONCE and threads it through both destructive steps (H-01/H-03), so
 * provisioning can never observe a different container/database than the one it just created. */
function provisionE2eDatabase(target = resolveE2ePostgresTarget()) {
  assertE2eRunLockHeld()
  ensureDatabaseExists(target.database, target)
  applyMigrations(target)
  return target.database
}

// ---------------------------------------------------------------------------------------------
// E2E run lock (B1 / M-S2-002)
//
// The whole mutable E2E environment — the disposable database, its migrations, the Playwright
// webServer, the fixed ports, `.playwright` storage state, the setup projects, the tests and the
// teardown — is owned by ONE run at a time, and ownership IS an exclusive listening socket on a
// dedicated loopback port held by `run-lock-holder.cjs`.
//
// Why a socket: bind() is arbitrated by the kernel. At most one socket may listen on
// 127.0.0.1:<port>, and every competing bind fails atomically with EADDRINUSE. Acquisition is
// therefore a single atomic operation whose result IS the decision — there is no observe-then-act
// window, and no destructive step anywhere in the protocol. A process that loses the race cannot
// remove, rename, displace or "recover" the winner's ownership, because the protocol gives it
// nothing to act on: the only thing it may do is attempt its own bind and be refused.
//
// Ownership ends only when the holder's own socket closes — explicit release, holder exit, or
// the owning process dying (the OS closes the socket for us, measured at ~75ms even after
// SIGKILL). That replaces the entire previous owner.json / PID-liveness / stale-age / quarantine
// protocol, which could not offer compare-and-swap semantics over `rename()` and therefore could
// displace a freshly installed owner based on a stale observation.
//
// The port is fixed and documented rather than derived: the harness already reserves 7246 (API),
// 7247-7249 (restart-persistence specs) and 4173 (preview), so the run lock takes 7245 — the slot
// immediately below that reserved block, so it cannot collide with a port the harness itself
// binds. Tests may pass an explicit `port` to exercise the primitive in isolation; the harness
// itself always uses the default.
const E2E_RUN_LOCK_PORT = 7245
const RUN_LOCK_HOLDER_SCRIPT = join(__dirname, 'run-lock-holder.cjs')
const RUN_LOCK_POLL_INTERVAL_MS = 50
const RUN_LOCK_HOLDER_GRACE_MS = 10_000
const RUN_LOCK_HOLDER_ENV = 'VERCE_E2E_RUN_LOCK_HOLDER'
/** Must stay byte-identical to run-lock-holder.cjs's AUTH_PROTOCOL_LABEL. */
const AUTH_PROTOCOL_LABEL = 'VERCE_E2E_RUN_LOCK_AUTH_V1'

/** The lock this process owns, if any. Ownership is per-process, never inferred from disk. */
let activeRunLock
let exitReleaseRegistered = false

function wait(milliseconds) {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, milliseconds)
}

/** Used only to answer "is a process I already have a relationship with still alive?" — never to
 * decide whether someone else's ownership may be taken away. */
function isProcessRunning(pid) {
  if (!Number.isInteger(pid) || pid <= 0) return false
  try { process.kill(pid, 0); return true } catch { return false }
}

function readRunLockStatus(statusPath) {
  try { return JSON.parse(readFileSync(statusPath, 'utf8')) } catch { return undefined }
}

/**
 * `<holderPid>:<port>:<capability>` exported to descendants.
 *
 * Parsing this marker proves NOTHING. It is a claim, not authority: any process can set an
 * environment variable, so a live PID and a matching port must never be accepted as evidence of
 * ownership (that was the forgeable-marker defect). The marker is only a hint about WHICH holder
 * to challenge and WITH WHAT KEY — authority exists exclusively after `authenticateWithHolder()`
 * verifies an HMAC proof that only a holder possessing this exact capability could have produced
 * for this exact, freshly generated nonce. No answer phrased in words is ever authority.
 */
function parseInheritedRunLock() {
  const raw = String(process.env[RUN_LOCK_HOLDER_ENV] ?? '')
  const separator = raw.indexOf(':')
  const secondSeparator = raw.indexOf(':', separator + 1)
  if (separator < 1 || secondSeparator < 0) return undefined
  const holderPid = Number(raw.slice(0, separator))
  const port = Number(raw.slice(separator + 1, secondSeparator))
  const capability = raw.slice(secondSeparator + 1)
  if (!Number.isInteger(holderPid) || holderPid <= 0 || !Number.isInteger(port) || port <= 0 || !capability) return undefined
  return { holderPid, port, capability }
}

/** Domain-separated canonical challenge message. Byte-identical to the holder's serialization
 * (run-lock-holder.cjs), so a proof is bound to this protocol version, this port, this holder
 * session and this single nonce. */
function canonicalChallenge(port, holderPid, nonce) {
  return `${AUTH_PROTOCOL_LABEL}\n${port}\n${holderPid}\n${nonce}`
}

/** Transport only: sends a (non-secret) nonce to whatever is listening and returns its raw first
 * reply line. It carries no secret, so an impersonating listener learns nothing from it, and the
 * bounded timeouts mean a listener that accepts but never answers cannot hang the harness. */
function requestHolderProof(port, nonce) {
  const probe = "const net=require('node:net');let nonce='';process.stdin.setEncoding('utf8');"
    + "process.stdin.on('data',(c)=>{nonce+=c});process.stdin.on('end',()=>{"
    + "const s=net.connect({host:'127.0.0.1',port:Number(process.argv[1])});let d='';s.setEncoding('utf8');"
    + "const done=()=>{process.stdout.write(d.split('\\n')[0]);process.exit(0)};"
    + "s.on('connect',()=>{s.write('CHALLENGE '+nonce.trim()+'\\n')});"
    + "s.on('data',(c)=>{d+=c;if(d.includes('\\n'))done();if(d.length>4096)done()});"
    + "s.on('end',done);s.on('error',()=>{process.exit(1)});"
    + "setTimeout(()=>{process.exit(1)},3000).unref()});"
  try {
    return execFileSync(process.execPath, ['-e', probe, String(port)], {
      input: `${nonce}\n`, encoding: 'utf8', timeout: 6_000,
    }).trim()
  } catch {
    return ''
  }
}

/**
 * Authenticates the process actually listening on the lock port by CHALLENGE-RESPONSE.
 *
 * The capability is a shared secret, so it is never transmitted: we send a fresh 256-bit nonce,
 * the holder returns HMAC-SHA256(capability, canonical challenge), and we recompute that HMAC
 * locally and compare it in constant time. A foreign listener that merely occupies the port
 * cannot produce the proof — replying `AUTHORIZED`, replying a random `PROOF`, replaying a
 * previously observed proof, staying silent, or answering anything else all fail here, and the
 * impersonator never learns the capability because it never crosses the socket.
 *
 * Any failure whatsoever (refused connection, timeout, malformed answer, wrong proof) returns
 * false, and the caller falls through to normal acquisition — it never becomes re-entrant.
 */
function authenticateWithHolder(port, capability, holderPid) {
  if (typeof capability !== 'string' || capability === '' || !Number.isInteger(holderPid) || holderPid <= 0) return false

  const nonce = randomBytes(32).toString('base64url')
  const reply = requestHolderProof(port, nonce)
  if (!reply.startsWith('PROOF ')) return false

  const provided = Buffer.from(reply.slice('PROOF '.length).trim(), 'base64url')
  const expected = createHmac('sha256', capability).update(canonicalChallenge(port, holderPid, nonce)).digest()
  if (provided.length !== expected.length) return false
  return timingSafeEqual(expected, provided)
}

function discardHolder(holder, statusPath) {
  try { holder.kill() } catch { /* already gone */ }
  try { rmSync(statusPath, { force: true }) } catch { /* best effort */ }
}

/** Best-effort diagnostic: ask the current holder who it is. Read-only by construction — the
 * holder answers connections with an identity line and a connection can never affect ownership. */
function describeE2eRunLockOwner(port = E2E_RUN_LOCK_PORT) {
  const probe = "const net=require('node:net');const s=net.connect({host:'127.0.0.1',port:Number(process.argv[1])});"
    + "let d='';s.setEncoding('utf8');s.on('connect',()=>{s.write('WHOAMI\\n')});s.on('data',(c)=>{d+=c});"
    + "s.on('end',()=>{process.stdout.write(d)});"
    + "s.on('error',()=>{process.exit(1)});setTimeout(()=>{process.stdout.write(d);process.exit(0)},2000).unref();"
  try {
    const output = execFileSync(process.execPath, ['-e', probe, String(port)], { encoding: 'utf8', timeout: 5_000 }).trim()
    return output ? JSON.parse(output) : undefined
  } catch {
    return undefined
  }
}

/**
 * Acquires exclusive ownership of the mutable E2E environment, blocking (synchronously, because
 * Playwright's config module must decide before it returns a config) until the kernel grants the
 * port or `timeoutMs` elapses. Never kills, signals or displaces the current owner: a busy lock
 * is simply waited out and then reported.
 */
function acquireE2eRunLock({ timeoutMs = DEFAULT_LOCK_TIMEOUT_MS, port = E2E_RUN_LOCK_PORT } = {}) {
  // Re-entrancy, in-process: nested acquisition inside our own critical section. Even here the
  // grant is proven against the live holder rather than assumed from PID liveness, so a holder
  // that died mid-run can never be mistaken for ownership we still have.
  if (activeRunLock && activeRunLock.port === port && authenticateWithHolder(port, activeRunLock.capability, activeRunLock.holderPid)) {
    return { port, holderPid: activeRunLock.holderPid, reentrant: true }
  }
  // Re-entrancy, across processes: Playwright re-evaluates this config in every worker process,
  // so a descendant spawned INSIDE an ancestor's critical section must operate under that
  // ownership rather than contend with it. The inherited marker is only a claim — it is granted
  // ONLY if the live holder on that port authenticates the capability. A forged or stale marker
  // therefore falls through to normal acquisition (bind / wait / bounded timeout) and can never
  // self-declare ownership.
  const inherited = parseInheritedRunLock()
  if (inherited && inherited.port === port && authenticateWithHolder(port, inherited.capability, inherited.holderPid)) {
    return { port, holderPid: inherited.holderPid, reentrant: true }
  }

  const statusPath = join(tmpdir(), `verce-e2e-run-lock-${randomUUID()}.json`)
  const deadlineAt = Date.now() + timeoutMs
  const ownerLabel = `pid ${process.pid} (${process.argv.slice(1).join(' ')})`.slice(0, 240)
  const holder = spawn(
    process.execPath,
    [RUN_LOCK_HOLDER_SCRIPT, String(port), statusPath, String(deadlineAt), String(process.pid), ownerLabel],
    { stdio: ['pipe', 'ignore', 'inherit'], windowsHide: true },
  )
  // The holder must never keep this process alive; it is released by our exit, not the reverse.
  try { holder.unref() } catch { /* not fatal */ }
  try { holder.stdin.unref() } catch { /* not fatal */ }

  const pollDeadline = deadlineAt + RUN_LOCK_HOLDER_GRACE_MS
  while (Date.now() < pollDeadline) {
    const status = readRunLockStatus(statusPath)
    if (status?.state === 'held') {
      activeRunLock = { port, holderPid: status.holderPid, capability: status.capability, statusPath, holder, reentrant: false }
      // The status file carries the capability for exactly this handshake and is consumed here:
      // ownership lives in the holder's socket and the capability in memory, never on disk. A
      // stale status file can therefore never authorize a later run, and an abruptly killed owner
      // leaves nothing behind at all (on Windows the holder is torn down with its parent and never
      // gets to run its own cleanup, which is also why the port frees within ~100ms).
      try { rmSync(statusPath, { force: true }) } catch { /* best effort */ }
      process.env[RUN_LOCK_HOLDER_ENV] = `${status.holderPid}:${port}:${status.capability}`
      if (!exitReleaseRegistered) {
        exitReleaseRegistered = true
        process.once('exit', () => { try { releaseE2eRunLock() } catch { /* exiting anyway */ } })
      }
      return activeRunLock
    }
    if (status?.state === 'timeout' || status?.state === 'error') {
      discardHolder(holder, statusPath)
      const owner = status.state === 'timeout' ? describeE2eRunLockOwner(port) : undefined
      throw new Error(
        `Could not acquire the E2E run lock on 127.0.0.1:${port} within ${timeoutMs}ms: ${status.reason}.`
        + (owner ? ` It is owned by ${owner.owner} (holder pid ${owner.holderPid}).` : '')
        + ' Another E2E run owns the disposable database, ports and storage state; wait for it to finish or stop it.',
      )
    }
    if (!isProcessRunning(holder.pid)) {
      discardHolder(holder, statusPath)
      throw new Error(`The E2E run-lock holder process exited before reporting ownership of 127.0.0.1:${port}.`)
    }
    wait(RUN_LOCK_POLL_INTERVAL_MS)
  }

  discardHolder(holder, statusPath)
  throw new Error(`Timed out after ${timeoutMs}ms waiting for the E2E run lock on 127.0.0.1:${port}.`)
}

/** Releases only OUR OWN ownership; a handle that this process does not own is ignored. */
function releaseE2eRunLock(lock = activeRunLock) {
  if (!lock || lock.reentrant) return
  const owned = activeRunLock
  if (!owned || owned.holderPid !== lock.holderPid) return

  activeRunLock = undefined
  delete process.env[RUN_LOCK_HOLDER_ENV]
  try { owned.holder.stdin.write('RELEASE\n') } catch { /* the kill below and our exit both cover this */ }
  try { owned.holder.stdin.end() } catch { /* same */ }
  try { owned.holder.kill() } catch { /* already gone */ }
  try { rmSync(owned.statusPath, { force: true }) } catch { /* best effort */ }
}

/** The lock covering this process: the one we acquired, or the ancestor's we are running inside.
 * Lets any process in the run (globalSetup, a spawned step) check continuity of the SAME
 * ownership without re-acquiring anything. */
function currentE2eRunLock() {
  if (activeRunLock) return activeRunLock
  const inherited = parseInheritedRunLock()
  return inherited ? { port: inherited.port, holderPid: inherited.holderPid, capability: inherited.capability, reentrant: true } : undefined
}

/** Continuity check: ownership lives in the holder's socket, so this asks the holder itself
 * rather than inspecting a PID — a recycled PID must never read as "still ours", and a holder
 * that died means the run no longer owns the environment it is still mutating. */
function isE2eRunLockHeld(lock = currentE2eRunLock()) {
  return Boolean(lock?.capability) && authenticateWithHolder(lock.port, lock.capability, lock.holderPid)
}

function assertE2eRunLockHeld(lock = currentE2eRunLock()) {
  if (!lock) throw new Error('This process does not own the E2E run lock.')
  if (!isE2eRunLockHeld(lock)) {
    throw new Error(
      `The E2E run lock on 127.0.0.1:${lock.port} was lost mid-run (holder pid ${lock.holderPid} is gone) — `
      + 'another run may now own the disposable database, ports and storage state. Aborting instead of continuing to mutate a shared environment.',
    )
  }
  return lock
}

function resolveOwnerEmail() {
  return process.env.VERCE_E2E_OWNER_EMAIL || DEFAULT_OWNER_EMAIL
}

function resolveOwnerName() {
  return process.env.VERCE_E2E_OWNER_NAME || DEFAULT_OWNER_NAME
}

module.exports = {
  CONTROL_DATABASE_NAME,
  DEV_DATABASE_NAME,
  DEFAULT_E2E_DATABASE_NAME,
  DEFAULT_E2E_USER,
  DEFAULT_E2E_PASSWORD,
  DEFAULT_OWNER_EMAIL,
  DEFAULT_OWNER_NAME,
  resolveE2eRunId,
  DATABASE_NAME_PATTERN,
  ROLE_NAME_PATTERN,
  PROTECTED_POSTGRES_CONTAINERS,
  resolveE2eDatabaseName,
  resolveE2eConnectionString,
  resolveE2ePostgresTarget,
  extractConnectionEndpoint,
  inspectDockerContainer,
  resolveProtectedContainerId,
  assertSelectorIsNotProtectedByName,
  assertContainerIsNotProtected,
  parseConnectionString,
  normalizeDatabaseName,
  resolveDatabaseNameFromConnectionString,
  assertDisposableDatabaseName,
  assertDisposableE2eDatabase,
  assertSafeRoleName,
  quotePostgresIdentifier,
  quotePostgresRoleIdentifier,
  ensureDatabaseExists,
  dropE2eDatabaseIfExists,
  controlPsql,
  controlPsqlFromInput,
  runE2ePsql,
  applyMigrations,
  provisionE2eDatabase,
  E2E_RUN_LOCK_PORT,
  acquireE2eRunLock,
  releaseE2eRunLock,
  currentE2eRunLock,
  isE2eRunLockHeld,
  assertE2eRunLockHeld,
  describeE2eRunLockOwner,
  resolveOwnerEmail,
  resolveOwnerName,
}
