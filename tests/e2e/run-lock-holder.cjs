// B1 / M-S2-002 — the E2E run lock's ownership primitive.
//
// Ownership of the mutable E2E environment IS an exclusive listening socket on a dedicated
// loopback port. The kernel arbitrates bind(): at most one socket may listen on 127.0.0.1:<port>
// at a time, and every competing bind fails atomically with EADDRINUSE. There is no
// read-then-act sequence anywhere in this protocol, so there is nothing for a stale observation
// to act upon — acquisition is decided by the kernel, not by anything this code observes.
//
// This process exists because binding is asynchronous while Playwright's config module must
// decide synchronously: the parent polls the status file this process writes. The parent never
// holds the socket, so ownership always ends the same way — this process's socket closing:
//   * explicit release      — parent writes RELEASE on stdin,
//   * parent exit (any kind, including SIGKILL) — stdin reaches EOF,
//   * parent vanishes       — PID watchdog backstop,
//   * this process dying    — the OS closes the socket for us.
// Every one of those paths ends only THIS holder's own ownership. Nothing here can end anyone
// else's.
'use strict'

const net = require('node:net')
const { createHmac, randomBytes } = require('node:crypto')
const { renameSync, rmSync, writeFileSync } = require('node:fs')

const [, , portArgument, statusPath, deadlineArgument, parentPidArgument, ownerLabel] = process.argv
const port = Number(portArgument)
const deadlineAt = Number(deadlineArgument)
const parentPid = Number(parentPidArgument)
const RETRY_INTERVAL_MS = 100
const PARENT_WATCHDOG_INTERVAL_MS = 1_000
const REQUEST_TIMEOUT_MS = 3_000
const MAX_REQUEST_BYTES = 1_024
const MAX_NONCE_LENGTH = 256
const NONCE_PATTERN = /^[A-Za-z0-9_-]+$/
/** Domain separation: binds every proof to this protocol, this version, and this holder session,
 * so a proof can never be repurposed across protocols, ports or ownership sessions. Holder and
 * client MUST serialize this identically. */
const AUTH_PROTOCOL_LABEL = 'VERCE_E2E_RUN_LOCK_AUTH_V1'

let heldServer

/**
 * The re-entrancy capability (B1 capability correction). Playwright re-evaluates the config in
 * every worker process, so legitimate descendants MUST be able to operate under the root's
 * ownership instead of contending with it. Proving that they may is what this secret is for:
 * 256 bits of CSPRNG entropy, generated fresh at the moment this holder wins the bind, never
 * derived from PID/port, never reused across ownership sessions, never persisted once the owning
 * parent has consumed it, and never logged or echoed in any diagnostic.
 *
 * It exists ONLY in this holder's memory (and in the owning parent's, in transit). Ownership
 * itself is still the socket and nothing else — this capability never grants ownership, it only
 * answers "are you legitimately operating under the ownership I already hold?".
 */
const capability = randomBytes(32).toString('base64url')

/** Written temp-then-rename so the polling parent can never observe a half-written status. */
function writeStatus(status) {
  const temporaryPath = `${statusPath}.partial`
  writeFileSync(temporaryPath, JSON.stringify(status), { encoding: 'utf8', mode: 0o600 })
  renameSync(temporaryPath, statusPath)
}

function exitWith(status, code) {
  try { writeStatus(status) } catch { /* the parent's own bounded timeout still covers this */ }
  process.exit(code)
}

/**
 * The proof of capability possession, for a caller-chosen nonce.
 *
 * The capability itself NEVER crosses the socket in either direction — a caller that already
 * holds it can verify this answer locally, while an impersonator that merely occupies the port
 * learns nothing it could replay, reuse, or use to forge a future proof. Binding the port, the
 * holder PID and the caller's fresh nonce into the message is what makes each proof usable
 * exactly once, by exactly the caller that asked for it, against exactly this holder session.
 */
function proofFor(nonce) {
  const canonical = `${AUTH_PROTOCOL_LABEL}\n${port}\n${process.pid}\n${nonce}`
  return createHmac('sha256', capability).update(canonical).digest('base64url')
}

function isWellFormedNonce(nonce) {
  return typeof nonce === 'string' && nonce.length > 0 && nonce.length <= MAX_NONCE_LENGTH && NONCE_PATTERN.test(nonce)
}

/**
 * The ownership challenge endpoint. It answers exactly two questions and can do nothing else —
 * no request of any shape can release, transfer, or disturb ownership, and a malformed, oversized,
 * silent or hostile client is bounded and discarded without affecting this holder.
 *   CHALLENGE <nonce>  -> PROOF <hmac> | DENIED  (re-entrancy authority)
 *   anything else      -> identity JSON          (diagnostics; never includes the capability)
 *
 * There is deliberately no verb that accepts the capability, and no reply that asserts authority
 * in words: authority is only ever the PROOF itself, which only a holder possessing the capability
 * can produce and only the caller that chose the nonce can verify.
 */
function handleRequest(socket) {
  let received = ''
  let answered = false

  const answer = (reply) => {
    if (answered) return
    answered = true
    try { socket.end(`${reply}\n`) } catch { /* client vanished */ }
  }

  socket.setTimeout(REQUEST_TIMEOUT_MS)
  socket.setEncoding('utf8')
  // A silent client still gets the identity answer, so diagnostics work without a request.
  socket.on('timeout', () => { answer(identityJson()); try { socket.destroy() } catch { /* gone */ } })
  socket.on('error', () => { answered = true })
  socket.on('data', (chunk) => {
    if (answered) return
    received += chunk
    if (Buffer.byteLength(received, 'utf8') > MAX_REQUEST_BYTES) {
      answer('DENIED')
      try { socket.destroy() } catch { /* gone */ }
      return
    }
    const newline = received.indexOf('\n')
    if (newline < 0) return
    const request = received.slice(0, newline).trim()
    if (request === 'CHALLENGE' || request.startsWith('CHALLENGE ')) {
      // Any authentication attempt gets an authentication answer — an empty, oversized or
      // malformed nonce is a denial, never a fall-through to some other reply.
      const nonce = request.slice('CHALLENGE'.length).trim()
      answer(isWellFormedNonce(nonce) ? `PROOF ${proofFor(nonce)}` : 'DENIED')
      return
    }
    answer(identityJson())
  })
}

function identityJson() {
  return JSON.stringify({ holderPid: process.pid, parentPid, owner: ownerLabel, port })
}

function attemptBind() {
  const server = net.createServer((socket) => {
    // A connection can never affect ownership: this only answers the challenge above.
    try { handleRequest(socket) } catch { try { socket.destroy() } catch { /* gone */ } }
  })
  server.on('error', (error) => {
    if (error?.code !== 'EADDRINUSE') {
      exitWith({ state: 'error', reason: `bind failed: ${error?.code ?? error?.message ?? 'unknown'}`, port }, 3)
    }
    if (Date.now() >= deadlineAt) {
      exitWith({ state: 'timeout', reason: `another E2E run still owns 127.0.0.1:${port}`, port }, 4)
    }
    setTimeout(attemptBind, RETRY_INTERVAL_MS)
  })

  server.on('listening', () => {
    heldServer = server
    // The capability travels to the owning parent here and nowhere else. The parent consumes and
    // deletes this file immediately, so it never outlives the handshake (see e2e-env.cjs).
    writeStatus({ state: 'held', holderPid: process.pid, parentPid, port, capability, heldSince: new Date().toISOString() })
  })

  // `exclusive: true` keeps the handle out of any cluster-shared accounting, so this is a plain
  // kernel-arbitrated bind on both Windows and POSIX.
  server.listen({ host: '127.0.0.1', port, exclusive: true })
}

// ---- Release paths. Each one ends ONLY this holder's ownership. ----

function release() {
  try { heldServer?.close() } catch { /* closing on the way out */ }
  // Ownership is over, so the status file has no readers left. Clearing it here also cleans up
  // after an owner that was killed before it could clean up after itself.
  try { rmSync(statusPath, { force: true }) } catch { /* best effort */ }
  process.exit(0)
}

process.stdin.resume()
process.stdin.setEncoding('utf8')
process.stdin.on('data', (chunk) => { if (chunk.includes('RELEASE')) release() })
process.stdin.on('end', release)    // parent exited — normally or abruptly
process.stdin.on('close', release)
process.stdin.on('error', release)

// Backstop for the exotic case where the stdin pipe outlives the parent (e.g. an inherited write
// end). It only ever concludes that OUR OWN parent is gone: `kill(pid, 0)` failing is conclusive
// for "no such process", and the opposite error direction (a recycled PID looking alive) merely
// keeps us holding our own lock longer, which is the safe direction.
setInterval(() => {
  if (!Number.isInteger(parentPid) || parentPid <= 0) return
  try { process.kill(parentPid, 0) } catch { release() }
}, PARENT_WATCHDOG_INTERVAL_MS).unref()

attemptBind()
