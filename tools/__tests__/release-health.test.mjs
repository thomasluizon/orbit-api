import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { dirname, join, resolve } from "node:path"
import { test } from "node:test"
import { fileURLToPath } from "node:url"

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..")
const workflow = readFileSync(join(root, ".github/workflows/release.yml"), "utf8")
const healthy = JSON.parse(readFileSync(new URL("./fixtures/release-health.json", import.meta.url)))
const stale = { ...healthy, commit: "377eca519ab8d5e5f5eb41b05b448f42db88d589" }
const steps = ["Verify API health and live commit", "Verify staging health and live commit"]

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
  state.elapsed += Math.min(entry.duration ?? 0, requestLimit)
  save()
  if (entry.transport || (entry.duration >= requestLimit && !entry.deliverAtTimeout)) throw new Error("private transport details")
  return new Response(entry.raw ?? JSON.stringify(entry.body), { status: entry.http ?? 200 })
}
`)
  writeFileSync(join(directory, "node"), `#!/bin/sh\nexec '${process.execPath}' --import '${preload}' "$@"\n`, { mode: 0o755 })
  writeFileSync(join(directory, "curl"), `#!${process.execPath}
const fs = require("node:fs")
const statePath = ${JSON.stringify(statePath)}
const state = JSON.parse(fs.readFileSync(statePath))
const responses = JSON.parse(fs.readFileSync(${JSON.stringify(sequencePath)}))
const entry = responses[Math.min(state.calls.length, responses.length - 1)]
state.calls.push({})
fs.writeFileSync(statePath, JSON.stringify(state))
process.stdout.write(entry.raw ?? JSON.stringify(entry.body))
process.exitCode = entry.transport || entry.http >= 400 ? 1 : 0
`, { mode: 0o755 })
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

for (const name of steps) {
  test(`${name}: a Healthy stale instance converges on the next probe`, () => {
    const result = replay(name, [{ body: stale }, { body: healthy }])
    assert.equal(result.status, 0, result.stdout + result.stderr)
    assert.equal(result.calls.length, 2)
    assert.deepEqual(result.waits, [5000])
    assert.equal(result.elapsed, 5000)
  })

  test(`${name}: the first matching response finishes immediately`, () => {
    const result = replay(name, [{ body: healthy }])
    assert.equal(result.status, 0, result.stderr)
    assert.equal(result.calls.length, 1)
    assert.deepEqual(result.waits, [])
    assert.equal(result.elapsed, 0)
    assert.match(result.stdout, /attempts=1 elapsed=0\.000s/)
    const url = name === steps[0] ? "https://api.useorbit.org/health" : "https://api-staging.useorbit.org/health"
    assert.equal(result.calls[0].url, url)
  })

  const failures = [
    ["stale", { body: stale }],
    ["unhealthy", { body: { ...healthy, status: "Unhealthy" } }],
    ["wrong status case", { body: { ...healthy, status: "healthy" } }],
    ["malformed JSON", { raw: "private invalid JSON" }],
    ["missing status", { body: { ...healthy, status: undefined } }],
    ["missing commit", { body: { ...healthy, commit: undefined } }],
    ["non-string status", { body: { ...healthy, status: true } }],
    ["non-string commit", { body: { ...healthy, commit: [healthy.commit] } }],
    ["null body", { body: null }],
    ["array body", { body: [healthy] }],
    ["prefix commit", { body: { ...healthy, commit: healthy.commit.slice(0, 7) } }],
    ["HTTP error with matching body", { http: 503, body: healthy }],
    ["transport error", { transport: true }],
  ]

  for (const [kind, entry] of failures) {
    test(`${name}: persistent ${kind} exhausts the single deadline`, () => {
      const result = replay(name, [entry])
      assert.equal(result.status, 1, result.stdout + result.stderr)
      assert.equal(result.elapsed, 180_000)
      assert.equal(result.calls.length, 36)
      assert.equal(result.waits.length, 36)
      assert.ok(result.calls.every(call => call.timeout === Math.min(10_000, 180_000 - call.start) && call.start < 180_000))
      assert.match(result.stderr, new RegExp(`expected=${healthy.commit} status=\\S+ commit=\\S+ attempts=36 elapsed=180\\.000s`))
      assert.doesNotMatch(result.stdout + result.stderr, /private|description|background-services|Database reachable/)
    })

    test(`${name}: ${kind} can recover within the deadline`, () => {
      const result = replay(name, [entry, { body: healthy }])
      assert.equal(result.status, 0, result.stderr)
      assert.equal(result.calls.length, 2)
      assert.deepEqual(result.waits, [5000])
    })
  }

  test(`${name}: a wrong target never accepts a prefix or another commit`, () => {
    const result = replay(name, [{ body: healthy }], stale.commit)
    assert.equal(result.status, 1)
    assert.equal(result.elapsed, 180_000)
    assert.match(result.stderr, new RegExp(`expected=${stale.commit} status=Healthy commit=${healthy.commit}`))
  })

  test(`${name}: slow probes cap requests and the last wait to the remaining budget`, () => {
    const result = replay(name, [{ body: stale, duration: 9000 }])
    assert.equal(result.status, 1)
    assert.equal(result.elapsed, 180_000)
    assert.equal(result.calls.length, 13)
    assert.equal(result.calls.at(-1).timeout, 10_000)
    assert.equal(result.waits.at(-1), 3000)
  })

  test(`${name}: timeouts consume one budget and never sleep past it`, () => {
    const result = replay(name, [{ body: healthy, duration: 11_000 }])
    assert.equal(result.status, 1)
    assert.equal(result.elapsed, 180_000)
    assert.equal(result.calls.length, 12)
    assert.ok(result.calls.every(call => call.timeout === 10_000))
    assert.equal(result.waits.length, 12)
  })

  test(`${name}: the final request uses the remaining time and a late match fails`, () => {
    const result = replay(name, [{ body: stale, duration: 1000 }, { body: healthy, duration: 11_000 }])
    assert.equal(result.status, 1)
    assert.equal(result.elapsed, 180_000)
    assert.equal(result.calls.at(-1).timeout, 9000)
    assert.equal(result.waits.length, result.calls.length - 1)
  })

  for (const [duration, exitCode] of [[8999, 0], [9000, 1]]) {
    test(`${name}: a match at ${171_000 + duration}ms respects the deadline boundary`, () => {
      const sequence = [
        { body: stale, duration: 1000 },
        ...Array.from({ length: 11 }, () => ({ transport: true, duration: 11_000 })),
        { body: healthy, duration, deliverAtTimeout: true },
      ]
      const result = replay(name, sequence)
      assert.equal(result.status, exitCode, result.stdout + result.stderr)
      assert.equal(result.elapsed, 171_000 + duration)
      assert.equal(result.calls.at(-1).timeout, 9000)
      assert.equal(result.waits.length, result.calls.length - 1)
    })
  }

  test(`${name}: the last valid observation survives subsequent failures`, () => {
    const result = replay(name, [{ body: stale }, { raw: "private invalid JSON" }])
    assert.equal(result.status, 1)
    assert.match(result.stderr, new RegExp(`status=Healthy commit=${stale.commit}`))
  })

  test(`${name}: diagnostics exclude arbitrary values and descriptions`, () => {
    const result = replay(name, [{ body: { ...healthy, status: "private\n::error::injected", commit: "private".repeat(1000) } }])
    assert.equal(result.status, 1)
    assert.match(result.stderr, /status=<unknown> commit=<unknown>/)
    assert.doesNotMatch(result.stdout + result.stderr, /private|::error::|description|background-services/)
    assert.ok(result.stderr.length < 300)
  })
}

test("both jobs load the shared verifier from the workflow commit before final Render identity and recording", () => {
  for (const job of ["production", "staging"]) {
    const source = workflow.split(`\n  ${job}:\n`)[1].split(/\n  \w+:\n/)[0]
    const checkout = source.indexOf("uses: actions/checkout@v7")
    const setup = source.indexOf("uses: actions/setup-node@v7")
    const verify = source.indexOf("node tools/verify-release-health.mjs")
    const live = source.indexOf("          live_commit=")
    const record = source.indexOf("      - name: Record deployment")
    assert.ok(checkout > 0 && setup > checkout && verify > setup && live > verify && record > live)
    assert.match(source.slice(checkout, setup), /ref: \$\{\{ github.sha \}\}/)
    assert.match(source.slice(checkout, setup), /persist-credentials: false/)
    assert.match(source.slice(setup, verify), /node-version: 22/)
    if (job === "production") {
      assert.match(source.slice(live, record), /jq -er '\.\[0\]\.deploy\.commit\.id'/)
      assert.match(source.slice(live, record), /if \[ "\$live_commit" != "\$COMMIT_SHA" \]; then[\s\S]*exit 1/)
    } else {
      assert.match(source.slice(live, record), /jq -er '\.\[0\]\.deploy\.image\.ref'/)
      assert.match(source.slice(live, record), /if \[ "\$live_commit" != "ghcr.io\/thomasluizon\/orbit-api@\$IMAGE_DIGEST" \]; then[\s\S]*exit 1/)
    }
    assert.doesNotMatch(healthScript(steps[job === "production" ? 0 : 1]), /--retry/)
  }
  assert.match(workflow, /group: api-release-\$\{\{ inputs.environment \}\}[\s\S]*cancel-in-progress: false/)
})
