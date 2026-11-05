// Drives the recorded HTTP load against the API container. See infra/memory-budget.md.
const base = process.env.API_BASE_URL
const email = process.env.SMOKE_TEST_EMAIL
const code = process.env.SMOKE_TEST_CODE
const rounds = Number(process.env.ROUNDS ?? 30)
const concurrency = Number(process.env.CONCURRENCY ?? 8)
const today = new Date().toISOString().slice(0, 10)
const statuses = {}

async function call(method, path, token, body) {
  const response = await fetch(base + path, {
    method,
    headers: { 'content-type': 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  })
  const key = `${method} ${path.split('?')[0].replace(/[0-9a-f-]{36}/g, ':id')} ${response.status}`
  statuses[key] = (statuses[key] ?? 0) + 1
  const text = await response.text()
  try {
    return JSON.parse(text)
  } catch {
    return text
  }
}

await call('POST', '/api/auth/send-code', null, { email, language: 'en' })
const login = await call('POST', '/api/auth/verify-code', null, { email, code, language: 'en' })
if (!login?.token) throw new Error(`Smoke login failed: ${JSON.stringify(login).slice(0, 300)}`)
const token = login.token
await call('PUT', '/api/profile/timezone', token, { timeZone: 'America/Sao_Paulo' })
for (let batch = 0; batch < 4; batch++)
  await call('POST', '/api/habits/bulk', token, {
    habits: Array.from({ length: 25 }, (_, index) => ({
      title: `Load habit ${batch * 25 + index}`,
      description: 'x'.repeat(1000),
      frequencyUnit: 'Day',
      frequencyQuantity: 1,
    })),
  })

const streams = []
for (let stream = 0; stream < 3; stream++) {
  const ticket = await call('POST', '/api/events/ticket', token)
  const controller = new AbortController()
  streams.push(controller)
  fetch(`${base}/api/events?ticket=${encodeURIComponent(ticket.ticket)}`, { signal: controller.signal })
    .then(async response => {
      statuses[`GET /api/events ${response.status}`] = (statuses[`GET /api/events ${response.status}`] ?? 0) + 1
      for await (const _ of response.body);
    })
    .catch(error => {
      if (error.name !== 'AbortError') throw error
    })
}

async function account() {
  const page = await call('GET', `/api/habits?dateFrom=${today}&dateTo=${today}&includeOverdue=true&pageSize=100`, token)
  const ids = (page?.items ?? []).map(habit => habit.id).slice(0, 20)
  await call('GET', `/api/habits/calendar-month?dateFrom=${today.slice(0, 8)}01&dateTo=${today}`, token)
  await call('GET', '/api/profile', token)
  await call('POST', '/api/habits/bulk/log', token, { items: ids.slice(0, 10).map(habitId => ({ habitId, date: today })) })
  await call('POST', '/api/habits/bulk/skip', token, { items: ids.slice(10, 20).map(habitId => ({ habitId, date: today })) })
  await call('GET', `/api/habits/${ids[0]}/detail`, token)
  await call('GET', `/api/habits/${ids[0]}/metrics`, token)
  await call('GET', '/api/gamification/profile', token)
}

for (let round = 0; round < rounds; round++) {
  await Promise.all(Array.from({ length: concurrency }, account))
  if (round % 10 === 0) await call('GET', '/api/profile/export', token)
}
streams.forEach(controller => controller.abort())
console.log(JSON.stringify(statuses))
const failures = Object.keys(statuses).filter(key => / 5\d\d$/.test(key))
if (failures.length > 0) throw new Error(`The API failed requests: ${failures.join(', ')}`)
