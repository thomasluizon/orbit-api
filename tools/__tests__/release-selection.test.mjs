import assert from "node:assert/strict"
import { execFileSync, spawnSync } from "node:child_process"
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { dirname, join, resolve } from "node:path"
import { test } from "node:test"
import { fileURLToPath } from "node:url"

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..")
const workflow = readFileSync(join(root, ".github/workflows/release.yml"), "utf8")
const healthy = JSON.parse(readFileSync(new URL("./fixtures/release-health.json", import.meta.url)))
const otherSha = "377eca519ab8d5e5f5eb41b05b448f42db88d589"

function job(name) {
  return workflow.split(`\n  ${name}:\n`)[1].split(/\n  \w+:\n/)[0]
}

function step(source, name) {
  const start = source.indexOf(`      - name: ${name}\n`)
  assert.ok(start >= 0, `Missing ${name}`)
  const next = source.indexOf("\n      - ", start + 1)
  return source.slice(start, next < 0 ? undefined : next)
}

function script(source) {
  assert.ok(source.includes("        run: |\n"), "Missing shell script")
  return source.split("        run: |\n")[1].replace(/^          /gm, "")
}

function replay(source, env = {}, cwd = root) {
  const directory = mkdtempSync(join(tmpdir(), "orbit-release-selection-"))
  const output = join(directory, "output")
  const summary = join(directory, "summary")
  writeFileSync(output, "")
  writeFileSync(summary, "")
  try {
    const result = spawnSync("bash", ["-c", source], {
      cwd, encoding: "utf8", timeout: 5000,
      env: { ...process.env, GITHUB_OUTPUT: output, GITHUB_STEP_SUMMARY: summary, ...env },
    })
    assert.ifError(result.error)
    return { ...result, output: readFileSync(output, "utf8"), summary: readFileSync(summary, "utf8") }
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
}

const selectionEnv = {
  GITHUB_REF: "refs/heads/main", RELEASE_ENVIRONMENT: "staging", RELEASE_BRANCH: "main",
  RELEASE_SHA: healthy.commit, DISPATCH_SHA: otherSha,
}

test("prepare checks out the dispatch selection with history, never the moving branch", () => {
  const prepare = job("prepare")
  const selection = step(prepare, "Validate release selection")
  assert.match(prepare, /ref: \$\{\{ steps.selection.outputs.sha \}\}/)
  assert.match(selection, /id: selection/)
  assert.match(selection, /RELEASE_SHA: \$\{\{ inputs.sha \}\}/)
  assert.match(selection, /DISPATCH_SHA: \$\{\{ github.sha \}\}/)
  assert.match(prepare, /fetch-depth: 0/)
  assert.doesNotMatch(prepare, /ref: \$\{\{ inputs.branch \}\}/)
  const result = replay(script(selection), selectionEnv)
  assert.equal(result.status, 0, result.stdout + result.stderr)
  assert.equal(result.output, `sha=${healthy.commit}\n`)
})

test("production defaults to the dispatch commit and accepts an explicit full SHA", () => {
  const source = script(step(job("prepare"), "Validate release selection"))
  for (const sha of ["", healthy.commit]) {
    const result = replay(source, { ...selectionEnv, RELEASE_ENVIRONMENT: "production", RELEASE_SHA: sha })
    assert.equal(result.status, 0, result.stdout + result.stderr)
    assert.equal(result.output, `sha=${sha || otherSha}\n`)
  }
})

test("invalid dispatch selections fail before checkout", () => {
  const source = script(step(job("prepare"), "Validate release selection"))
  for (const env of [
    { RELEASE_SHA: "" }, { RELEASE_SHA: healthy.commit.slice(0, 8) },
    { RELEASE_SHA: "g".repeat(40) }, { RELEASE_SHA: `${healthy.commit}\n${otherSha}` },
    { RELEASE_BRANCH: "refs/heads/main" }, { RELEASE_BRANCH: "" },
    { RELEASE_BRANCH: "../main" }, { RELEASE_BRANCH: "main:other" },
    { GITHUB_REF: "refs/heads/feature/change" },
    { RELEASE_ENVIRONMENT: "production", RELEASE_BRANCH: "feature/change" },
  ]) {
    const result = replay(source, { ...selectionEnv, ...env })
    assert.notEqual(result.status, 0, JSON.stringify(env))
    assert.equal(result.output, "")
  }
})

test("a moved branch keeps the planned ancestor and rejects an unrelated commit or wrong checkout", () => {
  const resolveStep = step(job("prepare"), "Resolve commit")
  assert.match(resolveStep, /RELEASE_SHA: \$\{\{ steps.selection.outputs.sha \}\}/)
  assert.match(job("prepare"), /sha: \$\{\{ steps.commit.outputs.sha \}\}/)
  const directory = mkdtempSync(join(tmpdir(), "orbit-release-git-"))
  const git = (...args) => execFileSync("git", args, { cwd: directory, encoding: "utf8" }).trim()
  try {
    git("init", "--quiet")
    git("config", "user.name", "Release test")
    git("config", "user.email", "release-test@example.org")
    writeFileSync(join(directory, "file"), "planned")
    git("add", "file")
    git("commit", "--quiet", "-m", "Planned commit")
    const planned = git("rev-parse", "HEAD")
    writeFileSync(join(directory, "file"), "branch moved")
    git("commit", "--quiet", "-am", "Moved branch")
    const moved = git("rev-parse", "HEAD")
    git("update-ref", "refs/remotes/origin/main", moved)
    git("update-ref", "refs/remotes/origin/feature/change", planned)
    git("checkout", "--quiet", "--detach", planned)
    const result = replay(script(resolveStep), { RELEASE_SHA: planned, RELEASE_BRANCH: "main" }, directory)
    assert.equal(result.status, 0, result.stdout + result.stderr)
    assert.equal(result.output, `sha=${planned}\n`)
    const wrongCheckout = replay(script(resolveStep), { RELEASE_SHA: moved, RELEASE_BRANCH: "main" }, directory)
    assert.notEqual(wrongCheckout.status, 0)
    assert.equal(wrongCheckout.output, "")
    git("checkout", "--quiet", "--detach", moved)
    const unrelated = replay(script(resolveStep), { RELEASE_SHA: moved, RELEASE_BRANCH: "feature/change" }, directory)
    assert.notEqual(unrelated.status, 0)
    assert.equal(unrelated.output, "")
    git("checkout", "--quiet", "--detach", planned)
    const feature = replay(script(resolveStep), { RELEASE_SHA: planned, RELEASE_BRANCH: "feature/change" }, directory)
    assert.equal(feature.status, 0, feature.stdout + feature.stderr)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

function stagingGate(body, { force = "false", transport = false, raw } = {}) {
  const production = job("production")
  const gate = step(production, "Require healthy staging commit")
  assert.ok(production.indexOf(gate) < production.indexOf("      - name: Trigger and monitor Render deploy"))
  assert.match(gate, /FORCE_UNSTAGED: \$\{\{ inputs.force_unstaged \}\}/)
  const directory = mkdtempSync(join(tmpdir(), "orbit-release-staging-"))
  const calls = join(directory, "calls")
  writeFileSync(calls, "")
  writeFileSync(join(directory, "curl"), `#!${process.execPath}
const fs = require("node:fs")
fs.appendFileSync(${JSON.stringify(calls)}, JSON.stringify(process.argv.slice(2)) + "\\n")
process.stdout.write(${JSON.stringify(raw ?? JSON.stringify(body))})
process.exitCode = ${transport ? 22 : 0}
`, { mode: 0o755 })
  try {
    const result = replay(script(gate), {
      PATH: `${directory}:${process.env.PATH}`, COMMIT_SHA: healthy.commit, FORCE_UNSTAGED: force,
    })
    return { ...result, calls: readFileSync(calls, "utf8") }
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
}

test("production admits only the exact Healthy commit served by staging", () => {
  const result = stagingGate(healthy)
  assert.equal(result.status, 0, result.stdout + result.stderr)
  assert.match(result.calls, /https:\/\/api-staging\.useorbit\.org\/health/)
  assert.match(result.calls, /--fail-with-body/)
  assert.match(result.calls, /--max-time/)
  assert.match(result.summary, /force_unstaged: false/)
  for (const body of [
    { ...healthy, commit: otherSha }, { ...healthy, commit: healthy.commit.slice(0, 8) },
    { ...healthy, status: "Unhealthy" }, { ...healthy, status: "healthy" },
    { status: "Healthy" }, { commit: healthy.commit }, null, [healthy],
  ]) {
    const rejected = stagingGate(body)
    assert.notEqual(rejected.status, 0, JSON.stringify(body))
  }
  for (const options of [{ transport: true }, { raw: "invalid JSON" }]) {
    assert.notEqual(stagingGate(healthy, options).status, 0)
  }
})

test("an explicit staging override is recorded and can release while staging is unavailable", () => {
  assert.match(workflow, /force_unstaged:\n[\s\S]*?type: boolean\n[\s\S]*?default: false/)
  const result = stagingGate(null, { force: "true", transport: true })
  assert.equal(result.status, 0, result.stdout + result.stderr)
  assert.match(result.summary, /force_unstaged: true/)
  assert.match(result.summary, new RegExp(healthy.commit))
  assert.match(result.stdout, /::notice::/)
})

test("both deployments, verifiers and summaries use the prepared SHA", () => {
  for (const name of ["production", "staging"]) {
    const source = job(name)
    assert.match(source, /COMMIT_SHA: \$\{\{ needs.prepare.outputs.sha \}\}/)
    const deploy = script(step(source, "Trigger and monitor Render deploy"))
    assert.match(deploy, /--arg image "\$image_url" '\{imageUrl: \$image\}'/)
    assert.doesNotMatch(deploy, /commitId/)
    assert.doesNotMatch(deploy, /git rev-parse|inputs.branch|branches\//)
    const summary = replay(script(step(source, "Summarize release")), { COMMIT_SHA: healthy.commit, IMAGE_DIGEST: "sha256:" + "a".repeat(64) })
    assert.equal(summary.status, 0, summary.stderr)
    assert.match(summary.summary, new RegExp(`Deployed SHA: ${healthy.commit}`))
    assert.match(summary.summary, new RegExp(name))
  }
})


test("staging builds the verified source checkout and carries its SHA into the image", () => {
  const staging = job("staging")
  assert.match(staging, /packages: write/)
  assert.match(staging, /ref: \$\{\{ needs.prepare.outputs.sha \}\}[\s\S]*?fetch-depth: 0[\s\S]*?path: release-source/)
  assert.match(staging, /driver: docker/)
  assert.match(staging, /context: \.\/release-source/)
  assert.match(staging, /file: \.\/release-source\/Dockerfile/)
  assert.match(staging, /push: true/)
  assert.match(staging, /tags: ghcr.io\/thomasluizon\/orbit-api:\$\{\{ needs.prepare.outputs.sha \}\}/)
  assert.match(staging, /build-args: API_COMMIT_SHA=\$\{\{ needs.prepare.outputs.sha \}\}/)
  assert.match(staging, /IMAGE_DIGEST: \$\{\{ steps.image.outputs.digest \}\}/)
  assert.doesNotMatch(staging, /Select staging branch|render_request PATCH|commitId/)
  const dockerfile = readFileSync(join(root, "Dockerfile"), "utf8")
  assert.match(dockerfile, /FROM .* AS runtime[\s\S]*ARG API_COMMIT_SHA\nENV RENDER_GIT_COMMIT=\$API_COMMIT_SHA/)
  const directory = mkdtempSync(join(tmpdir(), "orbit-image-source-"))
  const git = (...args) => execFileSync("git", args, { cwd: directory, encoding: "utf8" }).trim()
  try {
    git("init", "--quiet", "release-source")
    git("-C", "release-source", "config", "user.name", "Release test")
    git("-C", "release-source", "config", "user.email", "release-test@example.org")
    writeFileSync(join(directory, "release-source", "file"), "planned")
    git("-C", "release-source", "add", "file")
    git("-C", "release-source", "commit", "--quiet", "-m", "Planned commit")
    const sha = git("-C", "release-source", "rev-parse", "HEAD")
    git("-C", "release-source", "update-ref", "refs/remotes/origin/main", sha)
    const source = script(step(staging, "Verify image source"))
    assert.equal(replay(source, { COMMIT_SHA: sha, RELEASE_BRANCH: "main" }, directory).status, 0)
    assert.notEqual(replay(source, { COMMIT_SHA: otherSha, RELEASE_BRANCH: "main" }, directory).status, 0)
    assert.notEqual(replay(source, { COMMIT_SHA: sha, RELEASE_BRANCH: "other" }, directory).status, 0)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})
