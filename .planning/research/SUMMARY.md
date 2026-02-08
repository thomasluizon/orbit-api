# Research Summary: Orbit Milestone 2 -- Backend Solidification

**Domain:** AI-powered habit tracking backend extension
**Researched:** 2026-02-07
**Overall confidence:** HIGH

## Executive Summary

Orbit's existing .NET 10.0 / EF Core 10 / PostgreSQL stack is well-suited for the planned features. The milestone requires no new frameworks or major architectural changes -- it is an extension of established patterns. The most impactful changes are infrastructural (migrating from `EnsureCreated()` to proper EF Core migrations, replacing deprecated Swashbuckle with Microsoft.AspNetCore.OpenApi + Scalar, and upgrading the JWT library from the legacy System.IdentityModel.Tokens.Jwt to Microsoft.IdentityModel.JsonWebTokens).

The feature set (sub-habits, bad habits, tags, progress metrics, user profiles) maps cleanly to the existing Clean Architecture + CQRS pattern. Sub-habits use a standard self-referencing FK on the Habit entity. Bad habits require a single boolean property (`IsNegative`) with inverted metric logic. Tags use PostgreSQL `text[]` array columns (reusing the existing `Days` pattern). Progress metrics are pure domain service computation over HabitLog data.

Two new libraries are recommended: FluentValidation 12.1.1 for input validation via MediatR pipeline behaviors (the de facto standard for .NET CQRS validation), and the Scalar.AspNetCore + Microsoft.AspNetCore.OpenApi combination replacing the deprecated Swashbuckle. Both are well-established with verified .NET 10 compatibility.

The single highest-risk element is the `EnsureCreated()` to migrations transition. This MUST be the first step because every subsequent feature adds columns or tables that require migrations. The transition requires creating a baseline migration with empty `Up()`/`Down()` methods, inserting the baseline into `__EFMigrationsHistory`, and replacing `EnsureCreatedAsync()` with `MigrateAsync()`. Getting this wrong can cause schema duplication or data loss.

## Key Findings

**Stack:** Extend existing .NET 10.0 / EF Core 10 / PostgreSQL stack. Add FluentValidation 12.1.1 for validation. Replace Swashbuckle with Microsoft.AspNetCore.OpenApi 10.0.2 + Scalar.AspNetCore 2.12.36. Replace legacy System.IdentityModel.Tokens.Jwt with Microsoft.IdentityModel.JsonWebTokens 8.15.0. No other new packages needed.

**Architecture:** Self-referencing Habit entity for sub-habits. `IsNegative` boolean for bad habits. PostgreSQL `text[]` for tags (or Tag entity + HabitTag join table for richer metadata). `IHabitProgressService` domain service for metrics computation. Named query filters (EF Core 10 feature) for per-user + soft-delete filtering.

**Critical pitfall:** The `EnsureCreated()` to migrations transition must be done FIRST and correctly. An incorrect baseline migration will cascade failures into every subsequent feature.

## Implications for Roadmap

Based on research, suggested phase structure:

1. **Infrastructure Foundation** - Migrations, validation pipeline, deprecated package replacements
   - Addresses: EF Core migrations setup, FluentValidation + MediatR pipeline, Swashbuckle to Scalar migration, JWT library upgrade
   - Avoids: EnsureCreated-to-migrations transition failure (Pitfall 1), validation gaps in new commands

2. **Domain Model Extensions** - Add new entity properties and entities
   - Addresses: `IsNegative` on Habit, `ParentHabitId` on Habit (sub-habits), User profile (timezone), HabitLog notes
   - Avoids: Migration ordering conflicts by doing all Habit table changes in one migration batch

3. **Tags System** - Tag entity, HabitTag join, CRUD endpoints
   - Addresses: Tag creation/management, habit-tag association, filtering by tag
   - Avoids: Polymorphic association anti-pattern (Pitfall 6), unnecessary complexity from array column for metadata-rich tags

4. **Progress Metrics** - Domain service for streaks, completion rates, metrics API
   - Addresses: `IHabitProgressService`, streak algorithm (frequency-aware), negative habit inverted streaks, metrics query endpoints
   - Avoids: Naive daily-only streak calculation (Pitfall 3), timezone-unaware date boundaries (Pitfall 5)

5. **AI Integration Enhancement** - Extend prompts with new capabilities
   - Addresses: Sub-habit creation via AI, negative habit awareness, tag suggestions, metrics-aware coaching
   - Avoids: AI prompt bloat (Pitfall 4) by being the last phase and adding incrementally

**Phase ordering rationale:**
- Migrations (Phase 1) gates everything -- no schema changes possible without it
- Domain model extensions (Phase 2) are prerequisite for features that depend on them
- Tags (Phase 3) is independent and low-risk, good for momentum after infrastructure work
- Metrics (Phase 4) depends on `IsNegative` and user timezone from Phase 2
- AI integration (Phase 5) touches the broadest surface area and benefits from all other features being stable

**Research flags for phases:**
- Phase 1: Standard patterns, unlikely to need additional research. Well-documented migration and FluentValidation patterns.
- Phase 4 (Metrics): May need deeper research on frequency-aware streak algorithms when implementation begins. The algorithm must handle daily, weekly, monthly, yearly frequencies plus day-specific schedules.
- Phase 5 (AI): Likely needs deeper research on optimal prompt engineering for expanded action types. The Gemini and Ollama providers may need different prompt strategies.

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | HIGH | All package versions verified on NuGet. Compatibility confirmed. Deprecation statuses verified against official Microsoft announcements. |
| Features | HIGH | Feature landscape validated against competitor apps. Data model patterns verified against EF Core documentation. |
| Architecture | HIGH | Patterns verified against official EF Core docs (self-referencing, many-to-many, migrations). Existing codebase analyzed directly. |
| Pitfalls | HIGH | Most critical pitfalls verified against official Microsoft documentation. AI prompt pitfalls based on established LLM behavior research. |

## Gaps to Address

- **Streak algorithm specifics:** The exact frequency-aware streak algorithm needs detailed design when Phase 4 implementation begins. Research provides the approach but not production-ready pseudocode.
- **Ollama reliability with expanded prompts:** The phi3.5:3.8b model's ability to handle additional action types is uncertain. May need to accept Gemini-only support for some features or test alternative Ollama models.
- **MediatR license key registration:** The Community license registration process needs to be followed. No technical blocker, but an administrative step.
- **Tag data model decision:** The ARCHITECTURE.md recommends a Tag entity + HabitTag join table for referential integrity and metadata. The STACK.md notes PostgreSQL `text[]` as a simpler alternative. The roadmap should make a definitive decision based on whether tag color/metadata is needed at the time of implementation.
