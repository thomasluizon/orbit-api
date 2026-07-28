<!--
Mirrored (body verbatim) from orbit-ui-mobile/.claude/skills/_shared/verification-protocol.md,
which is the source of truth. This API copy serves /pr-review only. The UI audit-family rows
below do not run here and stay verbatim so the shared reliability contract cannot drift.
-->
# Orbit review/audit verification protocol

**At a glance:** the shared reliability layer for every review and audit skill in this
repo: `/pr-review`, `/audit-security`, `/audit-tests`, `/audit-performance`,
`/audit-code-quality`, and `/prod-readiness`. Where `pr-review/rubric.md` defines *what*
these skills look for, this file defines *how* they stay trustworthy: nothing in scope is
silently skipped, no serious finding ships without surviving a challenge, and every run
states what it did **not** do.

One file, every skill reads it, so the reliability bar can never drift between them (the
same no-drift principle as the shared rubric). Each consuming skill names, in its own
pipeline, a **Verify** phase and a **Deferred ledger** section that apply the mechanisms
below, and declares which it runs: a bounded diff needs less than a repo-wide sweep (see
**Calibration**).

---

## 0. What a run produces (D10), and what it may look at (D11)

Two locked decisions bound every mechanism below. Read them first; they decide what goes
into the inventory in §1 and where the ledger in §4 is delivered.

**D10, the output is never a report file.** A report is a photograph that starts lying the
day after it is written. The four `/audit-*` skills and `/prod-readiness` turn verified
findings into **Linear tickets behind one human approval gate**, via the shared pipeline in
`.claude/skills/_shared/audit-to-tickets.md`. `/pr-review` delivers its findings against the
diff, in session. None of the six persists a findings document, and none writes to
`.claude/audits/`. Coverage, the Deferred ledger, and the convergence state are **provenance
presented at the point of decision**, not an artifact left on disk.

**D11, only what no gate can check.** A finding a gate already fails on is noise, not
signal. The mechanical layer therefore never enters the §1 inventory and never becomes a
ticket. The single list of those gates is
`.claude/skills/_shared/gate-owned-exclusions.md`. Where a concern is half-mechanical (a
gate proves presence, judgement proves correctness), keep only the judgement half and name
the gate that owns the other.

## 1. Coverage contract, inventory first, then nothing falls through

Before judging anything, enumerate the full scope as a concrete list: every
file / endpoint / surface / test-area / hot-zone the skill is responsible for. That list
is **binding**. At the end, every item is either **(a) covered with a verdict** or **(b)
in the Deferred ledger with a one-line reason**. There is no third "silently never looked
at" bucket, and that bucket is the failure this whole protocol exists to kill.

- Build the inventory from the skill's own scope phase (the diff's changed files, the
  repo's source globs, the attack surfaces, the critical paths), minus everything §0's D11
  boundary hands to a gate. A gate-owned concern is not a deferred item; it is out of the
  denominator entirely.
- **Rank it worst-first** so the highest-blast-radius items are examined even if the run
  is cut short: by tier (security), critical-path (tests), scaling impact (performance),
  blast-radius x churn (code-quality), or touched-surface severity (pr-review). A hot,
  frequently-edited file outranks a stable leaf carrying the same smell.
- If the scope is too large for one pass, that is a Deferred-ledger entry naming exactly
  what was left and why, never an unstated gap.

## 2. Adversarial verification, every serious finding survives a challenge

A finding that reaches the approval gate has **survived an independent attempt to refute
it.** False positives are the fastest way to get an audit ignored; this is the mechanism
that kills them. A refuted finding never becomes a ticket, which is why this pass runs
before the gate, not after.

- For each **Critical / High** finding (**Tier 1 / Tier 2** for security), spawn an
  independent skeptic subagent whose only job is to **disprove** it: read the cited
  `file:line` in full context and argue why the code is actually correct, the path is
  unreachable, the input already validated, the finding a duplicate, the severity
  inflated.
- The skeptic **defaults to refuted when uncertain.** The burden is on the finding to
  prove it is real, not on the skeptic to prove it isn't.
- A finding the skeptic refutes with evidence is **dropped** (or downgraded, with the
  skeptic's note attached). A finding that survives ships with confidence.
- Run skeptics concurrently, **3 at a time by default** (a token and rate-limit budget
  these skills set for themselves, not a cap enforced anywhere; queue the rest).
  **Diverse lenses beat repetition:** where a finding can fail more than one way, give
  skeptics distinct angles, is it reachable? is the input trusted? is it already handled
  elsewhere? is the severity right?
- Medium / Low / Info findings don't each need a skeptic, but the honesty bar (§5) still
  applies, don't pad.

## 3. Loop-until-dry + completeness critic, repo-wide audits only

A single pass misses the tail. For an unbounded scope, the skill does not stop after one
sweep, it asks what it missed and goes again until a round comes back empty.

- After the first finding pass, run a **completeness critic**: a fresh agent asked *"what
  did this audit NOT examine, a surface never swept, a file skipped, a pattern only
  half-searched, a claim left unverified?"* Its output is the next round's work-list.
- Spawn a focused finder round against each gap the critic names. Repeat until a round
  surfaces **nothing new**, or a stated bound is hit (e.g. 2 consecutive dry rounds), and
  **log the bound** so a cap never reads as completeness.
- A critic that dies (rate-limit, API error) makes completeness **UNKNOWN**; it is never a
  dry round and never a clean pass.
- Gated to **repo-wide** scopes. A bounded diff (`/pr-review`) is its own completeness
  boundary: one completeness pass over the changed surface is enough, do not loop.

## 4. Deferred ledger, say what you did NOT do

Every run carries an explicit **Deferred** ledger, presented with the findings at the point
of decision: the approval gate for the audits and `/prod-readiness`, the review itself for
`/pr-review`. Every item that was in scope but given no verdict, skipped for size,
unverifiable in CI, out-of-scope by tier, past the adversarial-verify cap, is listed there
with a one-line reason. **Silence reads as coverage**, so "not examined" is stated, never
disguised as "clean."

- If the run bounded itself (top-N, sampled, no sibling repo in CI, capped findings), the
  ledger names exactly what that dropped.
- A Tier-3 / enterprise-only control acknowledged-and-skipped is one line here, not a
  finding each.
- The ledger is shown, never saved. It is provenance for the human approving the tickets
  (§0, D10), and it dies with the session.

## 5. Honesty clause

Coverage you did not achieve is never implied. Severity you cannot justify is downgraded.
A finding with no concrete `file:line` + reproduction does not reach the gate. A clean area
earns a plain "None," not an invented nit. A run that found nothing in scope creates
nothing: "clean, no judgement-level findings; the mechanical layer is gate-owned" is a
complete and successful result. These bind every mechanism above, and a skill that
manufactures findings to look thorough has failed this protocol, not passed it.

---

## Calibration, which mechanisms each skill runs

| Skill | Coverage contract | Adversarial verify | Loop-until-dry | Deferred ledger |
|---|---|---|---|---|
| `/pr-review` (bounded diff) | changed files, ranked by surface severity | each Critical/High before posting | one completeness pass, no loop | N/A dimensions + out-of-diff defers |
| `/audit-security` (repo-wide) | attack surfaces, by tier | each Tier 1/2 finding | yes, until dry | Tier 3 + unswept surfaces |
| `/audit-tests` (repo-wide) | critical paths, criticality-first | each Critical/High gap | yes, until dry | non-critical paths + policy-excluded suites |
| `/audit-performance` (repo-wide) | hot zones, by scaling impact | each High finding | yes, until dry | enterprise-only tuning |
| `/audit-code-quality` (repo-wide) | code slices, blast-radius x churn | each Critical/High finding | yes, until dry | dimensions deferred to other audits |
| `/prod-readiness` (orchestrator) | the four audits + ops checks | inherits each child audit's verify | inherits each child's loop | merges every child ledger |

---

## Self-application

This protocol is held to its own bar. A skill that claims to run it must actually emit the
**Verify** phase and the **Deferred ledger** in its output, otherwise it is not running it,
it is only saying so. An audit that skips its own coverage contract is the exact failure
this file exists to prevent.
