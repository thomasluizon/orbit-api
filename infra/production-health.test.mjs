import assert from "node:assert/strict"
import { test } from "node:test"
import { endpoints, pollHealth } from "./workers/production-health.mjs"

test("polls both production probes and emits separate metrics", async () => {
  const requested = []
  const metrics = []
  await pollHealth(async (url, options) => {
    requested.push(url)
    assert.equal(options.redirect, "manual")
    return new Response("healthy", { status: url.endsWith("/jobs") ? 503 : 200 })
  }, value => metrics.push(JSON.parse(value)), () => 123)
  assert.deepEqual(requested, endpoints.map(path => `https://api.useorbit.org${path}`))
  assert.deepEqual(metrics.map(row => [row.Endpoint, row.Healthy]), [["/health", 1], ["/health/jobs", 0]])
  for (const metric of metrics) {
    assert.equal(metric._aws.Timestamp, 123)
    assert.equal(metric._aws.CloudWatchMetrics[0].Namespace, "Orbit/ProductionHealth")
  }
})

test("a network failure on one endpoint still records the other", async () => {
  const metrics = []
  await pollHealth(async url => {
    if (url.endsWith("/jobs")) throw new Error("timeout")
    return new Response("healthy")
  }, value => metrics.push(JSON.parse(value)))
  assert.deepEqual(metrics.map(row => row.Healthy).sort(), [0, 1])
})

test("redirects are failures", async () => {
  const metrics = []
  await pollHealth(async () => new Response(null, { status: 302 }), value => metrics.push(JSON.parse(value)))
  assert.deepEqual(metrics.map(row => row.Healthy), [0, 0])
})
