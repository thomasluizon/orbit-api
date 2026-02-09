# Milestones: Orbit

## v1.1 AI Intelligence & Multi-Action (Shipped: 2026-02-09)

**Delivered:** AI-powered intelligence layer with multi-action batch operations, user learning from conversations, image understanding via Gemini Vision, and routine pattern detection with scheduling conflict warnings.

**Phases completed:** 4-7 (8 plans total)

**Key accomplishments:**

- Multi-action AI responses with per-action error handling and partial success support for batch habit creation/logging
- User learning system extracting key facts from conversations, persisting to database, and loading into AI context for personalization
- Image intelligence via Gemini Vision multimodal API — upload images of schedules, bills, or todo lists and get habit suggestions
- Routine intelligence detecting time-of-day patterns from habit log timestamps with scheduling conflict warnings and triple-choice time slot suggestions
- Smart habit breakdown (SuggestBreakdown) with interactive confirmation flow — AI proposes sub-habits, user approves before creation
- Bulk create/delete REST endpoints with partial success policy for frontend batch operations

**Stats:**

- 35 files created/modified
- 9,928 lines of C# (total project, +5,164 from v1.0)
- 4 phases, 8 plans, ~16 tasks
- 1 day from start to ship (2026-02-09)

**Git range:** `feat(04-01)` to `test(07-02)`

**What's next:** TBD — frontend client, advanced AI learning, notifications

---

## v1.0 Backend Solidification (Shipped: 2026-02-08)

**Delivered:** Complete habit tracking backend with sub-habits, negative habits, tags, progress metrics, and AI-assisted management via multi-provider chat.

**Phases completed:** 1-3 (8 plans total)

**Key accomplishments:**

- Modernized infrastructure with EF Core migrations, FluentValidation pipeline, Scalar API docs, and JWT upgrade
- Built comprehensive habit domain: sub-habits, negative habits with slip-up tracking, text notes, and user timezone
- Implemented frequency-aware streak calculation supporting Day/Week/Month/Year patterns with timezone awareness
- Created tag system with CRUD, many-to-many assignment, and comma-separated filtering
- Expanded AI to handle sub-habit creation, tag suggestion/assignment, and graceful out-of-scope refusal
- Removed all legacy task management code, focusing Orbit purely on habits

**Stats:**

- 91 files created/modified
- 4,764 lines of C#
- 3 phases, 8 plans, ~20 tasks
- 1 day from start to ship (2026-02-07 to 2026-02-08)

**Git range:** `feat(01-01)` to `feat(03-02)`

**What's next:** TBD — frontend client, advanced AI insights, notifications

---
