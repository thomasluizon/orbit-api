import { test } from 'node:test'
import assert from 'node:assert/strict'
import { execFileSync, spawnSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const script = fileURLToPath(new URL('./changed-inputs.sh', import.meta.url))
const groups = ['tests', 'openapi', 'deps', 'infra']

function write(root, path, text = 'content\n') {
  mkdirSync(dirname(join(root, path)), { recursive: true })
  writeFileSync(join(root, path), text)
}

/** A repository whose HEAD merges a pull request branch into main, as actions/checkout presents it. */
function pullRequest(t, change, mainPath = 'docs-on-main.md') {
  const root = mkdtempSync(join(tmpdir(), 'orbit-changed-inputs-'))
  t.after(() => rmSync(root, { recursive: true, force: true }))
  const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8' })
  git('init', '--quiet', '--initial-branch=main')
  git('config', 'user.name', 'Changed inputs test')
  git('config', 'user.email', 'changed-inputs@example.org')
  write(root, 'README.md')
  write(root, 'src/Orbit.Api/Controllers/HabitsController.cs')
  git('add', '.')
  git('commit', '--quiet', '-m', 'base')
  git('checkout', '--quiet', '-b', 'feature')
  change(root, git)
  git('add', '-A')
  git('commit', '--quiet', '-m', 'change')
  git('checkout', '--quiet', 'main')
  write(root, mainPath)
  git('add', '.')
  git('commit', '--quiet', '-m', 'main moved')
  git('merge', '--quiet', '--no-ff', '-m', 'merge', 'feature')
  return root
}

function run(root, event, args = groups) {
  const output = join(root === null ? mkdtempSync(join(tmpdir(), 'orbit-changed-inputs-output-')) : root, 'github-output')
  writeFileSync(output, '')
  const result = spawnSync('bash', [script, ...args], {
    cwd: root ?? tmpdir(),
    encoding: 'utf8',
    env: { ...process.env, GITHUB_EVENT_NAME: event, GITHUB_OUTPUT: output },
  })
  const flags = Object.fromEntries(readFileSync(output, 'utf8').trim().split('\n').filter(Boolean).map(line => line.split('=')))
  return { ...result, flags }
}

function changedOnly(t, path) {
  return run(pullRequest(t, root => write(root, path)), 'pull_request').flags
}

test('a docs change selects no input group', t => {
  assert.deepEqual(changedOnly(t, 'docs/notes.md'), Object.fromEntries(groups.map(group => [group, 'false'])))
})

test('a change on main after the branch point is not a change of the pull request', t => {
  const { flags } = run(pullRequest(t, root => write(root, 'docs/notes.md'), 'src/Orbit.Domain/Entities/Moved.cs'), 'pull_request')
  assert.equal(flags.tests, 'false')
})

test('an API source change selects only the tests', t => {
  const flags = changedOnly(t, 'src/Orbit.Api/Controllers/NewController.cs')
  assert.deepEqual(flags, { tests: 'true', openapi: 'false', deps: 'false', infra: 'false' })
})

test('a published OpenAPI document selects the breaking-change gate', t => {
  const flags = changedOnly(t, 'src/Orbit.Api/openapi_v2-r2.json')
  assert.equal(flags.openapi, 'true')
  assert.equal(flags.tests, 'true')
})

for (const path of ['infra/configuration.tf', '.github/migration-locks.json', 'evals/astra/dates.json', 'coverlet.runsettings']) {
  test(`${path} is an input of the C# tests`, t => {
    assert.equal(changedOnly(t, path).tests, 'true')
  })
}

test('a path that carries the retired product name is an input of the legacy name guard', t => {
  assert.equal(changedOnly(t, 'docs/astra-notes.md').tests, 'true')
  assert.equal(changedOnly(t, 'docs/Astra.md').tests, 'true')
})

for (const path of ['Directory.Build.props', 'Directory.Build.targets']) {
  test(`${path} is an input of the tests and the dependency review`, t => {
    const flags = changedOnly(t, path)
    assert.equal(flags.tests, 'true')
    assert.equal(flags.deps, 'true')
  })
}

for (const path of ['src/Orbit.Api/Orbit.Api.csproj', 'tests/Orbit.Analyzers.Tests/Directory.Packages.props', 'infra/package-lock.json', '.config/dotnet-tools.json', '.github/workflows/test.yml', '.github/dependabot.yml']) {
  test(`${path} is an input of the dependency review`, t => {
    assert.equal(changedOnly(t, path).deps, 'true')
  })
}

test('a source file that is not a project file is not an input of the dependency review', t => {
  assert.equal(changedOnly(t, 'src/Orbit.Api/Controllers/NewController.cs').deps, 'false')
})

for (const path of ['infra/configuration.tf', '.github/scripts/render-release.sh', '.github/scripts/staging-reseed-data.py', '.github/scripts/test-postgres-access.sh', '.github/workflows/release.yml']) {
  test(`${path} is an input of the Render infrastructure validation`, t => {
    assert.equal(changedOnly(t, path).infra, 'true')
  })
}

test('a source change is not an input of the Render infrastructure validation', t => {
  assert.equal(changedOnly(t, 'src/Orbit.Api/Controllers/NewController.cs').infra, 'false')
})

test('a rename out of src counts the old path', t => {
  const flags = run(pullRequest(t, root => {
    mkdirSync(join(root, 'docs'), { recursive: true })
    renameSync(join(root, 'src/Orbit.Api/Controllers/HabitsController.cs'), join(root, 'docs/HabitsController.cs'))
  }), 'pull_request').flags
  assert.equal(flags.tests, 'true')
})

test('a Guards workflow change selects the dependency review, the OpenAPI gate and the infrastructure validation', t => {
  const flags = changedOnly(t, '.github/workflows/guards.yml')
  assert.deepEqual(flags, { tests: 'false', openapi: 'true', deps: 'true', infra: 'true' })
})

test('the filter script is an input of every group', t => {
  assert.deepEqual(changedOnly(t, '.github/scripts/changed-inputs.sh'), Object.fromEntries(groups.map(group => [group, 'true'])))
})

for (const event of ['push', 'workflow_dispatch', 'schedule']) {
  test(`${event} selects every group without reading git`, () => {
    const result = run(null, event)
    assert.equal(result.status, 0, result.stderr)
    assert.deepEqual(result.flags, Object.fromEntries(groups.map(group => [group, 'true'])))
  })
}

test('an unknown group fails instead of skipping work', () => {
  const result = run(null, 'push', ['tests', 'everything'])
  assert.equal(result.status, 2)
  assert.match(result.stderr, /unknown input group: everything/)
})
