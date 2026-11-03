import assert from "node:assert/strict"
import { readFileSync } from "node:fs"
import { test } from "node:test"

const configuration = readFileSync(new URL("./production.tf", import.meta.url), "utf8")
const resource = configuration.match(/^resource "render_web_service" "production_api" \{([\s\S]*?)^\}/m)
assert.ok(resource, "Missing production API")
const body = resource[1].replace(/^\s*#.*$/gm, "")

function assertSingleInstance(source) {
  assert.match(source, /^\s*num_instances\s*=\s*1\s*$/m)
  assert.doesNotMatch(source, /^\s*(?:autoscaling|scaling)\s*(?:=(?!=)|\{)/m)
  assert.match(source, /self\.num_instances\s*==\s*1\s*&&\s*self\.autoscaling\s*==\s*null/)
}

test("production API pins one instance and rejects scaling in the plan", () => assertSingleInstance(body))
test("the guard rejects a second instance", () => {
  assert.throws(() => assertSingleInstance(body.replace(/num_instances\s*=\s*1/, "num_instances = 2")))
})
test("the guard rejects autoscaling", () => {
  assert.throws(() => assertSingleInstance(`${body}\nautoscaling = { enabled = true, min = 1, max = 2 }`))
})
