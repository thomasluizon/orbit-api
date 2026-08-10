---
name: pr-review
description: Capped two-round review of ONE PR diff, by a session that did not write the code. The contract and rubric are single-sourced in orbit-ui-mobile; this skill points there. Use when asked to review a PR from an orbit-api checkout.
argument-hint: <ui#N | api#N | pr-url>
---

# PR Review (pointer)

**Input**: $ARGUMENTS

The review contract is **single-sourced in orbit-ui-mobile**. This repository deliberately carries
no copy of the contract or the rubric: a second committed copy is a drift surface, and a drift
gate over two copies once stood down every review in both repositories over wording differences.

Read and follow, in this order:

1. `C:\Users\thoma\Documents\Programming\Projects\orbit-ui-mobile\.claude\skills\pr-review\SKILL.md`
2. `C:\Users\thoma\Documents\Programming\Projects\orbit-ui-mobile\.claude\skills\pr-review\rubric.md`

Both are read from that checkout's files for an interactive run. An orchestrated review never
reads this file at all: `tools/launch-worker.mjs --review` hands the reviewer a complete review
order whose rubric snapshot is materialized from orbit-ui-mobile `origin/main`.

Everything in the canonical SKILL.md applies unchanged, including the repository floor for
orbit-api (P0/P1 only, from AGENTS.md), the two-round cap, and the posting rules. A machine never
merges.
