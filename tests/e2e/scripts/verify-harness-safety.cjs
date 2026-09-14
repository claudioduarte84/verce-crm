'use strict'

const assert = require('node:assert/strict')
const { spawn } = require('node:child_process')
const { randomUUID } = require('node:crypto')
const { readFileSync, rmSync, writeFileSync } = require('node:fs')
const { tmpdir } = require('node:os')
const { join, resolve } = require('node:path')
const {
  acquireE2eRunLock,
  assertDisposableE2eDatabase,
  dropE2eDatabaseIfExists,
  provisionE2eDatabase,
  releaseE2eRunLock,
} = require('../e2e-env.cjs')

const SCRIPT_PATH = resolve(__filename)
const SAFETY_DATABASE = 'verce_e2e_harness_safety'
const SAFETY_CONNECTION = `Host=localhost;Port=5432;Database=${SAFETY_DATABASE};Username=verce;Password=verce_dev_only`

function mustReject(connectionString) {
  assert.throws(() => assertDisposableE2eDatabase(connectionString), /shared development database|Unsafe E2E database name|Conflicting E2E database aliases|Invalid E2E connection string/)
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
}

/** Does the REAL mutable harness work (create database + apply migrations) inside the real run
 * lock, journalling when it entered and left so overlap can be proven impossible at the
 * full-harness level, not just at the primitive level. */
function provisionWithLock(journalPath) {
  const startedAt = Date.now()
  const lock = acquireE2eRunLock({ timeoutMs: 120_000 })
  const waitedMs = Date.now() - startedAt
  try {
    journal(journalPath, { event: 'enter', pid: process.pid, at: Date.now(), waitedMs })
    provisionE2eDatabase(SAFETY_CONNECTION)
    journal(journalPath, { event: 'leave', pid: process.pid, at: Date.now() })
    console.log(`PASS: provisioned ${SAFETY_DATABASE} after waiting ${waitedMs}ms for the E2E run lock`)
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
      else rejectChild(new Error(`Concurrent provision child failed with exit ${code}: ${output}`))
    })
  })
}

async function main() {
  testGuardMatrix()
  dropE2eDatabaseIfExists(SAFETY_DATABASE)
  const journalPath = join(tmpdir(), `verce-harness-safety-${randomUUID()}.ndjson`)
  try {
    for (let index = 1; index <= 3; index += 1) {
      provisionWithLock(undefined)
      console.log(`PASS: idempotent provisioning run ${index}`)
    }

    const results = await Promise.all([runChild(journalPath), runChild(journalPath)])
    assert.equal(results.length, 2)

    // Full-harness exclusivity evidence: two unrelated processes each provisioned a real
    // database inside the lock, and their critical sections did not overlap by a single ms.
    const entries = readJournal(journalPath)
    const enters = entries.filter((e) => e.event === 'enter')
    const leaves = entries.filter((e) => e.event === 'leave')
    assert.equal(enters.length, 2, 'both concurrent harness runs must have entered the critical section')
    assert.equal(leaves.length, 2, 'both concurrent harness runs must have left the critical section')
    const sessions = enters
      .map((enter) => ({ pid: enter.pid, waitedMs: enter.waitedMs, enteredAt: enter.at, leftAt: leaves.find((l) => l.pid === enter.pid)?.at }))
      .sort((left, right) => left.enteredAt - right.enteredAt)
    assert.ok(sessions.every((s) => Number.isFinite(s.leftAt)), 'every session must record its exit')
    assert.ok(
      sessions[1].enteredAt >= sessions[0].leftAt,
      `critical sections overlapped: pid ${sessions[1].pid} entered at ${sessions[1].enteredAt} while pid ${sessions[0].pid} held until ${sessions[0].leftAt}`,
    )
    console.log(
      `PASS: two concurrent harness runs serialized — pid ${sessions[0].pid} owned the environment for `
      + `${sessions[0].leftAt - sessions[0].enteredAt}ms, then pid ${sessions[1].pid} (which waited ${sessions[1].waitedMs}ms) `
      + `entered ${sessions[1].enteredAt - sessions[0].leftAt}ms later — zero overlap`,
    )
  } finally {
    rmSync(journalPath, { force: true })
    dropE2eDatabaseIfExists(SAFETY_DATABASE)
  }
}

if (process.argv[2] === '--provision-child') provisionWithLock(process.env.VERCE_E2E_HARNESS_SAFETY_JOURNAL)
else main().catch((error) => { console.error(error.stack || error); process.exitCode = 1 })
