#!/usr/bin/env node
// M-S2-005: the ONE reproducible pipeline from the real VERCE API host to the generated
// frontend TypeScript contract. No hand-written openapi.json, no running server left behind.
//
//   node scripts/openapi.mjs            -> regenerates src/api/generated/schema.d.ts
//   node scripts/openapi.mjs --check    -> fails if the committed file is stale
//
// Exposed as `npm run api:generate` / `npm run api:check`.
import { execFileSync } from 'node:child_process'
import { existsSync, mkdirSync, rmSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const frontendDir = dirname(dirname(fileURLToPath(import.meta.url)))
const repoRoot = resolve(frontendDir, '..')
const apiProject = resolve(repoRoot, 'src/Verce.Api/Verce.Api.csproj')
const artifactsDir = resolve(repoRoot, 'artifacts/openapi')
const openApiJsonPath = resolve(artifactsDir, 'Verce.Api.json')
const outputFile = resolve(frontendDir, 'src/api/generated/schema.d.ts')
// Invoked as `node <cli.js> ...args` directly rather than through the `npx`/`openapi-typescript`
// shim — spawning a .cmd shim via execFileSync's argv-array form throws EINVAL on Windows
// without `shell: true`, and `shell: true` with an args array is a real injection footgun
// (Node flags it as deprecated) for no benefit here: the package already ships a plain Node
// entry point, so there is nothing shell-specific left to invoke.
const openApiTsCli = resolve(frontendDir, 'node_modules/openapi-typescript/bin/cli.js')

const check = process.argv.includes('--check')

function run(command, args, options = {}) {
  execFileSync(command, args, { stdio: 'inherit', cwd: frontendDir, ...options })
}

console.log('[api] Building the real VERCE API host to extract its canonical OpenAPI document (no server left running)...')
rmSync(artifactsDir, { recursive: true, force: true })
mkdirSync(artifactsDir, { recursive: true })
// MSBuild's own incremental tracking for the GenerateOpenApiDocuments target does not know this
// script just deleted its output directory out from under it — without clearing its cache file
// too, a build with nothing else changed silently skips regenerating the document.
rmSync(resolve(dirname(apiProject), 'obj/Verce.Api.OpenApiFiles.cache'), { force: true })

try {
  execFileSync(
    'dotnet',
    ['build', apiProject, '-p:OpenApiGenerateDocumentsOnBuild=true', `-p:OpenApiDocumentsDirectory=${artifactsDir}`],
    {
      stdio: 'inherit',
      cwd: repoRoot,
      env: {
        ...process.env,
        // Development so Data Protection uses its file-system fallback instead of requiring a
        // Production certificate (mirrors VerceWebApplicationFactory's test convention); outbox
        // scheduling and seeding disabled so the introspection host starts and exits promptly
        // instead of running Quartz/hosted-service work it doesn't need for this purpose.
        ASPNETCORE_ENVIRONMENT: 'Development',
        Outbox__SchedulingEnabled: 'false',
        Settings__SeedOnStartup: 'false',
      },
    },
  )
} catch {
  console.error('[api] Failed to build the OpenAPI document from the real API host.')
  process.exit(1)
}

if (!existsSync(openApiJsonPath)) {
  console.error(`[api] Expected OpenAPI document at ${openApiJsonPath} was not produced.`)
  process.exit(1)
}

const generatorArgs = [openApiJsonPath, '-o', outputFile]
if (check) generatorArgs.push('--check')

console.log(`[api] Running openapi-typescript${check ? ' --check' : ''}...`)
try {
  run('node', [openApiTsCli, ...generatorArgs])
} catch {
  if (check) {
    console.error(
      '\n[api] api:check FAILED — the committed generated TypeScript contract is stale relative to ' +
        'the backend OpenAPI document. Run `npm run api:generate` and commit the result.',
    )
  }
  process.exit(1)
}

console.log(check ? '[api] api:check OK — generated contracts are up to date.' : `[api] Generated ${outputFile}`)
