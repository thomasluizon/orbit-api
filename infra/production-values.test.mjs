import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
const text = name => readFileSync(process.env.INFRA_TEST_ROOT ? resolve(process.env.INFRA_TEST_ROOT, name) : new URL(name, import.meta.url), 'utf8')
const configuration = text('configuration.tf')
const production = text('production.tf')
const value = (source, key, expected) => {
  const assignments = [...source.matchAll(/^\s*(\w+)\s*=\s*"([^"\n]*)"\s*$/gm)]
  assert.equal(assignments.find(([, name]) => name === key)?.[2], expected, `${key} must match its reviewed value`)
}

test('production API values are pinned', () => {
  const api = configuration.split('  production_api_values = {')[1].split('  staging_api_values =')[0]
  for (const [key, expected] of Object.entries({
    ASPNETCORE_ENVIRONMENT: 'Production', Cors__AllowedOrigins__0: 'https://app.useorbit.org',
    Database__MigrateOnStartup: 'true', Google__AllowedRedirectUris__0: 'https://app.useorbit.org/auth-callback',
    Google__AllowedRedirectUris__1: 'https://api.useorbit.org/oauth/google/callback',
    Google__ClientId: '355604968359-tqco1o3l874mg7rvme91vniael7pn3tk.apps.googleusercontent.com',
    Jwt__Issuer: 'OrbitApi', Jwt__Audience: 'OrbitClient', Ses__Region: 'us-east-2',
    Ses__FromEmail: 'Orbit <contact@useorbit.org>', Sentry__Environment: 'production',
    Storage__S3__PublicBaseUrl: 'https://api.useorbit.org',
  })) value(api, key, expected)
  assert.match(api, /Storage__S3__Bucket\s*=\s*aws_s3_bucket.uploads\["production"\].bucket/)
  assert.match(text('uploads.tf'), /bucket\s*=\s*"orbit-uploads-\$\{each.key\}-713285551626"/)
})
test('api_secret_keys covers every required API secret', () => {
  const keys = [...configuration.split('api_secret_keys = toset([')[1].split('])')[0].matchAll(/"([^"]+)"/g)].map(match => match[1])
  assert.deepEqual(keys.sort(), ['AI__ApiKey', 'Encryption__Key', 'Firebase__CredentialsJson', 'GooglePlay__ServiceAccountJson', 'Google__ClientSecret', 'Jwt__SecretKey', 'Marketing__UnsubscribeSigningKey', 'PostHog__ApiKey', 'SMOKE_TEST_CODE', 'SMOKE_TEST_EMAIL', 'Sentry__Dsn', 'Stripe__SecretKey', 'Stripe__WebhookSecret', 'TEST_ACCOUNTS', 'Vapid__PrivateKey', 'Waitlist__SigningKey', 'Wns__ClientSecret'].sort())
  assert.match(configuration, /for_each = setunion\(local.api_secret_keys, local.api_eval_secret_keys\["production"\]\)/)
  for (const key of ['BotProtection__SecretKey', 'Auth__BffForwardingSecret', 'Storage__S3__AccessKeyId', 'Storage__S3__SecretAccessKey', 'OnboardingSecurity__SigningSecret', 'Ses__AccessKeyId', 'Ses__SecretAccessKey'])
    assert.ok(configuration.includes(key), `Terraform-managed secret ${key} must reach the API group`)
})
test('production web values point at production hosts', () => {
  const web = configuration.split('  production_web_values = {')[1].split('  staging_web_values =')[0]
  value(web, 'API_BASE', 'https://api.useorbit.org')
  value(web, 'NEXT_PUBLIC_SITE_URL', 'https://app.useorbit.org')
  value(web, 'NODE_ENV', 'production')
})
test('each production service links its env group', () => {
  for (const [service, group] of [['production_api', 'production-api'], ['production_web', 'production-web']]) {
    const resource = production.split(`resource "render_web_service" "${service}" {`)[1].split('\nresource ')[0]
    assert.match(resource, /ignore_changes\s*=\s*\[[^\]]*\benv_vars\b/, `${service} must preserve service-level environment variables`)
    assert.match(resource, new RegExp(`ORBIT_TERRAFORM_ENV_GROUP = \\{ value = "${group}" \\}`))
    assert.match(production, new RegExp(`resource "render_env_group_link" "${service}" \\{\\s*env_group_id = render_env_group\\.${service}\\.id\\s*service_ids  = \\[render_web_service\\.${service}\\.id\\]`))
  }
})
test('production database keeps plan version HA and non-empty allow-list', () => {
  const database = production.split('resource "render_postgres" "production" {')[1].split('\nresource ')[0]
  value(database, 'plan', 'basic_1gb')
  value(database, 'version', '17')
  assert.match(database, /high_availability_enabled\s*=\s*false/)
  assert.match(database, /ip_allow_list\s*=\s*var.production_postgres_ip_allow_list/)
  assert.match(database, /length\(var.production_postgres_ip_allow_list\) > 0/)
})
