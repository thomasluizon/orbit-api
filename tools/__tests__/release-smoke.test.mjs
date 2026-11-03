import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { smoke } from '../release-smoke.mjs'

const id = '00000000-0000-0000-0000-000000000001'
const options = { email: 'smoke@example.test', code: '123456' }
function harness(statuses = {}) {
  const calls = []
  const requests = []
  return { calls, requests, fetcher: async (url, init) => {
    const key = `${init.method ?? 'GET'} ${url.pathname}`
    calls.push(key)
    requests.push({ path: url.pathname, ...init })
    const sequence = statuses[key]
    const status = Array.isArray(sequence) ? sequence.shift() : sequence ?? 200
    return new Response(JSON.stringify(url.pathname.endsWith('verify-code') ? { token: 'secret' } : { id }), { status })
  } }
}
test('both sign-in requests carry the smoke bypass, without leaking it to authenticated calls', async () => {
  const h = harness()
  await smoke('https://api-staging.useorbit.org', { ...options, ...h, smokeBotBypass: 'test-bypass', write: true })
  for (const request of h.requests) {
    assert.equal(new Headers(request.headers).get('X-Orbit-Smoke-Bypass'),
      request.path.startsWith('/api/auth/') ? 'test-bypass' : null)
    if (request.body) assert.equal('turnstileToken' in JSON.parse(request.body), false)
  }
})
for (const smokeBotBypass of [undefined, '']) {
  test(`an ${smokeBotBypass === undefined ? 'unset' : 'empty'} bypass leaves the disabled-enforcement smoke working`, async () => {
    const h = harness()
    await smoke('https://api.useorbit.org', { ...options, ...h, smokeBotBypass })
    assert.equal(h.calls.length, 3)
    for (const request of h.requests) assert.equal(new Headers(request.headers).has('X-Orbit-Smoke-Bypass'), false)
  })
}
test('release workflow supplies the environment-specific bypass to both smoke steps', () => {
  const workflow = readFileSync(new URL('../../.github/workflows/release.yml', import.meta.url), 'utf8')
  for (const environment of ['staging', 'production']) {
    const step = workflow.split(`- name: Run ${environment} `)[1].split('\n      - name:')[0]
    assert.match(step, /SMOKE_BOT_BYPASS: \$\{\{ secrets.SMOKE_BOT_BYPASS \}\}/)
  }
  const script = readFileSync(new URL('../release-smoke.mjs', import.meta.url), 'utf8')
  assert.match(script, /smokeBotBypass: process.env.SMOKE_BOT_BYPASS/)
  assert.doesNotMatch(script, /SMOKE_TURNSTILE_TOKEN|turnstileToken/)
})
for (const [path, status] of [['POST /api/auth/verify-code', 401], ['GET /api/habits/count', 500], ['POST /api/habits', 400], [`DELETE /api/habits/${id}`, 500]]) {
  test(`smoke refuses ${path} HTTP ${status}`, async () => {
    const h = harness({ [path]: status })
    await assert.rejects(smoke('https://api-staging.useorbit.org', { ...options, ...h, write: true }), /HTTP/)
  })
}
test('wake retries then signs in, reads, creates and deletes', async () => {
  const h = harness({ 'POST /api/auth/send-code': [429, 200] })
  const waits = []
  await smoke('https://api-staging.useorbit.org', { ...options, ...h, write: true, pause: async ms => waits.push(ms) })
  assert.deepEqual(waits, [5000])
  assert.deepEqual(h.calls, ['POST /api/auth/send-code', 'POST /api/auth/send-code', 'POST /api/auth/verify-code', 'GET /api/habits/count', 'POST /api/habits', `DELETE /api/habits/${id}`])
})
test('wake retries are bounded', async () => {
  const h = harness({ 'POST /api/auth/send-code': 429 })
  await assert.rejects(smoke('https://api-staging.useorbit.org', { ...options, ...h, pause: async () => {} }), /HTTP 429/)
  assert.equal(h.calls.length, 13)
})
test('production smoke only authenticates and reads', async () => {
  const h = harness()
  await smoke('https://api.useorbit.org', { ...options, ...h })
  assert.deepEqual(h.calls, ['POST /api/auth/send-code', 'POST /api/auth/verify-code', 'GET /api/habits/count'])
  await assert.rejects(smoke('https://api.useorbit.org', { ...options, ...h, write: true }), /staging/)
})
test('malformed login JSON cannot expose response text in diagnostics', async () => {
  const h = harness()
  const fetcher = (url, init) => url.pathname.endsWith('verify-code')
    ? Promise.resolve(new Response('private-token-response')) : h.fetcher(url, init)
  await assert.rejects(smoke('https://api.useorbit.org', { ...options, fetcher }), error => {
    assert.equal(error.message, 'Smoke sign-in returned invalid JSON')
    return true
  })
})
