import { execFileSync } from 'node:child_process'
import { appendFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

export function requireReason(force, reason) {
  if (force && !reason?.trim()) throw new Error('force_unstaged requires a non-empty override_reason')
}
export async function requireCI(api, repo, sha) {
  for (const workflow of ['test.yml', 'guards.yml']) {
    const { workflow_runs: runs } = await api(`repos/${repo}/actions/workflows/${workflow}/runs?head_sha=${sha}&branch=main&event=push&per_page=1`)
    const run = runs?.[0]
    if (run?.head_sha !== sha || run.status !== 'completed' || run.conclusion !== 'success') throw new Error(`Release requires successful ${workflow === 'test.yml' ? 'API Tests' : 'Guards'} on ${sha}`)
    const checks = await api(`repos/${repo}/check-suites/${run.check_suite_id}/check-runs?per_page=100`)
    if (!checks.check_runs?.some(check => check.head_sha === sha && check.status === 'completed' && check.conclusion === 'success')) throw new Error(`No successful check runs for ${workflow} on ${sha}`)
  }
}
export async function requireSmoke(api, repo, sha, force, reason) {
  requireReason(force, reason)
  if (force) return
  for (let page = 1; ; page++) {
    const statuses = await api(`repos/${repo}/commits/${sha}/statuses?per_page=100&page=${page}`)
    const status = statuses.find(item => item.context === 'api-staging-smoke' && item.creator?.login === 'github-actions[bot]')
    if (status) {
      if (status.state !== 'success') throw new Error('Selected SHA has no successful api-staging-smoke status')
      return
    }
    if (statuses.length < 100) throw new Error('Selected SHA has no successful api-staging-smoke status')
  }
}
export async function productionCommit(api, repo) {
  for (let page = 1; ; page++) {
    const deployments = await api(`repos/${repo}/deployments?environment=production&per_page=100&page=${page}`)
    for (const deployment of deployments) {
      if (deployment.environment !== 'production' || deployment.creator?.login !== 'github-actions[bot]') continue
      const statuses = await api(`repos/${repo}/deployments/${deployment.id}/statuses?per_page=1`)
      if (statuses[0]?.state === 'success' && /^[a-f0-9]{40}$/.test(deployment.sha)) return deployment.sha
    }
    if (deployments.length < 100) throw new Error('No successful production deployment establishes the migration baseline')
  }
}
export function migrationFiles(changed) {
  return changed.split('\n').filter(path => path.startsWith('src/Orbit.Infrastructure/Migrations/') && path.endsWith('.cs'))
}
export async function freshExport(files, { request, postgresId, pause = ms => new Promise(done => setTimeout(done, ms)) }) {
  if (!files.length) return ''
  if (!postgresId) throw new Error(`RENDER_PRODUCTION_POSTGRES_ID is required for migration files: ${files.join(', ')}`)
  const endpoint = `https://api.render.com/v1/postgres/${encodeURIComponent(postgresId)}/export`
  const previous = new Set((await request(endpoint)).map(item => item.id))
  await request(endpoint, 'POST')
  for (let attempt = 0; attempt < 80; attempt++) {
    const exports = await request(endpoint)
    const completed = exports.find(item => !previous.has(item.id) && typeof item.id === 'string' && item.id.length > 0 && /^https:\/\//.test(item.url ?? ''))
    if (completed) return completed.id
    await pause(15_000)
  }
  throw new Error(`No fresh completed Render export id for migration files: ${files.join(', ')}`)
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const { GITHUB_REPOSITORY: repo, COMMIT_SHA: sha, FORCE_UNSTAGED: force, OVERRIDE_REASON: reason } = process.env
    if (!/^[a-f0-9]{40}$/.test(sha ?? '')) throw new Error('Release admission requires COMMIT_SHA')
    const api = path => JSON.parse(execFileSync('/usr/bin/gh', ['api', path], { encoding: 'utf8' }))
    const summary = text => { if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${text}\n`) }
    switch (process.argv[2]) {
      case 'ci': requireReason(force === 'true', reason); await requireCI(api, repo, sha); break
      case 'smoke':
        await requireSmoke(api, repo, sha, force === 'true', reason)
        if (force === 'true') summary(`Staging override reason: ${reason}`)
        break
      case 'export': {
        const baseline = await productionCommit(api, repo)
        const files = migrationFiles(execFileSync('/usr/bin/git', ['diff', '--name-only', baseline, sha, '--', 'src/Orbit.Infrastructure/Migrations/'], { encoding: 'utf8' }))
        const request = async (url, method = 'GET') => {
          const response = await fetch(url, { method, headers: { Authorization: `Bearer ${process.env.RENDER_API_KEY}` }, signal: AbortSignal.timeout(30_000), redirect: 'error' })
          if (!response.ok) throw new Error(`Render export ${method} returned HTTP ${response.status}`)
          if (method === 'POST') {
            if (response.status !== 202) throw new Error('Render did not accept export creation')
            await response.body?.cancel()
            return
          }
          return response.json()
        }
        const id = await freshExport(files, { request, postgresId: process.env.RENDER_PRODUCTION_POSTGRES_ID })
        if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `render_export_id=${id}\n`)
        summary(`Production migration baseline: ${baseline}\nMigration files: ${files.join(', ') || 'none'}\nRender export id: ${id || 'not required'}`)
        break
      }
      default: throw new Error('Usage: release-admission.mjs ci|smoke|export')
    }
    console.log('Release admission passed')
  } catch (error) {
    console.error(error.message)
    process.exitCode = 1
  }
}
