import assert from "node:assert/strict"
import { existsSync, readFileSync } from "node:fs"
import { dirname, join } from "node:path"
import { fileURLToPath } from "node:url"
import { test } from "node:test"

const root = dirname(fileURLToPath(import.meta.url))
const configuration = readFileSync(join(root, "configuration.tf"), "utf8")
const onboarding = existsSync(join(root, "onboarding.tf")) ? readFileSync(join(root, "onboarding.tf"), "utf8") : ""

test("onboarding signing secrets are stable per-environment SecureStrings", () => {
  assert.doesNotMatch(onboarding, /keepers|timestamp\(|plantimestamp\(/)
  for (const environment of ["production", "staging"]) {
    const password = onboarding.split(`resource "random_password" "onboarding_signing_secret_${environment}" {`)[1]?.split("\n}")[0]
    assert.ok(password, `Missing independently targeted ${environment} signing password`)
    assert.match(password, /length\s*=\s*64\b/)
    assert.match(password, /special\s*=\s*false\b/)
    assert.doesNotMatch(password, /for_each|each\./)
    const parameter = onboarding.split(`resource "aws_ssm_parameter" "onboarding_signing_secret_${environment}" {`)[1]?.split("\n}")[0]
    assert.ok(parameter, `Missing independently targeted ${environment} signing parameter`)
    assert.match(parameter, new RegExp(`name\\s*=\\s*"/orbit/${environment}/api/OnboardingSecurity__SigningSecret"`))
    assert.match(parameter, new RegExp(`type\\s*=\\s*"SecureString"\\s*value\\s*=\\s*random_password\\.onboarding_signing_secret_${environment}\\.result`))
    assert.doesNotMatch(parameter, /for_each|each\./)
  }
})

const expectedOrigins = {
  production: ["https://app.useorbit.org"],
  staging: ["https://app-staging.useorbit.org"],
}

for (const environment of ["production", "staging"]) {
  test(`${environment} API receives its own onboarding settings and secret`, () => {
    const group = configuration.split(`resource "render_env_group" "${environment}_api" {`)[1].split('\nresource "')[0]
    assert.ok(group.includes(`for key, value in local.onboarding_api_values["${environment}"] : key => { value = value }`))
    assert.ok(group.includes(`OnboardingSecurity__SigningSecret = { value = aws_ssm_parameter.onboarding_signing_secret_${environment}.value }`))
    const configuredOrigins = onboarding.split(`${environment} = [`)[1].split("]")[0]
    assert.deepEqual([...configuredOrigins.matchAll(/"([^"]+)"/g)].map(match => match[1]), expectedOrigins[environment])
  })
}

test("onboarding origins and widget-approved hostnames bind as indexed settings", () => {
  assert.match(onboarding, /for index, origin in origins : "OnboardingSecurity__AllowedOrigins__\$\{index\}" => origin/)
  assert.match(onboarding, /for index, hostname in sort\(setintersection\(/)
  assert.match(onboarding, /\[for origin in origins : trimprefix\(origin, "https:\/\/"\)\]/)
  assert.match(onboarding, /toset\(cloudflare_turnstile_widget\.orbit\.domains\)/)
  assert.match(onboarding, /"OnboardingSecurity__TurnstileHostnames__\$\{index\}" => hostname/)
  assert.doesNotMatch(onboarding, /localhost|tauri:\/\/|org\.useorbit\.desktop:\/\//)
})

test("optional mobile attestation settings come only from the matching SSM path", () => {
  assert.match(onboarding, /data "aws_ssm_parameters_by_path" "onboarding_platform"/)
  assert.match(onboarding, /path\s*=\s*"\/orbit\/\$\{each\.key\}\/api"/)
  assert.match(onboarding, /recursive\s*=\s*false/)
  assert.match(onboarding, /with_decryption\s*=\s*true/)
  assert.match(onboarding, /for environment, parameters in data\.aws_ssm_parameters_by_path\.onboarding_platform/)
  assert.match(onboarding, /for name, value in zipmap\(parameters\.names, parameters\.values\)/)
  assert.match(onboarding, /trimprefix\(name, "\/orbit\/\$\{environment\}\/api\/"\) => value/)
  assert.match(onboarding, /if can\(regex\(/)
  assert.match(onboarding, /PlayIntegrityCredentialsJson\|AndroidPackageName\|AndroidCertificateDigests__\[0-9\]\+\|AppleTeamId\|AppleBundleId\|AppleBundleVersions__\[0-9\]\+\|AppleValidationCategories__\[0-9\]\+/)
  assert.match(onboarding, /local\.onboarding_platform_values\[environment\]/)
  assert.doesNotMatch(onboarding, /resource "aws_ssm_parameter" "onboarding_platform"|GooglePlay__ServiceAccountJson/)
})
