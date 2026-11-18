import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { accepts, outcome, verify } from './stripe-webhook-version.mjs'

const sha = 'b'.repeat(40)
const library = '2026-08-26.dahlia'
const staging = 'https://api-staging.useorbit.org'
const production = 'https://api.useorbit.org'

function health(data, commit = sha, status = 'Healthy') {
  return { status, commit, checks: [{ name: 'stripe-webhook-version', status, description: 'stripe', data }] }
}

function server(routes) {
  const calls = []
  const fetchImpl = async url => {
    calls.push(url)
    const route = routes[url]
    if (route === undefined) return new Response('Not Found', { status: 404 })
    return Response.json(route, { status: route.status === 'Healthy' ? 200 : 503 })
  }
  return { calls, fetchImpl }
}

test('the version rule is the rule of Stripe.net EventUtility, row for row with the C# table test', () => {
  const cases = JSON.parse(readFileSync(new URL('./stripe-webhook-version.cases.json', import.meta.url), 'utf8'))
  const [date, release] = library.split('.')
  const expand = value => value.replaceAll('{library}', library).replaceAll('{date}', date).replaceAll('{release}', release)
  assert.ok(cases.length > 0)
  for (const row of cases) {
    const libraryVersion = expand(row.library)
    const endpointVersion = expand(row.endpoint)
    assert.equal(accepts(libraryVersion, endpointVersion), row.accepted, `${libraryVersion} with ${endpointVersion}`)
  }
})

test('a refused connection names the URL and the cause', async () => {
  const fetchImpl = async () => {
    throw new TypeError('fetch failed', { cause: new Error('connect ECONNREFUSED 127.0.0.1:443') })
  }
  await assert.rejects(verify(staging, production, sha, fetchImpl), error => {
    assert.equal(error.message, `${staging}/health/stripe: fetch failed (connect ECONNREFUSED 127.0.0.1:443)`)
    return true
  })
})

test('a refused connection on the endpoint side names the endpoint URL', async () => {
  const fetchImpl = async url => {
    if (url === `${production}/health/stripe`) throw new DOMException('The operation was aborted due to timeout', 'TimeoutError')
    return Response.json(health({ libraryApiVersion: library, endpointApiVersions: [library] }))
  }
  await assert.rejects(verify(staging, production, sha, fetchImpl), error => {
    assert.equal(error.message, `${production}/health/stripe: The operation was aborted due to timeout`)
    return true
  })
})

test('a body that is not JSON names the URL and the HTTP status', async () => {
  const fetchImpl = async url => url === `${production}/health/stripe`
    ? new Response('<!DOCTYPE html><title>Bad gateway</title>', { status: 502, headers: { 'content-type': 'text/html' } })
    : Response.json(health({ libraryApiVersion: library, endpointApiVersions: [library] }))
  await assert.rejects(verify(staging, production, sha, fetchImpl), error => {
    assert.match(error.message, new RegExp(`^${production.replaceAll('.', '\\.')}/health/stripe: HTTP 502 body is not JSON: `))
    return true
  })
})

test('production before the deploy refuses the clover endpoint that broke staging', async () => {
  const { fetchImpl } = server({
    [`${staging}/health/stripe`]: health({ libraryApiVersion: library, endpointApiVersions: [library] }),
    [`${production}/health/stripe`]: health({ libraryApiVersion: library, endpointApiVersions: ['2026-02-25.clover'] }, 'c'.repeat(40), 'Unhealthy'),
  })
  await assert.rejects(verify(staging, production, sha, fetchImpl), /expects 2026-08-26\.dahlia.*\[2026-02-25\.clover\]/)
})

test('production before a package bump passes when a new endpoint overlaps the old one', async () => {
  const { fetchImpl } = server({
    [`${staging}/health/stripe`]: health({ libraryApiVersion: '2027-01-27.elm', endpointApiVersions: ['2027-01-27.elm'] }),
    [`${production}/health/stripe`]: health({ libraryApiVersion: library, endpointApiVersions: [library, '2027-01-27.elm'] }, 'c'.repeat(40)),
  })
  const result = await verify(staging, production, sha, fetchImpl)
  assert.match(result.message, /2027-01-27\.elm/)
})

test('the same URL reads once and checks its own endpoints after a deploy', async () => {
  const { calls, fetchImpl } = server({
    [`${staging}/health/stripe`]: health({ libraryApiVersion: library, endpointApiVersions: ['2026-02-25.clover', library] }),
  })
  await verify(staging, staging, sha, fetchImpl)
  assert.deepEqual(calls, [`${staging}/health/stripe`])
})

test('the library side must serve the released commit', async () => {
  const { fetchImpl } = server({
    [`${staging}/health/stripe`]: health({ libraryApiVersion: library, endpointApiVersions: [library] }, 'd'.repeat(40)),
  })
  await assert.rejects(verify(staging, staging, sha, fetchImpl), /not b{40}/)
})

test('the library side must serve the route', async () => {
  const { fetchImpl } = server({})
  await assert.rejects(verify(staging, production, sha, fetchImpl), /is missing; the released build must serve it/)
})

test('a production build without the route only warns before the deploy', async () => {
  const { fetchImpl } = server({
    [`${staging}/health/stripe`]: health({ libraryApiVersion: library, endpointApiVersions: [library] }),
  })
  const result = await verify(staging, production, sha, fetchImpl)
  assert.match(result.warning, /not verified before the deploy/)
})

test('a degraded Stripe read fails instead of passing without versions', async () => {
  const { fetchImpl } = server({
    [`${staging}/health/stripe`]: health({ libraryApiVersion: library }, sha, 'Degraded'),
  })
  await assert.rejects(verify(staging, staging, sha, fetchImpl), /could not read the webhook endpoints/)
})

test('a body without the Stripe check fails', async () => {
  const { fetchImpl } = server({ [`${staging}/health/stripe`]: { status: 'Healthy', commit: sha, checks: [] } })
  await assert.rejects(verify(staging, staging, sha, fetchImpl), /has no stripe-webhook-version check/)
})

test('the release checks production before the deploy and each environment after its deploy', () => {
  const workflow = readFileSync(new URL('../workflows/release.yml', import.meta.url), 'utf8')
  const [, production, staging] = workflow.split(/\n  (?:production|staging):\n/)
  const before = production.indexOf("stripe-webhook-version.mjs 'https://api-staging.useorbit.org' 'https://api.useorbit.org'")
  assert.ok(before > production.indexOf('- name: Require healthy staging commit'))
  assert.ok(before < production.indexOf('- name: Trigger and monitor Render deploy'))
  assert.match(production, /- name: Require a production Stripe webhook endpoint the release accepts\n\s+if: \$\{\{ !inputs.force_unstaged \}\}/)
  const after = production.indexOf("stripe-webhook-version.mjs 'https://api.useorbit.org' 'https://api.useorbit.org'")
  assert.ok(after > production.indexOf('- name: Verify API health and live commit'))
  assert.ok(after < production.indexOf('- name: Record deployment'))
  const stagingAfter = staging.indexOf("stripe-webhook-version.mjs 'https://api-staging.useorbit.org' 'https://api-staging.useorbit.org'")
  assert.ok(stagingAfter > staging.indexOf('- name: Verify staging health and live commit'))
  assert.ok(stagingAfter < staging.indexOf('- name: Record deployment'))
  const guards = readFileSync(new URL('../workflows/guards.yml', import.meta.url), 'utf8')
  assert.match(guards, /node --test [^\n]*\.github\/scripts\/stripe-webhook-version\.test\.mjs/)
})

test('a line break that came from a health response cannot start a second log line or a workflow command', async () => {
  const forged = '2026-02-25.clover\r\n::error::forged\u2028'
  const cases = [
    { failed: true, routes: { [`${staging}/health/stripe`]: health({ libraryApiVersion: library, endpointApiVersions: [forged] }) } },
    { failed: false, routes: { [`${staging}/health/stripe`]: health({ libraryApiVersion: library, endpointApiVersions: [library, forged] }) } },
    { failed: true, routes: { [`${staging}/health/stripe`]: health({ libraryApiVersion: library, endpointApiVersions: [library] }, forged) } },
  ]
  for (const { failed, routes } of cases) {
    const result = await outcome(staging, staging, sha, server(routes).fetchImpl)
    assert.equal(result.failed, failed)
    assert.doesNotMatch(result.line, /[\u0000-\u001f\u007f-\u009f\u2028\u2029]/)
    assert.ok(result.line.includes('2026-02-25.clover\\u000d\\u000a::error::forged\\u2028'), result.line)
  }
  const body = await outcome(staging, staging, sha, async () => new Response('\n::error::forged', { status: 502 }))
  assert.equal(body.failed, true)
  assert.doesNotMatch(body.line, /[\u0000-\u001f\u007f-\u009f\u2028\u2029]/)
})
