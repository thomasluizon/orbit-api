#!/usr/bin/env node
// The cross-repo dash ban (REBUILD.md 6.1.1). Em dashes are banned everywhere;
// en dashes are banned except inside a numeric range (1–10). ESLint cannot be
// the home: the rule spans a TS monorepo, a .NET solution, and an Astro site,
// so this ONE script is vendored into all three repos and wired as a CI job
// (diff-scoped via --files), a lefthook pre-commit step (staged files), and a
// full-tree baseline ratchet (--check-baseline / --write-baseline).
//
// Usage:
//   node tools/check-dashes.mjs --files <path>...   check exactly these files (exit 1 on any hit)
//   node tools/check-dashes.mjs --check-baseline    full tree vs tools/dash-baseline.json (exit 1 on growth)
//   node tools/check-dashes.mjs --write-baseline    regenerate tools/dash-baseline.json
//   node tools/check-dashes.mjs --text "<string>"   check a string (PR titles/bodies; exit 1 on any hit)

import { execFileSync } from "node:child_process"
import { readFileSync, writeFileSync, existsSync } from "node:fs"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..")
const BASELINE_PATH = join(REPO_ROOT, "tools", "dash-baseline.json")

const SKIP = [
  /(^|\/)node_modules\//,
  /(^|\/)\.git\//,
  /(^|\/)\.claude\//,
  /(^|\/)\.opencode\//,
  /(^|\/)dist\//,
  /(^|\/)coverage\//,
  /(^|\/)\.next\//,
  /(^|\/)\.expo\//,
  /package-lock\.json$/,
  /\.(png|jpg|jpeg|gif|webp|ico|woff2?|ttf|otf|eot|pdf|zip|jar|keystore|p8|p12|mp4|webm|svg)$/i,
  /(^|\/)tools\/check-dashes\.mjs$/,
  /(^|\/)tools\/dash-baseline\.json$/,
]

const shouldSkip = (file) => {
  const normalized = file.replaceAll("\\", "/")
  return SKIP.some((pattern) => pattern.test(normalized))
}

export function dashFindings(text) {
  const allowedEnDash = new Set()
  for (const match of text.matchAll(/\d\s?–\s?\d/g)) {
    allowedEnDash.add(match.index + match[0].indexOf("–"))
  }
  const findings = []
  for (const match of text.matchAll(/[—–]/g)) {
    if (match[0] === "–" && allowedEnDash.has(match.index)) continue
    const line = text.slice(0, match.index).split("\n").length
    const snippet = text
      .slice(Math.max(0, match.index - 25), match.index + 26)
      .replace(/\s+/g, " ")
      .trim()
    findings.push({ line, kind: match[0] === "—" ? "em dash" : "en dash", snippet })
  }
  return findings
}

const checkFiles = (files) => {
  const perFile = new Map()
  for (const file of files) {
    if (shouldSkip(file)) continue
    const absolute = resolve(REPO_ROOT, file)
    if (!existsSync(absolute)) continue
    let text
    try {
      text = readFileSync(absolute, "utf8")
    } catch {
      continue
    }
    if (text.includes("\u0000")) continue
    const findings = dashFindings(text)
    if (findings.length) perFile.set(file.replaceAll("\\", "/"), findings)
  }
  return perFile
}

const trackedFiles = () =>
  execFileSync("git", ["ls-files"], { cwd: REPO_ROOT, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 })
    .split("\n")
    .filter(Boolean)

const printFindings = (perFile, limitPerFile = 3) => {
  for (const [file, findings] of perFile) {
    console.error(`${file}: ${findings.length}`)
    for (const finding of findings.slice(0, limitPerFile)) {
      console.error(`  L${finding.line} ${finding.kind}: …${finding.snippet}…`)
    }
  }
}

const HOW_TO_FIX =
  "\nEm dashes are a banned LLM tell in Orbit prose. Use a plain hyphen (-), a comma, or restructure.\nEn dashes are allowed only inside a numeric range (1–10).\nPre-existing debt lives in tools/dash-baseline.json and may only shrink (regenerate with --write-baseline AFTER removing dashes, never to admit new ones)."

const mode = process.argv[2]

if (mode === "--text") {
  const findings = dashFindings(process.argv.slice(3).join(" "))
  if (findings.length) {
    console.error(`Banned dash in the given text:`)
    for (const finding of findings) console.error(`  ${finding.kind}: …${finding.snippet}…`)
    console.error(HOW_TO_FIX)
    process.exit(1)
  }
  process.exit(0)
}

if (mode === "--files") {
  const files = process.argv.slice(3)
  const baseline = existsSync(BASELINE_PATH) ? JSON.parse(readFileSync(BASELINE_PATH, "utf8")) : {}
  const perFile = checkFiles(files)
  const offending = new Map(
    [...perFile].filter(([file, findings]) => findings.length > (baseline[file] ?? 0)),
  )
  if (offending.size) {
    console.error("Banned dash written into files (count exceeds the committed baseline):")
    printFindings(offending)
    console.error(HOW_TO_FIX)
    process.exit(1)
  }
  process.exit(0)
}

if (mode === "--check-baseline" || mode === "--write-baseline") {
  const perFile = checkFiles(trackedFiles())
  const current = Object.fromEntries([...perFile].map(([file, findings]) => [file, findings.length]).sort((a, b) => a[0].localeCompare(b[0])))
  if (mode === "--write-baseline") {
    writeFileSync(BASELINE_PATH, JSON.stringify(current, null, 2) + "\n")
    console.log(`dash-baseline.json written: ${Object.keys(current).length} files, ${Object.values(current).reduce((a, b) => a + b, 0)} dashes`)
    process.exit(0)
  }
  const baseline = existsSync(BASELINE_PATH) ? JSON.parse(readFileSync(BASELINE_PATH, "utf8")) : {}
  const grew = Object.entries(current).filter(([file, count]) => count > (baseline[file] ?? 0))
  if (grew.length) {
    console.error("Dash count grew beyond the baseline:")
    for (const [file, count] of grew) console.error(`  ${file}: ${count} (baseline ${baseline[file] ?? 0})`)
    console.error(HOW_TO_FIX)
    process.exit(1)
  }
  process.exit(0)
}

console.error("usage: check-dashes.mjs --files <f>... | --check-baseline | --write-baseline | --text <s>")
process.exit(2)
