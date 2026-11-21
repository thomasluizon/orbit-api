# Anonymous onboarding provisioning

`onboarding.tf` generates a separate 64-character signing secret for each environment,
stores it at `/orbit/<environment>/api/OnboardingSecurity__SigningSecret` as an AWS
SSM SecureString, and supplies the API environment groups in `configuration.tf`.
Keep these secrets stable: replacing one invalidates that environment's outstanding
install credentials. Terraform state contains decrypted secrets and needs restricted access.

The desktop shell loads the hosted app, so its origin is `https://app.useorbit.org`
or `https://app-staging.useorbit.org`. It needs no custom-scheme CORS origin.
Each environment allows only its app web origin. The landing page does not call
onboarding, and the web app forwards onboarding requests through its Server Action
BFF. The Turnstile hostname list is the intersection of each environment's origins
with `cloudflare_turnstile_widget.orbit.domains`, yielding only `app.useorbit.org`
for production and `app-staging.useorbit.org` for staging.

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

Run a targeted Terraform plan for the environment, then review it and apply it:

```sh
terraform -chdir=infra plan -input=false -var-file=local.tfvars -target=render_env_group.staging_api -target=aws_ssm_parameter.onboarding_signing_secret_staging -out=local.onboarding.tfplan
```

Stop on any change that the plan does not need. Then run the Release API workflow for the commit.
