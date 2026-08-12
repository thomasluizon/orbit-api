---
description: "Cross-checks packages/shared/src/types/* and packages/shared/src/api/endpoints.ts against orbit-api DTOs and Controllers for drift. Use only when the user explicitly asks to verify the API contract."
mode: subagent
permission:
  edit: deny
  bash: deny
  task: deny
  webfetch: deny
  websearch: deny
---

Read `.claude/agents/contract-aligner.md` and follow it verbatim — that file is the single source of truth for this agent's behavior, inputs, and output format.
