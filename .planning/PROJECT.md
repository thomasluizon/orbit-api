# Orbit

## What This Is

Orbit is an AI-powered habit tracking backend API. Users manage habits with flexible scheduling (daily, weekly, monthly, yearly, specific days), sub-habit checklists, negative habit tracking, tags, and progress metrics. An AI chat layer enables quick actions via natural language with multi-provider support (Gemini/Ollama), multi-action batch processing, image understanding, user learning from conversations, and routine pattern detection with scheduling intelligence. Built with .NET 10.0, PostgreSQL, and Clean Architecture with CQRS.

## Core Value

Users can track, build, and break habits with flexible scheduling, progress metrics, and AI-assisted management — the habit tracking must work reliably whether the user interacts manually or through chat.

## Requirements

### Validated

- ✓ User registration and login with JWT authentication — v1.0
- ✓ AI chat processing with multi-provider support (Gemini/Ollama) — v1.0
- ✓ Basic habit CRUD (create, list, log, delete) — v1.0
- ✓ Flexible frequency system (FrequencyUnit + FrequencyQuantity + optional days) — v1.0
- ✓ Boolean and quantifiable habit types — v1.0
- ✓ Integration test suite (15+ scenarios) — v1.0
- ✓ Clean Architecture with CQRS via MediatR — v1.0
- ✓ EF Core migrations for schema management — v1.0
- ✓ Request validation via FluentValidation pipeline — v1.0
- ✓ API documentation via Scalar UI — v1.0
- ✓ Sub-habits as parent-child checklists — v1.0
- ✓ Negative habit tracking with slip-up logging — v1.0
- ✓ User-defined tags with color for habit organization — v1.0
- ✓ Frequency-aware streaks, completion rates, and trend analysis — v1.0
- ✓ AI sub-habit creation and tag suggestion via chat — v1.0
- ✓ AI graceful refusal of out-of-scope requests — v1.0
- ✓ User timezone for correct date-based calculations — v1.0
- ✓ Task management code fully removed — v1.0
- ✓ Multi-action AI output (array of actions per prompt) — v1.1
- ✓ Smart habit breakdown with sub-habit generation and confirmation flow — v1.1
- ✓ Image processing in chat via Gemini Vision (multimodal) — v1.1
- ✓ AI user learning system (extract and store key facts, load into context) — v1.1
- ✓ Routine inference from log timestamps with conflict detection — v1.1
- ✓ Structured suggestion responses (triple-choice) for frontend rendering — v1.1

### Active

(None — next milestone requirements TBD via `/gsd:new-milestone`)

### Out of Scope

- Frontend/UI — backend must be solid first, frontend is a future milestone
- Mobile app — web API only for now
- Notifications/reminders — no email service in stack yet
- Email verification — deferred
- Password reset — deferred
- Social features (sharing, challenges) — deferred
- Voice input — frontend handles transcription, backend receives text
- Audio processing — frontend responsibility, backend receives transcribed text
- Habit templates/categories (predefined) — using user-defined tags instead
- Offline mode — real-time API is the architecture
- Ollama vision/image support — Ollama doesn't support multimodal APIs, Gemini-only for images
- Conversation history storage — key facts are more efficient than full history
- pgvector/embeddings — start with chronological fact retrieval, add semantic search only if needed

## Context

Shipped v1.0 (2026-02-08) and v1.1 (2026-02-09) with 9,928 LOC C# across ~100+ files.
Tech stack: .NET 10.0, PostgreSQL (Npgsql 10.0.0), MediatR 14.0.0, FluentValidation, Gemini 2.5 Flash / Ollama phi3.5:3.8b.
35 v1.1 requirements delivered across 7 phases. All phases verified.

Current capabilities:
- REST API with JWT auth, habit CRUD, sub-habits, tags, metrics
- AI chat with multi-action support, image understanding, user learning, routine intelligence
- Multi-provider AI (Gemini primary, Ollama fallback)
- Multipart form-data chat endpoint with image upload
- 20+ integration tests covering all features

Known concerns:
- Ollama reliability with expanded AI prompts uncertain (Gemini is highly reliable)
- Ollama doesn't support multimodal/vision — Gemini-only for image features
- Microsoft.EntityFrameworkCore in Application project (pragmatic clean architecture tradeoff)
- IFormFile in Domain project (pragmatic tradeoff for image validation interface)
- Pre-existing MSB3277 warning in IntegrationTests (cosmetic)
- Fact deduplication not implemented (same fact can be extracted multiple times)

## Constraints

- **Tech stack**: .NET 10.0, C# 13, PostgreSQL, Clean Architecture with CQRS — established, not changing
- **AI providers**: Gemini (cloud, primary) and Ollama (local, fallback) — both must be supported
- **Backend only**: No frontend work in current scope — API endpoints and business logic only

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Remove tasks entirely | Orbit is a habit tracker — tasks dilute focus | ✓ Good — cleaner domain model |
| Sub-habits as separate entity | Simpler than self-referencing Habit for checklist use case | ✓ Good — clean separation |
| Negative habits via slip-up logging | Track when user DOES the bad thing, goal is zero | ✓ Good — inverted streak logic works well |
| Tags over categories | User-defined tags are more flexible than predefined categories | ✓ Good — HabitTag join table with color |
| EF Core migrations before features | Schema changes require proper migration support | ✓ Good — enabled all Phase 2/3 work |
| HabitTag plain class (no Entity base) | Composite key doesn't need Guid Id | ✓ Good — simpler |
| FindOneTrackedAsync pattern | Keeps Application independent of Infrastructure | ✓ Good — clean architecture preserved |
| Microsoft.EntityFrameworkCore in Application | Needed for Include support in queries | ⚠️ Revisit — pragmatic but breaks strict layering |
| Tag suggestions informational only | AI suggests names in aiMessage, not auto-create | ✓ Good — user controls tag creation |
| Real-time metrics (no cache) | Calculate on-demand from logs with indexes | ✓ Good — simpler, accurate, fast enough |
| ActionResult with per-action error handling | Enables detailed batch operation feedback | ✓ Good — partial success for multi-action |
| SuggestBreakdown as suggestion-only action | User must confirm before creation | ✓ Good — safe interactive flow |
| Bulk endpoints with partial success policy | Keep successes even when some items fail | ✓ Good — consistent with chat pipeline |
| Fact extraction always uses Gemini | Structured JSON reliability regardless of AiProvider | ✓ Good — consistent behavior |
| Fact extraction is non-critical | Failures don't block chat, graceful degradation | ✓ Good — robust user experience |
| Soft delete for UserFacts | Global query filter, keeps history | ✓ Good — clean with EF Core |
| IFormFile in Domain layer | Needed for IImageValidationService interface | ⚠️ Revisit — pragmatic tradeoff |
| Multipart form-data for chat | Single endpoint for text + image, better UX | ✓ Good — maintains conversational flow |
| Base64 inline_data for Gemini Vision | Simpler than File API for images <20MB | ✓ Good — no upload/cleanup overhead |
| Gemini Vision for multimodal | Native support in Gemini API | ✓ Good — works well with existing setup |
| Key facts over conversation history | Compact, structured memory avoids token bloat | ✓ Good — efficient personalization |
| Routine inference from logs (no schema change) | Existing timestamps sufficient for pattern detection | ✓ Good — zero migration needed |
| LLM-first pattern analysis | Gemini handles temporal reasoning natively | ✓ Good — human-readable patterns without complex algorithms |
| Non-critical routine analysis | Failures don't block chat, graceful degradation | ✓ Good — consistent with fact extraction pattern |
| Conflict warnings are informational | Habit creation always succeeds, warning is FYI | ✓ Good — non-blocking UX |

---
*Last updated: 2026-02-09 after v1.1 milestone*
