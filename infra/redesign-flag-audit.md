# Retired build selection and routing

Tracks thomasluizon/orbit-tickets#1626.

The promoted redesign ships from `main` on production web and Android.
Client build selection was retired in thomasluizon/orbit-tickets#1635.

## Retired build selectors

Read-only inspection of `orbit-ui-mobile`'s `origin/main` at
`58d55b9edbecb5f6d2c04bb5903e313a1fe0ce95` confirms the client retirement:

- Web no longer mounts `DesignCookieSync` or reads `useFlag('redesign')` to choose
  a build. `apps/web/lib/providers.tsx` invokes `clearDesignCookie` on mount;
  `apps/web/lib/design-cookie.ts` only deletes a stale `orbit_design` cookie.
  Account reset also clears it. There is no flag lookup or reload in the cleanup.
- Android no longer mounts `DesignFlagGate`, persists account design choices, or
  runs the design bundle launch selector. Its native launch glue is removed.
  `apps/mobile/app.config.js` disables updates in both production and staging, so
  the embedded promoted bundle launches without account-specific update channels.
- The API has no redesign-specific executable build reader. Its generic resolver,
  management surfaces, and capability gating remain shared infrastructure.

Reproduce the reference trace with `git grep -n -i -E
'redesign|orbit_design|DesignCookieSync|DesignFlagGate|design-bundle|design-flag'`
in each repository at the inspected revision. Migration designers, compatibility
tests, assets, and branch names are not executable build selectors.

## Infrastructure disposition

The coordinator's read-only infrastructure inventory establishes that
`app.useorbit.org` is a DNS-only CNAME to `orbit-web-3qmv.onrender.com`. There is no
production routing Worker script, no route on that host, no Worker custom domain,
and no separate production redesign web service in Render. The only zone Worker
route is the staging assetlinks route; the deployed scripts are the staging
assetlinks Worker and staging pinger. No detach or service deletion is needed.

The never-applied `infra/web-routing.tf`, `infra/workers/web-routing.mjs`, and
`infra/web-routing.test.mjs` are removed with their coverage step and Miniflare
dependencies. The separate production redesign service, digest variable, outputs,
environment group membership, and plan-guard expectation are removed too.
`infra/cloudflare.tf` keeps existing records unproxied, matching live DNS.
The production and staging web services, staging pinger, and other live
infrastructure remain configured.

These resource addresses leave the configuration; none exists live according to
the supplied inventory:

- `cloudflare_workers_script.web_routing`
- `cloudflare_workers_route.web_routing`
- `render_web_service.production_redesign_web`

No Terraform apply or live-resource mutation is part of this worker's change.
