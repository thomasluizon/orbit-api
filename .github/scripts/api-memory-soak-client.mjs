// One round of the uptime soak: identical reads, a habit create and delete, a daily summary and two Astra turns. See infra/memory-budget.md.
import fs from 'node:fs'
const base = process.env.API_BASE_URL
const email = process.env.SMOKE_TEST_EMAIL
const code = process.env.SMOKE_TEST_CODE
const state = '/state/token.json'
const today = new Date().toISOString().slice(0, 10)
const log = []
async function call(method, path, token, body) {
  const response = await fetch(base + path, { method, headers: { 'content-type': 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}) }, body: body ? JSON.stringify(body) : undefined })
  const text = await response.text()
  log.push(`${method} ${path.split('?')[0].replace(/[0-9a-f-]{36}/g, ':id')} ${response.status}`)
  try { return { status: response.status, body: JSON.parse(text) } } catch { return { status: response.status, body: text } }
}
async function login() {
  if ((await call('POST', '/api/auth/send-code', null, { email, language: 'en' })).status === 400) {
    await new Promise(resolve => setTimeout(resolve, 61_000))
    await call('POST', '/api/auth/send-code', null, { email, language: 'en' })
  }
  const result = await call('POST', '/api/auth/verify-code', null, { email, code, language: 'en' })
  if (!result.body?.token) return null
  fs.writeFileSync(state, JSON.stringify({ token: result.body.token }))
  return result.body.token
}
let token = fs.existsSync(state) ? JSON.parse(fs.readFileSync(state, 'utf8')).token : null
if (!token || (await call('GET', '/api/profile', token)).status === 401) token = await login()
if (!token) { console.log('login failed', log.join(', ')); process.exit(0) }
await call('POST', '/api/events/ticket', token)
const page = await call('GET', `/api/habits?dateFrom=${today}&dateTo=${today}&includeOverdue=true&pageSize=100`, token)
const ids = (page.body?.items ?? []).map(habit => habit.id)
await call('GET', `/api/habits/${ids[0]}/detail`, token)
await call('GET', `/api/habits/${ids[0]}/metrics`, token)
await call('GET', '/api/gamification/profile', token)
await call('GET', `/api/habits/calendar-month?dateFrom=${today.slice(0, 8)}01&dateTo=${today}`, token)
await call('GET', `/api/habits/summary?dateFrom=${today}&dateTo=${today}&language=en`, token)
const created = await call('POST', '/api/habits', token, { title: 'Soak habit', description: 'Created and deleted by the soak', frequencyUnit: 'Day', frequencyQuantity: 1, days: ['Monday', 'Wednesday'] })
if (created.body?.id) await call('DELETE', `/api/habits/${created.body.id}`, token)
for (const question of ['What should I focus on today?', 'Which habit did I skip most this week?']) {
  const form = new FormData()
  form.append('message', `${question} ${Date.now()}`)
  const chat = await fetch(base + '/api/chat', { method: 'POST', headers: { authorization: `Bearer ${token}` }, body: form })
  log.push(`POST /api/chat ${chat.status} ${(await chat.text()).slice(0, 80).replace(/\s+/g, ' ')}`)
}
console.log(`${new Date().toISOString()} ${log.join(', ')}`)
