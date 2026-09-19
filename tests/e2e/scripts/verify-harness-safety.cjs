'use strict'

const assert = require('node:assert/strict')
const { execFileSync, spawn } = require('node:child_process')
const { randomUUID } = require('node:crypto')
const { readFileSync, rmSync, writeFileSync } = require('node:fs')
const { tmpdir } = require('node:os')
const { join, resolve } = require('node:path')
const {
  acquireE2eRunLock,
  assertDisposableE2eDatabase,
  assertSafeRoleName,
  dropE2eDatabaseIfExists,
  provisionE2eDatabase,
  quotePostgresRoleIdentifier,
  releaseE2eRunLock,
  resolveE2ePostgresTarget,
} = require('../e2e-env.cjs')

const SCRIPT_PATH = resolve(__filename)
const SAFETY_DATABASE = 'verce_e2e_harness_safety'

function currentContainerSelector() {
  const value = String(process.env.VERCE_E2E_POSTGRES_CONTAINER ?? '').trim()
  if (!value) throw new Error('Set VERCE_E2E_POSTGRES_CONTAINER to a disposable PostgreSQL container before running verify-harness-safety.cjs.')
  return value
}

/** Same target, different database — used to point provisioning/drop at the safety database
 * without re-deriving container/user/password by hand at each call site. */
function withDatabase(target, database) {
  return {
    ...target,
    database,
    connectionString: `Host=${target.host};Port=${target.publishedPort};Database=${database};Username=${target.user};Password=${target.password}`,
  }
}

function withEnv(overrides, fn) {
  const previous = {}
  for (const key of Object.keys(overrides)) previous[key] = process.env[key]
  Object.assign(process.env, overrides)
  try {
    return fn()
  } finally {
    for (const key of Object.keys(overrides)) {
      if (previous[key] === undefined) delete process.env[key]
      else process.env[key] = previous[key]
    }
  }
}

function mustReject(connectionString) {
  assert.throws(() => assertDisposableE2eDatabase(connectionString), /shared development database|Unsafe E2E database name|E2E_AMBIGUOUS_CONNECTION_TARGET|Invalid E2E connection string/)
}

function testGuardMatrix() {
  for (const value of [
    'Database=verce',
    'database=verce',
    'Database=VERCE',
    'Database = verce',
    'Database= verce',
    'Initial Catalog=verce',
    'initial catalog = VERCE',
  ]) mustReject(value)

  assert.equal(assertDisposableE2eDatabase('Database=verce_e2e'), 'verce_e2e')
  assert.equal(assertDisposableE2eDatabase('Initial Catalog=verce_e2e'), 'verce_e2e')
  mustReject('Database=verce_e2e;Initial Catalog=other_e2e')

  for (const unsafeName of [
    'verce_e2e;DROP DATABASE verce',
    'verce e2e',
    'verce-e2e',
    'verce_e2e"',
    "verce_e2e'",
  ]) mustReject(`Database=${unsafeName}`)
  console.log('PASS: database-name guard matrix (unsafe names, protected name, duplicate database aliases)')
}

/**
 * R-01/§11: every one of the six Database/Initial-Catalog duplicate combinations is rejected —
 * EQUAL values exactly as much as conflicting ones. This is the specific gap Sol's re-review
 * found: `resolveDatabaseNameFromConnectionString` used to compare the resulting VALUES
 * (`new Set(aliases).size !== 1`), so `Database=x;Database=x` slipped through as "not
 * conflicting". The fix checks raw occurrence count, never value equality, and this test proves
 * both members of each pair fail identically.
 */
function testDatabaseDuplicateMatrix() {
  const equalAndConflicting = [
    ['Database=verce_e2e;Database=verce_e2e', 'Database=verce_e2e;Database=verce_test'],
    ['Database=verce_e2e;Initial Catalog=verce_e2e', 'Database=verce_e2e;Initial Catalog=verce_test'],
    ['Initial Catalog=verce_e2e;Initial Catalog=verce_e2e', 'Initial Catalog=verce_e2e;Initial Catalog=verce_test'],
  ]
  for (const [equal, conflicting] of equalAndConflicting) {
    assert.throws(() => assertDisposableE2eDatabase(equal), /E2E_AMBIGUOUS_CONNECTION_TARGET/, `equal-value duplicate must be rejected: ${equal.split('=')[0]}`)
    assert.throws(() => assertDisposableE2eDatabase(conflicting), /E2E_AMBIGUOUS_CONNECTION_TARGET/, `conflicting duplicate must be rejected: ${conflicting.split('=')[0]}`)
  }
  // Guard timing: this must fail on connection-string inspection alone, with no container
  // configured and therefore no possibility of a docker inspect/exec having occurred.
  withEnv({ VERCE_E2E_POSTGRES_CONTAINER: '' }, () => {
    delete process.env.VERCE_E2E_POSTGRES_CONTAINER
    assert.throws(() => assertDisposableE2eDatabase('Database=verce_e2e;Database=verce_e2e'), /E2E_AMBIGUOUS_CONNECTION_TARGET/)
  })
  console.log('PASS: all six Database/Initial-Catalog duplicate combinations (equal and conflicting) rejected as E2E_AMBIGUOUS_CONNECTION_TARGET, independent of any Docker target')
}

/** Fails the assertion (with a useful diagnostic) if `error.message` contains any of `secrets`
 * or the full `connectionString` that produced it — the R-02 leak-detection primitive every
 * ambiguity test below is built on. */
function assertDiagnosticLeaksNothing(error, connectionString, secrets) {
  for (const secret of secrets) {
    assert.ok(!error.message.includes(secret), `diagnostic leaked a secret value: "${secret}" appeared in "${error.message}"`)
  }
  assert.ok(!error.message.includes(connectionString), 'diagnostic must never echo the full connection string')
}

/**
 * H-01/R-01/§8/§12-14/§21/§41: an ambiguous connection string is rejected as
 * `E2E_AMBIGUOUS_CONNECTION_TARGET` before Docker is ever consulted and before any SQL runs — for
 * EQUAL values exactly as much as conflicting ones (R-01), and the diagnostic never echoes a raw
 * key=value pair (R-02, generalized to every property here — password gets its own dedicated,
 * stricter test below).
 */
function testAmbiguousConnectionAliases() {
  const container = currentContainerSelector()
  const rejects = (connectionString) => withEnv(
    { VERCE_E2E_POSTGRES_CONTAINER: container, ConnectionStrings__Verce: connectionString },
    () => assert.throws(() => resolveE2ePostgresTarget(), (error) => {
      assert.match(error.message, /E2E_AMBIGUOUS_CONNECTION_TARGET/, `expected ambiguity rejection for "${connectionString}"`)
      assertDiagnosticLeaksNothing(error, connectionString, [])
      return true
    }),
  )

  // Conflicting values (the original H-01 coverage).
  rejects('Host=127.0.0.1;Host=evil.example;Port=1;Database=verce_e2e;Username=verce;Password=x')
  rejects('Host=127.0.0.1;Server=evil.example;Port=1;Database=verce_e2e;Username=verce;Password=x')
  rejects('Host=127.0.0.1;Port=1;Port=2;Database=verce_e2e;Username=verce;Password=x')
  rejects('Host=127.0.0.1;Port=1;Database=verce_e2e;Username=verce;User ID=other;Password=x')

  // R-01: the SAME cases with EQUAL values — must fail identically, never accepted because "they
  // agree today".
  rejects('Host=127.0.0.1;Host=127.0.0.1;Port=1;Database=verce_e2e;Username=verce;Password=x')
  rejects('Host=127.0.0.1;Server=127.0.0.1;Port=1;Database=verce_e2e;Username=verce;Password=x')
  rejects('Host=127.0.0.1;Port=1;Port=1;Database=verce_e2e;Username=verce;Password=x')
  rejects('Host=127.0.0.1;Port=1;Database=verce_e2e;Username=verce;User ID=verce;Password=x')

  console.log('PASS: duplicate Host, Host+Server, Port and Username aliases are rejected as E2E_AMBIGUOUS_CONNECTION_TARGET for BOTH equal and conflicting values, before any Docker/SQL operation, with no leaked diagnostic content')
}

/**
 * R-02/§7-9/§15/§23: the specific finding — a duplicate Password/Pwd diagnostic could expose a
 * raw credential. Proves both the conflicting-values case and the equal-values case (duplicate
 * semantics are invalid regardless of whether the two values happen to agree) are rejected, and
 * that neither supplied password value, nor the full connection string, ever appears in the
 * thrown message.
 */
function testPasswordRedaction() {
  const container = currentContainerSelector()
  const rejectsWithoutLeaking = (connectionString, secrets) => withEnv(
    { VERCE_E2E_POSTGRES_CONTAINER: container, ConnectionStrings__Verce: connectionString },
    () => assert.throws(() => resolveE2ePostgresTarget(), (error) => {
      assert.match(error.message, /E2E_AMBIGUOUS_CONNECTION_TARGET/)
      assert.match(error.message, /'password'/, 'the diagnostic must name the semantic property, not the raw keys')
      assertDiagnosticLeaksNothing(error, connectionString, secrets)
      return true
    }),
  )

  rejectsWithoutLeaking(
    'Host=127.0.0.1;Port=1;Database=verce_e2e;Username=verce;Password=secret-one;Pwd=secret-two',
    ['secret-one', 'secret-two', 'Password=secret-one', 'Pwd=secret-two'],
  )
  rejectsWithoutLeaking(
    'Host=127.0.0.1;Port=1;Database=verce_e2e;Username=verce;Password=same-secret;Pwd=same-secret',
    ['same-secret', 'Password=same-secret', 'Pwd=same-secret'],
  )
  console.log('PASS: duplicate Password/Pwd — both conflicting and equal-value — rejected as E2E_AMBIGUOUS_CONNECTION_TARGET with neither credential value nor the connection string in the diagnostic')
}

/** §16-17: a valid, single-valued configuration must still resolve correctly (no accidental
 * over-rejection), and a legitimate value containing semicolons must survive quoting without
 * being misread as a second occurrence of anything. */
function testValidConfigurationStillResolves() {
  const container = currentContainerSelector()
  const target = resolveE2ePostgresTarget()
  withEnv(
    { VERCE_E2E_POSTGRES_CONTAINER: container, ConnectionStrings__Verce: `Host=127.0.0.1;Port=${target.publishedPort};Database=${SAFETY_DATABASE};Username=${target.user};Password="${target.password};with;semicolons"` },
    () => {
      const resolved = resolveE2ePostgresTarget()
      assert.equal(resolved.database, SAFETY_DATABASE)
      assert.equal(resolved.user, target.user)
      assert.ok(resolved.password.includes(`${target.password};with;semicolons`), 'a quoted value containing semicolons must parse as ONE value, not be split or misread as a duplicate')
    },
  )
  console.log('PASS: a valid single-valued connection string (including a quoted value containing semicolons) still resolves correctly — no accidental over-rejection')
}

/**
 * H-02/H-04/§10-§12/§40: proves the protected container is rejected by its literal name AND by
 * its Docker identity (full ID, short ID) — not merely by a name-string comparison a selector of
 * the protected container's own ID could sail straight through. This is deliberately a
 * Docker-backed proof against the REAL, currently running `verce-postgres`, not a string-matching
 * unit test — per §21, if it cannot be inspected this fails loudly rather than silently passing.
 */
function testProtectedContainerIdentity() {
  let inspected
  try {
    inspected = JSON.parse(execFileSync('docker', ['inspect', 'verce-postgres'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }))[0]
  } catch (error) {
    throw new Error(`This proof requires the real, running "verce-postgres" container to be inspectable — it is not: ${error.message}`)
  }
  assert.ok(inspected, 'the protected verce-postgres container must exist and be inspectable for this proof to be meaningful')
  const fullId = inspected.Id
  const shortId = fullId.slice(0, 12)
  assert.ok(inspected.State?.Running, 'the protected verce-postgres container must be running for this proof to be meaningful')

  for (const selector of ['verce-postgres', fullId, shortId]) {
    withEnv({ VERCE_E2E_POSTGRES_CONTAINER: selector }, () =>
      assert.throws(() => resolveE2ePostgresTarget(), /E2E_PROTECTED_DATABASE_TARGET/, `selector "${selector}" must be rejected as the protected container`))
  }
  console.log(`PASS: protected container rejected by name ("verce-postgres"), full ID (${fullId}) and short ID (${shortId})`)
}

/** §14-15/§40/§42: missing target, protected-name target, and application/control port
 * consistency (a correct port is accepted, an incorrect one is rejected) — all resolved through
 * the one authoritative `resolveE2ePostgresTarget()`, never a second, independent parser. */
function testTargetGuards() {
  const container = currentContainerSelector()
  const target = resolveE2ePostgresTarget()

  withEnv({ VERCE_E2E_POSTGRES_CONTAINER: '' }, () => {
    delete process.env.VERCE_E2E_POSTGRES_CONTAINER
    assert.throws(() => resolveE2ePostgresTarget(), /E2E_POSTGRES_CONTAINER_REQUIRED/)
  })

  withEnv({ VERCE_E2E_POSTGRES_CONTAINER: 'verce-postgres' }, () =>
    assert.throws(() => resolveE2ePostgresTarget(), /E2E_PROTECTED_DATABASE_TARGET/))

  const wrongPort = String(Number(target.publishedPort) + 1)
  withEnv(
    { VERCE_E2E_POSTGRES_CONTAINER: container, ConnectionStrings__Verce: `Host=127.0.0.1;Port=${wrongPort};Database=${SAFETY_DATABASE};Username=${target.user};Password=${target.password}` },
    () => assert.throws(() => resolveE2ePostgresTarget(), /E2E_DATABASE_TARGET_MISMATCH/, 'a port that does not match the inspected container must be rejected BEFORE any destructive SQL'),
  )

  withEnv(
    { VERCE_E2E_POSTGRES_CONTAINER: container, ConnectionStrings__Verce: `Host=127.0.0.1;Port=${target.publishedPort};Database=${SAFETY_DATABASE};Username=${target.user};Password=${target.password}` },
    () => {
      const resolved = resolveE2ePostgresTarget()
      assert.equal(resolved.publishedPort, target.publishedPort)
      assert.equal(resolved.containerId, target.containerId)
    },
  )
  console.log('PASS: missing-container, protected-container and application/control port mismatch/match target guards')
}

/** H-05/§23-24: the owner role is validated against a deliberately restricted grammar and quoted
 * as a delimited identifier — never treated as a string literal or SQL expression. */
function testRoleIdentifierGuard() {
  assert.equal(quotePostgresRoleIdentifier('verce_s4_harness'), '"verce_s4_harness"')
  assert.equal(quotePostgresRoleIdentifier('verce'), '"verce"')
  for (const unsafe of ['verce"; DROP TABLE x; --', 'verce role', '1verce_role', '', 'verce-role', 'verce;drop database verce']) {
    assert.throws(() => assertSafeRoleName(unsafe), /Unsafe PostgreSQL role identifier/, `role "${unsafe}" must be rejected`)
  }
  console.log('PASS: PostgreSQL owner-role identifier validation and delimited-identifier quoting')
}

/**
 * H-03/§16-§18: does the REAL mutable harness work — pre-drop, CREATE+migrate, post-drop —
 * entirely inside ONE E2E run-lock acquisition, journalling entry/exit around the WHOLE
 * destructive lifecycle (not just the middle provisioning step) so overlap can be proven
 * impossible for the complete sequence a real verifier actually runs.
 */
function runDestructiveLifecycle(journalPath) {
  const startedAt = Date.now()
  const lock = acquireE2eRunLock({ timeoutMs: 120_000 })
  const waitedMs = Date.now() - startedAt
  try {
    journal(journalPath, { event: 'enter', pid: process.pid, at: Date.now(), waitedMs })
    const target = withDatabase(resolveE2ePostgresTarget(), SAFETY_DATABASE)
    dropE2eDatabaseIfExists(SAFETY_DATABASE, target)
    provisionE2eDatabase(target)
    dropE2eDatabaseIfExists(SAFETY_DATABASE, target)
    journal(journalPath, { event: 'leave', pid: process.pid, at: Date.now() })
    console.log(`PASS: full destructive lifecycle (pre-drop, provision, post-drop) for ${SAFETY_DATABASE} after waiting ${waitedMs}ms for the E2E run lock`)
  } finally {
    releaseE2eRunLock(lock)
  }
}

function journal(journalPath, entry) {
  if (journalPath) writeFileSync(journalPath, `${JSON.stringify(entry)}\n`, { flag: 'a' })
}

function readJournal(journalPath) {
  try {
    return readFileSync(journalPath, 'utf8').split('\n').filter(Boolean).map((line) => JSON.parse(line))
  } catch {
    return []
  }
}

function runChild(journalPath) {
  return new Promise((resolveChild, rejectChild) => {
    // An UNRELATED harness run: strip the descendant re-entrancy marker so the child genuinely
    // contends for the same lock, exactly as a second `npx playwright test` would.
    const env = { ...process.env, VERCE_E2E_HARNESS_SAFETY_JOURNAL: journalPath }
    delete env.VERCE_E2E_RUN_LOCK_HOLDER
    const child = spawn(process.execPath, [SCRIPT_PATH, '--provision-child'], {
      cwd: resolve(__dirname, '..'),
      env,
      stdio: 'pipe',
      windowsHide: true,
    })
    let output = ''
    child.stdout.on('data', (chunk) => { output += chunk })
    child.stderr.on('data', (chunk) => { output += chunk })
    child.on('error', rejectChild)
    child.on('exit', (code) => {
      if (code === 0) resolveChild(output.trim())
      else rejectChild(new Error(`Concurrent destructive-lifecycle child failed with exit ${code}: ${output}`))
    })
  })
}

async function main() {
  testGuardMatrix()
  testDatabaseDuplicateMatrix()
  testAmbiguousConnectionAliases()
  testPasswordRedaction()
  testValidConfigurationStillResolves()
  testProtectedContainerIdentity()
  testTargetGuards()
  testRoleIdentifierGuard()

  const journalPath = join(tmpdir(), `verce-harness-safety-${randomUUID()}.ndjson`)
  try {
    for (let index = 1; index <= 3; index += 1) {
      runDestructiveLifecycle(undefined)
      console.log(`PASS: idempotent full destructive lifecycle run ${index}`)
    }

    // §18/§22: two UNRELATED processes each run the COMPLETE destructive lifecycle
    // (pre-drop, provision, post-drop) concurrently; the journal must show their intervals never
    // overlap, proving the run lock serializes more than just the central provisioning call.
    const results = await Promise.all([runChild(journalPath), runChild(journalPath)])
    assert.equal(results.length, 2)

    const entries = readJournal(journalPath)
    const enters = entries.filter((e) => e.event === 'enter')
    const leaves = entries.filter((e) => e.event === 'leave')
    assert.equal(enters.length, 2, 'both concurrent lifecycles must have entered the critical section')
    assert.equal(leaves.length, 2, 'both concurrent lifecycles must have left the critical section')
    const sessions = enters
      .map((enter) => ({ pid: enter.pid, waitedMs: enter.waitedMs, enteredAt: enter.at, leftAt: leaves.find((l) => l.pid === enter.pid)?.at }))
      .sort((left, right) => left.enteredAt - right.enteredAt)
    assert.ok(sessions.every((s) => Number.isFinite(s.leftAt)), 'every session must record its exit')
    assert.ok(
      sessions[1].enteredAt >= sessions[0].leftAt,
      `full destructive lifecycles overlapped: pid ${sessions[1].pid} entered at ${sessions[1].enteredAt} while pid ${sessions[0].pid} held until ${sessions[0].leftAt}`,
    )
    console.log(
      'PASS: two concurrent FULL destructive lifecycles (pre-drop, provision, post-drop) serialized — '
      + `pid ${sessions[0].pid} owned the environment for ${sessions[0].leftAt - sessions[0].enteredAt}ms, `
      + `then pid ${sessions[1].pid} (which waited ${sessions[1].waitedMs}ms) entered ${sessions[1].enteredAt - sessions[0].leftAt}ms later — zero overlap`,
    )
  } finally {
    rmSync(journalPath, { force: true })
    // Safety-net cleanup — normally redundant since every lifecycle above already leaves the
    // database dropped, but still never destructive outside the lock (H-03).
    const lock = acquireE2eRunLock({ timeoutMs: 120_000 })
    try { dropE2eDatabaseIfExists(SAFETY_DATABASE) } finally { releaseE2eRunLock(lock) }
  }
}

if (process.argv[2] === '--provision-child') runDestructiveLifecycle(process.env.VERCE_E2E_HARNESS_SAFETY_JOURNAL)
else main().catch((error) => { console.error(error.stack || error); process.exitCode = 1 })
