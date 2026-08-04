#!/usr/bin/env node
/**
 * Gate repository-root entries by location, not by guessed filename patterns.
 *
 * Vendored from orbit-ui-mobile (ORB-170). The logic is identical in all three Orbit repositories;
 * only root-allowlist.json differs, because each repository's root differs.
 *
 * The allowlist is deliberately a closed set: every unlisted root file AND every unlisted root
 * directory fails regardless of its name, extension, or leading dot. Adding a legitimate root entry
 * therefore requires a visible data change instead of another pattern that only recognizes shapes
 * already seen.
 *
 * Entries that are gitignored are declared anyway when they legitimately appear on a real checkout:
 * a local .env, the IDE directories, and the test, mutation and benchmark output this repository's
 * own .gitignore already blesses. A gate that fires on those would block every commit, which was
 * measured in ORB-170 before .env was declared.
 */

import { readdirSync, readFileSync } from "node:fs"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const USAGE = `usage: check-root-allowlist.mjs

  Fails when the repository root contains a file or directory not named in root-allowlist.json.
  The .git worktree pointer is always ignored.

  --help, -h  print this usage and exit 0

exit codes: 0 every root entry is declared, 1 undeclared root entries exist, 2 usage or configuration error`

if (process.argv.includes("--help") || process.argv.includes("-h")) {
  console.log(USAGE)
  process.exit(0)
}

if (process.argv.length > 2) {
  console.error(`check-root-allowlist: takes no arguments, got: ${process.argv.slice(2).join(" ")}\n`)
  console.error(USAGE)
  process.exit(2)
}

const toolsDirectory = dirname(fileURLToPath(import.meta.url))
const allowlistPath = join(toolsDirectory, "root-allowlist.json")
const repositoryRoot = resolve(toolsDirectory, "..")

let allowlist
try {
  allowlist = JSON.parse(readFileSync(allowlistPath, "utf8"))
} catch (error) {
  console.error(`check-root-allowlist: cannot read ${allowlistPath}: ${error.message}`)
  process.exit(2)
}

const isNameList = (value) =>
  Array.isArray(value) &&
  value.every((name) => typeof name === "string" && name.length > 0) &&
  new Set(value).size === value.length

if (allowlist === null || typeof allowlist !== "object" || !isNameList(allowlist.files) || !isNameList(allowlist.directories)) {
  console.error(
    "check-root-allowlist: root-allowlist.json must be an object with `files` and `directories`, each an array of unique, non-empty names",
  )
  process.exit(2)
}

const allowedFiles = new Set(allowlist.files)
const allowedDirectories = new Set(allowlist.directories)

const undeclared = readdirSync(repositoryRoot, { withFileTypes: true })
  .filter((entry) => entry.name !== ".git")
  .filter((entry) => (entry.isDirectory() ? !allowedDirectories.has(entry.name) : !allowedFiles.has(entry.name)))
  .map((entry) => (entry.isDirectory() ? `${entry.name}/` : entry.name))
  .sort()

if (undeclared.length > 0) {
  console.error("Root allowlist violation: declare or remove these repository-root entries:")
  for (const name of undeclared) console.error(`  ${name}`)
  process.exit(1)
}
