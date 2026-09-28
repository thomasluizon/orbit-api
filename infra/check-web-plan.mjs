import { execFileSync } from "node:child_process"
import { basename, dirname, resolve } from "node:path"
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
    .sort()
}

export function checkPlan(plan) {
  if (!Array.isArray(plan?.resource_changes)) {
    throw new Error("Terraform plan JSON has no resource_changes array")
  }

  return plan.resource_changes.flatMap(resource => {
    if (!guardedAddresses.has(resource.address)) return []
    if (JSON.stringify(resource.change?.actions) !== JSON.stringify(["update"])) return []
    const attributes = changedAttributes(resource.change)
    return [`Blocked in-place update to ${resource.address}: ${attributes.join(", ") || "attributes unavailable"}`]
  })
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const planFile = process.argv[2]
  if (!planFile || process.argv.length !== 3) {
    process.stderr.write("Usage: node infra/check-web-plan.mjs <planfile>\n")
    process.exitCode = 2
  } else {
    try {
      const absolutePlanFile = resolve(planFile)
      const output = execFileSync("terraform", ["show", "-json", basename(absolutePlanFile)], {
        cwd: dirname(absolutePlanFile),
        encoding: "utf8",
        maxBuffer: 64 * 1024 * 1024,
        stdio: ["ignore", "pipe", "pipe"],
      })
      const issues = checkPlan(JSON.parse(output))
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
