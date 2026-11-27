#!/usr/bin/env node

import { execFileSync } from "node:child_process"
import { existsSync, readFileSync } from "node:fs"
import { join, resolve } from "node:path"
import { fileURLToPath } from "node:url"

export const HARNESS_LINE_BUDGET = 6176

const ALWAYS_LOADED = { bytes: 6000, lines: 200 }
const READ_PER_TASK = { bytes: 20000, lines: 500 }
const CONTENTS_LINE_THRESHOLD = 100
const HARNESS_PREFIXES = ["tools/", ".claude/", ".github/", "eslint-rules/"]
const HARNESS_FILES = new Set(["lefthook.yml"])
const SKIPPED_TOKEN = /[*?<>{}$\s]/

const countLines = (text) => {
  if (text.length === 0) return 0
  const parts = text.split("\n")
  return text.endsWith("\n") ? parts.length - 1 : parts.length
}

const isAlwaysLoaded = (file) =>
  /(^|\/)(CLAUDE|AGENTS)\.md$/.test(file) || /(^|\/)\.claude\/rules\//.test(file)

const isHarness = (file) =>
  HARNESS_FILES.has(file) || HARNESS_PREFIXES.some((prefix) => file.startsWith(prefix))

const trackedFiles = (root) =>
  execFileSync("git", ["ls-files", "-z"], { cwd: root, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 })
    .split("\0")
    .filter(Boolean)

const citedPaths = (markdown) => {
  const withoutFences = markdown.replace(/^(```|~~~)[^\n]*\n[\s\S]*?^\1[^\n]*$/gm, "")
  const tokens = []
  for (const match of withoutFences.matchAll(/`([^`\n]+)`/g)) tokens.push(match[1].trim())
  return tokens
}

const normalizeToken = (token) =>
  token
    .replace(/#.*$/, "")
    .replace(/:\d+(-\d+)?$/, "")
    .replace(/\/+$/, "")

export function findings(root, harnessLineBudget = HARNESS_LINE_BUDGET) {
  const files = trackedFiles(root).filter((file) => existsSync(join(root, file)))
  const fileSet = new Set(files)
  const topLevel = new Set(files.map((file) => file.split("/")[0]))
  const folders = new Set()
  for (const file of files) {
    const parts = file.split("/")
    for (let i = 1; i < parts.length; i++) folders.add(parts.slice(0, i).join("/"))
  }

  const found = []
  let harnessLines = 0

  for (const file of files) {
    const buffer = readFileSync(join(root, file))
    if (buffer.includes(0)) continue
    const text = buffer.toString("utf8")
    const lines = countLines(text)

    if (isHarness(file)) harnessLines += lines

    if (isAlwaysLoaded(file) || file.endsWith(".md")) {
      const budget = isAlwaysLoaded(file) ? ALWAYS_LOADED : READ_PER_TASK
      if (buffer.length > budget.bytes) {
        found.push(`${file}: ${buffer.length} B is over the ${budget.bytes} B budget`)
      }
      if (lines > budget.lines) {
        found.push(`${file}: ${lines} lines is over the ${budget.lines} line budget`)
      }
    }

    if (!file.endsWith(".md")) continue

    if (!isAlwaysLoaded(file) && lines > CONTENTS_LINE_THRESHOLD && !/^## Contents\s*$/m.test(text)) {
      found.push(`${file}: ${lines} lines and no "## Contents" heading`)
    }

    const reported = new Set()
    for (const token of citedPaths(text)) {
      if (SKIPPED_TOKEN.test(token)) continue
      const path = normalizeToken(token)
      if (path === "" || reported.has(path)) continue
      if (!topLevel.has(path.split("/")[0])) continue
      if (fileSet.has(path) || folders.has(path)) continue
      reported.add(path)
      found.push(`${file}: cited path \`${path}\` does not exist`)
    }
  }

  if (harnessLines > harnessLineBudget) {
    found.push(
      `harness: ${harnessLines} lines is over the budget of ${harnessLineBudget} lines. The harness line budget changes only by the owner's words.`,
    )
  }

  return found
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const result = findings(process.cwd())
  for (const line of result) console.error(line)
  process.exit(result.length === 0 ? 0 : 1)
}
