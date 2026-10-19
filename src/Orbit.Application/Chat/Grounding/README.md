# Astra text grounding

`ProcessUserChatCommandHandler` checks the final assistant message after removing card
directives and checks follow-up text separately. Each unsupported claim produces a warning
with `ViolationKind`, `Claim`, `Location`, `UserId`, `CorrelationId`, and `Enforced`.
Successful tool results from all rounds supply the fact set. Failed calls, tool arguments,
prompt indexes, and prior turns do not supply facts.

`ChatResponse.GroundingFacts` exposes normalized facts and their tool name and call ID to
in-process evaluation callers. It is excluded from JSON, so the mobile contract is unchanged.
The harness can also call `TurnFactSet.FromToolResults` and `GroundednessChecker.Check`.

## Mode switch

The `AppConfigs` key `AstraGroundingEnforce` defaults to `false` when absent. This is log-only
mode. To enable enforcement, run this in the Supabase SQL Editor for the API database:

```sql
INSERT INTO "AppConfigs" ("Key", "Value")
VALUES ('AstraGroundingEnforce', 'true')
ON CONFLICT ("Key") DO UPDATE SET "Value" = EXCLUDED."Value";

SELECT "Key", "Value" FROM "AppConfigs" WHERE "Key" = 'AstraGroundingEnforce';
```

Read back the exact value. `AppConfigService` caches each key for 30 minutes per process,
including the absent-key default. No redeploy is needed; warm processes can switch at
different times within that window. An enforced violation carries `Enforced=true` in logs.
Set the same row to `false` to return to log-only mode, allowing the same cache window.

Enforcement suppresses a message containing any violation and its follow-ups. A violation
only in follow-ups suppresses those follow-ups. Actions, pending confirmations, and server
cards remain available. Streaming enforcement holds model text until the check finishes;
tool progress events remain available during execution. Log-only mode preserves streaming.
Cached FAQ text must pass against an empty current-turn fact set before reuse.

## Deterministic coverage

Numbers are compared as decimal values, with pt-BR formatting selected by the user's language.
Full ISO, Brazilian numeric, and English or Portuguese prose dates are compared as whole
dates, preventing individual date components from authorizing a different date. List markers
and GUIDs are excluded. Typed payload numbers use invariant JSON formatting. Prose tool
outputs use the invariant formatting emitted by the habit and goal query tools.

Entity matching ignores case and accents, and uses word boundaries. It scans names loaded
for the turn plus quoted names and explicit single-quoted habit or goal references. Successful
entity targets, payload name/title fields, and quoted names in prose tool outputs ground names.

This is lexical screening, not semantic entailment. It cannot establish that a returned number
belongs to the asserted metric, detect every unmarked invented name, or recognize arbitrary
numbers written as words or translated names. Quoted non-entity text and ambiguous entity
titles can produce warnings. Keep log-only mode while evaluating recorded conversations.
The unit suite includes clean conversational examples and output from the real habit tool;
the repository supplies no recorded production conversation corpus. The evaluation harness
owns that corpus and its scenarios.
