# Orbit infrastructure

## Contents

- [Layout and providers](#layout-and-providers)
- [AI cost controls and evaluation credentials](#ai-cost-controls-and-evaluation-credentials)
- [Windows push credentials](#windows-push-credentials)
- [Free-service Terraform updates](#free-service-terraform-updates)
- [Staging API image source](#staging-api-image-source)
- [Production API image source](#production-api-image-source)
- [Startup migrations and long migrations](#startup-migrations-and-long-migrations)
- [SSM parameters](#ssm-parameters)
- [Plan and apply](#plan-and-apply)
- [Production web routing](#production-web-routing)
- [API release admission](#api-release-admission)
- [Stripe webhook endpoint version](#stripe-webhook-endpoint-version)
- [Store-review sign-in](#store-review-sign-in)
- [Migration lock budget](#migration-lock-budget)
- [Production plan monitor](#production-plan-monitor)
- [Landing content security policy](#landing-content-security-policy)

[staging.md](staging.md) covers the staging reseed, the staging database access, and the staging pinger. [email-and-bot-protection.md](email-and-bot-protection.md) covers SES, the dead-letter queue, Turnstile, and the DNS check.

## Layout and providers

The main Terraform state owns the Render production resources and the staging services. It also owns the Cloudflare DNS and Turnstile, and the AWS SSM parameters. These files hold the resources:

- `production.tf` imports the live API and declares the production database, web service, and landing site.
- `staging.tf` declares the staging API, web service, and landing site.
- `configuration.tf` owns the service environment groups and their SSM inputs.
- `cloudflare.tf` owns the zone, the DNS records, and the Turnstile widget.
- `ses.tf` owns the SES identities, the event routing, and the send credentials.
- `uploads.tf` owns the S3 uploads bucket and a scoped IAM user for each environment.
- `variables.tf` holds the inputs. `versions.tf` pins the providers and sets the S3 state.
- `staging-database/` is a separate Terraform root. It owns only the staging Postgres, its environment group, and the link to the staging API.

The Render provider reads `RENDER_API_KEY`. The Cloudflare provider reads `CLOUDFLARE_API_TOKEN`. The AWS provider uses the default credential chain in `us-east-2`. The main S3 state holds decrypted SecureString values, so restrict access to it. Do not remove the import block of the API.

Apply a change to `aws_iam_user_policy.uploads` before you release the API code that needs it. Without the policy, S3 denies the list call, and account erasures stay scheduled.

## AI cost controls and evaluation credentials

Create a separate evaluation project in OpenAI Platform with its own budget and key. Store the key as a SecureString at `/orbit/<environment>/api/AI__EvalApiKey` for production and for staging. Terraform reads the parameter and does not create it. An absent parameter omits `AI__EvalApiKey` from the environment group, and the plan continues. An absent or empty runtime key blocks each evaluation call before dispatch.

The parameter `/orbit/<environment>/api/StagingEval__AccountIds` holds the evaluation account UUIDs, separated by commas. It reaches the API through the environment group. Do not set a service-level override. These accounts use the evaluation key, and their usage label is `eval_<purpose>`.

`AI__EvalDailyBudgetUsd` is `0.50` in code and in `production_api_values`. `staging_api_values` sets it to `1.20`. All evaluation accounts of one environment share one UTC daily ceiling that includes unsettled reservations. This holds when `astra_eval_unmetered` is on. A reservation error prevents the dispatch, and a settlement error keeps the reservation.

## Windows push credentials

Both API environment groups use the multitenant Orbit Windows Push application. `Wns__TenantId` is `d0a72506-23da-4004-afff-9c077a5b72a5` and `Wns__ClientId` is `d8d3802e-fed9-4d7e-a7b9-87fdfcf2d0db`. These identifiers are public. Staging inherits them. Each environment reads its secret from the SecureString `/orbit/<environment>/api/Wns__ClientSecret`. Terraform does not create it.

## Free-service Terraform updates

Render provider v1.9.1 always sends `MaintenanceMode` in the web service update body ([provider source](https://github.com/render-oss/terraform-provider-render/blob/v1.9.1/internal/provider/webservice/resource/internal/update.go#L53)), and Render rejects that field for a free-plan service. A targeted apply that updates `render_web_service.staging_api` or `render_web_service.staging_web` fails until [provider issue 80](https://github.com/render-oss/terraform-provider-render/issues/80) is fixed. Change these two services through the Render API or the dashboard, and keep the Terraform values equal to the values that you set there.

## Staging API image source

`render_web_service.staging_api` declares the public image `ghcr.io/thomasluizon/orbit-api` with `staging_api_digest` as its first digest. Terraform ignores later digest changes, so `.github/workflows/release.yml` owns the releases. The package is public, so the service needs no registry credential. A plan targeted to this service must show no change. Switch an image source through the Render API, never through Terraform.

The staging job of **Actions > Release API** builds and pushes the image. Then it requests `imageUrl=ghcr.io/thomasluizon/orbit-api@sha256:...` and requires HTTP 201 with that image reference. It polls the returned deploy until the deploy is `live`. The job uses the `staging` environment secret `RENDER_API_KEY` and the repository variable `RENDER_STAGING_SERVICE_ID`.

## Production API image source

`render_web_service.production_api` keeps the imported service `srv-d6tc2isr85hc739bf75g`, named `orbit-api`. It keeps the `starter` plan, the `ohio` region, the domain `api.useorbit.org`, `/health`, and a shutdown delay of 300 seconds. It pulls the public image `ghcr.io/thomasluizon/orbit-api`. `production_api_digest` seeds the image, and `.github/workflows/release.yml` owns later releases. The service has no Render build and no pre-deploy command.

The staging job builds the selected commit once. It records `payload.image_digest` in a successful GitHub staging deployment. The production job reads that record and does not build the commit again.

Production keeps the main-only selection and the healthy staging admission. The override `force_unstaged` bypasses only the current staging health. It still needs a successful staging digest record for the commit. A legacy record without a digest needs a new staging release of that commit.

## Startup migrations and long migrations

Both API environment groups set `Database__MigrateOnStartup=true`. Remove a service-level override `false` if one exists. The API awaits `Database.MigrateAsync()` before it serves requests. A migration error stops the startup, so `/health` cannot pass. Render keeps the old instance in service until the new instance is ready, so each migration must work with the old instance.

Render gives a new instance [15 minutes to pass the health checks](https://render.com/docs/health-checks#handling-failures). Then it cancels the deploy. The migration command timeout is 180 seconds for each command. The lock waits, the retries, and all migrations that wait to run share the 15 minutes. Review the complete batch before the release. If it cannot fit in 15 minutes, split it into bounded expand-contract migrations and resumable backfills.

For a long batch that already exists, run `/app/efbundle` from the staging-verified image digest. Use operator compute with the production session credentials. Take a fresh completed export first. Run the bundle before the production release, and require exit code zero. Do not increase the poll time of the workflow to get around the startup limit.

## SSM parameters

Create each SecureString in SSM Parameter Store in `us-east-2` before the plan. The path is `/orbit/<environment>/api/<Key>` for the API and `/orbit/<environment>/web/SENTRY_DSN` for the web service. `<environment>` is `production` or `staging`. The production API connection strings come from `render_postgres.production`. Staging uses its own Render database.

| Parameters | Where |
| --- | --- |
| The keys in `api_secret_keys` of `infra/configuration.tf` | Both API environments |
| `StagingEval__AccountIds` | Both API environments |
| `AI__EvalApiKey` (optional) | Both API environments |
| `REVIEW_ACCOUNT_CODE`, `REVIEW_ACCOUNT_EMAIL` | Production API only |
| `TEST_ACCOUNTS` | Staging API only |
| `Stripe__ProProductId` and the four `Stripe__*PriceId*` keys (String parameters) | Staging API only |
| `SENTRY_DSN` | Each web service |

Terraform generates `Auth__BffForwardingSecret`, `BotProtection__SecretKey`, `BotProtection__SmokeBypassSecret`, the SES credentials, and the S3 credentials. Do not create them by hand.

Generate each staging secret for staging alone. The staging Stripe secrets hold test-mode credentials. The staging webhook endpoint is `https://api-staging.useorbit.org/api/subscriptions/webhook`. The staging Pub/Sub push subscription for Google Play uses `https://api-staging.useorbit.org/api/subscriptions/play/rtdn` as its endpoint and as its OIDC audience. Its token issuer is the configured `GooglePlay__RtdnServiceAccountEmail`.

## Plan and apply

Set `RENDER_API_KEY` and `CLOUDFLARE_API_TOKEN` in the shell, and provide the AWS credentials through the default chain. Make a local `local.tfvars` file in the `infra` folder with the image digests and the approved domain and database network settings. `example.tfvars` shows its shape. Copy the production Postgres IP allow list from the Render Networking screen into `production_postgres_ip_allow_list`. The default `[]` removes the external access of production.

Check both database resources in each plan. Neither may be replaced, and no operator address may be removed. Terraform ignores later changes to `database_name` and `database_user`.

The manual Release workflow of `orbit-ui-mobile` deploys the web images. Terraform ignores digest changes after the first apply.

With Render provider v1.9.1, a refreshed digest image path also fills a computed image tag. On a web service update, the provider builds an image reference from the planned tag and ignores the digest. Run `terraform -chdir=infra show -json local.tfplan | node infra/check-web-plan.mjs` on the saved plan before each local apply. The guard blocks an update, a replacement, or a deletion of a web service, and it names the changed attributes. For a deliberate change, apply the reviewed plan as an exception. Then redeploy the approved digest through `orbit-ui-mobile` and verify the live `imagePath`.

After each apply, read the live `imagePath` of each web service with `GET https://api.render.com/v1/services/<service-id>`. It must equal `ghcr.io/thomasluizon/orbit-web@sha256:<approved digest>`. Do not use the Terraform digest variables as the expected value. If an image differs, stop the applies and redeploy the approved digest.

Render provider v1.9.1 requests a deploy after each service update. Set `RENDER_SKIP_DEPLOY_AFTER_SERVICE_UPDATE=true` for each targeted apply that updates a service ([provider source](https://github.com/render-oss/terraform-provider-render/blob/v1.9.1/internal/provider/provider.go#L249)). Cancel a deploy that starts, unless it is an intended release of the same commit.

```sh
terraform -chdir=infra init
terraform fmt -check -recursive infra
terraform -chdir=infra validate
terraform -chdir=infra plan -var-file=local.tfvars -out=local.tfplan
terraform -chdir=infra show -json local.tfplan | node infra/check-web-plan.mjs
terraform -chdir=infra apply local.tfplan
```

## Production web routing

Production web is the DNS-only CNAME `app.useorbit.org` to `orbit-web-3qmv.onrender.com`. The variables `dns_app_target` and `web_custom_domains` set it.

Run `terraform -chdir=infra init -backend=false -input=false`, then `npm test --prefix infra` for the infrastructure tests. They use mocked plans and need no cloud credentials.

## API release admission

Release API needs completed successful runs of `API Tests` (`.github/workflows/test.yml`) and `Guards` (`.github/workflows/guards.yml`). Each run must come from a `push` or `workflow_dispatch` event on `main`, at exactly the selected SHA. A pull request run, a run for another branch, and a run for another SHA do not qualify. A commit that a bot merged can have no push CI. In that case, confirm that `main` still points to the commit and dispatch both workflows:

```sh
gh workflow run test.yml --ref main --repo thomasluizon/orbit-api
gh workflow run guards.yml --ref main --repo thomasluizon/orbit-api
```

Wait until both runs succeed and their `head_sha` equals the selected SHA. Then run Release API again. If `main` moved on, select the new commit.

The staging smoke signs in with a dedicated account, reads the habit count, creates a throwaway task, and deletes it. The workflow records the status `api-staging-smoke` only after the health, image, and smoke checks pass. `force_unstaged` needs `override_reason` and does not bypass CI or the export gate.

Set the GitHub secrets `SMOKE_TEST_EMAIL`, `SMOKE_TEST_CODE`, and `SMOKE_BOT_BYPASS` in the environments `staging` and `production`. The staging account and its six-digit code also live in `/orbit/staging/api/TEST_ACCOUNTS`. After a staging reseed, provision the staging account again if it is absent.

For a release with a migration, set the variable `RENDER_PRODUCTION_POSTGRES_ID` in the GitHub environment `production`. Admission compares the migrations of the latest successful trusted production deployment with the selected SHA. When a migration changed, it creates a fresh Render export and waits for a new export ID with a download URL. No database ID, an unavailable export, or no deployment baseline blocks the release.

## Stripe webhook endpoint version

The webhook handler refuses each event whose API version is outside the major release of the pinned `Stripe.net` (`StripeConfiguration.ApiVersion`). Stripe fixes the version of an endpoint when you create it, so the endpoint does not follow a package update. `/health/stripe` lists the versions of the enabled endpoints for `/api/subscriptions/webhook` and the library version.

Release API reads `/health/stripe` three times. Before the production deploy, it compares the library version of staging at the selected SHA with the production endpoint versions. `force_unstaged` skips this check. After each deploy, it checks the environment against its own endpoints. The staging deployment record waits for the staging check.

Before you release a `Stripe.net` update that changes the major release, change the endpoint version in each environment. Production uses Stripe live mode and the service `orbit-api`. Staging uses Stripe test mode and the service `orbit-api-staging`. Do these steps in order:

1. In the Stripe Dashboard, in the mode of the environment, open **Developers > Webhooks**. Add an endpoint at the new version with the same URL. It must deliver `checkout.session.completed`, `invoice.paid`, `invoice.payment_failed`, `customer.subscription.deleted`, and `customer.subscription.updated`. Keep the old endpoint enabled.
2. Write the signing secret of the new endpoint to the SecureString `/orbit/<environment>/api/Stripe__WebhookSecret`.
3. Apply a reviewed plan targeted only to `render_env_group.production_api` or `render_env_group.staging_api`. Its only change must be the masked `Stripe__WebhookSecret`. Confirm in Render that no service override exists.
4. Run **Actions > Release API** from `main` for the environment, directly after step 3. The API reads the secret only at process start, so until then the signature check can refuse events.
5. Confirm that the check after the deploy passes and that the next event of the new endpoint gets a 2xx in **Developers > Webhooks**. Then disable the old endpoint.

## Store-review sign-in

Google Play and App Store review sign in to production with a dedicated account that has no mailbox and no personal data. Production reads its address from `/orbit/production/api/REVIEW_ACCOUNT_EMAIL` and its code from `/orbit/production/api/REVIEW_ACCOUNT_CODE`. Only the production API group receives them.

When the address matches and the code is exactly six ASCII digits, send-code seeds that code and queues no email. For the same pair, SES skips each message to the address, so nothing bounces. A value that is absent or malformed seeds nothing, and the address receives a normal emailed code. The reviewer still passes Turnstile in the app.

Generate the code once with `LC_ALL=C tr -dc '0-9' < /dev/urandom | head -c 6`. After you change a parameter, apply a plan targeted to `render_env_group.production_api`. Then update the login details in the store consoles.

## Migration lock budget

The migration lock gate checks each compiled migration after the applied baseline in `.github/migration-locks.json`. These rules apply:

- Create an index on a table that already exists concurrently, with transaction suppression.
- Drop an index with a raw `DROP INDEX CONCURRENTLY` and `suppressTransaction: true`, never a fluent drop.
- Run a backfill in bounded primary-key ranges, one suppressed SQL operation for each batch.
- Add a foreign key or a check as `NOT VALID`. Validate it in a separate suppressed operation.
- Set a `lock_timeout` of at most five seconds before you alter a table that already exists.
- Add a validated not-null check before a required column.
- Do not rewrite a type. Add a length check without validation instead.
- Give procedural and dynamic SQL reviewed evidence.

An exception for one migration names the rule, the measured row count, the expected lock time in milliseconds, the review URL, and the reason. It lives in the same policy file. A higher applied baseline is not an exception.

## Production plan monitor

The production infrastructure plan workflow runs daily and on manual dispatch from `main`. Its environment `render-operations` assumes the role `orbit-production-plan`. It refreshes only the guarded services and the API environment group, with their dependency graph, and never applies. It publishes no plan and no state.

The job fails on a guarded change or drift, on a different sender, and on a shutdown delay other than 300 seconds. A configuration change needs a reviewed targeted apply first. Each release deploys a digest outside Terraform, so it causes refresh drift on `runtime_source.image.digest` and `runtime_source.image.tag`. The production checker accepts a web service drift only on those two attributes.

Apply `aws_iam_role.production_plan` and `aws_iam_role_policy.production_plan` with targets before the workflow can assume the role. The role does not grant `s3:ListTagsForResource`, so CloudTrail records an expected `AccessDenied` for each bucket. The provider then uses `s3:GetBucketTagging`.

The environment `render-operations` needs the secrets `RENDER_API_KEY`, `CLOUDFLARE_READ_ONLY_API_TOKEN` (Account Turnstile Read), and `PRODUCTION_PLAN_TFVARS`. The last one is a JSON object with the reviewed Terraform inputs, with the four digest variables and the non-empty `production_postgres_ip_allow_list`. A pull request runs mock-provider Terraform tests and never receives these credentials.

## Landing content security policy

Both static sites send a Terraform CSP header that enforces the policy by default. The variable `staging_landing_csp_report_only` in `infra/landing-csp.tf` switches the staging site to the report-only header. `infra/landing-csp.tf` hashes the inline font styles, the exact style attributes, and the inline structured-data scripts. `infra/fixtures/landing-inline-blocks.json` holds the bytes that the hashes come from. Compute the hashes again when the inline bytes change. Never add `unsafe-inline`.
