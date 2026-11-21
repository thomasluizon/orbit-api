#!/usr/bin/env node

import { readFileSync, existsSync } from "node:fs"
import { dirname, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..")

const SKIP = [
  /(^|\/)node_modules\//,
  /(^|\/)\.git\//,
  /(^|\/)dist\//,
  /(^|\/)coverage\//,
  /(^|\/)\.next\//,
  /(^|\/)\.expo\//,
  /(^|\/)Migrations\//,
  /package-lock\.json$/,
  /\.(png|jpg|jpeg|gif|webp|ico|woff2?|ttf|otf|eot|pdf|zip|jar|keystore|p8|p12|mp4|webm|svg)$/i,
  /(^|\/)tools\/check-dashes\.mjs$/,
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

const HOW_TO_FIX =
  "\nEm dashes are a banned LLM tell in Orbit prose. Use a plain hyphen (-), a comma, or restructure.\nEn dashes are allowed only inside a numeric range (1–10)."

if (process.argv[2] !== "--files") {
  console.error("usage: check-dashes.mjs --files <f>...")
  process.exit(2)
}

let failed = false
for (const file of process.argv.slice(3)) {
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
  if (!findings.length) continue
  if (!failed) console.error("Banned dash written into files:")
  failed = true
  console.error(`${file.replaceAll("\\", "/")}: ${findings.length}`)
  for (const finding of findings.slice(0, 3)) {
    console.error(`  L${finding.line} ${finding.kind}: …${finding.snippet}…`)
  }
}
if (failed) {
  console.error(HOW_TO_FIX)
  process.exit(1)
}
