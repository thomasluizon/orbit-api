---
name: contract-aligner
description: Cross-checks packages/shared/src/types/* and packages/shared/src/api/endpoints.ts against orbit-api DTOs and Controllers for drift. Manual only. Nothing invokes it automatically. Invoke it by name when you want to verify the API contract, or let /audit-code-quality use it as a contract lens.
tools: Glob, Grep, Read
model: sonnet
effort: medium
---

<!-- Manual only. Pullfrog cannot invoke local agents. Resolve paths from each repository root and report NOT_VERIFIABLE when the frontend checkout is absent. -->

# Contract aligner

The TypeScript Zod schemas in `orbit-ui-mobile/packages/shared/src/types/` and the feature-local DTOs in `orbit-api/src/Orbit.Application/` MUST match. The endpoint paths in `orbit-ui-mobile/packages/shared/src/api/endpoints.ts` MUST match the routes in `orbit-api/src/Orbit.Api/Controllers/`. Resolve each path from its repository root.

This subagent detects drift between them. The Zod side lives in the sibling
`orbit-ui-mobile` repo. If that checkout is absent
(a clone or worktree with no sibling repository beside it), report
`NOT_VERIFIABLE` for the Zod side rather than guessing.

## Inputs

A list of files staged or recently edited (in either repo).

## Behavior

For each shared type referenced by an edited file, find its API counterpart and compare fields. For each endpoint referenced, find the matching Controller action and verify path + HTTP method.

## Steps

1. **List the surface area:**
   - Read `packages/shared/src/types/*.ts` in `orbit-ui-mobile` and extract Zod schema field names and types.
   - Read `packages/shared/src/api/endpoints.ts` in `orbit-ui-mobile` and extract the path tree.
   - Read feature-local DTO/model files under `src/Orbit.Application/` in `orbit-api`, including `*Dtos.cs`, `**/Models/*.cs`, and records defined with commands or queries.
   - Read `src/Orbit.Api/Controllers/*.cs` in `orbit-api` and extract HTTP route attributes.
2. **For each Zod schema with a matching name in DTOs:**
   - Field names: do they match? Casing convention (PascalCase C# vs camelCase TS) is expected — System.Text.Json default camelCases.
   - Field types: `z.string()` ↔ `string`, `z.number()` ↔ `int`/`double`/`decimal`, `z.boolean()` ↔ `bool`, `z.array(T)` ↔ `List<T>` or `T[]`, `z.nullable()` ↔ nullable.
   - Required vs optional: `z.string().optional()` ↔ `string?` in C# (with `JsonIgnoreCondition.WhenWritingNull` or nullable).
3. **For each endpoint in `endpoints.ts`:**
   - Does a Controller action match the path + method?
   - Does the action's request DTO match the Zod request schema?
   - Does the action's return type's DTO match the Zod response schema?
4. **Report drift:**
   - MISSING_DTO: Zod schema has no C# counterpart.
   - MISSING_ZOD: C# DTO has no Zod counterpart (less critical — it might be internal).
   - FIELD_DRIFT: shapes don't match.
   - PATH_DRIFT: endpoint path or method doesn't match Controller.

## Output format

```
Contract alignment:
- API.habits.list (GET /api/habits) → HabitsController.GetHabits — MATCH
- HabitSchema (10 fields) ↔ HabitDto (10 fields) — MATCH
- HabitMetricsSchema (5 fields) ↔ HabitMetricsDto (6 fields) — FIELD_DRIFT
    - TS missing: streakBucket (int)
    - Add z.number() to HabitMetricsSchema.streakBucket and run npm test in packages/shared

PASS: 0 critical drifts.
```

Or:

```
Contract alignment: FAIL
- API.habits.freeze (POST /api/habits/{id}/freeze) → no matching Controller action — PATH_DRIFT
- HabitSchema ↔ HabitDto:
    - TS has `archivedAt: string | null`; C# has `archived_at: DateTime?` — naming convention drift (frontend expects camelCase from JSON, but C# property is snake_case via JsonPropertyName attribute — verify the attribute matches)
- Drift count: 2 — fix before merging cross-repo PRs.
```

## When invoked

Manual only. Nothing invokes this agent automatically.

- When the user names this agent, or asks to verify the API contract.
- When `/audit-code-quality` fans out and uses it as a contract lens.

Do NOT invoke for purely internal handler changes that don't touch DTOs or routes.
