# Architecture Patterns: AI Intelligence Enhancements

**Domain:** AI-powered habit tracking API enhancements
**Researched:** 2026-02-09
**Confidence:** HIGH

## System Overview

This research covers architectural integration of four new AI capabilities into the existing Clean Architecture + CQRS system:

1. **Multi-action execution** with partial failure handling
2. **Gemini Vision** multimodal API integration for image processing
3. **User fact storage** (semantic memory) for AI personalization
4. **Routine inference** from HabitLog time-series pattern analysis

### Existing Architecture Foundation

```
Domain (Entities, Interfaces, Models)
    ↓
Application (Commands/Queries via MediatR)
    ↓
Infrastructure (Services, Repositories, DbContext)
    ↓
Api (Controllers)
```

**Key Components:**
- `IAiIntentService` → `GeminiIntentService` (HTTP to Gemini API)
- `SystemPromptBuilder` → constructs prompts with habit/tag context
- `ProcessUserChatCommand` → orchestrates AI chat flow
- `AiActionPlan` → contains `Actions[]` + `AiMessage`
- `GenericRepository<T>` + `UnitOfWork` → data access pattern

**Current Limitation:** Single action execution mindset (though Actions is already a list), text-only input, no user memory, no pattern detection.

---

## Component Responsibilities

### 1. Multi-Action Execution

**Current State:**
- `ProcessUserChatCommand.Handle()` iterates `plan.Actions` (lines 79-100)
- Calls action executors: `ExecuteLogHabitAsync`, `ExecuteCreateHabitAsync`, `ExecuteAssignTagAsync`
- Logs failures but continues execution
- Single `UnitOfWork.SaveChangesAsync()` at end (line 109)

**Problem:** All-or-nothing transaction. If SaveChanges fails, ALL actions fail. No granular feedback.

**Solution Pattern:** Partial commit with compensating actions

| Approach | Pros | Cons | Recommendation |
|----------|------|------|----------------|
| **Single transaction (current)** | Atomic consistency | All-or-nothing, poor UX | Keep for simple cases |
| **Per-action commits** | Granular control, partial success | Complexity, inconsistency risk | Use for multi-action chat |
| **Saga orchestration** | Compensating actions, resilience | High complexity, overkill | Not needed for this scale |

**Recommended Architecture:**
```csharp
// NEW: Multi-action executor with partial failure
public class ActionExecutionResult
{
    public bool Success { get; init; }
    public string ActionType { get; init; }
    public string ActionDescription { get; init; }
    public string? ErrorMessage { get; init; }
}

// Modified ProcessUserChatCommand handler
foreach (var action in plan.Actions)
{
    var result = await ExecuteActionAsync(action, userId, ct);
    executionResults.Add(result);

    if (result.Success)
    {
        await unitOfWork.SaveChangesAsync(ct); // Commit per action
    }
    else
    {
        // Log failure, continue with next action
        logger.LogWarning("Action failed: {Error}", result.ErrorMessage);
    }
}
```

**Integration Point:** Modify `ProcessUserChatCommandHandler` to support per-action commits with detailed result tracking.

**New Components:**
- `ActionExecutionResult` record (Domain/Models)
- Enhanced `ChatResponse` with `IReadOnlyList<ActionExecutionResult>` instead of `string[]`

**References:**
- [Domain events: Design and implementation - .NET](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/domain-events-design-implementation)
- [CQRS and exception handling · Enterprise Craftsmanship](https://enterprisecraftsmanship.com/posts/cqrs-exception-handling/)

---

### 2. Gemini Vision Multimodal Integration

**Current State:**
- `GeminiIntentService` sends text-only requests
- Request structure: `GeminiContent.Parts[] = [{ Text }]`
- No image handling

**Gemini Vision API Structure:**

```json
{
  "contents": [{
    "parts": [
      {"text": "Analyze this image"},
      {
        "inline_data": {
          "mime_type": "image/jpeg",
          "data": "base64_encoded_string"
        }
      }
    ]
  }],
  "generationConfig": {
    "temperature": 0.1,
    "responseMimeType": "application/json"
  }
}
```

**Supported formats:** PNG, JPEG, WEBP, HEIC, HEIF
**Size limit:** 20MB total request (inline), or use File API for larger files
**Model:** `gemini-2.5-flash` supports multimodal (already configured)

**Architectural Components:**

| Layer | Component | Responsibility |
|-------|-----------|----------------|
| **Api** | `ChatController.ProcessChat()` | Accept `IFormFile` images with message |
| **Application** | `ProcessUserChatCommand` | Add `byte[]? ImageData, string? ImageMimeType` properties |
| **Infrastructure** | `GeminiIntentService.InterpretAsync()` | Add image parameter, construct multimodal request |
| **Domain** | `IAiIntentService` | Add image parameters to signature |

**New DTOs:**

```csharp
// Domain/Models/GeminiImageData.cs
public record GeminiImageData
{
    public required byte[] Data { get; init; }
    public required string MimeType { get; init; } // image/jpeg, image/png
}

// Modified IAiIntentService
Task<Result<AiActionPlan>> InterpretAsync(
    string userMessage,
    IReadOnlyList<Habit> activeHabits,
    IReadOnlyList<Tag> userTags,
    GeminiImageData? image = null, // NEW
    CancellationToken cancellationToken = default);
```

**GeminiIntentService Enhancement:**

```csharp
// Add to existing GeminiRequest DTOs
private record GeminiPart
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("inline_data")] // NEW
    public GeminiInlineData? InlineData { get; init; }
}

private record GeminiInlineData
{
    [JsonPropertyName("mime_type")]
    public required string MimeType { get; init; }

    [JsonPropertyName("data")]
    public required string Data { get; init; } // Base64
}

// Modified request building
var parts = new List<GeminiPart>
{
    new() { Text = $"{systemPrompt}\n\nUser: {userMessage}" }
};

if (image is not null)
{
    parts.Add(new GeminiPart
    {
        InlineData = new GeminiInlineData
        {
            MimeType = image.MimeType,
            Data = Convert.ToBase64String(image.Data)
        }
    });
}

var request = new GeminiRequest
{
    Contents = [new GeminiContent { Parts = parts.ToArray() }],
    GenerationConfig = new() { Temperature = 0.1, ResponseMimeType = "application/json" }
};
```

**Controller Enhancement:**

```csharp
// Api/Controllers/ChatController.cs
[HttpPost]
public async Task<IActionResult> ProcessChat(
    [FromForm] string message,
    [FromForm] IFormFile? image) // NEW
{
    byte[]? imageData = null;
    string? mimeType = null;

    if (image is not null)
    {
        // Validate size (20MB limit)
        if (image.Length > 20 * 1024 * 1024)
            return BadRequest("Image too large (max 20MB)");

        // Validate format
        var allowedTypes = new[] { "image/jpeg", "image/png", "image/webp" };
        if (!allowedTypes.Contains(image.ContentType))
            return BadRequest("Unsupported image format");

        using var ms = new MemoryStream();
        await image.CopyToAsync(ms);
        imageData = ms.ToArray();
        mimeType = image.ContentType;
    }

    var command = new ProcessUserChatCommand(
        UserId: userId,
        Message: message,
        ImageData: imageData,
        ImageMimeType: mimeType
    );

    // ... execute command
}
```

**Integration Point:** Flow is Api → Application → Infrastructure. Add optional image parameters at each layer.

**References:**
- [Image understanding | Gemini API | Google AI for Developers](https://ai.google.dev/gemini-api/docs/image-understanding)
- [Multimodal Prompting with Gemini: Working with Images](https://googlecloudplatform.github.io/applied-ai-engineering-samples/genai-on-vertex-ai/gemini/prompting_recipes/multimodal/multimodal_prompting_image/)

---

### 3. User Fact Storage (Semantic Memory)

**Purpose:** AI learns user preferences, key facts, context for better personalization.

**Examples:**
- "I prefer morning workouts"
- "I'm trying to quit caffeine"
- "I have a dog named Max"
- "I work night shifts"

**Architecture Pattern:** Semantic memory with embedding-based retrieval

| Component | Technology | Rationale |
|-----------|------------|-----------|
| **Storage** | PostgreSQL (existing) + pgvector extension | Reuse existing DB, avoid new infrastructure |
| **Embeddings** | Gemini text-embedding-004 | Same provider, simple HTTP API |
| **Retrieval** | Cosine similarity search | Standard semantic search |
| **Fallback** | Simple keyword search | No embeddings needed initially |

**New Domain Entity:**

```csharp
// Domain/Entities/UserFact.cs
public class UserFact : Entity
{
    public Guid UserId { get; private set; }
    public string Content { get; private set; } = null!; // "I prefer morning workouts"
    public string? Category { get; private set; } // "Preferences", "Context", "Goals"
    public float[]? Embedding { get; private set; } // Vector for semantic search
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? LastReferencedUtc { get; private set; } // Track usage

    private UserFact() { }

    public static Result<UserFact> Create(Guid userId, string content, string? category = null)
    {
        if (userId == Guid.Empty)
            return Result.Failure<UserFact>("User ID is required");

        if (string.IsNullOrWhiteSpace(content))
            return Result.Failure<UserFact>("Content is required");

        return Result.Success(new UserFact
        {
            UserId = userId,
            Content = content.Trim(),
            Category = category?.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public void SetEmbedding(float[] embedding)
    {
        Embedding = embedding;
    }

    public void MarkReferenced()
    {
        LastReferencedUtc = DateTime.UtcNow;
    }
}
```

**Database Schema (EF Core Migration):**

```csharp
// OrbitDbContext.cs
public DbSet<UserFact> UserFacts => Set<UserFact>();

protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    // ...existing

    modelBuilder.Entity<UserFact>(entity =>
    {
        entity.HasIndex(uf => new { uf.UserId, uf.CreatedAtUtc });
        entity.HasIndex(uf => uf.Category);

        // PostgreSQL pgvector extension support (optional)
        entity.Property(uf => uf.Embedding)
            .HasColumnType("vector(768)"); // Gemini embedding dimension
    });
}
```

**New Application Layer Commands/Queries:**

```csharp
// Application/UserFacts/Commands/CreateUserFactCommand.cs
public record CreateUserFactCommand(
    Guid UserId,
    string Content,
    string? Category = null
) : IRequest<Result<Guid>>;

// Application/UserFacts/Queries/GetRelevantFactsQuery.cs
public record GetRelevantFactsQuery(
    Guid UserId,
    string Context, // Current user message for semantic matching
    int MaxResults = 5
) : IRequest<Result<IReadOnlyList<UserFact>>>;
```

**New Infrastructure Service:**

```csharp
// Infrastructure/Services/IUserFactService.cs (Domain/Interfaces)
public interface IUserFactService
{
    Task<Result<float[]>> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
    Task<Result<IReadOnlyList<UserFact>>> SearchSimilarFactsAsync(
        Guid userId,
        float[] queryEmbedding,
        int maxResults = 5,
        CancellationToken ct = default);
}

// Infrastructure/Services/GeminiUserFactService.cs
public class GeminiUserFactService : IUserFactService
{
    // Use Gemini text-embedding-004 model
    // https://generativelanguage.googleapis.com/v1beta/models/text-embedding-004:embedContent

    public async Task<Result<float[]>> GenerateEmbeddingAsync(string text, CancellationToken ct)
    {
        var request = new { content = new { parts = new[] { new { text } } } };
        var response = await httpClient.PostAsJsonAsync($"{baseUrl}/models/text-embedding-004:embedContent?key={apiKey}", request, ct);
        // Parse response.embedding.values (float[])
    }

    public async Task<Result<IReadOnlyList<UserFact>>> SearchSimilarFactsAsync(...)
    {
        // Use PostgreSQL pgvector cosine similarity
        // SELECT *, (embedding <=> @queryEmbedding) AS distance
        // FROM UserFacts
        // WHERE UserId = @userId
        // ORDER BY distance
        // LIMIT @maxResults
    }
}
```

**Integration into SystemPromptBuilder:**

```csharp
// SystemPromptBuilder.cs
public static string BuildSystemPrompt(
    IReadOnlyList<Habit> activeHabits,
    IReadOnlyList<Tag> userTags,
    IReadOnlyList<UserFact> relevantFacts) // NEW
{
    var sb = new StringBuilder();

    // ...existing sections

    if (relevantFacts.Count > 0)
    {
        sb.AppendLine();
        sb.AppendLine("## User Context & Preferences");
        sb.AppendLine("Consider these facts about the user:");
        foreach (var fact in relevantFacts)
        {
            sb.AppendLine($"- {fact.Content}");
        }
        sb.AppendLine();
    }

    // ...rest of prompt
}
```

**Data Flow:**

```
User Message → GetRelevantFactsQuery
    → Generate embedding for message
    → Search similar UserFacts by embedding
    → Load into SystemPromptBuilder
    → Send to Gemini

AI Response → Extract new facts (if any)
    → CreateUserFactCommand
    → Generate embedding
    → Store UserFact
```

**Extraction Strategy:** Add new AiActionType for fact capture:

```csharp
// Domain/Enums/AiActionType.cs
public enum AiActionType
{
    LogHabit,
    CreateHabit,
    AssignTag,
    StoreFact // NEW - AI captures notable user information
}

// Domain/Models/AiAction.cs
public record AiAction
{
    // ...existing
    public string? FactContent { get; init; } // For StoreFact action
    public string? FactCategory { get; init; } // Optional category
}
```

**Alternative (Simpler) Implementation:** No embeddings initially, use simple keyword search or chronological retrieval:

```csharp
// Simpler GetRelevantFactsQuery (no embeddings)
var facts = await factRepository.FindAsync(
    f => f.UserId == userId,
    ct,
    orderBy: q => q.OrderByDescending(f => f.LastReferencedUtc ?? f.CreatedAtUtc),
    take: 10
);
```

**Recommendation:** Start simple (no embeddings), add semantic search in later phase if needed.

**References:**
- [What Is AI Agent Memory? | IBM](https://www.ibm.com/think/topics/ai-agent-memory)
- [Design Patterns for Long-Term Memory in LLM-Powered Architectures](https://serokell.io/blog/design-patterns-for-long-term-memory-in-llm-powered-architectures)
- [Build a .NET AI vector search app](https://learn.microsoft.com/en-us/dotnet/ai/quickstarts/build-vector-search-app)

---

### 4. Routine Inference (Pattern Detection)

**Purpose:** Detect patterns in HabitLog timestamps to suggest routines.

**Examples:**
- User logs "Gym" every Monday/Wednesday/Friday at 6am → Suggest "Create weekly gym routine for MWF mornings"
- User logs "Coffee" daily at 2pm → Detect potential caffeine dependency pattern
- User logs "Reading" inconsistently → Suggest setting a recurring habit

**Architecture Pattern:** Analytical service with time-series pattern detection

| Component | Responsibility |
|-----------|----------------|
| **RoutineAnalysisService** | Analyze HabitLog data for patterns |
| **RoutineInferenceQuery** | Trigger analysis, return suggestions |
| **RoutineSuggestion model** | Structured suggestion for UI |

**Pattern Detection Algorithms:**

| Pattern Type | Detection Method | Complexity |
|--------------|------------------|------------|
| **Day-of-week regularity** | Group logs by DayOfWeek, calculate frequency | Low |
| **Time-of-day clustering** | Group logs by hour window, find peaks | Medium |
| **Consistency analysis** | Calculate streaks, gaps, standard deviation | Low |
| **Frequency recommendation** | Analyze intervals between logs, suggest FrequencyUnit | Medium |

**New Domain Models:**

```csharp
// Domain/Models/RoutineSuggestion.cs
public record RoutineSuggestion
{
    public required string Title { get; init; } // "Gym Routine"
    public required string Description { get; init; } // "You go to the gym every Mon/Wed/Fri at 6am"
    public required string SuggestedAction { get; init; } // "Create a weekly habit for MWF mornings?"
    public FrequencyUnit? SuggestedFrequency { get; init; }
    public int? SuggestedQuantity { get; init; }
    public List<DayOfWeek>? SuggestedDays { get; init; }
    public TimeOnly? SuggestedTime { get; init; } // For time-based prompts
    public Guid? RelatedHabitId { get; init; } // If analyzing existing habit
    public ConfidenceLevel Confidence { get; init; } // High/Medium/Low
}

// Domain/Enums/ConfidenceLevel.cs
public enum ConfidenceLevel
{
    Low,    // 50-70% pattern match
    Medium, // 70-85% pattern match
    High    // 85%+ pattern match
}
```

**New Infrastructure Service:**

```csharp
// Domain/Interfaces/IRoutineAnalysisService.cs
public interface IRoutineAnalysisService
{
    Task<Result<IReadOnlyList<RoutineSuggestion>>> AnalyzeUserRoutinesAsync(
        Guid userId,
        CancellationToken ct = default);

    Task<Result<IReadOnlyList<RoutineSuggestion>>> AnalyzeHabitPatternsAsync(
        Guid habitId,
        CancellationToken ct = default);
}

// Infrastructure/Services/RoutineAnalysisService.cs
public class RoutineAnalysisService : IRoutineAnalysisService
{
    private readonly IGenericRepository<HabitLog> _logRepository;
    private readonly IGenericRepository<Habit> _habitRepository;

    public async Task<Result<IReadOnlyList<RoutineSuggestion>>> AnalyzeUserRoutinesAsync(
        Guid userId,
        CancellationToken ct)
    {
        // 1. Load all HabitLogs for user's habits (last 90 days)
        var cutoffDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-90));

        var logs = await _logRepository.FindAsync(
            l => l.Date >= cutoffDate && _habitRepository
                .GetQueryable()
                .Where(h => h.UserId == userId)
                .Select(h => h.Id)
                .Contains(l.HabitId),
            ct
        );

        // 2. Group by HabitId
        var habitGroups = logs.GroupBy(l => l.HabitId);

        var suggestions = new List<RoutineSuggestion>();

        foreach (var group in habitGroups)
        {
            // 3. Analyze patterns
            var dayOfWeekPattern = AnalyzeDayOfWeekPattern(group);
            var timeOfDayPattern = AnalyzeTimeOfDayPattern(group);
            var consistencyPattern = AnalyzeConsistency(group);

            // 4. Generate suggestions based on patterns
            if (dayOfWeekPattern.Confidence >= ConfidenceLevel.Medium)
            {
                suggestions.Add(dayOfWeekPattern);
            }
        }

        return Result.Success<IReadOnlyList<RoutineSuggestion>>(suggestions);
    }

    private RoutineSuggestion AnalyzeDayOfWeekPattern(IEnumerable<HabitLog> logs)
    {
        // Group by DayOfWeek
        var dayFrequency = logs
            .GroupBy(l => l.Date.DayOfWeek)
            .ToDictionary(g => g.Key, g => g.Count());

        var totalLogs = logs.Count();
        var dominantDays = dayFrequency
            .Where(kvp => kvp.Value / (double)totalLogs >= 0.7) // 70% threshold
            .Select(kvp => kvp.Key)
            .ToList();

        if (dominantDays.Count >= 2 && dominantDays.Count <= 5)
        {
            // High confidence weekly pattern
            var habit = await _habitRepository.GetByIdAsync(logs.First().HabitId);

            return new RoutineSuggestion
            {
                Title = $"{habit.Title} Routine",
                Description = $"You typically do this on {string.Join(", ", dominantDays)}",
                SuggestedAction = "Create a weekly habit with specific days?",
                SuggestedFrequency = FrequencyUnit.Day,
                SuggestedQuantity = 1,
                SuggestedDays = dominantDays,
                RelatedHabitId = habit.Id,
                Confidence = ConfidenceLevel.High
            };
        }

        // ...return low confidence default
    }

    private RoutineSuggestion AnalyzeTimeOfDayPattern(IEnumerable<HabitLog> logs)
    {
        // Group by hour of CreatedAtUtc (proxy for log time)
        var hourFrequency = logs
            .GroupBy(l => l.CreatedAtUtc.Hour)
            .ToDictionary(g => g.Key, g => g.Count());

        var dominantHour = hourFrequency.MaxBy(kvp => kvp.Value);

        if (dominantHour.Value / (double)logs.Count() >= 0.6) // 60% threshold
        {
            // Consistent time pattern
            return new RoutineSuggestion
            {
                Title = "Time Pattern Detected",
                Description = $"You usually log this around {dominantHour.Key}:00",
                SuggestedAction = "Set a reminder for this time?",
                SuggestedTime = new TimeOnly(dominantHour.Key, 0),
                Confidence = ConfidenceLevel.Medium
            };
        }

        // ...return low confidence
    }
}
```

**New Application Query:**

```csharp
// Application/Routines/Queries/GetRoutineSuggestionsQuery.cs
public record GetRoutineSuggestionsQuery(Guid UserId) : IRequest<Result<IReadOnlyList<RoutineSuggestion>>>;

public class GetRoutineSuggestionsQueryHandler : IRequestHandler<GetRoutineSuggestionsQuery, Result<IReadOnlyList<RoutineSuggestion>>>
{
    private readonly IRoutineAnalysisService _analysisService;

    public async Task<Result<IReadOnlyList<RoutineSuggestion>>> Handle(
        GetRoutineSuggestionsQuery request,
        CancellationToken ct)
    {
        return await _analysisService.AnalyzeUserRoutinesAsync(request.UserId, ct);
    }
}
```

**API Endpoint:**

```csharp
// Api/Controllers/RoutinesController.cs
[ApiController]
[Route("api/routines")]
[Authorize]
public class RoutinesController : ControllerBase
{
    private readonly IMediator _mediator;

    [HttpGet("suggestions")]
    public async Task<IActionResult> GetSuggestions()
    {
        var userId = GetUserIdFromClaims();
        var query = new GetRoutineSuggestionsQuery(userId);
        var result = await _mediator.Send(query);

        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(result.Error);
    }
}
```

**Integration with AI Chat:**

Option 1: Proactive suggestions in AI responses
```csharp
// SystemPromptBuilder enhancement
if (routineSuggestions.Count > 0)
{
    sb.AppendLine("## Detected Patterns");
    sb.AppendLine("You may want to suggest these based on user's logging history:");
    foreach (var suggestion in routineSuggestions)
    {
        sb.AppendLine($"- {suggestion.Description}");
    }
}
```

Option 2: Separate endpoint for frontend to poll
```
GET /api/routines/suggestions → Frontend displays suggestions
User accepts → POST /api/habits with suggested parameters
```

**Recommendation:** Start with separate endpoint (Option 2), add to AI context in later phase.

**References:**
- [Pattern Recognition in Time Series | Baeldung](https://www.baeldung.com/cs/pattern-recognition-time-series)
- [Time Traveling with Data Science: Pattern Recognition, Motifs Discovery and the Matrix Profile](https://www.iese.fraunhofer.de/blog/pattern-recognition/)

---

## Data Flow Diagrams

### Current Chat Flow (Text Only)

```
User: "I want to run daily"
    ↓
ChatController.ProcessChat(message)
    ↓
ProcessUserChatCommand { UserId, Message }
    ↓
1. Load Habits + Tags from DB
    ↓
2. Build system prompt with context
    ↓
3. GeminiIntentService.InterpretAsync(message, habits, tags)
    ↓
4. Gemini API returns AiActionPlan { Actions[], AiMessage }
    ↓
5. Execute each action (CreateHabit, LogHabit, etc.)
    ↓
6. UnitOfWork.SaveChangesAsync() - single transaction
    ↓
ChatResponse { ExecutedActions[], AiMessage }
```

### Enhanced Chat Flow (All Features)

```
User: "I want to run daily" + [image of running shoes]
    ↓
ChatController.ProcessChat(message, image)
    ↓
ProcessUserChatCommand { UserId, Message, ImageData?, ImageMimeType? }
    ↓
1. Load Habits + Tags + UserFacts from DB
    ├─ GetRelevantFactsQuery → Search similar facts (optional: by embedding)
    ↓
2. Build system prompt with habits, tags, facts, routine suggestions
    ↓
3. GeminiIntentService.InterpretAsync(message, habits, tags, facts, image)
    ├─ Construct multimodal request with text + image
    ├─ Send to Gemini API
    ↓
4. Gemini returns AiActionPlan { Actions[], AiMessage }
    ├─ Actions may include: CreateHabit, LogHabit, AssignTag, StoreFact
    ↓
5. Execute actions with per-action commits
    ├─ For each action:
    │   ├─ Execute (CreateHabit, LogHabit, etc.)
    │   ├─ If success: SaveChangesAsync(), add to success list
    │   ├─ If failure: Log error, add to failure list, continue
    ↓
6. Build ChatResponse with ActionExecutionResult[] + AiMessage
    ↓
ChatResponse {
    Results: [
        { Success: true, Type: "CreateHabit", Description: "Created 'Running' habit" },
        { Success: false, Type: "AssignTag", ErrorMessage: "Tag not found" }
    ],
    AiMessage: "Created your running habit!"
}
```

### Routine Suggestion Flow

```
User: Opens app / navigates to "Suggestions"
    ↓
Frontend: GET /api/routines/suggestions
    ↓
GetRoutineSuggestionsQuery { UserId }
    ↓
RoutineAnalysisService.AnalyzeUserRoutinesAsync()
    ├─ Load HabitLogs (last 90 days) for user
    ├─ Group by HabitId
    ├─ For each habit:
    │   ├─ AnalyzeDayOfWeekPattern()
    │   ├─ AnalyzeTimeOfDayPattern()
    │   ├─ AnalyzeConsistency()
    │   └─ Generate RoutineSuggestion if confidence >= Medium
    ↓
RoutineSuggestion[] { Title, Description, SuggestedAction, Confidence, ... }
    ↓
Frontend: Display suggestions as triple-choice format
    ├─ Option 1: "Create weekly habit for MWF"
    ├─ Option 2: "Set reminder for 6am"
    ├─ Option 3: "Dismiss"
    ↓
User selects: POST /api/habits with pre-filled data
```

---

## Integration Points

### 1. Domain Layer

**New Entities:**
- `UserFact` (user memory storage)

**New Models:**
- `GeminiImageData` (multimodal request wrapper)
- `ActionExecutionResult` (detailed action feedback)
- `RoutineSuggestion` (pattern detection output)

**New Enums:**
- `ConfidenceLevel` (High/Medium/Low)
- Add `StoreFact` to `AiActionType`

**Modified Interfaces:**
- `IAiIntentService.InterpretAsync()` → add `GeminiImageData? image, IReadOnlyList<UserFact> facts`

**New Interfaces:**
- `IUserFactService` (embedding + semantic search)
- `IRoutineAnalysisService` (pattern detection)

### 2. Application Layer

**New Commands:**
- `CreateUserFactCommand` (store user memory)
- `DeleteUserFactCommand` (forget specific fact)

**New Queries:**
- `GetRelevantFactsQuery` (load facts for AI context)
- `GetAllUserFactsQuery` (list all facts for UI)
- `GetRoutineSuggestionsQuery` (trigger pattern analysis)

**Modified Commands:**
- `ProcessUserChatCommand` → add `byte[]? ImageData, string? ImageMimeType, bool EnableRoutineSuggestions`

**Modified Handlers:**
- `ProcessUserChatCommandHandler` → integrate facts loading, per-action commits, fact storage action

### 3. Infrastructure Layer

**New Services:**
- `GeminiUserFactService : IUserFactService` (embeddings + semantic search)
- `RoutineAnalysisService : IRoutineAnalysisService` (pattern detection algorithms)

**Modified Services:**
- `GeminiIntentService` → add multimodal request building (inline_data support)
- `SystemPromptBuilder` → add facts section, routine suggestions section

**Database Changes:**
- Add `UserFacts` DbSet to `OrbitDbContext`
- EF Core migration for `UserFact` table
- Add pgvector extension (optional, for embeddings)
- Configure `Embedding` property as `vector(768)` column (if using pgvector)

### 4. Api Layer

**Modified Controllers:**
- `ChatController.ProcessChat()` → accept `[FromForm] IFormFile? image`

**New Controllers:**
- `UserFactsController` (CRUD for user memory)
  - `GET /api/facts` → list all facts
  - `POST /api/facts` → manually add fact
  - `DELETE /api/facts/{id}` → remove fact
- `RoutinesController` (pattern suggestions)
  - `GET /api/routines/suggestions` → get suggestions

### 5. Configuration

**Modified Settings:**
- `GeminiSettings` → optionally add `EmbeddingModel = "text-embedding-004"`

**New Settings (if using separate embedding service):**
- `UserFactSettings` → `EnableEmbeddings`, `SimilarityThreshold`, `MaxFactsInContext`

---

## Architectural Patterns

### Pattern 1: Multimodal Request Builder

**What:** Construct Gemini requests with flexible content parts (text + images)

**When:** User uploads image with chat message

**Example:**
```csharp
public static class GeminiRequestBuilder
{
    public static GeminiRequest BuildMultimodalRequest(
        string systemPrompt,
        string userMessage,
        GeminiImageData? image = null)
    {
        var parts = new List<GeminiPart>
        {
            new() { Text = $"{systemPrompt}\n\nUser: {userMessage}" }
        };

        if (image is not null)
        {
            parts.Add(new GeminiPart
            {
                InlineData = new GeminiInlineData
                {
                    MimeType = image.MimeType,
                    Data = Convert.ToBase64String(image.Data)
                }
            });
        }

        return new GeminiRequest
        {
            Contents = [new GeminiContent { Parts = parts.ToArray() }],
            GenerationConfig = new GeminiGenerationConfig
            {
                Temperature = 0.1,
                ResponseMimeType = "application/json"
            }
        };
    }
}
```

### Pattern 2: Per-Action Transaction Commit

**What:** Commit database changes after each action succeeds, allowing partial success

**When:** AI returns multiple actions, some may fail

**Example:**
```csharp
var executionResults = new List<ActionExecutionResult>();

foreach (var action in plan.Actions)
{
    try
    {
        var actionResult = await ExecuteActionAsync(action, userId, ct);

        if (actionResult.IsSuccess)
        {
            await unitOfWork.SaveChangesAsync(ct); // Commit per action

            executionResults.Add(new ActionExecutionResult
            {
                Success = true,
                ActionType = action.Type.ToString(),
                ActionDescription = GetActionDescription(action)
            });
        }
        else
        {
            executionResults.Add(new ActionExecutionResult
            {
                Success = false,
                ActionType = action.Type.ToString(),
                ActionDescription = GetActionDescription(action),
                ErrorMessage = actionResult.Error
            });
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Action execution failed: {Type}", action.Type);

        executionResults.Add(new ActionExecutionResult
        {
            Success = false,
            ActionType = action.Type.ToString(),
            ErrorMessage = ex.Message
        });
    }
}

return Result.Success(new ChatResponse(executionResults, plan.AiMessage));
```

### Pattern 3: Semantic Memory with Embedding Retrieval

**What:** Store user facts with vector embeddings, retrieve by semantic similarity

**When:** AI needs user context, preferences, or past information

**Example:**
```csharp
// Store fact with embedding
var fact = UserFact.Create(userId, "I prefer morning workouts");
var embeddingResult = await userFactService.GenerateEmbeddingAsync(fact.Content);
if (embeddingResult.IsSuccess)
{
    fact.SetEmbedding(embeddingResult.Value);
}
await factRepository.AddAsync(fact);
await unitOfWork.SaveChangesAsync();

// Retrieve relevant facts
var messageEmbedding = await userFactService.GenerateEmbeddingAsync(userMessage);
var relevantFacts = await userFactService.SearchSimilarFactsAsync(
    userId,
    messageEmbedding.Value,
    maxResults: 5
);

// Load into AI context
var prompt = SystemPromptBuilder.BuildSystemPrompt(habits, tags, relevantFacts);
```

**Alternative (Simple):** No embeddings, chronological or keyword-based retrieval:
```csharp
// Simple: Load most recent facts
var recentFacts = await factRepository.FindAsync(
    f => f.UserId == userId,
    ct,
    orderBy: q => q.OrderByDescending(f => f.CreatedAtUtc),
    take: 10
);
```

### Pattern 4: Time-Series Pattern Detection

**What:** Analyze HabitLog timestamps to detect routines, consistency, preferred times

**When:** User has sufficient log history (30+ logs or 2+ weeks)

**Example:**
```csharp
public RoutineSuggestion AnalyzeDayOfWeekPattern(IGrouping<Guid, HabitLog> habitLogs)
{
    var logs = habitLogs.ToList();
    var totalLogs = logs.Count;

    // Group by day of week
    var dayFrequency = logs
        .GroupBy(l => l.Date.DayOfWeek)
        .Select(g => new { Day = g.Key, Count = g.Count(), Percentage = g.Count() / (double)totalLogs })
        .Where(x => x.Percentage >= 0.7) // 70% threshold
        .Select(x => x.Day)
        .OrderBy(d => d)
        .ToList();

    if (dayFrequency.Count >= 2 && dayFrequency.Count <= 5)
    {
        var habit = await habitRepository.GetByIdAsync(habitLogs.Key);

        return new RoutineSuggestion
        {
            Title = $"{habit.Title} Weekly Pattern",
            Description = $"You do this {dayFrequency.Count}x per week: {string.Join(", ", dayFrequency)}",
            SuggestedAction = "Convert to recurring habit with specific days?",
            SuggestedFrequency = FrequencyUnit.Day,
            SuggestedQuantity = 1,
            SuggestedDays = dayFrequency,
            RelatedHabitId = habit.Id,
            Confidence = ConfidenceLevel.High
        };
    }

    return null; // No strong pattern
}
```

---

## Anti-Patterns to Avoid

### Anti-Pattern 1: Synchronous Embedding Generation in Request Path

**What:** Generating embeddings inline during chat request

**Why bad:** Adds 200-500ms latency per fact, blocks response

**Instead:** Generate embeddings asynchronously after fact creation, or skip embeddings initially

```csharp
// BAD
var fact = UserFact.Create(userId, content);
var embedding = await GenerateEmbeddingAsync(content); // Blocks request
fact.SetEmbedding(embedding);

// GOOD
var fact = UserFact.Create(userId, content);
await factRepository.AddAsync(fact);
await unitOfWork.SaveChangesAsync();

// Background job
BackgroundJob.Enqueue(() => GenerateAndStoreEmbedding(fact.Id));
```

### Anti-Pattern 2: Loading All Facts into Every AI Request

**What:** Including all user facts in system prompt regardless of relevance

**Why bad:** Token waste, context pollution, slower responses

**Instead:** Use semantic search or limit to recent/relevant facts (5-10 max)

```csharp
// BAD
var allFacts = await factRepository.FindAsync(f => f.UserId == userId);
var prompt = BuildPrompt(habits, tags, allFacts); // Could be 100+ facts

// GOOD
var relevantFacts = await GetRelevantFactsQuery(userId, userMessage, maxResults: 5);
var prompt = BuildPrompt(habits, tags, relevantFacts); // Only 5 relevant facts
```

### Anti-Pattern 3: Storing Raw Image Data in Database

**What:** Storing full image bytes in SQL table

**Why bad:** Database bloat, slow queries, backup issues

**Instead:** Store images in blob storage (Azure Blob, S3, file system), reference URI in database

```csharp
// BAD
public class ChatMessage
{
    public byte[]? ImageData { get; set; } // Could be 5MB+
}

// GOOD
public class ChatMessage
{
    public string? ImageStorageUri { get; set; } // "blob://chat-images/abc123.jpg"
}
```

**Note:** For Orbit, images are ephemeral (sent to Gemini, not stored), so no database storage needed.

### Anti-Pattern 4: Running Pattern Analysis on Every Request

**What:** Calling `AnalyzeUserRoutinesAsync()` in ProcessUserChatCommand

**Why bad:** Expensive computation (queries 90 days of logs), slows chat response

**Instead:** Run analysis on-demand via separate endpoint, or background job (daily/weekly)

```csharp
// BAD
public async Task<Result<ChatResponse>> Handle(ProcessUserChatCommand request, CancellationToken ct)
{
    var suggestions = await routineAnalysisService.AnalyzeUserRoutinesAsync(request.UserId); // Slow!
    // ...
}

// GOOD - Separate endpoint
[HttpGet("api/routines/suggestions")]
public async Task<IActionResult> GetSuggestions()
{
    var suggestions = await mediator.Send(new GetRoutineSuggestionsQuery(UserId));
    return Ok(suggestions);
}
```

### Anti-Pattern 5: Single Transaction for All Actions (Current)

**What:** Commit all actions at once via single `SaveChangesAsync()` call

**Why bad:** If one action fails or SaveChanges fails, ALL actions are lost

**Instead:** Per-action commits with detailed result tracking (see Pattern 2)

---

## Suggested Build Order

Based on dependencies and complexity:

### Phase 1: Multi-Action Execution (Foundation)
**Why first:** Enables better error handling for all AI features, no external dependencies

**Components:**
1. `ActionExecutionResult` model (Domain/Models)
2. Modify `ChatResponse` to use `ActionExecutionResult[]`
3. Update `ProcessUserChatCommandHandler` with per-action commits
4. Update `ChatController` response mapping

**Integration:** Refactor existing chat flow

**Estimated Complexity:** Low

---

### Phase 2: User Fact Storage (Simple Version)
**Why second:** Adds AI memory without complex embeddings, high value

**Components:**
1. `UserFact` entity (Domain/Entities)
2. Add `UserFacts` DbSet + migration (Infrastructure/Persistence)
3. `CreateUserFactCommand`, `GetRelevantFactsQuery` (Application)
4. `UserFactsController` CRUD endpoints (Api)
5. Add `StoreFact` to `AiActionType` enum
6. Enhance `SystemPromptBuilder` to include facts
7. Update `ProcessUserChatCommand` to load recent facts (no embeddings)

**Integration:** Extend chat flow with fact loading + storage action

**Estimated Complexity:** Medium

---

### Phase 3: Gemini Vision Integration
**Why third:** Requires multimodal API changes, builds on Phase 1 error handling

**Components:**
1. `GeminiImageData` model (Domain/Models)
2. Add image parameters to `IAiIntentService.InterpretAsync()`
3. Modify `GeminiIntentService` to support `inline_data` parts
4. Update `ProcessUserChatCommand` with image properties
5. Modify `ChatController.ProcessChat()` to accept `IFormFile`

**Integration:** Extend AI service with multimodal requests

**Estimated Complexity:** Medium

---

### Phase 4: Routine Inference
**Why fourth:** Most complex, requires pattern detection algorithms, least critical

**Components:**
1. `RoutineSuggestion` model + `ConfidenceLevel` enum (Domain)
2. `IRoutineAnalysisService` interface (Domain/Interfaces)
3. `RoutineAnalysisService` with pattern detection methods (Infrastructure/Services)
4. `GetRoutineSuggestionsQuery` (Application/Routines/Queries)
5. `RoutinesController` (Api/Controllers)

**Integration:** New isolated feature, separate endpoint

**Estimated Complexity:** High

---

### Phase 5: Semantic Search (Optional Enhancement)
**Why last:** Requires pgvector setup, embeddings API, only improves Phase 2

**Components:**
1. Install pgvector extension in PostgreSQL
2. Add `Embedding` property to `UserFact` with `vector(768)` column type
3. `IUserFactService` interface (Domain/Interfaces)
4. `GeminiUserFactService` with embedding generation + similarity search (Infrastructure)
5. Modify `GetRelevantFactsQuery` to use semantic search instead of chronological

**Integration:** Enhance Phase 2 fact retrieval

**Estimated Complexity:** High

---

## Phase Dependencies

```
Phase 1 (Multi-Action)
    ├─→ Phase 2 (User Facts - Simple) ─→ Phase 5 (Semantic Search)
    └─→ Phase 3 (Gemini Vision)

Phase 4 (Routine Inference) - Independent, can be built anytime
```

**Recommended Order:**
1. Multi-action execution (enables better error handling)
2. User facts - simple version (high value, low complexity)
3. Gemini Vision (moderate complexity, visible feature)
4. Routine inference (complex, can be deferred)
5. Semantic search (optional enhancement to Phase 2)

---

## Scalability Considerations

| Concern | At 100 users | At 10K users | At 1M users |
|---------|--------------|--------------|-------------|
| **Gemini API rate limits** | No issue (free tier: 15 RPM) | Need paid tier | Need request queuing + caching |
| **Image processing** | Direct inline base64 | Consider File API | Blob storage + CDN |
| **UserFacts storage** | PostgreSQL sufficient | Add indexing on Embedding | Separate vector DB (Pinecone, Weaviate) |
| **Pattern analysis** | On-demand queries | Background jobs (daily) | Pre-computed suggestions cache |
| **Multi-action commits** | Per-action is fine | Monitor deadlocks | Consider saga pattern |

**Current Architecture Handles:** Up to 10K users without major changes

**Future Optimizations:**
- Caching layer for UserFacts retrieval (Redis)
- Async background jobs for embeddings + pattern analysis (Hangfire)
- Separate read models for routine suggestions (CQRS read side)

---

## Sources

- [Image understanding | Gemini API | Google AI for Developers](https://ai.google.dev/gemini-api/docs/image-understanding)
- [Multimodal Prompting with Gemini: Working with Images - Google Cloud Applied AI Engineering](https://googlecloudplatform.github.io/applied-ai-engineering-samples/genai-on-vertex-ai/gemini/prompting_recipes/multimodal/multimodal_prompting_image/)
- [What Is AI Agent Memory? | IBM](https://www.ibm.com/think/topics/ai-agent-memory)
- [Design Patterns for Long-Term Memory in LLM-Powered Architectures](https://serokell.io/blog/design-patterns-for-long-term-memory-in-llm-powered-architectures)
- [Domain events: Design and implementation - .NET | Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/domain-events-design-implementation)
- [CQRS and exception handling · Enterprise Craftsmanship](https://enterprisecraftsmanship.com/posts/cqrs-exception-handling/)
- [Build a .NET AI vector search app | Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/ai/quickstarts/build-vector-search-app)
- [Pattern Recognition in Time Series | Baeldung on Computer Science](https://www.baeldung.com/cs/pattern-recognition-time-series)
- [Time Traveling with Data Science: Pattern Recognition, Motifs Discovery and the Matrix Profile (Part 4) - Blog des Fraunhofer IESE](https://www.iese.fraunhofer.de/blog/pattern-recognition/)
- [Semantic Search Development with C# using Ollama & VectorDB orchestrate in .NET Aspire | by Mehmet Ozkaya | Medium](https://mehmetozkaya.medium.com/semantic-search-development-with-c-using-ollama-vectordb-orchestrate-in-net-aspire-d82eec73696a)
