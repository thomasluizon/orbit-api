import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
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
