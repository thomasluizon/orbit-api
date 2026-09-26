#!/usr/bin/env node
import { spawnSync } from "node:child_process"
import { existsSync, readFileSync, realpathSync } from "node:fs"
import { createRequire } from "node:module"
import { dirname, extname, join, relative, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..")
const SELF = fileURLToPath(import.meta.url)
const USAGE = "usage: check-timeless.mjs --all | --base <ref> | --staged | --hook"
const machine = new RegExp([
  "/" + "Users/", "[A-Za-z]:\\\\+Users", "/private/" + "tmp", "/private/" + "var", "/var/" + "folders",
  "(?:^|[\\s'\"=(])/home/[a-z][^/\\s.]+/", "~/" + "Developer/",
].join("|"))
const owner = new RegExp(["\\bTho" + "mas\\b", "\\b(?:in )?(?:his|her) (?:own )?words\\b", "\\b(?:he|she) (?:said|asked|wrote|answered|decided)\\b"].join("|"))
const date = /\b(?:19|20)\d{2}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])\b/
const session = new RegExp("\\bses" + "sion[\\s:_-]*[a-f0-9]{8,}\\b", "i")
const prose = new Set([".md", ".mdx", ".txt"])
const js = new Set([".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".cs"])
const typedJs = new Set([".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx"])
const html = new Set([".html", ".htm", ".svg", ".xml", ".astro", ".csproj", ".fsproj", ".props", ".targets", ".resx", ".config", ".nuspec", ".slnx", ".runsettings", ".xaml", ".xsd", ".xslt", ".plist"])
const hash = new Set([".yml", ".yaml", ".sh", ".bash", ".zsh", ".toml", ".properties", ".editorconfig", ".gitignore"])
const parsers = new Map()

function git(args, cwd = ROOT) {
  const result = spawnSync("git", args, { cwd, encoding: "utf8", maxBuffer: 32 * 1024 * 1024 })
  if (result.error || result.status !== 0) throw new Error(result.stderr?.trim() || result.error?.message || `git exited ${result.status}`)
  return result.stdout
}

function typescript(root) {
  if (!parsers.has(root)) {
    const require = createRequire(join(root, "package.json"))
    try { parsers.set(root, require("typescript")) }
    catch (error) {
      if (error.code !== "MODULE_NOT_FOUND") throw error
      parsers.set(root, null)
    }
  }
  return parsers.get(root)
}

function parsedComments(text, path, parser) {
  const extension = extname(path).toLowerCase()
  const scriptKind = extension === ".jsx" ? parser.ScriptKind.JSX
    : extension === ".tsx" ? parser.ScriptKind.TSX
      : extension === ".ts" ? parser.ScriptKind.TS : parser.ScriptKind.JS
  const source = parser.createSourceFile(path, text, parser.ScriptTarget.Latest, true, scriptKind)
  const found = [], seen = new Set(), literalSpans = []
  const collect = (position) => {
    for (const range of [
      ...(parser.getLeadingCommentRanges(text, position) ?? []),
      ...(parser.getTrailingCommentRanges(text, position) ?? []),
    ]) {
      if (seen.has(range.pos)) continue
      seen.add(range.pos)
      const line = parser.getLineAndCharacterOfPosition(source, range.pos).line + 1
      const value = text.slice(range.pos, range.end)
      found.push({ line, position: range.pos, text: value, length: value.split("\n").length, singleLine: range.kind === parser.SyntaxKind.SingleLineCommentTrivia })
    }
  }
  const visit = (node) => {
    if ([parser.SyntaxKind.JsxText, parser.SyntaxKind.StringLiteral, parser.SyntaxKind.NoSubstitutionTemplateLiteral,
      parser.SyntaxKind.RegularExpressionLiteral, parser.SyntaxKind.TemplateHead, parser.SyntaxKind.TemplateMiddle,
      parser.SyntaxKind.TemplateTail].includes(node.kind)) literalSpans.push([node.getStart(source), node.getEnd()])
    if (node.kind !== parser.SyntaxKind.JsxText) collect(node.getFullStart())
    collect(node.getEnd())
    if (node.kind === parser.SyntaxKind.JsxExpression) collect(node.getStart(source) + 1)
    parser.forEachChild(node, visit)
  }
  visit(source)
  collect(source.endOfFileToken.getFullStart())
  return found.filter((item) => !literalSpans.some(([start, end]) => item.position >= start && item.position < end))
}

function comments(text, path, root) {
  const extension = extname(path).toLowerCase()
  const lines = text.split("\n")
  const found = []
  let markup = text
  if (extension === ".astro") {
    const regions = []
    const opening = /^---[ \t]*\r?\n/.exec(text)
    if (opening) {
      const closing = /^---[ \t]*\r?$/gm
      closing.lastIndex = opening[0].length
      const fence = closing.exec(text)
      if (fence) regions.push([opening[0].length, fence.index])
    }
    const withoutFrontmatter = regions.reduce((body, [start, end]) =>
      body.slice(0, start) + body.slice(start, end).replace(/[^\r\n]/g, " ") + body.slice(end), text)
    const scripts = /<script\b(?:[^>"']|"[^"]*"|'[^']*')*>([\s\S]*?)<\/script(?:[\t\n\f\r /][^>]*)?>/gi
    for (const match of withoutFrontmatter.matchAll(scripts)) {
      const openingTag = /^<script\b(?:[^>"']|"[^"]*"|'[^']*')*>/i.exec(match[0])[0]
      const start = match.index + openingTag.length
      regions.push([start, start + match[1].length])
    }
    if (regions.length) {
      const parser = typescript(root)
      if (!parser) throw new Error(`TypeScript is required to check ${path}`)
      for (const [start, end] of regions) {
        const offset = text.slice(0, start).split("\n").length - 1
        found.push(...parsedComments(text.slice(start, end), `${path}.ts`, parser)
          .map((item) => ({ ...item, line: item.line + offset })))
      }
      markup = regions.reduce((body, [start, end]) =>
        body.slice(0, start) + body.slice(start, end).replace(/[^\r\n]/g, " ") + body.slice(end), text)
    }
  }
  if (html.has(extension)) {
    const pattern = /<!--([\s\S]*?)-->/g
    for (const match of markup.matchAll(pattern)) {
      const line = markup.slice(0, match.index).split("\n").length
      found.push({ line, text: match[1], length: match[0].split("\n").length })
    }
  } else if (hash.has(extension) || path.endsWith(".gitignore")) {
    lines.forEach((line, index) => {
      let quote = ""
      for (let i = 0; i < line.length; i++) {
        const char = line[i]
        if (quote) {
          let slashes = 0
          while (line[i - 1 - slashes] === "\\") slashes++
          if (char === quote && (quote === "'" || slashes % 2 === 0)) quote = ""
        }
        else if (char === "'" || char === '"') quote = char
        else if (char === "#") { found.push({ line: index + 1, text: line.slice(i), length: 1 }); break }
      }
    })
  } else if (js.has(extension)) {
    const parser = typedJs.has(extension) ? typescript(root) : null
    if (parser) found.push(...parsedComments(text, path, parser))
    else {
      if (extension === ".jsx" || extension === ".tsx") throw new Error(`TypeScript is required to check ${path}`)
      let state = "code", start = 0, value = "", previous = ""
      for (let i = 0, line = 1; i < text.length; i++) {
        const char = text[i], next = text[i + 1]
        if (state === "line") {
          if (char === "\n") { found.push({ line: start, text: value, length: 1, singleLine: true }); state = "code"; value = ""; line++ }
          else value += char
          continue
        }
        if (state === "block") {
          if (char === "*" && next === "/") { found.push({ line: start, text: value, length: line - start + 1 }); i++; state = "code"; value = "" }
          else { value += char; if (char === "\n") line++ }
          continue
        }
        if (state !== "code") {
          if (char === "\\") { i++; if (text[i] === "\n") line++; continue }
          if (char === state) state = "code"
          if (char === "\n") line++
          continue
        }
        if (char === "\n") { line++; continue }
        if (char === "'" || char === '"' || char === "`") { state = char; continue }
        if (char === "/" && next === "/") { state = "line"; start = line; value = ""; i++; continue }
        if (char === "/" && next === "*") { state = "block"; start = line; value = ""; i++; continue }
        if (char === "/" && /[=(:,!\[{?]|\breturn$/.test(previous)) { state = "/"; continue }
        if (!/\s/.test(char)) previous = char
      }
      if (state === "line" || state === "block") found.push({ line: start, text: value, length: lines.length - start + 1, singleLine: state === "line" })
    }
  }
  if (js.has(extension) || extension === ".astro") {
    let group = []
    for (const item of found.filter((item) => item.length === 1 && (item.singleLine ?? item.text.startsWith("//"))).sort((a, b) => a.line - b.line)) {
      if (group.length && item.line !== group.at(-1).line + 1) { if (group.length > 6) found.push({ line: group[0].line, text: group.map((part) => part.text).join("\n"), length: group.length, block: true }); group = [] }
      group.push(item)
    }
    if (group.length > 6) found.push({ line: group[0].line, text: group.map((part) => part.text).join("\n"), length: group.length, block: true })
  }
  return found
}

function findings(path, content, root = ROOT) {
  if (content.includes("\0")) return []
  const lines = content.split("\n"), results = []
  for (let index = 0; index < lines.length; index++) {
    for (const [rule, pattern] of [["machine-path", machine], ["owner-name", owner]]) {
      for (const hit of lines[index].matchAll(new RegExp(pattern.source, pattern.flags + "g"))) {
        results.push({ path, line: index + 1, rule, match: hit[0] })
      }
    }
  }
  const sections = prose.has(extname(path).toLowerCase())
    ? lines.map((text, index) => ({ line: index + 1, text, length: 1 }))
    : comments(content, path, root)
  for (const section of sections) {
    for (const [rule, pattern] of [["dated-anecdote", date], ["dated-anecdote", session]]) {
      for (const hit of section.text.matchAll(new RegExp(pattern.source, pattern.flags + "g"))) {
        results.push({ path, line: section.line + section.text.slice(0, hit.index).split("\n").length - 1, rule, match: hit[0] })
      }
    }
    if ((js.has(extname(path).toLowerCase()) || extname(path).toLowerCase() === ".astro") && section.length > 6 && (section.block || section.text.includes("\n"))) {
      results.push({ path, line: section.line, end: section.line + section.length - 1, rule: "comment-length", match: `${section.length} lines` })
    }
  }
  return results
}

function addedLines(diff) {
  const selected = new Map()
  let path = null, line = 0
  for (const row of diff.split("\n")) {
    if (row.startsWith("+++ b/")) path = row.slice(6)
    const header = /^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@/.exec(row)
    if (header) { line = Number(header[1]); continue }
    if (!path || row.startsWith("+++")) continue
    if (row.startsWith("+")) { if (!selected.has(path)) selected.set(path, new Set()); selected.get(path).add(line++) }
    else if (row.startsWith(" ")) line++
  }
  return selected
}

function allowlist(root) {
  const path = join(root, "tools", "timeless-allowlist.json")
  const entries = existsSync(path) ? JSON.parse(readFileSync(path, "utf8")) : []
  if (!Array.isArray(entries) || entries.some((entry) =>
    !entry || typeof entry.path !== "string" || !entry.path || typeof entry.reason !== "string" || !entry.reason ||
    !["machine-path", "owner-name", "dated-anecdote", "comment-length"].includes(entry.rule) ||
    (entry.scope === "directory"
      ? entry.match !== null || !entry.path.endsWith("/") || !["owner-name", "dated-anecdote"].includes(entry.rule) ||
        (entry.exclude !== undefined && (!Array.isArray(entry.exclude) ||
          entry.exclude.some((prefix) => typeof prefix !== "string" || !prefix.startsWith(entry.path) || !prefix.endsWith("/"))))
      : entry.scope !== undefined || typeof entry.match !== "string" || !entry.match))) {
    throw new Error("tools/timeless-allowlist.json requires valid exact or directory-scoped entries")
  }
  return entries
}

function report(results, allowed, checkUnused) {
  const used = new Set(), failures = []
  for (const item of results) {
    const index = allowed.findIndex((entry) => entry.rule === item.rule && (entry.scope === "directory"
      ? item.path.startsWith(entry.path) && !(entry.exclude ?? []).some((prefix) => item.path.startsWith(prefix))
      : entry.path === item.path && item.match.includes(entry.match)))
    if (index < 0) failures.push(`${item.path}:${item.line}: ${item.rule}: ${item.match}`)
    else used.add(index)
  }
  if (checkUnused) allowed.forEach((entry, index) => { if (!used.has(index)) failures.push(`unused allowlist: ${entry.path} ${entry.rule} ${entry.match}`) })
  failures.forEach((failure) => console.error(failure))
  return failures.length ? 1 : 0
}

function hook() {
  const payload = JSON.parse(readFileSync(0, "utf8"))
  const fileInput = (value) => {
    if (!value || typeof value !== "object") return null
    if (typeof value.file_path === "string") return value
    for (const child of Object.values(value)) {
      const found = fileInput(child)
      if (found) return found
    }
    return null
  }
  const input = fileInput(payload)
  if (!input) return 0
  const absoluteTarget = resolve(process.cwd(), input.file_path)
  let directory = dirname(absoluteTarget)
  while (!existsSync(directory) && dirname(directory) !== directory) directory = dirname(directory)
  const rootResult = spawnSync("git", ["-C", directory, "rev-parse", "--show-toplevel"], { encoding: "utf8" })
  if (rootResult.status !== 0) return 0
  const root = rootResult.stdout.trim(), checker = join(root, "tools", "check-timeless.mjs")
  if (!existsSync(checker)) return 0
  if (checker !== SELF) {
    const result = spawnSync(process.execPath, [checker, "--hook"], { input: JSON.stringify(payload), encoding: "utf8" })
    if (result.stderr) process.stderr.write(result.stderr)
    return result.status ?? 2
  }
  const canonicalTarget = existsSync(absoluteTarget) ? realpathSync(absoluteTarget)
    : resolve(realpathSync(directory), relative(directory, absoluteTarget))
  const path = relative(realpathSync(root), canonicalTarget).replaceAll("\\", "/")
  const current = existsSync(absoluteTarget) ? readFileSync(absoluteTarget, "utf8") : ""
  const pairs = (value) => {
    if (!value || typeof value !== "object") return []
    if (typeof value.old_string === "string" && typeof value.new_string === "string") return [value]
    return Object.values(value).flatMap(pairs)
  }
  const isWrite = typeof input.content === "string"
  const edits = isWrite ? [{ old_string: current, new_string: input.content }] : pairs(input)
  const added = new Set(), oldLines = new Set()
  for (const edit of edits) {
    String(edit.old_string ?? "").split("\n").forEach((line) => oldLines.add(line))
    String(edit.new_string ?? "").split("\n").forEach((line) => { if (!oldLines.has(line)) added.add(line) })
  }
  const proposed = isWrite ? input.content
    : edits.reduce((body, edit) => body.replace(String(edit.old_string ?? ""), String(edit.new_string ?? "")), current)
  const lines = proposed.split("\n")
  const results = findings(path, proposed, root).filter((item) => {
    if (item.rule === "comment-length") return lines.slice(item.line - 1, item.end).some((line) => added.has(line))
    return added.has(lines[item.line - 1])
  })
  return report(results, allowlist(root), false) ? 2 : 0
}

try {
  const args = process.argv.slice(2)
  if (args.length === 1 && ["--help", "-h"].includes(args[0])) { console.log(USAGE); process.exit(0) }
  if (args.length === 1 && args[0] === "--hook") process.exit(hook())
  if (!((args.length === 1 && ["--all", "--staged"].includes(args[0])) || (args.length === 2 && args[0] === "--base"))) {
    console.error(USAGE); process.exit(2)
  }
  let selected
  if (args[0] === "--all") selected = null
  else if (args[0] === "--staged") selected = addedLines(git(["diff", "--cached", "--unified=0", "--diff-filter=ACMRT"]))
  else {
    const base = git(["merge-base", args[1], "HEAD"]).trim()
    selected = addedLines(git(["diff", "--unified=0", "--diff-filter=ACMRT", base, "HEAD"]))
  }
  const paths = selected ? [...selected.keys()] : git(["ls-files", "-z"]).split("\0").filter(Boolean)
  const results = []
  for (const path of paths) {
    const absolute = join(ROOT, path)
    if (!selected && !existsSync(absolute)) continue
    const content = !selected ? readFileSync(absolute, "utf8")
      : args[0] === "--staged" ? git(["show", `:${path}`]) : git(["show", `HEAD:${path}`])
    for (const item of findings(path, content)) {
      if (!selected || selected.get(path)?.has(item.line) || (item.rule === "comment-length" && [...selected.get(path)].some((line) => line >= item.line && line <= item.end))) results.push(item)
    }
  }
  process.exit(report(results, allowlist(ROOT), !selected))
} catch (error) {
  console.error(error.message)
  process.exit(2)
}
