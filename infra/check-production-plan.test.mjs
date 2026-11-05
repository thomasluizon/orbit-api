import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { checkProductionPlan } from './check-production-plan.mjs'
const generated = JSON.parse(readFileSync(new URL('./fixtures/production-plan.json', import.meta.url), 'utf8'))
const clean = () => {
  const plan = structuredClone(generated)
  for (const resource of plan.resource_changes) {
    resource.change.actions = ['no-op']
    resource.change.before = structuredClone(resource.change.after)
  }
  return plan
}
test('accepts unchanged production resources with reviewed sender and shutdown values', () => {
  assert.deepEqual(checkProductionPlan(clean()), [])
})
for (const address of generated.resource_changes.map(item => item.address)) {
  test(`rejects a seeded update replacement and drift on ${address}`, () => {
    for (const actions of [['update'], ['delete', 'create'], ['delete'], ['create']]) {
      const plan = clean()
      plan.resource_changes.find(item => item.address === address).change.actions = actions
      assert.ok(checkProductionPlan(plan).some(issue => issue.includes(address)))
    }
    const drift = clean()
    drift.resource_drift = [structuredClone(drift.resource_changes.find(item => item.address === address))]
    drift.resource_drift[0].change.actions = ['update']
    assert.ok(checkProductionPlan(drift).some(issue => issue.includes('drift')))
  })
}
/**
 * Release-owned drift in the render-oss/render 1.9.1 schema (`terraform providers schema -json`):
 * `runtime_source` and `runtime_source.image` are single-nested attributes, so `terraform show -json`
 * renders `runtime_source.image.digest` and `runtime_source.image.tag` as nested object keys.
 */
const imageSource = (digest, tag) => ({
  docker: null,
  image: { digest, image_url: 'ghcr.io/thomasluizon/orbit-api', registry_credential_id: null, tag },
  native_runtime: null,
})
const imageDrift = (address, mutate = () => {}) => {
  const plan = clean()
  const resource = structuredClone(plan.resource_changes.find(item => item.address === address))
  const base = { ...resource.change.after, plan: 'starter', num_instances: 1, env_vars: { A: { value: 'one' } }, custom_domains: [{ name: 'api.useorbit.org' }] }
  resource.change = {
    actions: ['update'],
    before: { ...structuredClone(base), runtime_source: imageSource('sha256:aaa', 'old') },
    after: { ...structuredClone(base), runtime_source: imageSource('sha256:bbb', 'new') },
  }
  mutate(resource.change)
  plan.resource_drift = [resource]
  return plan
}
for (const address of ['render_web_service.production_api', 'render_web_service.production_web']) {
  test(`accepts release-owned image digest and tag drift on ${address}`, () => {
    assert.deepEqual(checkProductionPlan(imageDrift(address)), [])
    assert.deepEqual(checkProductionPlan(imageDrift(address, change => { change.after.runtime_source.image.tag = 'old' })), [])
  })
  test(`rejects drift beyond the release-owned image attributes on ${address}`, () => {
    for (const [kind, mutate] of [
      ['env vars', change => { change.after.env_vars.A.value = 'two' }],
      ['an added env var', change => { change.after.env_vars.B = { value: 'two' } }],
      ['plan', change => { change.after.plan = 'standard' }],
      ['instance count', change => { change.after.num_instances = 2 }],
      ['shutdown delay', change => { change.after.max_shutdown_delay_seconds = 30 }],
      ['domains', change => { change.after.custom_domains = [{ name: 'other.example' }] }],
      ['image url', change => { change.after.runtime_source.image.image_url = 'ghcr.io/other/image' }],
      ['registry credential', change => { change.after.runtime_source.image.registry_credential_id = 'rgc-1' }],
      ['runtime kind', change => { change.after.runtime_source = { docker: {}, image: null, native_runtime: null } }],
      ['a new attribute', change => { change.after.start_command = 'run' }],
      ['a replacement', change => { change.actions = ['delete', 'create'] }],
      ['a deletion', change => { change.actions = ['delete'] }],
      ['an update with no attribute difference', change => { change.after = structuredClone(change.before) }],
    ]) {
      assert.ok(checkProductionPlan(imageDrift(address, mutate)).some(issue => issue.includes(`drift detected on ${address}`)), kind)
    }
  })
}
test('rejects image-only drift on the guarded env group', () => {
  const plan = imageDrift('render_web_service.production_api')
  plan.resource_drift[0].address = 'render_env_group.production_api'
  assert.ok(checkProductionPlan(plan).some(issue => issue.includes('drift detected on render_env_group.production_api')))
})
test('rejects missing resources and changed shutdown or sender even on a no-op plan', () => {
  const missing = clean()
  missing.resource_changes.pop()
  assert.ok(checkProductionPlan(missing).some(issue => issue.includes('Missing')))
  const plan = clean()
  plan.resource_changes.find(item => item.address === 'render_web_service.production_api').change.after.max_shutdown_delay_seconds = 30
  plan.resource_changes.find(item => item.address === 'render_env_group.production_api').change.after.env_vars.Ses__FromEmail.value = 'Changed'
  const issues = checkProductionPlan(plan)
  assert.ok(issues.some(issue => issue.includes('Shutdown')))
  assert.ok(issues.some(issue => issue.includes('Ses__FromEmail')))
  assert.throws(() => checkProductionPlan({}), /resource_changes/)
})
test('CLI accepts clean plans, rejects guarded changes and fails closed on malformed input', () => {
  const script = fileURLToPath(new URL('./check-production-plan.mjs', import.meta.url))
  for (const [input, status, message] of [
    [JSON.stringify(clean()), 0, 'Production plan guard passed'],
    [JSON.stringify(generated), 1, 'Unexpected production change'],
    ['not JSON', 2, 'could not inspect plan JSON'],
  ]) {
    const result = spawnSync(process.execPath, [script], { input, encoding: 'utf8', timeout: 10_000 })
    assert.equal(result.status, status, result.stderr)
    assert.ok((result.stdout + result.stderr).includes(message))
    assert.ok(!(result.stdout + result.stderr).includes('contact@useorbit.org'), 'Diagnostics must not disclose plan values')
  }
})
