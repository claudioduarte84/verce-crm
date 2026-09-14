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
const DEFAULT_E2E_DATABASE_NAME = 'verce_e2e'
const DEFAULT_OWNER_EMAIL = 'e2e-owner@example.test'
const DEFAULT_OWNER_NAME = 'E2E Owner'
const DEFAULT_LOCK_TIMEOUT_MS = 300_000
const DATABASE_NAME_PATTERN = /^[A-Za-z0-9_]+$/

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
  return databaseName
}

function resolveE2eDatabaseName() {
  return process.env.VERCE_E2E_DATABASE || DEFAULT_E2E_DATABASE_NAME
}

function resolveDatabaseNameFromConnectionString(connectionString) {
  const aliases = parseConnectionString(connectionString)
    .filter((pair) => pair.normalizedKey === 'database' || pair.normalizedKey === 'initial catalog')
    .map((pair) => normalizeDatabaseName(pair.value))
  if (aliases.length === 0) return normalizeDatabaseName(resolveE2eDatabaseName())
  if (new Set(aliases).size !== 1) {
    throw new Error(`Conflicting E2E database aliases in connection string: ${aliases.join(', ')}`)
  }
  return aliases[0]
}

function resolveE2eConnectionString() {
  const configured = process.env.ConnectionStrings__Verce
  if (!configured) {
    return `Host=localhost;Port=5432;Database=${assertDisposableDatabaseName(resolveE2eDatabaseName())};Username=verce;Password=verce_dev_only`
  }

  const pairs = parseConnectionString(configured)
  const databaseName = assertDisposableE2eDatabase(configured)
  let wroteDatabase = false
  const normalized = pairs
    .filter((pair) => {
      if (pair.normalizedKey !== 'database' && pair.normalizedKey !== 'initial catalog') return true
      if (wroteDatabase) return false
      wroteDatabase = true
      return true
    })
    .map((pair) => (pair.normalizedKey === 'database' || pair.normalizedKey === 'initial catalog' ? `Database=${databaseName}` : pair.raw))
  if (!wroteDatabase) normalized.push(`Database=${databaseName}`)
  return normalized.join(';')
}

/** Fails fast before database creation, migrations, setup or any provisioning SQL. */
function assertDisposableE2eDatabase(connectionString) {
  return assertDisposableDatabaseName(resolveDatabaseNameFromConnectionString(connectionString))
}

function quotePostgresIdentifier(identifier) {
  const safeIdentifier = assertDisposableDatabaseName(identifier)
  return `"${safeIdentifier.replaceAll('"', '""')}"`
}

function controlPsql(argumentsAfterPsql) {
  return execFileSync(
    'docker',
    ['exec', 'verce-postgres', 'psql', '-v', 'ON_ERROR_STOP=1', '-U', 'verce', '-d', CONTROL_DATABASE_NAME, ...argumentsAfterPsql],
    { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] },
  ).trim()
}

function controlPsqlFromInput(sql, argumentsAfterPsql) {
  return execFileSync(
    'docker',
    ['exec', '-i', 'verce-postgres', 'psql', '-v', 'ON_ERROR_STOP=1', '-U', 'verce', '-d', CONTROL_DATABASE_NAME, ...argumentsAfterPsql],
    { input: sql, encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'] },
  ).trim()
}

/** Creates only a validated, disposable database. The control connection is PostgreSQL's neutral
 * maintenance database, never the protected application database. */
function ensureDatabaseExists(databaseName) {
  const safeDatabaseName = assertDisposableDatabaseName(databaseName)
  const exists = controlPsqlFromInput("SELECT 1 FROM pg_database WHERE datname = :'target_database';\n", ['-v', `target_database=${safeDatabaseName}`, '-tA'])
  if (exists === '1') return safeDatabaseName
  controlPsql(['-c', `CREATE DATABASE ${quotePostgresIdentifier(safeDatabaseName)} OWNER verce`])
  return safeDatabaseName
}

function dropE2eDatabaseIfExists(databaseName) {
  const safeDatabaseName = assertDisposableDatabaseName(databaseName)
  controlPsql(['-c', `DROP DATABASE IF EXISTS ${quotePostgresIdentifier(safeDatabaseName)}`])
}

/** Applies EF Core migrations after the same disposable-database guard used for provisioning. */
function applyMigrations(connectionString) {
  assertDisposableE2eDatabase(connectionString)
  execFileSync('dotnet', ['run', '--no-build', '--project', API_PROJECT_DIR, '--', 'migrate'], {
    cwd: API_PROJECT_DIR,
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, ConnectionStrings__Verce: connectionString },
  })
}

function provisionE2eDatabase(connectionString) {
  const databaseName = assertDisposableE2eDatabase(connectionString)
  ensureDatabaseExists(databaseName)
  applyMigrations(connectionString)
  return databaseName
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
  DEFAULT_OWNER_EMAIL,
  DEFAULT_OWNER_NAME,
  DATABASE_NAME_PATTERN,
  resolveE2eDatabaseName,
  resolveE2eConnectionString,
  parseConnectionString,
  normalizeDatabaseName,
  resolveDatabaseNameFromConnectionString,
  assertDisposableDatabaseName,
  assertDisposableE2eDatabase,
  quotePostgresIdentifier,
  ensureDatabaseExists,
  dropE2eDatabaseIfExists,
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
