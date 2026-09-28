import { readFileSync } from "node:fs"
import { resolve } from "node:path"
import { fileURLToPath } from "node:url"

const guardedAddresses = new Set([
  "render_web_service.production_web",
  "render_web_service.staging_web",
])

const hasUnknown = value => value === true ||
  (value !== null && typeof value === "object" && Object.values(value).some(hasUnknown))

const changedAttributes = change => {
  const before = change.before ?? {}
  const after = change.after ?? {}
  const unknown = change.after_unknown ?? {}
  const names = new Set([...Object.keys(before), ...Object.keys(after), ...Object.keys(unknown)])
  return [...names].filter(name =>
    JSON.stringify(before[name]) !== JSON.stringify(after[name]) || hasUnknown(unknown[name]))
    .sort((left, right) => left.localeCompare(right))
}

export function checkPlan(plan) {
  if (!Array.isArray(plan?.resource_changes)) {
    throw new TypeError("Terraform plan JSON has no resource_changes array")
  }

  return plan.resource_changes.flatMap(resource => {
    if (!guardedAddresses.has(resource.address)) return []
    const actions = resource.change?.actions
    if (!Array.isArray(actions) || actions.length === 0) {
      throw new TypeError(`Terraform plan JSON has no actions for ${resource.address}`)
    }
    if (actions.length === 1 && ["create", "no-op", "read"].includes(actions[0])) return []

    const operation = actions.includes("delete") && actions.includes("create")
      ? "replacement"
      : actions.includes("delete") ? "deletion" : actions.includes("update") ? "in-place update" : actions.join(" then ")
    const attributes = changedAttributes(resource.change)
    return [`Blocked ${operation} of ${resource.address}: ${attributes.join(", ") || "attributes unavailable"}`]
  })
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.length !== 2) {
    process.stderr.write("Usage: terraform show -json <planfile> | node infra/check-web-plan.mjs\n")
    process.exitCode = 2
  } else {
    try {
      const issues = checkPlan(JSON.parse(readFileSync(0, "utf8")))
      if (issues.length) {
        process.stderr.write(`${issues.join("\n")}\n`)
        process.exitCode = 1
      } else {
        process.stdout.write("Web service plan guard passed.\n")
      }
    } catch (error) {
      process.stderr.write(`Web service plan guard could not inspect the plan: ${error.message}\n`)
      process.exitCode = 2
    }
  }
}
