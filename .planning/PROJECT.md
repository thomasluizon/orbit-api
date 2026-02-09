# Orbit

## What This Is

Orbit is an AI-powered habit tracking backend API. Users manage habits with flexible scheduling (daily, weekly, monthly, yearly, specific days), sub-habit checklists, negative habit tracking, tags, and progress metrics. An AI chat layer enables quick actions via natural language with multi-provider support (Gemini/Ollama). Built with .NET 10.0, PostgreSQL, and Clean Architecture with CQRS.

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

### Active

<!-- Current Milestone: v1.1 AI Intelligence & Multi-Action -->

- [ ] Multi-action AI output (array of actions per prompt)
- [ ] Smart habit breakdown with sub-habit generation and confirmation flow
- [ ] Image processing in chat via Gemini Vision (multimodal)
- [ ] AI user learning system (extract and store key facts, load into context)
- [ ] Routine inference from log timestamps with conflict detection
- [ ] Structured suggestion responses (triple-choice) for frontend rendering

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

## Current Milestone: v1.1 AI Intelligence & Multi-Action

**Goal:** Make the AI smarter — multi-action output, image understanding, user learning, routine inference, and interactive confirmation flows.

**Target features:**
- Multi-action AI responses (create/log multiple habits per prompt)
- Smart habit breakdown with interactive sub-habit confirmation
- Image processing via Gemini Vision multimodal API
- AI user learning (extract key facts from conversations, personalize responses)
- Routine inference from log timestamps with conflict detection and time slot suggestions

## Context

Shipped v1.0 with 4,764 LOC C# across 91 files.
Tech stack: .NET 10.0, PostgreSQL (Npgsql 10.0.0), MediatR 14.0.0, FluentValidation, Gemini 2.5 Flash / Ollama phi3.5:3.8b.
20/20 v1 requirements delivered. All 3 phases verified.

v1.1 focus: AI intelligence layer. Gemini Vision for multimodal, key fact extraction for personalization, routine pattern detection from existing log data. Multi-action support already partially exists (AiActionPlan.Actions is a list) but needs prompt engineering and execution hardening.

Known concerns:
- Ollama reliability with expanded AI prompts uncertain (Gemini is highly reliable)
- Ollama may not support multimodal/vision — Gemini-only for image features
- Microsoft.EntityFrameworkCore in Application project (pragmatic clean architecture tradeoff)
- Pre-existing MSB3277 warning in IntegrationTests (cosmetic)

## Constraints

- **Tech stack**: .NET 10.0, C# 13, PostgreSQL, Clean Architecture with CQRS — established, not changing
- **AI providers**: Gemini (cloud, primary) and Ollama (local, fallback) — both must be supported
- **Backend only**: No frontend work in this milestone — API endpoints and business logic only

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

| Gemini Vision for multimodal | Native support in Gemini API, already integrated as primary provider | — Pending |
| Key facts over conversation history | Compact, efficient, structured — avoids token bloat in system prompt | — Pending |
| Routine inference from logs (no schema change) | Existing log timestamps sufficient — no need for StartTime/EndTime on Habit | — Pending |
| Frontend handles audio transcription | Backend receives text only — simpler, no audio infrastructure needed | — Pending |

---
*Last updated: 2026-02-09 after v1.1 milestone start*
