# Orbit Review Rubric

**At a glance:** the frozen list of what a review checks here. `/pr-review` walks it over a diff and
`/audit-code-quality` walks the same file over the whole repo, so it stays orchestration-free: dimensions,
severities, and the finding template only. A twin lives at `orbit-api/.claude/skills/pr-review/rubric.md`
because `/pr-review` runs from either repo root and two git repos cannot share a file; mirror any
substantive edit there in the same task. Every finding quotes the diff line it is about and cites the rule
it came from by section, never by line number: line numbers rot silently and then point at the wrong rule.

## Severity and the blocking decision

| Severity | Meaning |
|---|---|
| **Critical** | Exploitable, data loss, crash, broken contract, or breaks an already-shipped client. |
| **High** | Type-safety hole, missing error handling, missing parity, missing validation, a test that cannot fail. |
| **Medium** | Pattern inconsistency, missing edge case, defense-in-depth gap. |
| **Low / Info** | Style deviation, minor naming, observation. |

Severity is descriptive. **`blocking` is the decision**, and it has one test: does the finding break
behaviour, security, or data integrity? A High that does not is `"blocking": false` and becomes a follow-up
ticket. Never manufacture a Blocking finding to avoid handing over a clean diff.

`BREAKS-OLD-CLIENTS` is a Critical-class marker, always Blocking, for a change that makes an
already-installed Android client misbehave, because old builds ship a frozen `@orbit/shared` snapshot.
Detection is dimension 7.

## Gate-owned: never a hand-written finding

The most-broken rule in `.claude/rules/core.md`. If a required check already fails on it, a hand-written
finding is noise, because the PR cannot merge either way. ESLint `local/*` and Roslyn `ORBIT0001..0005`
(`Lint`) own `any` / `as any` / `as unknown as X`, `console.log`, the whole comment policy, full-bleed web
buttons, overshoot easing, and `will-change` discipline. `Type Check`, `Unit Tests`, `Build`, `Dash Ban`
(em and en dashes, PR title and body included), `Copy Register` (shouted strings and the cliche register in
i18n values), `Design Token Guard` (raw `--slate-*`, `transition-all`, `h-screen` in `apps/*`),
`Suppressions Ratchet`, `Expo SDK Pin`, `Dependency Review`, `Dependency Audit`, `GitGuardian Security
Checks`, `Analyze` (CodeQL), `SonarCloud Code Analysis`, and `React Doctor` own the rest. Three gates are
partial and the uncovered half is yours: `local/no-decorative-glow` and `local/no-raw-gradient` ship at
`warn` in `apps/*`, so pre-existing violations are known debt but a **newly introduced** one is a finding;
`Cross-Platform Parity` and `Contract Drift` are scoped in dimensions 8 and 7.

## Finding template

```
[SEVERITY] <one-line title>          blocking: true|false   [BREAKS-OLD-CLIENTS if applicable]
- dimension: <number and name>
- location: <repo>/<path>:<line>     (a line in the diff; quote it)
- claim: <what is wrong, and what goes wrong if it ships>
- fix: <the concrete change>
- reference: <CLAUDE.md rule N | DESIGN.md section | orbit-api hard rule | security category>
```

## Dimensions

A diff that does not touch a dimension's surface skips it, recorded as N/A with the reason. Do not invent
findings for files the diff never changes.

### 1. Correctness

> Reference: the change's own intent (PR body, linked ticket).

Does it do what the ticket says, across every boundary it crosses? Follow the data flow (request shape in,
handler, response shape out, consumer reads it) and name any mismatch. Check the boundaries (empty list,
zero, null, first and last item, timezone edges), that loading, error, and empty states are all handled,
and any concurrency or ordering assumption the diff silently relies on.

### 2. Dead / stale code

> Reference: CLAUDE.md rule 2.

Orphaned exports, functions, or types with zero references after this change (cite the grep); dead branches;
commented-out code; stubs and speculative parameters; imports the diff itself orphaned.

### 3. SOLID / clean architecture

> Reference: CLAUDE.md rules 3, 4, 7.

- Function soft cap ~50 lines and ~3 nesting levels, hard cap ~100. A file near 1,000 lines or carrying
  unrelated responsibilities is a cohesion finding when the evidence supports it, but when a split would
  merely relocate the same tangle, say that instead.
- For branch-heavy code look for the **code-judo move**: a state-model or data-shape reframe that deletes
  whole branches. Prefer it to another conditional; flag flag-soup and special-case ladders.
- No premature abstraction: extract on the third real use, not the second. Apply the **deletion test** to
  thin wrappers, since if removing the module makes its complexity vanish rather than exposing useful
  behaviour it is pass-through indirection. Repeated casts or optionality juggling means a structural type
  mismatch that one better type, or one parse at the trust boundary, would remove.
- DRY at the right level: cross-app to `packages/shared`, cross-component to `apps/<platform>/components/`,
  never lifted to shared for one caller. Business logic belongs in its domain, CQRS, or shared layer, never
  in a controller, component, DTO, or adapter; new backend endpoints follow CQRS.

### 4. No-workaround / root cause

> Reference: CLAUDE.md rule 1.

The signature smell is **frontend written to dodge a missing or awkward API**: client-side reshaping,
refetch-and-merge, optimistic patches papering over a shape the backend should return. Flag it and point at
the upstream fix, and the same for fallbacks or defensive branches covering a problem that belongs to a
config, a type, or a shared util. An unavoidable workaround is allowed only with a one-line WHY comment
carrying an `http(s)://` link to the upstream issue; no link means it is not sanctioned.

### 5. Test quality

> Reference: the `tdd` skill (`~/.claude/skills/tdd/`). `Unit Tests` proves the suite is green; it cannot
> tell a real test from one that passes by construction.

- **Tautological test (High).** The assertion recomputes the expected value the way the code does
  (`expect(add(a, b)).toBe(a + b)`, a snapshot derived by hand the same way, a constant asserted equal to
  itself), so it passes by construction and can never disagree with the code. Expected values must come
  from an independent source of truth: a known-good literal, a worked example, the spec. This is exactly
  what a worker under an evidence gate produces when it needs a green result, so read the new tests before
  believing the green check.
- **Implementation-coupled test (Medium).** Mocks internal collaborators, tests private methods, or
  verifies through a side channel (querying the database instead of reading back through the interface).
  The tell: it breaks on a refactor that changed no behaviour. Mock only at system boundaries (external
  APIs, time, randomness), never your own modules.
- **New behaviour with no test that would fail without it is High.** Assert the behaviour a caller cares
  about, not the shape of the call.

### 6. Security

> Reference: OWASP and the orbit-api hard rules. CodeQL, SonarCloud, and GitGuardian own the mechanical half
> (injection patterns, committed secrets); the judgement below is yours.

- **Authorization.** A new controller endpoint carries `[Authorize]` or `[AllowAnonymous]`; missing both is
  a bug and the default is `[Authorize]`. `[AllowAnonymous]` on anything touching user data is
  public-by-mistake, a `userId` read from the request body instead of `User.GetUserId()` is a tenancy hole,
  and Server Actions and BFF routes need their own auth check.
- **Webhooks and keys.** Every Stripe webhook calls `EventUtility.ConstructEvent(json, signature,
  WebhookSecret)` before any processing and rejects a null or empty `WebhookSecret`; the Stripe API key is
  set once at startup, never per request. The JWT secret comes from configuration with no dev fallback in
  production, HS256 pinned with `none` and asymmetric rejected, short-lived access tokens, DB-backed
  revocable refresh tokens, never logged.
- **CORS and session.** No `AllowAnyHeader()`, no `AllowAnyMethod()`, never `AllowAnyOrigin()` with
  `AllowCredentials()`. Web session stays httpOnly, sameSite strict, secure; mobile tokens live in
  SecureStore, never AsyncStorage.
- **Validation and rate limits.** The backend is the source of truth and frontend Zod is convenience only:
  every new endpoint has a FluentValidation validator **and** a domain-entity guard, with request size
  limits intact (Kestrel 10MB global, chat 20MB). Abuse-prone endpoints (auth `send-code` / `verify-code`,
  chat, AI summary) carry `[DistributedRateLimit]`.
- **Leakage.** No stack traces, DB schema, passwords, tokens, or PII in responses or logs, and response DTOs
  carry no password hashes or refresh tokens. Logging is structured, never interpolated, because
  interpolation leaks PII into log analytics. Weak hashing (MD5, SHA1) or an insecure RNG for a
  security-sensitive value is Critical. Frontend: XSS via unescaped input in JSX or
  `dangerouslySetInnerHTML`, and auth state reaching logs or analytics.

### 7. Contract alignment and backward compatibility

> Reference: CLAUDE.md "Security & contracts"; issue #206. **`Contract Drift` regenerates the Zod snapshot
> from orbit-api `main`**, so it cannot see a paired in-flight API PR and it never makes the append-only
> judgement. Both are yours.

Compare `packages/shared/src/types/*` and `packages/shared/src/api/endpoints.ts` against the orbit-api DTOs
(feature-local records under `src/Orbit.Application/`) and the Controller routes; casing is expected to
differ (PascalCase C#, camelCase over JSON). Report `MISSING_DTO`, `MISSING_ZOD`, `FIELD_DRIFT` (name, type,
or required-vs-optional), `PATH_DRIFT` (route or method). Then make the append-only judgement drift
detection does not: shared and DTO changes **add optional fields** and never rename, remove, or retype a
field an old mobile client still reads, because mobile lags via the Play store.

- Removed or renamed in a **response** DTO or schema: old clients read `undefined`. **Critical,
  `BREAKS-OLD-CLIENTS`**, unless it was already optional AND unused (cite the grep).
- Removed or renamed in a **request** DTO or schema, or made newly required: old clients still send the old
  shape and validation rejects it. **Critical, `BREAKS-OLD-CLIENTS`**.
- Added as optional: forward-compatible, **Info**. Enum value removed: old clients may still send it.

The fix is always the compatible alternative: keep-and-deprecate, accept both names server-side for a
release, or expand-contract behind the `AppConfig.MinSupportedVersion` gate, raised only after the carrying
build is live in the Play fleet. When old-client reach is genuinely uncertain, downgrade to High with a
"verify old-client usage" note rather than over-claiming Critical. Semantic breaks under an unchanged field
name belong to dimension 1; do not over-claim completeness here.

### 8. Cross-platform parity

> Reference: root CLAUDE.md "Cross-platform parity (MANDATORY)". **The `Cross-Platform Parity` gate only
> counts changed files per platform**, so it catches a wholly one-sided PR and nothing else. Per-file
> mirrors and behavioural equivalence are yours.

Every changed `apps/web/**` file has its `apps/mobile/**` mirror changed in the same PR and vice versa:
`hooks/use-<x>.ts`, `stores/<x>-store.ts`, and `components/<feature>/<X>.tsx` map one to one,
`app/(app)/<page>/page.tsx` maps to `app/<page>.tsx`, and `app/actions/<x>.ts` maps to a mobile hook calling
`apiClient` directly. The mirror is **behaviourally identical**: same logic, data flow, and error handling,
reverts included. Only platform adapters may differ (BFF vs direct API, cookie vs SecureStore, shadcn vs
NativeWind, next-intl vs i18next). A missing mirror file, or one that exists but was not updated, is
**High** until proven intentional (the `parity:exempt` label plus a justification in the PR body). Never
flag `packages/shared/**`, `apps/web/middleware.ts`, `apps/web/app/api/[...path]/route.ts` (its mobile
equivalent is built into `apps/mobile/lib/api-client.ts`), or a platform-specific test.

### 9. i18n

> Reference: root CLAUDE.md. No gate checks locale parity.

Every new user-facing string has a key in **both** `packages/shared/src/i18n/en.json` and `pt-BR.json`, in
the same edit (`MISSING_PT` / `MISSING_EN`), and no callsite references a key that exists in neither
(`ORPHANED`). `Orbit` and `Astra` are never translated. Keys stay dot-notation hierarchical and alphabetized
within their hierarchy, with ICU plurals where a count is interpolated. Copy that is wordy, redundant with
its own heading, or inconsistent in terminology across the two locales is a finding even though the Copy
Register gate passed it.

### 10. Design

> Reference: `DESIGN.md`. **Gated: only when the diff touches `apps/*` UI files.** Cite the DESIGN.md section
> for every finding; if a rule is in neither `DESIGN.md` nor `.claude/rules/`, it is your taste and you must
> label it as such.

The anchor is the **de-decorated navy-violet orbital** (#539 freeze): a near-black canvas, one rationed
violet, opaque surface steps, hairline rings and dividers, with hierarchy bought by surface steps, size,
weight, and whitespace. **Quiet decoration is still decoration**: a softened glow or a 0.03-opacity texture
is the same violation as the loud version.

- **AI-slop test.** Any decorative glow or gradient (the tokens are deleted, so anything reaching this diff
  is hand-rolled), including gradient borders and `bg-clip-text` gradient text; decorative background orbit
  arcs; a coloured side-stripe border; cards-in-cards; connector or tree lines; gray text on coloured
  backgrounds; rounded-square icon tiles above headings; an oversized centered H1 outside a hero; the
  hero-metric template used as decoration, or any invented precise-looking number.
- **Token adherence and accent rationing.** `--primary` is fill and graphic only: CTA background, FAB,
  progress ring, done dots, level bar, active tab, active nav. That is the whole list. It is never small
  text on the canvas (that is `--primary-soft`), never decorative on a card, row, border, or heading. No new
  font, radius, shadow, or colour outside the spec.
- **Scene-sentence test.** Describe the rendered screen in one sentence. If it reads like generic SaaS ("a
  clean modern dashboard with cards"), name what would make it read as Orbit; if the only way to make it
  specific is to describe decoration, the design has failed and decoration is not the fix. **Restraint has a
  floor** on the other side: a quieting pass that flattens everything to one size and weight, goes
  grayscale, or trades an affordance for calm has also failed.
- **Responsive and layout.** Desktop composes horizontally rather than stretching one mobile column, with
  intrinsic-width pills; the 65ch measure; concentric radii (outer = inner + padding); one focal element per
  view; a card is not a layout primitive; every sub-screen has a visible back affordance. A pill hugs its
  content unless it is a mobile bottom-sheet or dialog's single primary action, a mobile auth or onboarding
  submit, a full-screen empty-state CTA, or a paired confirm row, and the lint rule cannot see mobile
  StyleSheet width, so flag `alignSelf: 'stretch'` and `width: '100%'` pills by eye.
- **A11y.** Colour as the only signal; the 3:1 non-text contrast floor for icons, borders, and state
  indicators; where focus lands on open, that it is trapped, and where it returns on close; a localized
  label in both locales for icon-only controls; hit targets at least 44, reached by padding rather than by
  growing the glyph. Arbitrary `z-*` values have no lint rule yet, so flag them by eye. 200% zoom and
  screen-reader semantics need the live DOM: say a pass is owed, do not guess.

### 11. Backend hard rules

> Reference: orbit-api/CLAUDE.md "Cross-cutting hard rules". **Gated: `orbit-api` changes only.**

What dimensions 5 and 6 do not already carry. **Timezone**: user-facing dates use
`IUserDateService.GetUserTodayAsync(userId)`, never `DateOnly.FromDateTime(DateTime.UtcNow)`, which is for
`CreatedAtUtc` and cache keys only. **Validator placement**: `Orbit.Application/<Feature>/Validators/`.
**Result flow**: `Result<T>` propagated correctly (`PropagateError<T>()`, `ToPayGateAwareResult()`), with no
catch block that swallows an error silently. **Test scope**: every new command or query handler, validator,
and service has a unit test, and unit is all there is, so never ask for an integration or E2E suite.

### 12. FEATURES.md gating

> Reference: `FEATURES.md` at the orbit-ui-mobile root. **Gated: only when the diff changes the user-facing
> feature surface.** Hand-maintained, so nothing generates it and no gate checks it.

Triggers: a new screen, route, or tab; a new or removed Astra (`IAiTool`) or MCP (`[McpServerTool]`) tool; a
plan-gating change (`PayGateService`, `AppConstants`); a platform-availability or locale-specific behaviour
change. Pure refactors, bugfixes, and visual polish are N/A. The same PR updates the row, keeping the
Gating, Platform, and Locale columns accurate and the stated tool counts correct; a missing update is
**High**, as is a gating or platform claim the diff makes stale, and a change that makes the in-app guide
(`onboarding.featureGuide.*`) wrong is **Medium**. In the orbit-api repo the file is not checked out, so do
not guess: emit "FEATURES.md update required in thomasluizon/orbit-ui-mobile" (**High**) so it lands in the
paired frontend PR.

### 13. External-interface evidence

> Reference: root AGENTS.md "Never assume an external interface. Check it, then use it." Gated whenever
> the diff adds or changes a read of a CLI, GitHub/Linear/provider API, Git response, SDK, or library field,
> flag, subcommand, exit code, enum, event argument, or response shape.

Inspect the PR body's evidence for every such read. It must show the **complete selected key/type shape**
or complete compared enum/value set from a real invocation or installed source, redact credentials and
personal/account values, preserve literal formats the code parses, and include an exact reproduction
command or installed `file:line`. An existing callsite, documentation, memory, and a fixture authored with
the implementation are not evidence. Compare the code and its fixture to that evidence; a fixture that
invents the same field proves nothing.

Missing or guessed evidence for a field on the correctness path is **High and Blocking** because the
implementation is unproven against the interface it will execute. If the diff redesigns so the unknown is
not read and success depends only on a confirmed exit code, record why this dimension passes. Do not
manufacture a failure from an absent unconfirmed field, and do not expose a credential while proving it.
