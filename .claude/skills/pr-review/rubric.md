# Orbit Review Rubric

The single source of truth for what a review checks **in this repo**. Exactly one thing
reads it: `/pr-review` (`.claude/skills/pr-review/SKILL.md`), which walks it
dimension-by-dimension over a **diff**, orchestrator-side in a fresh worktree at the pull
request head. There is no CI reviewer in either repository. Unlike
orbit-ui-mobile, this repo ships no `/audit-code-quality` skill, so nothing here walks the
rubric over the whole repo.

**A lockstep TWIN does exist**, at `orbit-ui-mobile/.claude/skills/pr-review/rubric.md`,
where `/audit-code-quality` also walks it. Two repos mean the file cannot be deduped, so
`orbit-ui-mobile/tools/check-lockstep.mjs` compares the two line by line and a divergence
is legal only when its diff-hunk fingerprint carries a justification in
`orbit-ui-mobile/tools/lockstep-declarations.json`, which that repository owns and
recomputes. `Harness Lockstep` is a REQUIRED status check on this repository's `main`, so
an unmirrored, undeclared edit here turns that check red. It is not kept aligned by hand:
it is machine-compared. Sanctioned divergences run in both directions: backend-only
material here, such as dimension 13's transaction-teardown bullet (`ORBIT0002`), and
orbit-ui-mobile-only material there, such as dimension 15's harness-execution evidence,
which has no counterpart in this repository because the harness runner
`tools/test-tools.mjs` and the per-tool case modules it loads from `tools/__tests__/` exist
only in orbit-ui-mobile. Change a dimension here and mirror it there in the same task; a
drift between the two is a defect, not a variant.

It is command-agnostic on purpose: it contains **dimensions, severities, and templates**,
no orchestration, no scope resolution, no GitHub mechanics. Those live in the consuming
skill.

Every finding cites the rule it came from (a `CLAUDE.md` rule number, `no-comments.cjs`,
a `DESIGN.md` section, an orbit-api hard rule, or a security category) so the author can
trace it back. Tag every finding with a severity from the ladder at the bottom.

---

## Severity ladder

One vocabulary for every dimension. A finding's severity is about blast radius, not
which dimension raised it.

| Severity | Meaning | Action |
|---|---|---|
| **Critical** | Exploitable, data loss, crash, broken contract, or **breaks an already-shipped client**. | Block merge. Fix now. |
| **High** | Type-safety hole, missing error handling, missing parity, missing validation, dead code that ships. | Fix before merge. |
| **Medium** | Pattern inconsistency, missing edge case, missing test, defense-in-depth gap. | Fix soon; OK to merge with a tracked follow-up. |
| **Low** | Style deviation, minor naming, micro-cleanup. | Address when convenient. |
| **Info** | Observation, forward-compatible note, praise. | No action required. |

### The `⚠️ breaks old mobile clients` marker

A **Critical-class** marker, separate from the severity word, applied to any finding
where a `packages/shared` Zod schema or an orbit-api DTO change makes an
already-installed Android client misbehave. Old Android builds ship a **frozen
`@orbit/shared` snapshot** — a server-side or shared rename is invisible to them; they
keep using the old field name and silently break. Detection and classification are
defined in the **Contract drift + backward-compat guard** dimension below. Any finding
carrying this marker is Critical regardless of how small the diff looks.

---

## Signal gate — post high-signal only

The review CONVERGES; it is not a nit machine. What gets posted is gated by severity:

- **Critical / High** — always post; these decide the outcome.
- **Medium** — post only when concretely actionable (a specific missing test, a real unhandled edge case, a definite pattern break). Never speculative.
- **Low / Info** — do **not** post as PR-review findings. A local deep audit may list them; on a PR they are noise.

**Never post — not findings, in any dimension:** style preferences (verbose vs concise, arrow vs named function, optional-chaining vs guard); naming bikeshed; reformatting; "consider extracting / hoisting / future-proofing" on code that already works; Zod modifier ordering (`.nullable().optional()`) when behavior is correct; magic-number→const when the value is obvious from context; anything the author chose defensibly that you would merely prefer otherwise; anything already addressed in an earlier commit or a resolved review thread.

**Outcome is deterministic:** `NEEDS WORK` iff ≥1 surviving **Critical or High** finding (including any `⚠️ breaks old mobile clients`); otherwise `APPROVE`. Medium / Low / Info never force NEEDS WORK. Never manufacture a Critical/High finding to avoid approving — a clean diff earns a plain approval.

---

## Finding template

Every finding, every dimension, the same shape:

```
[SEVERITY] <one-line title>  ⚠️ breaks old mobile clients (only if applicable)
· dimension: <rubric dimension>
· location: <repo>/<path>:<line>
· issue: <1-2 sentences — what is wrong>
· risk: <1-2 sentences — what goes wrong if it ships>
· fix: <the concrete change, or a corrected snippet>
· reference: <CLAUDE.md rule N | no-comments.cjs | DESIGN.md "Bans" | orbit-api hard rule | OWASP | security category>
```

---

## Dimensions

Each dimension is a checklist. A diff that doesn't touch a dimension's surface skips it
(noted as N/A) — do not invent findings for files the diff never changes. UI dimensions
are **gated to `apps/*` changes**; backend hard rules are gated to `orbit-api` changes.

### 1. Correctness

> Reference: the change's own intent (PR body / linked issue / plan).

- Does it do what the PR/issue says, across every boundary it crosses?
- Data flow: request shape in → handler → response shape out → consumer reads it. Any
  mismatch in that chain?
- Boundary conditions: empty list, zero, null, first/last item, timezone edges (dates
  must route through `IUserDateService` on the backend — see dimension 13).
- State: are loading / error / empty states all handled, not just the happy path?
- Concurrency / ordering assumptions that the diff silently relies on.

### 2. Dead / stale code

> Reference: CLAUDE.md rule 2; orbit-api "No dead code".

- Orphaned exports, functions, or types with **zero references** after this change
  (cite the zero-reference grep).
- Dead branches that can no longer be reached.
- Commented-out code blocks.
- Stub functions and speculative "just in case" parameters.
- Imports / variables the diff itself left unused.

### 3. SOLID / clean architecture

> Reference: CLAUDE.md rules 6, 7, 10.

- Function size soft cap ~50 lines, nesting ~3 levels; hard cap ~100. Over → the
  function is doing too much, split it (rule 7). A file around 1,000 lines or one
  carrying several unrelated responsibilities is a cohesion finding when the evidence
  supports it. Split by responsibility or extract well-named pure helpers; when a split
  merely moves the same tangle, report relocation, not simplification.
- New endpoints follow CQRS (Command/Query + Handler + Validator) on the backend.
- Frontend respects the adapter split: Server Action (web) vs `apiClient` (mobile);
  shared logic in `packages/shared`, not duplicated per app.
- For branch-heavy code, look for the **code-judo move**: a state-model or data-shape
  reframe that deletes whole branches. Prefer that reframe to adding more conditionals.
- Flag special-case `if/else` ladders, deeply coupled branching, and flag soup that grows
  per case. Prefer the smallest fitting remedy: early returns, a lookup table, or
  polymorphism that makes the variants explicit.
- No premature abstraction — extract on the third real use, not the second (rule 6).
  Three similar lines beat a helper invented for two. Apply the **deletion test** to thin
  wrappers: if removing the module makes its complexity vanish instead of exposing useful
  behavior, it is pass-through indirection. Flag magical abstractions that hide control
  flow; delete needless wrappers or deepen the abstraction until its boundary is clear.
- Repeated casts or optionality juggling indicate a structural type mismatch when one
  better type or one parse at the trust boundary would remove the churn. Recommend that
  structural fix, but exclude gate-owned mechanical forms such as `as any`,
  `as unknown as X`, and unjustified `null!`; dimension 6 owns those direct violations.
- DRY at the right level (rule 10): cross-app → `packages/shared`; cross-component →
  `apps/<platform>/components/`; repeated handler or cross-function logic → one
  well-named helper at the narrowest shared layer. Don't lift to `shared` for one caller.
- Business logic belongs in its canonical domain, CQRS, or shared-logic layer, not in a
  controller, component, DTO, or platform adapter. Move the rule to the owning layer
  instead of duplicating or coordinating it at the edges.

### 4. Comment policy

> Reference: `eslint-rules/no-comments.cjs:17-24` (local/no-comments); orbit-api `ORBIT0001`.

The reviewer flags a comment exactly when the linter would. **Allowed**, nothing else:

- `/** … */` JSDoc block (a `Block` comment whose value starts with `*`) on an exported
  function, hook, or type — one short paragraph on intent and contract.
- A `///` line (a `Line` comment whose trimmed value starts with `/`) — TS triple-slash
  reference / C# XML doc.
- A tooling directive matching `no-comments.cjs`'s `DIRECTIVE` set: `eslint-disable*`,
  `@ts-*`, `ts-*`, `prettier-ignore`, `@jsx`, coverage/bundler pragmas
  (`c8`/`v8`/`istanbul`/`webpack`/`@vite`/`@vitest`/`@__PURE__`).
- A WHY note that contains an `http(s)://` URL to an upstream issue/PR/doc — a real
  external constraint the author cannot fix here.

Everything else is a finding: `//` narration, restating code, task/PR/fix references,
TODOs. The fix is never "reword the comment" — it is **rename the symbol or extract a
well-named function** so the code reads without prose.

### 5. No-workaround / root-cause

> Reference: CLAUDE.md rule 1; orbit-api "No workarounds".

- The signature smell: **ugly frontend written to dodge a missing or awkward API** —
  client-side reshaping, refetch-and-merge, optimistic patches that paper over a shape
  the backend should return directly. Flag it and point at the upstream fix.
- Fallbacks, defensive branches, or local patches for a problem that belongs to a
  config, a type, or a shared util.
- An unavoidable workaround is allowed **only** with a one-line WHY-with-URL note
  (dimension 4). No link → it is not a sanctioned workaround.

### 6. Type safety

> Reference: CLAUDE.md rule 3.

- TypeScript: any `any`, `as any`, or `as unknown as X` escape hatch. Use `unknown`
  with narrowing instead.
- C#: implicit conversions and unjustified `null!` (the C# analog of an `as any`).
- Inferred-`any` callbacks and untyped external payloads crossing a trust boundary
  without a Zod parse.

### 7. No `console.log`

> Reference: CLAUDE.md rule 4.

- Any `console.log` (or stray `print`/`Debug.WriteLine`) in production code. Use the
  project logger or remove it. Test files are exempt.

### 8. DESIGN.md / AI-slop

> Reference: `DESIGN.md` at the orbit-ui-mobile repo root, sections **Identity & anchor
> (locked)** (:18), **Bans** (:469), **AI-slop test** (:505), **Scene-sentence test**
> (:525). Cite the section name, not the line alone: the line numbers move, the section
> names do not. **Gated: only when the diff touches `apps/*` UI files.** An
> orbit-api-only diff marks this N/A, and where the orbit-ui-mobile checkout is absent the
> reference cannot be read at all, so it is "not verifiable here".

The anchor is the **de-decorated navy-violet orbital** (the #539 freeze, 2026-07-17).
Identity comes from three carriers and nothing else: the **orbital logo mark**, the
**Astra orbital glyph**, and **ring-shaped status and progress indicators**. It never
comes from a background gradient, a glow, decorative background orbit arcs, or texture.
**Quiet decoration is still decoration**: a softened glow, a 0.03-opacity texture, a
"subtle" mesh is the same finding as the loud version. The freeze removed the layer, it
did not dim it.

Scan for the AI-slop tells:

- Decoration used as hierarchy: any glow, gradient wash, gradient border, gradient text
  (`bg-clip-text` over a gradient), mesh, bloom, texture, or "quiet" background effect.
  There is no sanctioned gradient and no sanctioned glow left: `--gradient-header`,
  `GradientTop`, and the primary-glow shadow token are **deleted**.
- Cards in cards (opaque card-on-card on dark), and cards used where spacing would have
  grouped.
- A coloured side-stripe border on a row, card, callout, or alert.
- Connector or tree lines in a hierarchy.
- Grey text on coloured backgrounds; rounded-square icon tiles above headings.
- Semantic-red destructive fills where the spec shows a text pill.
- An oversized centered H1 outside a hero context.
- The hero-metric template used as decoration, or any invented precise-looking number.
- A whole-section fade-and-rise scroll reveal, or any page-load choreography.
- An animation whose purpose cannot be named from the closed list.
- A heading and the intro beneath it saying the same thing; an eyebrow that enumerates
  rather than labels.

Token checks that need judgment:

- **`--primary` is fill and graphic only; `--primary-soft` is the accent text token.**
  Accent-coloured small text on the canvas is a finding, not a preference. On light both
  resolve to the same value, so the split only bites on dark.
- **Accent rationing**: the accent appears on the active tab, progress and ring
  indicators, done dots, the primary CTA, the FAB, and active nav. That is the whole
  list. Accent on a card, a row, a border, a heading, or an icon not communicating state
  is decoration.
- No raw `--slate-*` reference and no hardcoded violet rgba. Semantic tokens only; tints
  come from `--primary-rgb` (web) or `tintFromPrimary` (mobile).
- No hand-rolled `box-shadow` heavier than `--shadow-1/2/3`. Shadows model occlusion
  under a lifted surface; they never carry the accent hue.
- No `transition-all` (animate `transform` and `opacity`, named); no `h-screen` (use
  `min-h-dvh`); no new font families, radii, or colors outside the spec.
- No per-component scheme branch. Schemes resolve through tokens.

**Do not hand-flag what an ESLint `local/*` rule already fails on** in orbit-ui-mobile:
decorative glow, raw gradient and gradient text, side-stripe borders, off-scale spacing,
`space-x-*` / `space-y-*`, overshoot easing, arbitrary z-index, full-bleed pill CTAs, and
em dashes in copy each have a gate. Report the gate's verdict; re-flagging it by hand is
noise. `DESIGN.md`'s **Enforcement** section is the authoritative gate-versus-reviewer
split.

Then the **scene-sentence test**: describe the rendered screen in one sentence, as if
narrating a film scene. If it reads like every other SaaS app ("a clean modern dashboard
with cards"), it is generic. The sentence must name Orbit's character: a near-black
neutral canvas, quiet tonal panels separated by hairlines, one violet reserved for what is
done and what is next, and the orbital ring language carrying the identity. **If the only
way to make the sentence specific is to describe decoration, the design has failed and the
decoration is not the fix.**

### 9. Parity (web ↔ mobile)

> Reference: root CLAUDE.md "Cross-platform parity (MANDATORY)". Engine: `parity-checker`.

- Every changed `apps/web/**` file has its `apps/mobile/**` mirror changed in the same
  PR (and vice-versa), per the mirror map in the `parity-checker` contract.
- The mirror is **behaviorally identical** — same logic, data flow, error handling.
  Only platform adapters may differ (BFF vs direct API, cookie vs SecureStore, shadcn vs
  NativeWind, next-intl vs i18next).
- `MISSING` (no mirror file) is High; `PARTIAL` (mirror exists, not updated) is High
  until proven intentional.

### 10. i18n

> Reference: root CLAUDE.md (add keys to both locales in the same edit). Engine: `i18n-syncer`.

- Every new user-facing string has a key in **both** `packages/shared/src/i18n/en.json`
  AND `pt-BR.json` (`MISSING_PT` / `MISSING_EN` are findings).
- No `ORPHANED` callsite referencing a key that exists in neither locale.
- Brand words (`Orbit`, `Astra`) stay untranslated.
- Keys stay dot-notation hierarchical and alphabetized within their hierarchy.

### 11. Contract drift + backward-compat guard

> Reference: CLAUDE.md "API contract" / orbit-api "Cross-repo parity contract".
> Engine: `contract-aligner` for the field-by-field shape comparison.

First, drift (from `contract-aligner`): `MISSING_DTO`, `MISSING_ZOD`, `FIELD_DRIFT`,
`PATH_DRIFT` between `packages/shared/src/types/*` + `endpoints.ts` and the orbit-api
DTOs + Controller routes.

Then the **backward-compat judgment** drift detection alone does not make — the
direction and the add/remove of each field, because old Android clients run a frozen
`@orbit/shared`:

- **Field removed from / renamed in a *response* DTO or schema** → old clients that read
  it now get `undefined` → **`⚠️ breaks old mobile clients` (Critical)**, unless the
  field was already optional AND unused (cite the grep proving it).
- **Field removed from / renamed in a *request* DTO or schema, or a field made
  newly-required** → old clients still send the old shape → server validation rejects
  it → **`⚠️ breaks old mobile clients` (Critical)**.
- **Field added as optional** → forward-compatible → **Info**, not a break.
- **Enum value removed** → old clients may still send it → flag.

Recommend the compatible alternative in the fix: keep-and-deprecate the old field,
accept both names server-side for a release, or gate behind the min-version gate. When
old-client reach is uncertain, downgrade to **High** with a "verify old-client usage"
note rather than over-claiming Critical.

### 12. Security

> Reference: OWASP + orbit-api hard rules. Engine for API code: `security-reviewer`
> (the frontend categories below are what that agent explicitly does NOT cover).

Review the categories relevant to the change.

**Injection** — raw or string-interpolated SQL / EF queries; XSS via unescaped user
input in JSX or `dangerouslySetInnerHTML`; command injection (`exec()` /
`Process.Start()` with user input); path traversal from unsanitized input in file paths.

**Authentication & authorization** — missing `[Authorize]` on a new API endpoint (the
default is `[Authorize]`; missing both it and `[AllowAnonymous]` is a bug); missing auth
checks on Server Actions / BFF routes; hardcoded credentials, JWT secrets, or API keys;
session config must stay httpOnly + sameSite strict + secure always; CORS must stay
restrictive (no `AllowAnyHeader()` / `AllowAnyMethod()`, never `AllowAnyOrigin()` with
`AllowCredentials()`); the Stripe API key set globally in `Program.cs`, never
per-request.

**Data exposure** — sensitive data (passwords, tokens, PII) in `console.log` or
`ILogger`; responses leaking stack traces or DB schema; secrets in source / config;
missing input validation at the API boundary; webhook handlers must verify signatures
(Stripe `WebhookSecret`).

**Dependency & configuration** — known-vulnerable dependency versions; debug mode
enabled in production config; `SecurityHeadersMiddleware` (nosniff, DENY,
referrer-policy, XSS) must not be disabled; request size limits (Kestrel 10MB global,
chat endpoint 20MB) intact.

**Cryptography** — weak hashing (MD5 / SHA1 for passwords — BCrypt is the standard);
hardcoded encryption keys; insecure RNG for security-sensitive values; HTTPS enforcement
intact.

**Error handling** — verbose error messages exposing internals; unhandled promise
rejections / unobserved tasks; catch blocks that swallow errors silently; `Result<T>`
propagated correctly (`PropagateError<T>()` / `ToPayGateAwareResult()` per
`orbit-api/CLAUDE.md`).

**Validation (Orbit-specific)** — the backend is the source of truth; frontend Zod is
convenience only. Every new endpoint needs FluentValidation **and** a domain-entity
guard in the factory/update method. Numeric bounds, date ranges, and mutually exclusive
options are enforced server-side.

### 13. Backend hard rules

> Reference: orbit-api/CLAUDE.md "Cross-cutting hard rules". **Gated: only when the diff
> touches `orbit-api`.**

- **Timezone**: user-facing dates use `IUserDateService.GetUserTodayAsync(userId)`,
  never `DateOnly.FromDateTime(DateTime.UtcNow)`. `DateTime.UtcNow` is only for
  `CreatedAtUtc` timestamps and cache keys.
- **Authorization**: every controller endpoint requires JWT Bearer unless it is
  `/health` or `/api/auth/*`; new endpoints default to `[Authorize]`.
- **Validation**: validators in `Orbit.Application/<Feature>/Validators/` **and**
  domain-entity guards.
- **Logging**: structured, PascalCase properties, English only —
  `logger.LogInformation("Action {Property}", value)`, never interpolated.
- **Transaction teardown**: no explicit `RollbackAsync()`/`Rollback()` inside a
  `using`/`await using`-scoped EF transaction — scope disposal already rolls back an
  uncommitted transaction. The `ORBIT0002` analyzer (`src/Orbit.Analyzers`) fails the CI
  build on violations; a genuinely manually-owned transaction (no `using`) is exempt.
- **Tests**: every new command/query handler, validator, and service has a unit test
  (unit only — no integration or E2E suite exists).

### 14. FEATURES.md parity (feature inventory)

> Reference: `FEATURES.md` at the orbit-ui-mobile repo root — the code-derived feature
> inventory (#378). **Gated: only when the diff changes the user-facing feature surface.**

- Triggers: a feature added, materially changed, or removed — new screen/route/tab, new
  or removed Astra (`IAiTool`) or MCP (`[McpServerTool]`) tool, plan-gating change
  (`PayGateService` / `AppConstants`), platform-availability change, or locale-specific
  behavior change. Pure refactors, bugfixes, and visual polish with no behavior change
  are N/A.
- The same PR updates `FEATURES.md` — row added, edited, or removed, with the Gating /
  Platform / Locale columns still accurate, and the stated tool counts corrected when
  tools are added or removed. A missing update is **High** (same bar as a missing
  web↔mobile mirror); a gating or platform claim the diff makes stale is **High** too.
- Headline-set features (Astra, MCP, social, core tracker) also surface in the in-app
  feature guide (`onboarding.featureGuide.*`) — if the change makes the guide wrong or
  incomplete, flag it (**Medium**).
- In the orbit-api repo the file is not in the checkout: do not verify — emit the
  finding as "FEATURES.md update required in thomasluizon/orbit-ui-mobile" (**High**)
  so it lands in the paired frontend PR.

---

## Self-review note

This rubric and the skill that walks it are themselves held to the standard they
enforce: every code snippet here is exemplary (no narration comments, no `any`, no
`console.log`). Dogfood the rubric against the review output before posting.
