# AI text grounding

`ProcessUserChatCommandHandler` checks the final assistant message after removing card
directives and checks follow-up text separately. Each unsupported claim produces a warning
with `ViolationKind`, `Claim`, `Location`, `UserId`, `CorrelationId`, and `Enforced`.
Successful tool results from all rounds supply the fact set. Failed calls, tool arguments,
prompt indexes, and prior turns do not supply facts.

`ChatResponse.GroundingFacts` exposes normalized facts and their tool name and call ID to
in-process evaluation callers. It is excluded from JSON, so the mobile contract is unchanged.
`GroundednessCheckerTests` call `TurnFactSet.FromToolResults` and `GroundednessChecker.Check` directly.

## Mode switch

The `AppConfigs` key `AstraGroundingEnforce` defaults to `false` when absent. This is log-only
mode. To enable enforcement, run this against the API's Render PostgreSQL database:

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

The checker compares numbers as decimal values and dates as whole dates. It matches entity
names without case and accents, at word boundaries. This is lexical screening, not semantic
entailment: a number can match the wrong metric, and an invented name without a marker can
pass. Quoted non-entity text can warn. Keep log-only mode while you evaluate conversations.
