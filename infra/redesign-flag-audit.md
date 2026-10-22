# Redesign flag retention

Tracks thomasluizon/orbit-tickets#1626.

Retain the `redesign` seed and the account-resolved `features.redesign` response until
the remaining web build selector is retired and supported Android clients no longer
need the key. The API has no redesign-specific executable reader to remove. Its
generic flag resolver and management surfaces serve other flags too.

The web reader still controls whether an existing design cookie survives. The
Cloudflare Worker still selects a separate origin for that cookie. Enabling the
flag globally would preserve that route for accounts carrying the cookie, without
establishing which build that origin serves. Do not enable it as a rollout shortcut.

## Source evidence

The inspected `main` commits, confirmed against each origin's branch head, are:

| Repository | Commit |
| --- | --- |
| `orbit-api` | `316877abe6dfe7aa978a0d086ca5eb3295851719` |
| `orbit-ui-mobile` | `47d95de1d81b1b89809698549df7a878b4270385` |
| `orbit-landing-page` | `7516f49b4317a4d0d4c7cbcab114d32e5814d118` |

Paths and lines below refer to those commits. Reproduce the inventory with
`git grep -n -i -E 'redesign|orbit_design|useFlag' <commit>` in each repository;
use `git show <commit>:<path>` to follow the readers into their callers and guards.
Historical migration designers, compatibility tests, design assets, and branch
names are references rather than executable build selectors.

### API and routing infrastructure

| Reader or source in `orbit-api` | Selects a build? |
| --- | --- |
| `src/Orbit.Infrastructure/Persistence/OrbitDbContext.cs:668` | No. Seeds `redesign` disabled, with no plan requirement. Retained for client compatibility. |
| `src/Orbit.Infrastructure/Migrations/20261017011939_AddFeatureFlagAccounts.cs:38` | No. Historical seed insertion; its applied migration and designers must remain intact. |
| `src/Orbit.Infrastructure/Services/FeatureFlagService.cs:72` and `:78` | No. Generic snapshot and account/plan resolution, shared by synchronous, asynchronous, and batched reads. |
| `src/Orbit.Api/Controllers/ConfigController.cs:28` | No. Publishes the resolved flag map to both API versions, preserving `{ enabled, planRequirement }`. This is the producer for client build selection. |
| `src/Orbit.Infrastructure/Services/FeatureFlagManagementService.cs:92` | No. Generic administrator listing of stored settings and account emails. Its write lookup at `:30` also serves all flags. |
| `src/Orbit.Api/Controllers/AdminController.FeatureFlags.cs:17` and `src/Orbit.Api/Mcp/Tools/FeatureFlagTools.cs:16` | No. Administrator entry points into the generic management service. |
| `src/Orbit.Infrastructure/Services/AgentPolicyEvaluator.cs:172` | No. Generic capability gating; no capability declares `redesign` in `FeatureFlagKeys`. Other resolver callers use their own feature keys. |
| `infra/workers/web-routing.mjs:3` and `:11` | **Yes.** Exact `orbit_design=redesign` selects `REDESIGN_HOST`; other requests use the DNS origin. The Worker does not query the API flag. |
| `infra/web-routing.tf:9` and `:18` | **Yes, configuration.** Binds the redesign Render origin and attaches the Worker to `app.useorbit.org/*`. |
| `infra/production.tf:106` | Separate origin resource, not a flag reader. Its existence does not prove its deployed commit. |

The API's only nonhistorical `redesign` literal under `src/` is the retained seed.
The other generic consumers of resolved keys govern chat, integrations, profile,
gamification, and proactive services; none selects a UI build with this key.

### Web client and edge proxy

All paths in this table belong to `orbit-ui-mobile`.

| Reader or source | Selects a build? |
| --- | --- |
| `apps/web/components/design-cookie-sync.tsx:9` and `:13` | **Yes, indirectly.** Reads `useFlag('redesign')`; a resolved false clears an existing design cookie. True leaves an existing cookie untouched. It never creates a redesign cookie. |
| `apps/web/lib/design-cookie.ts:3` and `:12` | **Yes, indirectly.** Clears the cookie and reloads once, returning routing to the DNS origin. |
| `apps/web/lib/providers.tsx:21` | Mounts the cookie reader. No independent selector. |
| `apps/web/stores/auth-store.ts:87` | Clears the cookie during account reset. No independent flag read; affects the next Worker route. |
| `apps/web/hooks/use-flag.ts:8` and `apps/web/hooks/use-config.ts:20` | Generic account-scoped config access. No independent selector. |
| `packages/shared/src/utils/config.ts:21` and `packages/shared/src/types/config.ts:10` | Generic resolution and the shared key type. Missing keys resolve false; removing the response key changes older readers' behavior. |
| `apps/web/proxy.ts:86` and `:99` | No. Handles sessions and canonical public hosts, including the Worker's forwarded host. Contains no design flag or cookie selector. |

The routing Worker lives in `orbit-api/infra`, not in `orbit-ui-mobile`. The web
release workflow's separate `production-redesign` target is deployment machinery,
not an account flag reader. Promoting the DNS origin to redesigned `main` does not
remove the cookie route to the separate service.

### Android update channel

All paths in this table belong to `orbit-ui-mobile`.

| Reader or source | Selects a build? |
| --- | --- |
| `apps/mobile/lib/design-flag-refresh.ts:11` | Stores the resolved flag as an account-specific design choice. The subscription at `:7` and refresh at `:16` do not themselves launch a bundle. |
| `apps/mobile/lib/providers.tsx:102` and `:107` | Reads `useFlag('redesign')` and stores the account choice. |
| `apps/mobile/lib/providers.tsx:150`, `:159`, `:211`, and `:242` | Installs the subscription, calls the launch selector at boot, refreshes on foreground, and mounts the flag reader. |
| `apps/mobile/hooks/use-flag.ts:6` and `apps/mobile/hooks/use-config.ts:16` | Generic account-scoped resolution. No independent selector. |
| `apps/mobile/lib/design-bundle.ts:16`, `:26`, and `:33` | Conditional selector: stores `legacy`/`redesign`, then chooses an Expo channel at launch only when updates are enabled. Missing choice or signed-out state chooses `legacy`. |
| `apps/mobile/lib/design-bundle-identity.ts:3` | Identifies the embedded bundle as `redesign`; not a flag reader. |
| `apps/mobile/app.config.js:28` and `:45` | **Disables channel selection** in production and staging with `updates.enabled: false`. The launch guard returns before fetching or reloading. |
| `apps/mobile/app.json:9` and `:187` | Base config still names `legacy` and the shared runtime, but the effective app config overrides update enablement. Do not treat the base file as the release config. |
| `apps/mobile/plugins/with-design-bundle-launch.js:10` | Skips native launch injection when updates are disabled. |
| `apps/mobile/modules/orbit-design-bundle/index.ts:3` and `apps/mobile/modules/orbit-design-bundle/android/src/main/java/org/useorbit/app/bundle/OrbitDesignBundleModule.kt:13` and `:27` | Native launch preparation and one-launch claim, not flag readers. Dormant for the effective config. |
| `.github/workflows/android-release.yml:68`, `:322`, and `:387` | Production/open releases require `main`, prebuild the effective config, and bundle the application. No account flag channel selection. |

Current source therefore keeps the embedded redesign for both enabled and disabled
flag values. This does not establish the configuration or adoption of previously
installed binaries. Preserve the API key for those clients until their compatibility
and supported population are verified. `MinSupportedVersion` is unchanged.

### Landing page

`orbit-landing-page` has no `redesign`, `orbit_design`, or `useFlag` reader on the
inspected `main`. Its only matching references are branch names in
`.github/workflows/build.yml:5` and `.github/workflows/guards.yml:5` and `:7`.
None selects an account's build.

## Environment evidence and remaining rollout work

The coordinator's production before listing in thomasluizon/orbit-tickets#1626
records `redesign` with description "Serve the redesigned web and Android builds
to this account", `EnabledForEveryone: false`, `PlanRequirement: null`, and
`Accounts: []`. It also records production web and API serving promoted `main`.
Those are supplied observations, not fresh worker measurements.

Staging before state is unavailable: the coordinator reports its staging credential
cannot access the administrator tool. Neither environment has a worker after listing
or flag mutation. Production owner-account and fresh-account web/Android verification
remains the coordinator's rollout check; source inspection is not that evidence.

For the paired UI ticket, remove `DesignCookieSync` and its provider mount, retire
the reload-on-cookie-change path while preserving one-time stale-cookie cleanup,
and remove Android `DesignFlagGate`, its subscription/refresh calls and persisted
design writes, the launch switch, and unused native launch glue. Update their
behavior tests and the UI flag guide. Retire the shared `redesign` key only after
both platform callers are gone. Keep general `useFlag` and config-cache behavior.

The infrastructure follow-up must detach the `app.useorbit.org/*` Worker route
before retiring its script, `REDESIGN_HOST` binding, and separate service lifecycle.
Verify stale-cookie and no-cookie requests both reach the approved promoted build.
Do not destroy a Render service merely because its flag has no targeted accounts.

After the client and routing changes land, the coordinator must obtain staging
administrator access, record both environments' before and after listings, and
verify owner and fresh-account behavior on web and Android. Only then settle API
seed/row retirement with an append-only compatibility plan for older clients.
`FeatureFlagActions.Writes` in `src/Orbit.Application/FeatureFlags/FeatureFlagManagement.cs:30`
and the MCP tool declaration expose no delete action. A deletion flow must be
established explicitly; never send an invented `delete` action or bypass MCP with SQL.
