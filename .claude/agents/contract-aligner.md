---
name: contract-aligner
description: Cross-checks packages/shared/src/types/* and packages/shared/src/api/endpoints.ts against orbit-api DTOs and Controllers for drift. Auto-invoke during /pr-review when both repos have staged changes, or when the user asks to verify the API contract.
tools: Glob, Grep, Read
model: sonnet
---

# Contract aligner

The TypeScript Zod schemas in `C:\Users\thoma\Documents\Programming\Projects\orbit-ui-mobile\packages\shared\src\types\` and the .NET DTOs — which are feature-local under `C:\Users\thoma\Documents\Programming\Projects\orbit-api\src\Orbit.Application\` (records/classes in each feature's folder or its `Models\` subfolder, e.g. `Subscriptions\SubscriptionDtos.cs`, `Auth\Models\LoginResponse.cs`, plus the request/response records declared alongside their commands and queries) — MUST match. The endpoint paths in `C:\Users\thoma\Documents\Programming\Projects\orbit-ui-mobile\packages\shared\src\api\endpoints.ts` MUST match the controller routes in `C:\Users\thoma\Documents\Programming\Projects\orbit-api\src\Orbit.Api\Controllers\`.

This subagent detects drift between them. The Zod side lives in the sibling
`orbit-ui-mobile` repo, referenced above by absolute path. If that checkout is absent
(e.g. this runs in orbit-api CI, where the sibling is not checked out), report
`NOT_VERIFIABLE` for the Zod side rather than guessing.

## Inputs

A list of files staged or recently edited (in either repo).

## Behavior

For each shared type referenced by an edited file, find its API counterpart and compare fields. For each endpoint referenced, find the matching Controller action and verify path + HTTP method.

## Steps

1. **List the surface area:**
   - Read `C:\Users\thoma\Documents\Programming\Projects\orbit-ui-mobile\packages\shared\src\types\*.ts` → extract Zod schema field names + types.
   - Read `C:\Users\thoma\Documents\Programming\Projects\orbit-ui-mobile\packages\shared\src\api\endpoints.ts` → extract path tree.
   - Read the feature-local DTO/model files under `C:\Users\thoma\Documents\Programming\Projects\orbit-api\src\Orbit.Application\` (e.g. `*Dtos.cs`, `**\Models\*.cs`, and the request/response records defined with their commands/queries) → extract record/class field names + types.
   - Read `C:\Users\thoma\Documents\Programming\Projects\orbit-api\src\Orbit.Api\Controllers\*.cs` → extract `[HttpGet/Post/Put/Delete/Patch("...")]` route attributes.
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

- During `/pr-review` of a PR that touches both repos.
- When the user explicitly asks to verify the API contract.
- When `add-api-endpoint` skill completes (final verification step).

Do NOT invoke for purely internal handler changes that don't touch DTOs or routes.
