import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"
import { readFileSync } from "node:fs"
import { dirname, join } from "node:path"
import { fileURLToPath } from "node:url"
import { test } from "node:test"
import { checkPlan } from "./check-web-plan.mjs"

const root = dirname(fileURLToPath(import.meta.url))
const fixture = name => JSON.parse(readFileSync(join(root, "fixtures", name), "utf8"))

test("rejects an in-place web update and names changed attributes", () => {
  const issues = checkPlan(fixture("web-update.json"))
  assert.equal(issues.length, 1)
  assert.match(issues[0], /render_web_service\.staging_web/)
  assert.match(issues[0], /health_check_path/)
  assert.match(issues[0], /runtime_source/)
})

test("allows web creation", () => {
  assert.deepEqual(checkPlan(fixture("web-create.json")), [])
})

test("allows an unrelated service update", () => {
  assert.deepEqual(checkPlan(fixture("unrelated-update.json")), [])
})

test("guards the production address and rejects replacement", () => {
  const update = fixture("web-update.json")
  update.resource_changes[0].address = "render_web_service.production_web"
  assert.match(checkPlan(update)[0], /render_web_service\.production_web/)
  update.resource_changes[0].change.actions = ["delete", "create"]
  assert.match(checkPlan(update)[0], /replacement/)
  update.resource_changes[0].change.actions = ["create", "delete"]
  assert.match(checkPlan(update)[0], /replacement/)
})

test("rejects deletion of a web service", () => {
  const deletion = fixture("web-update.json")
  deletion.resource_changes[0].change.actions = ["delete"]
  deletion.resource_changes[0].change.after = null
  assert.match(checkPlan(deletion)[0], /deletion of render_web_service\.staging_web/)
})

test("reads Terraform plan JSON from standard input", () => {
  const run = name => spawnSync(process.execPath, [join(root, "check-web-plan.mjs")], {
    input: readFileSync(join(root, "fixtures", name), "utf8"),
    encoding: "utf8",
  })
  const update = run("web-update.json")
  assert.equal(update.status, 1)
  assert.match(update.stderr, /Blocked in-place update/)
  const creation = run("web-create.json")
  assert.equal(creation.status, 0)
  assert.match(creation.stdout, /guard passed/)
})
