import { randomUUID } from 'node:crypto'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

export async function smoke(origin, { email, code, smokeBotBypass, write = false,
  fetcher = fetch, pause = ms => new Promise(done => setTimeout(done, ms)) } = {}) {
  if (!email || !/^\d{6}$/.test(code ?? '')) throw new Error('Smoke requires SMOKE_TEST_EMAIL and a six-digit SMOKE_TEST_CODE')
  if (write && origin !== 'https://api-staging.useorbit.org') throw new Error('Smoke writes require the staging API origin')
  const request = async (path, options = {}, wake = false) => {
    for (let attempt = 0; ; attempt++) {
      const response = await fetcher(new URL(path, origin), {
        ...options, redirect: 'error', signal: AbortSignal.timeout(15_000),
      })
      if (wake && [429, 502, 503].includes(response.status) && attempt < 12) {
        await response.body?.cancel()
        await pause(5000)
        continue
      }
      if (!response.ok) throw new Error(`Smoke ${options.method ?? 'GET'} ${path} returned HTTP ${response.status}`)
      return response
    }
  }
  const json = body => ({ method: 'POST', headers: { 'Content-Type': 'application/json',
    ...(smokeBotBypass ? { 'X-Orbit-Smoke-Bypass': smokeBotBypass } : {}),
  }, body: JSON.stringify(body) })
  await request('/api/auth/send-code', json({ email }), true)
  const loginResponse = await request('/api/auth/verify-code', json({ email, code }))
  let login
  try { login = await loginResponse.json() } catch { throw new Error('Smoke sign-in returned invalid JSON') }
  if (typeof login.token !== 'string' || !login.token) throw new Error('Smoke sign-in returned no token')
  const headers = { Authorization: `Bearer ${login.token}`, 'Content-Type': 'application/json' }
  await request('/api/habits/count', { headers })
  if (write) {
    const createResponse = await request('/api/habits', {
      method: 'POST', headers, body: JSON.stringify({ title: `Release smoke ${randomUUID()}`, isGeneral: true }),
    })
    let created
    try { created = await createResponse.json() } catch { throw new Error('Smoke create returned invalid JSON') }
    if (typeof created.id !== 'string' || !/^[0-9a-f-]{36}$/i.test(created.id)) throw new Error('Smoke create returned no habit id')
    await request(`/api/habits/${created.id}`, { method: 'DELETE', headers })
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    if (process.argv.length < 3 || process.argv.length > 4 || (process.argv[3] && process.argv[3] !== '--write')) throw new Error('Usage: node tools/release-smoke.mjs <origin> [--write]')
    await smoke(process.argv[2], { email: process.env.SMOKE_TEST_EMAIL, code: process.env.SMOKE_TEST_CODE,
      smokeBotBypass: process.env.SMOKE_BOT_BYPASS, write: process.argv[3] === '--write' })
    console.log('Authenticated release smoke passed')
  } catch (error) {
    console.error(error.message)
    process.exitCode = 1
  }
}
