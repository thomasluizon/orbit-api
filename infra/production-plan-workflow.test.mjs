import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
const workflow = readFileSync(new URL('../.github/workflows/production-plan.yml', import.meta.url), 'utf8')
const oidc = readFileSync(new URL('./github_oidc.tf', import.meta.url), 'utf8')
test('production plan runs only on main with an environment and read-only role', () => {
  assert.match(workflow, /push:\s*branches: \[main\]/)
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
test('plan role grants only reads with one state object and bounded provider dependencies', () => {
  const role = oidc.split('resource "aws_iam_role" "production_plan"')[1]
  assert.match(role, /assume_role_policy = data.aws_iam_policy_document.staging_reseed_trust.json/)
  assert.match(oidc, /repo:thomasluizon\/orbit-api:environment:render-operations/)
  assert.match(role, /actions\s*=\s*\["s3:GetObject"\]\s*resources\s*=\s*\["arn:aws:s3:::orbit-terraform-state-713285551626\/render\/terraform.tfstate"\]/)
  assert.doesNotMatch(role, /:(?:Put|Delete|Create|Update|Send|Tag|Untag|Attach|Detach|AssumeRole)\w*"/)
  assert.match(role, /kms:ViaService/)
  assert.match(role, /kms:EncryptionContext:PARAMETER_ARN/)
})
