# Phase 4: Multi-Action Foundation - Context

**Gathered:** 2026-02-09
**Status:** Ready for planning

<domain>
## Phase Boundary

AI can execute multiple actions per prompt with safe partial failure handling. Includes batch operations, per-action error handling, habit breakdown with confirmation, and structured status responses. New bulk API endpoints for frontend multi-select scenarios.

</domain>

<decisions>
## Implementation Decisions

### Batch response format
- Conversational + structured: AI returns natural language summary ("Created 3 habits!") plus a structured actions array with per-action status
- Actions array uses status + ID only (no full entity data) — frontend refetches to get full data
- Each action has a typed `type` field (create, log, delete, update, suggest_breakdown) for type-specific frontend rendering
- Response shape stays flat: `{ message, actions[] }` — same as current, just multiple items in the array

### Confirmation flow design
- Habit breakdown is stateless: AI returns suggested parent + sub-habits as data in the response, user sends back edits via a separate endpoint
- User can edit names/details of suggested sub-habits before confirming creation
- Nothing is created until user confirms — both parent and selected sub-habits are created together after confirmation
- No expiry on suggestions — stateless data, user acts whenever

### Bulk endpoints
- General-purpose `POST /api/habits/bulk` that accepts an array of habits with parent-child relationships — used for confirmations and any other bulk scenario
- `DELETE /api/habits/bulk` that accepts an array of habit IDs for batch deletion — supports frontend multi-select
- Both are standalone endpoints, not confirmation-specific

### Error & partial failure behavior
- Keep successes policy: successful actions are committed, failed actions reported with errors, no rollback
- Bulk endpoints follow the same partial success policy — consistent everywhere
- Specific field-level errors per failed action (e.g., `{ action: 'create', habitName: 'reading', error: 'Name already exists', field: 'name' }`)
- AI conversational message acknowledges failures naturally (e.g., "Created exercise and meditation, but reading already exists")

### AI prompt & parsing strategy
- Same AiActionPlan structure, Actions becomes a list with multiple items — minimal architecture change
- `suggest_breakdown` is a distinct action type separate from `create` — carries proposed parent + sub-habits
- Fully mixed action types in a single response — any combination of creates, logs, deletes, and breakdowns
- Continue using JSON schema approach (not Gemini function calling) — proven reliable, format under our control

### Claude's Discretion
- Exact JSON schema for the expanded AiActionPlan
- How suggest_breakdown action payload is structured internally
- Validation rules for bulk endpoints
- AI prompt engineering for reliable multi-action JSON output

</decisions>

<specifics>
## Specific Ideas

- Bulk create endpoint should support parent-child relationships in the payload so a breakdown confirmation is just a call to the bulk create
- AI should be able to handle prompts like "create X, log Y, and break down Z" in a single response with mixed action types
- Frontend will use bulk endpoints for multi-select operations independently of the AI chat flow

</specifics>

<deferred>
## Deferred Ideas

- Bulk log endpoint — not needed for Phase 4, can be added later if needed

</deferred>

---

*Phase: 04-multi-action-foundation*
*Context gathered: 2026-02-09*
