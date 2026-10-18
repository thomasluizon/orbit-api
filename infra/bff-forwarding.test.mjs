import assert from "node:assert/strict"
import { readFileSync } from "node:fs"
import { dirname, join } from "node:path"
import { fileURLToPath } from "node:url"
import { test } from "node:test"

const root = dirname(fileURLToPath(import.meta.url))
const configuration = readFileSync(join(root, "configuration.tf"), "utf8")
const variables = readFileSync(join(root, "variables.tf"), "utf8")

function block(header) {
  const start = configuration.indexOf(header)
  assert.notEqual(start, -1, `Missing ${header}`)
  const opening = configuration.indexOf("{", start + header.length)
  let depth = 0
  for (let index = opening; index < configuration.length; index++) {
    if (configuration[index] === "{") depth++
    if (configuration[index] === "}" && --depth === 0) return configuration.slice(opening + 1, index)
  }
  assert.fail(`Unclosed ${header}`)
}

test("BFF forwarding secrets are generated per environment without operator inputs", () => {
  assert.doesNotMatch(variables, /variable\s+"(?:production|staging)_bff_forwarding_secret"/)
  const password = block('resource "random_password" "bff_forwarding_secret"')
  assert.match(password, /for_each\s*=\s*toset\(\["production", "staging"\]\)/)
  assert.match(password, /length\s*=\s*64\b/)
  assert.match(password, /special\s*=\s*false\b/)
  assert.doesNotMatch(password, /keepers|timestamp\(|plantimestamp\(/)
})

test("generated BFF forwarding secrets are persisted as environment SecureStrings", () => {
  const parameter = block('resource "aws_ssm_parameter" "bff_forwarding_secret"')
  assert.match(parameter, /for_each\s*=\s*random_password\.bff_forwarding_secret/)
  assert.match(parameter, /name\s*=\s*"\/orbit\/\$\{each\.key\}\/api\/Auth__BffForwardingSecret"/)
  assert.match(parameter, /type\s*=\s*"SecureString"/)
  assert.match(parameter, /value\s*=\s*each\.value\.result/)
})

for (const environment of ["production", "staging"]) {
  test(`${environment} API and web share their persisted forwarding secret`, () => {
    for (const [service, key] of [["api", "Auth__BffForwardingSecret"], ["web", "BFF_FORWARDING_SECRET"]]) {
      const group = block(`resource "render_env_group" "${environment}_${service}"`)
      assert.match(group, new RegExp(`${key}\\s*=\\s*\\{\\s*value\\s*=\\s*aws_ssm_parameter\\.bff_forwarding_secret\\["${environment}"\\]\\.value\\s*\\}`))
      assert.doesNotMatch(group, /var\.(?:production|staging)_bff_forwarding_secret/)
    }
  })
}
