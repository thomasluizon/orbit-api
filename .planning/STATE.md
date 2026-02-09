# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-02-09)

**Core value:** Users can track, build, and break habits with flexible scheduling, progress metrics, and AI-assisted management
**Current focus:** Phase 7: Routine Intelligence

## Current Position

Phase: 7 of 7 (Routine Intelligence)
Plan: 2 of 2 complete
Status: Phase complete
Last activity: 2026-02-09 — Completed 07-02-PLAN.md (Routine Intelligence Chat Integration)

Progress: [████████░░] 89% (8 of ~9 v1.1 plans)

## Performance Metrics

**Velocity:**
- Total plans completed: 16 (8 v1.0 + 8 v1.1)
- Average duration: 6min
- Total execution time: 1.6 hours

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01-infrastructure-foundation | 3/3 | 20min | 7min |
| 02-habit-domain-extensions | 3/3 | 16min | 5min |
| 03-metrics-and-ai-enhancement | 2/2 | 10min | 5min |
| 04-multi-action-foundation | 2/2 | 13min | 7min |
| 05-user-learning-system | 2/2 | 16min | 8min |
| 06-image-intelligence | 2/2 | 13min | 7min |
| 07-routine-intelligence | 2/2 | 10min | 5min |

**Recent Trend:**
- Last 5 plans: 10min, 5min, 8min, 5min, 5min
- Trend: Stable

*Updated after each plan completion*

## Accumulated Context

### Decisions

See PROJECT.md Key Decisions table for full history.

Recent decisions affecting v1.1:
- Non-critical routine analysis in chat pipeline — Follows fact extraction pattern, failures don't block chat responses (07-02)
- Conflict warnings post-creation — Informational only, habit creation always succeeds (07-02)
- Dictionary-based conflict warning tracking — Avoids changing Execute method signatures, scales to multi-action operations (07-02)
- Routine patterns passed via AI intent services — Maintains single responsibility, no direct coupling to prompt builder (07-02)
- LLM-first pattern analysis over statistical methods — Gemini handles temporal reasoning natively, produces human-readable patterns without ML.NET complexity (07-01)
- Routine analysis always uses Gemini — Structured JSON output reliability, follows fact extraction pattern (07-01)
- Fallback generic time slot suggestions — UX consistency, users get actionable recommendations even with insufficient data (07-01)
- Routine patterns optional in SystemPromptBuilder — Backward compatible, enables gradual Plan 02 rollout (07-01)
- IFormFile in Domain layer — Pragmatic tradeoff similar to EF Core in Application, needed for IImageValidationService (06-01)
- Multipart form-data over separate endpoints — Single endpoint maintains conversational flow, better UX despite breaking change (06-01)
- Base64 inline_data over File API — Simpler for images <20MB, avoids upload/reference/cleanup overhead (06-01)
- Ollama image support deferred — Logs warning if image provided, continues text-only (Ollama doesn't support vision) (06-01)
- Gemini Vision for multimodal — Native support in Gemini API already integrated (Phase 6)
- Key facts over conversation history — Compact, structured memory avoids token bloat (Phase 5)
- Routine inference from logs — No schema change needed, use existing timestamps (Phase 7)
- Frontend handles audio transcription — Backend receives text only, simpler (out of scope)
- Per-action error handling with ActionResult — Enables detailed feedback for batch operations (04-01)
- SuggestBreakdown as suggestion-only action — Requires explicit user confirmation before creation (04-01)
- Bulk validation at structural level only — Per-item domain validation in handler enables partial success (04-02)
- Fact extraction always uses Gemini — Structured JSON output reliability, even when Ollama is main provider (05-01)
- Fact extraction is non-critical — Failures logged as warning, don't affect chat response (05-01)
- Soft delete for UserFacts — Global query filter, allows cleanup without losing history (05-01)

### Pending Todos

None.

### Blockers/Concerns

- Ollama image support not implemented — Logs warning, continues text-only (Ollama doesn't support vision APIs) (06-01)
- Ollama reliability with expanded AI prompts uncertain — Gemini is highly reliable, may need Gemini-only for image features
- Pre-existing MSB3277 warning in IntegrationTests (cosmetic)
- Phase 7 routine inference requires experimentation for threshold tuning (pattern detection confidence levels)
- Fact deduplication not implemented - same fact can be extracted multiple times (05-01)
- Category validation not enforced - accepts any string, not just "preference", "routine", "context" (05-01)
- Gemini rate limits can cause test failures when running many tests consecutively - integration tests need delays between runs (05-02)

## Session Continuity

Last session: 2026-02-09
Stopped at: Completed 07-02-PLAN.md — Routine intelligence integrated into chat flow with conflict warnings
Resume file: Phase 7 complete. All v1.1 features delivered.

07-02-SUMMARY.md Key Info:
- Commits: 402db03 (task 1), 9e6852c (task 2)
- Key files modified: ProcessUserChatCommand.cs, IAiIntentService.cs, GeminiIntentService.cs, AiIntentService.cs, AiChatIntegrationTests.cs
- Patterns: Non-critical routine analysis, post-creation conflict warnings, dictionary-based warning tracking
- Integration complete: AI receives routine context, conflict warnings on habit creation, 4 new integration tests
- Phase 7 complete: All RTNI requirements (01-04) delivered and tested
