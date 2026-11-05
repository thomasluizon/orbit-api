import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
const workflow = readFileSync(new URL('../.github/workflows/production-plan.yml', import.meta.url), 'utf8')
const oidc = readFileSync(new URL('./github_oidc.tf', import.meta.url), 'utf8')
test('production plan waits for credential provisioning and runs only on main with a read-only role', () => {
  assert.doesNotMatch(workflow, /^  push:/m)
  assert.match(workflow, /schedule:/)
  assert.match(workflow, /workflow_dispatch:/)
  assert.doesNotMatch(workflow, /pull_request:/)
  assert.match(workflow, /if: github.ref == 'refs\/heads\/main'/)
  assert.match(workflow, /environment: render-operations/)
  assert.match(workflow, /role-to-assume: arn:aws:iam::713285551626:role\/orbit-production-plan/)
  assert.match(workflow, /-lock=false/)
  for (const address of ['render_web_service.production_api', 'render_web_service.production_web', 'render_env_group.production_api']) assert.ok(workflow.includes(`-target=${address}`))
  assert.match(workflow, /show -json .*\| node infra\/check-web-plan.mjs/)
  assert.match(workflow, /show -json .*\| node infra\/check-production-plan.mjs/)
  assert.doesNotMatch(workflow, /upload-artifact|terraform .* apply|tee |cat .*plan/)
})
test('plan role authorizes the configured SSM hierarchy roots and retains child reads', () => {
  const onboarding = readFileSync(new URL('./onboarding.tf', import.meta.url), 'utf8')
    .split('data "aws_ssm_parameters_by_path" "onboarding_platform" {')[1].split('\n}')[0]
  const environments = JSON.parse(onboarding.match(/for_each\s*=\s*toset\((\[[^\]]+\])\)/)[1])
  const path = onboarding.match(/path\s*=\s*"([^"]+)"/)[1]
  const policy = oidc.split('data "aws_iam_policy_document" "production_plan" {')[1]
    .split('\nresource "aws_iam_role_policy"')[0]
  const statements = policy.split('  statement {').slice(1)
  const resourcesFor = action => statements.filter(statement => statement.includes(`"${action}"`)).flatMap(statement => {
    const resources = statement.match(/resources\s*=\s*\[for environment in (\[[^\]]+\]) : "([^"]+)"\]/)
    assert.ok(resources, `${action} must have explicitly scoped environment resources`)
    return JSON.parse(resources[1]).map(environment => resources[2].replace('${environment}', environment))
  })
  const roots = resourcesFor('ssm:GetParametersByPath')
  const children = resourcesFor('ssm:GetParameter')
  for (const environment of environments) {
    const arn = `arn:aws:ssm:us-east-2:713285551626:parameter${path.replace('${each.key}', environment)}`
    assert.ok(roots.includes(arn), `GetParametersByPath must authorize its requested root ${arn}`)
    assert.ok(children.includes(`${arn}/*`), `Parameter reads must retain the scoped children of ${arn}`)
  }
  assert.equal(roots.length, environments.length)
})
test('Sonar imports coverage generated for the production plan checker', () => {
  const sonar = readFileSync(new URL('../.github/workflows/sonarcloud.yml', import.meta.url), 'utf8')
  const reports = sonar.match(/sonar.javascript.lcov.reportPaths="([^"]+)"/)[1].split(',')
  const report = 'coverage/production-plan.lcov'
  assert.ok(reports.includes(report), 'Production plan coverage must be imported into Sonar')
  assert.ok(sonar.includes(`--test-coverage-include=infra/check-production-plan.mjs --test-reporter=lcov --test-reporter-destination=${report} infra/check-production-plan.test.mjs`))
})
test('plan role grants only reads with one state object and bounded provider dependencies', () => {
  const role = oidc.split('resource "aws_iam_role" "production_plan"')[1]
  assert.match(role, /assume_role_policy = data.aws_iam_policy_document.staging_reseed_trust.json/)
  assert.match(oidc, /repo:thomasluizon\/orbit-api:environment:render-operations/)
  assert.match(role, /actions\s*=\s*\["s3:GetObject"\]\s*resources\s*=\s*\["arn:aws:s3:::orbit-terraform-state-713285551626\/render\/terraform.tfstate"\]/)
  assert.doesNotMatch(role, /:(?:Put|Delete|Create|Update|Send|Tag|Untag|Attach|Detach|AssumeRole)\w*"/)
  assert.match(role, /kms:ViaService/)
  assert.match(role, /kms:EncryptionContext:PARAMETER_ARN/)
})
test('plan role reads upload bucket tags through the S3 Control call the locked AWS provider makes', () => {
  const lock = readFileSync(new URL('./.terraform.lock.hcl', import.meta.url), 'utf8')
  const major = Number(lock.split('provider "registry.terraform.io/hashicorp/aws" {')[1].match(/version\s*=\s*"(\d+)\./)[1])
  assert.ok(major >= 6, 'AWS provider 6 reads S3 bucket tags with s3:ListTagsForResource')
  const policy = oidc.split('data "aws_iam_policy_document" "production_plan" {')[1]
    .split('\nresource "aws_iam_role_policy"')[0]
  const uploads = policy.split('  statement {').slice(1)
    .filter(statement => statement.includes('arn:aws:s3:::orbit-uploads-${environment}-713285551626'))
  assert.equal(uploads.length, 1)
  for (const action of ['s3:GetBucketTagging', 's3:ListTagsForResource']) {
    assert.ok(uploads[0].includes(`"${action}"`), `Upload bucket refresh needs ${action}`)
  }
  assert.match(uploads[0], /for environment in \["production", "staging"\]/)
})
