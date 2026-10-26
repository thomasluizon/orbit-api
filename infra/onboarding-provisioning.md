# Anonymous onboarding provisioning

`onboarding.tf` generates a separate 64-character signing secret for each environment,
stores it at `/orbit/<environment>/api/OnboardingSecurity__SigningSecret` as an AWS
SSM SecureString, and supplies the API environment groups in `configuration.tf`.
Keep these secrets stable: replacing one invalidates that environment's outstanding
install credentials. Terraform state contains decrypted secrets and needs restricted access.

The desktop shell loads the hosted app, so its origin is `https://app.useorbit.org`
or `https://app-staging.useorbit.org`. It needs no custom-scheme CORS origin.
Production also allows the apex and www landing origins. Staging also allows its
legacy web origin and Render landing origin. The Turnstile hostname list is the
intersection of each environment's origins with `cloudflare_turnstile_widget.orbit.domains`.
The existing widget supports the staging app hostname; it does not support
`staging.useorbit.org` or `orbit-landing-staging.onrender.com`. Those origins can pass
CORS but cannot supply a valid proof until the widget supports their hostnames.

## Mobile inputs

In AWS Systems Manager > Parameter Store in `us-east-2`, the orchestrator supplies
the following parameters beneath `/orbit/<environment>/api/`. Use SecureString for
credentials. Array suffixes start at zero; create further numbered parameters for
additional accepted values.

| Parameter suffix | Source |
| --- | --- |
| `OnboardingSecurity__PlayIntegrityCredentialsJson` | Service account authorized to decode Play Integrity tokens for this environment's app |
| `OnboardingSecurity__AndroidPackageName` | Environment's Google Play app package name |
| `OnboardingSecurity__AndroidCertificateDigests__0` | Play app signing certificate SHA-256 digest matching Play Integrity's certificate digest value |
| `OnboardingSecurity__AppleTeamId` | Enrolled Apple Developer team identifier |
| `OnboardingSecurity__AppleBundleId` | Environment's registered Apple app bundle identifier |
| `OnboardingSecurity__AppleBundleVersions__0` | Accepted app bundle version |
| `OnboardingSecurity__AppleValidationCategories__0` | Accepted numeric App Attest validation category |

The path lookup forwards only those scalar names and numbered array entries.
Missing parameters are omitted from the environment group. Leave Apple parameters
absent until enrollment and the accepted app values are available. Do not create
empty numeric array parameters: configuration binding requires a valid unsigned
integer. Missing platform configuration keeps that platform closed. Google Play
billing credentials are not reused implicitly.

## Apply and release

Load `RENDER_API_KEY` from Render Account Settings > API Keys and
`CLOUDFLARE_API_TOKEN` from the existing operator credential, with AWS access to
the Terraform backend and SSM. Use the existing ignored `local.tfvars` containing
the live infrastructure inputs. From the repository root:

```sh
terraform -chdir=infra init -input=false
terraform -chdir=infra plan -input=false -var-file=local.tfvars \
  -target=render_env_group.staging_api \
  '-target=aws_ssm_parameter.onboarding_signing_secret["staging"]' \
  -out=local.onboarding-staging.tfplan
terraform -chdir=infra show local.onboarding-staging.tfplan
terraform -chdir=infra show -json local.onboarding-staging.tfplan | node infra/check-web-plan.mjs
terraform -chdir=infra apply local.onboarding-staging.tfplan
```

Before applying, require that the plan only creates the selected environment's
onboarding password and SSM parameter and adds onboarding entries to its API
environment group. Stop on any unrelated change, removal, replacement, web service
update, or production change in the staging plan. Do not apply an unreviewed plan.

Release the carrying commit through GitHub Actions > Release API with environment
`staging` and its explicit commit SHA. The release applies `SeedOnboardingBudgets`.
In Render > staging Postgres > Connections, use the external connection details
and existing operator network access to read:

```sql
SELECT "Key", "Value"
FROM "AppConfigs"
WHERE "Key" IN (
  'OnboardingIpCallsPerHour', 'OnboardingInstallCallsPerHour',
  'OnboardingCallsPerDay', 'OnboardingTokensPerDay'
)
ORDER BY "Key";
```

Expect `10`, `10`, `1000` and `16384000` for the corresponding keys. Existing
processes can retain an AppConfig read for 30 minutes; verify with the released
process. In Render > Environment Groups > orbit-staging-api, confirm the indexed
origins and hostnames and the signing-secret entry without exposing its value.

From `https://app-staging.useorbit.org`, complete enrollment with a fresh Turnstile
proof, then request a schedule with another fresh proof and the signed install id.
Verify a reused proof, forged install id and unlisted origin are refused. Follow
the [request contract](../src/Orbit.Api/OpenApi/Onboarding.md) for credential rotation.
The orchestrator gathers this acceptance evidence; the worker does not start or
open the app.

After staging acceptance, repeat the reviewed targeted plan and apply for
`render_env_group.production_api` and
`aws_ssm_parameter.onboarding_signing_secret["production"]`. Release the same
carrying commit from `main` through Release API with environment `production`.
Read back the four keys through Render > production Postgres > Connections and
confirm the production entries in Environment Groups > orbit-production-api.
