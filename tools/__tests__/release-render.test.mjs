import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { join, resolve } from "node:path"
import { test } from "node:test"

const root = resolve(import.meta.dirname, "../..")
const workflow = readFileSync(join(root, ".github/workflows/release.yml"), "utf8")
const fixture = JSON.parse(readFileSync(new URL("./fixtures/render-api.json", import.meta.url)))
const sha = fixture.deploy.commit.id
const id = fixture.deploy.id
const transient = { exit: 56, http: "000", body: "" }
const response = (body, http = 200) => ({ body, http })
const created = response(fixture.deploy, 201)
const live = response(fixture.deploy)
const recent = (overrides = {}) => [{
  ...fixture.recent[0],
  deploy: { ...fixture.deploy, createdAt: new Date(Date.now() + 60_000).toISOString(), ...overrides },
}]

function script(job, name) {
  const source = workflow.split(`\n  ${job}:\n`)[1].split(/\n  \w+:\n/)[0]
  const step = source.split(`      - name: ${name}\n`)[1]?.split(/\n      - /)[0]
  assert.ok(step, `Missing ${job}: ${name}`)
  return step.split("        run: |\n")[1].replace(/^          /gm, "")
}

function replay(t, job, sequences, name = "Trigger and monitor Render deploy") {
  const directory = mkdtempSync(join(tmpdir(), "orbit-render-retry-"))
  t.after(() => rmSync(directory, { recursive: true, force: true }))
  const statePath = join(directory, "state.json")
  const outputPath = join(directory, "output")
  writeFileSync(outputPath, "")
  writeFileSync(statePath, JSON.stringify({ sequences, counts: {}, calls: [], waits: [] }))
  writeFileSync(join(directory, "curl"), `#!${process.execPath}
const fs = require("node:fs")
const statePath = ${JSON.stringify(statePath)}
const state = JSON.parse(fs.readFileSync(statePath))
const args = process.argv.slice(2)
const option = flag => args[args.indexOf(flag) + 1]
const method = args.includes("--request") ? option("--request") : "GET"
const url = args.find(arg => arg.startsWith("https://api.render.com/"))
const route = method === "POST" ? "post" : method === "PATCH" ? "patch"
  : args.includes("status=live") ? "live" : url?.endsWith("/deploys") ? "list" : "poll"
if (!url || !state.sequences[route]) {
  console.error("Unexpected request", args)
  process.exit(99)
}
let entry
const retries = args.includes("--retry") ? Number(option("--retry")) : 0
for (let attempt = 0; attempt <= retries; attempt++) {
  const index = state.counts[route] ?? 0
  state.counts[route] = index + 1
  state.calls.push({ route, args })
  entry = state.sequences[route][index]
  if (!entry) {
    fs.writeFileSync(statePath, JSON.stringify(state))
    console.error("Response sequence exhausted", route)
    process.exit(99)
  }
  const http = Number(entry.http ?? 200)
  const failsHttp = args.includes("--fail") || args.includes("--fail-with-body")
  const retry = [408, 429, 500, 502, 503, 504].includes(http)
    || (args.includes("--retry-all-errors") && (entry.exit || (failsHttp && http >= 400)))
  if (!retry) break
}
fs.writeFileSync(statePath, JSON.stringify(state))
const body = typeof entry.body === "string" ? entry.body : JSON.stringify(entry.body)
if (args.includes("--output")) fs.writeFileSync(option("--output"), body)
else process.stdout.write(body)
if (args.includes("--write-out")) process.stdout.write(String(entry.http ?? 200))
process.exitCode = entry.exit ?? ((args.includes("--fail") || args.includes("--fail-with-body")) && Number(entry.http) >= 400 ? 22 : 0)
`, { mode: 0o755 })
  writeFileSync(join(directory, "sleep"), `#!${process.execPath}
const fs = require("node:fs")
const statePath = ${JSON.stringify(statePath)}
const state = JSON.parse(fs.readFileSync(statePath))
state.waits.push(process.argv[2])
fs.writeFileSync(statePath, JSON.stringify(state))
`, { mode: 0o755 })
  writeFileSync(join(directory, "node"), "#!/bin/sh\nexit 0\n", { mode: 0o755 })
  const result = spawnSync("bash", ["-c", script(job, name)], {
    cwd: root, encoding: "utf8", timeout: 10_000,
    env: {
      ...process.env, PATH: `${directory}:${process.env.PATH}`, RENDER_API_KEY: "test-key",
      RENDER_SERVICE_ID: fixture.service.id, COMMIT_SHA: sha, RELEASE_BRANCH: "main",
      GITHUB_OUTPUT: outputPath,
    },
  })
  assert.ifError(result.error)
  return { ...result, ...JSON.parse(readFileSync(statePath)), output: readFileSync(outputPath, "utf8") }
}

function followed(result, posts = 1) {
  assert.equal(result.status, 0, result.stdout + result.stderr)
  assert.equal(result.counts.post, posts)
  assert.ok(result.counts.poll >= 1)
  assert.match(result.stdout, /Render deploy status: live/)
  assert.match(result.output, new RegExp(`log_url=https://dashboard.render.com/web/.+/deploys/${id}`))
  for (const call of result.calls.filter(call => call.route === "post")) {
    assert.ok(!call.args.includes("--retry"), "POST must never use curl automatic retries")
    assert.deepEqual(JSON.parse(call.args[call.args.indexOf("--data") + 1]), { commitId: sha })
  }
}

for (const job of ["production", "staging"]) {
  test(`${job}: the first status poll can lose its connection and still follow the deploy to live`, t => {
    const result = replay(t, job, { post: [created], poll: [transient, response({ ...fixture.deploy, status: "build_in_progress" }), live] })
    followed(result)
    assert.equal(result.counts.poll, 3)
  })

  test(`${job}: an accepted POST with a lost response is reconciled without a second POST`, t => {
    const result = replay(t, job, { post: [transient], list: [response(recent())], poll: [live] })
    followed(result)
    assert.equal(result.counts.list, 1)
  })

  test(`${job}: a lost POST is resent only after the recent deploys exclude it`, t => {
    const result = replay(t, job, {
      post: [transient, created],
      list: [response([...fixture.recent, ...recent({ commit: fixture.recent[1].deploy.commit })])],
      poll: [live],
    })
    followed(result, 2)
    assert.deepEqual(result.calls.map(call => call.route), ["post", "list", "post", "poll"])
  })

  for (const http of [429, 500, 501, 503]) {
    test(`${job}: HTTP ${http} on POST is reconciled before any resend`, t => {
      followed(replay(t, job, { post: [response({ message: "temporary failure" }, http)], list: [response(recent())], poll: [live] }))
    })
  }

  for (const route of ["post", "poll", "list"]) {
    test(`${job}: HTTP 401 on ${route} fails immediately`, t => {
      const sequences = { post: route === "list" ? [transient] : [created], poll: [live] }
      sequences[route] = [response({ message: "Unauthorized" }, 401)]
      const result = replay(t, job, sequences)
      assert.notEqual(result.status, 0)
      assert.equal(result.counts[route], 1)
      assert.equal(result.counts.post, 1)
      assert.equal(result.output, route === "poll" ? `log_url=https://dashboard.render.com/web/${fixture.service.id}/deploys/${id}\n` : "")
    })
  }

  test(`${job}: a failed reconciliation read never permits another POST`, t => {
    const result = replay(t, job, { post: [transient], list: Array(20).fill(transient) })
    assert.notEqual(result.status, 0)
    assert.equal(result.counts.post, 1)
    assert.ok(result.counts.list >= 2 && result.counts.list <= 4)
  })

  test(`${job}: reconciliation retries a dropped read and follows the same deploy`, t => {
    followed(replay(t, job, { post: [transient], list: [transient, response(recent())], poll: [live] }))
  })

  test(`${job}: persistent POST failures have a bounded number of attempts`, t => {
    const result = replay(t, job, { post: Array(20).fill(transient), list: Array(20).fill(response([])) })
    assert.notEqual(result.status, 0)
    assert.ok(result.counts.post >= 2 && result.counts.post <= 4)
    assert.equal(result.counts.list, result.counts.post)
  })

  test(`${job}: a queued response preserves the existing refusal to guess a deploy ID`, t => {
    const result = replay(t, job, { post: [response("", 202)] })
    assert.notEqual(result.status, 0)
    assert.equal(result.counts.post, 1)
    assert.equal(result.output, "")
    assert.match(result.stdout, /queued/)
  })

  test(`${job}: ambiguous matching deploys stop without posting again`, t => {
    const result = replay(t, job, { post: [transient], list: [response([...recent(), ...recent({ id: "dep-other" })])] })
    assert.notEqual(result.status, 0)
    assert.equal(result.counts.post, 1)
    assert.equal(result.output, "")
  })

  test(`${job}: reconciliation follows pagination before resending`, t => {
    const page = Array.from({ length: 100 }, () => fixture.recent[1])
    const result = replay(t, job, { post: [transient], list: [response(page), response(recent())], poll: [live] })
    followed(result)
    assert.equal(result.counts.list, 2)
    assert.ok(result.calls.findLast(call => call.route === "list").args.includes(`cursor=${fixture.recent[1].cursor}`))
  })

  test(`${job}: malformed reconciliation data never permits another POST`, t => {
    for (const body of [{}, [{ deploy: { id, commit: { id: sha } } }], recent({ createdAt: "invalid" })]) {
      const result = replay(t, job, { post: [transient], list: [response(body)] })
      assert.notEqual(result.status, 0)
      assert.equal(result.counts.post, 1)
      assert.equal(result.output, "")
    }
  })

  for (const http of [429, 500, 501, 503]) {
    test(`${job}: status HTTP ${http} can recover to live`, t => {
      followed(replay(t, job, { post: [created], poll: [response({ message: "temporary failure" }, http), live] }))
    })
  }

  test(`${job}: final live commit read retries transient failures and still enforces identity`, t => {
    const name = job === "production" ? "Verify API health and live commit" : "Verify staging health and live commit"
    const recovered = replay(t, job, { live: [transient, response(fixture.recent)] }, name)
    assert.equal(recovered.status, 0, recovered.stdout + recovered.stderr)
    assert.equal(recovered.counts.live, 2)
    const wrong = replay(t, job, { live: [response([fixture.recent[1]])] }, name)
    assert.notEqual(wrong.status, 0)
    const denied = replay(t, job, { live: [response({ message: "Unauthorized" }, 401)] }, name)
    assert.notEqual(denied.status, 0)
    assert.equal(denied.counts.live, 1)
  })
}

test("staging: idempotent service PATCH retries a lost connection and 503, but fails on 401", t => {
  for (const failure of [transient, response({ message: "temporary failure" }, 503)]) {
    const result = replay(t, "staging", { patch: [failure, response(fixture.service)] }, "Select staging branch")
    assert.equal(result.status, 0, result.stdout + result.stderr)
    assert.equal(result.counts.patch, 2)
  }
  const denied = replay(t, "staging", { patch: [response({ message: "Unauthorized" }, 401)] }, "Select staging branch")
  assert.notEqual(denied.status, 0)
  assert.equal(denied.counts.patch, 1)
})
