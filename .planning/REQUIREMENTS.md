# Requirements: Orbit

**Defined:** 2026-02-09
**Core Value:** Users can track, build, and break habits with flexible scheduling, progress metrics, and AI-assisted management

## v1.1 Requirements

Requirements for AI Intelligence & Multi-Action milestone. Each maps to roadmap phases.

### Multi-Action AI

- [ ] **MACT-01**: AI can return multiple actions in a single response (e.g., create 3 habits from one prompt)
- [ ] **MACT-02**: Each action executes independently with per-action error handling (partial success supported)
- [ ] **MACT-03**: AI can decompose a user intent into a parent habit with auto-generated sub-habits
- [ ] **MACT-04**: AI can log multiple habits at once from a single user message (e.g., "finished my morning routine")
- [ ] **MACT-05**: Chat response includes per-action success/failure status in structured format

### User Learning

- [ ] **ULRN-01**: AI extracts key facts from chat messages and persists them to database
- [ ] **ULRN-02**: Stored user facts are loaded into AI system prompt for personalized responses
- [ ] **ULRN-03**: User can view all stored facts about themselves via API
- [ ] **ULRN-04**: User can delete individual stored facts via API

### Image Processing

- [ ] **IMGP-01**: Chat endpoint accepts image uploads (multipart/form-data)
- [ ] **IMGP-02**: Images are sent to Gemini Vision multimodal API for analysis
- [ ] **IMGP-03**: AI extracts structured information from images (titles, dates, amounts) and suggests habit creation
- [ ] **IMGP-04**: Image-based suggestions require user confirmation before creating habits

### Routine Intelligence

- [ ] **RTNI-01**: System analyzes habit log timestamps to detect recurring time-of-day patterns
- [ ] **RTNI-02**: System warns when a new habit's scheduling conflicts with detected routine blocks
- [ ] **RTNI-03**: System suggests available time slots for new habits based on routine gaps (triple-choice format)
- [ ] **RTNI-04**: Routine suggestions include confidence scores based on pattern consistency

## Future Requirements

### Advanced Learning

- **ALRN-01**: Semantic search over stored facts using pgvector embeddings
- **ALRN-02**: Fact relevance scoring based on conversation context
- **ALRN-03**: Conversation history storage for session continuity

### Advanced Multimodal

- **AMMD-01**: Audio upload/recording support in chat interface
- **AMMD-02**: AI transcribes audio and separates venting from actionable items
- **AMMD-03**: Generates structured to-do list from audio for user approval

## Out of Scope

| Feature | Reason |
|---------|--------|
| Audio processing in backend | Frontend handles transcription, backend receives text |
| Calendar sync integration | Infer routines from logs instead — no external calendar dependency |
| Auto-execution without confirmation | EU AI Act compliance + user trust — always require confirmation for impactful actions |
| Real-time chat (WebSockets) | REST API is sufficient for v1.1 — defer to frontend milestone |
| Conversation history storage | Key facts are more efficient than full history — defer to v1.2+ |
| pgvector/embeddings | Start with chronological fact retrieval — add semantic search only if needed |

## Traceability

Which phases cover which requirements. Updated during roadmap creation.

| Requirement | Phase | Status |
|-------------|-------|--------|
| MACT-01 | Phase 4 | Pending |
| MACT-02 | Phase 4 | Pending |
| MACT-03 | Phase 4 | Pending |
| MACT-04 | Phase 4 | Pending |
| MACT-05 | Phase 4 | Pending |
| ULRN-01 | Phase 5 | Pending |
| ULRN-02 | Phase 5 | Pending |
| ULRN-03 | Phase 5 | Pending |
| ULRN-04 | Phase 5 | Pending |
| IMGP-01 | Phase 6 | Pending |
| IMGP-02 | Phase 6 | Pending |
| IMGP-03 | Phase 6 | Pending |
| IMGP-04 | Phase 6 | Pending |
| RTNI-01 | Phase 7 | Pending |
| RTNI-02 | Phase 7 | Pending |
| RTNI-03 | Phase 7 | Pending |
| RTNI-04 | Phase 7 | Pending |

**Coverage:**
- v1.1 requirements: 17 total
- Mapped to phases: 17 (100%)
- Unmapped: 0

---
*Requirements defined: 2026-02-09*
*Last updated: 2026-02-09 after roadmap creation*
