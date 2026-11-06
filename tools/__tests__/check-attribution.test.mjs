import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"
import { mkdtempSync, rmSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"
import { after, test } from "node:test"

const script = resolve(dirname(fileURLToPath(import.meta.url)), "../check-attribution.mjs")

/** Built from parts, so this source file never holds an attribution line of its own. */
const TRAILER = ["Co", "Authored", "By: Claude Opus 5.5 <noreply@anthropic.com>"].join("-")
const GENERATED = `\u{1F916} ${["Generated", "with"].join(" ")} [Claude Code](https://claude.com/claude-code)`
const RULE = "Never add a `Co-Authored-By:` trailer or a \"Generated with\" line to a commit."

const scratch = mkdtempSync(join(tmpdir(), "orbit-attribution-"))
after(() => rmSync(scratch, { recursive: true, force: true }))

const run = (args) => spawnSync(process.execPath, [script, ...args], { encoding: "utf8" })
const git = (root, ...args) => {
  const result = spawnSync("git", ["-C", root, ...args], { encoding: "utf8" })
  assert.equal(result.status, 0, result.stderr)
  return result.stdout.trim()
}

/**
 * Commits through `git commit-tree`, which runs no hook, so a local commit-msg hook that strips
 * trailers cannot clean the fixture before the guard reads it.
 */
const commitRange = (label, messages) => {
  const root = join(scratch, label)
  git(scratch, "init", "-q", "--initial-branch=main", root)
  git(root, "config", "user.name", "Gate Test")
  git(root, "config", "user.email", "gate@test.invalid")
  const tree = git(root, "hash-object", "-w", "-t", "tree", "/dev/null")
  const base = git(root, "commit-tree", tree, "-m", "base")
  let head = base
  for (const message of messages) head = git(root, "commit-tree", tree, "-p", head, "-m", message)
  return ["--root", root, "--range", `${base}..${head}`]
}

const bodyFile = (label, text) => {
  const path = join(scratch, `${label}.md`)
  writeFileSync(path, text)
  return ["--body-file", path]
}

test("clean commits that name the rule pass", () => {
  const result = run(commitRange("clean", ["feat(tools): add the attribution guard\n\nThe guard refuses both lines.", `docs: state the rule\n\n${RULE}`]))
  assert.equal(result.status, 0, result.stderr)
  assert.match(result.stdout, /Attribution guard passed for commits [0-9a-f]{40}\.\.[0-9a-f]{40}\./)
})

test("a co-author trailer in any commit fails and names the commit line", () => {
  const result = run(commitRange("trailer", ["feat: one", `fix: two\n\nBody text.\n\n${TRAILER}`]))
  assert.equal(result.status, 1)
  assert.match(result.stderr, /commit [0-9a-f]{12} line 5: a co-author trailer: Co-Authored-By: Claude/)
})

test("an indented trailer in lower case fails", () => {
  const result = run(commitRange("lowercase", [`fix: three\n\n  ${TRAILER.toLowerCase()}`]))
  assert.equal(result.status, 1)
  assert.match(result.stderr, /a co-author trailer: co-authored-by: claude/)
})

test("a generated-with line after an emoji in a commit fails", () => {
  const result = run(commitRange("generated", [`feat: four\n\n${GENERATED}`]))
  assert.equal(result.status, 1)
  assert.match(result.stderr, /commit [0-9a-f]{12} line 3: a generated-with line: /)
})

test("a clean body that names the rule passes", () => {
  const result = run(bodyFile("clean-body", `## Summary\r\n\r\n${RULE}\r\n\r\nTracks thomasluizon/orbit-tickets#2040\r\n`))
  assert.equal(result.status, 0, result.stderr)
  assert.match(result.stdout, /Attribution guard passed for the pull request body\./)
})

test("a body with a co-author trailer after CRLF lines fails and names its line", () => {
  const result = run(bodyFile("trailer-body", `## Summary\r\n\r\n${TRAILER}\r\n`))
  assert.equal(result.status, 1)
  assert.match(result.stderr, new RegExp(`pull request body line 3: a co-author trailer: ${TRAILER.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}\\n`))
})

test("a body with a generated-with line fails", () => {
  const result = run(bodyFile("generated-body", `## Summary\n\nWork.\n\n${GENERATED}\n`))
  assert.equal(result.status, 1)
  assert.match(result.stderr, /pull request body line 5: a generated-with line: /)
})

test("each attribution line across commits and body is one finding", () => {
  const result = run([...commitRange("both", [`fix: five\n\n${TRAILER}\n${GENERATED}`]), ...bodyFile("both-body", `${TRAILER}\n`)])
  assert.equal(result.status, 1)
  assert.equal(result.stderr.split("\n").filter((line) => /^ {2}(commit|pull request body) /.test(line)).length, 3)
})

test("a clean range and a clean body pass together", () => {
  const result = run([...commitRange("clean-pair", ["chore: six"]), ...bodyFile("clean-pair-body", "Clean body.\n")])
  assert.equal(result.status, 0, result.stderr)
  assert.match(result.stdout, /for commits \S+ and the pull request body\./)
})

test("usage errors exit 2", () => {
  const clean = commitRange("usage", ["chore: seven"])
  const root = clean.slice(0, 2)
  for (const [args, message] of [
    [root, /--range or --body-file is required/],
    [[...root, "--range", "main...HEAD"], /--range must be/],
    [[...root, "--range", "missing..HEAD"], /git log missing\.\.HEAD failed/],
    [["--body-file", join(scratch, "absent.md")], /cannot read --body-file/],
    [["--body-file"], /--body-file requires a value/],
    [["--unknown", "x"], /unknown argument: --unknown/],
  ]) {
    const result = run(args)
    assert.equal(result.status, 2, `${args.join(" ")}: ${result.stdout}`)
    assert.match(result.stderr, message)
  }
})
