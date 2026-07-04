---
description: Cross-checks packages/shared/src/types/* and packages/shared/src/api/endpoints.ts against orbit-api DTOs and Controllers for drift. Auto-invoke during /pr-review when both repos have staged changes, or when the user asks to verify the API contract.
mode: subagent
---

Read `.claude/agents/contract-aligner.md` and follow it verbatim — that file is the single source of truth for this agent's behavior, inputs, and output format.
