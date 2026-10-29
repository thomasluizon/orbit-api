import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { join, resolve } from "node:path"
import { test } from "node:test"

const root = resolve(import.meta.dirname, "../..")
const workflow = readFileSync(join(root, ".github/workflows/release.yml"), "utf8")
const fixture = JSON.parse(readFileSync(new URL("./fixtures/staging-deployment.json", import.meta.url)))
const sha = fixture.deployments[0].sha
const digest = "sha256:" + "a".repeat(64)

function job(name) {
  return workflow.split(`\n  ${name}:\n`)[1].split(/\n  \w+:\n/)[0]
}

function script(jobName, step) {
  return job(jobName).split(`      - name: ${step}\n`)[1].split(/\n      - /)[0]
    .split("        run: |\n")[1].replace(/^          /gm, "")
}

function replay(t, jobName, step, deployments = fixture.deployments, statuses = fixture.statuses, exit = 0) {
  const directory = mkdtempSync(join(tmpdir(), "orbit-release-promotion-"))
  t.after(() => rmSync(directory, { recursive: true, force: true }))
  const statePath = join(directory, "state.json")
  const output = join(directory, "output")
  const summary = join(directory, "summary")
  writeFileSync(output, "")
  writeFileSync(summary, "")
  writeFileSync(statePath, JSON.stringify({ calls: [] }))
  writeFileSync(join(directory, "gh"), `#!${process.execPath}
const fs = require("node:fs")
const args = process.argv.slice(2)
const statePath = ${JSON.stringify(statePath)}
const state = JSON.parse(fs.readFileSync(statePath))
const input = args.includes("--input") ? fs.readFileSync(0, "utf8") : ""
state.calls.push({ args, input })
fs.writeFileSync(statePath, JSON.stringify(state))
if (args.includes("POST")) {
  process.stdout.write(args.includes("--jq") ? ${JSON.stringify(String(fixture.deployments[0].id))} : "{}")
} else if (args[1] === "repos/thomasluizon/orbit-api/deployments?sha=${sha}&environment=staging&per_page=1") {
  process.stdout.write(${JSON.stringify(JSON.stringify(deployments))})
} else if (args[1] === "repos/thomasluizon/orbit-api/deployments/${fixture.deployments[0].id}/statuses") {
  process.stdout.write(${JSON.stringify(JSON.stringify(statuses))})
} else {
  process.exit(99)
}
process.exitCode = ${exit}
`, { mode: 0o755 })
  const result = spawnSync("bash", ["-c", script(jobName, step)], {
    cwd: root, encoding: "utf8", timeout: 5000,
    env: {
      ...process.env, PATH: `${directory}:${process.env.PATH}`, COMMIT_SHA: sha,
      IMAGE_DIGEST: digest, GITHUB_REPOSITORY: "thomasluizon/orbit-api",
      GITHUB_OUTPUT: output, GITHUB_STEP_SUMMARY: summary, RENDER_LOG_URL: "https://dashboard.render.com/web/service/deploys/deploy",
    },
  })
  assert.ifError(result.error)
  return { ...result, ...JSON.parse(readFileSync(statePath)), output: readFileSync(output, "utf8"), summary: readFileSync(summary, "utf8") }
}

function recordedImage(t) {
  const result = replay(t, "staging", "Record deployment")
  assert.equal(result.status, 0, result.stdout + result.stderr)
  assert.equal(result.calls.length, 2)
  const body = JSON.parse(result.calls[0].input)
  assert.equal(body.ref, sha)
  assert.equal(body.environment, "staging")
  assert.equal(body.payload.image_digest, digest)
  assert.equal(JSON.parse(result.calls[1].input).state, "success")
  return { ...fixture.deployments[0], payload: body.payload }
}

test("production promotes the digest emitted by the successful staging record for its selected SHA", t => {
  const record = recordedImage(t)
  const result = replay(t, "production", "Resolve verified staging image", [record])
  assert.equal(result.status, 0, result.stdout + result.stderr)
  assert.equal(result.output, `digest=${digest}\n`)
  assert.match(result.summary, new RegExp(sha))
  assert.match(result.summary, new RegExp(digest))
  assert.equal(result.calls.length, 2)
  const production = job("production")
  assert.doesNotMatch(production, /build-push-action|docker build|packages: write|commitId/)
  assert.ok(production.indexOf("Require healthy staging commit") < production.indexOf("Resolve verified staging image"))
  assert.ok(production.indexOf("Resolve verified staging image") < production.indexOf("Trigger and monitor Render deploy"))
  for (const step of ["Trigger and monitor Render deploy", "Verify API health and live commit"]) {
    const source = production.split(`      - name: ${step}\n`)[1].split(/\n      - /)[0]
    assert.match(source, /IMAGE_DIGEST: \$\{\{ steps.image.outputs.digest \}\}/)
  }
  const staging = job("staging")
  assert.ok(staging.indexOf("Verify staging health and live commit") < staging.indexOf("Record deployment"))
})

test("missing, legacy, malformed and unrelated staging records cannot supply a production image", t => {
  const record = recordedImage(t)
  for (const records of [
    [], fixture.deployments, [null], [{ ...record, sha: "b".repeat(40) }],
    [{ ...record, environment: "production" }], [{ ...record, creator: { login: "operator" } }],
    [{ ...record, id: null }], [{ ...record, payload: null }],
    ...[null, "latest", "sha256:" + "a".repeat(63), digest + "\n", { digest }]
      .map(image_digest => [{ ...record, payload: { image_digest } }]),
  ]) {
    const result = replay(t, "production", "Resolve verified staging image", records)
    assert.notEqual(result.status, 0, JSON.stringify(records))
    assert.equal(result.output, "")
    assert.equal(result.calls.length, 1)
  }
})

test("a recorded digest still requires staging success and successful GitHub reads", t => {
  const record = recordedImage(t)
  for (const statuses of [[], [{ state: "failure" }], [{ state: "pending" }], null, {}]) {
    const result = replay(t, "production", "Resolve verified staging image", [record], statuses)
    assert.notEqual(result.status, 0, JSON.stringify(statuses))
    assert.equal(result.output, "")
  }
  const unavailable = replay(t, "production", "Resolve verified staging image", [record], fixture.statuses, 1)
  assert.notEqual(unavailable.status, 0)
  assert.equal(unavailable.output, "")
})
