import assert from "node:assert/strict"
import { readFileSync } from "node:fs"
import { test } from "node:test"
import worker from "./workers/staging-pinger.mjs"

const instant = time => Date.parse(`2026-09-30T${time}Z`)

function setup(t, time = "11:00:00") {
  t.mock.timers.enable({ apis: ["Date", "setTimeout"], now: instant(time) })
  const logs = []
  const errors = []
  const requests = []
  t.mock.method(console, "log", entry => logs.push(entry))
  t.mock.method(console, "error", entry => errors.push(entry))
  t.mock.method(globalThis, "fetch", async (url, options) => {
    requests.push(new Request(url, options))
    return new Response("Healthy")
  })
  return { logs, errors, requests }
}

test("Terraform schedules every five minutes across exactly the Sao Paulo window", () => {
  const source = readFileSync(new URL("./staging-pinger.tf", import.meta.url), "utf8")
  const crons = [...source.matchAll(/cron\s*=\s*"([^"]+)"/g)].map(match => match[1])
  assert.equal(crons.length, 2)
  const minutes = []
  for (const cron of crons) {
    const fields = cron.split(" ")
    assert.deepEqual(fields.slice(2), ["*", "*", "*"])
    assert.equal(fields[0], "*/5")
    const [first, last] = fields[1].split("-").map(Number)
    for (let hour = first; hour <= last; hour++) {
      for (let minute = 0; minute < 60; minute += 5) minutes.push(hour * 60 + minute)
    }
  }
  const expected = Array.from({ length: 192 }, (_, index) => (11 * 60 + index * 5) % 1440)
  assert.deepEqual(minutes.sort((a, b) => a - b), expected.sort((a, b) => a - b))
})

for (const [time, allowed] of [
  ["00:00:00", true],
  ["02:55:00", true],
  ["02:59:59", true],
  ["03:00:00", false],
  ["10:59:59", false],
  ["11:00:00", true],
  ["23:55:00", true],
]) {
  test(`scheduled handler ${allowed ? "pings" : "skips"} at ${time} UTC`, async t => {
    const { requests, logs } = setup(t, time)
    await worker.scheduled({ scheduledTime: instant(time) })
    assert.equal(requests.length, allowed ? 1 : 0)
    assert.equal(logs[0].outcome, allowed ? "healthy" : "skipped-outside-window")
  })
}

test("delayed triggers cannot ping after midnight in Sao Paulo", async t => {
  const { requests, logs } = setup(t, "03:00:00")
  await worker.scheduled({ scheduledTime: instant("02:55:00") })
  assert.equal(requests.length, 0)
  assert.equal(logs[0].outcome, "skipped-outside-window")
})

test("a trigger scheduled outside the window cannot ping when delivered inside it", async t => {
  const { requests } = setup(t)
  await worker.scheduled({ scheduledTime: instant("10:55:00") })
  assert.equal(requests.length, 0)
})

test("pings only staging without cache or redirect and records the completed request", async t => {
  const { requests, logs, errors } = setup(t)
  t.mock.method(globalThis, "fetch", async (url, options) => {
    requests.push(new Request(url, options))
    t.mock.timers.tick(1200)
    return new Response("Healthy")
  })
  await worker.scheduled({ scheduledTime: instant("11:00:00") })
  assert.equal(requests.length, 1)
  assert.equal(requests[0].url, "https://api-staging.useorbit.org/health")
  assert.equal(requests[0].method, "GET")
  assert.equal(requests[0].cache, "no-store")
  assert.equal(requests[0].redirect, "manual")
  assert.deepEqual(logs, [{
    event: "staging-health-ping",
    scheduledAt: "2026-09-30T11:00:00.000Z",
    startedAt: "2026-09-30T11:00:00.000Z",
    outcome: "healthy",
    status: 200,
    durationMs: 1200,
  }])
  assert.deepEqual(errors, [])
  t.mock.timers.tick(90_000)
  assert.equal(requests[0].signal.aborted, false)
})

test("non-success health responses fail the invocation and retain the HTTP status", async t => {
  const { logs, errors } = setup(t)
  t.mock.method(globalThis, "fetch", async () => new Response("Unhealthy", { status: 503 }))
  await assert.rejects(worker.scheduled({ scheduledTime: instant("11:00:00") }), /HTTP 503/)
  assert.deepEqual(logs, [])
  assert.equal(errors.length, 1)
  assert.equal(errors[0].outcome, "failed")
  assert.equal(errors[0].status, 503)
})

test("a redirect response fails the invocation instead of being followed", async t => {
  const { requests, logs, errors } = setup(t)
  t.mock.method(globalThis, "fetch", async (url, options) => {
    requests.push(new Request(url, options))
    return new Response(null, { status: 301, headers: { location: "https://example.com/health" } })
  })
  await assert.rejects(worker.scheduled({ scheduledTime: instant("11:00:00") }), /HTTP 301/)
  assert.equal(requests.length, 1)
  assert.deepEqual(logs, [])
  assert.equal(errors.length, 1)
  assert.equal(errors[0].status, 301)
})

test("network errors fail the invocation and remain visible in logs", async t => {
  const { errors } = setup(t)
  const failure = new Error("Connection failed")
  t.mock.method(globalThis, "fetch", async () => { throw failure })
  await assert.rejects(worker.scheduled({ scheduledTime: instant("11:00:00") }), error => error === failure)
  assert.equal(errors[0].outcome, "failed")
  assert.match(errors[0].error, /Connection failed/)
})

test("a stalled request aborts after 90 seconds and fails the invocation", async t => {
  const { errors } = setup(t)
  let signal
  t.mock.method(globalThis, "fetch", (_url, options) => {
    signal = options.signal
    return new Promise((_resolve, reject) => {
      signal.addEventListener("abort", () => reject(signal.reason), { once: true })
    })
  })
  const run = worker.scheduled({ scheduledTime: instant("11:00:00") })
  const rejected = assert.rejects(run, { name: "AbortError" })
  t.mock.timers.tick(89_999)
  assert.equal(signal.aborted, false)
  t.mock.timers.tick(1)
  await rejected
  assert.equal(signal.aborted, true)
  assert.equal(errors[0].durationMs, 90_000)
})
