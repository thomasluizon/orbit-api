---
name: pr-review
description: Deep code review of a diff across both Orbit repos against one shared rubric, orchestrating the review subagents and a backward-compat guard. Use when the user asks to review a PR, file, folder, or staged changes in orbit-api or orbit-ui-mobile. Replaces /review and /security-review.
argument-hint: <pr-number | api#N | pr-url | file | folder | blank=staged>
context: fork
---

# PR Review

**Input**: $ARGUMENTS

Review a diff end-to-end against `rubric.md`, fold in the review subagents, guard
against changes that break already-shipped mobile clients, and produce one
severity-ranked report — posted to the PR when the scope is a PR.

This skill subsumes the old `/review` and `/security-review` commands: it does
everything both did and adds the backward-compat guard and a single shared rubric.

<!--
Lockstep twin of orbit-ui-mobile/.claude/skills/pr-review/SKILL.md — keep them behavior-
aligned (Stage-6 rule: the copies can't be deduped across two repos + CIs, so they stay
aligned by hand). Both walk the shared rubric, run the adversarial Phase 6
(`.claude/skills/_shared/verification-protocol.md`) with the cross-model `/second-opinion`
step on surviving Critical findings, and end decisively APPROVE / NEEDS WORK. Sanctioned
differences: default repo (api here / ui there) + the mirror-image `ui#`/`api#` selector, the
subagent set (security-reviewer + contract-aligner here; parity/i18n/design are ui-only), and
`dotnet` validate vs the ui `/validate` skill. `opencode` is absent in orbit-api CI (this
copy's only runtime), so the second-opinion step degrades to UNAVAILABLE here — identical to
how the ui copy behaves in ui CI.
-->

**Golden rule**: every finding is constructive and actionable — a clear fix, a file:line,
and the rule it traces to. Severity is about blast radius, not which dimension raised it.

---

## Phase 0 — Provenance & self-containment

The review dimensions in `rubric.md` were adapted at authoring time from the
**code-review base on claudeskills.info** (https://claudeskills.info — the "code-review"
/ reviewing-AI-code base), then specialized to Orbit's own standards (the ten Code
Standards in root `CLAUDE.md`, the orbit-api hard rules, `eslint-rules/no-comments.cjs`,
`DESIGN.md`, and the folded-in `/security-review` categories), which are richer than any
generic base. The adapted result is committed in-repo at `rubric.md`.

This skill is **self-contained**: it makes **no network call at run time** and has no
runtime marketplace dependency. It reads only local repo files and runs `gh` / `git`
against the project's own remotes. The provenance above is the single WHY-with-URL note
the standard allows; nothing here is fetched live.

---

## Phase 1 — Resolve scope

Parse `$ARGUMENTS` into a review target and detect which repos it touches.

| Input | Repo | Example | Action |
|---|---|---|---|
| Number `123` | api (default) | `#123` | `gh pr view 123 --repo thomasluizon/orbit-api` |
| `ui#123` or `orbit-ui-mobile#123` | ui-mobile | `ui#42` | `gh pr view 42 --repo thomasluizon/orbit-ui-mobile` |
| Full PR URL | parsed from URL | `https://github.com/thomasluizon/orbit-ui-mobile/pull/9` | use the URL's repo |
| File path | local repo | `src/Orbit.Application/Habits/Commands/CreateHabitCommand.cs` | review that single file |
| Folder path | local repo | `src/Orbit.Api/Controllers/` | review every source file under it |
| Blank | local | (none) | review staged changes; if none staged, review unstaged |

**For a PR:**

```bash
gh pr view {N} --repo {OWNER/REPO} --json number,title,body,author,baseRefName,headRefName,files,labels
gh pr diff {N} --repo {OWNER/REPO}
```

**For a file / folder:** use Glob with `**/*.cs` scoped to the target path.

**For blank:**

```bash
git diff --cached --name-only
git diff --cached
```

(If nothing is staged, fall back to `git diff`.)

Then classify the diff: **frontend** (`apps/`, `packages/`), **backend**
(`orbit-api/src/`), or **both**. The classification drives which dimensions are gated in
and which subagents fire in Phase 4.

---

## Phase 2 — Load context

In parallel:

- `C:\Users\thoma\Documents\Programming\Projects\orbit-api\CLAUDE.md` (root + the scoped
  project `CLAUDE.md` for any touched `src/Orbit.*` project or `tests/`).
- `C:\Users\thoma\Documents\Programming\Projects\orbit-ui-mobile\CLAUDE.md` (root +
  `packages/shared`) — only if the diff changes a DTO, endpoint, or contract surface the
  web/mobile clients consume.
- The plan in `.claude/plans/completed/` if the PR body references one.
- **`.claude/skills/pr-review/rubric.md`** — the dimensions, severities, and finding
  template this review walks.
- **`.claude/skills/_shared/verification-protocol.md`** — the shared reliability contract;
  its Verify phase and Deferred ledger run below.

Understand intent: for a PR read the title, body, and linked issue; for a file
understand its role; for staged changes, what is in flight.

---

## Phase 3 — Walk the rubric

Go dimension-by-dimension through `rubric.md` against the diff. For each, emit findings
in the rubric's finding template, tagged with a severity from the ladder. Honor the
gates: skip a dimension whose surface the diff never touches (mark N/A — do not invent
findings), and only run the UI dimension (DESIGN.md / AI-slop, #8) when `apps/*` UI
files changed, the backend hard rules (#13) only when `orbit-api` changed, and
FEATURES.md parity (#14) only when the diff changes the user-facing feature surface.

The dimensions, in order: Correctness · Dead/stale code · SOLID/clean-arch · Comment
policy · No-workaround · Type safety · No `console.log` · DESIGN.md/AI-slop ·
Parity · i18n · Contract drift + backward-compat · Security · Backend hard rules ·
FEATURES.md parity.

Focus on changed code, not pre-existing issues — unless a pre-existing issue is Critical.

**Coverage contract (verification protocol §1):** the diff's changed files are the binding
inventory — rank them worst-first (highest-blast-radius / most-churned files and the
trust-boundary + contract surfaces before stable leaves) so the riskiest code is reviewed
even under pressure, and every changed file ends with a verdict or in the Deferred ledger.
Nothing changed is silently skipped.

Apply the rubric's **Signal gate**: post Critical/High and concretely-actionable Medium only — drop Low/Info nits and style preferences (manufacturing nits to avoid approving is a defect). The outcome is deterministic: **NEEDS WORK** iff any Critical/High finding survives, otherwise **APPROVE**.

---

## Phase 4 — Orchestrate subagents

Delegate the specialist subagents, gated by what the diff touches. Pass each the list of
changed files. Fold every result back into the Phase 3 findings under the matching rubric
dimension.

| Subagent | Gate (fire when…) | Folds into rubric dimension |
|---|---|---|
| `security-reviewer` | any `src/` code changed (i.e. every backend PR) | Security (#12, API side) |
| `contract-aligner` | a DTO, Controller route, or sibling `packages/shared` type / `endpoints.ts` changed | Contract drift (#11) |

`security-reviewer` fires on essentially every orbit-api PR; `contract-aligner` fires when
the change touches the cross-repo contract — launch them together when both apply.

Parity (#9) and i18n (#10) are **frontend-only** dimensions owned by the `orbit-ui-mobile`
side of the review: `parity-checker` and `i18n-syncer` do not live in this repo and never
fire on an orbit-api diff. On a cross-repo PR reviewed from here, mark those dimensions
"not verifiable here" and let the orbit-ui-mobile review cover them.

---

## Phase 5 — Backward-compat guard

Answer one question: **does this diff rename or remove a field that an already-shipped
(old) mobile client still sends or reads?** Old Android builds run a frozen
`@orbit/shared` snapshot, so a server/shared rename is invisible to them — they keep the
old name and silently break. This leans on `contract-aligner`'s field comparison from
Phase 4 and adds the direction + add/remove judgment.

1. From the diff, isolate hunks in `packages/shared/src/types/*.ts` (Zod
   `z.object({...})` schemas) and in `orbit-api/**/DTOs/*.cs` (records / classes).
2. A **removed line** declaring a field (`fieldName: z.…` removed with no matching add),
   OR a **renamed field** (one field removed + one added in the same schema, types
   compatible), is a candidate.
3. Classify each candidate and tag per `rubric.md` dimension 11:
   - Removed/renamed in a **response** shape → old readers get `undefined` →
     **`⚠️ breaks old mobile clients` (Critical)**, unless already optional AND unused
     (cite the grep).
   - Removed/renamed in a **request** shape, or a field made **newly-required** → old
     senders are rejected by validation → **`⚠️ breaks old mobile clients` (Critical)**.
   - **Added optional** field → forward-compatible → **Info**.
   - **Enum value removed** → old clients may still send it → flag.
4. In the fix, recommend the compatible alternative: keep-and-deprecate the old field,
   accept both names server-side for a release, or gate behind the min-version gate.
   When old-client reach is uncertain, downgrade to **High** with a "verify old-client
   usage" note rather than over-claiming Critical.

Scope is **field add/remove/rename in the reviewed diff**. Semantic/behavioral breaks
under an unchanged field name are caught by Correctness (#1) and the human reviewer — do
not over-claim completeness here.

---

## Phase 6 — Verify findings (adversarial)

Run `.claude/skills/_shared/verification-protocol.md` before validating — every finding
that will decide the outcome has to survive a challenge first.

1. **Adversarial pass (§2).** For every **Critical / High** finding (including any
   `⚠️ breaks old mobile clients`), spawn an independent skeptic subagent (3 concurrent)
   whose only job is to *refute* it — read the cited `file:line` in full diff context and
   argue it is a false positive (the path is unreachable, the value already validated, the
   field actually still present or optional-and-unused with the grep to prove it, a
   duplicate, the severity inflated). Default to refuted when uncertain. Drop or downgrade
   anything the skeptic disproves — a false Critical that blocks a clean PR is as costly as
   a missed one. The survivors decide the recommendation.
2. **Cross-model second opinion (§2, Critical survivors — interactive only).** For each
   **Critical** finding that survives step 1 (including any `⚠️ breaks old mobile clients`),
   fire **`/second-opinion`** so a *different* model (GLM-5.2 via opencode) independently
   judges it — pipe the finding dossier (title · severity · `repo/path:line` · the claimed
   defect · the cited code hunk) to `node .claude/skills/second-opinion/second-opinion.mjs`
   and apply the JSON verdict it prints:
   - **AGREE** → the finding is cross-model corroborated; keep the severity, note the
     confirmation.
   - **DISAGREE** → tag the finding **`CONTESTED`** and record GLM's `reasoning` beside
     Claude's; surface **both** verdicts in the report. It stays Critical — the
     disagreement is the human's to resolve. **Never** let it force a merge or silently drop
     the finding (the skeptic in step 1 already owns the drop decision).
   - **UNSURE** → note it; the finding stands as step 1 left it.
   - **UNAVAILABLE** (opencode absent — **always the case in CI**, or capped / offline) →
     skip the second opinion, leave the finding unchanged, and state it in one line. Never
     read "couldn't ask" as agreement. This graceful-degradation path keeps the CI review
     (no opencode) byte-for-byte identical to today.
   Scope to **Critical only** (not High) — cross-model time/cost is reserved for the findings
   that actually block, per the on-demand-diversity budget (research.md). CONTESTED never
   changes the deterministic recommendation: a surviving Critical still means NEEDS WORK.
3. **Completeness pass (§3).** One pass only — a diff is its own boundary, so no loop: ask
   *"what changed file or hunk did I not give a verdict, what dimension did I mark N/A
   without checking its surface?"* and close the gap before reporting.
4. **Deferred ledger (§4).** Every dimension marked N/A and every changed file not
   verdicted goes into the report's **Deferred** line with a one-line reason — so "clean"
   never hides "not looked at."

---

## Phase 7 — Validate

Run the backend checks from the orbit-api root:

```bash
dotnet build
dotnet test
```

Record each result as PASS / FAIL with the error summary for the report's validation
table. For a file/folder scope with no working-tree changes, validation is N/A. In CI this
phase is skipped — Build / Unit Tests run as separate required checks.

---

## Phase 8 — Report

Write the report, then post it to the PR when the scope is a PR.

```bash
mkdir -p .claude/reviews
```

**Output path**: `.claude/reviews/{scope-name}-review.md`

```markdown
# Code Review: {SCOPE}

**Scope**: {PR #N in repo / file / folder / staged}
**Recommendation**: APPROVE / NEEDS WORK

## Summary

{2-3 sentences: what was reviewed and the overall assessment.}

## Findings

### Critical
{findings in the rubric template, or "None" — `⚠️ breaks old mobile clients` findings sort here first.
A finding a cross-model second opinion disputed carries a **`CONTESTED`** tag with both
verdicts inline — e.g. "Claude: Critical · GLM-5.2: DISAGREE — {GLM's reasoning}" — so the
human sees the disagreement. It stays Critical; the tag never downgrades it.}

### High
{… or "None"}

### Medium
{… or "None"}

### Low / Info
{… or "None"}

## Subagents

| Agent | Verdict |
|---|---|
| security-reviewer | PASS / FAIL / N/A |
| contract-aligner | MATCH / DRIFT / NOT VERIFIABLE / N/A |

## Validation

| Check | Result |
|---|---|
| Build (dotnet) | PASS / FAIL / N/A |
| Tests (dotnet) | PASS / FAIL / N/A |

## Deferred — N/A dimensions & files not verdicted

{Per the verification protocol §4: each dimension marked N/A (with why its surface wasn't
touched) and any changed file not given a verdict — one line each. "Nothing deferred" if
every dimension and file got a verdict.}

## What's good

{positive observations}

## Recommendation

{what needs to happen next}
```

### Post to GitHub (PR scope only)

The review is **decisive** — it ends as APPROVE or REQUEST_CHANGES, never a bare comment.
Map the deterministic recommendation (NEEDS WORK iff any Critical/High finding):

```bash
# NEEDS WORK — any Critical/High (incl. ⚠️ old-client break)
gh pr review {N} --repo {OWNER/REPO} --request-changes --body-file .claude/reviews/{scope-name}-review.md
# APPROVE — no Critical/High
gh pr review {N} --repo {OWNER/REPO} --approve --body-file .claude/reviews/{scope-name}-review.md
```

Inline comments (Critical/High, tied to a specific line) via the PR review-comments
endpoint / `mcp__github_inline_comment__create_inline_comment`.

**Caller context decides who posts:**

- **CI wrapper** (`.github/workflows/claude-review.yml`) invokes this skill: it owns the
  single decisive post — produce the report + recommendation and let it submit (skip this
  posting step). In CI also skip Phase 7 (Validate) — Build / Unit Tests run as separate
  required checks — and mark any dimension that needs the un-checked-out sibling repo as
  "not verifiable in CI". The Phase 6 adversarial pass still runs; its `/second-opinion` step
  returns UNAVAILABLE (no `opencode` in CI) and the findings stand.
- **Local, a PR you do NOT own**: post the decisive review yourself per the recommendation.
- **Local, your OWN PR** (GitHub blocks self-approval): write the report and post it with
  `--comment` instead — do not fail trying to `--approve`.
- **Local file / folder / staged** scope: only write the report file, never post.

---

## Output

```markdown
## Review Complete

**Scope**: {what was reviewed}
**Recommendation**: APPROVE / NEEDS WORK

| Severity | Count |
|---|---|
| Critical (incl. ⚠️ old-client breaks) | {N} |
| High | {N} |
| Medium | {N} |
| Low / Info | {N} |

**Report**: `.claude/reviews/{scope-name}-review.md`
{Posted to PR #N — only if scope was a PR}
```
