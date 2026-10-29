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

function replay(t, jobName, step, deployments = fixture.deployments, statuses = fixture.statuses, exit = 0, forceUnstaged = false, imageEnvironment = {}) {
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
      FORCE_UNSTAGED: String(forceUnstaged), IMAGE_DIGEST: digest, GITHUB_REPOSITORY: "thomasluizon/orbit-api",
      GITHUB_OUTPUT: output, GITHUB_STEP_SUMMARY: summary, RENDER_LOG_URL: "https://dashboard.render.com/web/service/deploys/deploy",
      ...imageEnvironment,
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
  assert.doesNotMatch(production, /commitId/)
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

test("only an explicit override without a successful staging image requests a production build", t => {
  const record = recordedImage(t)
  for (const [records, statuses] of [
    [[], fixture.statuses], [fixture.deployments, fixture.statuses],
    [[record], []], [[record], [{ state: "failure" }]], [[record], [{ state: "pending" }]],
  ]) {
    const refused = replay(t, "production", "Resolve verified staging image", records, statuses)
    assert.notEqual(refused.status, 0)
    assert.equal(refused.output, "")
    const forced = replay(t, "production", "Resolve verified staging image", records, statuses, 0, true)
    assert.equal(forced.status, 0, forced.stdout + forced.stderr)
    assert.equal(forced.output, "build_required=true\n")
  }
  const promoted = replay(t, "production", "Resolve verified staging image", [record], fixture.statuses, 0, true)
  assert.equal(promoted.status, 0, promoted.stdout + promoted.stderr)
  assert.equal(promoted.output, `digest=${digest}\n`)
  const unavailable = replay(t, "production", "Resolve verified staging image", [record], fixture.statuses, 1, true)
  assert.notEqual(unavailable.status, 0)
  assert.equal(unavailable.output, "")
})

function blocks(jobName) {
  return job(jobName).split("\n      - ").slice(1).map(block => `      - ${block}`)
}

test("every production build step is gated and reuses the verified staging build recipe", () => {
  const production = blocks("production")
  const staging = blocks("staging")
  const gate = "        if: inputs.force_unstaged && steps.staging_image.outputs.build_required == 'true'\n"
  for (const marker of ["path: release-source", "name: Verify image source", "docker/setup-buildx-action@", "docker/login-action@", "docker/build-push-action@"]) {
    const step = production.find(block => block.includes(marker))
    assert.ok(step, `Missing production build step: ${marker}`)
    assert.ok(step.includes(gate) || step.startsWith(`      - ${gate.trim()}\n`), step)
    const recipe = staging.find(block => block.includes(marker))
    const normalize = block => block
      .replace(gate, "")
      .replace(`      - ${gate.trim()}\n        uses:`, "      - uses:")
      .replace("Build and push production image", "Build and push staging image")
      .replace("id: build_image", "id: image").trim()
    assert.equal(normalize(step), normalize(recipe))
  }
  assert.match(job("production"), /packages: write/)
  assert.equal(script("production", "Verify image source"), script("staging", "Verify image source"))
  assert.ok(job("production").indexOf("Select production image") < job("production").indexOf("Trigger and monitor Render deploy"))
})

test("the selected production digest reaches the deploy, verification and deployment record", t => {
  const built = "sha256:" + "b".repeat(64)
  for (const [staged, image, expected] of [[digest, "", digest], ["", built, built]]) {
    const result = replay(t, "production", "Select production image", undefined, undefined, 0, false,
      { STAGING_IMAGE_DIGEST: staged, BUILT_IMAGE_DIGEST: image })
    assert.equal(result.status, 0, result.stdout + result.stderr)
    assert.equal(result.output, `digest=${expected}\n`)
    assert.match(result.summary, new RegExp(expected))
  }
  for (const value of ["", "latest", "sha256:" + "a".repeat(63), digest + "\n"]) {
    const result = replay(t, "production", "Select production image", undefined, undefined, 0, false,
      { STAGING_IMAGE_DIGEST: "", BUILT_IMAGE_DIGEST: value })
    assert.notEqual(result.status, 0)
    assert.equal(result.output, "")
  }
  const recorded = replay(t, "production", "Record deployment")
  assert.equal(recorded.status, 0, recorded.stdout + recorded.stderr)
  const body = JSON.parse(recorded.calls[0].input)
  assert.equal(body.ref, sha)
  assert.equal(body.environment, "production")
  assert.equal(body.payload.image_digest, digest)
  for (const step of ["Trigger and monitor Render deploy", "Verify API health and live commit", "Record deployment", "Summarize release"]) {
    assert.match(blocks("production").find(block => block.includes(`name: ${step}\n`)), /IMAGE_DIGEST: \$\{\{ steps.image.outputs.digest \}\}/)
  }
})
