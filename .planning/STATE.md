# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-02-09)

**Core value:** Users can track, build, and break habits with flexible scheduling, progress metrics, and AI-assisted management
**Current focus:** Phase 7: Routine Intelligence

## Current Position

Phase: 6 of 7 (Image Intelligence)
Plan: 2 of 2 complete
Status: Phase complete
Last activity: 2026-02-09 — Completed 06-02-PLAN.md (Image-Aware AI Prompting)

Progress: [██████░░░░] 67% (6 of ~9 v1.1 plans)

## Performance Metrics

**Velocity:**
- Total plans completed: 14 (8 v1.0 + 6 v1.1)
- Average duration: 6min
- Total execution time: 1.5 hours

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01-infrastructure-foundation | 3/3 | 20min | 7min |
| 02-habit-domain-extensions | 3/3 | 16min | 5min |
| 03-metrics-and-ai-enhancement | 2/2 | 10min | 5min |
| 04-multi-action-foundation | 2/2 | 13min | 7min |
| 05-user-learning-system | 2/2 | 16min | 8min |
| 06-image-intelligence | 2/2 | 13min | 7min |

**Recent Trend:**
- Last 5 plans: 5min, 6min, 10min, 5min, 8min
- Trend: Stable

*Updated after each plan completion*

## Accumulated Context

### Decisions

See PROJECT.md Key Decisions table for full history.

Recent decisions affecting v1.1:
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
Stopped at: Completed 06-02-PLAN.md — Image-aware AI prompting with SuggestBreakdown enforcement and multipart test coverage
Resume file: Phase 6 complete, ready for Phase 7 (Routine Intelligence)

06-02-SUMMARY.md Key Info:
- Commits: 657c8b1 (task 1), 60230c0 (task 2)
- Key files modified: SystemPromptBuilder.cs, GeminiIntentService.cs, AiChatIntegrationTests.cs
- Patterns: Conditional prompt injection (hasImage flag), multipart form testing
- Image analysis instructions mandate SuggestBreakdown (never auto-create from images)
- All 14 existing tests updated to multipart format, 2 new image tests added
- Phase 6 Image Intelligence complete: upload → validate → analyze → suggest → confirm
