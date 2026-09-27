# Render infrastructure

This directory is one Terraform state for the Render production and staging resources. `production.tf` imports the existing API and declares the production database, web service, and landing site. `staging.tf` declares the staging database and services. `configuration.tf` owns four environment groups and their AWS SSM inputs. `variables.tf` contains the cutover switches and image digests. `versions.tf` pins the providers and configures the encrypted S3 state with native locking.

The Render provider reads `RENDER_API_KEY` from the environment. The AWS provider uses the default credential chain in `us-east-2`. The S3 state contains decrypted SecureString values, so access to the bucket and its lock file must stay restricted. The existing API is imported with its service ID; do not remove that import block or replace the service.

## Staging lifecycle

The staging keepalive workflow calls the API health endpoint every five minutes from 08:00 through 23:55 in Sao Paulo. The reseed workflow checks the Render Postgres creation time daily and replaces the staging database once it is at least 26 days old. A manual dispatch replaces it immediately. The replacement plan is restricted to `render_postgres.staging` and its staging API environment group, and the workflow rejects a plan that changes another resource.

Create the GitHub OIDC provider and staging reseed role from `github_oidc.tf` with a reviewed targeted Terraform apply before enabling the workflow. If the GitHub OIDC provider already exists in the AWS account, import it into this state before applying. Set `RENDER_API_KEY` in GitHub Actions secrets from Render Account Settings > API Keys, and `SEED_OWNER_EMAIL` in GitHub Actions variables to the owner's Google sign-in email. The role trust policy accepts only workflows on this repository's `main` branch.

After the API has migrated the new database, the workflow invokes the seed command using the database connection returned by the staging Terraform resource. To run the same command manually from the repository root after `terraform -chdir=infra init`:

```sh
ASPNETCORE_ENVIRONMENT=Staging \
Seed__OwnerEmail=owner@example.com \
Seed__ExpectedHost="$(terraform -chdir=infra output -raw staging_external_host)" \
Seed__DatabaseUrl="$(terraform -chdir=infra output -raw staging_external_connection_string)" \
dotnet run --project src/Orbit.Api/Orbit.Api.csproj -- seed-staging
```

The command checks the environment, staging host, database name, and database user before opening a connection. It reuses the owner's account by email and adds missing sample records without duplicating existing ones.

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

The initial plan must keep `srv-d6tc2isr85hc739bf75g`, its URL, and `api.useorbit.org` in place. Terraform ignores the imported API's own environment variables (`lifecycle.ignore_changes`), so the first apply only creates `orbit-production-api` and links it; the service keeps its existing variables, which override the group's identical values. After a deploy proves the service healthy on the linked group, remove the duplicated direct variables through the Render API, leaving only `ORBIT_TERRAFORM_ENV_GROUP`. Check the imported service's plan before applying: it must show no change to the service.

Staging deliberately carries no production integration identifiers: Stripe price, product and publishable values are `*_staging_unset` placeholders with staging return URLs (billing is unavailable on staging until Stripe test-mode values exist), and `Supabase__Url` points at an invalid host so staging can never write to production storage. The web image must exist at both selected digests before the first apply. Custom domains for production web and landing remain empty until cutover. DNS and certificate verification follow the domain changes.
