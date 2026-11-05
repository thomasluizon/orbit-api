import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { checkPlan as checkWebPlan } from './check-web-plan.mjs'

const guarded = new Set(['render_web_service.production_api', 'render_web_service.production_web', 'render_env_group.production_api'])
/**
 * Release workflows deploy image digests outside Terraform, so Render reports them as refresh drift.
 * Paths come from the render-oss/render 1.9.1 schema, where `runtime_source.image` is single-nested.
 */
const releaseOwned = new Set(['runtime_source.image.digest', 'runtime_source.image.tag'])
const isObject = value => value !== null && typeof value === 'object' && !Array.isArray(value)
function changedPaths(before, after, path = []) {
  if (!isObject(before) || !isObject(after))
    return JSON.stringify(before) === JSON.stringify(after) ? [] : [path.join('.')]
  return [...new Set([...Object.keys(before), ...Object.keys(after)])]
    .flatMap(key => changedPaths(before[key], after[key], [...path, key]))
}
function isReleaseImageDrift(resource) {
  if (!resource.address.startsWith('render_web_service.') || JSON.stringify(resource.change?.actions) !== '["update"]') return false
  const paths = changedPaths(resource.change.before, resource.change.after)
  return paths.length > 0 && paths.every(path => releaseOwned.has(path))
}
function checkValues(address, change) {
  if (address.startsWith('render_web_service.')) {
    return change?.before?.max_shutdown_delay_seconds === 300 && change?.after?.max_shutdown_delay_seconds === 300
      ? [] : [`Shutdown delay must remain 300 seconds on ${address}`]
  }
  return ['before', 'after']
    .filter(side => change?.[side]?.env_vars?.Ses__FromEmail?.value !== 'Orbit <contact@useorbit.org>')
    .map(side => `Production Ses__FromEmail differs from the reviewed sender (${side})`)
}
export function checkProductionPlan(plan) {
  const issues = checkWebPlan(plan)
  for (const address of guarded) {
    const resource = plan.resource_changes.find(item => item.address === address)
    if (!resource) { issues.push(`Missing guarded resource ${address}`); continue }
    const change = resource.change
    if (JSON.stringify(change?.actions) !== '["no-op"]') issues.push(`Unexpected production change to ${address}`)
    issues.push(...checkValues(address, change))
  }
  for (const resource of plan.resource_drift ?? []) {
    if (guarded.has(resource.address) && JSON.stringify(resource.change?.actions) !== '["no-op"]' && !isReleaseImageDrift(resource))
      issues.push(`Production drift detected on ${resource.address}`)
  }
  return issues
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const issues = checkProductionPlan(JSON.parse(readFileSync(0, 'utf8')))
    if (issues.length) { console.error(issues.join('\n')); process.exitCode = 1 }
    else console.log('Production plan guard passed')
  } catch {
    console.error('Production plan guard could not inspect plan JSON')
    process.exitCode = 2
  }
}
