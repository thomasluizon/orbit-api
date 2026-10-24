import assert from "node:assert/strict"
import { readFileSync } from "node:fs"
import { test } from "node:test"

for (const environment of ["production", "staging"]) {
  const configuration = readFileSync(new URL(`./${environment}.tf`, import.meta.url), "utf8")
  for (const service of ["api", "web"]) {
    const name = `${environment}_${service}`
    test(`${name} carries the shutdown allowance supported by its plan`, () => {
      const resource = configuration.match(new RegExp(`^resource "render_web_service" "${name}" \\{([\\s\\S]*?)^\\}`, "m"))
      assert.ok(resource, `Missing ${name}`)
      if (environment === "production") {
        assert.doesNotMatch(resource[1], /\bplan\s*=\s*"free"/)
        assert.match(resource[1], /^\s*max_shutdown_delay_seconds\s*=\s*300\s*$/m)
      } else {
        assert.match(resource[1], /\bplan\s*=\s*"free"/)
        assert.doesNotMatch(resource[1], /^\s*max_shutdown_delay_seconds\s*=/m)
      }
    })
  }
}
