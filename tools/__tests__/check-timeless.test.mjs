import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"
import { copyFileSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { dirname, isAbsolute, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"
import { test } from "node:test"

const project = resolve(dirname(fileURLToPath(import.meta.url)), "../..")
const run = (root, args, input) => spawnSync(process.execPath, [join(root, "tools/check-timeless.mjs"), ...args], { cwd: root, input, encoding: "utf8" })
const git = (root, ...args) => {
  const result = spawnSync("git", args, { cwd: root, encoding: "utf8" })
  assert.equal(result.status, 0, result.stderr)
  return result.stdout.trim()
}
const make = (name, content = "clean\n") => {
  const root = mkdtempSync(join(tmpdir(), "orbit-timeless-"))
  mkdirSync(join(root, "tools"))
  mkdirSync(join(root, ".claude/hooks"), { recursive: true })
  copyFileSync(join(project, "tools/check-timeless.mjs"), join(root, "tools/check-timeless.mjs"))
  copyFileSync(join(project, ".claude/hooks/forbid-stale-text.mjs"), join(root, ".claude/hooks/forbid-stale-text.mjs"))
  writeFileSync(join(root, "tools/timeless-allowlist.json"), "[]\n")
  writeFileSync(join(root, name), content)
  git(root, "init", "-q", "--initial-branch=main")
  git(root, "config", "user.name", "Gate Test")
  git(root, "config", "user.email", "gate@test.invalid")
  git(root, "add", "tools/check-timeless.mjs", "tools/timeless-allowlist.json", name)
  git(root, "commit", "-qm", "base")
  return root
}
const hook = (root, name, content, command = "tools/check-timeless.mjs") => {
  const input = JSON.stringify({ tool_name: "Write", tool_input: { file_path: isAbsolute(name) ? name : join(root, name), content } })
  return command === "tools/check-timeless.mjs" ? run(root, ["--hook"], input)
    : spawnSync(process.execPath, [join(root, command)], { cwd: root, input, encoding: "utf8" })
}

const date = ["2026", "-08-12"].join("")
const owner = ["Tho", "mas"].join("")
const machine = ["/", "Users/example/work"].join("")
const cases = [
  ["machine-path", "sample.md", `Read ${machine}.\n`],
  ["owner-name", "sample.md", `Ask ${owner}.\n`],
  ["dated-anecdote", "sample.md", `On ${date}, it failed.\n`],
  ["comment-length", "sample.js", `${Array.from({ length: 7 }, (_, i) => `// reason ${i}`).join("\n")}\n`],
]

for (const [rule, name, planted] of cases) {
  test(`${rule} fails every mode and passes after removal`, () => {
    const root = make(name)
    try {
      const base = git(root, "rev-parse", "HEAD")
      assert.equal(hook(root, name, planted).status, 2)
      writeFileSync(join(root, name), planted)
      git(root, "add", name)
      assert.equal(run(root, ["--all"]).status, 1)
      assert.equal(run(root, ["--staged"]).status, 1)
      git(root, "commit", "-qm", "plant")
      assert.equal(run(root, ["--base", base]).status, 1)
      writeFileSync(join(root, name), "clean\n")
      git(root, "add", name)
      assert.equal(run(root, ["--all"]).status, 0)
      assert.equal(run(root, ["--staged"]).status, 0)
      git(root, "commit", "-qm", "remove")
      assert.equal(run(root, ["--base", base]).status, 0)
      assert.equal(hook(root, name, "clean\n").status, 0)
    } finally { rmSync(root, { recursive: true, force: true }) }
  })
}

test("six comments and dates in literals or JSON pass; a seventh comment fails", () => {
  const six = Array.from({ length: 6 }, (_, i) => `// reason ${i}`).join("\n")
  const root = make("sample.js", `const text = "${date}";\nconst pattern = /${date}/;\n${six}\n`)
  try {
    writeFileSync(join(root, "sample.json"), JSON.stringify({ date }))
    git(root, "add", "sample.json")
    assert.equal(run(root, ["--all"]).status, 0)
    const seven = `const text = "${date}";\nconst pattern = /${date}/;\n${six}\n// reason 6\n`
    assert.equal(hook(root, "sample.js", seven).status, 2)
    writeFileSync(join(root, "sample.js"), seven)
    git(root, "add", "sample.js")
    assert.equal(run(root, ["--staged"]).status, 1)
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("the entry point rejects a planted edit and ignores scratchpad files", () => {
  const root = make("sample.md")
  try {
    const edit = { file_path: join(root, "sample.md"), old_string: "clean", new_string: owner }
    const input = JSON.stringify({ tool_name: "Edit", tool_input: edit })
    const entry = ".claude/hooks/forbid-stale-text.mjs"
    assert.equal(spawnSync(process.execPath, [join(root, entry)], { cwd: root, input, encoding: "utf8" }).status, 2)
    const scratch = join(tmpdir(), "orbit-timeless-scratchpad.txt")
    assert.equal(hook(root, scratch, machine, entry).status, 0)
    assert.equal(hook(root, "sample.md", "clean\n", entry).status, 0)
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("an unused allowlist entry fails the full scan", () => {
  const root = make("sample.md")
  try {
    writeFileSync(join(root, "tools/timeless-allowlist.json"), JSON.stringify([{ path: "sample.md", rule: "owner-name", match: owner, reason: "unused" }]))
    git(root, "add", "tools/timeless-allowlist.json")
    const result = run(root, ["--all"])
    assert.equal(result.status, 1)
    assert.match(result.stderr, /unused allowlist/)
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("the Guards and Lefthook commands reject planted text and pass its removal", () => {
  const root = make("sample.md")
  try {
    const base = git(root, "rev-parse", "HEAD")
    git(root, "update-ref", "refs/remotes/origin/main", base)
    writeFileSync(join(root, "sample.md"), `Ask ${owner}.\n`)
    git(root, "add", "sample.md")
    const staged = run(root, ["--staged"])
    assert.equal(staged.status, 1)
    assert.match(staged.stderr, /sample\.md:1: owner-name/)
    git(root, "commit", "-qm", "plant")
    const guard = run(root, ["--base", "origin/main"])
    assert.equal(guard.status, 1)
    assert.match(guard.stderr, /sample\.md:1: owner-name/)
    writeFileSync(join(root, "sample.md"), "clean\n")
    git(root, "add", "sample.md")
    assert.equal(run(root, ["--staged"]).status, 0)
    git(root, "commit", "-qm", "remove")
    assert.equal(run(root, ["--base", "origin/main"]).status, 0)
  } finally { rmSync(root, { recursive: true, force: true }) }
})
