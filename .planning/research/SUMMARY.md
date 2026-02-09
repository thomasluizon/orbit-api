# Project Research Summary

**Project:** Orbit v1.1 - AI Intelligence Enhancements
**Domain:** AI-powered habit tracking API (existing .NET 10/PostgreSQL system)
**Researched:** 2026-02-09
**Confidence:** MEDIUM-HIGH

## Executive Summary

Orbit v1.1 adds four AI intelligence capabilities to an existing CQRS/.NET 10 habit tracker: multi-action AI responses, Gemini Vision image processing, user fact storage (AI memory), and routine inference from log patterns. The existing architecture is remarkably well-positioned—most features require no new dependencies or only standard library enhancements. Only routine inference requires adding Microsoft.ML.TimeSeries, a stable Microsoft package.

The recommended approach is a phased rollout in dependency order: (1) multi-action execution with partial failure handling, (2) user fact storage (simple, no embeddings initially), (3) Gemini Vision integration, and (4) routine inference. This order minimizes risk while delivering visible value early. The critical risks are transaction atomicity across multi-action plans, Gemini's structured output validation failures, and context window exhaustion when combining user facts with images. All three have proven mitigation strategies from 2026 AI engineering practice.

Key insight: This is table-stakes evolution, not greenfield innovation. Multi-action responses and structured suggestions are baseline expectations for conversational AI in 2026. User learning and routine inference are the differentiators—no habit tracking app currently does timezone-aware pattern detection without calendar integration. The architecture supports these features with minimal refactoring, making this a high-value, low-friction enhancement set.

## Key Findings

### Recommended Stack

The existing stack supports all v1.1 features with one optional addition. No core technology changes needed—.NET 10, PostgreSQL, Npgsql 10.0.0, and HttpClient already handle multimodal AI, JSON storage, and time-series data.

**Core technologies (no changes):**
- .NET 10 + C# 13: Runtime and language — supports all new capabilities, removed Uri length limits for base64 images
- PostgreSQL + Npgsql 10.0.0: Database — JSONB perfect for fact storage, proven at scale
- System.Text.Json: Serialization — built-in .NET 10, handles all deserialization needs
- HttpClient: Gemini API communication — works for Vision API with inline_data parts

**New dependency (Phase 4 only):**
- Microsoft.ML.TimeSeries 5.0.0: Pattern detection for routine inference — SSA (Singular Spectrum Analysis) for detecting user logging patterns and forecasting optimal habit schedules

**Explicitly NOT adding:**
- SixLabors.ImageSharp: Licensing complexity, unnecessary (Gemini accepts base64 directly)
- JSON schema libraries: .NET 10 has schema export, FluentValidation handles validation
- NodaTime: .NET's TimeZoneInfo already supports IANA IDs (proven in User entity)
- RestSharp/Flurl/Refit: HttpClient + System.Net.Http.Json sufficient

**Confidence:** HIGH. Existing stack handles 75% of features. ML.NET.TimeSeries is official Microsoft package with stable 5.0 release.

### Expected Features

Research reveals a three-tier feature landscape: table stakes (multi-action, structured responses), strong differentiators (user learning, routine inference), and emerging capabilities (image processing). The 2026 AI assistant landscape has converged on memory-enabled, context-aware systems with batch operations.

**Must have (table stakes):**
- Multi-action AI responses: Users expect "create 3 habits" to work in one command (BatchIt MCP, modern AI workflows)
- Structured suggestion responses: Jotform/Emergent standard—clickable options over open prompts
- Confirmation flow for complex operations: EU AI Act 2026 compliance, Microsoft Copilot Studio pattern
- Batch logging: "I exercised and meditated" should log both in one interaction

**Should have (competitive differentiators):**
- AI user learning (fact extraction): ChatGPT memory, Claude Projects, NotebookLM Personal Intelligence—hyper-personalization is the top 2026 trend
- Routine inference from logs: Reclaim.ai does this with calendar; Orbit can do it from log timestamps alone (greenfield)
- Smart habit breakdown: AFFiNE/ClickUp Brain task decomposition, but with confirmation (AI safety)
- Image-based habit creation: Gemini Vision 95%+ OCR accuracy—photo gym flyer → habits with schedule

**Defer (v1.2+):**
- Automatic action execution (no confirmation): EU AI Act concern, loss of user control
- AI-generated goals/targets: Reduces intrinsic motivation, patronizing
- Full calendar integration: Entire product vertical (Reclaim.ai raised VC for this alone)
- Real-time coaching during activities: Requires wearables, always-on tracking, massive scope

**Confidence:** MEDIUM. Table stakes verified via 2026 AI UX standards (Jotform, Microsoft Copilot). Differentiators based on competitive analysis and emerging patterns. Some habit-specific UX (routine inference thresholds) will need experimentation.

### Architecture Approach

Clean Architecture + CQRS foundation enables AI intelligence features with minimal structural change. The key pattern is extending existing layers (Domain → Application → Infrastructure → Api) rather than introducing new architectural concepts.

**Major components:**

1. **Multi-action executor with partial failure tracking**: Modify `ProcessUserChatCommandHandler` to support per-action commits with `ActionExecutionResult[]` instead of all-or-nothing transactions. Enables safe batch operations with detailed user feedback.

2. **Gemini Vision multimodal integration**: Extend `GeminiIntentService.InterpretAsync()` with optional `GeminiImageData` parameter. Add `inline_data` parts to request JSON. No new service needed—same HTTP API endpoint as text-only.

3. **UserFact entity for semantic memory**: New Domain entity with UserId, Content, Category, Embedding (optional pgvector). Simple chronological retrieval initially (no embeddings), upgrade to semantic search in later phase. EF Core + PostgreSQL JSONB handles storage.

4. **RoutineAnalysisService for pattern detection**: Infrastructure service analyzing HabitLog timestamps (UTC → local time via User.TimeZone). Detects day-of-week and time-of-day patterns using grouping/frequency analysis. Returns `RoutineSuggestion[]` for user approval (never auto-create).

**Anti-patterns to avoid:**
- Single transaction for all actions (current): If SaveChanges fails, all actions lost. Use per-action commits.
- Synchronous embedding generation in request path: Adds 200-500ms latency. Generate async or skip embeddings initially.
- Loading all facts into every AI request: Token waste. Limit to recent/relevant 5-10 facts.
- Running pattern analysis on every request: Expensive (queries 90 days of logs). Use separate endpoint or background job.
- Base64 image encoding: 5x-20x worse performance than multipart. Use IFormFile from start.

**Confidence:** HIGH. Existing Clean Architecture + CQRS accommodates all features. Pattern detection and multimodal API integration have proven implementations in .NET ecosystem.

### Critical Pitfalls

Top 5 pitfalls cluster around transaction boundaries, AI output reliability, and architectural consistency:

1. **Partial failure in multi-action execution**: AI returns 5 actions, action 3 fails, actions 1-2 already committed. User sees inconsistent state. Prevention: Pre-validate all actions before executing any, or use fail-fast with detailed error reporting.

2. **Gemini structured output validation failures**: API returns 200 OK but JSON fails schema validation. Retry doesn't help (LLM non-determinism). Prevention: Detect schema failures via `finishReason`, implement simplified schema fallback, limit context complexity to avoid MAX_TOKENS.

3. **Context window exhaustion with user facts**: 80 habits + 200 facts + image = 47K tokens. Models degrade at 60-70% of advertised limits. Prevention: Tiered context strategy with token budgets per section, fact pruning (archive after 90 days unused), image resizing for large uploads.

4. **Prompt injection via user-generated content**: Habit title includes `IGNORE PREVIOUS INSTRUCTIONS`. AI breaks scope boundaries. Prevention: Sanitize user input before prompt inclusion, use structured delimiters (`--- BEGIN USER DATA ---`), validate AI output doesn't reference cross-user resources.

5. **EF Core navigation property fixup conflicts**: Multiple queries load same User entity via different paths. Change tracker sees conflicting instances: "Instance already tracked." Prevention: AsNoTracking for read-only queries (AI context loading), explicit AsTracking + AddAsync for new entities with Guid.NewGuid() keys.

**Confidence:** HIGH. All pitfalls verified via research (OWASP Top 10 LLM, Microsoft Learn, EF Core docs) or project memory (Guid.NewGuid() trap). Mitigation strategies proven in 2026 AI engineering practice.

## Implications for Roadmap

Based on architecture dependencies and risk mitigation, suggest 4-phase structure:

### Phase 1: Multi-Action Execution with Safety (Foundation)
**Rationale:** Enables all subsequent features. Table stakes for 2026 AI. No external dependencies. Low technical risk.
**Delivers:** AI can execute multiple actions in one turn (create 3 habits, batch log). Partial failure handling with detailed user feedback. Pre-validation prevents inconsistent database state.
**Addresses:** Multi-action responses, confirmation flow, structured suggestions (all P0 table stakes from FEATURES.md)
**Avoids:** Partial failure pitfall, transaction atomicity issues
**Stack impact:** None—refactors existing ChatCommandHandler
**Estimated complexity:** MEDIUM (5-7 days)

### Phase 2: User Fact Storage (Simple Memory)
**Rationale:** High value, low complexity. No embeddings initially (defer to Phase 5). Enables AI personalization immediately.
**Delivers:** AI learns user preferences/routines/context across sessions. Facts stored in PostgreSQL, loaded into prompt context.
**Addresses:** AI user learning (P1 differentiator from FEATURES.md)
**Avoids:** Context window exhaustion (tiered context, limit 30 facts), EF tracking conflicts (AsNoTracking), Guid.NewGuid() trap (explicit AddAsync)
**Stack impact:** Standard EF Core entity + migration, no new dependencies
**Estimated complexity:** MEDIUM (5-7 days)

### Phase 3: Gemini Vision Integration
**Rationale:** Builds on Phase 1 error handling. Moderate complexity. Requires multimodal API changes.
**Delivers:** Users upload images (gym schedule, bill, todo list photo). AI extracts structured data, returns suggestions for user confirmation.
**Addresses:** Image-based habit creation (P2 nice-to-have from FEATURES.md)
**Avoids:** Base64 performance trap (use multipart IFormFile), content-type trust (magic byte validation), context window budget (image token calculation)
**Stack impact:** None—same Gemini API, inline_data JSON parts
**Estimated complexity:** MEDIUM (5-7 days)

### Phase 4: Routine Inference (Pattern Detection)
**Rationale:** Most complex, least critical. Requires ML.NET.TimeSeries. High experimentation risk (thresholds, confidence scoring).
**Delivers:** AI detects user logging patterns (Mon/Wed/Fri at 7am). Suggests recurring habits. Proactive conflict detection ("7am meditation conflicts with 7am exercise").
**Addresses:** Routine inference, context-aware time suggestions (P1 differentiators from FEATURES.md)
**Avoids:** Timezone edge cases (capture local time, handle DST), routine inference without confidence thresholds (require >70% consistency + user approval)
**Stack impact:** +1 dependency (Microsoft.ML.TimeSeries 5.0.0)
**Estimated complexity:** HIGH (10-14 days, includes pattern algorithm experimentation)

### Phase 5 (Optional): Semantic Search Enhancement
**Rationale:** Only improves Phase 2 fact retrieval. Requires pgvector setup, embeddings API. Defer unless fact relevance becomes problem.
**Delivers:** UserFact retrieval by semantic similarity instead of chronological order. Improved context relevance.
**Stack impact:** pgvector PostgreSQL extension, Gemini text-embedding-004 API calls
**Estimated complexity:** HIGH (7-10 days)

### Phase Ordering Rationale

- **Phase 1 first:** Multi-action is foundational. Every feature (batch logging, habit breakdown, fact storage) builds on this. Table stakes for 2026 AI—users will try "create 3 habits" immediately.
- **Phase 2 second:** User facts enable personalization without complex dependencies. Simple version (no embeddings) delivers 80% of value. Informs routine inference (Phase 4 can store patterns as facts).
- **Phase 3 third:** Image processing is independent but builds on Phase 1 confirmation flow. Moderate complexity, clear value (photo → habits). Can be parallelized with Phase 4 if needed.
- **Phase 4 last:** Routine inference is highest complexity (ML.NET, pattern algorithms, timezone handling). Requires sufficient log history (2+ weeks). Can defer to v1.2 if needed without breaking core value prop.
- **Dependency chain:** Phase 1 → (Phase 2 + Phase 3 can parallelize) → Phase 4. Phase 5 is optional enhancement to Phase 2.

### Research Flags

Phases likely needing `/gsd:research-phase` during planning:
- **Phase 4 (Routine Inference):** Pattern detection thresholds (how many logs = pattern?), confidence scoring, timezone complexity. Sparse habit-specific research—will need experimentation.
- **Phase 5 (Semantic Search):** pgvector setup, embedding model selection, similarity search performance tuning.

Phases with standard patterns (skip research-phase):
- **Phase 1 (Multi-Action):** CQRS + MediatR patterns well-documented. Pre-validation and per-action commits are standard transaction strategies.
- **Phase 2 (User Facts - Simple):** Standard EF Core entity + repository. Chronological retrieval (no embeddings) is straightforward SQL.
- **Phase 3 (Gemini Vision):** Official Gemini API docs cover multimodal requests. Multipart uploads are standard ASP.NET Core.

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | HIGH | 75% of features use existing stack. ML.NET.TimeSeries is official Microsoft package with stable 5.0 release. |
| Features | MEDIUM | Table stakes verified via 2026 AI UX research. Differentiators based on competitive analysis. Routine inference thresholds need experimentation. |
| Architecture | HIGH | Clean Architecture + CQRS handles all features. Multimodal API, fact storage, pattern detection have proven .NET implementations. |
| Pitfalls | HIGH | All critical pitfalls verified via official docs (OWASP, Microsoft Learn, EF Core) or project memory. Mitigations tested in 2026 AI systems. |

**Overall confidence:** MEDIUM-HIGH

Phase 1-3 are HIGH confidence (standard patterns, proven tech). Phase 4 is MEDIUM confidence (requires experimentation for habit-specific thresholds and pattern algorithms).

### Gaps to Address

Areas where research was inconclusive or needs validation during implementation:

- **Routine inference minimum data requirements:** Research doesn't specify habit-specific thresholds. Recommendation: Start with 2 weeks (14 days) minimum, require >=70% consistency to flag pattern. A/B test in Phase 4.
- **Fact deduplication algorithm:** Simple text matching vs. semantic similarity via embeddings? Phase 2 uses chronological retrieval (defer deduplication). Phase 5 can add semantic deduplication if fact volume becomes issue.
- **Multi-action transaction boundaries:** Rollback strategy unclear. Phase 1 should start with pre-validation + fail-fast. Saga pattern (compensating actions) only if user testing reveals need for partial success UX.
- **Image hallucination mitigation:** What's acceptable accuracy for habit creation from images? Recommendation: Always require user confirmation, show extracted text for verification. Phase 3 user testing will reveal if additional validation needed.
- **Gemini structured output retry strategy:** How many retries before degraded mode? Research suggests 3 retries with simplified schema fallback. Phase 1 implementation will validate.

## Sources

### Primary (HIGH confidence)
- [Gemini API Official Docs](https://ai.google.dev/gemini-api/docs) — Image understanding, structured output, function calling, rate limits
- [Microsoft Learn: EF Core](https://learn.microsoft.com/en-us/ef/core/) — Transactions, relationship navigations, tracking behavior
- [Microsoft Learn: ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/) — File uploads, multipart forms, rate limiting
- [Microsoft Learn: ML.NET](https://learn.microsoft.com/en-us/dotnet/machine-learning/) — TimeSeries forecasting, SSA pattern detection
- [OWASP LLM Top 10 2025](https://owasp.org/www-project-top-10-for-large-language-model-applications/) — Prompt injection prevention, indirect prompt injection

### Secondary (MEDIUM confidence)
- AI assistant trends (Codiant, Built In, Medium) — 2026 conversational AI standards, memory-enabled assistants
- Memory architectures (Mem0, Zep, AWS Bedrock AgentCore) — Episodic memory patterns, fact extraction
- Chatbot UX research (Jotform, Emergent, Botpress) — Structured suggestions, progressive disclosure, confirmation flows
- Routine detection (Reclaim.ai, Habitify, Pattrn) — Pattern analysis, smart scheduling, conflict detection
- Enterprise AI platforms (Microsoft Copilot Studio, Salesforce Flow) — Multistage approvals, human-in-the-loop

### Tertiary (LOW confidence)
- Habit-specific ML thresholds — No research found. Will need experimentation in Phase 4.
- Gemini Vision accuracy for habit photos — OCR research shows 95%+ for receipts/bills, but habit-specific use cases (gym schedules, todo lists) unverified. Phase 3 user testing will validate.
- .NET 10 Uri length limits removed — Mentioned in .NET 10 networking improvements blog, but specific data URI limits not documented. Assume conservative 20MB inline limit per Gemini docs.

---
*Research completed: 2026-02-09*
*Ready for roadmap: yes*
