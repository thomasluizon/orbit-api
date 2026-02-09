# Roadmap: Orbit

## Milestones

- ✅ **v1.0 Backend Solidification** - Phases 1-3 (shipped 2026-02-08)
- 🚧 **v1.1 AI Intelligence & Multi-Action** - Phases 4-7 (in progress)

## Phases

<details>
<summary>✅ v1.0 Backend Solidification (Phases 1-3) - SHIPPED 2026-02-08</summary>

- [x] Phase 1: Infrastructure Foundation (3/3 plans) - completed 2026-02-08
- [x] Phase 2: Habit Domain Extensions (3/3 plans) - completed 2026-02-08
- [x] Phase 3: Metrics and AI Enhancement (2/2 plans) - completed 2026-02-08

See: `.planning/milestones/v1.0-ROADMAP.md` for full details.

</details>

### 🚧 v1.1 AI Intelligence & Multi-Action (In Progress)

**Milestone Goal:** Make the AI smarter - multi-action output, image understanding, user learning, routine inference, and interactive confirmation flows.

#### Phase 4: Multi-Action Foundation
**Goal**: AI can execute multiple actions per prompt with safe partial failure handling
**Depends on**: Nothing (first v1.1 phase)
**Requirements**: MACT-01, MACT-02, MACT-03, MACT-04, MACT-05
**Success Criteria** (what must be TRUE):
  1. User can request multiple habit creations in one prompt (e.g., "create habits for exercise, meditation, reading") and all are created
  2. User can log multiple habits at once (e.g., "I exercised and meditated") and both logs are recorded
  3. User can request habit breakdown and AI returns parent habit with suggested sub-habits in structured format requiring confirmation
  4. When one action in a batch fails, other actions still succeed and user receives detailed per-action status
  5. Chat response shows success/failure for each action with clear error messages when applicable
**Plans:** 2 plans

Plans:
- [x] 04-01-PLAN.md — Multi-action chat pipeline (domain models, handler refactor, prompt update, chat tests)
- [x] 04-02-PLAN.md — Bulk endpoints (POST/DELETE /api/habits/bulk with partial success, endpoint tests)

#### Phase 5: User Learning System
**Goal**: AI learns and personalizes based on user facts extracted from conversations
**Depends on**: Phase 4
**Requirements**: ULRN-01, ULRN-02, ULRN-03, ULRN-04
**Success Criteria** (what must be TRUE):
  1. When user shares personal information in chat (preferences, routines, context), AI extracts and persists key facts to database
  2. Stored facts automatically load into AI system prompt for personalized responses in subsequent conversations
  3. User can retrieve all stored facts about themselves via API endpoint
  4. User can delete individual facts they no longer want AI to remember
**Plans:** 2 plans

Plans:
- [x] 05-01-PLAN.md — UserFact entity, extraction service, and chat pipeline integration (ULRN-01, ULRN-02)
- [x] 05-02-PLAN.md — UserFacts API endpoints and integration tests (ULRN-03, ULRN-04)

#### Phase 6: Image Intelligence
**Goal**: AI can analyze uploaded images and suggest habit creation from visual content
**Depends on**: Phase 4 (needs confirmation flow)
**Requirements**: IMGP-01, IMGP-02, IMGP-03, IMGP-04
**Success Criteria** (what must be TRUE):
  1. User can upload image (photo of schedule, bill, todo list) to chat endpoint via multipart form data
  2. Image is processed by Gemini Vision API and AI can describe visual content
  3. AI extracts structured data from images (habit titles, dates, frequencies) and suggests habit creation
  4. Image-based habit suggestions require explicit user confirmation before creating any habits
**Plans:** 2 plans

Plans:
- [x] 06-01-PLAN.md — Image upload infrastructure (validation, multipart binding, Gemini Vision integration)
- [x] 06-02-PLAN.md — Image-aware AI prompting and integration tests

#### Phase 7: Routine Intelligence
**Goal**: AI detects user logging patterns and suggests optimal scheduling with conflict detection
**Depends on**: Phase 5 (can store patterns as facts)
**Requirements**: RTNI-01, RTNI-02, RTNI-03, RTNI-04
**Success Criteria** (what must be TRUE):
  1. System analyzes existing habit log timestamps and detects recurring patterns (e.g., "user logs exercise Mon/Wed/Fri at 7am")
  2. When user creates new habit with conflicting schedule, system warns about detected time conflicts
  3. AI suggests available time slots for new habits based on routine gaps in triple-choice format
  4. Routine suggestions include confidence scores showing pattern consistency (e.g., "70% confidence - logged 7 of last 10 Mondays")
**Plans**: TBD

Plans:
- [ ] 07-01: TBD
- [ ] 07-02: TBD

## Progress

**Execution Order:**
Phases execute in numeric order: 4 → 5 → 6 → 7

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 1. Infrastructure Foundation | v1.0 | 3/3 | ✓ Complete | 2026-02-08 |
| 2. Habit Domain Extensions | v1.0 | 3/3 | ✓ Complete | 2026-02-08 |
| 3. Metrics and AI Enhancement | v1.0 | 2/2 | ✓ Complete | 2026-02-08 |
| 4. Multi-Action Foundation | v1.1 | 2/2 | ✓ Complete | 2026-02-09 |
| 5. User Learning System | v1.1 | 2/2 | ✓ Complete | 2026-02-09 |
| 6. Image Intelligence | v1.1 | 2/2 | ✓ Complete | 2026-02-09 |
| 7. Routine Intelligence | v1.1 | 0/TBD | Not started | - |
