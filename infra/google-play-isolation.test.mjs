import assert from "node:assert/strict"
import { readFileSync } from "node:fs"
import { dirname, join } from "node:path"
import { fileURLToPath } from "node:url"
import { test } from "node:test"

const root = dirname(fileURLToPath(import.meta.url))
const configuration = readFileSync(join(root, "configuration.tf"), "utf8")

function block(header) {
  const start = configuration.indexOf(header)
  assert.notEqual(start, -1, `Missing ${header}`)
  const opening = configuration.indexOf("{", start + header.length - (header.endsWith("{") ? 1 : 0))
  let depth = 0
  for (let index = opening; index < configuration.length; index++) {
    if (configuration[index] === "{") depth++
    if (configuration[index] === "}" && --depth === 0) return configuration.slice(opening + 1, index)
  }
  assert.fail(`Unclosed ${header}`)
}

test("staging Play purchases use the staging package in the API environment group", () => {
  const staging = block("staging_api_values = merge(")
  const group = block('resource "render_env_group" "staging_api"')

  assert.match(configuration, /staging_api_values\s*=\s*merge\(local\.production_api_values,\s*\{/)
  assert.match(staging, /GooglePlay__PackageName\s*=\s*"org\.useorbit\.app\.staging"/)
  assert.match(group, /for key, value in local\.staging_api_values\s*:\s*key => \{ value = value \}/)
  assert.doesNotMatch(group, /local\.production_api_values|GooglePlay__PackageName/)
  assert.doesNotMatch(configuration, /"GooglePlay__PackageName"/)
})

test("production billing values remain byte-identical", () => {
  const production = block("production_api_values = {")
  const billing = production.split("\n").filter(line => /^\s*(GooglePlay|Stripe)__/.test(line))

  assert.deepEqual(billing, [
    '    GooglePlay__MonthlyBasePlanId       = "monthly"',
    '    GooglePlay__PackageName             = "org.useorbit.app"',
    '    GooglePlay__ProductId               = "orbit_pro"',
    '    GooglePlay__ReferralOfferId         = "referral10"',
    '    GooglePlay__RtdnAudience            = "https://api.useorbit.org/api/subscriptions/play/rtdn"',
    '    GooglePlay__RtdnServiceAccountEmail = "orbit-299@orbit-490614.iam.gserviceaccount.com"',
    '    GooglePlay__YearlyBasePlanId        = "yearly"',
    '    Stripe__CancelUrl                   = "https://app.useorbit.org/upgrade"',
    '    Stripe__MonthlyPriceIdBrl           = "price_1U59khGwWZvarDk3duqWRGu7"',
    '    Stripe__MonthlyPriceIdUsd           = "price_1U59miGwWZvarDk3c7Jomocl"',
    '    Stripe__ProProductId                = "prod_UBUPrTlZg8chuk"',
    '    Stripe__SuccessUrl                  = "https://app.useorbit.org/profile?subscription=success"',
    '    Stripe__YearlyPriceIdBrl            = "price_1U59lVGwWZvarDk3FBO8ci6L"',
    '    Stripe__YearlyPriceIdUsd            = "price_1U59ncGwWZvarDk3Ydiw7jP7"',
  ])
  const group = block('resource "render_env_group" "production_api"')
  assert.match(group, /for key, value in local\.production_api_values\s*:\s*key => \{ value = value \}/)
  assert.doesNotMatch(group, /staging|GooglePlay__|Stripe__/)
})

for (const [environment, header, host, route] of [
  ["production", "production_api_values = {", "app.useorbit.org", "profile"],
  ["staging", "staging_api_values = merge(", "app-staging.useorbit.org", "upgrade"],
]) {
  test(`${environment} Stripe checkout success returns to the supported purchase route`, () => {
    const values = block(header)
    const urls = [...values.matchAll(/\bStripe__SuccessUrl\s*=\s*"([^"]*)"/g)].map(match => match[1])

    assert.deepEqual(urls, [`https://${host}/${route}?subscription=success`])
  })

  test(`${environment} Stripe checkout cancellation stays on upgrade`, () => {
    const values = block(header)
    const urls = [...values.matchAll(/\bStripe__CancelUrl\s*=\s*"([^"]*)"/g)].map(match => match[1])

    assert.deepEqual(urls, [`https://${host}/upgrade`])
  })
}

test("staging Play notifications retain the staging endpoint audience", () => {
  const staging = block("staging_api_values = merge(")
  assert.match(staging, /GooglePlay__RtdnAudience\s*=\s*"https:\/\/api-staging\.useorbit\.org\/api\/subscriptions\/play\/rtdn"/)
})
