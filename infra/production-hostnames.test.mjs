import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

for (const [key, hostname] of [['API_BASE', 'api.useorbit.org'], ['NEXT_PUBLIC_SITE_URL', 'app.useorbit.org']]) {
  for (const impostor of [hostname.replace('.', 'X'), `${hostname}.attacker.test`]) {
    test(`${key} rejects hostname ${impostor}`, () => {
      const directory = mkdtempSync(join(tmpdir(), 'production-hostname-'))
      try {
        for (const file of ['configuration.tf', 'production.tf', 'uploads.tf']) {
          const source = readFileSync(new URL(file, import.meta.url), 'utf8')
          writeFileSync(join(directory, file), file === 'configuration.tf' ? source.replaceAll(hostname, impostor) : source)
        }
        const env = { ...process.env, INFRA_TEST_ROOT: directory }
        delete env.NODE_TEST_CONTEXT
        const result = spawnSync(process.execPath, ['--test', '--test-name-pattern=production web values point at production hosts', fileURLToPath(new URL('./production-values.test.mjs', import.meta.url))], {
          env, encoding: 'utf8', timeout: 10_000,
        })
        assert.equal(result.status, 1, result.stdout + result.stderr)
        assert.match(result.stdout, /production web values point at production hosts/)
      } finally {
        rmSync(directory, { recursive: true, force: true })
      }
    })
  }
}
