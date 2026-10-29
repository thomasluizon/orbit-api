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

for (const job of ["production", "staging"]) {

test(`${job}: sends only the immutable image and follows its returned deploy`, t => {
  const image = "ghcr.io/thomasluizon/orbit-api@sha256:" + "a".repeat(64)
  const deploy = { id, image: { ref: image, sha: "sha256:" + "a".repeat(64) }, status: "live" }
  const result = replay(t, job, { post: [response(deploy, 201)], poll: [response(deploy)] })
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
  test(`${job}: ${name} stops without another POST or a status poll`, t => {
    const result = replay(t, job, { post: [entry] })
    assert.notEqual(result.status, 0)
    assert.equal(result.counts.post, 1)
    assert.equal(result.counts.poll ?? 0, 0)
    assert.equal(result.output, "")
    if (name === "Git-backed service") assert.match(result.stdout, /not image-backed yet.*coordinator.*refresh Terraform state/)
  })
}

for (const status of ["build_failed", "pre_deploy_failed", "update_failed", "canceled", "deactivated", "unknown"]) {
  test(`${job}: ${status} never records a successful release`, t => {
    const image = "ghcr.io/thomasluizon/orbit-api@sha256:" + "a".repeat(64)
    const result = replay(t, job, {
      post: [response({ id, image: { ref: image } }, 201)], poll: [response({ id, status })],
    })
    assert.notEqual(result.status, 0)
    assert.equal(result.counts.post, 1)
    assert.equal(result.counts.poll, 1)
  })
}

test(`${job}: transient polling recovers and the final live image must match`, t => {
  const image = "ghcr.io/thomasluizon/orbit-api@sha256:" + "a".repeat(64)
  const deploy = { id, image: { ref: image }, status: "live" }
  const result = replay(t, job, {
    post: [response(deploy, 201)], poll: [transient, response({ ...deploy, status: "update_in_progress" }), response(deploy)],
  })
  assert.equal(result.status, 0, result.stdout + result.stderr)
  assert.equal(result.counts.poll, 3)
  const name = job === "production" ? "Verify API health and live commit" : "Verify staging health and live commit"
  const recovered = replay(t, job, { live: [transient, response([{ deploy }])] }, name)
  assert.equal(recovered.status, 0, recovered.stdout + recovered.stderr)
  for (const body of [fixture.recent, [{ deploy: { ...deploy, image: { ref: image + "wrong" } } }]]) {
    assert.notEqual(replay(t, job, { live: [response(body)] }, name).status, 0)
  }
})

}
