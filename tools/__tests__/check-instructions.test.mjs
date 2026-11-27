import { test } from 'node:test'
import assert from 'node:assert/strict'
import { execFileSync, spawnSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { findings } from '../check-instructions.mjs'

const script = fileURLToPath(new URL('../check-instructions.mjs', import.meta.url))

const lines = (count, line = 'x') => `${line}\n`.repeat(count)
const docOf = (count, head = '# Title\n') => head + lines(count - head.split('\n').length + 1)

function makeRepo(files) {
  const root = mkdtempSync(join(tmpdir(), 'check-instructions-'))
  execFileSync('git', ['init', '-q'], { cwd: root })
  for (const [path, content] of Object.entries(files)) {
    mkdirSync(dirname(join(root, path)), { recursive: true })
    writeFileSync(join(root, path), content)
  }
  execFileSync('git', ['add', '-A'], { cwd: root })
  return root
}

function run(files) {
  const root = makeRepo(files)
  try {
    const result = spawnSync(process.execPath, [script], { cwd: root, encoding: 'utf8' })
    return { status: result.status, output: result.stderr.trim() }
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
}

const live = { 'src/Live.cs': 'class Live {}\n', 'lefthook.yml': 'pre-commit:\n' }

const cases = [
  {
    name: 'a 6001 B CLAUDE.md fails',
    files: { 'CLAUDE.md': 'a'.repeat(6001) },
    finding: 'CLAUDE.md: 6001 B is over the 6000 B budget',
  },
  { name: 'a 6000 B CLAUDE.md passes', files: { 'CLAUDE.md': 'a'.repeat(6000) } },
  {
    name: 'a nested AGENTS.md of 201 lines fails',
    files: { 'src/AGENTS.md': lines(201) },
    finding: 'src/AGENTS.md: 201 lines is over the 200 line budget',
  },
  { name: 'a nested AGENTS.md of 200 lines passes', files: { 'src/AGENTS.md': lines(200) } },
  {
    name: 'a file under .claude/rules over 6000 B fails',
    files: { '.claude/rules/api.md': 'a'.repeat(6001) },
    finding: '.claude/rules/api.md: 6001 B is over the 6000 B budget',
  },
  {
    name: 'a doc of 20001 B fails',
    files: { 'docs/big.md': 'a'.repeat(20001) },
    finding: 'docs/big.md: 20001 B is over the 20000 B budget',
  },
  { name: 'a doc of 20000 B passes', files: { 'docs/big.md': 'a'.repeat(20000) } },
  {
    name: 'a doc of 501 lines fails',
    files: { 'docs/long.md': `## Contents\n${lines(500)}` },
    finding: 'docs/long.md: 501 lines is over the 500 line budget',
  },
  {
    name: 'a doc of 101 lines with no Contents heading fails',
    files: { 'docs/a.md': docOf(101) },
    finding: 'docs/a.md: 101 lines and no "## Contents" heading',
  },
  { name: 'a doc of 100 lines with no Contents heading passes', files: { 'docs/a.md': docOf(100) } },
  { name: 'a doc of 101 lines with a Contents heading passes', files: { 'docs/a.md': docOf(101, '# Title\n\n## Contents\n') } },
  {
    name: 'a dead backticked path fails',
    files: { ...live, 'docs/a.md': 'See `src/Missing.cs`.\n' },
    finding: 'docs/a.md: cited path `src/Missing.cs` does not exist',
  },
  { name: 'a live backticked path passes', files: { ...live, 'docs/a.md': 'See `src/Live.cs`.\n' } },
  { name: 'a live path with a :12 suffix passes', files: { ...live, 'docs/a.md': 'See `src/Live.cs:12` and `src/Live.cs:12-14`.\n' } },
  {
    name: 'a dead path with a :12 suffix fails on the bare path',
    files: { ...live, 'docs/a.md': 'See `src/Gone.cs:12`.\n' },
    finding: 'docs/a.md: cited path `src/Gone.cs` does not exist',
  },
  { name: 'a live path with an anchor passes', files: { ...live, 'docs/a.md': 'See `src/Live.cs#top`.\n' } },
  { name: 'a live folder with a trailing slash passes', files: { ...live, 'docs/a.md': 'See `src/`.\n' } },
  { name: 'a tracked root file name passes', files: { ...live, 'docs/a.md': 'See `lefthook.yml`.\n' } },
  {
    name: 'a missing root file name that is not top level passes',
    files: { ...live, 'docs/a.md': 'See `missing.yml`.\n' },
  },
  { name: 'a path of another repository passes', files: { ...live, 'docs/a.md': 'See `other-repo/src/Missing.cs`.\n' } },
  { name: 'a path that starts with .. passes', files: { ...live, 'docs/a.md': 'See `../src/Missing.cs`.\n' } },
  {
    name: 'a token with a glob, a placeholder or a variable passes',
    files: { ...live, 'docs/a.md': 'See `src/*.cs`, `src/<Name>.cs`, `src/{A,B}.cs`, `src/$X.cs` and `src/Missing?.cs`.\n' },
  },
  { name: 'a command token passes', files: { ...live, 'docs/a.md': 'Run `src/Missing.cs --flag`.\n' } },
  { name: 'a path in a fenced block passes', files: { ...live, 'docs/a.md': '```sh\ncat `src/Missing.cs`\n```\n' } },
]

for (const { name, files, finding } of cases) {
  test(name, () => {
    const { status, output } = run(files)
    if (finding === undefined) {
      assert.equal(output, '')
      assert.equal(status, 0)
      return
    }
    assert.equal(status, 1)
    assert.ok(output.split('\n').includes(finding), `expected "${finding}" in:\n${output}`)
  })
}

test('a harness one line over the budget fails with the owner rule', () => {
  const root = makeRepo({ 'tools/a.mjs': lines(3), '.github/b.yml': lines(2), 'lefthook.yml': lines(1), 'src/Skipped.cs': lines(50) })
  try {
    assert.deepEqual(findings(root, 6), [])
    assert.deepEqual(findings(root, 5), [
      "harness: 6 lines is over the budget of 5 lines. The harness line budget changes only by the owner's words.",
    ])
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('the harness count skips a binary file', () => {
  const root = makeRepo({ 'tools/a.mjs': lines(2) })
  try {
    writeFileSync(join(root, 'tools/logo.bin'), Buffer.from([0, 10, 10, 10, 10]))
    execFileSync('git', ['add', '-A'], { cwd: root })
    assert.deepEqual(findings(root, 2), [])
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})
