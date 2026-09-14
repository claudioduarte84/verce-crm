'use strict'

const { releaseE2eRunLock } = require('./e2e-env.cjs')

// Releases the run lock as soon as the suite is done rather than waiting for process exit. It is
// a best-effort early release: it only ever releases a lock THIS process owns, and the config
// module's `process.once('exit')` handler — plus the holder's own stdin-EOF/parent-watchdog
// paths — guarantee release even if this never runs or runs in a different process.
module.exports = () => releaseE2eRunLock()
