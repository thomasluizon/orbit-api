# Orbit infrastructure

This directory is one Terraform state for the Render production and staging resources, Cloudflare DNS and Turnstile, and AWS SSM parameters. `production.tf` imports the existing API and declares the production database, web service, and landing site. `staging.tf` declares the staging database and services. `configuration.tf` owns four environment groups and their AWS SSM inputs. `cloudflare.tf` owns the zone, existing DNS records, widget, and its secret parameters. `ses.tf` owns SES identities, new DNS records, event routing, and send credentials. `variables.tf` contains the cutover switches and image digests. `versions.tf` pins the providers and configures the encrypted S3 state with native locking.

The Render provider reads `RENDER_API_KEY` from the environment. The Cloudflare provider reads `CLOUDFLARE_API_TOKEN` from the environment. The AWS provider uses the default credential chain in `us-east-2`. The S3 state contains decrypted SecureString values, including the Turnstile and SES secrets, so access to the bucket and its lock file must stay restricted. The existing API is imported with its service ID; do not remove that import block or replace the service.

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

Terraform creates `/orbit/<environment>/api/Ses__AccessKeyId` and `/orbit/<environment>/api/Ses__SecretAccessKey` as SecureString parameters and places them in both Render API environment groups. The one `orbit-api-ses` IAM user sends only through the two SES identities and configuration sets. The groups also provide `Ses__Region`, `Ses__FromEmail`, `Ses__MarketingFromEmail`, `Ses__SupportEmail`, `Ses__TransactionalConfigurationSet`, `Ses__MarketingConfigurationSet`, and `Ses__TopicArn`. `Email__Provider` comes from separate `production_email_provider` and `staging_email_provider` Terraform variables, each defaulting to `Resend`. After SES production access and identity verification are complete, set the intended environment's variable to `Ses` in `infra/local.tfvars`, plan, and apply. Roll back by setting it to `Resend` in that file, planning, and applying again. The SNS subscription targets the production API endpoint and confirms through the signed webhook.

The SES DKIM CNAME records and the MAIL FROM MX and SPF records are managed directly in `ses.tf` using the SES identity outputs. They are unproxied and use `bounce.send.useorbit.org` and `bounce.updates.useorbit.org`, leaving the existing Resend records intact. The Cloudflare zone is created on the Free plan without a zone subscription resource because that resource requires billing scope unavailable to the DNS token.

Terraform creates `/orbit/production/api/BotProtection__SecretKey` and `/orbit/staging/api/BotProtection__SecretKey` as SecureString parameters from the managed Turnstile widget. Both API environment groups read the corresponding parameter. `BotProtection__Enabled` remains at its current setting. The public `turnstile_site_key` output is for the web and landing builds.

## DNS cutover

The Cloudflare zone uses full DNS setup on the Free plan. The existing records have automatic TTL and remain unproxied. The default `dns_apex_target`, `dns_www_target`, and `dns_app_target` values point to the current Vercel destinations. Change those variables only during the later Render cutover. `api` continues to point to its existing Render service. Staging CNAMEs take their hostnames from the staging Render resources.

After apply, read the `cloudflare_name_servers` output. Before you change the delegation, run `bash infra/check-dns-cutover.sh <cloudflare-nameserver>` once for each of those nameservers. The script asks the Cloudflare nameserver directly, so it verifies the new zone while Spaceship still serves live traffic. It compares record values and MX priorities while ignoring TTL and TXT chunk boundaries. Switch the nameservers at Spaceship only when every run prints that all answers match. After the switch propagates, run the script again for each nameserver and confirm that `dig NS useorbit.org +short` returns the Cloudflare nameservers.

## Plan and apply

Set `RENDER_API_KEY` and `CLOUDFLARE_API_TOKEN` in the shell and provide AWS credentials through the default credential chain. Make a local `infra/local.tfvars` containing the published production and staging image digests and any approved domain or database cutover settings. This file is ignored; `example.tfvars` shows the shape only. Run:

```sh
terraform -chdir=infra init
terraform fmt -check -recursive infra
terraform -chdir=infra validate
terraform -chdir=infra plan -var-file=local.tfvars -out=local.tfplan
terraform -chdir=infra apply local.tfplan
```

The initial plan must keep `srv-d6tc2isr85hc739bf75g`, its URL, and `api.useorbit.org` in place. Terraform ignores the imported API's own environment variables (`lifecycle.ignore_changes`), so the first apply only creates `orbit-production-api` and links it; the service keeps its existing variables, which override the group's identical values. After a deploy proves the service healthy on the linked group, remove the duplicated direct variables through the Render API, leaving only `ORBIT_TERRAFORM_ENV_GROUP`. Check the imported service's plan before applying: it must show no change to the service.

Staging deliberately carries no production integration identifiers: Stripe price, product and publishable values are `*_staging_unset` placeholders with staging return URLs (billing is unavailable on staging until Stripe test-mode values exist), and `Supabase__Url` points at an invalid host so staging can never write to production storage. The web image must exist at both selected digests before the first apply. Custom domains for production web and landing remain empty until cutover. DNS and certificate verification follow the domain changes.
