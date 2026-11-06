#!/usr/bin/env node

import { spawnSync } from "node:child_process"
import { readFileSync } from "node:fs"
import { dirname, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const USAGE = `usage: check-attribution.mjs [--range <base>..<head>] [--body-file <path>] [--root <repository>]

  Fails when a commit message in the range or the pull request body carries an attribution line:
  a co-author trailer (a line that starts with "Co-Authored-By:", in any letter case) or a line
  that starts with "Generated with", after any emoji or punctuation. A mention of either string
  inside a sentence is not an attribution line, so a rule that names them still passes.

  --range <base>..<head>  the pull request's commits, as a git revision range
  --body-file <path>      the pull request body, as a text file
  --root <repository>     read commits from this repository instead of the script's repository
  --help, -h              print this usage and exit 0

  Give --range, --body-file, or both.

exit codes: 0 no attribution line, 1 an attribution line was found, 2 usage error or unreadable input`

const ATTRIBUTION_LINES = [
  { label: "a co-author trailer", pattern: /^\s*co-authored-by\s*:/i },
  { label: "a generated-with line", pattern: /^[^\p{L}\p{N}]*generated with\b/iu },
]

/** One `<source> line <n>: <label>: <line>` finding per attribution line in `text`. */
const attributionFindings = (source, text) => text.split(/\r?\n/).flatMap((line, index) =>
  ATTRIBUTION_LINES.filter(({ pattern }) => pattern.test(line)).map(({ label }) => `${source} line ${index + 1}: ${label}: ${line.trim()}`))

const fail = (message) => {
  console.error(`${message}\n\n${USAGE}`)
  process.exit(2)
}

const parseArguments = (argv) => {
  const options = { range: null, bodyFile: null, root: resolve(dirname(fileURLToPath(import.meta.url)), "..") }
  for (let index = 0; index < argv.length; index++) {
    const flag = argv[index]
    if (flag === "--help" || flag === "-h") {
      console.log(USAGE)
      process.exit(0)
    }
    const key = { "--range": "range", "--body-file": "bodyFile", "--root": "root" }[flag]
    if (key === undefined) fail(`unknown argument: ${flag}`)
    const value = argv[index + 1]
    if (typeof value !== "string" || value === "" || value.startsWith("--")) fail(`${flag} requires a value`)
    options[key] = key === "range" ? value : resolve(value)
    index++
  }
  if (options.range === null && options.bodyFile === null) fail("--range or --body-file is required")
  if (options.range !== null && (!/^[^\s.]\S*\.\.[^\s.]\S*$/.test(options.range) || options.range.includes("..."))) {
    fail(`--range must be <base>..<head>, got "${options.range}"`)
  }
  return options
}

/** Every commit in the range as `{ sha, message }`. A range git cannot read is a usage error. */
const commitMessages = (root, range) => {
  const result = spawnSync("git", ["-C", root, "log", "--format=%H%x00%B%x1e", range, "--"], { encoding: "utf8", windowsHide: true })
  if (result.error || result.status !== 0) fail(`git log ${range} failed: ${(result.stderr || result.error?.message || "").trim()}`)
  return result.stdout.split("\x1e").map((record) => record.replace(/^\n/, "")).filter(Boolean).map((record) => {
    const separator = record.indexOf("\0")
    return { sha: record.slice(0, separator), message: record.slice(separator + 1) }
  })
}

const readBody = (path) => {
  try {
    return readFileSync(path, "utf8")
  } catch (error) {
    return fail(`cannot read --body-file ${path}: ${error.message}`)
  }
}

const options = parseArguments(process.argv.slice(2))
const commits = options.range === null ? [] : commitMessages(options.root, options.range)
const problems = [
  ...commits.flatMap(({ sha, message }) => attributionFindings(`commit ${sha.slice(0, 12)}`, message)),
  ...(options.bodyFile === null ? [] : attributionFindings("pull request body", readBody(options.bodyFile))),
]
if (problems.length > 0) {
  console.error("Attribution lines are not allowed in pull request commits or the pull request body:")
  for (const problem of problems) console.error(`  ${problem}`)
  console.error("Remove each line: reword the commit or edit the pull request body.")
  process.exit(1)
}
const checked = [options.range === null ? null : `commits ${options.range}`, options.bodyFile === null ? null : "the pull request body"]
console.log(`Attribution guard passed for ${checked.filter(Boolean).join(" and ")}.`)
