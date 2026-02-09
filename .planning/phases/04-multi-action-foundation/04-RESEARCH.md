# Phase 4: Multi-Action Foundation - Research

**Researched:** 2026-02-09
**Domain:** Multi-action AI response processing, batch operations, partial failure handling
**Confidence:** HIGH

## Summary

Phase 4 extends the existing single-action AI architecture to support multiple actions per prompt with per-action error handling. The current system uses Gemini JSON structured outputs with a proven AiActionPlan → AiAction[] architecture. Research confirms that expanding this to multi-action is straightforward: change Actions from single item to array, add new action types (suggest_breakdown), and implement partial success handling at the command execution level.

Key finding: The project already has the right patterns in place. The single-action code uses foreach over plan.Actions (line 79 in ProcessUserChatCommandHandler), but currently receives only one action. Extending this to multiple actions requires no architectural changes—just expanded AI prompts, new action types, and error aggregation.

**Primary recommendation:** Build on existing architecture with minimal disruption. Add bulk endpoints separately from chat processing. Keep "keep successes" policy simple by not using transactions across individual action executions.

## User Constraints (from CONTEXT.md)

<user_constraints>

### Batch response format
- Conversational + structured: AI returns natural language summary ("Created 3 habits!") plus a structured actions array with per-action status
- Actions array uses status + ID only (no full entity data) — frontend refetches to get full data
- Each action has a typed `type` field (create, log, delete, update, suggest_breakdown) for type-specific frontend rendering
- Response shape stays flat: `{ message, actions[] }` — same as current, just multiple items in the array

### Confirmation flow design
- Habit breakdown is stateless: AI returns suggested parent + sub-habits as data in the response, user sends back edits via a separate endpoint
- User can edit names/details of suggested sub-habits before confirming creation
- Nothing is created until user confirms — both parent and selected sub-habits are created together after confirmation
- No expiry on suggestions — stateless data, user acts whenever

### Bulk endpoints
- General-purpose `POST /api/habits/bulk` that accepts an array of habits with parent-child relationships — used for confirmations and any other bulk scenario
- `DELETE /api/habits/bulk` that accepts an array of habit IDs for batch deletion — supports frontend multi-select
- Both are standalone endpoints, not confirmation-specific

### Error & partial failure behavior
- Keep successes policy: successful actions are committed, failed actions reported with errors, no rollback
- Bulk endpoints follow the same partial success policy — consistent everywhere
- Specific field-level errors per failed action (e.g., `{ action: 'create', habitName: 'reading', error: 'Name already exists', field: 'name' }`)
- AI conversational message acknowledges failures naturally (e.g., "Created exercise and meditation, but reading already exists")

### AI prompt & parsing strategy
- Same AiActionPlan structure, Actions becomes a list with multiple items — minimal architecture change
- `suggest_breakdown` is a distinct action type separate from `create` — carries proposed parent + sub-habits
- Fully mixed action types in a single response — any combination of creates, logs, deletes, and breakdowns
- Continue using JSON schema approach (not Gemini function calling) — proven reliable, format under our control

### Claude's Discretion
- Exact JSON schema for the expanded AiActionPlan
- How suggest_breakdown action payload is structured internally
- Validation rules for bulk endpoints
- AI prompt engineering for reliable multi-action JSON output

</user_constraints>

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MediatR | 14.0.0 | CQRS command/query handling | Already integrated, proven handler pattern for commands |
| FluentValidation | (current) | Request validation | Already integrated, supports RuleForEach for collection validation |
| System.Text.Json | .NET 10.0 | JSON serialization | Built-in, already configured with PropertyNameCaseInsensitive + JsonStringEnumConverter |
| EF Core | 10.0 | Database operations with AddRange | AddRange is adequate for bulk operations under 1,000 items per batch |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Gemini API | Current (gemini-2.5-flash) | Structured JSON outputs | Already integrated, supports complex nested schemas with arrays |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| EF AddRange | EFCore.BulkExtensions | 10-100x faster for 10,000+ records, but project scale suggests <100 habits per batch maximum |
| JSON schema | Gemini function calling | Function calling is more restrictive, loses control over exact response format |
| Result pattern | Exception throwing | Current architecture uses Result pattern consistently, changing would disrupt codebase |

**Installation:**
No new packages required — all capabilities exist in current stack.

## Architecture Patterns

### Current Single-Action Flow (Proven)
```
User Message → AI Intent Service → AiActionPlan { Actions[] } → Execute foreach action → UnitOfWork.SaveChangesAsync
```

**Key insight:** Code already loops over Actions (ProcessUserChatCommandHandler.cs line 79). Current prompts return single action, but architecture supports multiple.

### Multi-Action Expansion Pattern
```
User: "create habits for exercise, meditation, and reading"
                    ↓
AI returns: { actions: [CreateHabit×3], aiMessage: "Created 3 habits!" }
                    ↓
Execute each: foreach (var action in plan.Actions)
                    ↓
Aggregate results: { message, actions: [{ type, status, id, error? }] }
                    ↓
SaveChangesAsync (commits all successful actions)
```

### Per-Action Error Handling Pattern
```csharp
// Current code (line 79-100, ProcessUserChatCommandHandler.cs)
foreach (var action in plan.Actions)
{
    var actionResult = action.Type switch
    {
        AiActionType.LogHabit => await ExecuteLogHabitAsync(...),
        AiActionType.CreateHabit => await ExecuteCreateHabitAsync(...),
        // Add new types here
        _ => Result.Failure($"Unknown action type: {action.Type}")
    };

    if (actionResult.IsSuccess)
        executedActions.Add($"{action.Type}: {action.Title ?? ...}");
    else
        logger.LogError("Action failed: {ActionType} - Error: {Error}", ...);
}
// Changes saved AFTER all actions execute (line 109)
await unitOfWork.SaveChangesAsync(cancellationToken);
```

**Critical insight:** Current code already has partial success behavior — failed actions are logged but don't stop execution. SaveChangesAsync commits all successful entity additions at the end.

### Recommended Response Structure
```csharp
public record ChatResponse(
    string? AiMessage,
    IReadOnlyList<ActionResult> Actions);

public record ActionResult(
    AiActionType Type,
    ActionStatus Status,  // Success | Failed
    Guid? EntityId,       // For successful creates/logs
    string? EntityName,   // For UI display (habit title)
    string? Error,        // For failed actions
    string? Field);       // For validation errors (e.g., "title")
```

### Bulk Endpoint Pattern
```csharp
POST /api/habits/bulk
{
  "habits": [
    { "title": "Morning Routine", "frequencyUnit": "Day", "subHabits": [
        { "title": "Meditate" },
        { "title": "Journal" }
    ]},
    { "title": "Exercise" }
  ]
}

Response 200 OK:
{
  "results": [
    { "index": 0, "status": "success", "habitId": "..." },
    { "index": 1, "status": "success", "habitId": "..." }
  ]
}
```

**Validation:** Use FluentValidation with RuleForEach to validate each habit in the array.

### Habit Breakdown Suggestion Pattern
```csharp
// AI Action Type
public enum AiActionType
{
    LogHabit,
    CreateHabit,
    AssignTag,
    SuggestBreakdown  // NEW
}

// AI returns suggestion (no DB writes)
{
  "type": "SuggestBreakdown",
  "parentHabit": {
    "title": "Morning Routine",
    "frequencyUnit": "Day",
    "frequencyQuantity": 1
  },
  "suggestedSubHabits": [
    { "title": "Meditate for 10 minutes" },
    { "title": "Journal gratitude" },
    { "title": "Stretch" }
  ]
}

// User edits, then calls bulk create endpoint with final structure
```

**Stateless design:** No storage of suggestions. Frontend holds data, user confirms via bulk endpoint.

### Anti-Patterns to Avoid
- **TransactionScope for partial rollback:** .NET TransactionScope doesn't support manual partial rollback. Either commit all or rollback all. For "keep successes" policy, avoid wrapping in transactions.
- **Separate SaveChangesAsync per action:** Creates N+1 database round-trips. Current pattern (single save at end) is optimal.
- **Returning full habit entities in chat response:** Bloats response size, creates stale data issues. Return IDs only, frontend refetches.
- **Using AddRange without explicit AddAsync:** EF treats Guid.NewGuid() entities as Modified, not Added. Must explicitly call AddAsync for each new entity (already done correctly in codebase).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| JSON schema validation | Custom parser | Gemini responseMimeType: "application/json" | Gemini validates against schema before returning |
| Retry logic | Custom backoff | Polly library (if needed) | Production-tested exponential backoff, circuit breaker patterns |
| Bulk database operations | Custom batching | EF AddRange (current scale) | Adequate for <1,000 items, proven in codebase |
| Validation aggregation | Manual collection checks | FluentValidation RuleForEach | Built-in collection validation with per-item error messages |

**Key insight:** Don't optimize for problems you don't have. Project scale (personal habit tracker) suggests batches of 3-20 items maximum, not thousands. EF AddRange is sufficient.

## Common Pitfalls

### Pitfall 1: Gemini Returning Single Action Despite Multi-Action Prompt
**What goes wrong:** AI interprets multiple requests as separate conversations, returns only first action.
**Why it happens:** Prompt doesn't explicitly demonstrate multi-action JSON response format.
**How to avoid:** Add multi-action examples to SystemPromptBuilder (lines 130-374). Show 2-3 examples with actions array containing 3+ items.
**Warning signs:** Integration tests show AI returns single action when expecting multiple. Check prompt examples.

### Pitfall 2: FluentValidation Failing Entire Batch on Single Invalid Item
**What goes wrong:** Validator marks entire collection invalid if one item fails, preventing partial success.
**Why it happens:** Default behavior is all-or-nothing validation.
**How to avoid:** Catch ValidationException, extract per-item errors using {CollectionIndex} placeholder (FluentValidation 8.5+), convert to per-action error responses.
**Warning signs:** Bulk create fails completely when one habit has invalid title.

### Pitfall 3: EF Change Tracker Conflicts with Partial Success
**What goes wrong:** Failed action leaves entity in Added state, causes SaveChangesAsync to fail for successful actions too.
**Why it happens:** Exception during entity creation adds partial state to tracker.
**How to avoid:** Use try-catch around individual entity creation, don't call AddAsync if entity creation fails. Current code does this correctly (CreateHabitCommandHandler lines 26-37 check habitResult.IsFailure before AddAsync).
**Warning signs:** One invalid habit causes all habits in batch to fail despite individual validation passing.

### Pitfall 4: Lost Context in Per-Action Errors
**What goes wrong:** Error message says "Title required" but doesn't indicate which habit in batch failed.
**Why it happens:** Error aggregation loses original request context.
**How to avoid:** Include habit title or index in error responses. Use FluentValidation's {CollectionIndex} for positional errors.
**Warning signs:** User gets "Validation failed" without knowing which of 5 habits caused the issue.

### Pitfall 5: Oversized JSON Schema Rejection
**What goes wrong:** Gemini rejects complex nested schema for suggest_breakdown with multiple sub-habits.
**Why it happens:** Gemini has undocumented limits on schema depth/size.
**How to avoid:** Keep suggest_breakdown schema simple: `parentHabit` object + `suggestedSubHabits` array of objects with title/description only. Don't nest beyond 2-3 levels.
**Warning signs:** Gemini returns 400 error or generic text instead of JSON when schema includes breakdown type.

### Pitfall 6: Race Condition in Habit Title Uniqueness
**What goes wrong:** User creates "Exercise" twice in bulk request, both pass validation, causes duplicate key error.
**Why it happens:** Validation checks existing DB habits, but not within-batch duplicates.
**How to avoid:** In bulk create validation, check for duplicate titles within the request payload, not just against DB.
**Warning signs:** Bulk create succeeds individually but fails when same habits sent in batch.

## Code Examples

Verified patterns from official sources and current codebase:

### Multi-Action AI Prompt Example
```csharp
// Add to SystemPromptBuilder.cs (line ~375)
sb.AppendLine("""
### Multi-Action Examples:

User: "create habits for exercise, meditation, and reading"
{
  "actions": [
    {
      "type": "CreateHabit",
      "title": "Exercise",
      "frequencyUnit": "Day",
      "frequencyQuantity": 1,
      "dueDate": "2026-02-09"
    },
    {
      "type": "CreateHabit",
      "title": "Meditation",
      "frequencyUnit": "Day",
      "frequencyQuantity": 1,
      "dueDate": "2026-02-09"
    },
    {
      "type": "CreateHabit",
      "title": "Reading",
      "frequencyUnit": "Day",
      "frequencyQuantity": 1,
      "dueDate": "2026-02-09"
    }
  ],
  "aiMessage": "Created 3 new habits: Exercise, Meditation, and Reading!"
}

User: "I exercised and meditated today" (both habits exist)
{
  "actions": [
    {
      "type": "LogHabit",
      "habitId": "guid-for-exercise"
    },
    {
      "type": "LogHabit",
      "habitId": "guid-for-meditation"
    }
  ],
  "aiMessage": "Logged both habits! Great work!"
}
""");
```

### Suggest Breakdown Action Structure
```csharp
// Add to AiAction.cs
public record ParentHabitSuggestion
{
    public required string Title { get; init; }
    public string? Description { get; init; }
    public FrequencyUnit? FrequencyUnit { get; init; }
    public int? FrequencyQuantity { get; init; }
}

public record SubHabitSuggestion
{
    public required string Title { get; init; }
    public string? Description { get; init; }
}

// Add to AiAction.cs
public record AiAction
{
    // ... existing properties ...

    // For SuggestBreakdown type
    public ParentHabitSuggestion? ParentHabit { get; init; }
    public List<SubHabitSuggestion>? SuggestedSubHabits { get; init; }
}
```

### Bulk Create Endpoint with Validation
```csharp
// HabitsController.cs
public record BulkCreateRequest(IReadOnlyList<HabitDto> Habits);

public record HabitDto(
    string Title,
    string? Description,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    DateOnly? DueDate,
    IReadOnlyList<HabitDto>? SubHabits = null);

[HttpPost("bulk")]
public async Task<IActionResult> BulkCreate(
    [FromBody] BulkCreateRequest request,
    CancellationToken cancellationToken)
{
    var command = new BulkCreateHabitsCommand(
        HttpContext.GetUserId(),
        request.Habits);

    var result = await mediator.Send(command, cancellationToken);

    return result.IsSuccess
        ? Ok(result.Value)  // Returns BulkOperationResult
        : BadRequest(new { error = result.Error });
}
```

### FluentValidation for Bulk Operations
```csharp
// Source: https://docs.fluentvalidation.net/en/latest/collections.html
public class BulkCreateHabitsCommandValidator : AbstractValidator<BulkCreateHabitsCommand>
{
    public BulkCreateHabitsCommandValidator()
    {
        RuleFor(x => x.Habits)
            .NotEmpty()
            .WithMessage("Must provide at least one habit");

        RuleFor(x => x.Habits)
            .Must(habits => habits.Count <= 100)
            .WithMessage("Cannot create more than 100 habits at once");

        // Validate each habit in collection
        RuleForEach(x => x.Habits)
            .ChildRules(habit =>
            {
                habit.RuleFor(h => h.Title)
                    .NotEmpty()
                    .WithMessage("Habit at index {CollectionIndex} is missing title")
                    .MaximumLength(200);

                habit.RuleFor(h => h.FrequencyQuantity)
                    .GreaterThan(0)
                    .When(h => h.FrequencyQuantity is not null);
            });

        // Check for duplicate titles within batch
        RuleFor(x => x.Habits)
            .Must(habits => habits.Select(h => h.Title).Distinct().Count() == habits.Count)
            .WithMessage("Duplicate habit titles in batch");
    }
}
```

### Per-Action Error Aggregation
```csharp
// ProcessUserChatCommandHandler.cs (modified)
var results = new List<ActionResult>();

foreach (var action in plan.Actions)
{
    try
    {
        var actionResult = action.Type switch
        {
            AiActionType.LogHabit => await ExecuteLogHabitAsync(...),
            AiActionType.CreateHabit => await ExecuteCreateHabitAsync(...),
            AiActionType.AssignTag => await ExecuteAssignTagAsync(...),
            AiActionType.SuggestBreakdown => ExecuteSuggestBreakdown(action),
            _ => Result.Failure($"Unknown action type: {action.Type}")
        };

        if (actionResult.IsSuccess)
        {
            results.Add(new ActionResult(
                Type: action.Type,
                Status: ActionStatus.Success,
                EntityId: actionResult.Value,
                EntityName: action.Title,
                Error: null,
                Field: null));
        }
        else
        {
            results.Add(new ActionResult(
                Type: action.Type,
                Status: ActionStatus.Failed,
                EntityId: null,
                EntityName: action.Title,
                Error: actionResult.Error,
                Field: null));
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unexpected error executing action");
        results.Add(new ActionResult(
            Type: action.Type,
            Status: ActionStatus.Failed,
            EntityId: null,
            EntityName: action.Title,
            Error: "An unexpected error occurred",
            Field: null));
    }
}

await unitOfWork.SaveChangesAsync(cancellationToken);

return Result.Success(new ChatResponse(plan.AiMessage, results));
```

### Bulk Delete Endpoint
```csharp
[HttpDelete("bulk")]
public async Task<IActionResult> BulkDelete(
    [FromBody] BulkDeleteRequest request,
    CancellationToken cancellationToken)
{
    var command = new BulkDeleteHabitsCommand(
        HttpContext.GetUserId(),
        request.HabitIds);

    var result = await mediator.Send(command, cancellationToken);

    // Returns 200 with partial results even if some fail
    return Ok(result.Value);
}

// Handler
public async Task<Result<BulkOperationResult>> Handle(
    BulkDeleteHabitsCommand request,
    CancellationToken cancellationToken)
{
    var results = new List<OperationResult>();

    foreach (var habitId in request.HabitIds)
    {
        var habit = await habitRepository.FindOneAsync(
            h => h.Id == habitId && h.UserId == request.UserId,
            cancellationToken);

        if (habit is null)
        {
            results.Add(new OperationResult(
                Index: results.Count,
                Status: "failed",
                EntityId: habitId,
                Error: "Habit not found"));
            continue;
        }

        habit.Delete();
        results.Add(new OperationResult(
            Index: results.Count,
            Status: "success",
            EntityId: habitId,
            Error: null));
    }

    await unitOfWork.SaveChangesAsync(cancellationToken);

    return Result.Success(new BulkOperationResult(results));
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Single action per prompt | Multi-action with array support | 2024-2025 (OpenAI, Gemini) | Enables conversational "create X, Y, Z" requests |
| Exception-based error handling | Result pattern with partial success | .NET community trend 2023+ | Cleaner flow control, explicit error handling |
| All-or-nothing validation | Per-item validation with RuleForEach | FluentValidation 8.5+ (2019) | Enables partial success with detailed errors |
| Manual JSON parsing | Gemini structured outputs with schema | Gemini API 2024 updates | Guaranteed schema compliance, no regex parsing |
| HTTP 200 for partial success | HTTP 207 Multi-Status | WebDAV spec (old), rarely adopted | Project uses 200 with structured errors (more common) |

**Deprecated/outdated:**
- Manual JSON validation: Gemini responseMimeType handles this now
- TransactionScope for partial rollback: Not supported in .NET, use savepoints if needed (EF Core supports)
- Separate batch API standards (RFC 7234): Modern APIs use domain-specific bulk endpoints instead

## Open Questions

1. **Optimal batch size limit for bulk endpoints**
   - What we know: EF AddRange adequate for <1,000 items, personal habit tracker unlikely to need more
   - What's unclear: Should enforce hard limit (50? 100?) to prevent abuse/timeout
   - Recommendation: Start with 100 max per batch, add validation rule, monitor in production

2. **Handling AI suggestion conflicts (breakdown suggests existing habit)**
   - What we know: Stateless suggestions mean no validation until confirmation
   - What's unclear: Should frontend check for duplicates before showing suggestions, or let bulk create endpoint handle it?
   - Recommendation: Let bulk endpoint handle it — returns per-item errors, user can adjust

3. **Response size for large multi-action responses**
   - What we know: Returning IDs only keeps response small, frontend refetches
   - What's unclear: Is separate refetch acceptable UX, or should we include minimal habit data (title + frequency) in response?
   - Recommendation: Start with IDs only (matches user constraint), add title if frontend requests for immediate UI update

4. **Gemini JSON schema size limits for complex suggest_breakdown**
   - What we know: Undocumented limits on schema complexity exist
   - What's unclear: Maximum safe nesting depth and array sizes
   - Recommendation: Test with 3-level nesting (action → parentHabit → suggestedSubHabits with 10 items), add error handling for schema rejection

## Sources

### Primary (HIGH confidence)
- Gemini API Structured Output documentation: https://ai.google.dev/gemini-api/docs/structured-output
- Microsoft Learn - Partial Failure Strategies: https://learn.microsoft.com/en-us/dotnet/architecture/microservices/implement-resilient-applications/partial-failure-strategies
- FluentValidation Collections documentation: https://docs.fluentvalidation.net/en/latest/collections.html
- Current codebase files:
  - `ProcessUserChatCommandHandler.cs` (lines 79-100: already loops over Actions)
  - `AiActionPlan.cs`, `AiAction.cs`, `AiActionType.cs`
  - `SystemPromptBuilder.cs` (lines 130-374: JSON examples)
  - `CreateHabitCommandHandler.cs` (pattern for sub-habits creation)

### Secondary (MEDIUM confidence)
- [Strategies for handling partial failure - .NET | Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/implement-resilient-applications/partial-failure-strategies)
- [Structured outputs | Gemini API | Google AI for Developers](https://ai.google.dev/gemini-api/docs/structured-output)
- [Google announces support for JSON Schema](https://blog.google/technology/developers/gemini-api-structured-outputs/)
- [Supporting bulk operations in REST APIs](https://www.mscharhag.com/api-design/bulk-and-batch-operations)
- [Design Patterns for Handling Mixed Success and Failure Scenarios](https://medium.com/api-catalyst/design-patterns-for-handling-mixed-success-and-failure-scenarios-in-http-200-ok-responses-07e26684f1ec)
- [Improving Error Handling with the Result Pattern in MediatR](https://goatreview.com/improving-error-handling-result-pattern-mediatr/)
- [EF Core Bulk Insert best practices](https://www.milanjovanovic.tech/blog/fast-sql-bulk-inserts-with-csharp-and-ef-core)

### Tertiary (LOW confidence - needs verification)
- WebSearch findings on MediatR batch processing patterns (no 2026-specific sources found)
- HTTP 207 Multi-Status adoption rates (WebDAV spec exists, but 200 + structured errors more common in practice)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - All libraries already integrated, capabilities verified in codebase
- Architecture: HIGH - Current code already has multi-action loop, minimal changes needed
- Pitfalls: MEDIUM - Based on common patterns and Gemini documentation, not project-specific testing
- Bulk endpoints: MEDIUM - Standard REST patterns, but FluentValidation collection validation needs testing at project scale

**Research date:** 2026-02-09
**Valid until:** 2026-03-09 (30 days - stable domain, Gemini API unlikely to change significantly)
