# Phase 5: User Learning System - Research

**Researched:** 2026-02-09
**Domain:** AI fact extraction, user memory persistence, and personalization systems
**Confidence:** MEDIUM-HIGH

## Summary

Phase 5 implements a user learning system where the AI extracts and persists key facts from conversations, enabling personalized responses in subsequent interactions. Research reveals this is a rapidly evolving domain with established patterns emerging in 2025-2026.

The core architecture involves a **dual-pass LLM approach**: the first pass generates AI responses (already implemented), and a second pass extracts structured facts from the conversation for persistence. Facts are stored in PostgreSQL using EF Core (no vector embeddings needed for MVP - chronological retrieval suffices), loaded into the system prompt for context, and exposed via REST API for user control.

Current best practices emphasize **user control over memory** (view, delete individual facts), **fact deduplication** (ADD/UPDATE/DELETE/NOOP operations), and **timestamp-based fact expiration**. The primary risk is **hallucinated fact extraction**, mitigated through structured output constraints, domain validation, and user control mechanisms.

**Primary recommendation:** Implement a simple, transparent fact extraction system with strong user control. Start with chronological fact retrieval (no vector search) and focus on extraction accuracy over sophistication. Prioritize user trust through visibility and deletion controls.

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Entity Framework Core | 10.0.0 | Fact persistence with PostgreSQL | Already in stack, proven ORM for .NET |
| MediatR | 14.0.0 | CQRS for fact commands/queries | Already in stack, established pattern |
| FluentValidation | 11.x | Fact validation | Already in stack for request validation |
| System.Text.Json | Built-in | Fact deserialization from LLM | Already used for AiActionPlan deserialization |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| NJsonSchema | Latest | Generate JSON schema for LLM structured output | If using explicit schema generation for fact extraction (recommended for accuracy) |
| Npgsql | 10.0.0 | PostgreSQL provider | Already in stack |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| PostgreSQL tables | pgvector + embeddings | Semantic search capability vs added complexity. OUT OF SCOPE per user decision. |
| Dual-pass extraction | Single-pass with fact fields in AiActionPlan | Simpler but less accurate - facts mixed with action logic |
| Manual JSON schema | Semantic Kernel | Better structured outputs but adds major dependency |

**Installation:**
```bash
# No new packages needed for MVP
# All required packages already in stack
# If adding NJsonSchema for explicit schema generation:
dotnet add package NJsonSchema --version 11.1.0
```

## Architecture Patterns

### Recommended Project Structure
```
src/
├── Orbit.Domain/
│   ├── Entities/
│   │   └── UserFact.cs                    # New entity
│   ├── Interfaces/
│   │   └── IFactExtractionService.cs      # New interface
│   └── Models/
│       └── ExtractedFacts.cs              # LLM response model
├── Orbit.Application/
│   ├── Chat/Commands/
│   │   └── ProcessUserChatCommand.cs      # MODIFY: Add fact extraction after actions
│   └── UserFacts/
│       ├── Commands/
│       │   ├── DeleteUserFactCommand.cs   # New
│       │   └── ExtractAndSaveFactsCommand.cs  # New (internal)
│       └── Queries/
│           └── GetUserFactsQuery.cs       # New
├── Orbit.Infrastructure/
│   ├── Persistence/
│   │   └── OrbitDbContext.cs              # MODIFY: Add UserFacts DbSet
│   └── Services/
│       ├── SystemPromptBuilder.cs         # MODIFY: Add facts to prompt
│       └── GeminiFactExtractionService.cs # New
└── Orbit.Api/
    └── Controllers/
        └── UserFactsController.cs         # New: GET /api/user-facts, DELETE /api/user-facts/{id}
```

### Pattern 1: Dual-Pass Fact Extraction

**What:** Separate fact extraction from action execution using two sequential LLM calls

**When to use:** When accuracy and separation of concerns matter more than token cost

**Example:**
```csharp
// In ProcessUserChatCommandHandler
// After executing actions and responding to user...

// Second pass: Extract facts from the conversation
var factExtractionResult = await _factExtractionService.ExtractFactsAsync(
    request.Message,
    plan.AiMessage,
    request.UserId,
    cancellationToken);

if (factExtractionResult.IsSuccess && factExtractionResult.Value.Facts.Count > 0)
{
    await _mediator.Send(new ExtractAndSaveFactsCommand(
        request.UserId,
        factExtractionResult.Value.Facts),
        cancellationToken);
}

return Result.Success(new ChatResponse(plan.AiMessage, actionResults));
```

**Source:** Based on G&O (Generate & Organize) dual-pass pipeline pattern from [Medium - Structured Output Generation](https://medium.com/@emrekaratas-ai/structured-output-generation-in-llms-json-schema-and-grammar-based-decoding-6a5c58b698a6)

### Pattern 2: Fact CRUD Operations with Timestamps

**What:** Use ADD/UPDATE/DELETE/NOOP pattern with timestamp-based versioning

**When to use:** For managing fact lifecycle and preventing duplicates

**Example:**
```csharp
public class UserFact : Entity
{
    public Guid UserId { get; private set; }
    public string FactText { get; private set; } = null!;
    public string? Category { get; private set; }  // "preference", "routine", "context"
    public DateTime ExtractedAtUtc { get; private set; }
    public DateTime? UpdatedAtUtc { get; private set; }

    // Soft delete pattern
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }

    public void Update(string newFactText)
    {
        FactText = newFactText;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void SoftDelete()
    {
        IsDeleted = true;
        DeletedAtUtc = DateTime.UtcNow;
    }
}
```

**Source:** Based on [Milan Jovanovic - Implementing Soft Delete with EF Core](https://www.milanjovanovic.tech/blog/implementing-soft-delete-with-ef-core)

### Pattern 3: System Prompt Injection with User Facts

**What:** Load facts into system prompt for AI personalization

**When to use:** Every chat request after facts exist

**Example:**
```csharp
public static string BuildSystemPrompt(
    IReadOnlyList<Habit> activeHabits,
    IReadOnlyList<Tag> userTags,
    IReadOnlyList<UserFact> userFacts)  // NEW PARAMETER
{
    var sb = new StringBuilder();

    // ... existing prompt sections ...

    sb.AppendLine();
    sb.AppendLine("## What You Know About This User");
    if (userFacts.Count == 0)
    {
        sb.AppendLine("(nothing yet - learn as you go)");
    }
    else
    {
        foreach (var fact in userFacts.OrderByDescending(f => f.ExtractedAtUtc))
        {
            sb.AppendLine($"- {fact.FactText} (learned: {fact.ExtractedAtUtc:yyyy-MM-dd})");
        }
    }

    return sb.ToString();
}
```

**Source:** Pattern adapted from [OpenAI Cookbook - Context Engineering for Personalization](https://cookbook.openai.com/examples/agents_sdk/context_personalization)

### Pattern 4: Structured Output Schema for Fact Extraction

**What:** Use explicit JSON schema to constrain LLM fact extraction format

**When to use:** To reduce hallucination risk and ensure parseable output

**Example:**
```csharp
// Fact extraction prompt
var extractionPrompt = """
# Extract Key Facts from Conversation

Analyze this conversation and extract ONLY factual information the user shared about themselves.

**User message:** {userMessage}
**AI response:** {aiResponse}

Return JSON with this EXACT structure:
{
  "facts": [
    {
      "factText": "clear, concise fact statement",
      "category": "preference" | "routine" | "context"
    }
  ]
}

Rules:
- Extract ONLY explicit statements by the user
- Do NOT infer or assume facts not directly stated
- Each fact should be a standalone sentence
- Category: preference (likes/dislikes), routine (schedules/habits), context (situation/background)
- If no facts to extract, return empty array
- NEVER extract actions, requests, or commands
""";
```

**Source:** Based on [Arize AI - Structured Data Extraction](https://arize.com/blog-course/structured-data-extraction-openai-function-calling/)

### Anti-Patterns to Avoid

- **Single-pass extraction mixed with actions:** Fact extraction logic entangled with action execution reduces accuracy and maintainability. Keep them separate.
- **Unbounded fact storage:** Without deduplication or expiration, fact table grows infinitely. Implement UPDATE logic to merge similar facts.
- **Implicit fact extraction:** User doesn't know what's being stored. Always show extracted facts or provide visibility API.
- **Hard delete only:** Users lose trust if they can't control what's remembered. Implement soft delete with user-initiated purge.
- **Vector embeddings for MVP:** Adds complexity without proven value for small fact sets. Start simple with chronological retrieval.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Fact similarity detection | Custom string matching algorithm | LLM-based semantic comparison or simple exact match for MVP | Edge cases in natural language similarity are complex |
| JSON schema generation | Manual schema strings | NJsonSchema library or rely on Gemini's responseMimeType | Schema drift and validation errors |
| Soft delete infrastructure | Custom IsDeleted logic per entity | EF Core SaveChangesInterceptor with ISoftDeletable interface | Cross-cutting concern, better handled globally |
| Fact deduplication | Custom merge logic | LLM call to determine ADD/UPDATE/DELETE/NOOP | Natural language comparison is LLM's strength |

**Key insight:** Fact extraction is an LLM-native task. Don't try to outsmart the model with custom heuristics - use structured prompts and let the model decide what constitutes a fact, duplicate, or update.

## Common Pitfalls

### Pitfall 1: Hallucinated Fact Extraction

**What goes wrong:** LLM invents facts not actually stated by user, polluting memory with false information

**Why it happens:** LLMs are probabilistic and trained to be helpful, sometimes inferring or filling gaps

**How to avoid:**
- Use explicit instructions: "Extract ONLY explicit statements by the user"
- Include negative examples in prompt
- Use structured output with schema validation
- Implement confidence scoring (optional for MVP)
- Provide user visibility and delete control

**Warning signs:** Facts appear that seem reasonable but weren't explicitly stated. User says "I never said that."

**Source:** [Infomineo - Stop AI Hallucinations Guide 2025](https://infomineo.com/artificial-intelligence/stop-ai-hallucinations-detection-prevention-verification-guide-2025/)

### Pitfall 2: Prompt Injection via User Facts

**What goes wrong:** User intentionally includes instructions in conversation that get stored as facts, then executed in future prompts

**Why it happens:** System prompt cannot distinguish between trusted facts and untrusted user input in context window

**How to avoid:**
- Sanitize fact text before storage (remove instruction-like patterns)
- Keep fact section clearly separated in system prompt with explicit boundaries
- Use prompt partitioning techniques (if available in API)
- Monitor for suspicious patterns in extracted facts
- User control allows deletion of problematic facts

**Warning signs:** Facts contain phrases like "ignore previous instructions", "system:", "you must", or imperative commands

**Source:** [OWASP - LLM01:2025 Prompt Injection](https://genai.owasp.org/llmrisk/llm01-prompt-injection/)

### Pitfall 3: Fact Table Bloat Without Deduplication

**What goes wrong:** Every conversation creates new facts, many duplicates or near-duplicates, degrading performance

**Why it happens:** No merge/update logic - only ADD operations

**How to avoid:**
- Implement LLM-based duplicate detection before persisting
- Ask LLM: "Does this new fact UPDATE an existing fact or ADD a new one?"
- Provide existing facts as context to extraction prompt
- Set soft delete + expiration policy (e.g., facts older than 6 months auto-expire)

**Warning signs:** Fact count grows linearly with conversation count. Prompt becomes bloated with redundant information.

**Source:** [Mem0 - AI Memory Layer Guide](https://mem0.ai/blog/ai-memory-layer-guide)

### Pitfall 4: FluentValidation Blocking Partial Success

**What goes wrong:** Validation pipeline rejects entire request if one fact has validation error

**Why it happens:** Same issue encountered in Phase 4 bulk operations - validator runs before handler

**How to avoid:**
- Use domain validation inside try-catch blocks (matching ProcessUserChatCommand pattern)
- Keep FluentValidation for structural checks only (not empty, max length)
- Let UserFact.Create() domain factory handle business rules
- Per-fact error handling with partial success response

**Warning signs:** Test expects 200 with mixed results, gets 400 BadRequest. Mirrors Phase 4 Gap 1.

**Source:** Phase 4 VERIFICATION.md - BulkCreateHabitsCommandValidator gap analysis

### Pitfall 5: Ignoring User Timezone for Fact Timestamps

**What goes wrong:** Facts show extraction time in UTC, confusing users in different timezones

**Why it happens:** Store DateTime.UtcNow but display without timezone conversion

**How to avoid:**
- Store ExtractedAtUtc as UTC (correct)
- Convert to user timezone when displaying in API response
- Use existing User.TimeZone infrastructure
- Follow pattern from HabitLog.Date resolution

**Warning signs:** User sees "extracted at 2026-02-09 03:00" when their local time was 10:00 PM the previous day

**Source:** Existing codebase pattern in ProcessUserChatCommandHandler.GetUserToday()

## Code Examples

Verified patterns from official sources and existing codebase:

### Fact Extraction Service Interface

```csharp
// src/Orbit.Domain/Interfaces/IFactExtractionService.cs
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IFactExtractionService
{
    Task<Result<ExtractedFacts>> ExtractFactsAsync(
        string userMessage,
        string? aiResponse,
        Guid userId,
        CancellationToken cancellationToken = default);
}
```

### Fact Extraction Response Model

```csharp
// src/Orbit.Domain/Models/ExtractedFacts.cs
namespace Orbit.Domain.Models;

public record ExtractedFacts
{
    public required List<FactCandidate> Facts { get; init; }
}

public record FactCandidate
{
    public required string FactText { get; init; }
    public required string Category { get; init; }  // "preference" | "routine" | "context"
}
```

### UserFact Entity

```csharp
// src/Orbit.Domain/Entities/UserFact.cs
using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class UserFact : Entity
{
    public Guid UserId { get; private set; }
    public string FactText { get; private set; } = null!;
    public string? Category { get; private set; }
    public DateTime ExtractedAtUtc { get; private set; }
    public DateTime? UpdatedAtUtc { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }

    private UserFact() { }

    public static Result<UserFact> Create(Guid userId, string factText, string? category = null)
    {
        if (string.IsNullOrWhiteSpace(factText))
            return Result.Failure<UserFact>("Fact text is required");

        if (factText.Length > 500)
            return Result.Failure<UserFact>("Fact text cannot exceed 500 characters");

        // Basic prompt injection detection
        var suspiciousPatterns = new[] { "ignore", "system:", "you must", "instruction:" };
        if (suspiciousPatterns.Any(p => factText.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return Result.Failure<UserFact>("Fact text contains suspicious patterns");

        return Result.Success(new UserFact
        {
            UserId = userId,
            FactText = factText.Trim(),
            Category = category?.Trim(),
            ExtractedAtUtc = DateTime.UtcNow
        });
    }

    public void Update(string newFactText)
    {
        FactText = newFactText.Trim();
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void SoftDelete()
    {
        IsDeleted = true;
        DeletedAtUtc = DateTime.UtcNow;
    }
}
```

### DbContext Configuration

```csharp
// src/Orbit.Infrastructure/Persistence/OrbitDbContext.cs
// Add to OnModelCreating:

modelBuilder.Entity<UserFact>(entity =>
{
    entity.HasIndex(f => new { f.UserId, f.IsDeleted });
    entity.HasQueryFilter(f => !f.IsDeleted);  // Global query filter for soft delete
});
```

**Source:** [JetBrains - Soft Delete with EF Core](https://blog.jetbrains.com/dotnet/2023/06/14/how-to-implement-a-soft-delete-strategy-with-entity-framework-core/)

### REST API Endpoints

```csharp
// src/Orbit.Api/Controllers/UserFactsController.cs
[ApiController]
[Route("api/user-facts")]
[Authorize]
public class UserFactsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetUserFacts()
    {
        var userId = User.GetUserId();
        var result = await mediator.Send(new GetUserFactsQuery(userId));

        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(result.Error);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteUserFact(Guid id)
    {
        var userId = User.GetUserId();
        var result = await mediator.Send(new DeleteUserFactCommand(userId, id));

        return result.IsSuccess
            ? NoContent()
            : NotFound(result.Error);
    }
}
```

**Source:** REST API best practices from [Microsoft Azure - API Design Best Practices](https://learn.microsoft.com/en-us/azure/architecture/best-practices/api-design)

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Full conversation history storage | Key facts extraction only | 2025-2026 | Reduced token costs, faster retrieval, less PII risk |
| Vector embeddings for all memory | Chronological + optional semantic search | 2025-2026 | Simpler MVP, add embeddings only when proven valuable |
| JSON mode (valid JSON) | Structured Outputs (schema adherence) | 2024-2025 (OpenAI) | Higher extraction accuracy, fewer deserialization errors |
| Single memory type | Hierarchical memory (session, user, agent) | 2025-2026 | Better context organization |
| No user control | Explicit memory CRUD APIs | 2025-2026 | Privacy compliance, user trust |

**Deprecated/outdated:**
- **JSON mode without schema:** Replaced by structured outputs with explicit schema validation (OpenAI Structured Outputs, Gemini responseMimeType)
- **Hard delete only:** Modern systems use soft delete with user-controlled purge for audit and recovery
- **Embedding-first approach:** Start simple with chronological retrieval, add embeddings when scale demands it

## Open Questions

1. **Should we implement fact deduplication in MVP?**
   - What we know: Mem0 and other systems use LLM-based ADD/UPDATE/DELETE/NOOP pattern
   - What's unclear: Whether fact volume justifies complexity for initial release
   - Recommendation: Start with simple ADD-only, add deduplication in Phase 6 if fact bloat occurs

2. **How many facts to include in system prompt?**
   - What we know: Context window limits exist, but Gemini 2.5 Flash has large capacity
   - What's unclear: Optimal fact count before diminishing returns or confusion
   - Recommendation: Start with all active facts (ordered by recency), monitor prompt size, add pagination if >50 facts

3. **Should facts have expiration policy?**
   - What we know: Some systems auto-expire facts older than 6-12 months
   - What's unclear: Whether habit-tracking context has shelf life (user preferences may be stable)
   - Recommendation: No auto-expiration for MVP, let users manually delete outdated facts

4. **Single extraction call or batch processing?**
   - What we know: Dual-pass adds latency and token cost
   - What's unclear: Whether to extract facts synchronously (after each chat) or asynchronously (batch)
   - Recommendation: Synchronous for MVP (simpler), async batch if performance issues arise

## Sources

### Primary (HIGH confidence)
- [Mem0 - Graph Memory for AI Agents (January 2026)](https://mem0.ai/blog/graph-memory-solutions-ai-agents) - Memory architecture patterns
- [Arize AI - Structured Data Extraction](https://arize.com/blog-course/structured-data-extraction-openai-function-calling/) - Fact extraction techniques
- [OWASP - LLM01:2025 Prompt Injection](https://genai.owasp.org/llmrisk/llm01-prompt-injection/) - Security considerations
- [Microsoft Azure - API Design Best Practices](https://learn.microsoft.com/en-us/azure/architecture/best-practices/api-design) - REST API patterns
- [Milan Jovanovic - Implementing Soft Delete with EF Core](https://www.milanjovanovic.tech/blog/implementing-soft-delete-with-ef-core) - EF Core patterns
- [OpenAI Cookbook - Context Engineering for Personalization](https://cookbook.openai.com/examples/agents_sdk/context_personalization) - Memory injection patterns

### Secondary (MEDIUM confidence)
- [Medium - Structured Output Generation (G&O pattern)](https://medium.com/@emrekaratas-ai/structured-output-generation-in-llms-json-schema-and-grammar-based-decoding-6a5c58b698a6) - Dual-pass extraction
- [Getting structured JSON from LLMs in C#](https://evdbogaard.nl/posts/getting-structured-json-from-llms-in-csharp/) - C# deserialization patterns
- [Infomineo - Stop AI Hallucinations Guide 2025](https://infomineo.com/artificial-intelligence/stop-ai-hallucinations-detection-prevention-verification-guide-2025/) - Hallucination mitigation
- [AI Memory vs Context (Plurality Network)](https://plurality.network/blogs/ai-memory-vs-ai-context/) - Memory system design
- [JetBrains - Soft Delete with EF Core](https://blog.jetbrains.com/dotnet/2023/06/14/how-to-implement-a-soft-delete-strategy-with-entity-framework-core/) - EF Core soft delete
- [Mem0 - AI Memory Layer Guide](https://mem0.ai/blog/ai-memory-layer-guide) - Memory operations (ADD/UPDATE/DELETE/NOOP)

### Tertiary (LOW confidence)
- [WebSearch results on fact categorization, preferences, routines] - General memory categorization patterns (needs verification in production)
- [WebSearch results on deduplication strategies] - General deduplication concepts, not LLM-specific

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - All libraries already in use, verified in codebase
- Architecture patterns: MEDIUM-HIGH - Dual-pass extraction is emerging standard (2025-2026), patterns verified in multiple sources
- Fact extraction specifics: MEDIUM - Best practices still evolving, less industry consensus than established domains
- Pitfalls: HIGH - Prompt injection, hallucination, and validation issues well-documented
- EF Core patterns: HIGH - Soft delete and entity patterns are established .NET practices

**Research date:** 2026-02-09
**Valid until:** ~60 days (stable domain but rapidly evolving with new LLM capabilities)
**Re-verification recommended before:** 2026-04-10

**Notes:**
- No CONTEXT.md exists for this phase - all decisions are open for discussion
- pgvector/embeddings confirmed OUT OF SCOPE per prior decisions
- Conversation history storage confirmed OUT OF SCOPE per prior decisions
- Phase 4 patterns (multi-action, partial success, domain validation) should inform implementation
- Existing SystemPromptBuilder and GeminiIntentService provide foundation for extension
