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
const history = Array.from({ length: 600 }, (_, index) => ({
  ...fixture.recent[1],
  deploy: { ...fixture.recent[1].deploy, id: `${fixture.recent[1].deploy.id}-${index}` },
  cursor: `${fixture.recent[1].cursor}-${index}`,
}))
const pages = entries => Array.from({ length: Math.floor(entries.length / 100) + 1 },
  (_, index) => response(entries.slice(index * 100, (index + 1) * 100)))

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
  writeFileSync(statePath, JSON.stringify({ sequences: { baseline: [response([])], ...sequences }, counts: {}, calls: [], waits: [], elapsed: 0 }))
  writeFileSync(join(directory, "curl"), `#!${process.execPath}
const fs = require("node:fs")
const statePath = ${JSON.stringify(statePath)}
const state = JSON.parse(fs.readFileSync(statePath))
const args = process.argv.slice(2)
const option = flag => args[args.indexOf(flag) + 1]
const method = args.includes("--request") ? option("--request") : "GET"
const url = args.find(arg => arg.startsWith("https://api.render.com/"))
const route = method === "POST" ? "post" : method === "PATCH" ? "patch"
  : args.includes("status=live") ? "live" : url?.endsWith("/deploys") ? (state.counts.post ? "list" : "baseline") : "poll"
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
  if (entry.notBefore > state.elapsed) {
    fs.writeFileSync(statePath, JSON.stringify(state))
    console.error("Retried before Retry-After expired")
    process.exit(99)
  }
  const http = Number(entry.http ?? 200)
  const failsHttp = args.includes("--fail") || args.includes("--fail-with-body")
  const retry = [408, 429, 500, 502, 503, 504].includes(http)
    || (args.includes("--retry-all-errors") && (entry.exit || (failsHttp && http >= 400)))
  if (!retry || attempt === retries) break
  const delay = /^\\d+$/.test(entry.retryAfter ?? "") ? Math.max(2, Number(entry.retryAfter)) : 2
  if (delay > Number(args.includes("--retry-max-time") ? option("--retry-max-time") : Infinity)) break
  state.waits.push(String(delay))
  state.elapsed += delay
}
fs.writeFileSync(statePath, JSON.stringify(state))
const body = typeof entry.body === "string" ? entry.body : JSON.stringify(entry.body)
if (args.includes("--output")) fs.writeFileSync(option("--output"), body)
else process.stdout.write(body)
if (args.includes("--dump-header")) fs.writeFileSync(option("--dump-header"),
  "HTTP/1.1 " + (entry.http ?? 200) + "\\r\\n" + (entry.retryAfter === undefined ? "" : "rEtRy-AfTeR: " + entry.retryAfter + "\\r\\n") + "\\r\\n")
if (args.includes("--write-out")) process.stdout.write(String(entry.http ?? 200))
process.exitCode = entry.exit ?? ((args.includes("--fail") || args.includes("--fail-with-body")) && Number(entry.http) >= 400 ? 22 : 0)
`, { mode: 0o755 })
  writeFileSync(join(directory, "sleep"), `#!${process.execPath}
const fs = require("node:fs")
const statePath = ${JSON.stringify(statePath)}
const state = JSON.parse(fs.readFileSync(statePath))
state.waits.push(process.argv[2])
state.elapsed += Number(process.argv[2])
fs.writeFileSync(statePath, JSON.stringify(state))
`, { mode: 0o755 })
  writeFileSync(join(directory, "node"), "#!/bin/sh\nexit 0\n", { mode: 0o755 })
  const result = spawnSync("bash", ["-c", script(job, name)], {
    cwd: root, encoding: "utf8", timeout: 10_000,
    env: {
      ...process.env, PATH: `${directory}:${process.env.PATH}`, RENDER_API_KEY: "test-key",
      RENDER_SERVICE_ID: fixture.service.id, COMMIT_SHA: sha, RELEASE_BRANCH: "main",
      GITHUB_OUTPUT: outputPath, IMAGE_DIGEST: "sha256:" + "a".repeat(64),
    },
  })
  assert.ifError(result.error)
  return { ...result, ...JSON.parse(readFileSync(statePath)), output: readFileSync(outputPath, "utf8") }
}

function followed(result, posts = 1) {
  assert.equal(result.status, 0, result.stdout + result.stderr)
  assert.equal(result.counts.post, posts)
  assert.equal(result.calls[0].route, "baseline")
  assert.ok(result.counts.poll >= 1)
  assert.match(result.stdout, /Render deploy status: live/)
  assert.match(result.output, new RegExp(`log_url=https://dashboard.render.com/web/.+/deploys/${id}`))
  for (const call of result.calls.filter(call => call.route === "post")) {
    assert.ok(!call.args.includes("--retry"), "POST must never use curl automatic retries")
    assert.deepEqual(JSON.parse(call.args[call.args.indexOf("--data") + 1]), { commitId: sha })
  }
}

for (const job of ["production"]) {
  test(`${job}: a large deploy history completes the baseline and permits one POST`, t => {
    const result = replay(t, job, { baseline: pages(history), post: [created], poll: [live] })
    followed(result)
    assert.equal(result.counts.baseline, 7)
  })

  test(`${job}: a large deploy history reconciles a lost POST response and excludes baseline identities`, t => {
    const historical = recent({ id: "dep-historical" })
    const result = replay(t, job, {
      baseline: pages([...history, ...historical]), post: [transient],
      list: pages([...history, ...historical, ...recent()]), poll: [live],
    })
    followed(result)
    assert.equal(result.counts.baseline, 7)
    assert.equal(result.counts.list, 7)
  })

  test(`${job}: a large deploy history rejects matches on different pages without another POST`, t => {
    const result = replay(t, job, {
      baseline: pages(history), post: [transient],
      list: pages([...recent(), ...history, ...recent({ id: "dep-other" })]),
    })
    assert.notEqual(result.status, 0)
    assert.equal(result.counts.post, 1)
    assert.equal(result.counts.list, 7)
    assert.equal(result.counts.poll ?? 0, 0)
    assert.match(result.stderr, /Multiple Render deploys match this release/)
  })

  test(`${job}: repeated pagination cursors stop the baseline before any POST`, t => {
    const page = history.slice(0, 100)
    const result = replay(t, job, { baseline: [response(page), response(page)] })
    assert.notEqual(result.status, 0)
    assert.equal(result.counts.baseline, 2)
    assert.equal(result.counts.post ?? 0, 0)
    assert.match(result.stderr, /Render deploy listing repeated its cursor/)
  })

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

  for (const exit of [28, 52, 55]) {
    test(`${job}: curl ${exit} after sending POST only reconciles`, t => {
      followed(replay(t, job, { post: [{ ...transient, exit }], list: [response([]), response(recent())], poll: [live] }))
    })
  }

  for (const exit of [6, 7]) {
    test(`${job}: curl ${exit} before sending POST can retry`, t => {
      followed(replay(t, job, { post: [{ ...transient, exit }, created], poll: [live] }), 2)
    })
  }

  test(`${job}: repeated pre-connection failures stop after four POST attempts`, t => {
    const result = replay(t, job, { post: Array(4).fill({ ...transient, exit: 7 }) })
    assert.notEqual(result.status, 0)
    assert.equal(result.counts.post, 4)
    assert.equal(result.counts.list ?? 0, 0)
  })

  test(`${job}: a POST completing after the first empty snapshot is followed without resending`, t => {
    const result = replay(t, job, {
      post: [transient, created],
      list: [response([]), response(recent())],
      poll: [live],
    })
    followed(result)
    assert.equal(result.counts.list, 2)
  })

  for (const http of [500, 501, 503]) {
    test(`${job}: HTTP ${http} on POST is reconciled without any resend`, t => {
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

  test(`${job}: unrecovered ambiguous POST stops with operator guidance after bounded read-only reconciliation`, t => {
    const result = replay(t, job, { post: Array(20).fill(transient), list: Array(20).fill(response([])) })
    assert.notEqual(result.status, 0)
    assert.equal(result.counts.post, 1)
    assert.equal(result.counts.list, 4)
    assert.match(result.stderr, new RegExp(`check.*deploys.*before.*dispatch`, "i"))
    assert.ok(result.stderr.includes(sha))
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

  test(`${job}: reconciliation follows pagination without resending`, t => {
    const page = Array.from({ length: 100 }, () => fixture.recent[1])
    const result = replay(t, job, { post: [transient], list: [response(page), response(recent())], poll: [live] })
    followed(result)
    assert.equal(result.counts.list, 2)
    assert.ok(result.calls.findLast(call => call.route === "list").args.includes(`cursor=${fixture.recent[1].cursor}`))
  })

  test(`${job}: a historical-only same-commit deploy is never reused even with a recent timestamp`, t => {
    const history = recent()
    const result = replay(t, job, {
      baseline: [response(history)], post: [transient],
      list: Array(4).fill(response(history)), poll: [live],
    })
    assert.notEqual(result.status, 0)
    assert.equal(result.counts.post, 1)
    assert.equal(result.counts.poll ?? 0, 0)
  })

  for (const historicalFirst of [true, false]) {
    test(`${job}: selects the new same-commit deploy with historicalFirst=${historicalFirst}`, t => {
      const history = recent({ id: "dep-historical" })
      const entries = historicalFirst ? [...history, ...recent()] : [...recent(), ...history]
      followed(replay(t, job, { baseline: [response(history)], post: [transient], list: [response(entries)], poll: [live] }))
    })
  }

  for (const route of ["post", "poll", "baseline"]) {
    test(`${job}: HTTP 429 on ${route} waits for Retry-After before trying again`, t => {
      const sequences = { post: [created], poll: [live], baseline: [response([])], list: Array(4).fill(response([])) }
      const success = route === "post" ? created : route === "poll" ? live : response([])
      sequences[route] = [{ ...response({ message: "rate limit exceeded" }, 429), retryAfter: "7" }, { ...success, notBefore: 7 }]
      const result = replay(t, job, sequences)
      followed(result, route === "post" ? 2 : 1)
      assert.ok(result.waits.includes("7"))
    })
  }

  test(`${job}: POST 429 uses the fallback delay for missing or invalid Retry-After`, t => {
    for (const retryAfter of [undefined, "invalid", "-1"]) {
      const result = replay(t, job, {
        post: [{ ...response({}, 429), retryAfter }, { ...created, notBefore: 2 }], poll: [live],
      })
      followed(result, 2)
      assert.deepEqual(result.waits, ["2"])
    }
  })

  test(`${job}: Retry-After beyond the 60-second budget stops without retrying early`, t => {
    for (const route of ["post", "baseline"]) {
      const result = replay(t, job, { [route]: [{ ...response({}, 429), retryAfter: "61" }] })
      assert.notEqual(result.status, 0)
      assert.equal(result.counts[route], 1)
      assert.deepEqual(result.waits, [])
    }
  })

  test(`${job}: a failed baseline read stops before sending any POST`, t => {
    const result = replay(t, job, { baseline: [response({}, 401)] })
    assert.notEqual(result.status, 0)
    assert.equal(result.counts.post ?? 0, 0)
  })

  test(`${job}: baseline and reconciliation scan every page before selecting an identity`, t => {
    const history = recent({ id: "dep-historical" })
    const page = Array.from({ length: 100 }, () => fixture.recent[1])
    const result = replay(t, job, {
      baseline: [response(page), response(history)], post: [transient],
      list: [response([...page.slice(0, 98), ...recent(), ...history]), response([])], poll: [live],
    })
    followed(result)
    assert.equal(result.counts.baseline, 2)
    assert.equal(result.counts.list, 2)
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

test("staging: sends only the immutable image and follows its returned deploy", t => {
  const image = "ghcr.io/thomasluizon/orbit-api@sha256:" + "a".repeat(64)
  const deploy = { id, image: { ref: image, sha: "sha256:" + "a".repeat(64) }, status: "live" }
  const result = replay(t, "staging", { post: [response(deploy, 201)], poll: [response(deploy)] })
  assert.equal(result.status, 0, result.stdout + result.stderr)
  const posts = result.calls.filter(call => call.route === "post")
  assert.equal(posts.length, 1)
  assert.deepEqual(JSON.parse(posts[0].args[posts[0].args.indexOf("--data") + 1]), { imageUrl: image })
  assert.equal(result.counts.poll, 1)
  assert.equal(result.counts.baseline ?? 0, 0)
})


for (const [name, entry] of [
  ["queued", response("", 202)],
  ["Git-backed service", response({ message: "imageUrl is not supported" }, 400)],
  ["server failure", response({}, 503)],
  ["lost response", transient],
  ["wrong image", response({ id, image: { ref: "ghcr.io/thomasluizon/orbit-api:main" } }, 201)],
  ["missing image", created],
  ["missing identity", response({ image: { ref: "ghcr.io/thomasluizon/orbit-api@sha256:" + "a".repeat(64) } }, 201)],
]) {
  test(`staging: ${name} stops without another POST or a status poll`, t => {
    const result = replay(t, "staging", { post: [entry] })
    assert.notEqual(result.status, 0)
    assert.equal(result.counts.post, 1)
    assert.equal(result.counts.poll ?? 0, 0)
    assert.equal(result.output, "")
    if (name === "Git-backed service") assert.match(result.stdout, /not image-backed yet.*coordinator.*refresh Terraform state/)
  })
}

for (const status of ["build_failed", "pre_deploy_failed", "update_failed", "canceled", "deactivated", "unknown"]) {
  test(`staging: ${status} never records a successful release`, t => {
    const image = "ghcr.io/thomasluizon/orbit-api@sha256:" + "a".repeat(64)
    const result = replay(t, "staging", {
      post: [response({ id, image: { ref: image } }, 201)], poll: [response({ id, status })],
    })
    assert.notEqual(result.status, 0)
    assert.equal(result.counts.post, 1)
    assert.equal(result.counts.poll, 1)
  })
}

test("staging: transient polling recovers and the final live image must match", t => {
  const image = "ghcr.io/thomasluizon/orbit-api@sha256:" + "a".repeat(64)
  const deploy = { id, image: { ref: image }, status: "live" }
  const result = replay(t, "staging", {
    post: [response(deploy, 201)], poll: [transient, response({ ...deploy, status: "update_in_progress" }), response(deploy)],
  })
  assert.equal(result.status, 0, result.stdout + result.stderr)
  assert.equal(result.counts.poll, 3)
  const name = "Verify staging health and live commit"
  const recovered = replay(t, "staging", { live: [transient, response([{ deploy }])] }, name)
  assert.equal(recovered.status, 0, recovered.stdout + recovered.stderr)
  for (const body of [fixture.recent, [{ deploy: { ...deploy, image: { ref: image + "wrong" } } }]]) {
    assert.notEqual(replay(t, "staging", { live: [response(body)] }, name).status, 0)
  }
})
