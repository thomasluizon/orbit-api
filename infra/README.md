# Orbit infrastructure

The main Terraform state owns the Render production resources and the staging services, Cloudflare DNS and Turnstile, and AWS SSM parameters. `production.tf` imports the existing API and declares the production database, web service, and landing site. `staging.tf` declares the staging API, web service, and landing site. `configuration.tf` owns the service environment groups and their AWS SSM inputs. `cloudflare.tf` owns the zone, existing DNS records, widget, and its secret parameters. `ses.tf` owns SES identities, new DNS records, event routing, and send credentials. `variables.tf` contains the cutover switches and image digests. `versions.tf` pins the providers and configures the encrypted S3 state with native locking. `staging-database/` is a separate Terraform root that owns only the staging Postgres, its connection environment group, and the link to the staging API.

`uploads.tf` creates a private, encrypted S3 uploads bucket and a scoped IAM user in each environment. Terraform stores each user's access key in SecureString parameters at `/orbit/<environment>/api/Storage__S3__AccessKeyId` and `/orbit/<environment>/api/Storage__S3__SecretAccessKey`, then passes them to the API environment group. The groups keep `Storage__Provider=Supabase` until the storage cutover. Set `production_storage_provider` or `staging_storage_provider` to `S3` in `local.tfvars` for the chosen environment after its bucket and key are provisioned. Keep the S3 bucket and credentials configured when switching new uploads back to Supabase so existing S3 read links keep working. The S3 upload URL expires after 10 minutes. The stable API read URL redirects to a private S3 read URL that expires after 10 minutes. Browsers may cache the redirect for 5 minutes. The API allows 600 read redirects per object per minute, so image pages and viewers sharing an IP have separate budgets for each object.

The Render provider reads `RENDER_API_KEY` from the environment. The Cloudflare provider reads `CLOUDFLARE_API_TOKEN` from the environment. The AWS provider uses the default credential chain in `us-east-2`. The main S3 state contains decrypted SecureString values, including the Turnstile and SES secrets, so access to that state and its lock file must stay restricted. The staging reseed role can access only the separate staging database state. The existing API is imported with its service ID; do not remove that import block or replace the service.

## Staging lifecycle

The staging web app uses `https://app-staging.useorbit.org`. Keep `staging.useorbit.org` as a custom domain on `orbit-web-staging`; the web proxy redirects requests on that host permanently to the new host. Add the new domain to the live Render service before applying the targeted Terraform changes, and confirm the plan has no update to `render_web_service.staging_web`. Update the `STAGING_NEXT_PUBLIC_SITE_URL` repository variable in `orbit-ui-mobile` to `https://app-staging.useorbit.org`, and add `https://app-staging.useorbit.org/auth-callback` to the Google OAuth web client's redirect URIs. Release the staging API and web from `redesign/main`, then verify the new health route, old-host redirect, email sign-in, and Google authorization.

The staging keepalive workflow calls the API health endpoint every five minutes from 08:00 through 23:55 in Sao Paulo. The reseed workflow checks the Render Postgres creation time daily and replaces the staging database once it is at least 26 days old. A manual dispatch replaces it immediately. The replacement plan is restricted to `render_postgres.staging` and its staging database environment group, and the workflow rejects a plan that changes another resource. For migrations and the owner seed, the workflow temporarily adds the GitHub runner's public IPv4 `/32` to the staging database allow list. It restores and verifies the operator list after each command, including when a command fails. If the reseed job times out, `staging-postgres-access-reconcile.yml` runs independently after completion and on a recurring schedule to remove a tagged runner address. It preserves any other live addresses and fails when those differ from the configured operator list. Between the commands, the reseed workflow triggers an API deploy and waits for that deploy to become live.

Create the GitHub OIDC provider and staging reseed role from `github_oidc.tf` with a reviewed targeted Terraform apply before enabling the workflow. If the GitHub OIDC provider already exists in the AWS account, import it into this state before applying. Set `RENDER_API_KEY` in GitHub Actions secrets from Render Account Settings > API Keys. Set `SEED_OWNER_EMAIL`, `STAGING_ENVIRONMENT_ID`, and `STAGING_API_SERVICE_ID` in GitHub Actions variables. The IDs come from the main Terraform state's `render_project.orbit.environments["Staging"].id` and `render_web_service.staging_api.id`. The role trust policy accepts only workflows on this repository's `main` branch.

Set `STAGING_POSTGRES_IP_ALLOW_LIST` in GitHub Actions variables to the same JSON array of `cidr_block` and `description` objects used for `staging_postgres_ip_allow_list` in the staging database root. Use `[]` when no external addresses are allowed. The reseed workflow uses that value when it creates a replacement database and restores the list after temporary runner access.

Move the staging database state before applying either root. With credentials for the main state and the Render API, record its ID, remove its old state entry, initialize the new root, and import that ID:

```sh
database_id="$(terraform -chdir=infra state show -no-color render_postgres.staging | awk -F'"' '/^ +id +=/ {print $2; exit}')"
export TF_VAR_staging_environment_id='<staging environment ID from Render dashboard>'
export TF_VAR_staging_api_service_id='<staging API service ID from Render dashboard>'
terraform -chdir=infra state rm render_postgres.staging
terraform -chdir=infra/staging-database init
terraform -chdir=infra/staging-database import render_postgres.staging "$database_id"
```

Before the first apply that creates or updates `orbit-staging-database`, copy the existing staging data from `orbit_staging` into the Render URL database `orbit_staging_jo8c`. Linking the new group changes the staging API connection immediately. Quiesce staging writes before the dump and keep them stopped until both group updates are applied and the copied records are verified. On an operator machine with PostgreSQL client tools, obtain the external host, port, database user, and password from the Render Postgres Connections screen. Set `PGHOST`, `PGPORT`, `PGUSER`, `PGPASSWORD`, and `PGSSLMODE=require` in the operator shell. Confirm the destination has no independent data, then run the dump and restore from a protected directory outside the repository:

```sh
pg_dump --dbname=orbit_staging --format=custom --no-owner --no-acl --file=staging-cutover.dump
pg_restore --dbname=orbit_staging_jo8c --clean --if-exists --no-owner --no-acl staging-cutover.dump
for database in orbit_staging orbit_staging_jo8c; do
  psql --dbname="$database" --no-psqlrc -Atc 'SELECT (SELECT count(*) FROM "Users"), (SELECT count(*) FROM "Habits")'
done
```

Compare the counts and spot-check the same user and habit records in both databases. Review the staging root plan and confirm `render_postgres.staging` is not replaced, its IP allow list matches the live Render list, and both connection strings name `orbit_staging_jo8c`. Apply the staging root to create and link `orbit-staging-database`, then apply the main root to remove only the two database connection values from `orbit-staging-api`. Review both plans before applying. Confirm the staging API reads the copied records through the new group before allowing writes. Keep `orbit_staging` intact until that verification succeeds; then connect to `orbit_staging_jo8c` and run `DROP DATABASE orbit_staging`. The new group contains only `ConnectionStrings__DefaultConnection` and `ConnectionStrings__SessionConnection`; the existing `orbit-staging-api` group retains the other settings. After the main apply, `staging_environment_id` and `staging_api_service_id` outputs give the values for the GitHub variables. The Render provider documentation for version 1.9.1 confirms that both `render_postgres` and `render_env_group` can be imported by ID. The new group is created, so it needs no import.

After the API has migrated the new database, the workflow invokes the seed command using the database connection returned by the staging Terraform resource. To run the same command manually from the repository root after `terraform -chdir=infra/staging-database init`:

```sh
ASPNETCORE_ENVIRONMENT=Staging \
Seed__OwnerEmail=owner@example.com \
Seed__ExpectedHost="$(terraform -chdir=infra/staging-database output -raw staging_external_host)" \
Seed__DatabaseUrl="$(terraform -chdir=infra/staging-database output -raw staging_external_connection_string)" \
dotnet run --project src/Orbit.Api/Orbit.Api.csproj --no-launch-profile -- seed-staging
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

Terraform creates `/orbit/<environment>/api/Ses__AccessKeyId` and `/orbit/<environment>/api/Ses__SecretAccessKey` as SecureString parameters and places the matching values in each Render API environment group. `orbit-api-ses` can send with the production configuration sets; `orbit-api-ses-staging` can send with the staging sets. Both users can send from the two shared SES identities. The production sets `orbit-transactional` and `orbit-marketing` publish to `orbit-ses-events`, which subscribes only to `https://api.useorbit.org/api/email/ses-events`. The staging sets `orbit-staging-transactional` and `orbit-staging-marketing` publish to `orbit-ses-events-staging`, which subscribes only to `https://api-staging.useorbit.org/api/email/ses-events`. Each API group receives its own `Ses__TransactionalConfigurationSet`, `Ses__MarketingConfigurationSet`, and `Ses__TopicArn`. Both groups also provide `Ses__Region`, `Ses__FromEmail`, `Ses__MarketingFromEmail`, and `Ses__SupportEmail`. `Email__Provider` comes from separate `production_email_provider` and `staging_email_provider` Terraform variables, each defaulting to `Resend`. After SES production access and identity verification are complete, set the intended environment's variable to `Ses` in `infra/local.tfvars`, plan, and apply. Roll back by setting it to `Resend` in that file, planning, and applying again. Confirm each SNS subscription through its own signed webhook before enabling that environment's SES provider.

Each SNS HTTPS subscription retains failed deliveries for up to 14 days in its own SQS dead-letter queue: `orbit-ses-events-dead-letter` for production and `orbit-ses-events-staging-dead-letter` for staging. A CloudWatch alarm on each queue enters ALARM when `ApproximateNumberOfMessagesVisible` reaches one and publishes to its matching operations topic, `orbit-ses-dead-letter-alerts` or `orbit-ses-dead-letter-alerts-staging`. Set `production_ses_dlq_alert_email` and `staging_ses_dlq_alert_email` in the ignored `infra/local.tfvars` before applying Terraform. Both default to an empty value, which creates the alarm and topic without an email recipient. Confirm each subscription through the email sent by SNS. In the AWS SNS console in `us-east-2`, check that neither operations topic has a subscription marked `PendingConfirmation`. To verify delivery, use the AWS SQS console to send a test message to each matching dead-letter queue, confirm its CloudWatch alarm enters ALARM and the matching recipient receives the alert, then delete the test message. Investigate any real alarm before retention expires. Once the webhook and database are healthy, recover messages in the matching environment:

1. In the AWS SQS console in `us-east-2`, open the matching queue and use **Send and receive messages** to inspect one message. Identify the original SES event payload from the actual message body. Keep the message if its payload cannot be identified.
2. In the AWS SNS console, open the matching topic (`orbit-ses-events` or `orbit-ses-events-staging`) and use **Publish message** with that original event payload. SNS signs a new webhook envelope for the same environment. Do not publish production events to staging or staging events to production.
3. Confirm the webhook accepted the event and the affected marketing contact is suppressed for a permanent bounce or complaint. Delete the original SQS message only after that confirmation. If processing fails, leave it in the queue for another attempt.

The SES DKIM CNAME records and the MAIL FROM MX and SPF records are managed directly in `ses.tf` using the SES identity outputs. They are unproxied and use `bounce.send.useorbit.org` and `bounce.updates.useorbit.org`, leaving the existing Resend records intact. The Cloudflare zone is created on the Free plan without a zone subscription resource because that resource requires billing scope unavailable to the DNS token.

Terraform creates `/orbit/production/api/BotProtection__SecretKey` and `/orbit/staging/api/BotProtection__SecretKey` as SecureString parameters from the managed Turnstile widget. Both API environment groups read the corresponding parameter. `BotProtection__Enabled` remains at its current setting. The public `turnstile_site_key` output is for the web and landing builds.

## DNS cutover

The Cloudflare zone uses full DNS setup on the Free plan. The existing records have automatic TTL and remain unproxied. At cutover, use these values in `infra/local.tfvars`:

```hcl
dns_apex_target        = "orbit-landing-aaa7.onrender.com"
dns_www_target         = "orbit-landing-aaa7.onrender.com"
dns_app_target         = "orbit-web-3qmv.onrender.com"
landing_custom_domains = ["useorbit.org"]
web_custom_domains     = ["app.useorbit.org"]
```

Render adds `www.useorbit.org` as a redirect to `useorbit.org` when the apex is added as a landing custom domain. Cloudflare flattens the unproxied apex CNAME into A answers while leaving its MX and TXT records in place. `api` continues to point to its existing Render service. Staging CNAMEs take their hostnames from the staging Render resources.

After apply, read the `cloudflare_name_servers` output. Run `bash infra/check-dns-cutover.sh <cloudflare-nameserver> [apex-target] [app-target] [www-target]` once for each nameserver. The optional targets default to the Render hostnames above; pass all three configured targets in that order if any differs. The script compares the apex's flattened A answers with the Render target's A answers, checks the `app` and `www` CNAMEs against their targets, and compares the remaining records with Spaceship while ignoring TTL and TXT chunk boundaries. For an initial nameserver cutover, switch the nameservers at Spaceship only when every run succeeds. Confirm that `dig NS useorbit.org +short` returns the Cloudflare nameservers and that Render's Custom Domains screen reports `useorbit.org` verified.

## Plan and apply

Set `RENDER_API_KEY` and `CLOUDFLARE_API_TOKEN` in the shell and provide AWS credentials through the default credential chain. Make a local `infra/local.tfvars` containing the published production and staging image digests and any approved domain or database cutover settings. This file is ignored; `example.tfvars` shows the shape only. Copy the production Postgres IP allow list from the Render Postgres Networking screen into `production_postgres_ip_allow_list`. Set `staging_postgres_ip_allow_list` to the staging instance's live list in an ignored `infra/staging-database/local.tfvars`, or set `TF_VAR_staging_postgres_ip_allow_list` to the equivalent JSON array. Both variables default to `[]`, which removes external access when applied. Check both database resources in every plan before applying; neither database should be replaced, and an existing operator address must not be removed. The requested `database_name` and `database_user` values are creation inputs; Terraform ignores later changes to those fields because Render assigns the live database name with a suffix. Connection strings read the database name from the resource URL.

The `production_web_digest` and `staging_web_digest` variables seed the web images only when Terraform first creates each service. Terraform ignores later digest changes on both web services. Subsequent staging and production web deploys use the manual `release.yml` workflow in `orbit-ui-mobile` with its `environment` input.

With Render provider v1.9.1, a refreshed digest image path also populates a computed image tag. On any web service update, the provider sends an image reference built from the planned tag before considering the digest. Ignoring the entire image block retains that computed tag and does not make the update safe. Run `terraform -chdir=infra show -json local.tfplan | node infra/check-web-plan.mjs` on the saved plan before every local apply. The guard blocks an in-place update, replacement, or deletion of either web service and names the changed attributes. Initial creation remains allowed.

For a deliberate web service setting change, review the saved plan and its blocked attributes, then apply that specific plan as an explicit exception. Immediately redeploy the approved digest through `orbit-ui-mobile`'s manual `release.yml` workflow with the matching `environment` input and verify the live `imagePath` against that approved digest before proceeding with another apply. Keep the exception scoped to the intended service change.

A web service replacement or deletion requires separate recovery review before apply. For a replacement, confirm the currently approved digest, set the matching Terraform digest variable, and plan service continuity, domains, and service ID changes with the owner. Apply only the reviewed saved plan, redeploy the approved digest through the release workflow, and verify the live `imagePath`. For a deletion, confirm the service is intentionally being retired and arrange its traffic cutover before applying the reviewed plan.

After every apply, read each web service's live `imagePath` from Render using its service ID and compare it with the digest approved by its latest release workflow. The expected value is `ghcr.io/thomasluizon/orbit-web@sha256:<approved digest without the sha256: prefix>`. Do not use the Terraform digest variables as the expected value after creation. For each service, run:

```sh
curl -fsS -H "Authorization: Bearer $RENDER_API_KEY" \
  "https://api.render.com/v1/services/<service-id>" | jq -r '.imagePath'
```

If either image differs, stop further applies and redeploy the approved digest through the corresponding release workflow.

Changing a Render service's build or deploy settings through Terraform starts a Render deploy of the tracked branch head, including when the change is to an API service. Cancel that deploy unless it is an intended release of the same commit. The API and landing staging services have auto-deploy disabled. Their tracked branches are selected by their release workflows, and Terraform ignores later branch changes.

After apply, set the `RENDER_STAGING_SERVICE_ID` repository variable in the Orbit API GitHub repository to the staging API service ID. Set `RENDER_LANDING_STAGING_SERVICE_ID` in the Orbit landing repository to the `landing_staging_service_id` Terraform output. Confirm each variable in its repository Actions variables screen before running the corresponding `release.yml` from `main`.

Run:

```sh
terraform -chdir=infra init
terraform fmt -check -recursive infra
terraform -chdir=infra validate
terraform -chdir=infra plan -var-file=local.tfvars -out=local.tfplan
terraform -chdir=infra show -json local.tfplan | node infra/check-web-plan.mjs
terraform -chdir=infra apply local.tfplan
```

The initial plan must keep `srv-d6tc2isr85hc739bf75g`, its URL, and `api.useorbit.org` in place. Terraform ignores the imported API's own environment variables (`lifecycle.ignore_changes`), so the first apply only creates `orbit-production-api` and links it; the service keeps its existing variables, which override the group's identical values. Check the imported service's plan before applying: it must show no change to the service.

For the production database cutover, first confirm the production data is present in the Render database shown on the Render Postgres Connections screen. Set `api_database = "render"` in `infra/local.tfvars` and run a fresh plan. Confirm both `ConnectionStrings__DefaultConnection` and `ConnectionStrings__SessionConnection` in the production API environment group name `orbit_production_9g8l`, as derived from that Render connection URL. Confirm `render_postgres.production` is unchanged and neither web service nor the imported API service changes in place. Apply the plan only after those checks. Verify the linked environment group and production API health, then remove the production API service's duplicated direct variables in the Render service Environment screen, leaving `ORBIT_TERRAFORM_ENV_GROUP`. Confirm the service still uses the Render database after the direct variables are removed.

Staging reads Stripe test-mode product and price IDs from SSM and uses staging return URLs. `Supabase__Url` points at an invalid host so staging cannot write to production storage. The web image must exist at both selected digests before the first apply. Set production web and landing custom domains during cutover. DNS and certificate verification follow the domain changes.
