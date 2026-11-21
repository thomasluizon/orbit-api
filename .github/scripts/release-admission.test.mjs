import { test } from 'node:test'
import assert from 'node:assert/strict'
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
    await assert.rejects(requireCI(async () => ({ workflow_runs: [{ head_sha: sha, head_branch: 'main', event: 'push', status: 'completed', conclusion }] }), 'owner/repo', sha), /API Tests/)
  })
}
test('CI requires both successful workflows and their check runs', async () => {
  const paths = []
  await requireCI(async path => {
    paths.push(path)
    return path.includes('/check-suites/') ? { check_runs: [{ head_sha: sha, status: 'completed', conclusion: 'success' }] }
      : { workflow_runs: [{ head_sha: sha, head_branch: 'main', event: 'push', status: 'completed', conclusion: 'success', check_suite_id: 123 }] }
  }, 'owner/repo', sha)
  assert.equal(paths.length, 4)
  assert.match(paths[2], /guards.yml/)
  await assert.rejects(requireCI(async () => ({ workflow_runs: [] }), 'owner/repo', sha), /API Tests/)
})
const successfulRun = { head_sha: sha, head_branch: 'main', event: 'workflow_dispatch', status: 'completed', conclusion: 'success', check_suite_id: 123 }
function ciApi(runs) {
  return async path => {
    if (path.includes('/check-suites/')) return { check_runs: [{ head_sha: sha, status: 'completed', conclusion: 'success' }] }
    const query = new URL(path, 'https://api.github.com/').searchParams
    return { workflow_runs: runs.filter(run => !query.has('event') || run.event === query.get('event')) }
  }
}
test('CI accepts dispatched evidence at the selected main SHA', async () => {
  await requireCI(ciApi([successfulRun]), 'owner/repo', sha)
})
for (const [label, changes] of [
  ['another SHA', { head_sha: 'b'.repeat(40) }],
  ['a pull request', { event: 'pull_request' }],
  ['another branch', { head_branch: 'feature/other' }],
]) {
  test(`CI rejects dispatched evidence from ${label} with recovery instructions`, async () => {
    await assert.rejects(requireCI(ciApi([{ ...successfulRun, ...changes }]), 'owner/repo', sha), error => {
      assert.match(error.message, /API Tests.*test\.yml/)
      assert.ok(error.message.includes(sha))
      assert.match(error.message, /dispatch test\.yml and guards\.yml on main at that commit, then rerun the release/)
      return true
    })
  })
}
test('CI finds qualifying dispatch evidence among unrelated runs', async () => {
  await requireCI(ciApi([{ ...successfulRun, event: 'pull_request' }, { ...successfulRun, conclusion: 'failure' }, successfulRun]), 'owner/repo', sha)
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
