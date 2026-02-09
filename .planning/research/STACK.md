# Technology Stack - AI Intelligence Enhancements

**Project:** Orbit v1.1 - AI Intelligence Features
**Researched:** 2026-02-09
**Overall Confidence:** HIGH

## Executive Summary

This research covers stack additions needed for v1.1 AI intelligence enhancements: multi-action AI output, Gemini Vision image processing, AI user fact extraction/storage, and routine inference. The existing .NET 10 + PostgreSQL + HttpClient architecture requires minimal new dependencies. Most features can be implemented with existing packages or standard library enhancements. Only routine inference requires a new ML.NET package.

**Key Finding:** The architecture is well-positioned for these enhancements. Multi-action already exists in the codebase, Gemini Vision uses the same REST API, fact storage fits existing EF Core patterns, and routine inference needs only Microsoft.ML.TimeSeries.

---

## Recommended Stack

### Core Technologies (No Changes)

| Technology | Version | Purpose | Rationale |
|------------|---------|---------|-----------|
| .NET | 10.0 | Runtime & framework | Already in use, supports all new features |
| C# | 13 | Language | Already in use |
| PostgreSQL | Latest | Database with JSON support | Already in use, JSONB perfect for fact storage |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.0 | Database provider | Already in use, supports JSON columns |
| System.Text.Json | Built-in (.NET 10) | JSON serialization | Already in use, handles all serialization needs |
| HttpClient | Built-in (.NET 10) | HTTP communication | Already in use for Gemini API, works for Vision |

**Rationale:** No core technology changes needed. Existing stack already supports all required capabilities.

---

## Supporting Libraries

### NEW: Time Series Analysis

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Microsoft.ML.TimeSeries | 5.0.0 | Routine pattern detection, anomaly detection | Required for detecting user logging patterns and inferring optimal habit schedules |

**Installation:**
```bash
dotnet add src/Orbit.Infrastructure/Orbit.Infrastructure.csproj package Microsoft.ML.TimeSeries --version 5.0.0
```

**Why:** ML.NET's TimeSeries library provides Singular Spectrum Analysis (SSA) for pattern detection and forecasting. Enables detecting when users typically log habits (morning routines, evening activities) and suggesting optimal schedules.

**Confidence:** HIGH - Official Microsoft package, version 5.0.0 released, stable API, excellent .NET integration.

**Sources:**
- [Microsoft.ML.TimeSeries NuGet Package](https://www.nuget.org/packages/Microsoft.ML.TimeSeries/)
- [ML.NET Time Series Tutorial](https://learn.microsoft.com/en-us/dotnet/machine-learning/tutorials/time-series-demand-forecasting)

---

### OPTIONAL: Enhanced Image Processing

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| None required | - | Image validation & processing | .NET 10 built-in APIs sufficient for base64 encoding |

**Why NOT SixLabors.ImageSharp:**
- **Licensing concern:** Dual-license model (Apache 2.0 + Commercial) with revenue thresholds may complicate commercial use
- **Unnecessary for MVP:** Gemini Vision accepts base64-encoded images directly; no need for resize/crop/format conversion in v1.1
- **.NET 10 sufficient:** System.IO and System.Convert provide base64 encoding without external dependencies

**When to reconsider:** If future requirements include image preprocessing (resize, crop, format conversion, validation of dimensions) before sending to Gemini.

**Confidence:** HIGH - .NET 10 removed Uri length limits specifically to support data URIs for base64 images.

**Sources:**
- [.NET 10 Networking Improvements](https://devblogs.microsoft.com/dotnet/dotnet-10-networking-improvements/)
- [SixLabors ImageSharp](https://sixlabors.com/products/imagesharp/)

---

## What NOT to Use

### JSON Schema Validation Libraries

**DO NOT ADD:** JsonSchema.Net, NJsonSchema, Microsoft.Json.Schema.Validation

**Why:** System.Text.Json in .NET 9+ includes built-in JSON schema export capabilities. For AI response validation, explicit schema enforcement is unnecessary - FluentValidation already validates command DTOs after deserialization.

**Alternative:** Continue using FluentValidation for request validation post-deserialization. Gemini's JSON mode enforces structure at generation time.

**Confidence:** MEDIUM - .NET 10 likely inherits .NET 9's schema features, though specific documentation is limited.

**Sources:**
- [System.Text.Json in .NET 9](https://devblogs.microsoft.com/dotnet/system-text-json-in-dotnet-9/)
- [JsonSchema.Net NuGet](https://www.nuget.org/packages/JsonSchema.Net)

---

### NodaTime

**DO NOT ADD:** NodaTime (IANA timezone library)

**Why:** .NET's `TimeZoneInfo.FindSystemTimeZoneById()` already supports IANA timezone IDs (verified in User entity). The project already uses this approach successfully. NodaTime adds complexity without providing additional value for this use case.

**Current implementation:** User.SetTimeZone() validates IANA IDs using TimeZoneInfo.FindSystemTimeZoneById().

**When to reconsider:** If complex timezone arithmetic or historical timezone data access becomes necessary.

**Confidence:** HIGH - Existing implementation proven in production.

**Sources:**
- [NodaTime Documentation](https://nodatime.org/2.4.x/api/NodaTime.DateTimeZone.html)
- User.cs entity (existing codebase)

---

### Separate HTTP Client Libraries

**DO NOT ADD:** RestSharp, Flurl, Refit

**Why:** HttpClient + System.Net.Http.Json already handles all Gemini API needs. Adding another HTTP library introduces unnecessary abstraction. Current GeminiIntentService demonstrates HttpClient works well for REST APIs with JSON.

**Current implementation:** PostAsJsonAsync, ReadFromJsonAsync provide clean JSON handling.

**Confidence:** HIGH - Existing implementation proven reliable.

---

## Integration Points & Implementation Notes

### 1. Multi-Action AI Output

**Status:** Already implemented - no stack changes needed.

**Current state:**
- `AiActionPlan` already contains `IReadOnlyList<AiAction> Actions`
- SystemPromptBuilder already instructs Gemini to return multiple actions
- GeminiIntentService already deserializes action arrays

**What to add:**
- Nothing. Feature exists but may not be fully utilized in current prompts.
- Enhancement: Update system prompt to emphasize multi-action capability.

**Confidence:** HIGH - Verified in codebase.

---

### 2. Gemini Vision (Image Processing)

**Status:** Same API, different content parts - no new dependencies needed.

**Implementation approach:**
```csharp
// Current request format (text only)
{
  "contents": [{
    "parts": [
      {"text": "User message here"}
    ]
  }]
}

// New format (image + text)
{
  "contents": [{
    "parts": [
      {
        "inline_data": {
          "mime_type": "image/jpeg",
          "data": "[base64_string]"
        }
      },
      {"text": "What habit is shown in this image?"}
    ]
  }]
}
```

**Key requirements:**
- **Base64 encoding:** Use `Convert.ToBase64String(byte[])` (built-in)
- **MIME types:** Support PNG, JPEG, WEBP, HEIC, HEIF
- **Size limit:** 20MB total request (inline data)
- **Model:** Continue using gemini-2.5-flash (supports multimodal)

**Integration point:** Extend GeminiIntentService.InterpretAsync() to accept optional byte[] imageData parameter. Add inline_data part to GeminiRequest when image provided.

**Confidence:** HIGH - Gemini Vision uses same REST endpoint as text-only API.

**Sources:**
- [Gemini Image Understanding](https://ai.google.dev/gemini-api/docs/image-understanding)
- [Gemini API Generate Content](https://ai.google.dev/api/generate-content)

---

### 3. AI User Learning (Fact Storage)

**Status:** Use EF Core JSON columns - no new dependencies needed.

**Implementation approach:**

**Entity:** Add new `UserFact` entity or extend `User` with JSON column.

```csharp
public class User : Entity
{
    // Existing properties...
    public string? LearnedFacts { get; private set; } // JSONB column
}

// Or separate entity (recommended):
public class UserFact : Entity
{
    public Guid UserId { get; private set; }
    public string Category { get; private set; } // "preference", "routine", "goal"
    public string Fact { get; private set; }
    public DateTime LearnedAtUtc { get; private set; }
    public int ConfidenceScore { get; private set; } // 0-100
}
```

**EF Core configuration:**
```csharp
// For JSON column on User
modelBuilder.Entity<User>()
    .Property(u => u.LearnedFacts)
    .HasColumnType("jsonb");

// For separate entity (recommended)
modelBuilder.Entity<UserFact>()
    .HasIndex(uf => uf.UserId);
```

**AI integration:**
- SystemPromptBuilder includes user facts in prompt context
- Gemini identifies learnable facts in responses
- New action type: LearnFact (or store facts in AiMessage metadata)

**Storage recommendation:** Separate `UserFact` entity (not JSON column) for queryability and structured access.

**Confidence:** HIGH - EF Core 10 with PostgreSQL has mature JSONB support.

**Sources:**
- [JSON Mapping in Npgsql](https://www.npgsql.org/efcore/mapping/json.html)
- [EF Core JSON Columns](https://www.learnentityframeworkcore.com/misc/json-columns)

---

### 4. Routine Inference (Pattern Detection)

**Status:** Requires Microsoft.ML.TimeSeries package.

**Implementation approach:**

**Data source:** HabitLog.CreatedAtUtc timestamps converted to user's timezone.

**ML.NET usage:**
```csharp
// Pseudocode structure
using Microsoft.ML;
using Microsoft.ML.Transforms.TimeSeries;

public class RoutineAnalyzer
{
    private readonly MLContext _mlContext;

    public RoutineInference DetectRoutine(List<HabitLog> logs, string userTimeZone)
    {
        // Convert UTC timestamps to user local time
        var localTimes = logs
            .Select(log => TimeZoneInfo.ConvertTimeFromUtc(
                log.CreatedAtUtc,
                TimeZoneInfo.FindSystemTimeZoneById(userTimeZone)))
            .ToList();

        // Extract hour-of-day patterns
        var hourFrequency = localTimes
            .GroupBy(dt => dt.Hour)
            .ToDictionary(g => g.Key, g => g.Count());

        // Use SSA for pattern detection
        var forecast = _mlContext.Forecasting.ForecastBySsa(
            outputColumnName: "Forecast",
            inputColumnName: "Value",
            windowSize: 7,
            seriesLength: logs.Count,
            trainSize: logs.Count,
            horizon: 7
        );

        return new RoutineInference
        {
            MostCommonHour = hourFrequency.OrderByDescending(kvp => kvp.Value).First().Key,
            Confidence = CalculateConfidence(hourFrequency),
            SuggestedTime = BuildSuggestion(hourFrequency)
        };
    }
}
```

**Integration point:**
- New query: `GetHabitRoutineQuery` (returns suggested times based on past logs)
- Endpoint: `GET /api/habits/{id}/routine` (optional analytics feature)
- AI integration: Include routine insights in SystemPromptBuilder for personalized suggestions

**Minimum logs required:** 7+ logs recommended for meaningful pattern detection.

**Confidence:** MEDIUM - ML.NET.TimeSeries proven for time series, but application to user routines requires experimentation.

**Sources:**
- [Anomaly Detection with ML.NET](https://medium.com/geekculture/anomaly-detection-and-prediction-using-ml-net-timeseries-library-e91838ca0ba3)
- [ML.NET TimeSeries Package](https://www.nuget.org/packages/Microsoft.ML.TimeSeries/)

---

## Alternatives Considered

### 1. Gemini 3.0 vs Gemini 2.5 Flash

**Decision:** Continue with Gemini 2.5 Flash

**Rationale:**
- Gemini 2.0 Flash retiring March 31, 2026
- Gemini 2.5 Flash is current stable model with multimodal support
- Gemini 3.0 introduces "thinking budget" and code execution (overkill for habit tracking)
- 2.5 Flash offers 1M token context, multimodal input, $0.30/1M input tokens

**When to reconsider:** If Gemini 3.0 pricing becomes competitive and thinking capability improves routine inference quality.

**Confidence:** HIGH - 2.5 Flash meets all requirements at reasonable cost.

**Sources:**
- [Gemini 2.5 Flash Specs](https://docs.cloud.google.com/vertex-ai/generative-ai/docs/models/gemini/2-5-flash)
- [Gemini 2.5 Family Expansion](https://blog.google/products-and-platforms/products/gemini/gemini-2-5-model-family-expands/)

---

### 2. Deedle vs ML.NET.TimeSeries

**Decision:** ML.NET.TimeSeries

**Rationale:**
- ML.NET is official Microsoft library with first-party support
- TimeSeries package specifically designed for forecasting and pattern detection
- Deedle is more general-purpose data manipulation (like pandas)
- SSA (Singular Spectrum Analysis) built into ML.NET.TimeSeries
- Better .NET ecosystem integration

**When to reconsider:** If complex data frame transformations become necessary beyond time series analysis.

**Confidence:** HIGH - ML.NET.TimeSeries is purpose-built for this use case.

**Sources:**
- [Deedle GitHub](https://github.com/fslaborg/Deedle)
- [ML.NET TimeSeries NuGet](https://www.nuget.org/packages/Microsoft.ML.TimeSeries/)

---

### 3. Separate UserFact Entity vs JSON Column on User

**Decision:** Separate UserFact entity (recommended)

**Rationale:**
- **Queryability:** Can efficiently query facts by category, date range, confidence
- **Indexing:** Can index UserId for fast lookups
- **Type safety:** Strongly typed entity vs dynamic JSON
- **Growth:** Scales better as fact volume increases
- **Relations:** Can reference facts from other entities if needed

**Alternative:** JSON column if facts are purely for AI context (read-only, no queries).

**Confidence:** HIGH - Separate entity follows Clean Architecture and domain modeling best practices.

**Sources:**
- [EF Core JSON Columns](https://www.learnentityframeworkcore.com/misc/json-columns)
- [PostgreSQL JSONB in .NET](https://mareks-082.medium.com/postgresql-jsonb-in-net-25fbcc7b64b2)

---

## Migration & Rollout Considerations

### Incremental Adoption Path

1. **Phase 1 (No dependencies):** Multi-action AI output, User fact storage entity
2. **Phase 2 (HttpClient enhancement):** Gemini Vision API integration
3. **Phase 3 (New dependency):** Routine inference with ML.NET.TimeSeries

**Rationale:** Defer ML.NET dependency until routine inference is validated as valuable feature.

---

### Database Migrations

**Required:**
- New `UserFacts` table (or `LearnedFacts` JSONB column on Users table)
- No changes to existing tables

**EF Core migration:** Standard Add-Migration workflow, no special considerations.

---

### API Version Compatibility

**Breaking changes:** None expected. New features are additive.

**Optional parameters:**
- Image upload to chat endpoint (new optional field)
- Routine endpoint is new (doesn't affect existing endpoints)

---

## Confidence Assessment

| Area | Confidence | Rationale |
|------|------------|-----------|
| Multi-action AI | HIGH | Already implemented in codebase |
| Gemini Vision | HIGH | Same API as text-only, official docs verified |
| Fact storage | HIGH | EF Core + PostgreSQL JSONB well-documented |
| Routine inference | MEDIUM | ML.NET.TimeSeries proven but requires experimentation for this use case |
| Overall stack | HIGH | Minimal new dependencies, proven technologies |

---

## Open Questions & Risks

### 1. ML.NET.TimeSeries Performance
**Question:** Will ML.NET.TimeSeries scale to thousands of logs per user?

**Mitigation:**
- Start with recent logs only (last 90 days)
- Cache routine inference results
- Run analysis async/background job

**Risk level:** LOW - Can optimize if needed.

---

### 2. Gemini Vision Accuracy
**Question:** Will Gemini Vision reliably identify habits from user photos?

**Mitigation:**
- Clear user guidance on photo quality
- Fallback to text-based confirmation
- Allow users to correct AI interpretation

**Risk level:** MEDIUM - Depends on image quality and habit types.

---

### 3. Base64 Image Size Limits
**Question:** Will 20MB inline limit be sufficient?

**Analysis:** 20MB base64 = ~15MB original image. Smartphone photos typically 2-5MB. Limit is sufficient for MVP.

**Mitigation:** If limit becomes issue, implement Gemini Files API upload (larger files, reusable references).

**Risk level:** LOW - 20MB sufficient for mobile photos.

---

## Summary for Roadmap

### Required Stack Changes

**New Package:**
- Microsoft.ML.TimeSeries 5.0.0 (for routine inference)

**No Changes Needed:**
- Multi-action AI (already exists)
- Gemini Vision (same HttpClient + API)
- Fact storage (existing EF Core + PostgreSQL)

### Implementation Complexity

| Feature | Stack Impact | Complexity |
|---------|--------------|------------|
| Multi-action AI | None (exists) | LOW - Prompt engineering only |
| Gemini Vision | None (API enhancement) | LOW - Add inline_data part |
| User fact storage | None (standard EF Core) | LOW - New entity + migration |
| Routine inference | +1 package (ML.NET) | MEDIUM - ML experimentation needed |

### Recommendation

**Proceed with confidence.** The existing stack is well-positioned for v1.1 enhancements. Only routine inference requires a new dependency, and it's a stable, official Microsoft package. All other features work with existing infrastructure.

**Critical path:** Validate ML.NET.TimeSeries for routine inference early. Other features have minimal technical risk.

---

## Sources

### Gemini Vision & Multimodal
- [Image understanding | Gemini API | Google AI for Developers](https://ai.google.dev/gemini-api/docs/image-understanding)
- [Gemini API reference | Google AI for Developers](https://ai.google.dev/api)
- [Generating content | Gemini API | Google AI for Developers](https://ai.google.dev/api/generate-content)
- [Gemini 2.5 Flash | Generative AI on Vertex AI](https://docs.cloud.google.com/vertex-ai/generative-ai/docs/models/gemini/2-5-flash)
- [Gemini 2.5 Model Specs, Costs & Benchmarks](https://blog.galaxy.ai/model/gemini-2-5-flash)

### .NET 10 & Base64 Encoding
- [.NET 10 Networking Improvements - .NET Blog](https://devblogs.microsoft.com/dotnet/dotnet-10-networking-improvements/)
- [Base64 Encode and Decode in C# - Code Maze](https://code-maze.com/base64-encode-decode-csharp/)

### ML.NET & Time Series
- [Microsoft.ML.TimeSeries NuGet Package](https://www.nuget.org/packages/Microsoft.ML.TimeSeries/)
- [Tutorial: Forecast bike rental demand - time series - ML.NET](https://learn.microsoft.com/en-us/dotnet/machine-learning/tutorials/time-series-demand-forecasting)
- [Anomaly Detection and Prediction using ML.Net TimeSeries library](https://medium.com/geekculture/anomaly-detection-and-prediction-using-ml-net-timeseries-library-e91838ca0ba3)
- [Deedle: Easy to use .NET library for data and time series manipulation](https://github.com/fslaborg/Deedle)

### EF Core & PostgreSQL JSON
- [JSON Mapping | Npgsql Documentation](https://www.npgsql.org/efcore/mapping/json.html)
- [JSONB in PostgreSQL with EF Core](https://medium.com/@serhiikokhan/jsonb-in-postgresql-with-ef-core-cc945f1aba2a)
- [JSON Columns in Entity Framework Core](https://www.learnentityframeworkcore.com/misc/json-columns)
- [PostgreSQL JSONB in .NET](https://mareks-082.medium.com/postgresql-jsonb-in-net-25fbcc7b64b2)

### Alternative Libraries
- [SixLabors ImageSharp](https://sixlabors.com/products/imagesharp/)
- [NodaTime Documentation](https://nodatime.org/2.4.x/api/NodaTime.DateTimeZone.html)
- [System.Text.Json in .NET 9 - .NET Blog](https://devblogs.microsoft.com/dotnet/system-text-json-in-dotnet-9/)

---

*Stack research for: Orbit v1.1 AI Intelligence Enhancements*
*Researched: 2026-02-09*
