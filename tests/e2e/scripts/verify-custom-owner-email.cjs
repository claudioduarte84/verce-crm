// Regression for the Operator-setup Owner-email harness defect (§9/§10 of the M-S2-002
// correction): VERCE_E2E_OWNER_EMAIL used to be honored by Owner bootstrap but ignored by
// Operator setup, which searched for the literal 'E2E-OWNER@EXAMPLE.TEST' instead. This proves,
// end to end and on a disposable database of its own, that a NON-default Owner email now flows
// through both: Owner setup succeeds, Operator setup locates that same Owner, and Operator
// authentication succeeds.
'use strict'

const { execFileSync } = require('node:child_process')
const { dropE2eDatabaseIfExists } = require('../e2e-env.cjs')

const CUSTOM_OWNER_EMAIL = 'custom-owner@example.test'
const CUSTOM_DATABASE = 'verce_e2e_custom_owner_check'

function dropDatabaseIfExists() {
  dropE2eDatabaseIfExists(CUSTOM_DATABASE)
}

function main() {
  dropDatabaseIfExists()
  try {
    execFileSync('npx playwright test --project=setup --project=operator-setup', {
      cwd: __dirname + '/..',
      stdio: 'inherit',
      env: { ...process.env, VERCE_E2E_OWNER_EMAIL: CUSTOM_OWNER_EMAIL, VERCE_E2E_DATABASE: CUSTOM_DATABASE },
      shell: true,
    })

    const ownerExists = execFileSync(
      'docker',
      ['exec', 'verce-postgres', 'psql', '-U', 'verce', '-d', CUSTOM_DATABASE, '-tAc', `SELECT 1 FROM platform."user" WHERE normalized_email = '${CUSTOM_OWNER_EMAIL.toUpperCase()}'`],
      { encoding: 'utf8' },
    ).trim()
    if (ownerExists !== '1') throw new Error(`Owner bootstrap did not create ${CUSTOM_OWNER_EMAIL} in ${CUSTOM_DATABASE}`)

    const operatorLinkedToCustomOwner = execFileSync(
      'docker',
      [
        'exec', 'verce-postgres', 'psql', '-U', 'verce', '-d', CUSTOM_DATABASE, '-tAc',
        `SELECT 1 FROM platform."user" op
         JOIN platform.user_role ur ON ur.user_id = op.id
         JOIN platform.role r ON r.id = ur.role_id AND r.name = 'Operator'
         WHERE op.normalized_email = 'E2E-OPERATOR@EXAMPLE.TEST'
           AND op.password_hash = (SELECT password_hash FROM platform."user" WHERE normalized_email = '${CUSTOM_OWNER_EMAIL.toUpperCase()}')`,
      ],
      { encoding: 'utf8' },
    ).trim()
    if (operatorLinkedToCustomOwner !== '1') {
      throw new Error('Operator setup did not locate/provision against the custom Owner email — it used a stale hard-coded lookup')
    }

    console.log(`PASS: Owner bootstrap + Operator setup both honored VERCE_E2E_OWNER_EMAIL=${CUSTOM_OWNER_EMAIL}`)
  } finally {
    dropDatabaseIfExists()
  }
}

main()
