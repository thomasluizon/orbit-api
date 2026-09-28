import assert from "node:assert/strict"
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

test("guards the production address and allows replacement", () => {
  const update = fixture("web-update.json")
  update.resource_changes[0].address = "render_web_service.production_web"
  assert.match(checkPlan(update)[0], /render_web_service\.production_web/)
  update.resource_changes[0].change.actions = ["delete", "create"]
  assert.deepEqual(checkPlan(update), [])
})
