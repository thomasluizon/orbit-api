# Roadmap: Orbit

## Overview

Orbit's backend solidification milestone transforms the existing MVP into a full habit tracking platform. Starting with infrastructure modernization (migrations, validation, library upgrades, task removal), then extending the domain model with sub-habits, negative habits, tags, and user timezone, and finishing with progress metrics and AI enhancement. Three phases, each delivering a coherent capability that unblocks the next.

## Phases

**Phase Numbering:**
- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

Decimal phases appear between their surrounding integers in numeric order.

- [x] **Phase 1: Infrastructure Foundation** - Modernize schema management, validation, deprecated libraries, and remove task code
- [x] **Phase 2: Habit Domain Extensions** - Sub-habits, negative habits, tags, notes, and user timezone
- [x] **Phase 3: Metrics and AI Enhancement** - Progress metrics and expanded AI capabilities

## Phase Details

### Phase 1: Infrastructure Foundation
**Goal**: The codebase is modernized with proper schema management, input validation, updated libraries, and task management removed
**Depends on**: Nothing (first phase)
**Requirements**: INFRA-01, INFRA-02, INFRA-03, INFRA-04, CLEAN-01
**Success Criteria** (what must be TRUE):
  1. Database schema changes are managed through EF Core migrations (EnsureCreated is gone)
  2. API requests with invalid input return structured validation errors before reaching command/query handlers
  3. API documentation is browsable via Scalar UI at /scalar/v1
  4. JWT authentication works with the updated Microsoft.IdentityModel.JsonWebTokens library
  5. All task management code (entities, commands, queries, controllers, tests) is removed from the codebase
**Plans:** 3 plans

Plans:
- [x] 01-01-PLAN.md -- Migrations baseline + OpenAPI/Scalar swap + JWT upgrade (Wave 1)
- [x] 01-02-PLAN.md -- FluentValidation pipeline + validators (Wave 2)
- [x] 01-03-PLAN.md -- Task removal across all layers + RemoveTaskItems migration (Wave 2)

### Phase 2: Habit Domain Extensions
**Goal**: Users can manage sub-habits, negative habits, tags, notes, and their timezone -- the complete habit model
**Depends on**: Phase 1 (migrations must exist before schema changes)
**Requirements**: HABIT-01, HABIT-02, HABIT-03, HABIT-04, HABIT-05, TAG-01, TAG-02, TAG-03, PROF-01
**Success Criteria** (what must be TRUE):
  1. User can create a parent habit with child sub-habits and log each sub-habit individually
  2. User can create a negative habit and see "days since last slip" after logging slip-ups
  3. User can add a text note when logging any habit
  4. User can create tags with name and color, assign them to habits, and filter habits by tag
  5. User can set their timezone for correct date-based calculations
**Plans:** 3 plans

Plans:
- [x] 02-01-PLAN.md -- Domain entities, DbContext config, repository includes, and migration (Wave 1)
- [x] 02-02-PLAN.md -- Habit extensions: sub-habits, negative habits, notes, AI prompt update (Wave 2)
- [x] 02-03-PLAN.md -- Tags CRUD + assignment + filtering, profile/timezone (Wave 2)

### Phase 3: Metrics and AI Enhancement
**Goal**: Users can see progress metrics for their habits and the AI handles expanded capabilities correctly
**Depends on**: Phase 2 (metrics need domain model, AI needs new features to expose)
**Requirements**: METR-01, METR-02, METR-03, AI-01, AI-02, AI-03
**Success Criteria** (what must be TRUE):
  1. User can see current streak and longest streak for any habit (frequency-aware, timezone-aware)
  2. User can see weekly and monthly completion rates for any habit
  3. User can see progress trends over time for quantifiable habits
  4. AI gracefully refuses out-of-scope requests with helpful messages instead of attempting invalid actions
  5. AI can create sub-habits and suggest/assign tags when creating habits via chat
**Plans:** 2 plans

Plans:
- [x] 03-01-PLAN.md -- Habit metrics: frequency-aware streaks, completion rates, and quantifiable trends (Wave 1)
- [x] 03-02-PLAN.md -- AI enhancement: sub-habit creation, tag suggestion/assignment, refined refusal (Wave 1)

## Progress

**Execution Order:**
Phases execute in numeric order: 1 -> 2 -> 3

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Infrastructure Foundation | 3/3 | ✓ Complete | 2026-02-08 |
| 2. Habit Domain Extensions | 3/3 | ✓ Complete | 2026-02-08 |
| 3. Metrics and AI Enhancement | 2/2 | ✓ Complete | 2026-02-08 |
