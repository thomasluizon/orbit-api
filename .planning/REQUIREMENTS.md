# Requirements: Orbit

**Defined:** 2026-02-07
**Core Value:** Users can track, build, and break habits with flexible scheduling, progress metrics, and AI-assisted management

## v1 Requirements

Requirements for this milestone. Each maps to roadmap phases.

### Infrastructure

- [ ] **INFRA-01**: Database schema managed via EF Core migrations (replace EnsureCreated)
- [ ] **INFRA-02**: Request validation via FluentValidation + MediatR pipeline behavior
- [ ] **INFRA-03**: API documentation via Scalar + Microsoft.AspNetCore.OpenApi (replace Swashbuckle)
- [ ] **INFRA-04**: JWT token handling via Microsoft.IdentityModel.JsonWebTokens (replace legacy library)

### Habits

- [ ] **HABIT-01**: User can create a parent habit with child sub-habits as a checklist
- [ ] **HABIT-02**: User can log individual sub-habits within a parent habit
- [ ] **HABIT-03**: User can create a negative/bad habit that tracks slip-ups
- [ ] **HABIT-04**: User can see "days since last slip" for negative habits
- [ ] **HABIT-05**: User can add optional text notes when logging a habit

### Tags

- [ ] **TAG-01**: User can create tags with a name and color
- [ ] **TAG-02**: User can assign tags to habits
- [ ] **TAG-03**: User can filter habits by one or more tags

### Metrics

- [ ] **METR-01**: User can see current streak and longest streak for any habit (frequency-aware)
- [ ] **METR-02**: User can see weekly and monthly completion rate for any habit
- [ ] **METR-03**: User can see progress trends over time for quantifiable habits

### Profile

- [ ] **PROF-01**: User can set their timezone for correct streak/metric calculation

### AI

- [ ] **AI-01**: AI gracefully refuses out-of-scope requests instead of attempting invalid actions
- [ ] **AI-02**: AI can create sub-habits via chat (e.g., "create morning routine with meditate, journal, stretch")
- [ ] **AI-03**: AI suggests and assigns tags when creating habits via chat

### Cleanup

- [ ] **CLEAN-01**: All task management code removed (entities, commands, queries, controllers, tests)

## v2 Requirements

Deferred to future milestone. Tracked but not in current roadmap.

### Profiles

- **PROF-02**: User can view and edit their display name
- **PROF-03**: User can view and edit their email address

### Notifications

- **NOTF-01**: User receives reminders for habits due today
- **NOTF-02**: User can configure notification preferences

### Auth

- **AUTH-01**: User can verify email after registration
- **AUTH-02**: User can reset password via email link

### Advanced AI

- **AI-04**: AI provides insights based on progress metrics and trends
- **AI-05**: AI suggests habit adjustments based on completion patterns

## Out of Scope

Explicitly excluded. Documented to prevent scope creep.

| Feature | Reason |
|---------|--------|
| Frontend/UI | Backend must be solid first -- separate milestone |
| Mobile app | Web API only for now |
| Social features (sharing, challenges) | Adds complexity without core value |
| Voice input | Deferred to post-frontend milestone |
| Habit templates (predefined) | Using user-defined tags instead |
| Email notifications | Deferred -- no email service in stack yet |
| Real-time features (WebSockets) | Not needed for API-only backend |
| Habit categories (predefined) | Tags are more flexible -- user-defined |

## Traceability

Which phases cover which requirements. Updated during roadmap creation.

| Requirement | Phase | Status |
|-------------|-------|--------|
| INFRA-01 | Phase 1 | Pending |
| INFRA-02 | Phase 1 | Pending |
| INFRA-03 | Phase 1 | Pending |
| INFRA-04 | Phase 1 | Pending |
| CLEAN-01 | Phase 1 | Pending |
| HABIT-01 | Phase 2 | Pending |
| HABIT-02 | Phase 2 | Pending |
| HABIT-03 | Phase 2 | Pending |
| HABIT-04 | Phase 2 | Pending |
| HABIT-05 | Phase 2 | Pending |
| TAG-01 | Phase 2 | Pending |
| TAG-02 | Phase 2 | Pending |
| TAG-03 | Phase 2 | Pending |
| PROF-01 | Phase 2 | Pending |
| METR-01 | Phase 3 | Pending |
| METR-02 | Phase 3 | Pending |
| METR-03 | Phase 3 | Pending |
| AI-01 | Phase 3 | Pending |
| AI-02 | Phase 3 | Pending |
| AI-03 | Phase 3 | Pending |

**Coverage:**
- v1 requirements: 20 total
- Mapped to phases: 20
- Unmapped: 0

---
*Requirements defined: 2026-02-07*
*Last updated: 2026-02-07 after roadmap creation*
