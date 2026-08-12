---
name: second-opinion
description: >-
  Get an independent cross-model second opinion (GPT-5.6 Sol via Codex) on a specific, load-bearing technical claim or a Critical or High code-review finding. A different model reads the claim and code, then returns AGREE, DISAGREE, or UNSURE. Use to stress-test a single blocking finding, a risky assertion, or a close call before you commit to it. Nothing fires it automatically: you invoke it deliberately, one claim per call. Not for open-ended research (use /deep-research).
argument-hint: <a claim to test, optionally with a file:line to pull context from>
---

# Second Opinion (cross-model)

**Input**: $ARGUMENTS

Ask **GPT-5.6 Sol** through the local `codex` CLI to independently judge one concrete
claim. Sol is reserved for this ambiguous, difficult, high-value decision: you run the
helper once per claim, where the extra cost is justified by the consequence of getting
the call wrong.

Moving to Sol through Codex intentionally trades guaranteed cross-vendor diversity for
cross-model diversity. The caller and this helper can now share the
Codex/OpenAI family, so their blind spots may correlate more than the previous
cross-vendor pairing. That cost is accepted because Codex is the harness's supported
external model path and Sol is the strongest fit for this narrow judgement. The second
opinion remains an independent prompt and model call, not a consensus vote or a deciding
gate.

## Operating rules

- **Local-only, degrades to a no-op.** `codex` is a local CLI and can be absent
  from CI runners, unauthenticated, offline, timed out, or unable to return a parseable
  response. Every such path returns `UNAVAILABLE`: the skill says so in one line and
  moves on. It never blocks, invents a verdict, or treats "couldn't ask" as disagreement.
- **Never force a decision.** A Sol verdict is input, not a gate. It never auto-merges,
  auto-drops a finding, or overrides your own judgement. It surfaces a second
  view for a human to weigh.
- **One claim per call.** Feed a single, self-contained finding and its code. Sol judges
  only from the text you send it, with no repo access, so include the cited hunk.

## How it runs: the helper

The deterministic helper invokes `codex exec` for the one-shot, non-interactive verdict,
parses its machine-readable response, extracts the verdict, and degrades on any failure:

```bash
node .claude/skills/second-opinion/second-opinion.mjs <<'FINDING'
<the finding dossier: title, severity, repo/path:line, the claimed defect, the cited code/diff hunk>
FINDING
```

`codex exec` is correct here because this is a single unsupervised model call, not a
supervised worker launched through the harness. It does not conflict with the headless
worker ban in `tools/launch-worker.mjs`.

The helper reads the dossier from **stdin** so diffs avoid argument length and quoting
limits. It prompts Sol as an independent skeptic and prints **one line of JSON** to
stdout, always exiting 0:

| Field | Meaning |
|---|---|
| `status` | `OK` (a verdict was obtained) or `UNAVAILABLE` (Codex absent, unauthenticated, timed out, errored, or unparseable) |
| `verdict` | `AGREE`, `DISAGREE`, or `UNSURE` (only when `status: OK`) |
| `confidence` | `high`, `medium`, or `low` |
| `reasoning` | At most 2 sentences citing the specific code |
| `reason` | Why it degraded (only when `status: UNAVAILABLE`) |
| `model` | The slug used |

Options: `--model <slug>` (default `gpt-5.6-sol`; swap only to a live Codex model slug)
and `--timeout <ms>` (default 180000). The timeout is a backstop that yields
`UNAVAILABLE`, never a hang.

## Interpreting the verdict

| Result | What it means | What to do |
|---|---|---|
| `OK` and **AGREE** | An independent model confirms the defect and the severity. | State that the finding is cross-model confirmed. |
| `OK` and **DISAGREE** | Sol argues the code is correct, the severity is inflated, or the claim is unsupported. | Mark the finding **CONTESTED**. Surface both verdicts and let the human decide. Do not silently drop it or force a merge. |
| `OK` and **UNSURE** | The supplied context could not decide it. | Note it. The finding stands as the existing review already ruled. |
| **UNAVAILABLE** | No second opinion was obtained. | Say so in one line with the `reason`. The finding stands unchanged. Never read this as agreement or disagreement. |

## How you invoke it

Nothing fires this skill automatically. You run it yourself, one claim per call:

1. Build the dossier: the claim in one line, plus the relevant hunk when `$ARGUMENTS`
   names a `file:line` or snippet.
2. Run the helper.
3. Report your read of the claim and Sol's verdict side by side. On `DISAGREE`, present
   both cases and recommend how to resolve them. On `UNAVAILABLE`, answer from your own
   analysis and note that the second opinion was not reachable.

A Pullfrog finding is a valid claim to test. Paste the finding and its cited hunk into
the dossier, then weigh Sol's verdict yourself. The verdict never resolves the Pullfrog
thread and never merges the pull request.
