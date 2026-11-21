# Email and bot protection

## Contents

- [SES identities and DNS](#ses-identities-and-dns)
- [SES sending and events](#ses-sending-and-events)
- [Dead-letter queue recovery](#dead-letter-queue-recovery)
- [Sender addresses](#sender-addresses)
- [Turnstile](#turnstile)
- [DNS check](#dns-check)

## SES identities and DNS

`infra/ses.tf` owns the SES identities `send.useorbit.org`, `updates.useorbit.org`, and `useorbit.org`. Each identity uses Easy DKIM with 2048-bit keys and three unproxied CNAME records. The custom MAIL FROM domains are `bounce.send.useorbit.org`, `bounce.updates.useorbit.org`, and `bounce.useorbit.org`. Only these bounce subdomains receive the SES MX and SPF records. The root MX of `useorbit.org` stays on Google Workspace. The record `_dmarc.useorbit.org` keeps its reject policy.

The Cloudflare zone uses the Free plan. Terraform creates it without a zone subscription resource, because that resource needs a billing scope that the DNS token does not have.

## SES sending and events

SES is the only email transport. Terraform creates `/orbit/<environment>/api/Ses__AccessKeyId` and `/orbit/<environment>/api/Ses__SecretAccessKey` as SecureString parameters. It puts the same values in the Render API environment group of each environment. The user `orbit-api-ses` sends with the production configuration sets. The user `orbit-api-ses-staging` sends with the staging sets. Both users can send from all three identities.

| Item | Production | Staging |
| --- | --- | --- |
| Configuration sets | `orbit-transactional`, `orbit-marketing` | `orbit-staging-transactional`, `orbit-staging-marketing` |
| SNS topic | `orbit-ses-events` | `orbit-ses-events-staging` |
| Webhook | `https://api.useorbit.org/api/email/ses-events` | `https://api-staging.useorbit.org/api/email/ses-events` |

Each API group receives its own `Ses__TransactionalConfigurationSet`, `Ses__MarketingConfigurationSet`, and `Ses__TopicArn`. Both groups also receive `Ses__Region`, `Ses__FromEmail`, `Ses__MarketingFromEmail`, and `Ses__SupportEmail`. API startup fails when one of these nine settings is empty: those seven and `Ses__AccessKeyId` and `Ses__SecretAccessKey`. Each SNS subscription confirms itself through its own signed webhook.

## Dead-letter queue recovery

Each SNS HTTPS subscription keeps failed deliveries for up to 14 days in its own SQS dead-letter queue. The queues are `orbit-ses-events-dead-letter` and `orbit-ses-events-staging-dead-letter`. A CloudWatch alarm on each queue enters ALARM when `ApproximateNumberOfMessagesVisible` reaches one. The alarm publishes to the topic of the same environment, `orbit-ses-dead-letter-alerts` or `orbit-ses-dead-letter-alerts-staging`.

Set `production_ses_dlq_alert_email` and `staging_ses_dlq_alert_email` in the ignored `infra/local.tfvars`. An empty value creates the alarm and the topic without an email recipient. Confirm each subscription from the email that SNS sends. Then check in the SNS console in `us-east-2` that no subscription has the state `PendingConfirmation`.

To recover messages, wait until the webhook and the database are healthy. Then do these steps in the same environment as the queue:

1. In the SQS console, open the queue and use **Send and receive messages** to read one message. Find the original SES event payload in the body. Keep the message if you cannot identify the payload.
2. In the SNS console, open the topic of the queue and use **Publish message** with the original payload. SNS signs a new webhook envelope for the same environment. Never publish a production event to staging or a staging event to production.
3. Confirm that the webhook accepted the event. For a permanent bounce or a complaint, confirm that the marketing contact is suppressed.
4. Delete the original SQS message only after step 3. If the webhook fails, leave the message in the queue.

## Sender addresses

Both environments send transactional email from `Orbit <contact@useorbit.org>`. This is a Workspace user whose public profile photo is the Orbit icon. Staging inherits the value from `production_api_values`. Marketing uses `Orbit <news@updates.useorbit.org>`, which gives it a separate reputation. `Ses__SupportEmail` is `contact@useorbit.org`.

A configuration check does not prove that the avatar appears. After a sender change, receive one real transactional email in Gmail on the web. Confirm that the From address and the Orbit icon are correct. Use **Show original** to confirm that SPF, DKIM, and DMARC pass and align.

## Turnstile

`infra/cloudflare.tf` owns the managed Turnstile widget. Terraform writes its secret to `/orbit/<environment>/api/BotProtection__SecretKey` as a SecureString. Each API environment group reads that parameter and receives `BotProtection__SiteKey` from the widget `sitekey`. The staging landing site also receives `PUBLIC_TURNSTILE_SITE_KEY`. The output `turnstile_site_key` is the public key for the client builds.

Both environments set `BotProtection__Enabled = "true"` in `infra/configuration.tf`. Terraform derives `BotProtection__AllowedHostnames__N` from the widget domains. Production accepts `app.useorbit.org`, `api.useorbit.org`, `useorbit.org`, and `www.useorbit.org`. Staging accepts `app-staging.useorbit.org`, `api-staging.useorbit.org`, and `orbit-landing-staging.onrender.com`. The API hosts are widget domains, so the OAuth pages that the API serves can show the widget. The sign-in, waitlist, and onboarding checks use the same allow list.

Terraform generates a separate 64-character smoke bypass secret for each environment. It stores the secret at `/orbit/<environment>/api/BotProtection__SmokeBypassSecret` and gives it to the API group. Set the GitHub secret `SMOKE_BOT_BYPASS` in the environments `staging` and `production` to the value of the parameter of the same environment. The release smoke sends it as `X-Orbit-Smoke-Bypass` on both sign-in requests. 

The API accepts a bypass only when two checks pass. The secret must match the configured secret in constant time, and the email must match `SMOKE_TEST_EMAIL`. Every other request verifies Turnstile. Set `/orbit/staging/api/SMOKE_TEST_EMAIL` to the same email as the staging GitHub smoke secret and its `TEST_ACCOUNTS` entry.

To disable Turnstile for one environment, set its `BotProtection__Enabled` to `false` in Terraform. Apply only `render_env_group.production_api` or `render_env_group.staging_api`. Never run an untargeted production apply.

## DNS check

After a DNS change, read the `cloudflare_name_servers` output. Run `bash infra/check-dns-cutover.sh <cloudflare-nameserver> [apex-target] [app-target] [www-target]` once for each nameserver. The targets default to the Render hostnames in the script. Pass all three targets in order when one differs.

The script compares the flattened apex A answers with the A answers of the Render target. It checks the `app` and `www` CNAME records against their targets. It compares the other records with Spaceship and ignores the TTL and the TXT chunk boundaries.
