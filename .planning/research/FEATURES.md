# Feature Research: AI Intelligence Enhancements

**Domain:** AI-Powered Habit Tracking - Intelligence Layer (v1.1)
**Researched:** 2026-02-09
**Confidence:** MEDIUM (Verified via web search, limited Context7/official docs for habit-specific implementations)

## Executive Summary

This research focuses on AI intelligence enhancements for Orbit v1.1: multi-action responses, image processing, user learning, routine inference, and structured suggestions. Research reveals these features fall into three categories: table stakes (multi-action, structured responses), strong differentiators (user learning, routine inference), and emerging capabilities (image processing). The 2026 AI landscape shows convergence toward memory-enabled, context-aware assistants with batch operation support and proactive scheduling intelligence.

## Feature Landscape

### Table Stakes (Users Expect These)

Features users assume exist in modern AI assistants. Missing these = feels outdated.

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| Multi-action AI responses | BatchIt MCP and modern AI workflows support batching multiple operations in single requests. Users expect "create 3 habits" to work in one command, not require 3 separate prompts. This is standard in 2026 conversational AI. | MEDIUM | Modify AI response schema from single action to array of actions. Backend executes array sequentially or in parallel where safe. Frontend displays multi-step progress. Risk: partial failure handling (what if action 2 of 5 fails?). |
| Structured suggestion responses | Jotform, Emergent, and other chatbot UX guides emphasize clickable options over open-ended prompts. Users expect "Option A / Option B / Custom" choices, not pure text responses. Progressive disclosure is 2026 UX standard. | LOW | AI returns response type enum: `text`, `actions`, `suggestions`. Suggestions include 2-3 pre-built options + "custom" escape hatch. Frontend renders as buttons/cards. Already have action types in place, just need suggestion variant. |
| Confirmation flow for complex operations | EU AI Act 2026 requires human-in-the-loop for impactful decisions. Microsoft Copilot Studio, Salesforce Flow, and enterprise AI platforms all implement approval gates. Users expect "create 5 habits?" confirmation before execution, especially for bulk/destructive actions. | MEDIUM | Add `requiresConfirmation: boolean` to action plan response. Frontend shows preview + confirm/cancel UI. Backend executes only after user confirms. For "organize room" -> parent + 5 sub-habits, show full structure before creating. |
| Batch logging | Conversational AI in 2026 supports chaining operations (Azure Logic Apps, AI workflow builders). "I exercised and meditated today" should log both habits in one response. Users expect efficiency. | LOW | Extension of multi-action. AI returns array with multiple `log_habit` actions. Each action targets different habitId. Backend processes sequentially to avoid race conditions on user state. |

### Differentiators (Competitive Advantage)

Features that set Orbit apart from competitors. Not required everywhere, but high value.

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| AI user learning (extracted facts) | Dume.ai, ChatGPT memory, Claude Projects, Notion AI, and NotebookLM's Personal Intelligence (leaked 2026) all extract and store user facts across sessions. "Goes to gym at 7pm Tuesdays" becomes system context. This is hyper-personalization - the #1 AI trend in 2026. Orbit can learn routines, preferences, obstacles. | HIGH | New entity: `UserFact` (UserId, FactText, ExtractedAt, Category?, Confidence?). AI extracts facts via structured output on each chat turn. Facts loaded into system prompt context. Requires: fact extraction prompt engineering, fact deduplication logic, fact expiration/relevance scoring. See Mem0, Zep, AWS Bedrock AgentCore episodic memory for architectures. |
| Routine inference from log timestamps | Reclaim.ai finds best habit times via calendar analysis. Habitify 2026 flags "at-risk" habits via predictive AI. Users expect "you usually exercise at 7am" insights. No major habit app does conflict detection ("7am meditation conflicts with your 7am run"). This is greenfield differentiation. | HIGH | Analyze HabitLog timestamps to detect patterns (day of week, time of day). Store inferred routines as UserFacts or separate `RoutinePattern` entity. AI system prompt includes: "User typically logs Meditation Mon/Wed/Fri at 6:30am." Conflict detection: check if new habit's suggested time overlaps existing routine. Warn user proactively. |
| Smart habit breakdown with confirmation | AI task decomposition is standard in 2026 (AFFiNE AI, Kawaii Tasks, ClickUp Brain). "Organize my room" -> checklist of subtasks is expected. But AUTO-CREATING without confirmation is risky (AI might hallucinate irrelevant steps). Confirmation flow + preview makes this safe and user-controlled. | MEDIUM | AI detects complex/vague habit descriptions. Returns `suggestion` response type with parent habit + array of proposed sub-habits. User confirms/edits before creation. Leverages existing parent-child habit architecture. Requires: complexity detection heuristics, sub-habit generation prompts. |
| Image-based habit creation via Gemini Vision | Gemini 2.5/3 Flash Agentic Vision can extract structured data from images (receipts, flyers, bills, schedules). OCR apps like Expensify, Klippa, Wave achieve 95%+ accuracy in 2026. Users could photo a gym flyer -> habit created with schedule details. Or bill due date -> reminder habit. Novel in habit tracking space. | MEDIUM | Add image upload to chat endpoint. Use Gemini Vision API (multimodal prompt). Extract: text content, dates, schedules, tasks. Return as suggestion with parsed details for user confirmation. Already using Gemini, just add vision capability. Risk: hallucination (AI invents details not in image). Mitigation: always require confirmation. |
| Context-aware time suggestions | Reclaim.ai's core feature: AI finds best time based on existing calendar. For Orbit, use inferred routines instead of calendar. "You usually exercise in mornings, want to schedule meditation for 6:00am or 7:00am?" No habit app does this without calendar integration. | MEDIUM | Requires routine inference (see above). When creating habit with reminder, AI suggests 2-3 time slots based on: existing habit times (avoid conflicts), user's active hours (from log timestamps), category patterns (morning vs evening habits). |

### Anti-Features (Commonly Requested, Often Problematic)

Features that seem good but create problems. Explicitly do NOT build these.

| Feature | Why Requested | Why Problematic | Alternative |
|---------|---------------|-----------------|-------------|
| Automatic action execution without confirmation | "Just do what I ask, don't confirm" - power users want speed | EU AI Act 2026 requires human oversight for impactful actions. Automatic bulk operations risk catastrophic mistakes (delete wrong habits, log wrong dates). Loss of user control = loss of trust. | Confirmation is mandatory for multi-action, destructive, or complex operations. Single, simple actions (log one habit) can be immediate. Power users can batch-confirm, not auto-execute. |
| AI-generated habit goals/targets | "Set my goals for me based on my data" | Goal-setting is deeply personal. AI-imposed goals feel patronizing and reduce intrinsic motivation. Bad gamification trap. Users rebel against externally-set targets. | AI can SUGGEST based on trends ("you've been hitting 5x/week, want to try 6?"), but user must set the target. Suggestion != automatic goal change. |
| Full calendar integration (Google/Outlook sync) | "Put habits in my Google Calendar automatically" | Reclaim.ai raised VC funding for calendar sync alone. Requires OAuth, conflict resolution, timezone hell, bidirectional sync bugs. Entire product vertical, not a feature. | Store reminder times. Let future mobile app do LOCAL calendar integration (one-way write). No server-side bidirectional sync. |
| Real-time AI coaching during activities | "Notify me mid-workout to adjust form" | Requires real-time data feeds (wearables), continuous AI inference, push notification infrastructure, battery drain, privacy invasion (always-on tracking). Massive scope. | Post-activity coaching via chat. User reports "finished workout," AI asks "how did it go?" and provides feedback. |
| Emotion/sentiment analysis on habit notes | "Detect if I'm stressed from my notes" | Crosses into mental health territory. Sentiment analysis on personal notes is privacy-invasive and error-prone. Misclassifying emotions can harm user trust. Regulatory risk (HIPAA-adjacent if users log health feelings). | AI can read notes for context ("you mentioned feeling tired") but NOT classify emotions or make mental health inferences. Leave sentiment implicit. |
| Auto-import habits from photos (no confirmation) | "Scan my todo list photo and create all habits" | Gemini Vision hallucination risk is real. Auto-creating 10 wrong habits from misread text = terrible UX. Users lose trust in AI. Confirmation must be mandatory. | Always return suggestions for user review. Never auto-execute based on image parsing alone. |

## Feature Deep Dives

### Multi-Action AI Response Design

**What users expect in 2026:**
- "Create habits for exercise, meditation, and reading" -> all 3 created in one interaction
- "Log my morning routine" (if routine = 3 habits) -> all 3 logged
- "Delete old habits: X, Y, Z" -> batch deletion with single confirmation
- BatchIt MCP shows batch operations reduce latency and token usage via parallel execution

**Recommended behavior:**
- AI response schema changes from single `AiAction` to `AiActionPlan` with `actions: AiAction[]`
- Backend executes actions sequentially by default (safe), or in parallel if flagged `canParallelize: true`
- Partial failure handling: if action 3 of 5 fails, return success status for 1-2, error for 3, and skip 4-5 OR continue with 4-5
- Frontend shows progress: "Creating habit 1 of 3... Creating habit 2 of 3..."
- Multi-action operations that modify state (create/update/delete) require confirmation (see below)

**Data model:**
```typescript
// OLD (single action):
{
  action: "create_habit",
  parameters: { name: "Exercise" }
}

// NEW (multi-action):
{
  responseType: "actions",
  requiresConfirmation: true,
  actions: [
    { action: "create_habit", parameters: { name: "Exercise" } },
    { action: "create_habit", parameters: { name: "Meditation" } },
    { action: "create_habit", parameters: { name: "Reading" } }
  ],
  message: "I'll create 3 habits for you: Exercise, Meditation, and Reading."
}
```

**Complexity notes:**
- Medium complexity: requires schema change, execution loop, partial failure handling
- Dependency: requires confirmation flow for bulk operations (see below)
- Risk: transaction boundaries (if action 2 fails, do we rollback action 1? Probably not for habit creation, but YES for bulk updates)

### Confirmation Flow for Complex Operations

**What 2026 AI platforms do (Microsoft Copilot, Salesforce Flow, Power Automate):**
- Multistage approval: AI proposes action, waits for user approval, then executes
- Conditional logic: auto-approve simple requests, require approval for bulk/destructive
- EU AI Act 2026 compliance: human-in-the-loop for impactful decisions
- Enterprise AI prioritizes user control over speed

**Recommended behavior for Orbit:**
- AI returns `requiresConfirmation: boolean` in response
- If true, frontend shows preview of planned actions + confirm/cancel buttons
- User confirms -> frontend sends `POST /api/chat/confirm` with action IDs to execute
- User cancels -> discard action plan, ask for clarification
- Confirmation required for: multi-action operations, destructive actions (delete), complex habit creation (parent + sub-habits), bulk logging
- NO confirmation for: single simple actions (log one existing habit, get habits list)

**Example confirmation flow:**
1. User: "Organize my room"
2. AI: Returns `requiresConfirmation: true` with preview:
   ```json
   {
     "responseType": "suggestion",
     "requiresConfirmation": true,
     "message": "I'll create a parent habit 'Organize Room' with 5 sub-habits. Confirm?",
     "preview": {
       "parentHabit": "Organize Room",
       "subHabits": ["Clear desk", "Vacuum floor", "Sort laundry", "Organize closet", "Dust shelves"]
     },
     "actions": [...]  // Actual creation actions
   }
   ```
3. Frontend: Shows preview with "Confirm" / "Cancel" / "Edit" buttons
4. User confirms -> backend creates habits
5. User cancels -> ask "What would you like to change?"

**Data model:**
- Add `ConfirmationId` to action plan (UUID to track pending confirmations)
- Store pending action plans in memory or database with expiration (5 min?)
- On confirm, retrieve plan by ID and execute
- Alternatively: send full action plan back in confirm request (stateless, simpler)

**Complexity notes:**
- Medium complexity: requires preview rendering logic, confirmation endpoint, plan storage/retrieval
- Stateless approach (send plan back) is simpler than storing pending confirmations
- Security consideration: validate action plan on confirm to prevent tampering

### Smart Habit Breakdown with Confirmation

**What 2026 task management AI does:**
- AFFiNE: Highlight text -> "Make this a checklist" -> instant sub-tasks
- Kawaii Tasks: Breaks down todos into manageable sub-tasks for ADHD users
- ClickUp Brain: Generates subtasks from goals
- All require user review before committing (no auto-creation)

**Recommended behavior:**
- AI detects vague or complex habit descriptions (heuristics: length > X chars, contains words like "routine", "organize", "prepare")
- Returns `suggestion` response with parent habit + proposed sub-habits
- User can: accept all, edit sub-habits, reject breakdown (just create parent)
- Leverages existing parent-child habit architecture (already in codebase)

**Detection heuristics:**
- Keywords: "routine", "organize", "clean", "prepare", "morning/evening/daily routine"
- Multi-step phrases: "and", "then", commas in description
- Length: > 50 characters suggests complex task
- User history: if user has other parent habits, more likely to want breakdown

**Example interaction:**
1. User: "Create a morning routine habit"
2. AI: Detects "routine" keyword, suggests breakdown
3. Response:
   ```json
   {
     "responseType": "suggestion",
     "requiresConfirmation": true,
     "message": "A morning routine works best as a checklist. I suggest these sub-habits:",
     "suggestions": [
       {
         "label": "Full Routine (5 steps)",
         "actions": [
           // Create parent + 5 sub-habits: Wake up, Shower, Breakfast, Exercise, Plan day
         ]
       },
       {
         "label": "Simple Routine (3 steps)",
         "actions": [
           // Create parent + 3 sub-habits: Wake up, Exercise, Breakfast
         ]
       },
       {
         "label": "Single Habit (no breakdown)",
         "actions": [
           // Create just "Morning Routine" as boolean habit
         ]
       }
     ]
   }
   ```
4. User picks option or customizes

**Complexity notes:**
- Medium complexity: requires detection logic, sub-habit generation prompts, integration with existing parent-child system
- Dependency: requires confirmation flow (see above)
- Risk: AI suggests irrelevant sub-habits. Mitigation: user can edit before confirming.

### Image Processing via Gemini Vision

**What Gemini Vision can do (2026):**
- Agentic Vision in Gemini 3 Flash: Think-Act-Observe loop, zooms into image regions, parses tables, executes code
- Object detection, text extraction (OCR), visual question answering
- Receipt OCR apps (Expensify, Klippa, Wave) achieve 95%+ accuracy on structured documents
- Supports analyzing photos, charts, flyers, bills, schedules

**Use cases for Orbit:**
- Photo of gym class schedule -> create habits for each class with correct days/times
- Photo of bill -> create reminder habit for due date
- Photo of handwritten todo list -> extract tasks and create habits (with confirmation!)
- Photo of progress chart -> extract current streak/completion data (future: auto-log from fitness tracker screenshots)

**Recommended behavior:**
- Add optional `image` parameter to chat endpoint (base64 or URL)
- Send image + text prompt to Gemini Vision API
- AI extracts: dates, times, task descriptions, frequencies
- Returns `suggestion` response with parsed data + proposed habits
- User MUST confirm before creation (never auto-create from image)
- Show extracted data in preview so user can verify accuracy

**Example interaction:**
1. User uploads gym schedule photo + text: "Create habits for my classes"
2. Backend sends to Gemini Vision: "Extract class names, days, and times from this image"
3. Gemini returns: `[{name: "Yoga", days: ["Monday", "Wednesday"], time: "6:00 PM"}, ...]`
4. AI formats as suggestion response with 2-3 options:
   - "Create all 5 classes as separate habits"
   - "Create 1 'Gym Classes' habit with 5 sub-habits"
   - "Show me the list to pick which ones to create"
5. User confirms -> habits created with correct schedules

**Data model:**
- Chat endpoint: Add `image?: string` (base64) or `imageUrl?: string`
- Gemini Vision API call: Same `generateContent` method, include image in prompt parts
- Response: Same action plan structure, just populated from image data

**Complexity notes:**
- Medium complexity: image upload handling, Gemini Vision integration (straightforward if already using Gemini), confirmation flow
- Risk: OCR errors, hallucination (AI invents text not in image)
- Mitigation: always show extracted text for user verification before creating habits
- Performance: Gemini Vision is fast (similar to text-only), no major latency concern

**Technical implementation:**
```csharp
// Chat endpoint
[HttpPost]
public async Task<IResult> Chat([FromBody] ChatRequest request)
{
    // request.Image is base64 string
    var prompt = BuildPrompt(request.Message, userContext);

    if (!string.IsNullOrEmpty(request.Image))
    {
        var visionResult = await _geminiService.AnalyzeImage(request.Image, prompt);
        // visionResult contains extracted data
    }
}

// Gemini service
public async Task<VisionResult> AnalyzeImage(string base64Image, string prompt)
{
    var content = new
    {
        contents = new[]
        {
            new
            {
                parts = new object[]
                {
                    new { text = prompt },
                    new { inline_data = new { mime_type = "image/jpeg", data = base64Image } }
                }
            }
        }
    };

    var response = await _httpClient.PostAsJsonAsync(_visionApiUrl, content);
    // Parse response, extract structured data
}
```

### AI User Learning (Extracted Facts)

**What memory-enabled AI does in 2026:**
- Mem0, Zep, AWS Bedrock AgentCore: Extract facts from conversations, store in vector DB or graph format
- ChatGPT memory, Claude Projects: Remember user preferences across sessions
- NotebookLM Personal Intelligence: Learn user's goals and preferences over time
- Facts stored: preferences, routines, obstacles, goals, relationships, constraints

**Example facts for Orbit:**
- "User exercises at 7am on weekdays"
- "User struggles with consistency on Fridays"
- "User prefers morning habits over evening"
- "User has a work meeting Tuesdays at 9am" (time constraint)
- "User finds meditation easier after exercise"
- "User's goal is to build a morning routine"

**Recommended architecture (Mem0-style):**
1. After each chat turn, AI extracts 0-N facts from conversation
2. Facts stored in `UserFact` entity with: UserId, FactText, Category, ExtractedAt, Confidence
3. Deduplication: before inserting, check if similar fact exists (semantic similarity via embeddings or simple text match)
4. On next chat, load recent/relevant facts into system prompt context
5. Facts decay over time (old facts = lower relevance) or user can delete facts

**Data model:**
```csharp
public class UserFact : Entity
{
    public Guid UserId { get; set; }
    public string FactText { get; set; }  // "User exercises at 7am on weekdays"
    public string? Category { get; set; }  // "routine", "preference", "obstacle", "goal"
    public float Confidence { get; set; }  // 0.0-1.0, AI's confidence in fact accuracy
    public DateTime ExtractedAt { get; set; }
    public DateTime? LastConfirmedAt { get; set; }  // User confirmed this fact is still true

    public User User { get; set; }
}
```

**Fact extraction prompt:**
```
After each user message, extract 0-3 key facts about the user's habits, preferences, or routines.
Return as JSON array:
[
  { "factText": "User exercises at 7am on weekdays", "category": "routine", "confidence": 0.9 },
  { "factText": "User prefers morning habits", "category": "preference", "confidence": 0.8 }
]

Only extract facts that are:
- Persistent (not one-time events)
- Actionable for future coaching
- Clearly stated or strongly implied

If no facts to extract, return empty array.
```

**System prompt integration:**
```
## User Context

Known facts about this user:
- Exercises at 7am on weekdays
- Struggles with Friday consistency
- Prefers morning habits over evening
- Has work meeting Tuesdays at 9am

Use these facts to personalize suggestions and avoid scheduling conflicts.
```

**Complexity notes:**
- High complexity: requires fact extraction prompt, deduplication logic, relevance scoring, fact expiration
- Dependency: requires multi-turn conversation history (already have in chat endpoint)
- Risk: AI extracts incorrect facts, facts become stale
- Mitigation: user can view/delete facts via profile endpoint, facts auto-expire after 90 days unless re-confirmed

**Phased approach:**
1. Phase 1: Simple fact extraction + storage, load all facts into prompt (no filtering)
2. Phase 2: Relevance scoring (load only top-N most relevant facts per chat)
3. Phase 3: User-facing fact management UI (view/edit/delete facts)

### Routine Inference from Log Timestamps

**What 2026 habit apps do:**
- Reclaim.ai: Analyzes calendar to find optimal habit times, auto-reschedules on conflicts
- Habitify 2026: Predictive AI flags "at-risk" habits using pattern analysis
- General trend: apps show "you usually do X at Y time" insights

**What Orbit can do uniquely:**
- Analyze HabitLog timestamps to detect: day-of-week patterns, time-of-day patterns, co-occurrence (habits often logged together)
- Store as UserFacts or separate `RoutinePattern` entity
- Proactive conflict detection: "You want to meditate at 7am, but you usually exercise at 7am Mondays"
- Time slot suggestions: "You're free at 6am, 8am, or 9pm based on your routine"

**Pattern detection logic:**
1. Query last 30 days of HabitLogs for user
2. Group by habit + day-of-week + hour-of-day
3. Detect patterns:
   - Habit X logged Mon/Wed/Fri at 7am (>= 70% of logs) -> strong routine
   - Habit Y logged randomly across week -> no routine
4. Store patterns as UserFacts: "User typically logs Meditation Mon/Wed/Fri at 6:30am"
5. Re-run pattern detection weekly or on-demand

**Conflict detection:**
- When creating habit with reminder time, check if existing routine overlaps
- "You already exercise at 7am on Mondays. Choose a different time or combine into a routine?"

**Time slot suggestion:**
- Calculate user's "active hours" (times when they log habits)
- Suggest time slots that: avoid conflicts, match category patterns (morning vs evening), fit user's active hours
- "Based on your routine, I suggest 6:00am, 7:30am, or 8:00pm for meditation. Which works?"

**Data model (option 1: store as UserFacts):**
- FactText: "User typically logs Meditation Mon/Wed/Fri at 6:30am"
- Category: "routine"
- Confidence: based on consistency (90% of logs at same time = 0.9 confidence)

**Data model (option 2: dedicated RoutinePattern entity):**
```csharp
public class RoutinePattern : Entity
{
    public Guid UserId { get; set; }
    public Guid HabitId { get; set; }
    public DayOfWeek[] DaysOfWeek { get; set; }  // Mon, Wed, Fri
    public TimeOnly PreferredTime { get; set; }  // 6:30am
    public float Confidence { get; set; }  // 0.9
    public DateTime DetectedAt { get; set; }
    public DateTime LastObservedAt { get; set; }  // Most recent log matching this pattern
}
```

**System prompt integration:**
```
## User Routines

Detected patterns:
- Meditation: Mon/Wed/Fri at 6:30am (90% consistent)
- Exercise: Daily at 7:00am (85% consistent)
- Reading: Weekends at 9:00pm (70% consistent)

When suggesting times for new habits, avoid conflicts with these routines.
```

**Complexity notes:**
- High complexity: requires timestamp analysis, pattern detection algorithm, conflict checking, time slot recommendation logic
- Dependency: requires sufficient log history (>= 2 weeks recommended)
- Risk: patterns change over time (user shifts schedule)
- Mitigation: re-run detection weekly, mark patterns as stale if no recent logs match

**Phased approach:**
1. Phase 1: Basic pattern detection (day-of-week + time-of-day), store as UserFacts
2. Phase 2: Conflict detection on habit creation, warn user
3. Phase 3: Proactive time slot suggestions, co-occurrence detection (habit chains)

### Structured Suggestion Responses

**What 2026 chatbot UX expects:**
- Progressive disclosure: 2-3 options instead of open-ended prompts
- Jotform, Emergent: "clickable options" are core UX best practice
- Short, scannable responses with buttons/cards for structured inputs
- "Instead of asking 'How can I help?', ask 'Book appointment, check hours, or contact support?'"

**Use cases for Orbit:**
- Habit breakdown: "Full routine (5 steps) / Simple routine (3 steps) / Single habit"
- Time slot: "6:00am / 7:30am / 8:00pm / Custom time"
- Frequency: "Daily / 3x per week / Weekdays only / Custom"
- Conflict resolution: "Reschedule to 8am / Combine into routine / Keep both"

**Recommended behavior:**
- AI returns `responseType: "suggestion"` with array of 2-3 pre-built options + optional "custom" fallback
- Each option has: label, description, and action plan
- Frontend renders as buttons or cards (not plain text)
- User clicks option -> execute that option's actions (with confirmation if needed)

**Data model:**
```typescript
{
  responseType: "suggestion",
  message: "When do you want to exercise?",
  suggestions: [
    {
      label: "Morning (6:00 AM)",
      description: "You're usually active in mornings",
      actions: [{ action: "create_habit", parameters: { name: "Exercise", reminderTime: "06:00" } }]
    },
    {
      label: "Evening (7:00 PM)",
      description: "After work, when you're free",
      actions: [{ action: "create_habit", parameters: { name: "Exercise", reminderTime: "19:00" } }]
    },
    {
      label: "Custom time",
      description: "Choose your own time",
      actions: []  // Frontend prompts for custom input
    }
  ]
}
```

**Complexity notes:**
- Low complexity: just a schema variant of existing action plan response
- Works well with confirmation flow (preview options before executing)
- Reduces user friction: clicking button is easier than typing "7am"

**When to use suggestions vs actions:**
- Use `suggestions` when: multiple valid approaches, user input needed (time, frequency), ambiguous intent
- Use `actions` when: clear intent, single correct approach, simple execution

## Feature Dependencies

```
Multi-Action Responses
    requires Existing AI chat infrastructure (DONE)
    requires Confirmation flow (for bulk operations)
    (independent of other v1.1 features)

Confirmation Flow
    requires Multi-Action Responses (or single complex actions)
    (independent, foundational for other features)

Structured Suggestions
    requires Existing AI chat infrastructure (DONE)
    enhanced-by Routine Inference (time slot suggestions)
    (independent, low complexity)

Smart Habit Breakdown
    requires Parent-child habit architecture (DONE)
    requires Confirmation Flow
    enhanced-by Structured Suggestions (option selection)

Image Processing
    requires Gemini API integration (DONE)
    requires Confirmation Flow (never auto-create from image)
    (independent, just adds vision capability)

AI User Learning (Facts)
    requires Multi-turn conversation history (DONE)
    enhanced-by Routine Inference (stores patterns as facts)
    (foundational for proactive AI, but independent to build)

Routine Inference
    requires Sufficient log history (>= 2 weeks recommended)
    enhanced-by AI User Learning (stores patterns as facts)
    enhanced-by Confirmation Flow (conflict warnings)
    enhanced-by Structured Suggestions (time slot options)
```

### Dependency Notes

- **Confirmation Flow is foundational:** Multi-action, habit breakdown, and image processing all require confirmation. Build this first.
- **Structured Suggestions enhance everything:** Makes confirmation flow UX better, makes time slot selection easier. Low cost, high value. Build early.
- **User Learning and Routine Inference are separate:** Can build in either order. Routine Inference has immediate UX value (conflict detection). User Learning is more strategic (long-term personalization).
- **Image Processing is independent:** Just adds a new input modality. Can build any time after confirmation flow exists.
- **Multi-Action is table stakes:** Should be built early (users expect it in 2026). Unlocks batch logging, bulk creation.

## MVP Recommendation for v1.1

### Must Have (Core Intelligence Features)

1. **Multi-action AI responses** - Table stakes in 2026. Unlocks batch operations. Medium complexity.
2. **Confirmation flow** - Safety net for complex/bulk operations. EU AI Act compliance. Required by multi-action, habit breakdown, image processing.
3. **Structured suggestions** - Modern chatbot UX. Low complexity, high UX value. Makes confirmation flow better.

### Should Have (Strong Differentiators)

4. **Smart habit breakdown with confirmation** - Differentiator. Leverages existing parent-child architecture. Medium complexity.
5. **Routine inference (basic)** - Unique feature, high value. Conflict detection is compelling. Start simple (day-of-week + time patterns). High complexity, but phased approach makes it manageable.

### Nice to Have (Future Phases)

6. **AI user learning (facts)** - Strategic long-term feature. Enables deep personalization. High complexity. Build after routine inference to leverage pattern data.
7. **Image processing** - Novel feature, but niche use case. Medium complexity. Defer until core intelligence features are stable.

### Defer to v1.2+

- Advanced routine inference (co-occurrence, habit chains)
- Fact management UI (user edits/deletes facts)
- Context-aware time suggestions (beyond basic conflict detection)
- Image-based progress tracking (analyze fitness tracker screenshots)

## Feature Prioritization Matrix

| Feature | User Value | Implementation Cost | Dependency Risk | Priority |
|---------|------------|---------------------|-----------------|----------|
| Multi-action responses | HIGH | MEDIUM | LOW | P0 (table stakes) |
| Confirmation flow | HIGH | MEDIUM | LOW | P0 (foundational) |
| Structured suggestions | MEDIUM | LOW | LOW | P0 (UX enhancer) |
| Smart habit breakdown | HIGH | MEDIUM | MEDIUM (needs confirmation) | P1 |
| Routine inference (basic) | HIGH | HIGH | LOW (just needs logs) | P1 |
| AI user learning (facts) | MEDIUM | HIGH | MEDIUM (needs fact extraction) | P2 |
| Image processing | MEDIUM | MEDIUM | MEDIUM (needs confirmation) | P2 |
| Advanced routine inference | MEDIUM | HIGH | HIGH (needs basic inference) | P3 |
| Fact management UI | LOW | MEDIUM | HIGH (needs fact system) | P3 |

**Priority key:**
- P0: Must have for v1.1 (table stakes or foundational)
- P1: Should have for v1.1 (strong differentiators)
- P2: Nice to have for v1.1, likely defer to v1.2
- P3: Future consideration (v1.2+)

**Recommended v1.1 scope:**
- P0 features: Multi-action, Confirmation, Structured suggestions (5-7 days total)
- P1 features: Smart habit breakdown, Basic routine inference (10-14 days total)
- Total: 15-21 days for complete v1.1

**Defer to v1.2:**
- AI user learning (facts) - strategic, but routine inference delivers more immediate value
- Image processing - novel but niche, needs more UX research

## Competitive Feature Analysis

| Feature | Habitify | Reclaim.ai | Pattrn | Rocky.ai | ChatGPT | Orbit (v1.1 Planned) |
|---------|----------|------------|--------|----------|---------|----------------------|
| AI chat interface | No | No | No | Yes | Yes | Yes (DONE) |
| Multi-action AI | No | No | No | No | Yes (standard) | Yes (P0) |
| Confirmation flow | No | No | No | No | Implicit | Yes (P0) |
| Structured suggestions | No | No | No | No | No | Yes (P0) |
| Smart habit breakdown | Checklist (premium) | No | No | No | General task breakdown | Yes with confirmation (P1) |
| Routine detection | Predictive AI 2026 | Calendar analysis | Smart analytics | No | No | Yes via log analysis (P1) |
| Conflict detection | No | Yes (calendar) | No | No | No | Yes (P1, unique) |
| Time slot suggestions | No | Yes (calendar) | No | No | No | Yes (P1, without calendar) |
| AI user memory | No | No | No | No | Yes (ChatGPT memory) | Yes (P2, planned) |
| Image processing | No | No | No | No | Yes (vision) | Yes (P2, planned) |

**Key takeaways:**
- Multi-action, confirmation, structured suggestions are TABLE STAKES for AI chat in 2026 (ChatGPT has them, users expect them)
- Routine inference WITHOUT calendar integration is UNIQUE (Reclaim does it with calendar, no one does it with log history alone)
- Conflict detection for habits is GREENFIELD (no competitor does this)
- Smart habit breakdown with confirmation is DIFFERENTIATOR (most apps have checklists, none have AI-generated breakdown with preview)
- Orbit's v1.1 plan brings it to PARITY with general AI assistants (ChatGPT) while maintaining DIFFERENTIATION in habit-specific intelligence (routine inference, conflict detection)

## Research Gaps and Open Questions

### Gaps Identified

1. **Partial failure handling in multi-action:** Should backend rollback previous actions if later action fails? Or continue with "best effort"? No clear industry standard found. Recommendation: best effort with clear error reporting.

2. **Fact expiration/relevance scoring:** How long should facts remain active? When do routines become stale? Mem0 docs don't specify clear expiration policy. Recommendation: 90-day soft expiration, re-confirm if fact is referenced in prompt.

3. **Image hallucination mitigation:** Gemini Vision can invent text not in image. What's acceptable accuracy threshold for habit creation? Recommendation: always require confirmation, show extracted text for verification.

4. **Routine inference minimum data:** How many logs needed to establish pattern? 2 weeks? 4 weeks? No habit-specific research found. Recommendation: start with 2 weeks (14 days), require >= 70% consistency to flag as routine.

### Questions for Phase-Specific Research

- **Multi-action transaction boundaries:** When to use DB transactions for multi-action? (Phase 2: Implementation)
- **Fact deduplication algorithm:** Semantic similarity via embeddings or simple text matching? (Phase 5: User Learning)
- **Routine inference algorithm:** Use ML clustering or rule-based heuristics? (Phase 4: Routine Inference)
- **Image processing use cases:** What images do users actually want to upload? (Phase 6: User research)

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Multi-action responses | HIGH | Well-documented in BatchIt MCP, Azure Logic Apps, standard in 2026 AI |
| Confirmation flow | HIGH | Enterprise AI platforms (Microsoft, Salesforce) have clear patterns |
| Structured suggestions | HIGH | Chatbot UX best practices (Jotform, Emergent) are consistent |
| Smart habit breakdown | MEDIUM | Task decomposition is proven (AFFiNE, ClickUp), but habit-specific UX is less documented |
| Image processing | MEDIUM | Gemini Vision capabilities are well-documented, but habit tracking use cases are novel |
| AI user learning | MEDIUM | Memory architectures (Mem0, Zep, Bedrock) are clear, but fact extraction quality depends on prompt engineering |
| Routine inference | LOW | Pattern detection logic is standard, but habit-specific thresholds (% consistency, minimum logs) are not well-researched. Will need experimentation. |

**Overall confidence: MEDIUM**
- High confidence in table stakes features (multi-action, confirmation, suggestions)
- Medium confidence in differentiators (breakdown, image, learning)
- Low confidence in routine inference specifics (will need A/B testing)

## Sources

### Multi-Action & Batch Operations
- [BatchIt MCP Server: AI Workflow Optimization](https://skywork.ai/skypage/en/batchit-mcp-ai-workflow-optimization/1981937685883883520)
- [HighLevel: Conversation AI Multiple Messages](https://help.gohighlevel.com/support/solutions/articles/155000003207-conversation-ai-multiple-messages-in-one-workflow-action)
- [Agentic AI Orchestration in 2026](https://onereach.ai/blog/agentic-ai-orchestration-enterprise-workflow-automation/)

### Confirmation Flow & Approvals
- [Microsoft Copilot Studio: Multistage Approvals](https://learn.microsoft.com/en-us/microsoft-copilot-studio/flows-advanced-approvals)
- [Salesforce Flow Approval Processes Winter '26](https://www.salesforceben.com/salesforce-spring-25-release-new-flow-approval-process-capabilities/)
- [Budibase: Automate Internal Approvals with AI](https://budibase.com/blog/ai-agents/automate-internal-approvals-with-ai/)
- [AI Regulations 2026: EU AI Act](https://sombrainc.com/blog/ai-regulations-2026-eu-ai-act)

### Structured Suggestions & Chatbot UX
- [Jotform: Chatbot Design Challenges 2026](https://www.jotform.com/ai/agents/chatbot-design/)
- [Emergent: Best AI Chatbot Builders 2026](https://emergent.sh/learn/best-ai-chatbot-builders)
- [Botpress: Conversational AI Design 2026](https://botpress.com/blog/conversation-design)
- [UX Studio: Chatbot UI Best Practices](https://www.uxstudioteam.com/ux-blog/chatbot-ui)

### AI User Learning & Memory
- [Mem0: Graph Memory for AI Agents](https://mem0.ai/blog/graph-memory-solutions-ai-agents)
- [AWS: Amazon Bedrock AgentCore Episodic Memory](https://aws.amazon.com/blogs/machine-learning/build-agents-to-learn-from-experiences-using-amazon-bedrock-agentcore-episodic-memory/)
- [Stack AI: How AI Systems Remember Information in 2026](https://www.stack-ai.com/blog/how-ai-systems-remember-information-in-2026)
- [DataCamp: How Does LLM Memory Work?](https://www.datacamp.com/blog/how-does-llm-memory-work)
- [Android Headlines: NotebookLM Personal Intelligence](https://www.androidheadlines.com/2026/02/google-notebooklm-personal-intelligence-leak-learning-features.html)
- [Dume.ai: Top 10 AI Assistants With Memory in 2026](https://www.dume.ai/blog/top-10-ai-assistants-with-memory-in-2026)

### Routine Detection & Smart Scheduling
- [Reclaim.ai: #1 Habit Tracker App](https://reclaim.ai/features/habits)
- [Reclaim.ai: 10 Best Habit Tracker Apps 2026](https://reclaim.ai/blog/habit-tracker-apps)
- [Fhynix: Best Habit Tracking Apps 2026](https://fhynix.com/best-habit-tracking-apps/)
- [Emergent: Top 5 Habit Building Apps 2026](https://emergent.sh/learn/best-habit-building-apps)

### Task Breakdown & Sub-Habits
- [Medium: How to Use AI to Break Down Complex Projects](https://medium.com/@aiforeverything101/how-to-use-ai-to-break-down-complex-projects-into-actionable-tasks-f65946af642a)
- [OneUpTime: How to Create Task Decomposition](https://oneuptime.com/blog/post/2026-01-30-task-decomposition/view)
- [AFFiNE: ADHD Task Management Apps 2026](https://affine.pro/blog/adhd-task-management-apps)
- [Morgen: 5 Best AI Task Manager Software Tools 2026](https://www.morgen.so/blog-posts/ai-task-manager)

### Image Processing & Gemini Vision
- [Google: Introducing Agentic Vision in Gemini 3 Flash](https://blog.google/innovation-and-ai/technology/developers-tools/agentic-vision-gemini-3-flash/)
- [Google AI: Image Understanding with Gemini API](https://ai.google.dev/gemini-api/docs/image-understanding)
- [InfoQ: Google Supercharges Gemini 3 Flash](https://www.infoq.com/news/2026/02/google-gemini-agentic-vision/)
- [Ultralytics: Hands-on Gemini 2.5 for Computer Vision](https://www.ultralytics.com/blog/get-hands-on-with-google-gemini-2-5-for-computer-vision-tasks)
- [VideoSDK: Gemini Vision API Guide](https://www.videosdk.live/developer-hub/ai/gemini-vision-api)

### OCR & Receipt Processing (Image Context)
- [Bill.com: 13 Best Receipt Scanner Apps 2026](https://www.bill.com/blog/best-receipt-scanning-app)
- [Klippa: Best OCR Software for Receipts 2026](https://www.klippa.com/en/blog/information/ocr-software-receipts/)
- [Rho: Best Business Receipt Scanner Apps 2026](https://www.rho.co/blog/best-receipt-scanner-app)

### General AI Assistant Trends
- [Codiant: Top AI Assistant Trends in 2026](https://codiant.com/blog/top-ai-assistant-trends/)
- [Built In: 30 Popular AI Assistants in 2026](https://builtin.com/artificial-intelligence/ai-assistant)
- [Medium: Why Everyone Will Have a Personal AI Assistant by 2026](https://medium.com/@CodeWithHannan/why-everyone-will-have-a-personal-ai-assistant-by-2026-6ef59a684f40)

---

*Feature research for: AI Intelligence Enhancements (v1.1)*
*Researched: 2026-02-09*
*Focus: Multi-action responses, image processing, user learning, routine inference, structured suggestions*
