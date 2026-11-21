# Staging operations

## Contents

- [Staging web hosts](#staging-web-hosts)
- [Staging reseed](#staging-reseed)
- [Staging database access](#staging-database-access)
- [Staging pinger](#staging-pinger)

## Staging web hosts

The staging web app uses `https://app-staging.useorbit.org`. The host `staging.useorbit.org` stays a custom domain on `orbit-web-staging`. The web proxy redirects that host permanently to the new host. The staging API CORS origins and the Google redirect URIs keep both hosts.

## Staging reseed

The workflow `.github/workflows/staging-reseed.yml` runs daily. It reads the creation time of the Render Postgres database. It replaces the database when the database is at least 26 days old. A manual dispatch replaces the database at once. The plan must change only `render_postgres.staging` and its staging database environment group. The workflow rejects a plan that changes another resource.

Render allows only one active free Postgres database in a workspace. For this reason the workflow captures the old database before Terraform deletes it. The runner installs `postgresql-client-17` and puts `/usr/lib/postgresql/17/bin` first on the path.

The data helper exports a repeatable-read snapshot. It writes a custom-format `pg_dump` archive and records the row count of each user table from the same snapshot.  It checks the archive with `pg_restore --list`. A failed dump, count, or check stops the job before the old database is deleted.

After the replacement, the workflow restores the archive in one transaction without the old ownership and grants. It compares the table set and the row counts with the captured counts. A table that is absent, a table that is extra, or a different count fails the job before the migrations, the deploy, and the seed.

Then the workflow runs the migrations that wait to run. It redeploys the live API image by its immutable digest and requires HTTP 201 for the same image. It waits for the deploy to become live. Then it runs the idempotent owner seed, which reuses the copied accounts and sample records.

The copy holds only the data of the snapshot. Stop the staging evaluation writes and the demo writes during a reseed. The local dump is not a backup, and the workflow deletes it in an `always()` step. A failed restore needs operator recovery before the next replacement.

The workflow reads the connection string from the sensitive staging Terraform output and exports it as `STAGING_DATABASE_URL`. The data helper converts it to `PGHOST`, `PGPORT`, `PGUSER`, `PGPASSWORD`, and `PGDATABASE`. `PGSSLMODE` uses the `sslmode` of the URI, or `require` when the URI has none. A password never enters a command argument or a log line.

The AWS role `orbit-staging-reseed` comes from `infra/github_oidc.tf`. Its trust policy accepts only workflows on the `main` branch of this repository. The workflow needs these settings:

- The secret `RENDER_API_KEY` in the GitHub environment `render-operations`, which accepts only the branch `main`.
- The variables `SEED_OWNER_EMAIL`, `STAGING_ENVIRONMENT_ID`, and `STAGING_API_SERVICE_ID`.

## Staging database access

The staging database allow list lives in the Render API, outside Terraform. Terraform uses `staging_postgres_ip_allow_list` only when it creates the database. It ignores later changes to `ip_allow_list`. The default `[]` blocks external access.

The reseed workflow adds the public IPv4 `/32` of the runner to the allow list for the backup, the restore and migrations, and the seed. After each window, also when a command fails, the workflow removes only the entries tagged `github-actions-staging-reseed`. If the reseed job times out, `.github/workflows/staging-postgres-access-reconcile.yml` removes the tagged entry. It runs after each reseed run, and an operator can dispatch it. Both paths keep every other live entry.

To open local `psql` access, set `RENDER_API_KEY` and run this command from the repository root:

```sh
STAGING_POSTGRES_ID="$(terraform -chdir=infra/staging-database output -raw staging_postgres_id)" \
  bash .github/scripts/staging-postgres-access.sh operator-mac
```

The script needs `curl`, `jq`, and `python3`. It reads the public IPv4 address with `curl -4 https://api.ipify.org`. It replaces each entry that has the description `operator mac` with the new `/32`. It keeps the other entries and reads the list back from Render. Run it again after the address changes. Do not run it while a staging workflow runs, because a Render allow list update replaces the whole list.

## Staging pinger

The Cloudflare Worker `orbit-staging-pinger` is the only staging keepalive. Its files are `infra/staging-pinger.tf` and `infra/workers/staging-pinger.mjs`. It calls only `https://api-staging.useorbit.org/health`. It has a scheduled handler and needs no route, no DNS change, and no Render dependency.

The two UTC schedules are `*/5 11-23 * * *` and `*/5 0-2 * * *`. They give 192 invocations a day, from 08:00 to 23:55 in America/Sao_Paulo. The Worker skips an event when its scheduled time or its run time is outside 11:00 to 02:59 UTC. A request bypasses the cache and never follows a redirect. 

The Worker sends `manual`, because Cloudflare accepts only `follow` and `manual`, and a 3xx response fails the invocation. The timeout is 90 seconds, so that the first call can wake a service that sleeps. A non-success response or a network error fails the invocation.

Use a targeted plan to deploy the Worker. The plan must change only `cloudflare_workers_script.staging_pinger` and `cloudflare_workers_cron_trigger.staging_pinger`. Stop if it includes another resource. The token in `CLOUDFLARE_API_TOKEN` needs the account permission Workers Scripts Write.

The Worker writes a persisted log line for each invocation, with a sampling rate of 100 percent. Open **Workers & Pages > orbit-staging-pinger > Observability** and search for `staging-health-ping`. Each line has `scheduledAt`, `startedAt`, `outcome`, `status`, and `durationMs`. The outcome is `healthy`, `failed`, or `skipped-outside-window`. A failed line has an `error` string. A new cron schedule can need up to 15 minutes to take effect.
