# Feature Research

**Domain:** AI-Powered Habit Tracking (Backend API)
**Researched:** 2026-02-07
**Confidence:** HIGH (verified across multiple competitor apps, industry articles, and app store listings)

## Feature Landscape

### Table Stakes (Users Expect These)

Features users assume exist. Missing these = product feels incomplete.

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| Streaks (current + longest) | Every habit app shows streaks. Atomic Habits popularized "don't break the chain." Users measure themselves by streak length. | LOW | Calculate from HabitLog data. Store `CurrentStreak` and `LongestStreak` on Habit entity or compute on read. Computing on read is simpler and avoids sync bugs but costs a query scan; caching the values is fine for an API. |
| Completion rate | Users expect to see "you completed this habit 78% of the time." Streaks alone create all-or-nothing thinking; completion rate gives a forgiving, trend-based view. | LOW | `completedDays / expectedDays` over a configurable window (7d, 30d, all-time). Must account for frequency -- a 3x/week habit should not penalize off-days. |
| Basic progress charts data | Apps like Habitify, Way of Life, and Atoms all provide visual progress. Users expect the backend to return data that powers weekly/monthly trend views. | MEDIUM | Return aggregated log data by day/week/month. The frontend owns visualization; the API returns structured time-series data. |
| Archive/pause habits | Habitify, HabitKit, and others let users pause seasonal or temporary habits without losing history. Users get frustrated if the only options are "active" or "deleted." | LOW | Already have `IsActive` on Habit. Rename semantics: `IsActive=false` becomes "archived." Add an `ArchivedAtUtc` timestamp. Archived habits excluded from daily views but retain all logs. |
| Notes on habit logs | Way of Life, HabitYou, TickOff, and Done all support per-log notes. Users want to record context ("only did 10 minutes," "felt great after"). This turns tracking into journaling. | LOW | Add optional `Note` (string, nullable) to `HabitLog` entity. |
| User profile (display name, preferences) | Every app with auth has a profile. Users need to update name, email, and app preferences (timezone is critical for date-based tracking). | LOW | User entity already has `Name` and `Email` with `UpdateProfile`. Add `Timezone` (IANA string) and `PreferredStartOfWeek` (DayOfWeek). |
| Smart reminders data model | Every major competitor has notifications. The API needs to store reminder preferences per habit even if push delivery is deferred. | LOW | Add `ReminderTime` (TimeOnly, nullable) to Habit. Actual push notification delivery is infrastructure concern for later. |
| Tags/categories for habits | Habitify groups by time of day, Way of Life uses color coding, Notion templates use categories (Health, Productivity, Finance). Users need to filter and organize habits. | MEDIUM | See detailed design below in Tags section. |

### Differentiators (Competitive Advantage)

Features that set Orbit apart. Not required, but valuable -- especially the AI-powered ones.

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| AI-powered habit coaching via chat | Orbit's core differentiator. Pattrn, Habit AI, and Rocky.ai are emerging competitors, but most habit apps bolt on AI as an afterthought. Orbit's chat-first architecture makes AI the primary interface, not a sidebar feature. | MEDIUM | Already implemented. Enhance with progress-aware prompts: feed streak data and completion rates into system prompt so AI can say "you've hit 80% this week on meditation, nice work." |
| Sub-habits (parent with checklist children) | HabitNow Premium has checklists. Most apps do NOT have true sub-habits. This makes complex habits ("Morning Routine" with sub-steps) trackable without creating 5 separate habits. | MEDIUM | New entity `SubHabit` with `ParentHabitId`, `Title`, `SortOrder`. Parent habit completion = all sub-habits checked. See Architecture section. |
| Bad/negative habit tracking | Way of Life (color-coded), Streaks (negative tasks), Quitzilla (dedicated app). Most general habit trackers ignore this or treat it as an afterthought. Proper negative tracking with "days since last slip" and relapse history is genuinely useful. | MEDIUM | New `HabitDirection` enum: `Positive` (build) vs `Negative` (break). Negative habits invert the UI logic: logging = slip-up, streak = days WITHOUT logging. See detailed design below. |
| AI-generated insights from habit data | Pattrn leads here with "smart analytics." The AI can identify correlations ("you exercise more on days you meditate"), flag at-risk habits, and suggest adjustments. Most apps show charts but do not interpret them. | HIGH | Requires feeding aggregated habit data to AI. Build a `HabitInsightsService` that summarizes user data into a prompt context block. Start with weekly summaries, expand to correlation analysis. |
| Habit templates (pre-built habits) | Atoms offers guided habit creation based on identity ("the kind of person you want to become"). Pre-built templates reduce friction for new users. | LOW | Seed data or a `HabitTemplate` table with common habits (Drink water, Exercise, Read, Meditate) plus suggested frequency. AI chat can also suggest templates. |
| Flexible goal tracking | Some habits are not binary or simple counts. "Run 5km 3 times this week" combines quantifiable + frequency targets. Orbit already has the frequency system; adding period-based goal aggregation is natural. | MEDIUM | Compute goal progress: `logsInPeriod.Sum(l => l.Value) / targetValue` for quantifiable habits, `logsInPeriod.Count / frequencyQuantity` for boolean habits. |

### Anti-Features (Commonly Requested, Often Problematic)

Features that seem good but create problems. Explicitly do NOT build these.

| Feature | Why Requested | Why Problematic | Alternative |
|---------|---------------|-----------------|-------------|
| Social/community features | Habitica has guilds, Habitify has group challenges. Users ask for accountability partners. | Massive scope increase (friend system, privacy controls, moderation, abuse). Community features are a product in themselves. A solo backend API should not attempt this. | AI acts as the accountability partner. Add a "share progress" export endpoint later if needed. |
| Gamification (XP, levels, avatars) | Habitica's entire value prop. Fabulous uses "journeys." Users love game mechanics. | Gamification is extremely hard to balance. Bad gamification feels patronizing. It requires constant content creation (new rewards, levels, challenges) and becomes the product focus instead of actual habit building. | Use streaks and completion milestones as lightweight gamification. AI chat can celebrate milestones naturally ("30-day streak, that's impressive"). |
| Calendar integration (Google/Outlook sync) | Reclaim.ai's core feature. Users want habits in their calendar. | Requires OAuth flows, calendar API maintenance, conflict resolution, timezone hell at scale. This is an entire product vertical (Reclaim raised VC funding for this alone). | Store reminder times in the API. Let a future frontend/mobile app handle local calendar integration if desired. |
| Real-time sync/collaboration | "I want my partner to see my habits" | WebSocket infrastructure, conflict resolution, permissions system, real-time state management. Enormous complexity for marginal value in a personal habit tracker. | Per-user data only. Export/share as a future read-only feature. |
| Financial stakes/commitment contracts | stickK's model. "I'll lose $50 if I miss my habit." | Payment processing, legal liability, refund disputes, regulatory concerns. This is a fintech feature masquerading as a habit feature. | AI can ask "what's at stake?" as a motivational prompt without actual money changing hands. |
| VR/AR integration | Emerging trend per 2026 reports (Fabulous AR overlays). | Bleeding edge, tiny user base, requires platform-specific SDKs, zero ROI for a backend API. | Ignore entirely. |
| Mood/emotion tracking as primary feature | Some apps combine mood + habits. Users ask for "how did I feel" tracking. | Scope creep into mental health territory. Requires sensitive data handling, possibly regulatory compliance (HIPAA-adjacent). Dilutes the habit tracking focus. | Allow freeform notes on habit logs. Users can write "felt great" or "stressed" without structured mood data. AI can read these notes for context. |

## Feature Deep Dives

### Sub-Habits Design

**What users expect:** A parent habit ("Morning Routine") that contains ordered checklist items ("Make bed," "Stretch," "Drink water"). The parent is "complete" when all children are checked off. Some apps (HabitNow) show sub-tasks on a timer screen.

**Recommended behavior:**
- Parent habit has `HabitType.Checklist` (new enum value) or remains `Boolean` with children
- Sub-habits are simple boolean items with a `SortOrder`
- Logging a parent habit = logging all incomplete sub-habits for that date
- Sub-habits do NOT have their own frequency -- they inherit from the parent
- Sub-habits do NOT have independent streaks
- Parent streak counts days when ALL sub-habits were completed
- Sub-habits can be added/removed/reordered without breaking log history

**Data model:**
- `SubHabit` entity: `Id`, `HabitId` (FK to parent), `Title`, `SortOrder`, `CreatedAtUtc`
- `SubHabitLog` entity: `Id`, `SubHabitId`, `HabitLogId` (FK to parent log), `IsCompleted`
- Alternative: store sub-habit completion as JSON on `HabitLog` -- simpler but less queryable

### Bad/Negative Habit Tracking Design

**What users expect (from Quitzilla, Way of Life, Streaks):**
- Track habits you want to STOP doing (smoking, nail biting, social media doom scrolling)
- The "streak" is inverted: count days SINCE the last occurrence (not days completed)
- Logging a negative habit means recording a slip-up/relapse
- Relapse does NOT delete history -- previous streaks are preserved
- Show: "days since last," "longest streak without," "total slip-ups," "average days between slips"
- Optional: note WHY the slip happened (trigger tracking)

**Recommended behavior:**
- Add `Direction` property to Habit: `Positive` (default, build habit) or `Negative` (break habit)
- For negative habits, `HabitLog` entries represent slip-ups, not completions
- Streak calculation inverts: days since last log entry = current streak
- Completion rate inverts: fewer logs = better performance
- AI chat recognizes negative habits and adjusts language ("you've gone 12 days without smoking" not "you haven't logged smoking in 12 days")

**Data model impact:**
- Add `HabitDirection` enum: `Positive`, `Negative`
- Add `Direction` property to `Habit` entity
- No changes to `HabitLog` structure -- a log entry for a negative habit IS the slip-up
- Metrics calculation service must branch on `Direction`

### Tags System Design

**What competitors do:**
- Habitify: groups by time of day (Morning, Afternoon, Evening)
- Way of Life: color-coded categories
- Notion templates: freeform tags (Health, Work, Personal)
- Most apps: predefined categories with optional color

**Recommended approach for Orbit: User-defined tags**
- Tags are user-scoped (each user creates their own)
- Many-to-many: a habit can have multiple tags, a tag applies to multiple habits
- Tags have: `Id`, `UserId`, `Name`, `Color` (hex string)
- Predefined seed tags optional (Health, Productivity, Personal, Finance, Social)
- Filter habits by tag via query parameter
- AI chat can suggest tags when creating habits

**Data model:**
- `Tag` entity: `Id`, `UserId`, `Name`, `Color`, `CreatedAtUtc`
- `HabitTag` join entity: `HabitId`, `TagId`
- Unique constraint on `(UserId, Name)` -- no duplicate tag names per user

### Progress Metrics Design

**What every competitor offers (table stakes):**
- Current streak (consecutive completions)
- Longest streak (personal record)
- Completion rate (percentage over time window)
- Total completions (all-time count)

**What differentiators offer (Pattrn, Habitify Pro):**
- Completion rate by day of week (bar chart data)
- Trend over time (weekly averages as line chart data)
- Best/worst performing habits
- Habit correlation ("you exercise more when you also meditate")
- "At risk" detection (declining completion rate)

**Recommended phased approach:**
1. **Phase 1 (table stakes):** Current streak, longest streak, completion rate (7d/30d/all-time), total completions. Compute on read from HabitLog data.
2. **Phase 2 (differentiators):** Day-of-week breakdown, weekly trend data, best/worst habits ranking. Still computed from logs.
3. **Phase 3 (AI-powered):** Feed metrics into AI system prompt for personalized insights. "Your meditation habit drops on Wednesdays -- what happens on Wednesdays?"

**API shape:**
```
GET /api/habits/{id}/metrics?period=30d
Response: {
  currentStreak: 7,
  longestStreak: 23,
  completionRate: 0.82,
  totalCompletions: 45,
  dayOfWeekBreakdown: { Monday: 0.9, Tuesday: 0.75, ... },
  weeklyTrend: [{ week: "2026-W05", rate: 0.85 }, ...]
}
```

### User Profile Design

**What competitors include:**
- Display name, email, avatar
- Timezone (critical for date-boundary calculations)
- Preferred start of week (Monday vs Sunday)
- Notification preferences (global on/off, quiet hours)
- Theme preference (dark/light -- frontend concern, but stored server-side)
- Account management (change password, delete account)

**Recommended additions to User entity:**
- `Timezone` (string, IANA format like "America/New_York") -- critical for correct streak calculation across timezones
- `PreferredStartOfWeek` (DayOfWeek, default Monday) -- affects weekly metrics
- `AvatarUrl` (string, nullable) -- optional, low priority
- Change password endpoint
- Delete account endpoint (GDPR-style data export + deletion)

## Feature Dependencies

```
Tags
    (independent, no prerequisites)

Sub-Habits
    requires Habit CRUD (DONE)
    requires HabitLog system (DONE)

Bad/Negative Habits
    requires Habit CRUD (DONE)
    requires Metrics (for inverted streak calculation)

Progress Metrics
    requires HabitLog system (DONE)
    enhanced-by Tags (filter metrics by tag)
    enhanced-by Bad Habits (inverted calculations)

User Profile
    requires Auth system (DONE)
    enhanced-by Timezone for correct metric calculations

AI Coaching Improvements
    requires Progress Metrics (to feed data into prompts)
    enhanced-by Sub-Habits (AI can create habits with sub-steps)
    enhanced-by Bad Habits (AI adjusts language for negative habits)
    enhanced-by Tags (AI can suggest tags)
```

### Dependency Notes

- **Progress Metrics requires HabitLog:** Metrics are computed from log history. Already available.
- **Bad Habits requires Metrics:** The inverted streak logic is part of the metrics service, so they should be built together or metrics first.
- **AI Coaching requires Metrics:** The AI cannot give insights without data to analyze. Build metrics before enhancing AI prompts.
- **Tags is independent:** Can be built at any point. Simple many-to-many relationship.
- **Sub-Habits is independent of Tags/Metrics:** Can be built in any phase, but AI integration benefits from having sub-habits available.
- **User Profile (timezone) enhances Metrics:** Timezone-aware date boundaries make streak/completion calculations correct across timezones. Build profile timezone before or alongside metrics.

## MVP Definition

Note: Orbit already has a working MVP (habit CRUD, logging, AI chat). This defines the NEXT milestone's priorities.

### Launch With (v1.1 -- Next Milestone)

- [x] Habit CRUD with flexible frequency (DONE)
- [x] Habit logging with boolean + quantifiable types (DONE)
- [x] AI chat for quick actions (DONE)
- [ ] **User profile with timezone** -- Correct date handling is foundational for everything else
- [ ] **Progress metrics (basic)** -- Current streak, longest streak, completion rate. Users cannot evaluate their progress without this.
- [ ] **Tags** -- Low complexity, high organizational value. Unblocks habit filtering.
- [ ] **Notes on habit logs** -- Trivially simple, high user value for context.

### Add After Validation (v1.2)

- [ ] **Bad/negative habit tracking** -- Requires metrics foundation from v1.1. Meaningful differentiator.
- [ ] **Sub-habits** -- Medium complexity. Build after core metrics prove stable.
- [ ] **AI prompt improvements** -- Feed metrics data into system prompts for progress-aware coaching.
- [ ] **Habit templates** -- Low effort, reduces new user friction.

### Future Consideration (v2+)

- [ ] **AI-generated insights** -- Correlation analysis, at-risk detection, weekly AI summaries. Requires significant prompt engineering and testing.
- [ ] **Advanced metrics** -- Day-of-week breakdown, weekly trends, best/worst habit rankings.
- [ ] **Habit reminders delivery** -- Actual push notification infrastructure (not just storing reminder time).
- [ ] **Data export** -- CSV/JSON export of all user data.

## Feature Prioritization Matrix

| Feature | User Value | Implementation Cost | Priority |
|---------|------------|---------------------|----------|
| User profile + timezone | HIGH | LOW | P1 |
| Progress metrics (basic) | HIGH | MEDIUM | P1 |
| Tags | MEDIUM | LOW | P1 |
| Notes on habit logs | MEDIUM | LOW | P1 |
| Bad/negative habits | HIGH | MEDIUM | P2 |
| Sub-habits | MEDIUM | MEDIUM | P2 |
| AI prompt improvements | HIGH | MEDIUM | P2 |
| Habit templates | MEDIUM | LOW | P2 |
| AI-generated insights | HIGH | HIGH | P3 |
| Advanced metrics | MEDIUM | MEDIUM | P3 |
| Reminder delivery | MEDIUM | HIGH | P3 |
| Data export | LOW | LOW | P3 |

**Priority key:**
- P1: Must have for next milestone
- P2: Should have, add in following phases
- P3: Nice to have, future consideration

## Competitor Feature Analysis

| Feature | Habitify | Streaks | Way of Life | Quitzilla | Atoms | Orbit (Planned) |
|---------|----------|---------|-------------|-----------|-------|-----------------|
| Positive habit tracking | Yes | Yes | Yes | No | Yes | Yes (DONE) |
| Negative habit tracking | No | Yes (negative tasks) | Yes (color-coded) | Yes (dedicated) | No | Yes (HabitDirection enum) |
| Sub-habits/checklists | Yes (premium) | No | No | No | No | Yes (SubHabit entity) |
| Tags/categories | Time-of-day groups | No (limit 24) | Color codes | No | No | Yes (user-defined tags) |
| Streaks | Yes | Yes (core feature) | Yes | Yes (inverted) | Yes | Yes (computed from logs) |
| Completion rate | Yes | No | Yes (trends) | No | Yes | Yes (configurable window) |
| Notes per log | Yes (swipe) | No | Yes (diary) | Yes (diary) | No | Yes (on HabitLog) |
| AI chat interface | No | No | No | No | No | Yes (core differentiator) |
| AI-powered insights | Predictive AI (2026) | No | No | No | No | Planned (v2) |
| Archive/pause | Yes | No | No | No | No | Yes (IsActive flag) |
| Quantifiable habits | Yes | Timed tasks | No | No (time only) | No | Yes (DONE) |
| Flexible frequency | Yes | Custom intervals | Daily only | N/A | Yes | Yes (DONE, best-in-class) |

**Key takeaway:** Orbit's AI chat is a genuine differentiator. No major competitor has a natural language interface as the primary interaction model. The planned features (sub-habits, negative tracking, tags, metrics) bring Orbit to parity with premium competitors while maintaining the AI-first advantage.

## Sources

- [Reclaim.ai - Best Habit Tracker Apps 2026](https://reclaim.ai/blog/habit-tracker-apps) -- comprehensive competitor comparison
- [Way of Life](https://wayoflifeapp.com/) -- negative habit tracking with color-coded system and diary
- [Quitzilla](https://quitzilla.com/) -- dedicated bad habit tracker with relapse history and timers
- [Atoms by James Clear](https://atoms.jamesclear.com/) -- Atomic Habits methodology, guided habit creation
- [Habitify Help Center](https://intercom.help/habitify-app/en/articles/11597864-how-to-pause-or-cut-off-your-habits) -- archive/pause feature design
- [EverHabit - Habit Tracking Analytics](https://everhabit.app/blog/habit-tracking-analytics) -- analytics frameworks and metric definitions
- [Cohorty - Science of Habit Tracking](https://www.cohorty.app/blog/the-complete-science-of-habit-tracking-and-measurement) -- measurement methodology
- [Pattrn - Best Habit Tracker with Analytics](https://pattrn.io/blog/the-best-habit-tracker-with-analytics-charts-data-analytics-and-ai) -- AI analytics leader
- [Zapier - Best Habit Tracker Apps](https://zapier.com/blog/best-habit-tracker-app/) -- feature comparison
- [Streaks App](https://streaksapp.com/) -- negative task type for breaking bad habits
- [Habitify Changelog](https://feedback.habitify.me/changelog) -- recent feature additions including checklist sub-tasks
- [Knack - Best Habit Tracker Apps 2026](https://www.knack.com/blog/best-habit-tracker-app/) -- market overview

---
*Feature research for: AI-Powered Habit Tracking*
*Researched: 2026-02-07*
