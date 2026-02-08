# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-02-07)

**Core value:** Users can track, build, and break habits with flexible scheduling, progress metrics, and AI-assisted management
**Current focus:** Phase 3 - Metrics and AI Enhancement (In progress)

## Current Position

Phase: 3 of 3 (Metrics and AI Enhancement)
Plan: 2 of 3 in current phase
Status: In progress
Last activity: 2026-02-08 -- Completed 03-02-PLAN.md (AI sub-habit/tag capabilities)

Progress: [########.#] 83%

## Performance Metrics

**Velocity:**
- Total plans completed: 8
- Average duration: 5min
- Total execution time: 0.7 hours

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01-infrastructure-foundation | 3/3 | 20min | 7min |
| 02-habit-domain-extensions | 3/3 | 16min | 5min |
| 03-metrics-and-ai-enhancement | 2/3 | 10min | 5min |

**Recent Trend:**
- Last 5 plans: 5min, 7min, 4min, 5min, 5min
- Trend: stable at 5min average

*Updated after each plan completion*

## Accumulated Context

### Decisions

Decisions are logged in PROJECT.md Key Decisions table.
Recent decisions affecting current work:

- Roadmap: EF Core migrations (INFRA-01) gates all schema changes -- must be Phase 1
- Roadmap: Tags use Tag entity + HabitTag join table (not text[] array) for color metadata support
- Roadmap: Compressed research's 5 phases into 3 for quick depth (merged Tags into Phase 2, Metrics+AI into Phase 3)
- 01-01: Used OpenAPI.NET 2.0 API with OpenApiSecuritySchemeReference for .NET 10 compatibility (v1 Reference pattern broken)
- 01-01: SecurityTokenDescriptor.Subject (ClaimsIdentity) for JWT claims -- no deprecation on 8.15.0
- 01-02: Used FluentValidation.DependencyInjectionExtensions (not deprecated FluentValidation.AspNetCore) for DI scanning
- 01-02: ValidationBehavior throws FluentValidation.ValidationException, caught by ValidationExceptionHandler -- clean separation
- 01-02: Validators auto-discovered via AddValidatorsFromAssemblyContaining -- no manual registration for future validators
- 01-03: Habits-only domain -- removed all task management code (entity, enum, commands, queries, controller)
- 01-03: AI prompt explicitly rejects task-like requests with habit redirect suggestions
- 01-03: AiAction record no longer has DueDate, TaskId, or NewStatus properties
- 02-01: SubHabit is a separate entity (not self-referencing Habit) for simplicity
- 02-01: HabitTag does not extend Entity base class -- uses composite key (HabitId, TagId)
- 02-01: Negative boolean habits allow multiple logs per day (slip-up tracking)
- 02-01: User.TimeZone validated via TimeZoneInfo.FindSystemTimeZoneById for cross-platform IANA support
- 02-02: Added Microsoft.EntityFrameworkCore to Application project for Include support -- pragmatic clean architecture tradeoff
- 02-02: Used FindOneTrackedAsync instead of OrbitDbContext injection to keep Application independent of Infrastructure
- 02-02: Added Habit.LogSubHabitCompletions domain method because SubHabitLog.Create is internal to Domain
- 02-03: Tag assignment via EF navigation property manipulation (habit.Tags.Add/Remove) instead of OrbitDbContext injection
- 02-03: Tag filtering via repository predicate with h.Tags.Any() -- EF translates to SQL without DbContext
- 02-03: Comma-separated GUID query parameter pattern for filtering (GET /api/habits?tags=id1,id2)
- 03-02: AI can create sub-habits inline (SubHabits field on CreateHabit) OR add to existing habits (CreateSubHabit action)
- 03-02: Tag suggestions informational only in aiMessage - tag assignment requires existing tag IDs
- 03-02: Invalid tag IDs during AssignTag silently skipped to allow partial success

### Pending Todos

None.

### Blockers/Concerns

- EnsureCreated-to-migrations transition COMPLETED successfully (baseline migration applied, __EFMigrationsHistory populated)
- Ollama reliability with expanded AI prompts is uncertain -- may need Gemini-only for some features
- Pre-existing MSB3277 warning in IntegrationTests (EF Core Relational version conflict) -- cosmetic, not blocking

## Session Continuity

Last session: 2026-02-08
Stopped at: Completed plan 03-02 (AI sub-habit/tag capabilities) -- Phase 3 in progress
Resume file: .planning/phases/03-metrics-and-ai-enhancement/03-03-PLAN.md (next plan ready)
