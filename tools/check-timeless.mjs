#!/usr/bin/env node
import { spawnSync } from "node:child_process"
import { existsSync, lstatSync, readFileSync, readlinkSync, realpathSync } from "node:fs"
import { createRequire } from "node:module"
import { dirname, extname, join, relative, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..")
const SELF = fileURLToPath(import.meta.url)
const ALLOWLIST = "tools/timeless-allowlist.json"
const CHECKER = "tools/check-timeless.mjs"
const SCANNED_MODES = new Set(["100644", "100755", "120000"])
const USAGE = "usage: check-timeless.mjs --all | --base <ref> | --staged | --hook"
const [LEFT_SINGLE, RIGHT_SINGLE, LEFT_DOUBLE, RIGHT_DOUBLE] = ["\u2018", "\u2019", "\u201C", "\u201D"]
const ESCAPE = /\\u[0-9a-fA-F]{4}/, NON_ASCII = /[\u0080-\uffff]/
const machine = new RegExp([
  "/" + "Users/", "[A-Za-z]:\\\\+Users", "/private/" + "tmp", "/private/" + "var", "/var/" + "folders",
  "(?:^|[\\s'\"=(])/home/[a-z][^/\\s.]+/", "~/" + "Developer/",
].join("|"))
const owner = new RegExp(["\\bTho" + "mas\\b", "\\b(?:in )?(?:his|her) (?:own )?words\\b", "\\b(?:he|she) (?:said|asked|wrote|answered|decided)\\b"].join("|"))
const date = /\b(?:19|20)\d{2}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])(?=\b|T)/
const session = new RegExp("\\bses" + "sion[\\s:_-]*[a-f0-9]{8,}\\b", "i")
const prose = new Set([".md", ".mdx", ".txt"])
const js = new Set([".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".cs"])
const typedJs = new Set([".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx"])
const html = new Set([".html", ".htm", ".svg", ".xml", ".astro", ".csproj", ".fsproj", ".props", ".targets", ".resx", ".config", ".nuspec", ".slnx", ".runsettings", ".xaml", ".xsd", ".xslt", ".plist"])
const hash = new Set([".yml", ".yaml", ".sh", ".bash", ".zsh", ".toml", ".properties", ".editorconfig", ".gitignore"])
const parsers = new Map()

function git(args, options = {}) {
  const result = spawnSync("git", args, { cwd: ROOT, encoding: "utf8", maxBuffer: 32 * 1024 * 1024, ...options })
  if (result.error || result.status !== 0) throw new Error(result.stderr?.toString().trim() || result.error?.message || `git exited ${result.status}`)
  return result.stdout
}

function blobs(specs) {
  const output = git(["cat-file", "--batch", "-Z"], { input: Buffer.from(specs.map((spec) => `${spec}\0`).join("")), encoding: "buffer", maxBuffer: 1024 * 1024 * 1024 })
  const contents = []
  let offset = 0
  for (const spec of specs) {
    const headerEnd = output.indexOf(0, offset)
    const header = output.toString("utf8", offset, Math.max(headerEnd, offset)).split(" ")
    if (headerEnd >= 0 && header.at(-1) === "missing") { contents.push(null); offset = headerEnd + 1; continue }
    const end = headerEnd + 1 + Number(header[2])
    if (headerEnd < 0 || header.length !== 3 || header[1] !== "blob" || !/^\d+$/.test(header[2]) || output[end] !== 0) {
      throw new Error(`git cat-file returned an unexpected record for ${JSON.stringify(spec)}`)
    }
    contents.push(output.toString("utf8", headerEnd + 1, end))
    offset = end + 1
  }
  return contents
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
  const visitLiterals = (node) => {
    if ([parser.SyntaxKind.JsxText, parser.SyntaxKind.StringLiteral, parser.SyntaxKind.NoSubstitutionTemplateLiteral,
      parser.SyntaxKind.RegularExpressionLiteral, parser.SyntaxKind.TemplateHead, parser.SyntaxKind.TemplateMiddle,
      parser.SyntaxKind.TemplateTail].includes(node.kind)) literalSpans.push([node.getStart(source), node.getEnd()])
    parser.forEachChild(node, visitLiterals)
  }
  visitLiterals(source)
  const visitTokens = (node) => {
    const children = node.getChildren(source)
    if (children.length) { children.forEach(visitTokens); return }
    if (node.kind === parser.SyntaxKind.JsxText) return
    collect(node.getFullStart())
    collect(node.getEnd())
  }
  visitTokens(source)
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

function changedPaths(args) {
  const parts = git(["diff", "--no-renames", "--raw", "-z", ...args]).split("\0")
  const paths = []
  for (let index = 0; index + 1 < parts.length; index += 2) {
    if (!parts[index]) continue
    const [oldMode, newMode] = parts[index].slice(1).split(" ")
    paths.push({ path: parts[index + 1], oldMode, newMode })
  }
  return paths
}

function newFindings(path, before, after, root = ROOT) {
  const key = (item) => JSON.stringify([item.rule, item.match])
  const counts = new Map()
  for (const item of findings(path, before, root)) counts.set(key(item), (counts.get(key(item)) ?? 0) + 1)
  return findings(path, after, root).filter((item) => {
    const count = counts.get(key(item)) ?? 0
    if (count) counts.set(key(item), count - 1)
    return !count
  })
}

function allowlist(text) {
  const entries = text === null ? [] : JSON.parse(text)
  if (!Array.isArray(entries) || entries.some((entry) =>
    !entry || typeof entry.path !== "string" || !entry.path || typeof entry.reason !== "string" || !entry.reason ||
    !["machine-path", "owner-name", "dated-anecdote", "comment-length"].includes(entry.rule) ||
    (entry.scope === "directory"
      ? entry.match !== null || !entry.path.endsWith("/") || entry.rule === "machine-path" ||
        (entry.exclude !== undefined && (!Array.isArray(entry.exclude) ||
          entry.exclude.some((prefix) => typeof prefix !== "string" || !prefix.startsWith(entry.path) || !prefix.endsWith("/"))))
      : entry.scope !== undefined || typeof entry.match !== "string" || !entry.match))) {
    throw new Error("tools/timeless-allowlist.json requires valid exact or directory-scoped entries")
  }
  return entries
}

function covers(entry, path) {
  return entry.scope === "directory"
    ? path.startsWith(entry.path) && !(entry.exclude ?? []).some((prefix) => path.startsWith(prefix))
    : entry.path === path
}

function allowedIndex(allowed, item) {
  return allowed.findIndex((entry) => entry.rule === item.rule && covers(entry, item.path) &&
    (entry.scope === "directory" || item.match.includes(entry.match)))
}

function report(results, allowed, checked, usage = results) {
  const failures = results.filter((item) => allowedIndex(allowed, item) < 0)
    .map((item) => `${item.path}:${item.line}: ${item.rule}: ${item.match}`)
  const used = new Set(usage.map((item) => allowedIndex(allowed, item)))
  for (const index of checked) {
    const entry = allowed[index]
    if (!used.has(index)) failures.push(`unused allowlist: ${entry.path} ${entry.rule} ${entry.match}`)
  }
  failures.forEach((failure) => console.error(failure))
  return failures.length ? 1 : 0
}

function changedEntryUsage(allowed, touched, tracked, scan) {
  const checked = [...allowed.keys()].filter((index) => touched.some((path) => covers(allowed[index], path)))
  if (!checked.length) return { checked, usage: [] }
  return { checked, usage: scan(tracked().filter((path) => checked.some((index) => covers(allowed[index], path)))) }
}

function straightQuotes(text) {
  return text.replaceAll(LEFT_SINGLE, "'").replaceAll(RIGHT_SINGLE, "'").replaceAll(LEFT_DOUBLE, '"').replaceAll(RIGHT_DOUBLE, '"')
}

function decodeEscapes(text) {
  return text.replace(/(\\\\)|\\u([0-9a-fA-F]{4})/g, (whole, backslashes, hex) => backslashes === undefined ? String.fromCharCode(Number.parseInt(hex, 16)) : whole)
}

function escapedLength(text) {
  let nonAscii = 0
  for (let index = 0; index < text.length; index++) if (text.charCodeAt(index) >= 128) nonAscii++
  return text.length + 5 * nonAscii
}

function trailingBackslashes(text, end) {
  let count = 0
  while (text[end - 1 - count] === "\\") count++
  return count
}

function escapedMatch(search, content) {
  if (trailingBackslashes(search, search.length) % 2 === 1) return null
  const starts = search[0] === "\\" ? [0] : []
  let source = "", offset = 0
  for (let index = 0; index < search.length; index++) {
    const code = search.charCodeAt(index)
    if (code < 128) { source += search[index].replace(/[.*+?^${}()|[\]\\]/g, "\\$&"); offset += 1; continue }
    starts.push(offset)
    offset += 6
    source += "\\\\u"
    for (const digit of code.toString(16).padStart(4, "0")) source += digit >= "a" ? `[${digit}${digit.toUpperCase()}]` : digit
  }
  const pattern = new RegExp(source, "g")
  for (let found = pattern.exec(content); found !== null; found = pattern.exec(content)) {
    if (starts.every((start) => trailingBackslashes(content, found.index + start) % 2 === 0)) return content.indexOf(found[0]) === found.index ? found[0] : null
    pattern.lastIndex = found.index + 1
  }
  return null
}

function actualOldString(content, search) {
  if (content.includes(search)) return search
  const at = straightQuotes(content).indexOf(straightQuotes(search))
  if (at !== -1) return content.substring(at, at + search.length)
  if (ESCAPE.test(search)) {
    const decoded = decodeEscapes(search)
    if (decoded !== search && content.includes(decoded)) return decoded
  }
  if (!NON_ASCII.test(search) || escapedLength(search) > content.length || !content.includes("\\u")) return null
  return escapedMatch(search, content)
}

function opensQuote(characters, index) {
  return index === 0 || [" ", "\t", "\n", "\r", "(", "[", "{", "\u2014", "\u2013"].includes(characters[index - 1])
}

function quoteStyle(search, actual, replacement) {
  if (search === actual) return replacement
  let characters = [...replacement]
  if (actual.includes(LEFT_DOUBLE) || actual.includes(RIGHT_DOUBLE)) {
    characters = characters.map((character, index, all) => character !== '"' ? character : opensQuote(all, index) ? LEFT_DOUBLE : RIGHT_DOUBLE)
  }
  if (actual.includes(LEFT_SINGLE) || actual.includes(RIGHT_SINGLE)) {
    characters = characters.map((character, index, all) => {
      if (character !== "'") return character
      if (/\p{L}/u.test(all[index - 1] ?? "") && /\p{L}/u.test(all[index + 1] ?? "")) return RIGHT_SINGLE
      return opensQuote(all, index) ? LEFT_SINGLE : RIGHT_SINGLE
    })
  }
  return characters.join("")
}

function escapeStyle(search, actual, replacement) {
  if (search === actual) return replacement
  if (NON_ASCII.test(search) && escapedLength(search) === actual.length && escapedMatch(search, actual) !== null) {
    const digits = new Map()
    let upper = 0, lower = 0
    for (let index = 0, offset = 0; index < search.length; index++) {
      if (search.charCodeAt(index) < 128) { offset += 1; continue }
      const hex = actual.slice(offset + 2, offset + 6)
      digits.set(search.charCodeAt(index), hex)
      for (const digit of hex) {
        if (digit >= "a" && digit <= "f") lower++
        else if (digit >= "A" && digit <= "F") upper++
      }
      offset += 6
    }
    return replacement.replace(/[\u0080-\uffff]/g, (character) => {
      const kept = digits.get(character.charCodeAt(0)), hex = character.charCodeAt(0).toString(16).padStart(4, "0")
      return `\\u${kept ?? (upper > lower ? hex.toUpperCase() : hex)}`
    })
  }
  return ESCAPE.test(search) && decodeEscapes(search) === actual ? decodeEscapes(replacement) : replacement
}

function applyEdit(content, { old_string: search, new_string: replacement, replace_all: all }) {
  const actual = actualOldString(content, search) || search
  const written = escapeStyle(search, actual, quoteStyle(search, actual, replacement))
  if (!actual) return written
  const target = !written && !actual.endsWith("\n") && content.includes(`${actual}\n`) ? `${actual}\n` : actual
  return all ? content.split(target).join(written) : content.replace(target, () => written)
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
  const root = rootResult.stdout.trim(), checker = join(root, CHECKER)
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
  const sample = current.slice(0, 4096), crlf = sample.split("\r\n").length - 1
  const edited = pairs(input).reduce(applyEdit, current.replaceAll("\r\n", "\n"))
  const proposed = typeof input.content === "string" ? input.content
    : crlf > sample.split("\n").length - 1 - crlf ? edited.replaceAll("\r\n", "\n").split("\n").join("\r\n") : edited
  const allowlistPath = join(root, ALLOWLIST)
  const allowed = allowlist(existsSync(allowlistPath) ? readFileSync(allowlistPath, "utf8") : null)
  return report(newFindings(path, current, proposed, root), allowed, []) ? 2 : 0
}

try {
  const args = process.argv.slice(2)
  if (args.length === 1 && ["--help", "-h"].includes(args[0])) { console.log(USAGE); process.exit(0) }
  if (args.length === 1 && args[0] === "--hook") process.exit(hook())
  if (!((args.length === 1 && ["--all", "--staged"].includes(args[0])) || (args.length === 2 && args[0] === "--base"))) {
    console.error(USAGE); process.exit(2)
  }
  let changes = null, oldRef
  if (args[0] === "--staged") {
    oldRef = "HEAD"
    changes = changedPaths(["--cached"])
  }
  else if (args[0] === "--base") {
    oldRef = git(["merge-base", args[1], "HEAD"]).trim()
    changes = changedPaths([oldRef, "HEAD"])
  }
  const state = args[0] === "--staged" ? ":" : "HEAD:"
  const contents = (paths) => changes ? blobs(paths.map((path) => state + path))
    : paths.map((path) => {
      const absolute = join(ROOT, path), stat = lstatSync(absolute, { throwIfNoEntry: false })
      if (stat?.isSymbolicLink()) return readlinkSync(absolute, "utf8")
      return stat?.isFile() ? readFileSync(absolute, "utf8") : null
    })
  const scan = (paths) => {
    const texts = contents(paths)
    return paths.flatMap((path, index) => texts[index] === null ? [] : findings(path, texts[index]))
  }
  const tracked = () => changes
    ? (args[0] === "--base" ? git(["ls-tree", "-r", "-z", "HEAD"]) : git(["ls-files", "-s", "-z"])).split("\0")
      .filter((entry) => SCANNED_MODES.has(entry.slice(0, entry.indexOf(" ")))).map((entry) => entry.slice(entry.indexOf("\t") + 1))
    : git(["ls-files", "-z"]).split("\0").filter(Boolean)
  const allowed = allowlist(contents([ALLOWLIST])[0])
  if (!changes || changes.some(({ path }) => path === ALLOWLIST || path === CHECKER)) process.exit(report(scan(tracked()), allowed, allowed.keys()))
  const results = []
  for (const { path, oldMode } of changes.filter(({ newMode }) => SCANNED_MODES.has(newMode))) {
    const before = SCANNED_MODES.has(oldMode) ? git(["show", `${oldRef}:${path}`]) : ""
    results.push(...newFindings(path, before, git(["show", state + path])))
  }
  const { checked, usage } = changedEntryUsage(allowed, changes.map(({ path }) => path), tracked, scan)
  process.exit(report(results, allowed, checked, usage))
} catch (error) {
  console.error(error.message)
  process.exit(2)
}
