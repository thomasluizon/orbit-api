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

function assignments(source) {
  return Object.fromEntries([...source.matchAll(/^\s*(\w+)\s*=\s*"([^"\n]*)"\s*$/gm)].map(([, key, value]) => [key, value]))
}

const appleSecretKeys = ["AppStore__PrivateKey", "AppleSignIn__PrivateKey", "Apns__PrivateKey"]

const production = {
  AppStore__KeyId: "2D28SBZ84U",
  AppStore__IssuerId: "2a83c5e0-37ac-4e6b-b4ed-db0ed443d3dc",
  AppStore__BundleId: "org.useorbit.app",
  AppStore__AppAppleId: "6831372835",
  AppStore__MonthlyProductId: "orbit_pro_monthly",
  AppStore__YearlyProductId: "orbit_pro_yearly",
  AppleSignIn__KeyId: "C5949L4PM4",
  AppleSignIn__TeamId: "BD7JH468F7",
  AppleSignIn__NativeClientId: "org.useorbit.app",
  AppleSignIn__WebClientId: "org.useorbit.app.web",
  AppleSignIn__RedirectUri: "https://api.useorbit.org/api/auth/apple/callback",
  AppleSignIn__McpRedirectUri: "https://api.useorbit.org/oauth/apple/callback",
  AppleSignIn__WebOrigin: "https://app.useorbit.org",
  Apns__KeyId: "9UDC7DT49B",
  Apns__TeamId: "BD7JH468F7",
  Apns__Topic: "org.useorbit.app",
  Apns__UseSandbox: "false",
}

const stagingOverrides = {
  AppStore__BundleId: "org.useorbit.app.staging",
  AppStore__AppAppleId: "6831373752",
  AppStore__MonthlyProductId: "orbit_staging_pro_monthly",
  AppStore__YearlyProductId: "orbit_staging_pro_yearly",
  AppleSignIn__KeyId: "498G5TUZ23",
  AppleSignIn__NativeClientId: "org.useorbit.app.staging",
  AppleSignIn__WebClientId: "org.useorbit.app.staging.web",
  AppleSignIn__RedirectUri: "https://api-staging.useorbit.org/api/auth/apple/callback",
  AppleSignIn__McpRedirectUri: "https://api-staging.useorbit.org/oauth/apple/callback",
  AppleSignIn__WebOrigin: "https://app-staging.useorbit.org",
  Apns__Topic: "org.useorbit.app.staging",
}

test("both API groups read the Apple signing keys from SSM", () => {
  const secrets = [...configuration.split("api_secret_keys = toset([")[1].split("])")[0].matchAll(/"([^"]+)"/g)].map(match => match[1])
  for (const key of appleSecretKeys) assert.ok(secrets.includes(key), `${key} must be an SSM secret`)

  for (const environment of ["production", "staging"]) {
    const source = block(`data "aws_ssm_parameter" "${environment}_api"`)
    assert.match(source, /for_each = setunion\(local\.api_secret_keys,/)
    assert.match(source, new RegExp(`name\\s*=\\s*"/orbit/${environment}/api/\\$\\{each\\.key\\}"`))
    const group = block(`resource "render_env_group" "${environment}_api"`)
    assert.ok(group.includes(`for key, parameter in data.aws_ssm_parameter.${environment}_api : key => { value = parameter.value }`))
  }

  const plain = { ...assignments(block("production_api_values = {")), ...assignments(block("staging_api_values = merge(")) }
  for (const key of appleSecretKeys) assert.equal(plain[key], undefined, `${key} must never be a plain value`)
})

test("production API values pin every Apple identifier", () => {
  const values = assignments(block("production_api_values = {"))
  for (const [key, expected] of Object.entries(production)) assert.equal(values[key], expected, `production ${key}`)
})

test("staging API values pin the staging Apple identifiers over the production defaults", () => {
  assert.match(configuration, /staging_api_values\s*=\s*merge\(local\.production_api_values,\s*\{/)
  const overrides = assignments(block("staging_api_values = merge("))
  const values = { ...assignments(block("production_api_values = {")), ...overrides }
  for (const [key, expected] of Object.entries({ ...production, ...stagingOverrides }))
    assert.equal(values[key], expected, `staging ${key}`)
  for (const key of Object.keys(stagingOverrides)) assert.ok(key in overrides, `staging must override ${key}`)
})

test("each Apple web origin matches the web origin that the Google sign-in admits", () => {
  for (const [header, merged] of [["production_api_values = {", false], ["staging_api_values = merge(", true]]) {
    const values = merged
      ? { ...assignments(block("production_api_values = {")), ...assignments(block(header)) }
      : assignments(block(header))
    assert.equal(`${values.AppleSignIn__WebOrigin}/auth-callback`, values.Google__AllowedRedirectUris__0)
    assert.equal(values.AppleSignIn__WebOrigin, values.Cors__AllowedOrigins__0)
    const api = new URL(values.Storage__S3__PublicBaseUrl).origin
    assert.equal(new URL(values.AppleSignIn__RedirectUri).origin, api)
    assert.equal(new URL(values.AppleSignIn__McpRedirectUri).origin, api)
  }
})
