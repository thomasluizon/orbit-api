# Orbit infrastructure

## Astra answer composition activation

Deploy the query-bound read and ranking producers before this composition producer. Startup
applies `PersistComposedChatResponses`, which adds encrypted recording storage and seeds
`astra_block_first` off. It needs no model setting, secret, quota change or backfill.

In the existing admin feature flag screen, add evaluation accounts to `astra_block_first`.
The backing API is `POST /api/admin/feature-flags`; use `list` and `enable_for_accounts` with the exact key and account emails.
Verify `GET /api/config` reports that account's flag enabled and a parts-capable chat response
leads with the requested supported kind without framing prose. Check that complete empty habit
lists retain an empty `habitList` block with complete metadata and offer Today. Empty record
lists retain `recordList`, and explicit provider reads retain calendar recovery. Verify that
no text part contains a serialized composition envelope. Complete typed read
plans, including rounds of complete empty lists, need no narrative model call only when every
read declares `orbit_read_complete: true`. Incomplete plans continue to remaining reads or
approval-gated write previews. Confirm legacy and missing-kind fallbacks separately. Tool selection belongs
to the model; composition does not route tools or select blocks from message keywords.
Keep global activation off until #26 records passing English and Portuguese real-pipeline
composition cases and #1620 supports the consumer contract.

Pending-write text fallback does not require `astra_block_first`. Verify clients without
parts support and accounts with `astra_parts_disabled` receive readable approval text,
including after a blank narrative or preview recovery. Supported parts clients receive the
preview without repeated prose. Compare the final stream event with recorded history.
Check both English and Brazilian Portuguese through client locale and, for legacy clients
without a locale, profile language. Missing-kind compound reads retain their facts without
habit-list or calendar title headings, including stored replay. Feature help must use only
the returned markdown; a related screen does not establish editing steps. These fallback and
feature-help corrections apply independently of `astra_block_first`. #26 owns their live gate.

Monitor `astra_answer_composed` family, leading kind, external-text count, fallback reason,
build failures and duplicate suppression, plus `astra_composition_follow_ups` counts and
existing model-call telemetry. Do not attach prompt/completion text or arguments.

Rollback in the same screen: use `disable_for_everyone` for `astra_block_first` and `disable_for_accounts` for its account overrides.
Read the account's resolved config back and verify negotiated directive assembly returns.
Feature resolution caches for 30 seconds. Contracts and approval gates stay deployed;
model selection and quotas are independent. See the complete
[composition contract](../src/Orbit.Api/OpenApi/AnswerComposition.md).

The main Terraform state owns the Render production resources and the staging services, Cloudflare DNS and Turnstile, and AWS SSM parameters. `production.tf` imports the existing API and declares the production database, web service, and landing site. `staging.tf` declares the staging API, web service, and landing site. `configuration.tf` owns the service environment groups and their AWS SSM inputs. `cloudflare.tf` owns the zone, existing DNS records, widget, and its secret parameters. `ses.tf` owns SES identities, new DNS records, event routing, and send credentials. `variables.tf` contains the configuration inputs and image digests. `versions.tf` pins the providers and configures the encrypted S3 state with native locking. `staging-database/` is a separate Terraform root that owns only the staging Postgres, its connection environment group, and the link to the staging API.

`uploads.tf` creates a private, encrypted S3 uploads bucket and a scoped IAM user in each environment. S3 is the only upload, read, and erasure provider. Terraform stores each user's access key in SecureString parameters at `/orbit/<environment>/api/Storage__S3__AccessKeyId` and `/orbit/<environment>/api/Storage__S3__SecretAccessKey`, then passes them to the API environment group. The S3 upload URL expires after 10 minutes. The stable API read URL redirects to a private S3 read URL that expires after 10 minutes. The API checks that the owner still exists before issuing a read redirect and sends `Cache-Control: no-store`. The API allows 600 read redirects per object per minute, so image pages and viewers sharing an IP have separate budgets for each object. Account erasure drains the user's `{userId}/` prefix in the S3 uploads bucket before removing the user row. Startup requires `Storage__S3__Bucket`, `Storage__S3__Region`, `Storage__S3__PublicBaseUrl`, `Storage__S3__AccessKeyId`, and `Storage__S3__SecretAccessKey`. Storage failures leave the account scheduled for another deletion run.

Apply the uploads IAM policy before releasing the API code that uses it. In staging, review a plan targeted to `aws_iam_user_policy.uploads["staging"]`, then apply that reviewed plan before the staging API release carrying account-image erasure. Verify the `uploads-object-access` inline policy on IAM user `orbit-api-uploads-staging` grants `s3:ListBucket` on its uploads bucket and `s3:DeleteObject` on objects in that bucket. After the staging API release, verify an account deletion empties its S3 prefix, removes its user row, and returns 404 for its stable read link. With the owner's explicit approval, apply the matching change to `aws_iam_user_policy.uploads["production"]` before the production API release carrying this change. Verify the same policy scope on `orbit-api-uploads-production`. An API release before its IAM apply leaves erasures scheduled because S3 denies listing. Remove the obsolete `production_storage_provider` and `staging_storage_provider` assignments from ignored `local.tfvars`; no provider switch remains.

The Render provider reads `RENDER_API_KEY` from the environment. The Cloudflare provider reads `CLOUDFLARE_API_TOKEN` from the environment. The AWS provider uses the default credential chain in `us-east-2`. The main S3 state contains decrypted SecureString values, including the Turnstile and SES secrets, so access to that state and its lock file must stay restricted. The staging reseed role can access only the separate staging database state. The existing API is imported with its service ID; do not remove that import block or replace the service.

## Free-service Terraform updates

Render provider v1.9.1, pinned in `versions.tf`, always sends `MaintenanceMode` in
the web service update body ([provider source, line 53](https://github.com/render-oss/terraform-provider-render/blob/v1.9.1/internal/provider/webservice/resource/internal/update.go#L53)).
Render rejects that field for free-plan services with
"maintenance mode can only be configured for non-free tier services".
Any targeted apply that updates `render_web_service.staging_api` or the free staging
web service, `render_web_service.staging_web`, fails until upstream fixes
[render-oss/terraform-provider-render#80](https://github.com/render-oss/terraform-provider-render/issues/80),
even when the plan has no shutdown delay change. Change these services through the
Render API or dashboard instead, and keep the Terraform values matching what the
API or dashboard sets. Paid services, including the production API and web, are
unaffected.

## Staging API image source

The eval account allow-list lives in SSM parameter `/orbit/staging/api/StagingEval__AccountIds` and reaches the API as `StagingEval__AccountIds` through the `orbit-staging-api` environment group. The coordinator creates the parameter with comma-separated account UUIDs, reviews and applies a plan targeted to `render_env_group.staging_api`, then removes the service-level `StagingEval__AccountIds` override through the Render API. A subsequent plan targeted to `render_web_service.staging_api` must show no change.

`render_web_service.staging_api` declares the public
`ghcr.io/thomasluizon/orbit-api` image with `staging_api_digest` as its initial
digest. Like the staging web service, it ignores later digest changes so
`release.yml` owns releases. The service keeps its ID, free plan, domains,
environment group links and `/health` check. No registry credential is needed.

The staging API source is switched through the Render API because
[provider issue #80](https://github.com/render-oss/terraform-provider-render/issues/80)
prevents Terraform updates to free services. The coordinator performs the switch
after merge and after the first staging release has pushed an image. That first
run can fail at deployment while the service is still Git-backed; its summary
contains the published digest. In GitHub, open **thomasluizon > Packages >
orbit-api > Package settings > Change package visibility** and make the package
public, then verify an unauthenticated pull of the published digest. Through the
Render API, switch `orbit-api-staging` (`srv-dasotg8473hc739a5gjg`) to that image
without replacing the service or changing its environment groups or domains.
Set `staging_api_digest` in ignored `infra/local.tfvars` to that published digest,
refresh Terraform state, and review a later plan targeted only to
`render_web_service.staging_api`; it must show no change and no replacement.
Do not apply a source update through the affected provider.

Rerun **Actions > Release API** from `main` with `environment=staging`,
`branch=main` and the planned full `sha`. The staging job builds and pushes before
requesting `imageUrl=ghcr.io/thomasluizon/orbit-api@sha256:...`, requires HTTP 201
with that image reference, and polls the returned deploy to `live`. Verify the
Render deploy has no image build, Render can pull the public image, and
`https://api-staging.useorbit.org/health` reports `status: Healthy` and the planned
commit. The existing staging `RENDER_API_KEY` environment secret and repository
variable `RENDER_STAGING_SERVICE_ID` suffice. Production promotes the staging-verified
image as described below.

## Production API image source

`render_web_service.production_api` keeps the imported service
`srv-d6tc2isr85hc739bf75g`, `orbit-api`, its `starter` plan, `ohio` region,
`api.useorbit.org`, environment group link, `/health` and 300-second shutdown delay.
It pulls the public `ghcr.io/thomasluizon/orbit-api` image. `production_api_digest`
seeds the image; Terraform ignores later digest changes, so `release.yml` owns
production releases. There is no Render build or pre-deploy command.

The staging job builds the selected commit once, deploys its digest, verifies
public health and Render's live image, and records `payload.image_digest` in a
successful GitHub staging deployment. Production reads that record for its selected
commit instead of building or resolving the commit tag again. The existing main-only
selection and healthy staging admission remain. The recorded `force_unstaged`
override bypasses current staging health only; it still requires a successful
staging digest record for the selected commit. A legacy record without a digest
requires staging to release that commit again. No additional registry secret or
repository variable is required.

The coordinator performs the cutover after merge, with no service replacement:

1. Release the planned API `main` commit to staging through **GitHub > orbit-api >
   Actions > Release API** with `environment=staging`, `branch=main`, and its full
   `sha`. Verify the successful GitHub staging deployment has `payload.image_digest`,
   staging serves that commit healthy, and Render's staging live deploy names the
   same immutable image.
2. In **Render > production Postgres > Recovery > Create export**, take a fresh export and
   wait for completion before the first production image release. Record its
   identity and completion in the release evidence. Repeat before every later
   production release carrying migrations, following
   `.claude/specs/orbit-prod-release.md` in `orbit-ui-mobile`.
3. Through `PATCH /v1/services/srv-d6tc2isr85hc739bf75g`, set `image.ownerId` to
   the service's existing workspace, `image.imagePath` to
   `ghcr.io/thomasluizon/orbit-api@<staging-verified digest>`,
   `serviceDetails.runtime` to `image`, and `serviceDetails.preDeployCommand` to
   the empty string in the same request. Preserve domains, environment groups,
   plan, region and shutdown delay. Verify **Render > orbit-api > Settings**
   shows the image source and no pre-deploy command.
4. Set `production_api_digest` in ignored `infra/local.tfvars` to that digest.
   Perform a refresh-only apply targeted to `render_web_service.production_api`.
   Correct only its `runtime_source.image.image_url` state value to the repository
   `ghcr.io/thomasluizon/orbit-api`, as for the staging source switch. Provider
   v1.9.1 can retain the digest-qualified image path in that value; preserve the
   separate digest and all other state, back up state before correction, and use
   Terraform's state tooling rather than editing remote storage directly.
5. Save and review a plan targeted only to `render_web_service.production_api`
   and `render_env_group.production_api`. It must show only the declared source,
   pre-deploy removal and `Database__MigrateOnStartup=true` changes, with no
   replacement. Stop if it changes service identity or any unrelated resource.
   Apply only that reviewed targeted plan. Never run an untargeted apply.
   Verify **Render > orbit-api > Environment** resolves
   `Database__MigrateOnStartup=true` from `orbit-production-api`; remove a stale
   service-level `Database__MigrateOnStartup=false` override if one exists.
6. Run **GitHub > orbit-api > Actions > Release API** from `main` with
   `environment=production`, `branch=main`, and the same full `sha`. Require the
   promoted digest, a `live` deploy with no Render build or pre-deploy task, and
   `https://api.useorbit.org/health` reporting that commit and `status: Healthy`.

### Startup migrations and long migrations

Both API environment groups set `Database__MigrateOnStartup=true`.
`ConfigureOrbitPipeline` awaits `Database.MigrateAsync()` before `Program.cs`
calls `RunAsync()`. A migration error propagates out of startup, so `/health`
cannot pass on the new instance. Render's [zero-downtime rollout](https://render.com/docs/deploys#zero-downtime-deploys)
keeps the old instance serving until the replacement is ready. This does not undo
schema changes already committed; migrations must remain compatible with the old
instance during overlap.

The installed EF Core 10.0.14 migrator acquires the database migration lock before
reading applied migrations. Npgsql 10.0.3 uses a transaction-scoped
`LOCK TABLE ... IN ACCESS EXCLUSIVE MODE` on the migration history table. Another
starting instance waits for that lock, then reads the updated history instead of
applying the same transactional migration twice. Sources pinned by the installed
NuGet package manifests: [EF migrator](https://github.com/dotnet/dotnet/blob/0427b9abbead3fa64c6655eb4290c537397131fa/src/efcore/src/EFCore.Relational/Migrations/Internal/Migrator.cs#L293)
and [Npgsql history repository](https://github.com/npgsql/efcore.pg/blob/5e912bf071068a44e5abbdab497b313ba99d3ee1/src/EFCore.PG/Migrations/Internal/NpgsqlHistoryRepository.cs#L33).
Transaction-suppressed operations still need their own idempotency and rolling
compatibility review.

Render gives new instances [15 minutes to pass health checks](https://render.com/docs/health-checks#handling-failures)
and cancels the new deploy while keeping existing instances if they do not.
The API's migration command timeout defaults to 180 seconds per command, not per
migration or pending batch; lock waits, retries and all pending migrations share
the startup window. Production-scale migration duration is not established by
unit tests or staging alone. Review the complete pending batch before release.
If it cannot fit comfortably in 15 minutes, postpone traffic promotion and split
schema work into bounded expand-contract migrations and resumable backfills.
For an already built long batch, run `/app/efbundle` from the exact staging-verified
image digest on controlled operator compute with production session credentials,
a fresh completed export and the required database network access, before starting
the production release. Require exit code zero and verify migration history before
proceeding. This runs the existing bundle without starting the API server and
consumes no Render pipeline minutes. Do not substitute a Render one-off job
without proving it can run the new digest rather than the service's previous
artifact. Do not increase the workflow polling budget as a workaround for the
platform's startup limit.

## Staging lifecycle

The staging web app uses `https://app-staging.useorbit.org`. Keep `staging.useorbit.org` as a custom domain on `orbit-web-staging`; the web proxy redirects requests on that host permanently to the new host. The staging API CORS origins, its Google redirect URIs and the staging upload bucket CORS keep the old host beside the new one, so a tab still running the previous web build keeps working through the cutover. Add the new domain to the live Render service before applying the targeted Terraform changes, and confirm the plan has no update to `render_web_service.staging_web`. Update the `STAGING_NEXT_PUBLIC_SITE_URL` repository variable in `orbit-ui-mobile` to `https://app-staging.useorbit.org`, and add `https://app-staging.useorbit.org/auth-callback` to the Google OAuth web client's redirect URIs. Release the staging API and web from `redesign/main`, then verify the new health route, old-host redirect, email sign-in, and Google authorization.

`staging-pinger.tf` and `workers/staging-pinger.mjs` define the Cloudflare Worker that calls the staging API health endpoint every five minutes from 08:00 through 23:55 in America/Sao_Paulo. See [Staging pinger](#staging-pinger) for deployment, run history, and cutover proof. The Worker is the only staging keepalive.

The reseed workflow checks the Render Postgres creation time daily and replaces the staging database once it is at least 26 days old. A manual dispatch replaces it immediately. The replacement plan is restricted to `render_postgres.staging` and its staging database environment group, and the workflow rejects a plan that changes another resource.

[Render permits only one active free Postgres database per workspace](https://render.com/docs/free#single-instance-limit), so the workflow captures the old database before Terraform deletes it. On its Ubuntu runner, it installs `postgresql-client-17` from the PostgreSQL Apt repository and puts `/usr/lib/postgresql/17/bin` first on the path to match the server's major version. The data helper exports a repeatable-read snapshot, creates a custom-format `pg_dump` archive, and records exact counts for every user table, including empty tables and EF migration history, from that same snapshot. It validates the archive with `pg_restore --list` before allowing replacement. A failed dump, count capture, or archive validation stops the job with the old database intact.

After replacement, the workflow restores the archive in one transaction without the old ownership or grants. It compares the complete table set and each table's row count with the captured counts, then runs pending migrations. Missing or extra tables and differing counts fail the job before migrations, deploy, or seed. Because only one free instance is allowed, this comparison happens after deletion of the old instance. The carry preserves the snapshot taken at capture time; writes committed later are outside that snapshot. Keep staging evaluation and demo writes stopped during a reseed. After migration, the workflow redeploys the currently live API image by immutable digest, requires HTTP 201 naming the same image, waits for that deploy to become live, and runs the existing idempotent owner seed, which reuses copied accounts and sample records.

Connection strings come from the sensitive staging Terraform output populated by Render. The workflow exports `STAGING_DATABASE_URL` to the data helper, which parses it into `PGHOST`, `PGPORT`, `PGUSER`, `PGPASSWORD`, and the database name in `PGDATABASE`. All PostgreSQL clients receive the same connection environment. `PGSSLMODE` uses the URI's `sslmode` when present and defaults to `require`; the port defaults to `5432`. The restore command also supplies the database name with `--dbname` to select database restore mode. Connection URIs and passwords never enter command arguments or logs. The dump and count files have private permissions under the runner's temporary directory, are never uploaded as artifacts, and are deleted by an `always()` cleanup step on success or failure. A failed restore or verification requires operator recovery before another replacement; the local dump is not retained as a backup.

For the backup, restore and migrations, and owner seed, the workflow temporarily adds the GitHub runner's public IPv4 `/32` to the staging database allow list. After each access window, including when a command fails, it removes only entries tagged `github-actions-staging-reseed` and logs the live operator entries it keeps. If the reseed job times out, `staging-postgres-access-reconcile.yml` runs independently after completion and on a recurring schedule to remove a tagged runner address. Access windows and reconciliation preserve every other live entry, so an operator address change does not block a reseed or cleanup.

Create the GitHub OIDC provider and staging reseed role from `github_oidc.tf` with a reviewed targeted Terraform apply before enabling the workflow. If the GitHub OIDC provider already exists in the AWS account, import it into this state before applying. Set `RENDER_API_KEY` as a GitHub Actions environment secret in `render-operations` from Render Account Settings > API Keys, and restrict that environment to deployment branch `main`. Set `SEED_OWNER_EMAIL`, `STAGING_ENVIRONMENT_ID`, and `STAGING_API_SERVICE_ID` in GitHub Actions variables. The IDs come from the main Terraform state's `render_project.orbit.environments["Staging"].id` and `render_web_service.staging_api.id`. The role trust policy accepts only workflows on this repository's `main` branch.

The staging allow list is managed through the Render API outside Terraform. Terraform uses `staging_postgres_ip_allow_list` only when creating a database and ignores later changes to `ip_allow_list`. The default `[]` creates a database with external access blocked. A replacement starts with that default; runner access opens for restore, migrations, and seed, and local operator access must be added again when needed.

To give the Mac running this command local `psql` access, obtain an API key from Render Account Settings > API Keys and export it as `RENDER_API_KEY`. From the repository root, after initializing the staging Terraform root, run:

```sh
STAGING_POSTGRES_ID="$(terraform -chdir=infra/staging-database output -raw staging_postgres_id)" \
  bash .github/scripts/staging-postgres-access.sh operator-mac
```

The command reads the Mac's public IPv4 address with `curl -4 https://api.ipify.org`, validates it, and replaces every entry described as `operator mac` with that address's `/32`. It preserves other operators and runner entries, then reads the list back from Render to verify the update. It requires `curl`, `jq`, and `python3`. Run it again after the Mac's public address changes. Confirm the `operator mac` entry in Render > staging Postgres > Networking and connect using the external connection details in the Connections screen. Run this command between staging workflows because Render allow list updates replace the whole list and are not atomic with another update.

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

## Staging pinger

The Worker `orbit-staging-pinger` belongs to Cloudflare account `29945c90bc934c629c8e5a11cbfd146b`. Its only target is `https://api-staging.useorbit.org/health`. It has a scheduled handler and needs no HTTP route, DNS change, or Render dependency. The two UTC schedules, `*/5 11-23 * * *` and `*/5 0-2 * * *`, produce 192 invocations per day. Both the scheduled time and execution time must be inside 11:00 to 02:59 UTC; delayed events outside the window are skipped. Requests bypass cache, never follow a redirect (Cloudflare accepts only `follow` and `manual`, so the Worker sends `manual` and a 3xx response fails the invocation), and time out after 90 seconds to allow the first invocation to wake a sleeping service. A non-success HTTP response or network error fails the invocation. This timeout supports waking the service; the acceptance probes below still require responses under two seconds.

After review, the orchestrator loads `CLOUDFLARE_API_TOKEN` from the Keychain entry `orbit-cloudflare-api-token` without printing it, initializes the existing `infra/` backend, and uses the existing ignored `infra/local.tfvars`. The token needs account-level Workers Scripts Write permission. Review a plan restricted to these two resources, then apply with the same targets:

```sh
terraform -chdir=infra init
terraform -chdir=infra plan -var-file=local.tfvars \
  -target=cloudflare_workers_script.staging_pinger \
  -target=cloudflare_workers_cron_trigger.staging_pinger
terraform -chdir=infra apply -var-file=local.tfvars \
  -target=cloudflare_workers_script.staging_pinger \
  -target=cloudflare_workers_cron_trigger.staging_pinger
```

The reviewed plan must change only `cloudflare_workers_script.staging_pinger` and `cloudflare_workers_cron_trigger.staging_pinger`. Stop if it includes a web service, production API, or any other resource. Neither pinger resource references the managed Cloudflare zone or Render services, so targeting them does not pull those resources into the apply.

In the Cloudflare dashboard, open **Workers & Pages > orbit-staging-pinger > Settings > Triggers > Cron Triggers** and confirm both expressions. Allow up to 15 minutes for [Cron Trigger propagation](https://developers.cloudflare.com/workers/configuration/cron-triggers/). Open **Observability** on that Worker to see [persisted invocation and custom logs](https://developers.cloudflare.com/workers/observability/logs/workers-logs/). Sampling is set to 100 percent. Search for `staging-health-ping` and inspect `scheduledAt`, `startedAt`, `outcome`, `status`, and `durationMs`. Outcomes are `healthy`, `failed`, or `skipped-outside-window`. Failures include an `error` string. Compare consecutive scheduled and actual start times to check the five-minute cadence. Export or copy the evidence before the account's log retention expires.

Once scheduled invocations are visible, choose an hour entirely inside the window and record seven independent probes, ten minutes apart, spanning at least one hour. Append each probe to a file outside the repository using this command:

```sh
{
  date -u '+timestamp=%Y-%m-%dT%H:%M:%SZ'
  curl --silent --show-error --fail --max-time 2 --output /dev/null \
    --write-out 'http_status=%{http_code} duration_seconds=%{time_total}\n' \
    https://api-staging.useorbit.org/health
  printf 'curl_exit=%s\n' "$?"
} >> /tmp/orbit-staging-pinger-probes.log 2>&1
```

Record all seven timestamp, HTTP status, duration, and exit-code observations in the owning ticket or pull request. Every probe must have a success HTTP status, `curl_exit=0`, and `duration_seconds` strictly below 2. Also record the Worker's intervening invocations at five-minute intervals; the independent probes can themselves keep the service awake, so probe timings alone do not prove the Worker is running. If any probe fails or the Worker misses an invocation, investigate and restart the full observation hour. Run this observation again after any change to the Worker or its schedules.

Provider arguments were confirmed using the installed `cloudflare/cloudflare` 5.26.0 schema. The versioned [Worker script schema](https://github.com/cloudflare/terraform-provider-cloudflare/blob/v5.26.0/docs/resources/workers_script.md) documents `content`, `main_module`, and `observability`; the [Cron Trigger schema](https://github.com/cloudflare/terraform-provider-cloudflare/blob/v5.26.0/docs/resources/workers_cron_trigger.md) requires `schedules`, despite its example using `body`. The controller's `scheduledTime` is milliseconds since the UTC epoch per the [scheduled handler contract](https://developers.cloudflare.com/workers/runtime-apis/handlers/scheduled/). Health response status and success use the [Response contract](https://developers.cloudflare.com/workers/runtime-apis/response/); cache, redirect, and abort options use the [Request contract](https://developers.cloudflare.com/workers/runtime-apis/request/).

## SSM parameters

Create each entry as a SecureString in AWS Systems Manager Parameter Store in `us-east-2` before planning. Both production API connection strings always come from `render_postgres.production`. Its connection URL, database user, and password are converted to Npgsql's key and value connection string format. Staging uses its own Render database.

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
| Production API | `/orbit/production/api/SMOKE_TEST_CODE` |
| Production API | `/orbit/production/api/SMOKE_TEST_EMAIL` |
| Production API | `/orbit/production/api/Sentry__Dsn` |
| Production API | `/orbit/production/api/Stripe__SecretKey` |
| Production API | `/orbit/production/api/Stripe__WebhookSecret` |
| Production API | `/orbit/production/api/TEST_ACCOUNTS` |
| Production API | `/orbit/production/api/Vapid__PrivateKey` |
| Production API | `/orbit/production/api/Waitlist__SigningKey` |
| Production web | `/orbit/production/web/SENTRY_DSN` |
| Staging API | `/orbit/staging/api/AI__ApiKey` |
| Staging API | `/orbit/staging/api/Encryption__Key` |
| Staging API | `/orbit/staging/api/Firebase__CredentialsJson` |
| Staging API | `/orbit/staging/api/GooglePlay__ServiceAccountJson` |
| Staging API | `/orbit/staging/api/Google__ClientSecret` |
| Staging API | `/orbit/staging/api/Jwt__SecretKey` |
| Staging API | `/orbit/staging/api/Marketing__UnsubscribeSigningKey` |
| Staging API | `/orbit/staging/api/PostHog__ApiKey` |
| Staging API | `/orbit/staging/api/SMOKE_TEST_CODE` |
| Staging API | `/orbit/staging/api/SMOKE_TEST_EMAIL` |
| Staging API | `/orbit/staging/api/Sentry__Dsn` |
| Staging API | `/orbit/staging/api/Stripe__SecretKey` |
| Staging API | `/orbit/staging/api/Stripe__WebhookSecret` |
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

Terraform creates `/orbit/<environment>/api/Ses__AccessKeyId` and `/orbit/<environment>/api/Ses__SecretAccessKey` as SecureString parameters and places the matching values in each Render API environment group. `orbit-api-ses` can send with the production configuration sets; `orbit-api-ses-staging` can send with the staging sets. Both users can send from the shared SES identities `send.useorbit.org`, `updates.useorbit.org`, and `useorbit.org`. The production sets `orbit-transactional` and `orbit-marketing` publish to `orbit-ses-events`, which subscribes only to `https://api.useorbit.org/api/email/ses-events`. The staging sets `orbit-staging-transactional` and `orbit-staging-marketing` publish to `orbit-ses-events-staging`, which subscribes only to `https://api-staging.useorbit.org/api/email/ses-events`. Each API group receives its own `Ses__TransactionalConfigurationSet`, `Ses__MarketingConfigurationSet`, and `Ses__TopicArn`. Both groups also provide `Ses__Region`, `Ses__FromEmail`, `Ses__MarketingFromEmail`, and `Ses__SupportEmail`. SES is the only email transport. API startup requires nonempty `Ses__AccessKeyId`, `Ses__SecretAccessKey`, `Ses__Region`, `Ses__FromEmail`, `Ses__MarketingFromEmail`, `Ses__SupportEmail`, `Ses__TransactionalConfigurationSet`, `Ses__MarketingConfigurationSet`, and `Ses__TopicArn`. Confirm each SNS subscription through its own signed webhook.

Retire the old email settings only after the SES-only build runs in both environments. A build from before this change falls back to the retired transport when `Email__Provider` is absent, and Render's **Save and deploy** redeploys the existing build with the changed variables. Release production through `release.yml` from `main`, and staging after the standing sync to `redesign/main`. Confirm health and SES `Send` and `Delivery` metrics for each environment's transactional configuration set. The production mailbox simulator can verify delivery without sending to a real recipient. Until both releases pass that check, change no email setting and deploy no build from before this change.

After both releases pass, remove the obsolete provider variables from the ignored `infra/local.tfvars` and save a plan restricted to the two API groups:

```sh
terraform -chdir=infra plan -var-file=local.tfvars \
  -target=render_env_group.production_api \
  -target=render_env_group.staging_api \
  -out=email-cleanup.tfplan
```

Review the saved plan before applying. Only `render_env_group.production_api` and `render_env_group.staging_api` may change, and their existing SES settings must be preserved. The retired vendor's SSM inputs are data sources, so removing those references does not delete the live parameters. In Render's service Environment screens, remove any retired email variables that override the linked groups; the running SES-only build ignores them. Then remove the retired API-key SecureStrings from AWS Systems Manager > Parameter Store, and apply legacy email DNS removals through a separately reviewed DNS plan; the group-targeted plan excludes DNS resources.

Each SNS HTTPS subscription retains failed deliveries for up to 14 days in its own SQS dead-letter queue: `orbit-ses-events-dead-letter` for production and `orbit-ses-events-staging-dead-letter` for staging. A CloudWatch alarm on each queue enters ALARM when `ApproximateNumberOfMessagesVisible` reaches one and publishes to its matching operations topic, `orbit-ses-dead-letter-alerts` or `orbit-ses-dead-letter-alerts-staging`. Set `production_ses_dlq_alert_email` and `staging_ses_dlq_alert_email` in the ignored `infra/local.tfvars` before applying Terraform. Both default to an empty value, which creates the alarm and topic without an email recipient. Confirm each subscription through the email sent by SNS. In the AWS SNS console in `us-east-2`, check that neither operations topic has a subscription marked `PendingConfirmation`. To verify delivery, use the AWS SQS console to send a test message to each matching dead-letter queue, confirm its CloudWatch alarm enters ALARM and the matching recipient receives the alert, then delete the test message. Investigate any real alarm before retention expires. Once the webhook and database are healthy, recover messages in the matching environment:

1. In the AWS SQS console in `us-east-2`, open the matching queue and use **Send and receive messages** to inspect one message. Identify the original SES event payload from the actual message body. Keep the message if its payload cannot be identified.
2. In the AWS SNS console, open the matching topic (`orbit-ses-events` or `orbit-ses-events-staging`) and use **Publish message** with that original event payload. SNS signs a new webhook envelope for the same environment. Do not publish production events to staging or staging events to production.
3. Confirm the webhook accepted the event and the affected marketing contact is suppressed for a permanent bounce or complaint. Delete the original SQS message only after that confirmation. If processing fails, leave it in the queue for another attempt.

The SES DKIM CNAME records and the MAIL FROM MX and SPF records are managed directly in `ses.tf` using the SES identity outputs. Each identity uses Easy DKIM with 2048-bit keys and three unproxied CNAME records. The custom MAIL FROM domains are `bounce.send.useorbit.org`, `bounce.updates.useorbit.org`, and `bounce.useorbit.org`; only these bounce subdomains receive SES MX and SPF records. The root `useorbit.org` MX stays on Google Workspace, and `_dmarc.useorbit.org` retains its reject policy. The Cloudflare zone is created on the Free plan without a zone subscription resource because that resource requires billing scope unavailable to the DNS token.

Both environments send transactional email with `Ses__FromEmail = "Orbit <contact@useorbit.org>"`, the Workspace user whose public profile photo is the Orbit icon. Staging inherits this value from `production_api_values`. Marketing retains `Ses__MarketingFromEmail = "Orbit <news@updates.useorbit.org>"` for its separate reputation, and `Ses__SupportEmail` remains `contact@useorbit.org`.

To roll out the transactional sender to production:

1. In the AWS SES console in `us-east-2`, confirm that `useorbit.org` has successful identity, DKIM, and custom MAIL FROM verification.
2. Save a plan targeting only the production API environment group:

   ```sh
   terraform -chdir=infra plan -var-file=local.tfvars \
     -target=render_env_group.production_api \
     -out=production-sender.tfplan
   ```

3. Review the saved plan with `terraform -chdir=infra show production-sender.tfplan`. Only `render_env_group.production_api` may change, and its only setting change must be `Ses__FromEmail` to `Orbit <contact@useorbit.org>`. Preserve all other settings, including the marketing sender and support address. If the plan includes other changes, stop and resolve them before applying.
4. Apply the reviewed plan with `terraform -chdir=infra apply production-sender.tfplan`.
5. In Render, open the production API service `orbit-api` > **Environment**. Confirm it inherits `Ses__FromEmail = "Orbit <contact@useorbit.org>"` from the linked `orbit-production-api` environment group, with no service override, and confirm its deploy with the updated environment succeeds.
6. Receive one real production transactional email in Gmail web. Confirm the From address is `Orbit <contact@useorbit.org>`, the Orbit icon appears in the sender header, and **Show original** reports passing, aligned SPF, DKIM, and DMARC: MAIL FROM `bounce.useorbit.org` and DKIM signing domain `useorbit.org`. The Workspace profile photo route needs this received-email check; configuration alone does not prove the avatar appears.

Terraform creates `/orbit/production/api/BotProtection__SecretKey` and `/orbit/staging/api/BotProtection__SecretKey` as SecureString parameters from the managed Turnstile widget. Both API environment groups read the corresponding parameter. `BotProtection__Enabled` remains at its current setting. The public `turnstile_site_key` output is for the web and landing builds.

## DNS cutover

The Cloudflare zone uses full DNS setup on the Free plan. The existing records have automatic TTL. The existing records remain unproxied, including the production web CNAME. At cutover, use these values in `infra/local.tfvars`:

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

Set `RENDER_API_KEY` and `CLOUDFLARE_API_TOKEN` in the shell and provide AWS credentials through the default credential chain. Make a local `infra/local.tfvars` containing the published production and staging image digests and any approved domain settings and database network access settings. This file is ignored; `example.tfvars` shows the shape only. Copy the production Postgres IP allow list from the Render Postgres Networking screen into `production_postgres_ip_allow_list`. The production variable defaults to `[]`, which removes production external access when applied. The staging root uses `staging_postgres_ip_allow_list` only at creation and ignores later allow list changes; use the `operator-mac` command above for local staging access. Check both database resources in every plan before applying; neither database should be replaced, and an existing operator address must not be removed. The requested `database_name` and `database_user` values are creation inputs; Terraform ignores later changes to those fields because Render assigns the live database name with a suffix. Connection strings read the database name from the resource URL.

The `production_api_digest`, `production_web_digest`, `staging_web_digest` and `staging_api_digest` variables seed their respective images. Terraform ignores later digest changes on these image-backed services. Subsequent staging and production web deploys use the manual `release.yml` workflow in `orbit-ui-mobile` with its `environment` input.

With Render provider v1.9.1, a refreshed digest image path also populates a computed image tag. On any web service update, the provider sends an image reference built from the planned tag before considering the digest. Ignoring the entire image block retains that computed tag and does not make the update safe. Run `terraform -chdir=infra show -json local.tfplan | node infra/check-web-plan.mjs` on the saved plan before every local apply. The guard blocks an in-place update, replacement, or deletion of either web service and names the changed attributes. Initial creation remains allowed.

For a deliberate web service setting change, review the saved plan and its blocked attributes, then apply that specific plan as an explicit exception. Immediately redeploy the approved digest through `orbit-ui-mobile`'s manual `release.yml` workflow with the matching `environment` input and verify the live `imagePath` against that approved digest before proceeding with another apply. Keep the exception scoped to the intended service change.

A web service replacement or deletion requires separate recovery review before apply. For a replacement, confirm the currently approved digest, set the matching Terraform digest variable, and plan service continuity, domains, and service ID changes with the owner. Apply only the reviewed saved plan, redeploy the approved digest through the release workflow, and verify the live `imagePath`. For a deletion, confirm the service is intentionally being retired and arrange its traffic cutover before applying the reviewed plan.

After every apply, read each web service's live `imagePath` from Render using its service ID and compare it with the digest approved by its latest release workflow. The expected value is `ghcr.io/thomasluizon/orbit-web@sha256:<approved digest without the sha256: prefix>`. Do not use the Terraform digest variables as the expected value after creation. For each service, run:

```sh
curl -fsS -H "Authorization: Bearer $RENDER_API_KEY" \
  "https://api.render.com/v1/services/<service-id>" | jq -r '.imagePath'
```

If either image differs, stop further applies and redeploy the approved digest through the corresponding release workflow.

Changing a Git-backed Render service's build or deploy settings through Terraform starts a Render deploy of the tracked branch head. Cancel that deploy unless it is an intended release of the same commit. The landing staging service has auto-deploy disabled; its release workflow selects the tracked branch and Terraform ignores later branch changes. The staging API uses a workflow-selected image digest instead.

After apply, set the `RENDER_STAGING_SERVICE_ID` repository variable in the Orbit API GitHub repository to the staging API service ID. Set `RENDER_API_KEY` as the Orbit API GitHub `staging` environment secret and restrict that environment to deployment branch `main`. Set `RENDER_LANDING_STAGING_SERVICE_ID` in the Orbit landing repository to the `landing_staging_service_id` Terraform output. Confirm each variable in its repository Actions variables screen before running the corresponding `release.yml` from `main`.

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

Remove the obsolete `api_database` assignment from ignored `infra/local.tfvars`. Both `ConnectionStrings__DefaultConnection` and `ConnectionStrings__SessionConnection` in `orbit-production-api` use the managed Render database. A production plan for the selector removal must show no resource changes, including no environment group, service, or database updates. Only after that proof, delete `/orbit/production/api/ConnectionStrings__DefaultConnection` and `/orbit/production/api/ConnectionStrings__SessionConnection` from AWS Systems Manager Parameter Store in `us-east-2` without reading their values. Confirm both parameter names are absent afterward.

Staging reads Stripe test-mode product and price IDs from SSM and uses staging return URLs. Staging uploads use their own S3 bucket. The web image must exist at both selected digests before the first apply. Set production web and landing custom domains during cutover. DNS and certificate verification follow the domain changes.

## Production web routing

Production web serves the promoted `main` build directly through the DNS-only
`app.useorbit.org` CNAME to `orbit-web-3qmv.onrender.com`. Keep
`dns_app_target = "orbit-web-3qmv.onrender.com"` and
`web_custom_domains = ["app.useorbit.org"]` in ignored `local.tfvars`.
Cookie values do not select an origin. The never-applied build-routing Worker and
separate redesign Render service configuration are retired.

The [settled redesign flag audit](redesign-flag-audit.md) records the production
and staging flag observations, retired client readers, and compatibility plan.
The disabled `redesign` seed and `/api/config` response key remain for older
Android builds until thomasluizon/orbit-tickets#685 raises the supported version
floor and retires them.

Run `npm test --prefix infra` for the infrastructure tests. They use Node's built-in
test runner without third-party test dependencies.

## Exact chat read recovery

Chat read contracts and failure handling are documented in
[the read-tool contract](../src/Orbit.Application/Chat/Tools/README.md#exact-read-metadata-and-recovery).
Deploy the append-only API before the paired client advertises `periodTotal` or
`periodComparison`. Dated `habitList` output additionally needs
`supportsDatedHabitListCard: true`; `habitHistory` record lists need
`supportsHabitHistoryCard: true`. Each advertisement promises compatible parsing,
display and date-correct interactions; unadvertised subtypes fall back to queried
text. Refresh rechecks the subtype and may return HTTP 403 after a local-date
rollover for a client without dated support. The existing `AstraPeriodBlocksDisabled` feature flag disables
those producers; no fleet-floor change or new secret is required. Read tokens use
the shared `Jwt:SecretKey`; rotating that key expires outstanding queries and
requires repeating the original request.

On HTTP 409 `READ_QUERY_STALE`, refresh the original query before asking for another
page. On HTTP 503 `READ_QUERY_REFRESH_FAILED`, keep the prior answer and retry the
same token. HTTP 410 requires repeating the original query. A permission denial
requires restoring the original capability through its existing flow. Never replace
a historical query with Today, and never generate a recap to recover a read.
## Habit ranking activation

Release the append-only API through the existing `release.yml` workflow before
shipping the #1618 consumer. Verify the generated contract at the deployed producer
commit and an authenticated `POST /api/chat/habit-ranking/query` followed by paging
and refresh using its returned tokens. An empty ranking is a successful result.
No database migration, backfill, new secret or version-floor change is required.
The query signer uses the already configured `Jwt:SecretKey`.

The client enables the block by sending `supportsParts: true` and including
`habitRanking` in `partKinds`. Remove that kind advertisement to return readable
text. Existing `astra_period_blocks_disabled` or `astra_parts_disabled` flags
also disable structured ranking output. Revert the producer if counts are wrong.
Monitor `habit_ranking_query` outcomes and `habit_ranking_block` build failures;
HTTP 409 `HABIT_RANKING_STALE` means refresh the existing absolute query rather
than resolve its relative period again. The exact payload and boundary policy
are in [HabitRanking.md](../src/Orbit.Api/OpenApi/HabitRanking.md).
