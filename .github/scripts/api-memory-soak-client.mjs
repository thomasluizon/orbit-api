// One round of the uptime soak: identical reads, a habit create and delete, a daily summary and two Astra turns. See infra/memory-budget.md.
import fs from 'node:fs'
const base = 'http://localhost:8080'
const email = process.env.SMOKE_TEST_EMAIL
const code = process.env.SMOKE_TEST_CODE
const state = '/state/token.json'
const today = new Date().toISOString().slice(0, 10)
const log = []
const guid = /^([0-9a-f]{8})-([0-9a-f]{4})-([0-9a-f]{4})-([0-9a-f]{4})-([0-9a-f]{12})$/i
// A habit id comes from a response, so the path gets only hex digits rebuilt from numbers and never the response text.
function habitPath(id, suffix) {
  const groups = guid.exec(String(id))
  if (!groups) return null
  const hex = groups.slice(1).map(group => Number.parseInt(group, 16).toString(16).padStart(group.length, '0'))
  return `/api/habits/${hex.join('-')}${suffix}`
}
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
const firstHabit = page.body?.items?.[0]?.id
const detail = habitPath(firstHabit, '/detail')
if (detail) await call('GET', detail, token)
const metrics = habitPath(firstHabit, '/metrics')
if (metrics) await call('GET', metrics, token)
await call('GET', '/api/gamification/profile', token)
await call('GET', `/api/habits/calendar-month?dateFrom=${today.slice(0, 8)}01&dateTo=${today}`, token)
await call('GET', `/api/habits/summary?dateFrom=${today}&dateTo=${today}&language=en`, token)
const created = await call('POST', '/api/habits', token, { title: 'Soak habit', description: 'Created and deleted by the soak', frequencyUnit: 'Day', frequencyQuantity: 1, days: ['Monday', 'Wednesday'] })
const createdPath = habitPath(created.body?.id, '')
if (createdPath) await call('DELETE', createdPath, token)
async function astraTurn(question) {
  const form = new FormData()
  form.append('message', `${question} ${Date.now()}`)
  const chat = await fetch(base + '/api/chat', { method: 'POST', headers: { authorization: `Bearer ${token}` }, body: form })
  log.push(`POST /api/chat ${chat.status} ${(await chat.text()).slice(0, 80).replace(/\s+/g, ' ')}`)
}
await astraTurn('What should I focus on today?')
await astraTurn('Which habit did I skip most this week?')
console.log(`${new Date().toISOString()} ${log.join(', ')}`)
