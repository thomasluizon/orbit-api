# Phase 2: Habit Domain Extensions - Research

**Researched:** 2026-02-07
**Domain:** Domain modeling (sub-habits, negative habits, tags, notes, timezone), EF Core migrations, Clean Architecture CQRS
**Confidence:** HIGH

## Summary

Phase 2 extends the Orbit habit domain model with five distinct feature groups: sub-habits (parent-child habit relationships), negative/bad habits (slip-up tracking with "days since" calculation), habit log notes, tags (with color and many-to-many assignment), and user timezone support. All features require schema changes via EF Core migrations (established in Phase 1), new domain entities, new CQRS commands/queries, new API endpoints, and updates to the AI system prompt.

The existing codebase is well-structured with clear patterns: factory methods with Result pattern on entities, MediatR commands/queries, FluentValidation validators, GenericRepository with UnitOfWork, and controller request records. Every new feature should follow these established patterns exactly. The primary technical challenges are: (1) deciding whether sub-habits use a self-referencing Habit table or a separate SubHabit entity, (2) configuring the Tag-Habit many-to-many with an explicit join entity (HabitTag), and (3) storing IANA timezone IDs on the User entity for cross-platform date calculations.

**Primary recommendation:** Use a separate SubHabit entity (not self-referencing Habit) to keep the Habit entity simple, use an explicit HabitTag join entity for the Tag-Habit many-to-many, store IANA timezone strings on User, and add a `Note` property to HabitLog. Add a `IsNegative` boolean to Habit with a computed "days since last slip" query -- no new entity needed.

## Standard Stack

### Core (already in project)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Microsoft.EntityFrameworkCore | 10.0.2 | ORM, migrations, relationships | Already in use, handles all schema changes |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.0 | PostgreSQL provider | Already in use |
| MediatR | 14.0.0 | CQRS command/query dispatch | Already in use |
| FluentValidation | 12.1.1 | Input validation pipeline | Already in use |

### Supporting (already in project)
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| System.Text.Json | Built-in (.NET 10) | JSON serialization for AI responses | Already in use for AiAction deserialization |
| System.TimeZoneInfo | Built-in (.NET 10) | IANA timezone conversion | For PROF-01 timezone support -- no external package needed |

### No New Packages Required

All Phase 2 features can be implemented with the existing package set. Specifically:
- **Timezone handling:** .NET 6+ (and therefore .NET 10) has built-in IANA timezone support via `TimeZoneInfo.FindSystemTimeZoneById("America/New_York")` -- no need for TimeZoneConverter NuGet package.
- **Tag colors:** Store as string (hex format like `#FF5733`) -- no color library needed.
- **Sub-habits:** Standard EF Core one-to-many relationship -- no special packages.

## Architecture Patterns

### Entity Design Decisions

#### Sub-Habits: Separate Entity (NOT self-referencing)

**Decision:** Create a new `SubHabit` entity with a foreign key to `Habit`, rather than making Habit self-referencing.

**Rationale:**
- The existing `Habit` entity has 15+ properties (Title, Description, FrequencyUnit, FrequencyQuantity, Type, Unit, TargetValue, IsActive, Days, Logs, etc.)
- Sub-habits are simpler: they have a title, a boolean completion status per log, and belong to a parent habit
- Self-referencing would require `ParentHabitId?` nullable FK, making every query check "is this a parent or child?" -- unnecessary complexity
- Sub-habits don't have their own frequency, type, or target value -- they inherit from the parent
- The requirement says "checklist" -- sub-habits are items within a parent, not standalone habits

**Entity structure:**
```csharp
public class SubHabit : Entity
{
    public Guid HabitId { get; private set; }        // FK to parent Habit
    public string Title { get; private set; }
    public int SortOrder { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTime CreatedAtUtc { get; private set; }
}
```

**Sub-habit logging:** When logging a parent habit that has sub-habits, the user logs individual sub-habits. Create a `SubHabitLog` entity:
```csharp
public class SubHabitLog : Entity
{
    public Guid SubHabitId { get; private set; }     // FK to SubHabit
    public Guid HabitLogId { get; private set; }     // FK to parent HabitLog (groups sub-habit logs by date)
    public bool IsCompleted { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
}
```

Alternatively (simpler): Log sub-habits directly to a `SubHabitLog` with just `SubHabitId` + `Date` + `IsCompleted`, without requiring a parent HabitLog. This is simpler because the parent habit's log entry can be derived: if at least one sub-habit is logged for a date, the parent is "partially done" for that date.

**Recommended simpler approach:** SubHabitLog has `SubHabitId`, `DateOnly Date`, and `bool IsCompleted`. No FK to HabitLog. The parent habit's completion for a date = all sub-habits completed for that date.

#### Negative Habits: Boolean Flag on Existing Habit Entity

**Decision:** Add `bool IsNegative` property to Habit entity, not a separate entity or enum extension.

**Rationale:**
- A negative habit IS a habit -- it has title, description, frequency, etc.
- The only behavioral difference is display/interpretation: logging means "I slipped up" instead of "I completed it"
- "Days since last slip" is a computed query, not stored data -- it's `(today - most_recent_log_date).Days`
- The existing `Habit.Log()` method works as-is for recording slip-ups
- HabitType enum (Boolean/Quantifiable) remains orthogonal -- a negative habit can be boolean ("I smoked") or quantifiable ("I smoked 3 cigarettes")

**Implementation:**
```csharp
// In Habit entity, add:
public bool IsNegative { get; private set; }

// In Habit.Create factory, add parameter:
bool isNegative = false

// "Days since last slip" query (not stored):
// daysSinceLastSlip = logs.Count == 0
//     ? (today - habit.CreatedAtUtc).Days
//     : (today - logs.Max(l => l.Date)).Days
```

#### Habit Log Notes: Add Property to Existing HabitLog Entity

**Decision:** Add `string? Note` to HabitLog entity.

**Rationale:**
- HABIT-05 says "optional text notes when logging a habit"
- This is a simple nullable string on the existing HabitLog
- No new entity needed
- The `Habit.Log()` method gets an additional `string? note = null` parameter
- `HabitLog.Create()` gets the same parameter

**Implementation:**
```csharp
// In HabitLog, add:
public string? Note { get; private set; }

// Update HabitLog.Create:
internal static HabitLog Create(Guid habitId, DateOnly date, decimal value, string? note = null)
```

#### Tags: Separate Tag Entity + HabitTag Join Entity

**Decision:** Use explicit `Tag` entity and `HabitTag` join entity (as specified in roadmap prior decisions).

**Rationale:**
- Roadmap explicitly decided: "Tags use Tag entity + HabitTag join table (not text[] array) for color metadata support"
- Tags have `Name` and `Color` properties -- needs its own entity
- HabitTag is the many-to-many join -- could have payload like `AssignedAtUtc` but not required
- EF Core handles this with `UsingEntity<HabitTag>` in OnModelCreating

**Entity structures:**
```csharp
public class Tag : Entity
{
    public Guid UserId { get; private set; }         // Tags are per-user
    public string Name { get; private set; }
    public string Color { get; private set; }        // Hex string like "#FF5733"
    public DateTime CreatedAtUtc { get; private set; }
}

public class HabitTag  // NOT extending Entity -- uses composite key
{
    public Guid HabitId { get; private set; }
    public Guid TagId { get; private set; }
}
```

**EF Core configuration:**
```csharp
modelBuilder.Entity<Habit>()
    .HasMany(h => h.Tags)
    .WithMany(t => t.Habits)
    .UsingEntity<HabitTag>(
        r => r.HasOne<Tag>().WithMany().HasForeignKey(ht => ht.TagId),
        l => l.HasOne<Habit>().WithMany().HasForeignKey(ht => ht.HabitId),
        j => j.HasKey(ht => new { ht.HabitId, ht.TagId }));
```

#### User Timezone: String Property on User Entity

**Decision:** Add `string? TimeZone` to User entity, storing IANA timezone ID (e.g., "America/New_York").

**Rationale:**
- PROF-01 says "User can set their timezone for correct streak/metric calculation"
- IANA timezone IDs are the cross-platform standard (.NET 6+ supports them natively)
- `TimeZoneInfo.FindSystemTimeZoneById("America/New_York")` works on Windows AND Linux in .NET 10
- Nullable because existing users won't have it set -- default to UTC when null
- Stored as plain string in PostgreSQL (text column)
- Validation: `TimeZoneInfo.FindSystemTimeZoneById()` throws `TimeZoneNotFoundException` for invalid IDs

**Implementation:**
```csharp
// In User entity, add:
public string? TimeZone { get; private set; }

public Result SetTimeZone(string ianaTimeZoneId)
{
    try
    {
        TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZoneId);
        TimeZone = ianaTimeZoneId;
        return Result.Success();
    }
    catch (TimeZoneNotFoundException)
    {
        return Result.Failure($"Invalid timezone: {ianaTimeZoneId}");
    }
}
```

### Recommended Project Structure Changes

```
src/Orbit.Domain/
  Entities/
    Habit.cs           # Add: IsNegative, SubHabits nav, Tags nav
    HabitLog.cs        # Add: Note property
    SubHabit.cs        # NEW
    SubHabitLog.cs     # NEW
    Tag.cs             # NEW
    HabitTag.cs        # NEW (join entity, no Entity base)
    User.cs            # Add: TimeZone property

src/Orbit.Application/
  Habits/
    Commands/
      CreateHabitCommand.cs     # Update: add IsNegative, SubHabits params
      LogHabitCommand.cs        # Update: add Note param
      LogSubHabitCommand.cs     # NEW
      AddSubHabitCommand.cs     # NEW
      RemoveSubHabitCommand.cs  # NEW
    Queries/
      GetHabitsQuery.cs         # Update: include SubHabits, Tags in response
      GetHabitByIdQuery.cs      # NEW: single habit with full detail
    Validators/
      CreateHabitCommandValidator.cs  # Update: validate sub-habits
      LogHabitCommandValidator.cs     # Update: validate note length
  Tags/                         # NEW folder
    Commands/
      CreateTagCommand.cs       # NEW
      DeleteTagCommand.cs       # NEW
      AssignTagCommand.cs       # NEW
      UnassignTagCommand.cs     # NEW
    Queries/
      GetTagsQuery.cs           # NEW
    Validators/
      CreateTagCommandValidator.cs  # NEW
      AssignTagCommandValidator.cs  # NEW
  Profile/                      # NEW folder
    Commands/
      SetTimezoneCommand.cs     # NEW
    Queries/
      GetProfileQuery.cs        # NEW
    Validators/
      SetTimezoneCommandValidator.cs  # NEW

src/Orbit.Api/
  Controllers/
    HabitsController.cs   # Update: new endpoints for sub-habits, notes, filtering
    TagsController.cs     # NEW
    ProfileController.cs  # NEW

src/Orbit.Infrastructure/
  Persistence/
    OrbitDbContext.cs      # Update: new DbSets, OnModelCreating config
  Migrations/
    [timestamp]_AddSubHabitsAndNegativeHabits.cs  # NEW
    [timestamp]_AddTagsAndHabitTags.cs            # NEW
    [timestamp]_AddHabitLogNotesAndUserTimezone.cs # NEW
  Services/
    SystemPromptBuilder.cs  # Update: include sub-habits, negative habits, tags
```

### API Endpoint Design

**New/Updated Endpoints:**

```
# Sub-habits
POST   /api/habits                       # Updated: accepts subHabits[] in body
POST   /api/habits/{id}/sub-habits       # Add sub-habit to existing habit
DELETE /api/habits/{id}/sub-habits/{subId}  # Remove sub-habit
POST   /api/habits/{id}/log              # Updated: accepts note, subHabitCompletions[]

# Tags
GET    /api/tags                         # List user's tags
POST   /api/tags                         # Create tag
DELETE /api/tags/{id}                    # Delete tag
POST   /api/habits/{id}/tags/{tagId}     # Assign tag to habit
DELETE /api/habits/{id}/tags/{tagId}     # Remove tag from habit
GET    /api/habits?tags=id1,id2          # Filter habits by tags (query param)

# Profile
GET    /api/profile                      # Get user profile (name, email, timezone)
PUT    /api/profile/timezone             # Set timezone
```

### Pattern: Follow Existing CQRS Flow

Every new feature follows the same pattern visible in the codebase:

1. **Domain Entity** with private setters, factory `Create()` method returning `Result<T>`, and domain methods
2. **MediatR Command/Query** record implementing `IRequest<Result<T>>`
3. **Handler** class using constructor-injected `IGenericRepository<T>` and `IUnitOfWork`
4. **FluentValidation Validator** class extending `AbstractValidator<TCommand>`
5. **Controller Action** mapping HTTP request record to command, returning Ok/BadRequest/CreatedAtAction

### Anti-Patterns to Avoid

- **Don't add TagId[] to Habit entity:** Tags are a separate concern -- use the join table. Don't store tag IDs as a PostgreSQL array on Habit.
- **Don't make SubHabit extend Habit:** Sub-habits are simpler than habits. They don't have frequency, type, or target value.
- **Don't store "days since last slip" in the database:** This is a computed value that changes daily. Calculate it in the query handler.
- **Don't use Windows timezone IDs:** Use IANA IDs ("America/New_York" not "Eastern Standard Time") for cross-platform compatibility.
- **Don't skip validation on timezone strings:** Always verify with `TimeZoneInfo.FindSystemTimeZoneById()` before storing.
- **Don't use `habitRepository.Update(habit)` after adding child entities:** EF Core change tracking handles it automatically (known gotcha from MEMORY.md).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Timezone conversion | Custom UTC offset storage/lookup | `TimeZoneInfo.FindSystemTimeZoneById(ianaId)` | .NET 10 handles IANA IDs natively, including DST transitions |
| Timezone validation | Regex or hardcoded timezone list | `TimeZoneInfo.FindSystemTimeZoneById()` throws on invalid | IANA database updates with OS/ICU updates |
| Many-to-many join table | Manual SQL or custom repository method | EF Core `UsingEntity<HabitTag>()` fluent config | EF Core generates correct schema, handles FK constraints |
| "Days since last slip" | Stored computed column or background job | LINQ query: `(today - logs.Max(l => l.Date)).Days` | Changes daily, trivial to compute at read time |
| Migration generation | Manual SQL scripts | `dotnet ef migrations add [Name]` | EF Core diff engine handles schema changes correctly |

**Key insight:** Every feature in this phase uses standard EF Core capabilities and .NET BCL types. No external packages, no custom infrastructure.

## Common Pitfalls

### Pitfall 1: GenericRepository + AsNoTracking + Navigation Properties
**What goes wrong:** The existing `FindAsync` uses `AsNoTracking()`, which means navigation properties (SubHabits, Tags, Logs) won't be loaded. Calling `.Include()` on an `AsNoTracking()` query works, but the returned entities are detached -- you can't modify them and save.
**Why it happens:** The GenericRepository was designed for simple queries. Adding sub-habits/tags requires eager loading.
**How to avoid:** For queries that need navigation properties (GetHabits with tags/sub-habits), either:
  - Add a new repository method like `FindWithIncludesAsync` that supports `.Include()` chains
  - Or use the DbContext directly in the query handler (acceptable in Clean Architecture -- the handler is in Application layer, DbContext is injected)
  - Or extend `IGenericRepository<T>` with an overload that accepts `Func<IQueryable<T>, IQueryable<T>> includes`
**Warning signs:** Empty SubHabits/Tags collections in API responses even though data exists in DB.

### Pitfall 2: Cascade Delete with Sub-Habits
**What goes wrong:** Deleting a Habit should cascade-delete its SubHabits, SubHabitLogs, and HabitTags. If not configured, deleting a habit with sub-habits throws FK constraint violation.
**Why it happens:** EF Core defaults vary by relationship type. Self-referencing relationships default to `Restrict`, not `Cascade`.
**How to avoid:** Explicitly configure `OnDelete(DeleteBehavior.Cascade)` for Habit -> SubHabit, SubHabit -> SubHabitLog, and Habit -> HabitTag relationships.
**Warning signs:** `DbUpdateException` with "violates foreign key constraint" when deleting habits.

### Pitfall 3: HabitTag Composite Key vs Entity Base Class
**What goes wrong:** The `Entity` base class has `public Guid Id { get; init; } = Guid.NewGuid()`. If HabitTag extends Entity, it gets an auto-generated Id that's never used -- the real key is composite (HabitId, TagId).
**Why it happens:** The codebase convention is to extend Entity for all domain objects.
**How to avoid:** HabitTag should NOT extend Entity. It should be a plain class with only `HabitId` and `TagId`. Configure composite key in OnModelCreating: `j.HasKey(ht => new { ht.HabitId, ht.TagId })`.
**Warning signs:** Unnecessary `Id` column in HabitTags table, or EF Core trying to use `Id` as PK instead of composite.

### Pitfall 4: Migration Order Matters
**What goes wrong:** Creating all schema changes in one migration makes it harder to debug issues. If migration fails partway, rollback is all-or-nothing.
**Why it happens:** Desire to minimize migration count.
**How to avoid:** Create separate migrations for logically independent changes. Recommended order:
  1. SubHabits + IsNegative + HabitLog.Note (habit entity extensions)
  2. Tags + HabitTag (new entity group)
  3. User.TimeZone (profile extension)
**Warning signs:** Migration with 10+ table changes that's hard to review.

### Pitfall 5: Timezone-Aware Date Calculations
**What goes wrong:** The current `Habit.Log()` method uses `DateOnly` but `ProcessUserChatCommand` uses `DateOnly.FromDateTime(DateTime.UtcNow)` -- this is the UTC date, not the user's local date. A user in UTC-8 logging at 11pm local time gets tomorrow's UTC date.
**Why it happens:** No timezone awareness exists in the current codebase.
**How to avoid:** When PROF-01 adds timezone support, all date calculations in chat command and log commands must convert UTC to user's local date. The `LogHabitCommand` already accepts an explicit `DateOnly Date`, so the API caller can provide the correct local date. The AI chat path needs to use the user's timezone to determine "today."
**Warning signs:** Logs appearing on wrong date for users not in UTC timezone.

### Pitfall 6: Existing Boolean Habit Duplicate Log Check
**What goes wrong:** `Habit.Log()` checks `_logs.Exists(l => l.Date == date)` for Boolean habits to prevent duplicate logs. But for negative habits, users might slip up multiple times per day. This check would incorrectly prevent multiple slip-up logs.
**Why it happens:** The duplicate check was designed for positive boolean habits only.
**How to avoid:** Only enforce the "one log per date" rule for non-negative boolean habits: `if (Type == HabitType.Boolean && !IsNegative && _logs.Exists(l => l.Date == date))`.
**Warning signs:** Users can't log multiple slip-ups for a negative boolean habit on the same day.

## Code Examples

### Pattern: New Entity with Factory Method (following existing convention)

```csharp
// Source: Follows pattern from Habit.cs and HabitLog.cs
public class Tag : Entity
{
    public Guid UserId { get; private set; }
    public string Name { get; private set; } = null!;
    public string Color { get; private set; } = null!; // Hex: "#FF5733"
    public DateTime CreatedAtUtc { get; private set; }

    // Navigation for skip navigation (optional, for filtering)
    public ICollection<Habit> Habits { get; private set; } = [];

    private Tag() { }

    public static Result<Tag> Create(Guid userId, string name, string color)
    {
        if (userId == Guid.Empty)
            return Result.Failure<Tag>("User ID is required.");
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<Tag>("Tag name is required.");
        if (string.IsNullOrWhiteSpace(color) || !IsValidHexColor(color))
            return Result.Failure<Tag>("Tag color must be a valid hex color (e.g., #FF5733).");

        return Result.Success(new Tag
        {
            UserId = userId,
            Name = name.Trim(),
            Color = color.Trim().ToUpperInvariant(),
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    private static bool IsValidHexColor(string color)
    {
        var trimmed = color.Trim();
        if (trimmed.Length != 7 || trimmed[0] != '#') return false;
        return trimmed[1..].All(c => "0123456789ABCDEFabcdef".Contains(c));
    }
}
```

### Pattern: New CQRS Command (following existing convention)

```csharp
// Source: Follows pattern from CreateHabitCommand.cs
public record CreateTagCommand(
    Guid UserId,
    string Name,
    string Color) : IRequest<Result<Guid>>;

public class CreateTagCommandHandler(
    IGenericRepository<Tag> tagRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<CreateTagCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateTagCommand request, CancellationToken cancellationToken)
    {
        var tagResult = Tag.Create(request.UserId, request.Name, request.Color);
        if (tagResult.IsFailure)
            return Result.Failure<Guid>(tagResult.Error);

        await tagRepository.AddAsync(tagResult.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(tagResult.Value.Id);
    }
}
```

### Pattern: Timezone Conversion for Date Calculations

```csharp
// Source: .NET BCL TimeZoneInfo (verified in .NET 6+ docs)
// Convert UTC "now" to user's local date
public static DateOnly GetUserLocalDate(string? userTimeZone)
{
    if (string.IsNullOrEmpty(userTimeZone))
        return DateOnly.FromDateTime(DateTime.UtcNow);

    var tz = TimeZoneInfo.FindSystemTimeZoneById(userTimeZone); // Accepts IANA IDs in .NET 6+
    var localTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
    return DateOnly.FromDateTime(localTime);
}
```

### Pattern: EF Core OnModelCreating for New Entities

```csharp
// Source: Follows pattern from existing OrbitDbContext.cs
modelBuilder.Entity<SubHabit>(entity =>
{
    entity.HasOne<Habit>()
        .WithMany(h => h.SubHabits)
        .HasForeignKey(sh => sh.HabitId)
        .OnDelete(DeleteBehavior.Cascade);

    entity.HasIndex(sh => new { sh.HabitId, sh.SortOrder });
});

modelBuilder.Entity<Tag>(entity =>
{
    entity.HasIndex(t => new { t.UserId, t.Name }).IsUnique();
});

modelBuilder.Entity<Habit>()
    .HasMany(h => h.Tags)
    .WithMany(t => t.Habits)
    .UsingEntity<HabitTag>(
        r => r.HasOne<Tag>().WithMany().HasForeignKey(ht => ht.TagId)
            .OnDelete(DeleteBehavior.Cascade),
        l => l.HasOne<Habit>().WithMany().HasForeignKey(ht => ht.HabitId)
            .OnDelete(DeleteBehavior.Cascade),
        j => j.HasKey(ht => new { ht.HabitId, ht.TagId }));
```

### Pattern: Query with Includes (extending GenericRepository)

```csharp
// Option: Add to IGenericRepository<T>
Task<IReadOnlyList<T>> FindAsync(
    Expression<Func<T, bool>> predicate,
    Func<IQueryable<T>, IQueryable<T>>? includes = null,
    CancellationToken cancellationToken = default);

// Implementation in GenericRepository<T>
public async Task<IReadOnlyList<T>> FindAsync(
    Expression<Func<T, bool>> predicate,
    Func<IQueryable<T>, IQueryable<T>>? includes = null,
    CancellationToken cancellationToken = default)
{
    IQueryable<T> query = _dbSet.AsNoTracking();
    if (includes is not null)
        query = includes(query);
    return await query.Where(predicate).ToListAsync(cancellationToken);
}

// Usage in GetHabitsQueryHandler
var habits = await habitRepository.FindAsync(
    h => h.UserId == request.UserId && h.IsActive,
    q => q.Include(h => h.SubHabits)
          .Include(h => h.Tags),
    cancellationToken);
```

### Pattern: Filter Habits by Tags

```csharp
// In GetHabitsQuery, add optional tag filter
public record GetHabitsQuery(
    Guid UserId,
    IReadOnlyList<Guid>? TagIds = null) : IRequest<IReadOnlyList<Habit>>;

// In handler, apply filter
var query = dbContext.Habits
    .AsNoTracking()
    .Include(h => h.SubHabits.Where(sh => sh.IsActive).OrderBy(sh => sh.SortOrder))
    .Include(h => h.Tags)
    .Where(h => h.UserId == request.UserId && h.IsActive);

if (request.TagIds is { Count: > 0 })
{
    query = query.Where(h => h.Tags.Any(t => request.TagIds.Contains(t.Id)));
}

return await query.ToListAsync(cancellationToken);
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Windows timezone IDs | IANA timezone IDs (cross-platform) | .NET 6 (2021) | Use "America/New_York" not "Eastern Standard Time" |
| TimeZoneConverter NuGet | Built-in `TimeZoneInfo.FindSystemTimeZoneById` | .NET 6 (2021) | No external package needed for IANA support |
| Implicit many-to-many (EF Core 5+) | Explicit join entity with `UsingEntity<T>` | EF Core 5 (2020) | Required when join table needs payload or explicit control |
| `EnsureCreated()` for schema | EF Core Migrations | Phase 1 (just completed) | Already migrated -- use `dotnet ef migrations add` going forward |

**Deprecated/outdated:**
- `TimeZoneConverter` NuGet package: Not needed for .NET 6+ projects that use ICU (which .NET 10 does by default)
- Windows-only timezone IDs: Avoid `FindSystemTimeZoneById("Eastern Standard Time")` -- use IANA IDs for portability

## Open Questions

1. **Sub-habit log granularity: per sub-habit or batch?**
   - What we know: Requirement says "log individual sub-habits within a parent habit"
   - What's unclear: Should the API accept a batch of sub-habit completions in one request (e.g., `POST /api/habits/{id}/log` with `subHabitCompletions: [{subHabitId, isCompleted}]`), or should each sub-habit be logged individually?
   - Recommendation: Accept a batch in the existing log endpoint. This matches the "checklist" UX -- user checks off multiple sub-habits at once. The endpoint already exists, just extend the request body.

2. **Negative habit: allow multiple logs per day?**
   - What we know: Current code prevents duplicate boolean logs per date. Negative habits track slip-ups.
   - What's unclear: Can a user slip up multiple times per day?
   - Recommendation: Allow multiple logs per day for negative habits. A user might smoke multiple cigarettes. The "days since last slip" calculation uses the most recent log date regardless.

3. **Tag uniqueness scope: per-user or global?**
   - What we know: Tags have a name and color. Users create their own tags.
   - What's unclear: Should tag names be unique per user? Can two users have a tag named "Health"?
   - Recommendation: Unique per user (unique index on UserId + Name). Each user manages their own tag namespace.

4. **GetHabitsQuery: include logs in response?**
   - What we know: Current GetHabitsQuery returns habits via `FindAsync` with `AsNoTracking()` -- logs are NOT included (no `.Include(h => h.Logs)`).
   - What's unclear: For negative habits showing "days since last slip," does the list endpoint need to include the most recent log? That requires either including all logs or a separate computed field.
   - Recommendation: Don't include all logs in the list response. Instead, add a computed `DaysSinceLastSlip` field to the response DTO for negative habits, calculated in the query handler with a targeted sub-query.

5. **AI system prompt updates: scope for Phase 2?**
   - What we know: The AI can currently create habits and log habits. Phase 2 adds sub-habits, negative habits, tags, and notes.
   - What's unclear: Should the AI be taught to create sub-habits, assign tags, and add notes in Phase 2? Or is that deferred to Phase 3 (AI Enhancement)?
   - Recommendation: Phase 3 requirement AI-02 says "AI can create sub-habits and suggest/assign tags." Defer AI prompt updates for sub-habits and tags to Phase 3. In Phase 2, only update the prompt to handle the `IsNegative` flag when creating habits, and add `note` field support for `LogHabit` actions. The system prompt must also be updated to list habit metadata (sub-habit count, tags, negative flag) so AI has context.

## Sources

### Primary (HIGH confidence)
- Existing codebase analysis (all entity, command, query, controller, and DbContext files read directly)
- [EF Core Many-to-Many Relationships (Microsoft Learn)](https://learn.microsoft.com/en-us/ef/core/modeling/relationships/many-to-many) - UsingEntity configuration pattern
- [.NET 6 Date/Time/Timezone Enhancements (Microsoft DevBlog)](https://devblogs.microsoft.com/dotnet/date-time-and-time-zone-enhancements-in-net-6/) - IANA timezone ID support built into TimeZoneInfo

### Secondary (MEDIUM confidence)
- [Self-Referential Relationships in EF Core (Dot Net Tutorials)](https://dotnettutorials.net/lesson/self-referencing-relationship-in-entity-framework-core/) - Pattern analysis (decided against for sub-habits)
- [Cross-platform Time Zones with .NET Core (Microsoft DevBlog)](https://devblogs.microsoft.com/dotnet/cross-platform-time-zones-with-net-core/) - Background on cross-platform timezone behavior
- [Tree Structure in EF Core (Medium - Dmitry Pavlov)](https://medium.com/@dmitry.pavlov/tree-structure-in-ef-core-how-to-configure-a-self-referencing-table-and-use-it-53effad60bf) - Self-referencing pattern (decided against)

### Tertiary (LOW confidence)
- None -- all findings verified against official docs or codebase

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH -- No new packages needed; all existing packages verified in csproj files
- Architecture (entity design): HIGH -- Follows established codebase patterns exactly; EF Core patterns verified against official docs
- Architecture (sub-habit decision): MEDIUM -- Separate entity vs self-referencing is a design judgment; separate entity is simpler for the "checklist" requirement but self-referencing would be more flexible if sub-habits ever need their own frequency/type
- Pitfalls: HIGH -- Identified from direct codebase analysis (AsNoTracking, Entity base class, duplicate log check, cascade delete)
- Timezone: HIGH -- Built-in .NET 6+ IANA support verified against Microsoft DevBlog and official API docs

**Research date:** 2026-02-07
**Valid until:** 2026-03-09 (30 days -- stable domain, no fast-moving dependencies)
