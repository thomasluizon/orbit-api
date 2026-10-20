const url = process.argv[2]
const expected = process.env.COMMIT_SHA
const commitPattern = /^[a-f0-9]{40}$/

if (!url || !commitPattern.test(expected ?? "")) {
  console.error("Release health requires a health URL and a full COMMIT_SHA")
  process.exit(1)
}

const started = performance.now()
const deadline = started + 180_000
let attempts = 0
let lastStatus = "<unknown>"
let lastCommit = "<unknown>"
let lastResult = "no-response"
let matched = false

while (performance.now() < deadline) {
  const timeout = Math.floor(Math.min(10_000, deadline - performance.now()))
  if (timeout < 1) break
  attempts += 1
  try {
    const response = await fetch(url, { signal: AbortSignal.timeout(timeout), redirect: "error" })
    if (response.ok) {
      const health = await response.json()
      const status = health?.status
      const commit = health?.commit
      if (typeof status === "string" && /^[A-Za-z][A-Za-z0-9_-]{0,31}$/.test(status)) lastStatus = status
      if (typeof commit === "string" && commitPattern.test(commit)) lastCommit = commit
      lastResult = "health-mismatch"
      if (status === "Healthy" && commit === expected && performance.now() < deadline) {
        lastResult = "healthy-target"
        matched = true
        break
      }
    } else {
      lastResult = "http-error"
      await response.body?.cancel()
    }
  } catch {
    lastResult = "request-or-json-error"
  }

  const remaining = deadline - performance.now()
  if (remaining > 0) await new Promise(resolve => setTimeout(resolve, Math.min(5000, remaining)))
}

const elapsed = ((performance.now() - started) / 1000).toFixed(3)
const diagnostic = `expected=${expected} status=${lastStatus} commit=${lastCommit} attempts=${attempts} elapsed=${elapsed}s result=${lastResult}`
if (matched) {
  console.log(`Release health converged: ${diagnostic}`)
} else {
  console.error(`Release health deadline exhausted: ${diagnostic}`)
  process.exitCode = 1
}
