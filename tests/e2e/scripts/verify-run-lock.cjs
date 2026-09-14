// B1 / M-S2-002 — certification of the E2E run lock's SAFETY invariant:
//
//   At most one process may own the mutable E2E environment at any instant.
//
// Ownership is an exclusive loopback bind (see run-lock-holder.cjs), so these tests exercise the
// real primitive with real, independent OS processes — not a simulation of one. They run against
// a dedicated test port so certifying the lock never contends with an actual E2E run.
'use strict'

const assert = require('node:assert/strict')
const { execFileSync, spawn } = require('node:child_process')
const { randomBytes, randomUUID } = require('node:crypto')
const { mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } = require('node:fs')
const { tmpdir } = require('node:os')
const { join, resolve } = require('node:path')
const {
  E2E_RUN_LOCK_PORT,
  acquireE2eRunLock,
  releaseE2eRunLock,
  isE2eRunLockHeld,
  assertE2eRunLockHeld,
  describeE2eRunLockOwner,
} = require('../e2e-env.cjs')

// Certification runs on its own port so it can never disturb (or be disturbed by) a real run.
const TEST_PORT = 7355
const HELPER_DIRECTORY = mkdtempSync(join(tmpdir(), 'verce-run-lock-cert-'))
const HELPER = join(HELPER_DIRECTORY, 'holder-helper.cjs')
const FAKE_LISTENER = join(HELPER_DIRECTORY, 'fake-listener.cjs')
const E2E_DIR = resolve(__dirname, '..')

// An independent process that acquires the SAME lock through the SAME production API, records
// when it entered and left the critical section, and can be asked to crash instead of releasing
// or to attempt a release it is not entitled to. Whatever marker it inherits (genuine, forged or
// stale) it goes through `acquireE2eRunLock` exactly as any real harness process would.
writeFileSync(HELPER, `
'use strict'
const { writeFileSync } = require('node:fs')
const { acquireE2eRunLock, releaseE2eRunLock } = require(${JSON.stringify(join(E2E_DIR, 'e2e-env.cjs'))})
const [, , port, journalPath, label, holdMs, mode, timeoutMs] = process.argv
function record(event, extra) {
  writeFileSync(journalPath, JSON.stringify({ event, label, at: Date.now(), pid: process.pid, ...extra }) + '\\n', { flag: 'a' })
}
try {
  const lock = acquireE2eRunLock({ timeoutMs: Number(timeoutMs), port: Number(port) })
  // NOTE: the capability itself is never recorded or printed anywhere — only whether this call
  // was granted re-entrancy.
  record('enter', { reentrant: lock.reentrant === true })
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, Number(holdMs))
  if (mode === 'crash') { record('crash'); process.kill(process.pid, 'SIGKILL') }
  if (mode === 'release-attempt') {
    // A re-entrant descendant must not be able to end the ROOT owner's ownership.
    releaseE2eRunLock(lock)
    record('release-attempted')
    process.exit(0)
  }
  record('leave')
  releaseE2eRunLock(lock)
  process.exit(0)
} catch (error) {
  record('denied')
  process.stderr.write(String(error && error.message) + '\\n')
  process.exit(7)
}
`, 'utf8')

// A stand-in for ANY unrelated process that happens to occupy the lock port. It never knows the
// capability, so whatever it replies must be worthless to a caller — that is the whole point of
// challenge-response. `wiretap` additionally records exactly what the client put on the wire.
writeFileSync(FAKE_LISTENER, `
'use strict'
const net = require('node:net')
const { randomBytes } = require('node:crypto')
const { writeFileSync } = require('node:fs')
const [, , port, mode, payload] = process.argv
const NL = String.fromCharCode(10)
net.createServer((socket) => {
  socket.on('error', () => {})
  let received = ''
  socket.setEncoding('utf8')
  socket.on('data', (chunk) => {
    received += chunk
    if (!received.includes(NL) && received.length < 8192) return
    if (mode === 'wiretap') { try { writeFileSync(payload, received, { flag: 'a' }) } catch {} }
    if (mode === 'silent') return
    if (mode === 'authorized') return socket.end('AUTHORIZED' + NL)
    if (mode === 'randomproof') return socket.end('PROOF ' + randomBytes(32).toString('base64url') + NL)
    if (mode === 'replay') return socket.end('PROOF ' + payload + NL)
    return socket.end('AUTHORIZED' + NL)
  })
}).listen({ host: '127.0.0.1', port: Number(port), exclusive: true }, () => { setInterval(() => {}, 1000) })
`, 'utf8')

function newJournal() {
  return join(tmpdir(), `verce-run-lock-journal-${randomUUID()}.ndjson`)
}

function readJournal(path) {
  try {
    return readFileSync(path, 'utf8').split('\n').filter(Boolean).map((line) => JSON.parse(line))
  } catch {
    return []
  }
}

/**
 * @param independent  true  — an UNRELATED run: the descendant re-entrancy marker is stripped, so
 *                            the child genuinely contends for the lock like a second developer's
 *                            `npx playwright test` would.
 *                     false — a descendant spawned inside our own critical section.
 */
function startContender({ journalPath, label, holdMs = 400, mode = 'release', timeoutMs = 30_000, port = TEST_PORT, independent = true, marker }) {
  const env = { ...process.env }
  if (independent) delete env.VERCE_E2E_RUN_LOCK_HOLDER
  // A forged/stale marker supplied by an attacker, exactly as an unrelated process could set it.
  if (marker !== undefined) env.VERCE_E2E_RUN_LOCK_HOLDER = marker
  const child = spawn(process.execPath, [HELPER, String(port), journalPath, label, String(holdMs), mode, String(timeoutMs)], {
    stdio: ['ignore', 'ignore', 'pipe'], windowsHide: true, env,
  })
  let stderr = ''
  child.stderr.on('data', (chunk) => { stderr += chunk })
  const exited = new Promise((resolveExit) => child.on('exit', (code) => resolveExit({ code, stderr })))
  return { child, exited }
}

function sleep(ms) { Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms) }

// ---- 1. single acquisition succeeds -----------------------------------------------------------

function testSingleAcquisition() {
  const lock = acquireE2eRunLock({ timeoutMs: 15_000, port: TEST_PORT })
  assert.equal(lock.reentrant, false)
  assert.equal(lock.port, TEST_PORT)
  assert.ok(isE2eRunLockHeld(lock), 'a freshly acquired lock must report as held')
  assertE2eRunLockHeld(lock)
  const owner = describeE2eRunLockOwner(TEST_PORT)
  assert.equal(owner?.holderPid, lock.holderPid, 'the live holder must identify itself on the lock port')
  releaseE2eRunLock(lock)
  assert.equal(isE2eRunLockHeld(lock), false, 'a released lock must no longer report as held')
  console.log('PASS: single acquisition succeeds, reports ownership, and releases')
}

// ---- 2. release allows the next acquisition ---------------------------------------------------

function testSequentialOwnership() {
  const first = acquireE2eRunLock({ timeoutMs: 15_000, port: TEST_PORT })
  releaseE2eRunLock(first)
  const second = acquireE2eRunLock({ timeoutMs: 15_000, port: TEST_PORT })
  assert.notEqual(second.holderPid, first.holderPid, 'the second acquisition must be a genuinely new holder')
  releaseE2eRunLock(second)
  console.log('PASS: ownership is handed over cleanly on release')
}

// ---- 3./6. two independent processes serialize; critical sections never overlap ---------------

async function testTwoProcessesSerialize() {
  const journalPath = newJournal()
  const a = startContender({ journalPath, label: 'A', holdMs: 1_500 })
  // Give A a head start so B is guaranteed to arrive while A owns the lock.
  sleep(700)
  assert.deepEqual(readJournal(journalPath).map((e) => e.event), ['enter'], 'A must be inside the critical section')
  const b = startContender({ journalPath, label: 'B', holdMs: 200 })
  sleep(500)
  const midway = readJournal(journalPath)
  assert.equal(midway.filter((e) => e.event === 'enter').length, 1, 'B must NOT enter while A owns the lock')

  const [resultA, resultB] = await Promise.all([a.exited, b.exited])
  assert.equal(resultA.code, 0, `A failed: ${resultA.stderr}`)
  assert.equal(resultB.code, 0, `B failed: ${resultB.stderr}`)

  const journal = readJournal(journalPath)
  const aEnter = journal.find((e) => e.label === 'A' && e.event === 'enter')
  const aLeave = journal.find((e) => e.label === 'A' && e.event === 'leave')
  const bEnter = journal.find((e) => e.label === 'B' && e.event === 'enter')
  const bLeave = journal.find((e) => e.label === 'B' && e.event === 'leave')
  assert.ok(aEnter && aLeave && bEnter && bLeave, 'both processes must record a full critical section')
  assert.ok(bEnter.at >= aLeave.at, `B entered at ${bEnter.at} before A left at ${aLeave.at} — critical sections overlapped`)
  assert.ok(aEnter.at <= aLeave.at && bEnter.at <= bLeave.at)
  rmSync(journalPath, { force: true })
  console.log(`PASS: two independent processes serialize — B entered ${bEnter.at - aLeave.at}ms after A left, never overlapping`)
}

// ---- 4. the wait is bounded --------------------------------------------------------------------

async function testBoundedTimeout() {
  const journalPath = newJournal()
  const lock = acquireE2eRunLock({ timeoutMs: 15_000, port: TEST_PORT })
  let result
  let elapsed
  try {
    const startedAt = Date.now()
    // A genuinely independent process, so this exercises real contention rather than the
    // deliberate in-process/descendant re-entrancy path.
    const loser = startContender({ journalPath, label: 'TIMEOUT', holdMs: 0, timeoutMs: 1_000 })
    result = await loser.exited
    elapsed = Date.now() - startedAt
  } finally {
    releaseE2eRunLock(lock)
  }
  assert.equal(result.code, 7, 'a contender that cannot acquire must fail cleanly, not proceed')
  assert.match(result.stderr, /Could not acquire the E2E run lock/, `unexpected failure: ${result.stderr}`)
  assert.ok(!readJournal(journalPath).some((e) => e.event === 'enter'), 'the losing contender must never have entered the critical section')
  assert.ok(elapsed < 30_000, `the bounded wait took ${elapsed}ms`)
  rmSync(journalPath, { force: true })
  console.log(`PASS: a contended acquisition fails cleanly after its bounded timeout (${elapsed}ms) and never enters the critical section`)
}

/** The ancestor owns the environment for the descendant's whole lifetime, so a descendant must
 * be granted re-entrant ownership instead of deadlocking against its own ancestor. */
async function testDescendantReentrancy() {
  const journalPath = newJournal()
  const lock = acquireE2eRunLock({ timeoutMs: 15_000, port: TEST_PORT })
  let result
  try {
    const descendant = startContender({ journalPath, label: 'CHILD', holdMs: 50, timeoutMs: 3_000, independent: false })
    result = await descendant.exited
  } finally {
    releaseE2eRunLock(lock)
  }
  assert.equal(result.code, 0, `a descendant inside the owner's critical section must not contend: ${result.stderr}`)
  assert.ok(readJournal(journalPath).some((e) => e.event === 'enter'), 'the descendant must have been granted re-entrant ownership')
  assert.ok(isE2eRunLockHeld(lock) === false, 'the ancestor released its own lock afterwards')
  rmSync(journalPath, { force: true })
  console.log('PASS: a descendant of the owner is granted re-entrant ownership instead of deadlocking')
}

// ---- 5. a crashed owner never blocks the environment permanently ------------------------------

async function testCrashedOwnerDoesNotBlock() {
  const journalPath = newJournal()
  const crasher = startContender({ journalPath, label: 'CRASH', holdMs: 500, mode: 'crash' })
  const result = await crasher.exited
  assert.notEqual(result.code, 0, 'the crashing contender must not exit cleanly')
  const journal = readJournal(journalPath)
  assert.ok(journal.some((e) => e.event === 'crash'), 'the contender must have crashed while OWNING the lock')
  assert.ok(!journal.some((e) => e.event === 'leave'), 'the contender must never have released the lock')

  const startedAt = Date.now()
  const recovered = acquireE2eRunLock({ timeoutMs: 30_000, port: TEST_PORT })
  console.log(`PASS: a SIGKILLed owner released the lock automatically — next acquisition succeeded ${Date.now() - startedAt}ms later, with no manual cleanup`)
  releaseE2eRunLock(recovered)
  rmSync(journalPath, { force: true })
}

// ---- 7. a forged re-entrancy marker cannot bypass the socket ----------------------------------

/** Runs one unrelated process carrying `marker` and asserts it never enters the critical section
 * while we hold the lock. Returns the recorded journal for further assertions. */
async function expectMarkerDenied({ marker, label, lockHeld, port = TEST_PORT }) {
  const journalPath = newJournal()
  const attacker = startContender({ journalPath, label, marker, timeoutMs: 1_000, holdMs: 0, port })
  const result = await attacker.exited
  const journal = readJournal(journalPath)
  const entered = journal.filter((e) => e.event === 'enter')
  assert.deepEqual(entered, [], `${label}: a forged marker entered the critical section — ${JSON.stringify(entered)}`)
  assert.equal(result.code, 7, `${label}: expected a clean denial, got exit ${result.code} (${result.stderr})`)
  assert.ok(journal.some((e) => e.event === 'denied'), `${label}: the attacker must record a denial`)
  if (lockHeld) assert.ok(isE2eRunLockHeld(lockHeld), `${label}: our ownership must survive the attempt`)
  rmSync(journalPath, { force: true })
  return journal
}

async function testForgedMarkersDenied() {
  const lock = acquireE2eRunLock({ timeoutMs: 15_000, port: TEST_PORT })
  try {
    // Luna's exact reproduction: a live holder PID plus the correct port, self-declared.
    await expectMarkerDenied({ marker: `${lock.holderPid}:${TEST_PORT}`, label: 'FORGE-PID-PORT', lockHeld: lock })
    // Correct PID and port, but the capability is fabricated.
    await expectMarkerDenied({ marker: `${lock.holderPid}:${TEST_PORT}:not-the-real-capability`, label: 'FORGE-WRONG-CAP', lockHeld: lock })
    // A full-entropy guess of the right shape.
    await expectMarkerDenied({ marker: `${lock.holderPid}:${TEST_PORT}:${randomBytes(32).toString('base64url')}`, label: 'FORGE-RANDOM-CAP', lockHeld: lock })
    // A live but unrelated PID (this very process) claiming the same port.
    await expectMarkerDenied({ marker: `${process.pid}:${TEST_PORT}:${randomBytes(32).toString('base64url')}`, label: 'FORGE-LIVE-PID', lockHeld: lock })
    // The real capability, but pointed at a different (free) port — authority is per-holder, and
    // nothing is listening there, so it must fall through to a normal acquisition of THAT port.
    const freePort = TEST_PORT + 11
    const journalPath = newJournal()
    const stray = startContender({ journalPath, label: 'FORGE-FREE-PORT', marker: `${lock.holderPid}:${freePort}:${lock.capability}`, port: freePort, timeoutMs: 1_000, holdMs: 0 })
    const strayResult = await stray.exited
    const strayJournal = readJournal(journalPath)
    const strayEnter = strayJournal.find((e) => e.event === 'enter')
    assert.equal(strayResult.code, 0, `a free unrelated port should still be acquirable normally: ${strayResult.stderr}`)
    assert.equal(strayEnter?.reentrant, false, 'a marker for a different port must never grant re-entrancy — it must bind that port itself')
    assert.ok(isE2eRunLockHeld(lock), 'our ownership must be untouched by activity on another port')
    rmSync(journalPath, { force: true })
    console.log('PASS: forged markers (PID:PORT, wrong capability, random capability, live unrelated PID, other port) are all denied — none entered the critical section')
  } finally {
    releaseE2eRunLock(lock)
  }
}

// ---- 8. a legitimate descendant authenticates; a stale capability never does -------------------

async function testLegitimateDescendantAndStaleCapability() {
  const first = acquireE2eRunLock({ timeoutMs: 15_000, port: TEST_PORT })
  const staleCapability = first.capability
  const staleHolderPid = first.holderPid

  // A genuine descendant, inheriting the real marker, is granted re-entrancy by the live holder
  // and never attempts a second bind (proved by the fact that the port is still ours throughout).
  const journalPath = newJournal()
  const descendant = startContender({ journalPath, label: 'DESCENDANT', holdMs: 50, timeoutMs: 5_000, independent: false })
  const descendantResult = await descendant.exited
  const descendantEnter = readJournal(journalPath).find((e) => e.event === 'enter')
  assert.equal(descendantResult.code, 0, `a legitimate descendant must be admitted: ${descendantResult.stderr}`)
  assert.equal(descendantEnter?.reentrant, true, 'a legitimate descendant must be granted RE-ENTRANT ownership, not a new bind')
  assert.ok(isE2eRunLockHeld(first), 'the root ownership must be intact after a descendant operates under it')
  rmSync(journalPath, { force: true })
  console.log('PASS: a legitimate descendant authenticates against the live holder and is granted re-entrancy without a second bind')

  // Rotation: the next ownership session mints a different capability, and the previous one is
  // worthless against it.
  releaseE2eRunLock(first)
  const second = acquireE2eRunLock({ timeoutMs: 15_000, port: TEST_PORT })
  try {
    assert.notEqual(second.capability, staleCapability, 'each ownership session must mint a fresh capability')
    assert.notEqual(second.holderPid, staleHolderPid, 'the new ownership session must be a new holder')
    await expectMarkerDenied({ marker: `${second.holderPid}:${TEST_PORT}:${staleCapability}`, label: 'STALE-CAP', lockHeld: second })
    console.log('PASS: capabilities rotate per ownership session and a previous owner\'s capability is denied against the new holder')
  } finally {
    releaseE2eRunLock(second)
  }

  // And once no holder exists at all, the same capability authorizes nothing.
  assert.equal(isE2eRunLockHeld({ port: TEST_PORT, capability: staleCapability, holderPid: staleHolderPid }), false,
    'a capability must authorize nothing once its holder is gone')
  console.log('PASS: a capability whose holder has exited authorizes nothing')
}

// ---- 9. a re-entrant descendant cannot end the root's ownership --------------------------------

async function testReentrantDescendantCannotReleaseRoot() {
  const root = acquireE2eRunLock({ timeoutMs: 15_000, port: TEST_PORT })
  try {
    const journalPath = newJournal()
    const descendant = startContender({ journalPath, label: 'RELEASER', mode: 'release-attempt', holdMs: 0, timeoutMs: 5_000, independent: false })
    const result = await descendant.exited
    const journal = readJournal(journalPath)
    assert.equal(result.code, 0, `the descendant should run cleanly: ${result.stderr}`)
    assert.equal(journal.find((e) => e.event === 'enter')?.reentrant, true, 'the descendant must have been re-entrant')
    assert.ok(journal.some((e) => e.event === 'release-attempted'), 'the descendant must have called releaseE2eRunLock')
    assert.ok(isE2eRunLockHeld(root), 'the ROOT ownership must survive a re-entrant descendant calling release')
    const owner = describeE2eRunLockOwner(TEST_PORT)
    assert.equal(owner?.holderPid, root.holderPid, 'the original holder must still own the port')
    rmSync(journalPath, { force: true })
    console.log('PASS: a re-entrant descendant calling release is a local no-op — root ownership is untouched')
  } finally {
    releaseE2eRunLock(root)
  }
}

// ---- 10. an unrelated listener on the lock port is never mistaken for a holder -----------------

async function testPortCollisionIsNotMistakenForOwnership() {
  const collisionPort = TEST_PORT + 12
  const squatter = spawn(process.execPath, ['-e',
    `require('node:net').createServer((s)=>{s.on('error',()=>{});s.end('garbage\\n')}).listen({host:'127.0.0.1',port:${collisionPort},exclusive:true},()=>{setInterval(()=>{},1000)})`,
  ], { stdio: ['ignore', 'ignore', 'pipe'], windowsHide: true })
  let squatterStderr = ''
  squatter.stderr.on('data', (chunk) => { squatterStderr += chunk })
  try {
    // Wait until the foreign service is genuinely listening, rather than assuming it started.
    let occupied = ''
    const deadline = Date.now() + 15_000
    while (!occupied && Date.now() < deadline) {
      occupied = sendRaw(collisionPort, 'WHOAMI' + String.fromCharCode(10))
      if (!occupied) sleep(200)
    }
    assert.equal(occupied, 'garbage', `the foreign listener never came up on ${collisionPort}: ${squatterStderr}`)

    // Even with a marker naming that port and a capability, an unrelated listener cannot grant
    // authority — and the port cannot be bound either, so this must be a bounded, safe failure.
    const journalPath = newJournal()
    const victim = startContender({ journalPath, label: 'COLLISION', marker: `${process.pid}:${collisionPort}:${randomBytes(32).toString('base64url')}`, port: collisionPort, timeoutMs: 1_000, holdMs: 0 })
    const result = await victim.exited
    const journal = readJournal(journalPath)
    assert.deepEqual(journal.filter((e) => e.event === 'enter'), [], 'a foreign listener must never be treated as a valid holder')
    assert.equal(result.code, 7, `expected a bounded safe failure, got exit ${result.code}`)
    rmSync(journalPath, { force: true })
    console.log('PASS: an unrelated service occupying the lock port causes a bounded safe failure, never a critical-section entry')
  } finally {
    squatter.kill('SIGKILL')
  }
}

// ---- 11. a foreign listener cannot impersonate the holder --------------------------------------

/** Spawns a fake listener that occupies a port and answers every request according to `mode`.
 * It never knows the capability, so it stands in for any unrelated process — accidental or
 * hostile — that happens to own the lock port. */
function startFakeListener(port, mode, payload = '') {
  const child = spawn(process.execPath, [FAKE_LISTENER, String(port), mode, payload], {
    stdio: ['ignore', 'ignore', 'pipe'], windowsHide: true,
  })
  let stderr = ''
  child.stderr.on('data', (chunk) => { stderr += chunk })
  // Wait until it is genuinely listening before the test relies on it.
  const deadline = Date.now() + 15_000
  while (Date.now() < deadline) {
    if (sendRaw(port, 'PING\n') !== '' || child.exitCode !== null) break
    sleep(150)
  }
  return { child, stderr: () => stderr }
}

/** Sends one raw request line to whatever is listening and returns its first reply line. */
function sendRaw(port, request) {
  const probe = "const net=require('node:net');let payload='';process.stdin.setEncoding('utf8');"
    + "process.stdin.on('data',(c)=>{payload+=c});process.stdin.on('end',()=>{"
    + "const s=net.connect({host:'127.0.0.1',port:Number(process.argv[1])});let d='';s.setEncoding('utf8');"
    + "const done=()=>{process.stdout.write(d.split(String.fromCharCode(10))[0]);process.exit(0)};"
    + "s.on('connect',()=>{s.write(payload)});s.on('data',(c)=>{d+=c;if(d.includes(String.fromCharCode(10)))done()});"
    + "s.on('end',done);s.on('error',()=>{process.exit(1)});"
    + "setTimeout(done,5000).unref()});"
  try {
    return execFileSync(process.execPath, ['-e', probe, String(port)], { input: request, encoding: 'utf8', timeout: 9_000 }).trim()
  } catch {
    return ''
  }
}

/** Drives a real acquisition attempt against a port owned by a fake listener, carrying a
 * syntactically valid inherited marker, and asserts the attempt is denied without ever entering
 * the critical section. */
async function expectImpersonatorDenied({ port, mode, payload, capability, holderPid, label }) {
  const listener = startFakeListener(port, mode, payload)
  try {
    const journalPath = newJournal()
    const victim = startContender({
      journalPath, label, port, timeoutMs: 1_000, holdMs: 0,
      marker: `${holderPid}:${port}:${capability}`,
    })
    const result = await victim.exited
    const journal = readJournal(journalPath)
    assert.deepEqual(journal.filter((e) => e.event === 'enter'), [], `${label}: an impersonating listener granted re-entrancy`)
    assert.equal(result.code, 7, `${label}: expected a bounded safe failure, got exit ${result.code} (${result.stderr})`)
    rmSync(journalPath, { force: true })
  } finally {
    listener.child.kill('SIGKILL')
    sleep(200)
  }
}

async function testForeignListenerCannotImpersonateHolder() {
  const port = TEST_PORT + 13
  const capability = randomBytes(32).toString('base64url')

  // Luna's exact finding: the impersonator simply asserts success in words.
  await expectImpersonatorDenied({ port, mode: 'authorized', capability, holderPid: process.pid, label: 'IMPERSONATE-AUTHORIZED' })
  // A well-formed but fabricated proof of the right shape and length.
  await expectImpersonatorDenied({ port, mode: 'randomproof', capability, holderPid: process.pid, label: 'IMPERSONATE-RANDOM-PROOF' })
  // Plain noise.
  await expectImpersonatorDenied({ port, mode: 'garbage', capability, holderPid: process.pid, label: 'IMPERSONATE-GARBAGE' })
  console.log('PASS: a foreign listener replying AUTHORIZED, a random PROOF, or garbage is denied — words are never authority, only a verifiable proof is')
}

/** A listener that answers but never says anything must not hang the harness. */
async function testSilentForeignListenerTimesOut() {
  const port = TEST_PORT + 14
  const capability = randomBytes(32).toString('base64url')
  const listener = startFakeListener(port, 'silent')
  try {
    const journalPath = newJournal()
    const startedAt = Date.now()
    const victim = startContender({
      journalPath, label: 'IMPERSONATE-SILENT', port, timeoutMs: 1_000, holdMs: 0,
      marker: `${process.pid}:${port}:${capability}`,
    })
    const result = await victim.exited
    const elapsed = Date.now() - startedAt
    assert.deepEqual(readJournal(journalPath).filter((e) => e.event === 'enter'), [], 'a silent listener must never grant re-entrancy')
    assert.equal(result.code, 7, `expected a bounded safe failure, got exit ${result.code}`)
    assert.ok(elapsed < 60_000, `the authentication attempt against a silent listener took ${elapsed}ms — it must be bounded`)
    rmSync(journalPath, { force: true })
    console.log(`PASS: a listener that accepts the connection but never answers is bounded (${elapsed}ms) and falls through to a safe failure`)
  } finally {
    listener.child.kill('SIGKILL')
    sleep(200)
  }
}

/** A proof is bound to the nonce that requested it, so a captured proof cannot be reused. */
async function testProofReplayDenied() {
  const port = TEST_PORT + 15
  // Take a REAL proof from a REAL holder for a nonce we choose.
  const genuine = acquireE2eRunLock({ timeoutMs: 15_000, port })
  const capability = genuine.capability
  const holderPid = genuine.holderPid
  const firstNonce = randomBytes(32).toString('base64url')
  const capturedReply = sendRaw(port, `CHALLENGE ${firstNonce}\n`)
  assert.ok(capturedReply.startsWith('PROOF '), 'the genuine holder must answer a well-formed challenge with a proof')
  const capturedProof = capturedReply.slice('PROOF '.length).trim() // never printed
  releaseE2eRunLock(genuine)
  sleep(300)

  // Now a listener replays that exact proof against every future challenge.
  await expectImpersonatorDenied({ port, mode: 'replay', payload: capturedProof, capability, holderPid, label: 'REPLAY' })
  console.log('PASS: a genuine proof captured for one nonce is rejected when replayed against a fresh nonce')
}

/** The capability must never appear in what the client puts on the wire. */
async function testCapabilityNeverCrossesTheWire() {
  const port = TEST_PORT + 16
  const capability = randomBytes(32).toString('base64url')
  const wiretapPath = join(HELPER_DIRECTORY, 'wiretap.log')
  rmSync(wiretapPath, { force: true })
  const listener = startFakeListener(port, 'wiretap', wiretapPath)
  try {
    const journalPath = newJournal()
    const victim = startContender({
      journalPath, label: 'WIRETAP', port, timeoutMs: 1_000, holdMs: 0,
      marker: `${process.pid}:${port}:${capability}`,
    })
    const result = await victim.exited
    assert.equal(result.code, 7, 'the wiretapping impersonator must still be denied')
    assert.deepEqual(readJournal(journalPath).filter((e) => e.event === 'enter'), [], 'a wiretapping listener must never grant re-entrancy')

    const observed = readFileSync(wiretapPath, 'utf8')
    assert.ok(observed.includes('CHALLENGE '), `the client must have sent a challenge; observed: ${observed.slice(0, 80)}`)
    assert.ok(!observed.includes(capability), 'THE CAPABILITY WAS SENT OVER THE SOCKET — an impersonating listener would learn the secret')
    assert.ok(!observed.includes('AUTH '), 'no legacy AUTH verb may remain on the wire')
    rmSync(journalPath, { force: true })
    console.log('PASS: the client sends only a nonce — an impersonating listener observes no capability and learns nothing reusable')
  } finally {
    listener.child.kill('SIGKILL')
    sleep(200)
  }
}

// ---- 12. the challenge endpoint is bounded and cannot be used to disturb ownership -------------

function testChallengeEndpointRobustness() {
  const lock = acquireE2eRunLock({ timeoutMs: 15_000, port: TEST_PORT })
  try {
    const newline = String.fromCharCode(10)
    // Malformed / hostile challenges are answered, but never with a usable proof.
    assert.equal(sendRaw(TEST_PORT, `CHALLENGE${newline}`), 'DENIED', 'an empty nonce must be denied')
    assert.equal(sendRaw(TEST_PORT, `CHALLENGE ${newline}`), 'DENIED', 'a blank nonce must be denied')
    assert.equal(sendRaw(TEST_PORT, `CHALLENGE not a valid nonce!${newline}`), 'DENIED', 'a malformed nonce must be denied')
    assert.equal(sendRaw(TEST_PORT, `CHALLENGE ${'n'.repeat(300)}${newline}`), 'DENIED', 'an oversized nonce must be denied')
    assert.equal(sendRaw(TEST_PORT, `CHALLENGE ${'n'.repeat(5_000)}${newline}`), 'DENIED', 'an oversized frame must be bounded and denied')
    // Unknown verbs, command-looking requests and unterminated frames are inert.
    for (const request of [`AUTH ${lock.capability}${newline}`, `RELEASE${newline}`, `random bytes ${newline}`, 'unterminated']) {
      const reply = sendRaw(TEST_PORT, request)
      assert.ok(!reply.startsWith('PROOF '), `an inert request must never yield a proof (request answered: ${reply.slice(0, 40)})`)
      assert.notEqual(reply, 'AUTHORIZED', 'the holder must never assert authority in words')
    }
    // The legacy verb in particular must be dead: offering the capability wins nothing.
    assert.ok(!sendRaw(TEST_PORT, `AUTH ${lock.capability}${newline}`).startsWith('PROOF'), 'the legacy AUTH verb must not be honoured')

    // After every hostile request ownership is intact and still authenticates normally.
    assert.ok(isE2eRunLockHeld(lock), 'ownership must survive malformed, oversized and hostile requests')
    const nonce = randomBytes(32).toString('base64url')
    assert.ok(sendRaw(TEST_PORT, `CHALLENGE ${nonce}${newline}`).startsWith('PROOF '), 'the holder must keep serving valid challenges after denials')
    console.log('PASS: the challenge endpoint is bounded; malformed, oversized, unknown and legacy requests never yield a proof, crash the holder, or release ownership')
  } finally {
    releaseE2eRunLock(lock)
  }
}

// ---- 12. the capability never leaks into any observable surface --------------------------------

function testCapabilityNeverLeaks() {
  const lock = acquireE2eRunLock({ timeoutMs: 15_000, port: TEST_PORT })
  try {
    const capability = lock.capability
    assert.ok(capability && capability.length >= 43, 'the capability must carry at least 256 bits of entropy')

    // Diagnostics must never echo it.
    const identity = JSON.stringify(describeE2eRunLockOwner(TEST_PORT))
    assert.ok(!identity.includes(capability), 'the owner diagnostic must not contain the capability')

    // A contended acquisition's error message must never echo it.
    const journalPath = newJournal()
    const loser = startContender({ journalPath, label: 'LEAK', timeoutMs: 1_000, holdMs: 0 })
    return loser.exited.then((result) => {
      assert.ok(!result.stderr.includes(capability), 'a timeout diagnostic must not contain the capability')
      // Nothing on disk may retain it after the handshake.
      const residue = readdirSync(tmpdir()).filter((name) => name.startsWith('verce-e2e-run-lock-'))
      for (const name of residue) {
        assert.ok(!readFileSync(join(tmpdir(), name), 'utf8').includes(capability), `${name} still contains the capability`)
      }
      rmSync(journalPath, { force: true })
      releaseE2eRunLock(lock)
      console.log('PASS: the capability appears in no diagnostic, no error message and no file left on disk after the handshake')
    })
  } catch (error) {
    releaseE2eRunLock(lock)
    throw error
  }
}

// ---- 13. the protocol has no destructive step that could displace a live owner -----------------

function testNoDestructiveOwnershipOperation() {
  const source = readFileSync(join(E2E_DIR, 'e2e-env.cjs'), 'utf8') + readFileSync(join(E2E_DIR, 'run-lock-holder.cjs'), 'utf8')
  // The old protocol displaced owners by renaming/removing a canonical lock directory; the new
  // one owns a socket, so no such operation may exist anywhere in the lock implementation.
  assert.doesNotMatch(source, /\.e2e-run-lock/, 'the filesystem lock directory must be gone entirely')
  assert.doesNotMatch(source, /\btaskkill\b/i, 'lock recovery must never terminate another process')

  // `process.kill` may appear ONLY as a liveness probe (signal 0) — never as a terminating
  // signal, because no process may ever end another process's ownership.
  for (const call of source.match(/process\.kill\([^)]*\)/g) ?? []) {
    assert.match(call, /,\s*0\s*\)$/, `process.kill must only probe liveness with signal 0, found: ${call}`)
  }
  // Any other kill must target a holder child THIS process spawned and therefore owns.
  for (const [, target] of source.matchAll(/([A-Za-z_$][\w$]*(?:\.[\w$]+)*)\.kill\(/g)) {
    if (target === 'process') continue
    assert.match(target, /holder$/i, `the lock protocol may only kill its own holder child, found: ${target}.kill()`)
  }
  console.log('PASS: the protocol contains no operation capable of displacing another process\'s ownership')
}

// ---- 8. the real harness port is untouched by certification -----------------------------------

function testRealHarnessPortUntouched() {
  assert.notEqual(TEST_PORT, E2E_RUN_LOCK_PORT, 'certification must not run on the real harness lock port')
  console.log(`PASS: certification ran on 127.0.0.1:${TEST_PORT}; the harness lock port ${E2E_RUN_LOCK_PORT} was never contended`)
}

async function main() {
  try {
    testSingleAcquisition()
    testSequentialOwnership()
    await testTwoProcessesSerialize()
    await testBoundedTimeout()
    await testDescendantReentrancy()
    await testCrashedOwnerDoesNotBlock()
    await testForgedMarkersDenied()
    await testLegitimateDescendantAndStaleCapability()
    await testReentrantDescendantCannotReleaseRoot()
    await testPortCollisionIsNotMistakenForOwnership()
    await testForeignListenerCannotImpersonateHolder()
    await testSilentForeignListenerTimesOut()
    await testProofReplayDenied()
    await testCapabilityNeverCrossesTheWire()
    testChallengeEndpointRobustness()
    await testCapabilityNeverLeaks()
    testNoDestructiveOwnershipOperation()
    testRealHarnessPortUntouched()
  } finally {
    rmSync(HELPER_DIRECTORY, { recursive: true, force: true })
  }
}

main().catch((error) => { console.error(error.stack || error); process.exitCode = 1 })
