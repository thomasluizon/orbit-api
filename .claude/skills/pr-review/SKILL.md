---
name: pr-review
description: Capped two-round review of ONE PR diff against rubric.md, by a session that did not write the code. Use when asked to review a PR in orbit-ui-mobile or orbit-api.
argument-hint: <ui#N | api#N | pr-url>
---

# PR Review

**Input**: $ARGUMENTS

Review one diff against `rubric.md` and emit `findings.json`. This review is built to
**terminate**, not to be exhaustive. Read the whole contract below before reading any code.

## Why this shape

PR #672 ran **9 local `/pr-review` rounds over 38 hours**. Verdict every time `NEEDS_WORK`.
**19 findings, 19 unique, zero repeats.** The fixer fixed everything; the reviewer found
brand-new issues every round on a 7,078-line diff. Termination required
"the reviewer finds nothing", which on that diff has probability about zero. The skill was
correctly implemented and mathematically unable to stop.

A severity floor alone would not have saved it: every one of the 19 was High or Critical, and
there were no nitpicks. What was missing was a **round cap** and a **frozen finding list**.

Research backing: every mainstream reviewer converges on severity floors (Codex's GitHub surface
flags only P0/P1) and on verification gating before a finding is shown. Practitioner consensus on
round caps is 1-2 for CI automation, escalating to a human at the cap. Anthropic's own docs concede
that "a rule like after the first review, suppress new nits and post Important findings only stops
a one-line fix from reaching round seven on style alone."

---

## The termination contract

Six rules. All six bind. None is advisory, and none is negotiable mid-review.

**1. Freeze the ruleset before round 1.** Capture the PR's live `baseRefOid`, materialize
`rubric.md` from that exact Git blob, and store both the OID and artifact path in the receipt.
That snapshot supplies the dimensions and severity definitions for both rounds; never reload the
mutable main-checkout copy in round 2. A base change or an unexpected head change invalidates the
review and starts a fresh round 1. The single prescribed round-1-to-round-2 fixer head change keeps
the frozen receipt only when the reviewer is handed that receipt plus both exact head OIDs; any
other head transition restarts round 1. A bar raised after the captured base is a follow-up ticket
against the rubric, never a new bar this review may apply.

**2. Cross-vendor reviewer, fresh session.** Normal: Claude Opus 5 at `high`. `--codex-only`:
Sol at `xhigh` in a **separate** session, and the run prints `DEGRADED: same-vendor review` in its
opening line and in the PR comment. **The invariant in both modes: the session that writes the code
is never the session that reviews it.** Same model is acceptable; same session is not. LLM judges
measurably favour their own family's output, and the bias extends across the whole vendor family
(arXiv 2603.04582, arXiv 2508.06709). The direction is corroborated; the magnitude in a real PR
loop is **unmeasured**. State it that honestly, and never claim the degraded mode is unbiased.

**3. Apply the target repository's review floor, then classify each survivor as Blocking or
Non-blocking.** `orbit-api/AGENTS.md` permits P0/P1 only: Critical maps to P0, High maps to P1,
and Medium/Low/Info candidates are discarded before the receipt and create no ticket. For a
surviving candidate, Blocking means it **breaks behaviour, security, or data integrity**.
Everything else is **auto-filed as a follow-up Linear ticket** and never fixed in this PR. Apply
the floor and classification once at report time; neither is renegotiated per round.

**4. Diff-only scope.** Read `gh pr diff`. You are reviewing a diff, not a repository. Open a
repository file only to resolve a symbol the diff itself cites. A defect reachable only by browsing
code the diff never touched is out of scope; if it matters, it is a ticket. Dimension 7 has one
bounded exception: when the target diff changes a request, response, endpoint, or schema contract,
perform a targeted read/search in the sibling repository's primary `main` checkout for the shipped
consumer or provider symbols cited by that contract. If the PR body links a paired contract PR,
inspect that paired diff too. This is contract evidence, not permission to browse for unrelated
defects.

**5. Monotonic round 2.** Re-check **only** the frozen Blocking list, answering `CLOSED` or `OPEN`
per finding. New findings are forbidden, with exactly one mechanical carve-out: **any defect on a
line the fixer's own round-2 diff touched**. That line set is computed as `git diff <r1>..<r2>
--unified=0` and handed to the reviewer as data it cannot widen.

> The carve-out was widened during cross-model review. The original admitted a new finding only on
> the cited line of an existing finding, which let a fixer break something elsewhere in the same
> file unreported. GPT-5.6 Sol found the hole.

**6. Hard cap of 2 rounds, enforced in code. No round-3 path exists.** At the cap, hand to Thomas
with the OPEN findings listed. Do not re-review, do not request one more round, do not merge.

---

## Reviewer environment

The reviewer runs **from the MAIN CHECKOUT, never the worktree**, so it cannot load the PR's own
`AGENTS.md`. A reviewer that loads it is reading instructions written by the change under review.

Concretely, before round 1:

- Confirm this session did not write any of the code in the diff. If it did, **stop**: the review
  is invalid under rule 2. Fork-inherited context counts as the same session.
- `cwd` is the main checkout of the repo the PR targets. Never a worktree, never the fixer's tree.
- The inputs are the diff, the captured rubric artifact, and the PR title/body/linked ticket for intent, plus the
  targeted sibling-primary/paired-PR contract evidence permitted by rule 4 and nothing broader.

---

## Procedure

### Resolve scope

| Input | Repo | Command |
|---|---|---|
| `ui#123` | orbit-ui-mobile | `gh pr view 123 --repo thomasluizon/orbit-ui-mobile` |
| `api#123` | orbit-api | `gh pr view 123 --repo thomasluizon/orbit-api` |
| Full PR URL | parsed from the URL | use the URL's repo |

A bare number or blank scope is ambiguous across two repositories and must be refused. The caller
provides a repository-qualified selector or full PR URL; caller cwd never chooses the repository.

```bash
gh pr view {N} --repo {OWNER/REPO} --json number,title,body,baseRefName,baseRefOid,headRefName,headRefOid,files,labels
gh pr diff {N} --repo {OWNER/REPO} > <scratchpad>/pr-{N}.diff
git show {baseRefOid}:.claude/skills/pr-review/rubric.md > <scratchpad>/pr-{N}-rubric.md
```

If the base object is not present locally, fetch that exact OID from `origin` before `git show`;
never substitute the current working-tree rubric. Record `baseRefOid`, `headRefOid`, the rubric
artifact path, and the complete live selected key/type evidence required by the target AGENTS.md.

Read the captured rubric once, then classify repository-relative paths using the target repository:
**frontend** is `apps/` or `packages/` in orbit-ui-mobile; **backend** is `src/` or `tests/` in
orbit-api. A paired diff can be **both**. That classification gates which rubric dimensions apply.

### Round 1

1. Walk `rubric.md` dimension by dimension over the diff. Skip a dimension whose surface the diff
   never touches and record it as N/A with the reason. Do not invent findings to fill a dimension.
2. Verify each candidate finding against the diff text before writing it down: quote the line you
   are claiming about. A finding you cannot anchor to a diff line does not get reported.
3. Drop candidates below the target repository floor, then classify each survivor Blocking or
   Non-blocking by rule 3.
4. Write `findings.json`. **The list is now frozen.**
5. File every Non-blocking finding as a follow-up Linear ticket (one per finding, title = the
   claim, body = file, line, and the rubric dimension). They are not fixed in this PR.
6. Zero Blocking findings means the review is over. Hand to Thomas.

### Round 2

1. Fetch both exact reviewed head OIDs from `origin`, verify both objects exist, then compute the
   fixer's line set: `git diff <r1-sha>..<r2-sha> --unified=0`. Pass it as data. Never assume either
   head object is already present in the mandated main checkout.
2. For each frozen Blocking finding, answer `CLOSED` or `OPEN` with the line that settles it.
3. A new finding is admissible **only** if its line is in the round-2 line set from step 1, and it
   is Blocking. Append every admitted new blocker to `findings.json` with `status: "OPEN"`; it is
   part of the open list and verdict calculation. Anything else is a follow-up ticket.
4. Hand to Thomas as `CLEAN` only when every frozen blocker is `CLOSED` **and no admitted round-2
   blocker is OPEN**. Otherwise stop with every open frozen or admitted blocker listed. There is no
   round 3 and no opportunity to hide the new blocker behind the frozen list.

---

## Output contract

`findings.json` is one receipt object. Its `findings` array contains one object per finding:

```json
{"reviewerKind":"independent","verdict":"BLOCKING","rounds":1,
 "reviewedHeadOid":"<full head SHA>","baseSha":"<full base SHA>",
 "rubricBaseOid":"<full base SHA>","rubricArtifactPath":"<absolute snapshot path>",
 "artifactPath":"<absolute path to this file>",
 "frozenFindingIds":["F1"],
 "findings":[{"id":"F1","severity":"High","file":"apps/web/hooks/use-streak.ts","line":42,
   "claim":"one sentence: what is wrong and what goes wrong if it ships","blocking":true}]}
```

`severity` is descriptive and comes from the rubric's ladder. A candidate below the target
repository floor never enters this array. `blocking` is the decision for a surviving candidate:
a High that does not break behaviour, security, or data integrity is `"blocking": false` and
becomes a ticket where the repository floor permits it.

Round 2 rewrites the same receipt, sets `rounds` to 2, and adds
`"status": "CLOSED" | "OPEN"` to every round-1 Blocking finding. Round-1 entries are never removed.
`frozenFindingIds` is the exact ordered list of round-1 Blocking IDs, is written in round 1 (an
empty array for a clean round 1), and is never changed in round 2. Readiness rejects a round-2
receipt when that list is absent, empty, duplicated, or no longer represented by Blocking entries.
Every newly admitted round-2 blocker is appended with `status: "OPEN"`. Set `verdict` to `CLEAN`
only when no frozen or admitted Blocking finding remains OPEN. Capture `reviewedHeadOid` and
`baseSha` from the PR state reviewed; a review of any other head/base is stale by construction.

### Posting

Post the report as a **PR comment**, then hand to Thomas:

```bash
gh pr comment {N} --repo {OWNER/REPO} --body-file <scratchpad>/review-{N}.md
```

Never `--approve` and never `--request-changes`. `required_approving_review_count` is **0** on both
`main` branches, GitHub forbids a PR author approving their own PR, and no review status check
exists in either repository, so a GitHub review state gates nothing and reads misleadingly. The
verdict lives in `findings.json` and the comment body.

The comment body is: the verdict line (plus `DEGRADED: same-vendor review` when applicable), the
Blocking findings with file and line, the follow-up tickets filed, and the dimensions marked N/A
with why. **A machine never merges.**

---

## The risk this design accepts

A frozen finding list can bury a defect the fixer introduced in round 2. Three nets catch it, and
**none of them is the reviewer**:

1. The mechanical carve-out on the round-2 diff line set (rule 5).
2. The 18 required CI checks, which run on the fixer's commit independently of any reviewer verdict.
3. Thomas reads the PR with exact advisory file/line counts and all required generated artifacts
   attached to their source change.

This is a deliberate trade: a review that stops and hands a human a bounded review beats one that
is still finding true defects on hour 38.
