import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from "node:fs"
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
  mkdirSync(dirname(join(root, name)), { recursive: true })
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

test("XML build files have their comments checked", () => {
  for (const name of ["Sample.csproj", "Directory.Build.props", "nuget.config"]) {
    const root = make(name, `<Project>\n  <!-- ${date} -->\n</Project>\n`)
    try {
      const all = run(root, ["--all"])
      assert.equal(all.status, 1)
      assert.match(all.stderr, new RegExp(`${name.replace(".", "\\.")}:2: dated-anecdote`))
    } finally { rmSync(root, { recursive: true, force: true }) }
  }
})

test("an even number of backslashes closes a YAML string before a dated comment", () => {
  const root = make("sample.yml", String.raw`value: "foo\\\\" # ${date}` + "\n")
  try {
    const result = run(root, ["--all"])
    assert.equal(result.status, 1)
    assert.match(result.stderr, /sample\.yml:1: dated-anecdote/)
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("staged content is checked after its working copy is deleted", () => {
  const root = make("sample.md")
  try {
    writeFileSync(join(root, "sample.md"), `On ${date}, it failed.\n`)
    git(root, "add", "sample.md")
    rmSync(join(root, "sample.md"))
    const result = run(root, ["--staged"])
    assert.equal(result.status, 1)
    assert.match(result.stderr, /sample\.md:1: dated-anecdote/)
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("a dated comment inside an empty call fails the full scan", () => {
  const root = make("sample.ts", `run(/* On ${date}, it failed. */)\n`)
  try {
    const result = run(root, ["--all"])
    assert.equal(result.status, 1)
    assert.match(result.stderr, /sample\.ts:1: dated-anecdote/)
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("a Write hook rejects wrapping an existing date literal in a block comment", () => {
  const name = "sample.ts"
  const root = make(name, `const value = new Date("${date}")\n`)
  try {
    const result = hook(root, name, `/*\nconst value = new Date("${date}")\n*/\n`)
    assert.equal(result.status, 2)
    assert.match(result.stderr, /sample\.ts:2: dated-anecdote/)
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("moving an existing violating comment passes the staged diff", () => {
  const name = "sample.ts"
  const comment = `// On ${date}, it failed.\n`
  const root = make(name, `${comment}const value = 1\n`)
  try {
    writeFileSync(join(root, name), `const value = 1\n${comment}`)
    git(root, "add", name)
    const result = run(root, ["--staged"])
    assert.equal(result.status, 0, result.stderr)

  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("a directory entry may exempt comment-length but never machine-path", () => {
  const root = make("sample.md")
  try {
    mkdirSync(join(root, "export"))
    writeFileSync(join(root, "export/example.ts"), Array.from({ length: 7 }, (_, index) => `// export note ${index}`).join("\n") + "\n")
    git(root, "add", "export/example.ts")
    writeFileSync(join(root, "tools/timeless-allowlist.json"), JSON.stringify([{ path: "export/", rule: "comment-length", match: null, scope: "directory", reason: "generated export" }]))
    assert.equal(run(root, ["--all"]).status, 0)
    writeFileSync(join(root, "tools/timeless-allowlist.json"), JSON.stringify([{ path: "export/", rule: "machine-path", match: null, scope: "directory", reason: "never allowed" }]))
    const refused = run(root, ["--all"])
    assert.equal(refused.status, 2)
    assert.match(refused.stderr, /valid exact or directory-scoped entries/)

  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("a comment after an escaped backslash in a YAML string fails", () => {
  const root = make("sample.yml", "value: clean\n")
  try {
    writeFileSync(join(root, "sample.yml"), `value: "foo\\\\" # ${date}\n`)
    git(root, "add", "sample.yml")
    const all = run(root, ["--all"])
    assert.equal(all.status, 1)
    assert.ok(all.stderr.includes("sample.yml:1: dated-anecdote"))
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("--staged checks a staged file whose working copy is gone", () => {
  const root = make("sample.md")
  try {
    writeFileSync(join(root, "sample.md"), `Ask ${owner}.\n`)
    git(root, "add", "sample.md")
    rmSync(join(root, "sample.md"))
    const staged = run(root, ["--staged"])
    assert.equal(staged.status, 1)
    assert.ok(staged.stderr.includes("sample.md:1: owner-name"))
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("a comment inside an empty call is checked", () => {
  const root = make("sample.mjs")
  try {
    writeFileSync(join(root, "sample.mjs"), `run(/* ${date} */)\n`)
    git(root, "add", "sample.mjs")
    const all = run(root, ["--all"])
    assert.equal(all.status, 1)
    assert.ok(all.stderr.includes("sample.mjs:1: dated-anecdote"))
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("the hook reports findings an edit introduces, not lines it keeps", () => {
  const line = `const value = new Date("${date}T12:00:00.000Z")`
  const root = make("sample.mjs", `${line}\n`)
  try {
    const wrapped = hook(root, "sample.mjs", `/*\n${line}\n*/\n`)
    assert.equal(wrapped.status, 2)
    writeFileSync(join(root, "sample.mjs"), `// ${date}\nconst other = 1\n`)
    git(root, "add", "sample.mjs")
    git(root, "commit", "-qm", "existing comment")
    const moved = hook(root, "sample.mjs", `const other = 1\n// ${date}\n`)
    assert.equal(moved.status, 0)
  } finally { rmSync(root, { recursive: true, force: true }) }
})

const exportEntries = ["owner-name", "dated-anecdote"].map((rule) => ({ path: "export/", rule, match: null, scope: "directory", reason: "generated export" }))
const unusedDate = "unused allowlist: export/ dated-anecdote null"
const ownerFinding = (name) => `${name}:1: owner-name: ${owner}`
const exportFixture = () => {
  const root = make("export/kept.md", `Ask ${owner}.\n`)
  writeFileSync(join(root, "export/only.md"), `On ${date}, the export ran.\n`)
  writeFileSync(join(root, "tools/timeless-allowlist.json"), JSON.stringify(exportEntries))
  git(root, "add", "export/only.md", "tools/timeless-allowlist.json")
  git(root, "commit", "-qm", "allow")
  return { root, base: git(root, "rev-parse", "HEAD") }
}
const writeAllowlist = (root, entries) => {
  writeFileSync(join(root, "tools/timeless-allowlist.json"), JSON.stringify(entries))
  git(root, "add", "tools/timeless-allowlist.json")
}
const touchChecker = (root) => {
  const file = join(root, "tools/check-timeless.mjs")
  writeFileSync(file, `${readFileSync(file, "utf8")}// note\n`)
  git(root, "add", "tools/check-timeless.mjs")
}

test("deleting the only file an allowlist entry covers fails staged and base", () => {
  const { root, base } = exportFixture()
  try {
    assert.equal(run(root, ["--all"]).status, 0)
    git(root, "rm", "-q", "export/only.md")
    const staged = run(root, ["--staged"])
    assert.equal(staged.status, 1)
    assert.ok(staged.stderr.includes(unusedDate), staged.stderr)
    git(root, "commit", "-qm", "delete")
    const guard = run(root, ["--base", base])
    assert.equal(guard.status, 1)
    assert.ok(guard.stderr.includes(unusedDate), guard.stderr)
    assert.ok(!guard.stderr.includes("owner-name"), guard.stderr)
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("a pull request outside every entry scope skips the unused check", () => {
  const { root } = exportFixture()
  try {
    git(root, "rm", "-q", "export/only.md")
    git(root, "commit", "-qm", "delete")
    writeFileSync(join(root, "other.md"), "clean\n")
    git(root, "add", "other.md")
    git(root, "commit", "-qm", "unrelated")
    assert.equal(run(root, ["--base", "HEAD~1"]).status, 0)
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("removing the entry a deletion leaves unused passes base", () => {
  const { root, base } = exportFixture()
  try {
    git(root, "rm", "-q", "export/only.md")
    writeAllowlist(root, exportEntries.slice(0, 1))
    git(root, "commit", "-qm", "delete and drop entry")
    const guard = run(root, ["--base", base])
    assert.equal(guard.status, 0, guard.stderr)
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("a pull request that adds an unused entry fails base", () => {
  const { root, base } = exportFixture()
  try {
    writeAllowlist(root, [...exportEntries, { path: "notes.md", rule: "dated-anecdote", match: date, reason: "unused" }])
    git(root, "commit", "-qm", "add entry")
    const guard = run(root, ["--base", base])
    assert.equal(guard.status, 1)
    assert.ok(guard.stderr.includes(`unused allowlist: notes.md dated-anecdote ${date}`), guard.stderr)
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("moving the only covered file out of the scope fails base on the unused entry and the moved finding", () => {
  const { root, base } = exportFixture()
  try {
    mkdirSync(join(root, "docs"))
    git(root, "mv", "export/only.md", "docs/only.md")
    git(root, "commit", "-qm", "move")
    const guard = run(root, ["--base", base])
    assert.equal(guard.status, 1)
    assert.ok(guard.stderr.includes(unusedDate), guard.stderr)
    assert.ok(guard.stderr.includes("docs/only.md:1: dated-anecdote"), guard.stderr)
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("a checker change that leaves an entry unused fails base", () => {
  const { root, base } = exportFixture()
  try {
    const checker = join(root, "tools/check-timeless.mjs")
    const full = readFileSync(checker, "utf8")
    const withoutDateRule = full.replace('["dated-anecdote", date], ', "")
    assert.notEqual(withoutDateRule, full)
    writeFileSync(checker, withoutDateRule)
    git(root, "commit", "-qam", "narrow")
    const guard = run(root, ["--base", base])
    assert.equal(guard.status, 1)
    assert.ok(guard.stderr.includes(unusedDate), guard.stderr)
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("staged and base read the allowlist at the state they scan, not in the working tree", () => {
  const { root, base } = exportFixture()
  try {
    git(root, "rm", "-q", "export/only.md")
    writeFileSync(join(root, "tools/timeless-allowlist.json"), JSON.stringify(exportEntries.slice(0, 1)))
    const staged = run(root, ["--staged"])
    assert.equal(staged.status, 1)
    assert.ok(staged.stderr.includes(unusedDate), staged.stderr)
    git(root, "commit", "-qm", "delete")
    const guard = run(root, ["--base", base])
    assert.equal(guard.status, 1)
    assert.ok(guard.stderr.includes(unusedDate), guard.stderr)
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("an allowlist entry only in the working tree does not block a staged commit", () => {
  const { root } = exportFixture()
  try {
    writeFileSync(join(root, "tools/timeless-allowlist.json"), JSON.stringify([...exportEntries, { path: "notes.md", rule: "dated-anecdote", match: date, reason: "draft" }]))
    writeFileSync(join(root, "notes.md"), `On ${date}.\n`)
    touchChecker(root)
    const staged = run(root, ["--staged"])
    assert.equal(staged.status, 0, staged.stderr)
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("removing an allowlist entry that is still in use fails staged and base", () => {
  const { root, base } = exportFixture()
  try {
    writeAllowlist(root, exportEntries.slice(1))
    const staged = run(root, ["--staged"])
    git(root, "commit", "-qm", "drop used entry")
    const guard = run(root, ["--base", base])
    for (const result of [staged, guard]) {
      assert.equal(result.status, 1)
      assert.ok(result.stderr.includes(ownerFinding("export/kept.md")), result.stderr)
    }
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("a checker change that matches an unchanged file fails base", () => {
  const { root } = exportFixture()
  try {
    writeFileSync(join(root, "notes.md"), "Released 2025/01/02.\n")
    git(root, "add", "notes.md")
    git(root, "commit", "-qm", "notes")
    const base = git(root, "rev-parse", "HEAD")
    const checker = join(root, "tools/check-timeless.mjs")
    const hyphenOnly = readFileSync(checker, "utf8")
    const slashToo = hyphenOnly.replace("\\d{2}-(?:0[1-9]|1[0-2])-(", "\\d{2}[-/](?:0[1-9]|1[0-2])[-/](")
    assert.notEqual(slashToo, hyphenOnly)
    writeFileSync(checker, slashToo)
    git(root, "commit", "-qam", "widen")
    const guard = run(root, ["--base", base])
    assert.equal(guard.status, 1)
    assert.ok(guard.stderr.includes("notes.md:1: dated-anecdote: 2025/01/02"), guard.stderr)
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("a gitlink does not shift the later files in staged, all and base", () => {
  const root = make("a.md")
  try {
    const base = git(root, "rev-parse", "HEAD")
    git(root, "update-index", "--add", "--cacheinfo", `160000,${"1".repeat(40)},b-sub`)
    mkdirSync(join(root, "b-sub"))
    writeFileSync(join(root, "z.md"), `Ask ${owner}.\n`)
    git(root, "add", "z.md")
    touchChecker(root)
    const results = [run(root, ["--staged"]), run(root, ["--all"])]
    git(root, "commit", "-qm", "gitlink")
    results.push(run(root, ["--base", base]))
    for (const result of results) {
      assert.equal(result.status, 1, result.stderr)
      assert.equal(result.stderr.trim(), ownerFinding("z.md"))
    }
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("a path with a newline or a carriage return keeps its own content in staged, all and base", () => {
  const root = make("export/x.md", `On ${date}.\n`)
  try {
    writeFileSync(join(root, "tools/timeless-allowlist.json"), JSON.stringify(exportEntries.slice(1)))
    writeFileSync(join(root, "a\nb.md"), "clean\n")
    writeFileSync(join(root, "c\r"), `Ask ${owner}.\n`)
    git(root, "add", "-A")
    git(root, "commit", "-qm", "names")
    const base = git(root, "rev-parse", "HEAD")
    touchChecker(root)
    const results = [run(root, ["--staged"]), run(root, ["--all"])]
    git(root, "commit", "-qm", "checker")
    results.push(run(root, ["--base", base]))
    for (const result of results) {
      assert.equal(result.status, 1, result.stderr)
      assert.equal(result.stderr.trim(), ownerFinding("c\r"))
    }
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("a tracked symlink is scanned as its link text, never followed, in staged, all and base", () => {
  const root = make("export/kept.md", `Ask ${owner}.\n`)
  try {
    writeAllowlist(root, exportEntries.slice(0, 1))
    git(root, "commit", "-qm", "allow")
    const base = git(root, "rev-parse", "HEAD")
    mkdirSync(join(root, "docs"))
    symlinkSync("../export/kept.md", join(root, "docs/link.md"))
    symlinkSync("export", join(root, "exportlink"))
    symlinkSync(machine, join(root, "machine-link"))
    git(root, "add", "-A")
    const results = [run(root, ["--staged"])]
    touchChecker(root)
    results.push(run(root, ["--staged"]), run(root, ["--all"]))
    git(root, "commit", "-qm", "links")
    results.push(run(root, ["--base", base]))
    const linkFinding = `machine-link:1: machine-path: ${["/", "Users/"].join("")}`
    for (const result of results) {
      assert.equal(result.status, 1, result.stderr)
      assert.equal(result.stderr.trim(), linkFinding)
    }
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("a long comment that gets shorter but stays over the limit fails staged and base", () => {
  const block = (count, tag) => Array.from({ length: count }, (_, index) => `// ${tag} ${index}`).join("\n") + "\n"
  const root = make("src/a.js", `${block(12, "first")}const a = 1\n${block(12, "second")}`)
  try {
    writeAllowlist(root, [{ path: "src/a.js", rule: "comment-length", match: "12 lines", reason: "reviewed" }])
    git(root, "commit", "-qm", "allow")
    const base = git(root, "rev-parse", "HEAD")
    writeFileSync(join(root, "src/a.js"), `${block(9, "first")}const a = 1\n${block(12, "second")}`)
    git(root, "add", "src/a.js")
    const results = [run(root, ["--staged"])]
    git(root, "commit", "-qm", "shorten")
    results.push(run(root, ["--base", base]))
    for (const result of results) {
      assert.equal(result.status, 1, result.stderr)
      assert.ok(result.stderr.includes("src/a.js:1: comment-length: 9 lines"), result.stderr)
    }
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("an Edit hook with replace_all scans every replaced occurrence", () => {
  const root = make("sample.js", 'const word = "clean";\n// clean\n')
  try {
    const input = JSON.stringify({ tool_input: { file_path: join(root, "sample.js"), old_string: "clean", new_string: date, replace_all: true } })
    const result = run(root, ["--hook"], input)
    assert.equal(result.status, 2)
    assert.ok(result.stderr.includes(`sample.js:2: dated-anecdote: ${date}`), result.stderr)
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test("an Edit hook keeps a dollar pattern in the replacement literal", () => {
  const root = make("sample.md", `${owner} clean\n`)
  try {
    const edit = (new_string) => run(root, ["--hook"], JSON.stringify({ tool_input: { file_path: join(root, "sample.md"), old_string: "clean", new_string } }))
    assert.equal(edit("$`").status, 0)
    writeFileSync(join(root, "sample.md"), `clean ${owner}\n`)
    assert.equal(edit("$'").status, 0)
  } finally { rmSync(root, { recursive: true, force: true }) }
})
