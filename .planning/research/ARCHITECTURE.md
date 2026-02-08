# Architecture Patterns

**Domain:** AI-powered habit tracking -- extending existing Clean Architecture backend
**Researched:** 2026-02-07

## Current Architecture Snapshot

Before recommending extensions, here is how the system works today:

```
API Layer (Controllers + JWT Auth)
  |
  v  MediatR Commands/Queries
Application Layer (Handlers)
  |
  v  IGenericRepository<T>, IUnitOfWork, IAiIntentService
Domain Layer (Entities, Result, Enums, Interfaces)
  ^
  |  Implementations
Infrastructure Layer (EF Core DbContext, GenericRepository, AI Services)
```

**Entities:** User, Habit (with HabitLog child collection), TaskItem (being removed)
**Patterns:** Factory methods with Result return, private setters, private constructors, CQRS via MediatR, GenericRepository with AsNoTracking reads, single UnitOfWork per request
**AI Flow:** User message -> ProcessUserChatCommand -> IAiIntentService.InterpretAsync (sends user context + habits/tasks) -> AiActionPlan -> execute actions -> save

## Recommended Architecture for New Features

### Design Principle: Extend, Do Not Restructure

The existing architecture is sound. The new features (sub-habits, bad habits, tags, progress metrics) integrate as domain extensions and new Application-layer handlers. No layer boundary changes needed.

### Component Boundaries

| Component | Responsibility | Communicates With |
|-----------|---------------|-------------------|
| **Habit (Entity)** | Core habit state, logging, activation, parent-child relationship | HabitLog, Tag (via HabitTag join) |
| **HabitLog (Entity)** | Individual log entries with date and value | Habit (parent) |
| **Tag (Entity)** | User-defined categorization labels | Habit (via HabitTag join), User (owner) |
| **HabitTag (Join Entity)** | Many-to-many link between Habit and Tag | Habit, Tag |
| **HabitProgressService (Domain Service)** | Computes streaks, completion rates, metrics from HabitLog data | Habit, HabitLog (read-only) |
| **ProcessUserChatCommand (Handler)** | AI intent routing, action execution | All repositories, IAiIntentService |
| **SystemPromptBuilder (Infra Service)** | Builds AI context with habit/tag/sub-habit info | Habit, Tag (read-only) |
| **Progress Queries (Handlers)** | Fetches and returns computed metrics | HabitProgressService, Repositories |
| **GenericRepository** | CRUD for all entities | OrbitDbContext |

### High-Level Component Diagram

```
Controllers
  |
  +-- HabitsController -----> CreateHabitCommand (+ ParentHabitId, IsNegative, TagIds)
  |                    -----> GetHabitsQuery (includes sub-habits, tags)
  |                    -----> GetHabitProgressQuery -> HabitProgressService
  |
  +-- TagsController -------> CreateTagCommand
  |                    -----> GetTagsQuery
  |                    -----> UpdateTagCommand / DeleteTagCommand
  |
  +-- ChatController -------> ProcessUserChatCommand (extended with tag/sub-habit actions)
  |
  +-- ProfileController ----> GetProfileQuery / UpdateProfileCommand
```

---

## Feature 1: Sub-Habits (Parent-Child Hierarchy)

### Data Model

Self-referencing one-to-many on the Habit entity. A habit optionally has a parent. A parent can have many children. Depth is limited to one level (parent -> children, no grandchildren) to keep complexity manageable.

```csharp
// Added to Habit entity
public Guid? ParentHabitId { get; private set; }
public Habit? ParentHabit { get; private set; }  // Navigation (not exposed via API)

private readonly List<Habit> _subHabits = [];
public IReadOnlyCollection<Habit> SubHabits => _subHabits.AsReadOnly();
```

### Domain Rules (enforced in Habit.Create and new methods)

1. A sub-habit cannot itself be a parent (depth = 1 max)
2. A sub-habit must belong to the same user as its parent
3. A sub-habit inherits frequency from parent by default but can override
4. Deactivating a parent deactivates all sub-habits
5. Deleting a parent cascades to sub-habits (via EF DeleteBehavior.Cascade)

### EF Configuration

```csharp
modelBuilder.Entity<Habit>(entity =>
{
    // Existing config...

    entity.HasOne(h => h.ParentHabit)
        .WithMany(h => h.SubHabits)
        .HasForeignKey(h => h.ParentHabitId)
        .IsRequired(false)
        .OnDelete(DeleteBehavior.Cascade);

    entity.HasIndex(h => h.ParentHabitId);
});
```

### Why This Approach

- Self-referencing FK is the standard EF Core pattern for parent-child. No separate SubHabit entity needed because sub-habits are habits (same fields, same logging behavior).
- One-level depth limit avoids recursive query complexity while satisfying the use case ("Exercise" -> "Push-ups", "Running", "Yoga").
- Cascade delete is safe here because orphaned sub-habits have no meaning.

### Query Impact

GetHabitsQuery must be updated to eager-load sub-habits for top-level habits. Use `.Include(h => h.SubHabits)` and filter to `ParentHabitId == null` for the top-level list, with sub-habits nested inside.

### AI Impact

The AiAction model needs an optional `ParentHabitId` field. SystemPromptBuilder shows sub-habits indented under parents. When creating a habit that sounds like a sub-category of an existing habit, the AI should set the parent.

---

## Feature 2: Bad Habits (Negative Tracking)

### Data Model

Add an `IsNegative` boolean to the Habit entity. This is simpler and more flexible than adding a new enum value to HabitType because the positive/negative dimension is orthogonal to Boolean/Quantifiable.

```csharp
// Added to Habit entity
public bool IsNegative { get; private set; }
```

### Why Boolean Instead of Extending HabitType

HabitType currently tracks *how* a habit is measured (Boolean vs. Quantifiable). Whether a habit is positive or negative tracks *intent*. These are orthogonal:
- "Smoke cigarettes" = Negative + Quantifiable (count per day)
- "Bite nails" = Negative + Boolean (did it or not)
- "Drink water" = Positive + Quantifiable
- "Meditate" = Positive + Boolean

A single boolean keeps the domain clean. No enum explosion.

### Domain Rules

1. Negative habits track slip-ups rather than completions
2. For negative habits, logging means "I slipped up" -- the value represents magnitude (e.g., 3 cigarettes)
3. Streaks for negative habits count consecutive days *without* a log (days clean)
4. Progress for negative habits is measured as "days since last slip" and "total slip-free days in period"
5. The Log method on Habit does not change -- logging is logging. The *interpretation* changes in HabitProgressService.

### Why No Separate "SlipLog" Entity

A slip-up log has the same structure as a positive log: date + optional value. Creating a separate entity would duplicate HabitLog. Instead, the same HabitLog entity serves both, and the `IsNegative` flag on the parent Habit drives how metrics are computed.

### AI Impact

AiAction gets an optional `IsNegative` boolean. SystemPromptBuilder explains the concept. The AI must distinguish "I smoked 3 cigarettes" (log a negative habit) from "I ran 5km" (log a positive habit).

---

## Feature 3: Tags

### Data Model

Tags are user-scoped entities with a many-to-many relationship to Habits via an explicit join entity.

```csharp
// New entity: Tag
public class Tag : Entity
{
    public Guid UserId { get; private set; }
    public string Name { get; private set; } = null!;
    public string? Color { get; private set; }  // Hex color for UI
    public DateTime CreatedAtUtc { get; private set; }

    private readonly List<HabitTag> _habitTags = [];
    public IReadOnlyCollection<HabitTag> HabitTags => _habitTags.AsReadOnly();

    private Tag() { }

    public static Result<Tag> Create(Guid userId, string name, string? color = null) { ... }
}

// Join entity: HabitTag
public class HabitTag : Entity
{
    public Guid HabitId { get; private set; }
    public Guid TagId { get; private set; }
    public Habit Habit { get; private set; } = null!;
    public Tag Tag { get; private set; } = null!;

    private HabitTag() { }

    internal static HabitTag Create(Guid habitId, Guid tagId) { ... }
}
```

### Why Explicit Join Entity Instead of Skip Navigation

EF Core supports implicit many-to-many (skip navigations), but an explicit HabitTag entity gives us:
- Consistency with the existing Entity base class pattern (all entities have Guid Id)
- Room to add metadata later (e.g., tagged-at timestamp)
- Clearer DbContext configuration
- Easier to work with in the GenericRepository pattern

### EF Configuration

```csharp
modelBuilder.Entity<Tag>(entity =>
{
    entity.HasIndex(t => new { t.UserId, t.Name }).IsUnique();
});

modelBuilder.Entity<HabitTag>(entity =>
{
    entity.HasIndex(ht => new { ht.HabitId, ht.TagId }).IsUnique();

    entity.HasOne(ht => ht.Habit)
        .WithMany()
        .HasForeignKey(ht => ht.HabitId)
        .OnDelete(DeleteBehavior.Cascade);

    entity.HasOne(ht => ht.Tag)
        .WithMany(t => t.HabitTags)
        .HasForeignKey(ht => ht.TagId)
        .OnDelete(DeleteBehavior.Cascade);
});
```

### Domain Rules

1. Tag names are unique per user (enforced at DB level via unique index)
2. A habit can have 0-N tags
3. A tag can be applied to 0-N habits
4. Deleting a tag removes all HabitTag associations (cascade)
5. Deleting a habit removes all HabitTag associations (cascade)
6. Tags have an optional color for UI rendering

### Query Patterns

- `GET /api/tags` -- all tags for current user
- `GET /api/habits?tag=fitness` -- filter habits by tag name
- Tags are included in habit responses via eager loading

### AI Impact

AiAction gets an optional `Tags` string array. When the AI creates a habit, it can suggest tags based on the habit description. SystemPromptBuilder includes the user's existing tags so the AI reuses them rather than creating duplicates.

---

## Feature 4: Progress Metrics

### Architecture: Domain Service, Not Entity Method

Progress computation involves reading collections of HabitLogs and applying algorithms (streak calculation, completion rate). This is a read-only computation that does not modify state. It belongs in a **Domain Service**, not on the Habit entity, because:

1. The Habit entity should not need to load all its logs to compute metrics
2. Progress calculation varies by habit type (boolean vs. quantifiable) and polarity (positive vs. negative)
3. Keeping computation separate makes it independently testable
4. The repository provides the log data; the domain service computes metrics

### Domain Service Interface

```csharp
// In Domain/Interfaces/
public interface IHabitProgressService
{
    HabitProgress CalculateProgress(
        Habit habit,
        IReadOnlyList<HabitLog> logs,
        DateOnly periodStart,
        DateOnly periodEnd);
}
```

### Progress Model (Value Object in Domain)

```csharp
// In Domain/Models/
public record HabitProgress
{
    public int CurrentStreak { get; init; }
    public int LongestStreak { get; init; }
    public decimal CompletionRate { get; init; }     // 0.0 to 1.0
    public int TotalLogs { get; init; }
    public int ExpectedLogs { get; init; }           // Based on frequency within period
    public decimal? AverageValue { get; init; }      // For quantifiable habits
    public decimal? TotalValue { get; init; }        // For quantifiable habits
    public DateOnly? LastLogDate { get; init; }
    // Negative habit specific
    public int? DaysSinceLastSlip { get; init; }     // For negative habits
    public int? SlipFreeDays { get; init; }          // For negative habits in period
}
```

### Streak Calculation Algorithm

For **positive** habits:
1. Sort logs by date descending
2. Walk backward from today, checking each expected date (based on frequency + days)
3. Current streak = consecutive expected dates with a matching log
4. Longest streak = max consecutive run in the full log history

For **negative** habits:
1. Current streak = days from last log to today (days since last slip)
2. Longest streak = largest gap between consecutive logs
3. If no logs exist, streak = days since habit creation

For **quantifiable** habits:
- Additionally compute average value, total value, and trend (comparing recent period to prior period)

### Implementation Location

The `IHabitProgressService` interface lives in `Domain/Interfaces/`. The implementation `HabitProgressService` lives in `Application/Services/` (it is pure computation, no infrastructure dependency, but it orchestrates domain logic above what a single entity should own). Alternatively, it can live in `Domain/Services/` as a true domain service since it has zero infrastructure dependencies -- this is the better choice for this project.

### Query Pattern

```csharp
public record GetHabitProgressQuery(
    Guid UserId,
    Guid HabitId,
    DateOnly? PeriodStart = null,
    DateOnly? PeriodEnd = null) : IRequest<Result<HabitProgress>>;
```

The handler fetches the habit and its logs from the repository, then delegates to `IHabitProgressService` for computation.

### Why Not Computed Columns or Materialized Views

Streaks and completion rates depend on frequency rules, day-of-week filters, and habit polarity -- business logic that does not belong in SQL. Computing in the domain service keeps the logic testable and the database simple. If performance becomes an issue at scale, a caching layer or periodic snapshot table can be added later without changing the domain model.

---

## Feature 5: User Profile

### Data Model

The User entity already has `Name`, `Email`, `CreatedAtUtc`, and `UpdateProfile` method. Extend with:

```csharp
// Added to User entity
public string? Timezone { get; private set; }      // IANA timezone (e.g., "Europe/London")
public string? AvatarUrl { get; private set; }     // URL or null
```

### Why Timezone on User

Streak calculations and "today" depend on the user's timezone. Currently the system uses `DateTime.UtcNow` everywhere. With timezone stored on the profile, the progress service can determine "today" correctly for each user. This is critical for streak accuracy.

### Commands/Queries

```
UpdateProfileCommand(UserId, Name?, Timezone?, AvatarUrl?)
GetProfileQuery(UserId) -> UserProfile DTO
```

---

## Data Flow Diagrams

### Flow 1: Creating a Habit with Sub-Habit and Tags (via Chat)

```
User: "I want to track exercise with sub-habits for running and yoga, tag it fitness"
  |
  v
ChatController.ProcessChat
  |
  v
ProcessUserChatCommand
  |
  v
IAiIntentService.InterpretAsync
  -> Returns AiActionPlan with 3 actions:
     1. CreateHabit: "Exercise" (parent, tags: ["fitness"])
     2. CreateHabit: "Running" (parentHabitId: <exercise-id>, tags: ["fitness"])
     3. CreateHabit: "Yoga" (parentHabitId: <exercise-id>, tags: ["fitness"])
  |
  v
ExecuteCreateHabitAsync (for each action)
  -> Habit.Create(..., parentHabitId, isNegative: false)
  -> For each tag name: find or create Tag, create HabitTag
  -> habitRepository.AddAsync(habit)
  |
  v
UnitOfWork.SaveChangesAsync (single transaction)
```

### Flow 2: Getting Habit Progress

```
GET /api/habits/{id}/progress?from=2026-01-01&to=2026-02-07
  |
  v
HabitsController.GetProgress
  |
  v
GetHabitProgressQuery(UserId, HabitId, PeriodStart, PeriodEnd)
  |
  v
GetHabitProgressQueryHandler
  |
  +-- habitRepository.GetByIdAsync(id)  -> Habit
  +-- habitLogRepository.FindAsync(l => l.HabitId == id && l.Date >= start && l.Date <= end)  -> logs
  |
  v
IHabitProgressService.CalculateProgress(habit, logs, start, end)
  -> Pure computation, no DB access
  -> Returns HabitProgress record
  |
  v
Return HabitProgress to controller -> 200 OK
```

### Flow 3: Logging a Negative Habit (Slip-Up)

```
User: "I smoked 3 cigarettes today"
  |
  v
AI interprets -> LogHabit action for existing "Smoking" habit (IsNegative: true), value: 3
  |
  v
ExecuteLogHabitAsync
  -> habit.Log(today, 3)  -- same method as positive habits
  -> The value 3 means "3 slip-ups" because habit.IsNegative is true
  |
  v
Later: GetHabitProgressQuery
  -> HabitProgressService sees IsNegative = true
  -> Streak = days since last log (days clean)
  -> CompletionRate inverted: fewer logs = better
```

---

## Patterns to Follow

### Pattern 1: Rich Domain Entity with Factory Method

**What:** All entity creation goes through a static `Create` method that returns `Result<T>`. Validation lives in the factory.
**When:** Always, for every new entity (Tag, HabitTag).
**Why:** Already established in the codebase. Keeps validation centralized. The private constructor prevents invalid state.

```csharp
public static Result<Tag> Create(Guid userId, string name, string? color = null)
{
    if (userId == Guid.Empty)
        return Result.Failure<Tag>("User ID is required.");
    if (string.IsNullOrWhiteSpace(name))
        return Result.Failure<Tag>("Tag name is required.");
    if (name.Length > 50)
        return Result.Failure<Tag>("Tag name must be 50 characters or less.");
    if (color is not null && !System.Text.RegularExpressions.Regex.IsMatch(color, @"^#[0-9A-Fa-f]{6}$"))
        return Result.Failure<Tag>("Color must be a valid hex color (e.g., #FF5733).");

    return Result.Success(new Tag
    {
        UserId = userId,
        Name = name.Trim(),
        Color = color,
        CreatedAtUtc = DateTime.UtcNow
    });
}
```

### Pattern 2: Domain Service for Cross-Entity Computation

**What:** When computation spans multiple entities or requires data the entity should not load, use a domain service.
**When:** Progress metrics, streak calculation, completion rates.
**Why:** Keeps entities focused on their own state. Avoids loading entire log collections into the Habit entity just to compute a number.

### Pattern 3: Extending AiAction for New Capabilities

**What:** Add optional nullable properties to `AiAction` for new fields. Update `AiActionType` enum for new action types. Update `SystemPromptBuilder` to document new capabilities.
**When:** Each feature that the AI needs to interact with.
**Why:** The existing AI architecture is additive -- new fields do not break existing actions because they are all optional.

### Pattern 4: Eager Loading for Related Data

**What:** Use `.Include()` in repository queries when related entities are needed.
**When:** Loading habits with their sub-habits and tags.
**Why:** The current GenericRepository uses `AsNoTracking()` which means navigation properties are not loaded by default. The repository needs methods that support includes.

**Repository Extension Needed:**
```csharp
// Add to IGenericRepository<T>
Task<IReadOnlyList<T>> FindAsync(
    Expression<Func<T, bool>> predicate,
    Func<IQueryable<T>, IQueryable<T>>? include = null,
    CancellationToken cancellationToken = default);
```

This allows callers to pass `.Include(h => h.SubHabits).Include(h => h.HabitTags).ThenInclude(ht => ht.Tag)` without breaking the repository abstraction.

---

## Anti-Patterns to Avoid

### Anti-Pattern 1: Habit Entity Computing Its Own Metrics

**What:** Adding `GetCurrentStreak()` or `GetCompletionRate()` methods to the Habit entity.
**Why bad:** Forces the entity to load all its HabitLogs (potentially thousands). Violates the principle of entities managing their own state, not aggregating read models. The Habit entity already holds `_logs` for the Log method, but those are for adding -- not for analytics queries.
**Instead:** Use `IHabitProgressService` domain service that receives pre-loaded logs.

### Anti-Pattern 2: Separate SubHabit Entity

**What:** Creating a `SubHabit` class that duplicates all Habit fields.
**Why bad:** Sub-habits ARE habits. They can be logged, have types, frequencies. Duplicating the entity creates a maintenance nightmare and breaks the GenericRepository pattern.
**Instead:** Self-referencing FK on Habit with `ParentHabitId`.

### Anti-Pattern 3: Implicit Many-to-Many for Tags

**What:** Using EF Core's skip navigation (`List<Tag>` on Habit, `List<Habit>` on Tag, no join entity).
**Why bad:** The existing codebase uses Entity base class with Guid Id for everything. Skip navigations hide the join table, making it harder to add metadata later and inconsistent with the repository pattern.
**Instead:** Explicit `HabitTag` join entity.

### Anti-Pattern 4: Computing Metrics in the Database

**What:** SQL views or stored procedures for streak/completion calculation.
**Why bad:** Business logic in SQL is untestable, hard to change, and hard to debug. The frequency rules (every 2 weeks, specific days) make SQL computation extremely complex.
**Instead:** Domain service with pure C# logic. Optimize with caching only if needed.

### Anti-Pattern 5: Overloading HabitType Enum

**What:** Adding `Negative`, `NegativeBoolean`, `NegativeQuantifiable` to HabitType.
**Why bad:** Conflates two orthogonal dimensions (measurement type and intent). Leads to 2xN enum values as more types are added.
**Instead:** Separate `IsNegative` boolean on Habit.

---

## Scalability Considerations

| Concern | At 100 users | At 10K users | At 1M users |
|---------|--------------|--------------|-------------|
| Streak calculation | Compute on request, no caching | Compute on request, consider caching hot users | Periodic snapshot table, cache aggressively |
| Sub-habit loading | Eager load with Include | Same, single query with join | Same, indexed FK makes this fast |
| Tag filtering | In-memory LINQ is fine | DB-level filtering with indexed join table | Same, composite index on HabitTag |
| HabitLog volume | ~365 logs/user/year | ~3.6M total logs | ~365M total logs, partition by date |
| Progress queries | Direct computation | Add response caching (5 min TTL) | Pre-compute daily, serve from cache/snapshot |

### Index Strategy

The following indexes should be added for the new features:

```
Habit.ParentHabitId                      -- sub-habit lookups
Tag(UserId, Name) UNIQUE                 -- prevent duplicate tags per user
HabitTag(HabitId, TagId) UNIQUE          -- prevent duplicate tag assignments
HabitTag(TagId)                          -- reverse lookup: "all habits with tag X"
HabitLog(HabitId, Date)                  -- already exists, critical for streak calculation
```

---

## Database Schema Changes Summary

```sql
-- Habit table additions
ALTER TABLE "Habits" ADD COLUMN "ParentHabitId" uuid REFERENCES "Habits"("Id") ON DELETE CASCADE;
ALTER TABLE "Habits" ADD COLUMN "IsNegative" boolean NOT NULL DEFAULT false;

-- New Tag table
CREATE TABLE "Tags" (
    "Id" uuid PRIMARY KEY,
    "UserId" uuid NOT NULL REFERENCES "Users"("Id"),
    "Name" text NOT NULL,
    "Color" text,
    "CreatedAtUtc" timestamp with time zone NOT NULL
);
CREATE UNIQUE INDEX "IX_Tags_UserId_Name" ON "Tags" ("UserId", "Name");

-- New HabitTag join table
CREATE TABLE "HabitTags" (
    "Id" uuid PRIMARY KEY,
    "HabitId" uuid NOT NULL REFERENCES "Habits"("Id") ON DELETE CASCADE,
    "TagId" uuid NOT NULL REFERENCES "Tags"("Id") ON DELETE CASCADE
);
CREATE UNIQUE INDEX "IX_HabitTags_HabitId_TagId" ON "HabitTags" ("HabitId", "TagId");
CREATE INDEX "IX_HabitTags_TagId" ON "HabitTags" ("TagId");

-- User table additions
ALTER TABLE "Users" ADD COLUMN "Timezone" text;
ALTER TABLE "Users" ADD COLUMN "AvatarUrl" text;
```

Note: Since the project uses `EnsureCreated()` (no migrations), these changes apply via the DbContext model. When/if migrations are adopted, these become migration steps.

---

## Suggested Build Order

Features have the following dependency chain:

```
1. IsNegative on Habit        (no dependencies)
2. Tags + HabitTag            (no dependencies)
3. Sub-Habits (ParentHabitId) (no dependencies, but benefits from tags being done first)
4. User Profile (Timezone)    (no dependencies)
5. Progress Metrics Service   (depends on 1 for negative habit logic, depends on 4 for timezone)
6. AI Integration updates     (depends on 1, 2, 3 all being done -- prompts reference all)
```

**Recommended phase grouping:**

- **Phase A: Domain Model Extensions** -- Add IsNegative to Habit, create Tag and HabitTag entities, add ParentHabitId to Habit, extend User with Timezone. This is all domain + infrastructure (DbContext config). No application logic yet except basic CRUD commands/queries.

- **Phase B: CRUD Operations** -- TagsController with CreateTag, GetTags, DeleteTag. Extended CreateHabitCommand accepting ParentHabitId, IsNegative, TagIds. Extended GetHabitsQuery with Include for sub-habits and tags. UpdateProfileCommand with timezone.

- **Phase C: Progress Metrics** -- IHabitProgressService domain service, GetHabitProgressQuery, streak/completion algorithms. This is the most algorithmically complex phase but has clear inputs/outputs for testing.

- **Phase D: AI Integration** -- Extend AiAction model, update SystemPromptBuilder, update ProcessUserChatCommand to handle tag resolution and sub-habit creation. Update AI prompt examples. This is last because it touches the most surface area and benefits from all other features being stable.

---

## Sources

- [EF Core Self-Referencing Hierarchy (Medium - Dmitry Pavlov)](https://medium.com/@dmitry.pavlov/tree-structure-in-ef-core-how-to-configure-a-self-referencing-table-and-use-it-53effad60bf)
- [Self-Referencing Relationships in EF Core (Dot Net Tutorials)](https://dotnettutorials.net/lesson/self-referencing-relationship-in-entity-framework-core/)
- [EF Core Many-to-Many Relationships (Microsoft Learn)](https://learn.microsoft.com/en-us/ef/core/modeling/relationships/many-to-many)
- [Self-referencing hierarchy querying (dotnet/efcore #3241)](https://github.com/dotnet/efcore/issues/3241)
- [Negative habit tracking patterns (HabitBoard)](https://habitboard.app/negative-habits/)
- [Streak calculation patterns (MyTimeCalculator)](https://mytimecalculator.com/habit-tracker-calculator)
- [Best habit tracker apps 2026 (Reclaim)](https://reclaim.ai/blog/habit-tracker-apps)
