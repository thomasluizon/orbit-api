import { test } from 'node:test'
import assert from 'node:assert/strict'
import { smoke } from '../release-smoke.mjs'

const id = '00000000-0000-0000-0000-000000000001'
const options = { email: 'smoke@example.test', code: '123456' }
function harness(statuses = {}) {
  const calls = []
  return { calls, fetcher: async (url, init) => {
    const key = `${init.method ?? 'GET'} ${url.pathname}`
    calls.push(key)
    const sequence = statuses[key]
    const status = Array.isArray(sequence) ? sequence.shift() : sequence ?? 200
    return new Response(JSON.stringify(url.pathname.endsWith('verify-code') ? { token: 'secret' } : { id }), { status })
  } }
}
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
