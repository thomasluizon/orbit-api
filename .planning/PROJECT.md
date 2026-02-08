# Orbit

## What This Is

Orbit is an AI-powered habit tracking application. Users manage their habits through a visual interface with optional AI chat as a convenience layer for quick actions. Built with .NET 10.0, PostgreSQL, and multi-provider AI (Gemini/Ollama), it's a backend API that will eventually power a frontend client.

## Core Value

Users can track, build, and break habits with flexible scheduling, progress metrics, and AI-assisted management — the habit tracking must work reliably whether the user interacts manually or through chat.

## Requirements

### Validated

- User registration and login with JWT authentication — existing
- AI chat processing with multi-provider support (Gemini/Ollama) — existing
- Basic habit CRUD (create, list, log, delete) — existing
- Flexible frequency system (FrequencyUnit + FrequencyQuantity + optional days) — existing
- Boolean and quantifiable habit types — existing
- Integration test suite (15+ scenarios) — existing
- Clean Architecture with CQRS via MediatR — existing

### Active

- [ ] Remove task management entirely — Orbit is a habit tracker, not a life management suite
- [ ] Sub-habits — parent habit with child habits as checklist (e.g., "Morning routine" with meditate, journal, stretch)
- [ ] Bad habits — negative tracking where users log slip-ups, goal is zero logs, track "days since last slip"
- [ ] User-defined tags for habit organization
- [ ] Progress metrics — trends over time, improvement tracking for quantifiable habits
- [ ] User profiles — view/edit name, email, preferences
- [ ] EF Core migrations — replace EnsureCreated() with proper migration-based schema management
- [ ] Improved AI prompt — fix out-of-scope failures where AI tries to execute actions for things it can't do

### Out of Scope

- Frontend/UI — backend must be solid first, frontend is a future milestone
- Mobile app — web API only for now
- Notifications/reminders — deferred to post-MVP
- Email verification — not needed for MVP
- Password reset — deferred to post-MVP
- Social features (sharing, challenges) — deferred
- Voice input — deferred
- Habit templates/categories (predefined) — using user-defined tags instead

## Context

- Existing MVP backend is functional but incomplete — tasks need removal, habits need depth features
- AI chat is a feature, not the core interface — users will primarily interact visually with habits
- Gemini is the primary AI provider (~95% reliable, 1.6s), Ollama is fallback (~65% reliable, 30s)
- Current schema uses EnsureCreated() which won't survive the schema changes needed for sub-habits, tags, and bad habits
- Codebase is well-structured with Clean Architecture but has known concerns (see .planning/codebase/CONCERNS.md)

## Constraints

- **Tech stack**: .NET 10.0, C# 13, PostgreSQL, Clean Architecture with CQRS — established, not changing
- **AI providers**: Gemini (cloud, primary) and Ollama (local, fallback) — both must be supported
- **Backend only**: No frontend work in this milestone — API endpoints and business logic only
- **Schema changes**: Must migrate to EF Core migrations before any domain model changes

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Remove tasks entirely | Orbit is a habit tracker — tasks dilute focus and add complexity | — Pending |
| Sub-habits as parent-child | Users want "Morning routine" with sub-habits, not just flat list | — Pending |
| Bad habits via slip-up logging | Track when user DOES the bad thing, goal is zero — simpler than daily check-ins | — Pending |
| Tags over categories | User-defined tags are more flexible than predefined categories | — Pending |
| EF Core migrations before features | Schema changes for sub-habits/tags/bad-habits require proper migration support | — Pending |

---
*Last updated: 2026-02-07 after initialization*
