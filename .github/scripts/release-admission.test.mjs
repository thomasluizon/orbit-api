import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { requireReason, requireCI, requireSmoke, productionCommit, migrationFiles, freshExport } from './release-admission.mjs'

const sha = 'a'.repeat(40)
const bot = { login: 'github-actions[bot]' }
test('override requires a written reason', () => {
  assert.throws(() => requireReason(true, ' \n'), /override_reason/)
  requireReason(true, 'Staging unavailable; emergency repair')
  requireReason(false, '')
})
for (const conclusion of ['failure', null, 'skipped']) {
  test(`CI refuses ${conclusion}`, async () => {
    await assert.rejects(requireCI(async () => ({ workflow_runs: [{ head_sha: sha, status: 'completed', conclusion }] }), 'owner/repo', sha), /API Tests/)
  })
}
test('CI requires both successful workflows and their check runs', async () => {
  const paths = []
  await requireCI(async path => {
    paths.push(path)
    return path.includes('/check-suites/') ? { check_runs: [{ head_sha: sha, status: 'completed', conclusion: 'success' }] }
      : { workflow_runs: [{ head_sha: sha, status: 'completed', conclusion: 'success', check_suite_id: 123 }] }
  }, 'owner/repo', sha)
  assert.equal(paths.length, 4)
  assert.match(paths[2], /guards.yml/)
  await assert.rejects(requireCI(async () => ({ workflow_runs: [] }), 'owner/repo', sha), /API Tests/)
})
test('smoke requires latest trusted success, without a stale success fallback', async () => {
  for (const state of ['failure', 'pending']) {
    await assert.rejects(requireSmoke(async () => [
      { context: 'api-staging-smoke', state, creator: bot },
      { context: 'api-staging-smoke', state: 'success', creator: bot },
    ], 'owner/repo', sha, false), /api-staging-smoke/)
  }
  await assert.rejects(requireSmoke(async () => [], 'owner/repo', sha, false), /api-staging-smoke/)
  await requireSmoke(async () => [{ context: 'api-staging-smoke', state: 'success', creator: bot }], 'owner/repo', sha, false)
  await requireSmoke(() => { throw new Error('override must not call API') }, 'owner/repo', sha, true, 'Emergency')
})
test('finds production commit from the latest successful production deployment', async () => {
  const api = async path => path.includes('/statuses?')
    ? [{ state: path.includes('/2/') ? 'failure' : 'success' }]
    : [{ id: 2, environment: 'production', sha: 'b'.repeat(40), creator: bot }, { id: 1, environment: 'production', sha, creator: bot }]
  assert.equal(await productionCommit(api, 'owner/repo'), sha)
  await assert.rejects(productionCommit(async () => [], 'owner/repo'), /baseline/)
})
const files = migrationFiles('src/Orbit.Infrastructure/Migrations/new.cs\nsrc/Orbit.Application/file.cs\n')
test('migration changes require a fresh completed Render export id before deploy', async () => {
  assert.deepEqual(files, ['src/Orbit.Infrastructure/Migrations/new.cs'])
  await assert.rejects(freshExport(files, { request: async () => [] }), /RENDER_PRODUCTION_POSTGRES_ID.*new.cs/)
  await assert.rejects(freshExport(files, { postgresId: 'dpg-test', request: async () => [{ id: 'old', url: 'https://example.test/export' }], pause: async () => {} }), /No fresh completed Render export id.*new.cs/)
})
test('records a fresh export id only after completion', async () => {
  let polls = 0
  const methods = []
  const id = await freshExport(files, { postgresId: 'dpg-test', pause: async () => {}, request: async (_, method = 'GET') => {
    methods.push(method)
    if (method === 'POST') return
    polls++
    return [{ id: 'old', url: 'https://example.test/old' }, ...(polls > 1 ? [{ id: 'new', ...(polls > 2 ? { url: 'https://example.test/new' } : {}) }] : [])]
  } })
  assert.equal(id, 'new')
  assert.deepEqual(methods, ['GET', 'POST', 'GET', 'GET'])
})
test('release without migration changes needs no export', async () => {
  assert.equal(await freshExport([], { request: () => { throw new Error('must not call Render') } }), '')
})
test('production workflow gates CI, smoke and export before image build or deploy', () => {
  const source = readFileSync(process.env.RELEASE_WORKFLOW_PATH ?? new URL('../workflows/release.yml', import.meta.url), 'utf8')
  assert.match(source, /Require green release CI[\s\S]*release-admission.mjs ci[\s\S]*\n  production:/)
  const prod = source.split('\n  production:')[1].split('\n  staging:')[0]
  assert.match(prod, /release-admission.mjs smoke[\s\S]*Build and push production image/)
  assert.match(prod, /release-admission.mjs export[\s\S]*Trigger and monitor Render deploy/)
  assert.match(prod, /Run production read-only authenticated smoke[\s\S]*Record deployment/)
  assert.match(prod, /render_export_id: \$export_id/)
  assert.match(source.split('\n  staging:')[1], /Verify staging health and live commit[\s\S]*release-smoke.mjs.*--write/)
})
