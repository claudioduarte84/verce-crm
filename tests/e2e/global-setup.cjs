const { execFileSync } = require('node:child_process')
const { unlinkSync, writeFileSync } = require('node:fs')
const { join } = require('node:path')
const {
  resolveE2eConnectionString,
  assertDisposableE2eDatabase,
  assertE2eRunLockHeld,
  resolveOwnerEmail,
  resolveOwnerName,
} = require('./e2e-env.cjs')

module.exports = () => {
  // Database creation and migration (including the Quartz schema) already happened in
  // playwright.config.ts, synchronously, before webServer could spawn (§4 — one canonical
  // source, no parallel mechanism). This only needs the resolved values to bootstrap the Owner.
  // Continuity check first: ownership is the run-lock holder's socket, so a dead holder means
  // this run no longer owns the environment it is about to keep mutating.
  assertE2eRunLockHeld()
  const connectionString = resolveE2eConnectionString()
  assertDisposableE2eDatabase(connectionString)

  const ownerEmail = resolveOwnerEmail()
  const ownerName = resolveOwnerName()
  const tokenPath = join(__dirname, '.playwright-bootstrap-token')
  try { unlinkSync(tokenPath) } catch {}

  const childEnv = { ...process.env, ConnectionStrings__Verce: connectionString }
  let output
  try {
    output = execFileSync('dotnet', ['run', '--no-build', '--project', '../../src/Verce.Api', '--', 'bootstrap-owner', '--email', ownerEmail, '--name', ownerName], { cwd: __dirname, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], env: childEnv })
  } catch {
    output = execFileSync('dotnet', ['run', '--no-build', '--project', '../../src/Verce.Api', '--', 'recover-owner', '--email', ownerEmail], { cwd: __dirname, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], env: childEnv })
  }
  const token = /token=([^\s]+)/.exec(output)?.[1]
  if (!token) throw new Error('E2E bootstrap did not yield a setup token')
  writeFileSync(tokenPath, token, { encoding: 'utf8', mode: 0o600 })
}
