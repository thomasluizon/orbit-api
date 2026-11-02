import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, writeFileSync, readFileSync, existsSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const script = fileURLToPath(new URL('./release-admission.mjs', import.meta.url))
test('release admission never executes gh from an untrusted PATH', () => {
  const directory = mkdtempSync(join(tmpdir(), 'release-admission-path-'))
  try {
    const marker = join(directory, 'executed')
    writeFileSync(join(directory, 'gh'), `#!/bin/sh\nprintf compromised > '${marker}'\nprintf '{}'
`, { mode: 0o755 })
    const result = spawnSync(process.execPath, [script, 'ci'], {
      env: { PATH: directory, COMMIT_SHA: 'a'.repeat(40), GITHUB_REPOSITORY: 'owner/repo', GH_TOKEN: 'invalid-test-token' },
      encoding: 'utf8', timeout: 10_000,
    })
    assert.equal(existsSync(marker), false, 'A PATH entry supplied an executable to release admission')
    assert.notEqual(result.status, 0)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})
test('release admission pins both CLI executables to trusted system paths', () => {
  const source = readFileSync(script, 'utf8')
  for (const command of ['gh', 'git']) {
    assert.ok(source.includes(`execFileSync('/usr/bin/${command}',`), `${command} must not be resolved using PATH`)
  }
})
test('API CI runs the release admission security regressions', () => {
  const workflow = readFileSync(new URL('../workflows/test.yml', import.meta.url), 'utf8')
  assert.ok(workflow.includes('node --test .github/scripts/release-admission.test.mjs .github/scripts/release-admission-security.test.mjs'))
})
