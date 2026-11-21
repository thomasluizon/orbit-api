import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { dirname, join, resolve } from "node:path"
import { test } from "node:test"
import { fileURLToPath } from "node:url"

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..")
const workflow = readFileSync(join(root, ".github/workflows/release.yml"), "utf8")
const healthy = { status: "Healthy", commit: "49c0442128380129c5bfe7749c7baf62c4e5bcf9" }
const stale = { ...healthy, commit: "377eca519ab8d5e5f5eb41b05b448f42db88d589" }
const step = "Verify API health and live commit"

function healthScript(name) {
  const start = workflow.indexOf(`      - name: ${name}\n`)
  assert.ok(start >= 0)
  const run = workflow.indexOf("        run: |\n", start) + "        run: |\n".length
  const end = workflow.indexOf("          live_commit=", run)
  assert.ok(end > run)
  return workflow.slice(run, end).replace(/^          /gm, "")
}

function replay(name, responses, expected = healthy.commit) {
  const directory = mkdtempSync(join(tmpdir(), "orbit-release-health-"))
  const statePath = join(directory, "state.json")
  const sequencePath = join(directory, "responses.json")
  writeFileSync(sequencePath, JSON.stringify(responses))
  writeFileSync(statePath, JSON.stringify({ elapsed: 0, calls: [], waits: [] }))
  const preload = join(directory, "preload.mjs")
  writeFileSync(preload, `
import { readFileSync, writeFileSync } from "node:fs"
const statePath = ${JSON.stringify(statePath)}
const responses = JSON.parse(readFileSync(${JSON.stringify(sequencePath)}))
const state = JSON.parse(readFileSync(statePath))
const save = () => writeFileSync(statePath, JSON.stringify(state))
Object.defineProperty(globalThis, "performance", { value: { now: () => state.elapsed } })
globalThis.setTimeout = (callback, delay) => {
  state.waits.push(delay)
  state.elapsed += delay
  save()
  queueMicrotask(callback)
}
const timeout = AbortSignal.timeout
let requestLimit
AbortSignal.timeout = delay => {
  requestLimit = delay
  return timeout(delay)
}
globalThis.fetch = async url => {
  const entry = responses[Math.min(state.calls.length, responses.length - 1)]
  state.calls.push({ url, timeout: requestLimit, start: state.elapsed })
  save()
  if (entry.transport) throw new Error("private transport details")
  return new Response(entry.raw ?? JSON.stringify(entry.body), { status: entry.http ?? 200 })
}
`)
  writeFileSync(join(directory, "node"), `#!/bin/sh\nexec '${process.execPath}' --import '${preload}' "$@"\n`, { mode: 0o755 })
  try {
    const result = spawnSync("bash", ["-c", healthScript(name)], {
      cwd: root,
      encoding: "utf8",
      timeout: 5000,
      env: { ...process.env, PATH: `${directory}:${process.env.PATH}`, COMMIT_SHA: expected },
    })
    assert.ifError(result.error)
    return { ...result, ...JSON.parse(readFileSync(statePath)) }
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
}

test("a Healthy stale instance converges on the next probe", () => {
  const result = replay(step, [{ body: stale }, { body: healthy }])
  assert.equal(result.status, 0, result.stdout + result.stderr)
  assert.equal(result.calls.length, 2)
  assert.deepEqual(result.waits, [5000])
  assert.equal(result.elapsed, 5000)
})

test("the first matching response finishes immediately", () => {
  const result = replay(step, [{ body: healthy }])
  assert.equal(result.status, 0, result.stderr)
  assert.equal(result.calls.length, 1)
  assert.deepEqual(result.waits, [])
  assert.equal(result.elapsed, 0)
  assert.match(result.stdout, /attempts=1 elapsed=0\.000s/)
  assert.equal(result.calls[0].url, "https://api.useorbit.org/health")
})

for (const [kind, entry] of [
  ["stale", { body: stale }],
  ["unhealthy", { body: { ...healthy, status: "Unhealthy" } }],
  ["malformed JSON", { raw: "private invalid JSON" }],
  ["transport error", { transport: true }],
]) {
  test(`persistent ${kind} exhausts the single deadline`, () => {
    const result = replay(step, [entry])
    assert.equal(result.status, 1, result.stdout + result.stderr)
    assert.equal(result.elapsed, 180_000)
    assert.equal(result.calls.length, 36)
    assert.equal(result.waits.length, 36)
    assert.ok(result.calls.every(call => call.timeout === Math.min(10_000, 180_000 - call.start) && call.start < 180_000))
    assert.match(result.stderr, new RegExp(`expected=${healthy.commit} status=\\S+ commit=\\S+ attempts=36 elapsed=180\\.000s`))
    assert.doesNotMatch(result.stdout + result.stderr, /private|description|background-services|Database reachable/)
  })
}

test("a wrong target never accepts a prefix or another commit", () => {
  const result = replay(step, [{ body: healthy }], stale.commit)
  assert.equal(result.status, 1)
  assert.equal(result.elapsed, 180_000)
  assert.match(result.stderr, new RegExp(`expected=${stale.commit} status=Healthy commit=${healthy.commit}`))
})

test("diagnostics exclude arbitrary values and descriptions", () => {
  const result = replay(step, [{ body: { ...healthy, status: "private\n::error::injected", commit: "private".repeat(1000) } }])
  assert.equal(result.status, 1)
  assert.match(result.stderr, /status=<unknown> commit=<unknown>/)
  assert.doesNotMatch(result.stdout + result.stderr, /private|::error::|description|background-services/)
  assert.ok(result.stderr.length < 300)
})
