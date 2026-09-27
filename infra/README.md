# Render infrastructure

This directory is one Terraform state for the Render production and staging resources. `production.tf` imports the existing API and declares the production database, web service, and landing site. `staging.tf` declares the staging database and services. `configuration.tf` owns four environment groups and their AWS SSM inputs. `variables.tf` contains the cutover switches and image digests. `versions.tf` pins the providers and configures the encrypted S3 state with native locking.

The Render provider reads `RENDER_API_KEY` from the environment. The AWS provider uses the default credential chain in `us-east-2`. The S3 state contains decrypted SecureString values, so access to the bucket and its lock file must stay restricted. The existing API is imported with its service ID; do not remove that import block or replace the service.

## SSM parameters

Create each entry as a SecureString in AWS Systems Manager Parameter Store in `us-east-2` before planning. Production API connection strings remain the existing Supabase Npgsql strings while `api_database = "supabase"`. They are no longer read when `api_database = "render"`. The Render database URL is converted to Npgsql's key and value connection string format for both API connection strings. Staging uses its own Render database from the start.

| Service | SecureString parameter |
| --- | --- |
| Production API | `/orbit/production/api/AI__ApiKey` |
| Production API | `/orbit/production/api/Encryption__Key` |
| Production API | `/orbit/production/api/Firebase__CredentialsJson` |
| Production API | `/orbit/production/api/GooglePlay__ServiceAccountJson` |
| Production API | `/orbit/production/api/Google__ClientSecret` |
| Production API | `/orbit/production/api/Jwt__SecretKey` |
| Production API | `/orbit/production/api/Marketing__UnsubscribeSigningKey` |
| Production API | `/orbit/production/api/PostHog__ApiKey` |
| Production API | `/orbit/production/api/Resend__ApiKey` |
| Production API | `/orbit/production/api/SMOKE_TEST_CODE` |
| Production API | `/orbit/production/api/SMOKE_TEST_EMAIL` |
| Production API | `/orbit/production/api/Sentry__Dsn` |
| Production API | `/orbit/production/api/Stripe__SecretKey` |
| Production API | `/orbit/production/api/Stripe__WebhookSecret` |
| Production API | `/orbit/production/api/Supabase__AnonKey` |
| Production API | `/orbit/production/api/Supabase__SecretKey` |
| Production API | `/orbit/production/api/TEST_ACCOUNTS` |
| Production API | `/orbit/production/api/Vapid__PrivateKey` |
| Production API | `/orbit/production/api/Waitlist__SigningKey` |
| Production API while using Supabase | `/orbit/production/api/ConnectionStrings__DefaultConnection` |
| Production API while using Supabase | `/orbit/production/api/ConnectionStrings__SessionConnection` |
| Production web | `/orbit/production/web/SENTRY_DSN` |
| Staging API | `/orbit/staging/api/AI__ApiKey` |
| Staging API | `/orbit/staging/api/Encryption__Key` |
| Staging API | `/orbit/staging/api/Firebase__CredentialsJson` |
| Staging API | `/orbit/staging/api/GooglePlay__ServiceAccountJson` |
| Staging API | `/orbit/staging/api/Google__ClientSecret` |
| Staging API | `/orbit/staging/api/Jwt__SecretKey` |
| Staging API | `/orbit/staging/api/Marketing__UnsubscribeSigningKey` |
| Staging API | `/orbit/staging/api/PostHog__ApiKey` |
| Staging API | `/orbit/staging/api/Resend__ApiKey` |
| Staging API | `/orbit/staging/api/SMOKE_TEST_CODE` |
| Staging API | `/orbit/staging/api/SMOKE_TEST_EMAIL` |
| Staging API | `/orbit/staging/api/Sentry__Dsn` |
| Staging API | `/orbit/staging/api/Stripe__SecretKey` |
| Staging API | `/orbit/staging/api/Stripe__WebhookSecret` |
| Staging API | `/orbit/staging/api/Supabase__AnonKey` |
| Staging API | `/orbit/staging/api/Supabase__SecretKey` |
| Staging API | `/orbit/staging/api/TEST_ACCOUNTS` |
| Staging API | `/orbit/staging/api/Vapid__PrivateKey` |
| Staging API | `/orbit/staging/api/Waitlist__SigningKey` |
| Staging web | `/orbit/staging/web/SENTRY_DSN` |

Every staging secret must be independently generated or issued for staging. The production API's non-secret values mirror the existing Render service. Staging currently inherits the same public third-party identifiers except for its environment, CORS origin, and URL-specific web settings. Replace those identifiers when separate staging integrations are available.

## Plan and apply

Set `RENDER_API_KEY` in the shell and provide AWS credentials through the default credential chain. Make a local `infra/local.tfvars` containing the published production and staging image digests and any approved domain or database cutover settings. This file is ignored; `example.tfvars` shows the shape only. Run:

```sh
terraform -chdir=infra init
terraform fmt -check -recursive infra
terraform -chdir=infra validate
terraform -chdir=infra plan -var-file=local.tfvars -out=local.tfplan
terraform -chdir=infra apply local.tfplan
```

The initial plan must keep `srv-d6tc2isr85hc739bf75g`, its URL, and `api.useorbit.org` in place. It moves the API's direct environment variables into `orbit-production-api`. The provider requires one direct variable on a web service, so the API retains only `ORBIT_TERRAFORM_ENV_GROUP` outside its group. Check the imported service's plan before applying. The web image must exist at both selected digests before the first apply. Custom domains for production web and landing remain empty until cutover. DNS and certificate verification follow the domain changes.
