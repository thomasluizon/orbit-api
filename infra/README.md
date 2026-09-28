# Orbit infrastructure

This directory is one Terraform state for the Render production and staging resources, Cloudflare DNS and Turnstile, and AWS SSM parameters. `production.tf` imports the existing API and declares the production database, web service, and landing site. `staging.tf` declares the staging database and services. `configuration.tf` owns four environment groups and their AWS SSM inputs. `cloudflare.tf` owns the zone, DNS records, widget, and its secret parameters. `variables.tf` contains the cutover switches and image digests. `versions.tf` pins the providers and configures the encrypted S3 state with native locking.

The Render provider reads `RENDER_API_KEY` from the environment. The Cloudflare provider reads `CLOUDFLARE_API_TOKEN` from the environment. The AWS provider uses the default credential chain in `us-east-2`. The S3 state contains decrypted SecureString values, including the Turnstile secret, so access to the bucket and its lock file must stay restricted. The existing API is imported with its service ID; do not remove that import block or replace the service.

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

The staging billing IDs below are String parameters. Terraform reads them into the staging API environment group. Keep their values in SSM rather than in Terraform files.

| Service | String parameter |
| --- | --- |
| Staging API | `/orbit/staging/api/Stripe__ProProductId` |
| Staging API | `/orbit/staging/api/Stripe__MonthlyPriceIdUsd` |
| Staging API | `/orbit/staging/api/Stripe__YearlyPriceIdUsd` |
| Staging API | `/orbit/staging/api/Stripe__MonthlyPriceIdBrl` |
| Staging API | `/orbit/staging/api/Stripe__YearlyPriceIdBrl` |

Every staging secret must be independently generated or issued for staging. `/orbit/staging/api/Stripe__SecretKey` and `/orbit/staging/api/Stripe__WebhookSecret` are SecureString parameters containing Stripe test-mode credentials. The staging webhook endpoint is `https://api-staging.useorbit.org/api/subscriptions/webhook`. It must deliver `checkout.session.completed`, `invoice.paid`, `invoice.payment_failed`, `customer.subscription.deleted`, and `customer.subscription.updated`. The test product and prices mirror the production monthly and yearly plans in USD and BRL. Use a Stripe test card to confirm checkout and the signed webhook grant Pro without a charge.

Google Play uses the configured service account for both live and license-tester subscription purchases. Set up license testers on the internal and closed tracks, then verify a test purchase through `play/verify`. The staging Pub/Sub push subscription must use `https://api-staging.useorbit.org/api/subscriptions/play/rtdn` as both its endpoint and OIDC audience, with the configured `GooglePlay__RtdnServiceAccountEmail` as the token issuer. Confirm an RTDN push reaches staging and updates the linked purchase. The production API's non-secret values mirror the existing Render service; staging overrides its environment, CORS origin, return URLs, RTDN audience, and billing IDs.

Terraform creates `/orbit/production/api/BotProtection__SecretKey` and `/orbit/staging/api/BotProtection__SecretKey` as SecureString parameters from the managed Turnstile widget. Both API environment groups read the corresponding parameter. `BotProtection__Enabled` remains at its current setting. The public `turnstile_site_key` output is for the web and landing builds.

## DNS cutover

The Cloudflare zone uses full DNS setup on the Free plan. The existing records have automatic TTL and remain unproxied. The default `dns_apex_target`, `dns_www_target`, and `dns_app_target` values point to the current Vercel destinations. Change those variables only during the later Render cutover. `api` continues to point to its existing Render service. Staging CNAMEs take their hostnames from the staging Render resources.

After apply, read the `cloudflare_name_servers` output. Before you change the delegation, run `bash infra/check-dns-cutover.sh <cloudflare-nameserver>` once for each of those nameservers. The script asks the Cloudflare nameserver directly, so it verifies the new zone while Spaceship still serves live traffic. It compares record values and MX priorities while ignoring TTL and TXT chunk boundaries. Switch the nameservers at Spaceship only when every run prints that all answers match. After the switch propagates, run the script again for each nameserver and confirm that `dig NS useorbit.org +short` returns the Cloudflare nameservers.

## Plan and apply

Set `RENDER_API_KEY` and `CLOUDFLARE_API_TOKEN` in the shell and provide AWS credentials through the default credential chain. Make a local `infra/local.tfvars` containing the published production and staging image digests and any approved domain or database cutover settings. This file is ignored; `example.tfvars` shows the shape only.

The `production_web_digest` and `staging_web_digest` variables seed the web images only when Terraform first creates each service. Terraform ignores later digest changes on both web services. The `web-image.yml` and `deploy-web.yml` release workflows own subsequent staging and production web deploys by digest.

With Render provider v1.9.1, a refreshed digest image path also populates a computed image tag. On any web service update, the provider sends an image reference built from the planned tag before considering the digest. Ignoring the entire image block retains that computed tag and does not make the update safe. Once either web service exists, do not apply changes to any attribute of `render_web_service.production_web` or `render_web_service.staging_web`, including plan, region, health check path, custom domains, or runtime source. Before every apply, inspect the plan and stop if either web service has an update. Defer those service-setting changes until the provider can preserve digest image paths on update.

After every apply, read each web service's live `imagePath` from Render using its service ID and compare it with the digest approved by its latest release workflow. The expected value is `ghcr.io/thomasluizon/orbit-web@sha256:<approved digest without the sha256: prefix>`. Do not use the Terraform digest variables as the expected value after creation. For each service, run:

```sh
curl -fsS -H "Authorization: Bearer $RENDER_API_KEY" \
  "https://api.render.com/v1/services/<service-id>" | jq -r '.imagePath'
```

If either image differs, stop further applies and redeploy the approved digest through the corresponding release workflow.

Changing a Render service's build or deploy settings through Terraform starts a Render deploy of the tracked branch head, including when the change is to an API service. Before applying such a change, arrange to pause or cancel that deploy, or apply only when an approved release of the same commit is intended.

Run:

```sh
terraform -chdir=infra init
terraform fmt -check -recursive infra
terraform -chdir=infra validate
terraform -chdir=infra plan -var-file=local.tfvars -out=local.tfplan
terraform -chdir=infra apply local.tfplan
```

The initial plan must keep `srv-d6tc2isr85hc739bf75g`, its URL, and `api.useorbit.org` in place. Terraform ignores the imported API's own environment variables (`lifecycle.ignore_changes`), so the first apply only creates `orbit-production-api` and links it; the service keeps its existing variables, which override the group's identical values. After a deploy proves the service healthy on the linked group, remove the duplicated direct variables through the Render API, leaving only `ORBIT_TERRAFORM_ENV_GROUP`. Check the imported service's plan before applying: it must show no change to the service.

Staging reads Stripe test-mode product and price IDs from SSM and uses staging return URLs. `Supabase__Url` points at an invalid host so staging cannot write to production storage. The web image must exist at both selected digests before the first apply. Custom domains for production web and landing remain empty until cutover. DNS and certificate verification follow the domain changes.
