import { test } from 'node:test'
import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
const read = path => readFileSync(new URL(path, import.meta.url), 'utf8')
const snapshot = JSON.parse(read('fixtures/landing-inline-blocks.json'))
const policy = read('landing-csp.tf')
test('every captured inline block and style attribute has its exact sha256 source', () => {
  assert.equal(snapshot.sourceCommit, 'bb7a3a846e64a64e8310010b1eff00e76fe3435b')
  for (const block of [...snapshot.blocks, ...snapshot.styleAttributes]) {
    assert.equal(block.hash, `sha256-${createHash('sha256').update(block.body).digest('base64')}`)
    assert.ok(policy.includes(`'${block.hash}'`), `Missing ${block.tag ?? 'style attribute'} hash ${block.hash}`)
  }
  assert.doesNotMatch(policy, /unsafe-inline|unsafe-eval/)
  assert.match(policy, /style-src-attr 'unsafe-hashes'/)
})
test('both landing sites enforce CSP by default and retain the existing five security headers', () => {
  assert.match(read('production.tf'), /name = "Content-Security-Policy", value = local.landing_csp.production/)
  assert.match(read('staging.tf'), /var.staging_landing_csp_report_only \? "Content-Security-Policy-Report-Only" : "Content-Security-Policy"/)
  assert.match(policy, /variable "staging_landing_csp_report_only"[\s\S]*default\s*= false/)
  for (const name of ['production.tf', 'staging.tf']) {
    const source = read(name)
    for (const [header, value] of Object.entries({
      'X-Content-Type-Options': 'nosniff', 'X-Frame-Options': 'DENY',
      'Referrer-Policy': 'strict-origin-when-cross-origin', 'Permissions-Policy': 'camera=(), microphone=(), geolocation=()',
      'Strict-Transport-Security': 'max-age=63072000; includeSubDomains; preload',
    })) assert.ok(source.includes(`{ path = "/*", name = "${header}", value = "${value}" }`))
  }
})
