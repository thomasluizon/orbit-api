import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { join, resolve } from "node:path"
import { test } from "node:test"

const root = resolve(import.meta.dirname, "../..")
const workflow = readFileSync(join(root, ".github/workflows/staging-reseed.yml"), "utf8")
const step = workflow.split("      - name: Deploy the staging API and wait for that deploy\n")[1].split("\n      - name:")[0]
const script = step.split("        run: |\n")[1].replace(/^          /gm, "")
const image = "ghcr.io/thomasluizon/orbit-api@sha256:" + "a".repeat(64)
const deploy = { id: "dep-image-reseed", image: { ref: image }, status: "live" }

function replay(t, { live = [{ deploy }], post = deploy, http = 201, statuses = ["live"] } = {}) {
  const directory = mkdtempSync(join(tmpdir(), "orbit-reseed-image-"))
  t.after(() => rmSync(directory, { recursive: true, force: true }))
  const statePath = join(directory, "state.json")
  writeFileSync(statePath, JSON.stringify({ calls: [], polls: 0 }))
  writeFileSync(join(directory, "curl"), `#!${process.execPath}
const fs = require("node:fs")
const statePath = ${JSON.stringify(statePath)}
const state = JSON.parse(fs.readFileSync(statePath))
const args = process.argv.slice(2)
state.calls.push(args)
const post = args.includes("POST")
const live = args.includes("status=live")
let body = post ? ${JSON.stringify(post)} : live ? ${JSON.stringify(live)}
  : { id: ${JSON.stringify(deploy.id)}, status: ${JSON.stringify(statuses)}[state.polls++] }
fs.writeFileSync(statePath, JSON.stringify(state))
if (args.includes("--output")) fs.writeFileSync(args[args.indexOf("--output") + 1], JSON.stringify(body))
else process.stdout.write(JSON.stringify(body))
if (args.includes("--write-out")) process.stdout.write(${JSON.stringify(String(http))})
`, { mode: 0o755 })
  writeFileSync(join(directory, "sleep"), "#!/bin/sh\nexit 0\n", { mode: 0o755 })
  const result = spawnSync("bash", ["-euo", "pipefail", "-c", script], {
    cwd: root, encoding: "utf8", timeout: 5000,
    env: { ...process.env, PATH: `${directory}:${process.env.PATH}`, RENDER_API_KEY: "test-key",
      TF_VAR_staging_api_service_id: "srv-image-reseed" },
  })
  assert.ifError(result.error)
  return { ...result, ...JSON.parse(readFileSync(statePath)) }
}

test("reseed redeploys the current immutable image without requiring a Git commit", t => {
  const result = replay(t, { statuses: ["update_in_progress", "live"] })
  assert.equal(result.status, 0, result.stdout + result.stderr)
  const posts = result.calls.filter(args => args.includes("POST"))
  assert.equal(posts.length, 1)
  assert.deepEqual(JSON.parse(posts[0][posts[0].indexOf("--data") + 1]), { imageUrl: image })
  assert.equal(result.polls, 2)
  assert.ok(result.calls.at(-1).includes(`https://api.render.com/v1/services/srv-image-reseed/deploys/${deploy.id}`))
})

test("reseed rejects a mutable image or missing live image before posting", t => {
  for (const live of [[], [{ deploy: { id: deploy.id } }], [{ deploy: { ...deploy, image: { ref: "ghcr.io/thomasluizon/orbit-api:main" } } }]]) {
    const result = replay(t, { live })
    assert.notEqual(result.status, 0)
    assert.equal(result.calls.filter(args => args.includes("POST")).length, 0)
  }
})

test("reseed rejects queued requests and mismatched image responses without polling", t => {
  for (const options of [{ http: 202 }, { http: 400 }, { http: 503 },
    { post: { ...deploy, image: { ref: image + "wrong" } } }, { post: { image: { ref: image } } }]) {
    const result = replay(t, options)
    assert.notEqual(result.status, 0)
    assert.equal(result.calls.filter(args => args.includes("POST")).length, 1)
    assert.equal(result.polls, 0)
  }
})

test("reseed fails on terminal or unknown deploy statuses", t => {
  for (const status of ["build_failed", "pre_deploy_failed", "update_failed", "canceled", "deactivated", "unknown"]) {
    const result = replay(t, { statuses: [status] })
    assert.notEqual(result.status, 0)
    assert.equal(result.polls, 1)
  }
})
