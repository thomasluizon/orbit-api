# Phase 6: Image Intelligence - Research

**Researched:** 2026-02-09
**Domain:** Multimodal AI (Gemini Vision) + ASP.NET Core File Upload
**Confidence:** HIGH

## Summary

Phase 6 integrates Gemini Vision API's multimodal capabilities with ASP.NET Core file upload infrastructure to enable AI-driven habit creation from uploaded images (photos of schedules, bills, to-do lists). The implementation requires extending the existing chat endpoint to accept multipart/form-data requests containing both text and images, converting images to base64 for Gemini API submission, and leveraging Gemini's structured output capabilities to extract habit data from visual content.

**Key architectural insight:** The existing GeminiIntentService already uses HttpClient with JSON serialization and supports structured JSON output via `response_mime_type: "application/json"`. Adding image support requires minimal changes—extending the GeminiPart DTO to support inline_data, converting IFormFile to base64, and maintaining the existing confirmation flow pattern (SuggestBreakdown) for image-based suggestions.

**Primary recommendation:** Use inline base64 image encoding (not File API) for simplicity and alignment with existing architecture. Images under 20MB fit well within rate limits, and the base64 approach avoids additional API calls for file lifecycle management. Implement custom model binding to combine JSON chat messages with image uploads in a single multipart request, following Thomas Levesque's established pattern for ASP.NET Core.

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Gemini 2.5 Flash | Current | Multimodal vision + structured output | Already integrated, native vision support, 1.6s response time, reliable JSON |
| IFormFile | .NET 10 | File upload abstraction | Built-in ASP.NET Core, proven pattern in codebase |
| System.Text.Json | .NET 10 | JSON serialization | Already used for GeminiRequest/Response DTOs |
| MultipartReader | ASP.NET Core | Streaming multipart requests | Native support for large file handling |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| FileSignatures | 6.1.1 | Magic byte validation | Verify true file type beyond extension checking |
| System.Drawing.Common | .NET 10 | Image metadata extraction | Optional: validate dimensions, format |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Inline base64 | Gemini File API | File API supports 2GB files, but adds complexity (upload → reference → cleanup). Base64 is simpler, sufficient for images <20MB |
| Custom model binding | Separate endpoints | Separate text/image endpoints simplify implementation but fragment UX and complicate state management |
| System.Drawing | SixLabors.ImageSharp | ImageSharp is cross-platform and modern, but adds dependency. System.Drawing.Common works for basic validation |

**Installation:**
```bash
dotnet add src/Orbit.Infrastructure package FileSignatures
# System.Drawing.Common optional - only if metadata validation needed
```

## Architecture Patterns

### Recommended Project Structure
```
src/Orbit.Api/
├── Controllers/
│   └── ChatController.cs           # Extend [FromForm] binding
├── ModelBinders/
│   └── ChatWithImageModelBinder.cs # Custom multipart JSON + file binder
└── DTOs/
    └── ChatWithImageRequest.cs     # Message + IFormFile

src/Orbit.Application/
└── Chat/Commands/
    └── ProcessUserChatCommand.cs   # Add optional byte[]? ImageData param

src/Orbit.Infrastructure/
└── Services/
    ├── GeminiIntentService.cs      # Extend GeminiPart to support inline_data
    └── ImageValidationService.cs   # File signature + size validation
```

### Pattern 1: Multipart Request with JSON and Image

**What:** Combines structured JSON metadata (chat message) with binary file upload in single HTTP request

**When to use:** Chat endpoint needs both text context and image data for multimodal AI processing

**Example:**
```csharp
// Source: https://thomaslevesque.com/2018/09/04/handling-multipart-requests-with-json-and-file-uploads-in-asp-net-core/
[ModelBinder(typeof(ChatWithImageModelBinder), Name = "json")]
public class ChatWithImageRequest
{
    [Required]
    public string Message { get; set; }

    public IFormFile? Image { get; set; }
}

// Controller
[HttpPost]
public async Task<IActionResult> ProcessChat(ChatWithImageRequest request)
{
    // IFormFile automatically bound, JSON deserialized by custom binder
    var imageBytes = request.Image != null
        ? await ConvertToBase64(request.Image)
        : null;

    var command = new ProcessUserChatCommand(
        UserId: HttpContext.GetUserId(),
        Message: request.Message,
        ImageData: imageBytes);

    var result = await _mediator.Send(command);
    return Ok(result.Value);
}
```

### Pattern 2: IFormFile to Base64 Conversion

**What:** Convert uploaded image to base64 string for Gemini API inline_data submission

**When to use:** Image must be embedded in JSON request to Gemini

**Example:**
```csharp
// Source: https://learn.microsoft.com/en-us/answers/questions/1192125/how-to-convert-iformfile-to-byte-array
public static async Task<string> ConvertToBase64Async(IFormFile file)
{
    using var memoryStream = new MemoryStream();
    await file.CopyToAsync(memoryStream);
    byte[] fileBytes = memoryStream.ToArray();
    return Convert.ToBase64String(fileBytes);
}
```

### Pattern 3: Gemini Vision Request Structure

**What:** Extend existing GeminiRequest DTO to support inline image data alongside text prompts

**When to use:** Sending multimodal requests to Gemini API

**Example:**
```csharp
// Source: https://ai.google.dev/gemini-api/docs/image-understanding
private record GeminiPart
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("inline_data")]
    public InlineData? InlineData { get; init; }
}

private record InlineData
{
    [JsonPropertyName("mime_type")]
    public string MimeType { get; init; } = string.Empty;

    [JsonPropertyName("data")]
    public string Data { get; init; } = string.Empty; // base64 encoded
}

// Usage
var request = new GeminiRequest
{
    Contents = new[]
    {
        new GeminiContent
        {
            Parts = new[]
            {
                new GeminiPart { Text = systemPrompt + "\n\nUser: " + userMessage },
                new GeminiPart
                {
                    InlineData = new InlineData
                    {
                        MimeType = "image/jpeg",
                        Data = base64ImageData
                    }
                }
            }
        }
    },
    GenerationConfig = new GeminiGenerationConfig
    {
        Temperature = 0.1,
        ResponseMimeType = "application/json"
    }
};
```

### Pattern 4: File Signature Validation

**What:** Validate uploaded file's true type by checking magic bytes, not just extension

**When to use:** Security-critical file uploads to prevent malicious file execution

**Example:**
```csharp
// Source: https://www.nuget.org/packages/FileSignatures
using FileSignatures;
using FileSignatures.Formats;

public class ImageValidationService
{
    private static readonly IEnumerable<FileFormat> AllowedFormats = new FileFormat[]
    {
        new Jpeg(),
        new Png(),
        new WebP()
    };

    public async Task<(bool IsValid, string? MimeType)> ValidateImageAsync(IFormFile file)
    {
        var inspector = new FileFormatInspector();

        using var stream = file.OpenReadStream();
        var format = inspector.DetermineFileFormat(stream);

        if (format == null || !AllowedFormats.Any(f => f.GetType() == format.GetType()))
            return (false, null);

        return (true, format.MediaType);
    }
}
```

### Pattern 5: Confirmation Flow for Image-Based Suggestions

**What:** Reuse existing SuggestBreakdown pattern to require user confirmation before creating habits from image analysis

**When to use:** AI extracts multiple habits from image—user must approve before persistence

**Example:**
```csharp
// Already exists in codebase - extend for image source
private static Result<(Guid? Id, string? Name)> ExecuteSuggestBreakdown(AiAction action)
{
    // SuggestBreakdown creates nothing -- it just passes through the AI's suggestions
    // The ActionResult will carry the suggestions for frontend rendering
    return Result.Success<(Guid? Id, string? Name)>((null, action.Title));
}

// System prompt extension for image analysis
string systemPrompt = $$"""
When analyzing an uploaded image:
1. Extract all habit-like items (tasks, recurring events, goals)
2. Infer frequency from visual cues (daily checkboxes, week columns, month labels)
3. Extract due dates from dates visible in image
4. Return SuggestBreakdown action type - NEVER CreateHabit directly
5. User will explicitly confirm which suggestions to create
""";
```

### Anti-Patterns to Avoid

- **Trusting Content-Type header:** Attackers can spoof MIME types. Always validate file signatures (magic bytes) for true type verification
- **Using File API for small images:** Gemini File API adds upload/reference/cleanup overhead. Inline base64 is simpler for images <20MB
- **Blocking chat endpoint on image processing:** Images should be optional. Text-only requests must maintain current performance
- **Auto-creating habits from image analysis:** Violates confirmation requirement (IMGP-04). Always use SuggestBreakdown for image-extracted habits
- **Separate image upload endpoint:** Fragments user experience. Single multipart endpoint maintains conversational flow

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| File signature validation | Manual byte array comparison for JPEG/PNG headers | FileSignatures library (6.1.1) | Handles 70+ formats, validates full signatures (not just first bytes), accounts for format variations |
| Multipart request binding | Custom stream parsing for form-data sections | Custom IModelBinder + FormFileModelBinder | ASP.NET Core provides FormFileModelBinder—reuse it, only add JSON deserialization layer |
| Image resizing/optimization | Custom image compression before Gemini upload | Gemini's native resolution handling | Gemini accepts up to 20MB inline, handles resolution internally. Premature optimization adds complexity |
| Rate limiting | Custom semaphore + timer logic | Built-in retry logic with exponential backoff (already exists in GeminiIntentService) | Existing 429 retry logic works for both text and image requests |
| OCR/text extraction | Third-party OCR library | Gemini Vision native capabilities | Gemini 2.5 Flash handles OCR, object detection, segmentation natively. No additional libraries needed |

**Key insight:** Gemini Vision is a complete multimodal AI solution. Don't layer additional image processing libraries—Gemini handles format conversion, OCR, object detection, and structured extraction. Focus implementation on secure upload, validation, and request formatting.

## Common Pitfalls

### Pitfall 1: Multipart Request Deserialization Failures

**What goes wrong:** ASP.NET Core model binding doesn't automatically deserialize JSON in multipart form sections. Passing `[FromBody] ChatRequest` with multipart/form-data results in null binding.

**Why it happens:** Default model binders expect `application/json` for [FromBody] or simple key-value pairs for [FromForm]. Multipart sections containing JSON require manual deserialization.

**How to avoid:** Implement custom `IModelBinder` that:
1. Reads "json" form section
2. Deserializes to model object
3. Delegates IFormFile properties to FormFileModelBinder
4. Merges results into single bound model

**Warning signs:**
- Request succeeds but model properties are null
- Multipart request logs show form-data, but controller receives empty object
- Removing IFormFile makes [FromBody] work again

**Reference:** https://thomaslevesque.com/2018/09/04/handling-multipart-requests-with-json-and-file-uploads-in-asp-net-core/

### Pitfall 2: Base64 Performance Degradation

**What goes wrong:** Base64 encoding increases payload size by ~33%. For multiple images or large files (>5MB), response times degrade significantly.

**Why it happens:** Base64 encoding converts 3 bytes to 4 characters. Gemini API must transmit larger payloads, increasing network latency. Performance is 40x worse than direct file transmission for large batches.

**How to avoid:**
- Enforce client-side image size limits (recommend 2-5MB max)
- Consider File API for images >10MB (though rare in habit-tracking context)
- Validate file size server-side before base64 conversion
- Log conversion time and payload size for monitoring

**Warning signs:**
- Chat endpoint response time >5 seconds for image requests
- Memory spikes during IFormFile → base64 conversion
- Rate limit 429 errors increase due to larger request payloads

**Reference:** https://medium.com/@ma1f/file-streaming-performance-in-dotnet-4dee608dd953

### Pitfall 3: File Signature Bypass via Extension Renaming

**What goes wrong:** Validating only file extension allows attackers to rename malicious.exe → malicious.jpg and bypass filters.

**Why it happens:** Extensions are metadata, not intrinsic file properties. Trusting Content-Type header or filename extension is insufficient.

**How to avoid:**
1. Validate file signature (magic bytes) using FileSignatures library
2. Check first 8-16 bytes match expected image formats (JPEG: FF D8 FF, PNG: 89 50 4E 47)
3. Reject files where signature doesn't match allowed formats
4. Optional: Use System.Drawing to attempt image load (throws if invalid)

**Warning signs:**
- Security scans flag unrestricted file upload vulnerability
- Non-image files pass validation
- Gemini API returns errors for "image" processing

**Reference:** https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html

### Pitfall 4: Gemini Rate Limit 429 Errors

**What goes wrong:** Free tier limit is 15 RPM. Adding image uploads doesn't change quota, but larger payloads may trigger rate limits faster. 429 errors fail requests without proper retry logic.

**Why it happens:** Gemini enforces rate limits per project (not per API key). Image requests consume same quota as text requests but take longer to transmit, increasing collision likelihood with concurrent requests.

**How to avoid:**
- Existing GeminiIntentService already has retry logic with exponential backoff (2s, 4s, 8s)
- No changes needed—image requests inherit retry behavior
- Monitor 429 frequency in production logs
- Consider Tier 1 upgrade ($0.15/million tokens) for production use

**Warning signs:**
- Integration tests fail intermittently with 429 errors
- Chat endpoint returns "Gemini API error: TooManyRequests"
- Retry attempts exhaust max retries (3) before success

**Reference:** https://ai.google.dev/gemini-api/docs/rate-limits

### Pitfall 5: Missing Image Context in System Prompt

**What goes wrong:** System prompt doesn't instruct Gemini how to process images. AI may describe image without extracting structured habit data.

**Why it happens:** Gemini Vision is general-purpose multimodal AI. Without explicit instructions, it defaults to image captioning rather than structured extraction.

**How to avoid:**
- Extend SystemPromptBuilder to include image analysis instructions
- Specify desired output structure (habit title, frequency, due date extraction)
- Example prompt: "When user uploads an image, extract all habit-like items (tasks, goals, recurring events). Infer frequency from visual cues (daily checkboxes = Daily/1, week columns = Weekly/1). Extract dates from text in image. Return SuggestBreakdown action with extracted habits. Do not auto-create."
- Always require SuggestBreakdown for image-based habits (confirmation flow)

**Warning signs:**
- AI returns "I see a to-do list with 5 items" instead of structured actions
- Image upload succeeds but no habits suggested
- Actions array contains only TextResponse, not SuggestBreakdown

**Reference:** https://ai.google.dev/gemini-api/docs/image-understanding

### Pitfall 6: File Size Configuration Mismatch

**What goes wrong:** Kestrel rejects requests >30MB (default), but FormOptions.MultipartBodyLengthLimit is 128MB. Users get 413 Payload Too Large errors despite code allowing larger files.

**Why it happens:** ASP.NET Core has multiple size limit layers (Kestrel.MaxRequestBodySize, FormOptions.MultipartBodyLengthLimit, per-action RequestFormLimits). Smallest limit takes precedence.

**How to avoid:**
- Set consistent limits across all layers:
  - Kestrel: `options.Limits.MaxRequestBodySize = 52428800` (50MB)
  - FormOptions: `options.MultipartBodyLengthLimit = 52428800`
  - Action attribute: `[RequestFormLimits(MultipartBodyLengthLimit = 52428800)]`
- Recommend 20MB max for inline base64 (Gemini limit)
- Return clear error messages for oversized uploads

**Warning signs:**
- 413 Payload Too Large errors in production
- Development works but deployment fails
- FormOptions configured but Kestrel limits not updated

**Reference:** https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-10.0

## Code Examples

Verified patterns from official sources:

### Example 1: Custom Model Binder for Multipart JSON + File

```csharp
// Source: https://thomaslevesque.com/2018/09/04/handling-multipart-requests-with-json-and-file-uploads-in-asp-net-core/
public class ChatWithImageModelBinder : IModelBinder
{
    public async Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var modelType = bindingContext.ModelType;
        var request = bindingContext.HttpContext.Request;

        // 1. Extract JSON part from multipart form
        var jsonValue = request.Form["json"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(jsonValue))
        {
            bindingContext.Result = ModelBindingResult.Failed();
            return;
        }

        // 2. Deserialize JSON to model
        var model = JsonSerializer.Deserialize(jsonValue, modelType, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (model is null)
        {
            bindingContext.Result = ModelBindingResult.Failed();
            return;
        }

        // 3. Bind file properties using FormFileModelBinder
        var formFileProperties = modelType.GetProperties()
            .Where(p => p.PropertyType == typeof(IFormFile));

        foreach (var prop in formFileProperties)
        {
            var file = request.Form.Files.GetFile(prop.Name);
            if (file != null)
            {
                prop.SetValue(model, file);
            }
        }

        bindingContext.Result = ModelBindingResult.Success(model);
    }
}

// Register in Program.cs
builder.Services.AddControllers(options =>
{
    options.ModelBinderProviders.Insert(0, new ChatWithImageModelBinderProvider());
});
```

### Example 2: Secure Image Validation

```csharp
// Source: https://www.nuget.org/packages/FileSignatures + https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html
public interface IImageValidationService
{
    Task<Result<(string MimeType, long Size)>> ValidateAsync(IFormFile file);
}

public class ImageValidationService : IImageValidationService
{
    private const long MaxFileSizeBytes = 20_971_520; // 20MB (Gemini inline limit)
    private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif" };

    private static readonly IEnumerable<FileFormat> AllowedFormats = new FileFormat[]
    {
        new Jpeg(),
        new Png(),
        new WebP()
    };

    public async Task<Result<(string MimeType, long Size)>> ValidateAsync(IFormFile file)
    {
        // 1. Size validation
        if (file.Length > MaxFileSizeBytes)
            return Result.Failure<(string, long)>($"File size {file.Length} bytes exceeds maximum {MaxFileSizeBytes} bytes (20MB)");

        if (file.Length == 0)
            return Result.Failure<(string, long)>("File is empty");

        // 2. Extension validation (preliminary check)
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
            return Result.Failure<(string, long)>($"File extension {extension} not allowed. Allowed: {string.Join(", ", AllowedExtensions)}");

        // 3. File signature (magic bytes) validation
        var inspector = new FileFormatInspector();
        using var stream = file.OpenReadStream();

        var format = inspector.DetermineFileFormat(stream);
        if (format == null)
            return Result.Failure<(string, long)>("Unable to determine file format from content");

        if (!AllowedFormats.Any(f => f.GetType() == format.GetType()))
            return Result.Failure<(string, long)>($"File signature does not match allowed image formats. Detected: {format.GetType().Name}");

        return Result.Success((format.MediaType, file.Length));
    }
}
```

### Example 3: Extended GeminiIntentService for Image Support

```csharp
// Source: Existing codebase + https://ai.google.dev/gemini-api/docs/image-understanding
public async Task<Result<AiActionPlan>> InterpretAsync(
    string userMessage,
    IReadOnlyList<Habit> activeHabits,
    IReadOnlyList<Tag> userTags,
    IReadOnlyList<UserFact> userFacts,
    byte[]? imageData = null,
    string? imageMimeType = null,
    CancellationToken cancellationToken = default)
{
    var systemPrompt = SystemPromptBuilder.BuildSystemPrompt(activeHabits, userTags, userFacts);

    var parts = new List<GeminiPart>
    {
        new GeminiPart { Text = $"{systemPrompt}\n\nUser: {userMessage}" }
    };

    // Add image if provided
    if (imageData != null && !string.IsNullOrWhiteSpace(imageMimeType))
    {
        var base64Image = Convert.ToBase64String(imageData);
        parts.Add(new GeminiPart
        {
            InlineData = new InlineData
            {
                MimeType = imageMimeType,
                Data = base64Image
            }
        });
    }

    var request = new GeminiRequest
    {
        Contents = new[]
        {
            new GeminiContent { Parts = parts.ToArray() }
        },
        GenerationConfig = new GeminiGenerationConfig
        {
            Temperature = 0.1,
            ResponseMimeType = "application/json"
        }
    };

    // ... existing retry logic and response handling
}

// Updated DTOs
private record GeminiPart
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("inline_data")]
    public InlineData? InlineData { get; init; }
}

private record InlineData
{
    [JsonPropertyName("mime_type")]
    public string MimeType { get; init; } = string.Empty;

    [JsonPropertyName("data")]
    public string Data { get; init; } = string.Empty;
}
```

### Example 4: System Prompt Extension for Image Analysis

```csharp
// Source: https://ai.google.dev/gemini-api/docs/image-understanding + existing SystemPromptBuilder
public static class SystemPromptBuilder
{
    public static string BuildSystemPrompt(
        IReadOnlyList<Habit> habits,
        IReadOnlyList<Tag> tags,
        IReadOnlyList<UserFact> facts,
        bool includeImageInstructions = false)
    {
        // ... existing prompt building logic

        var imageInstructions = includeImageInstructions ? $$"""

        ## Image Analysis Instructions
        When the user uploads an image (photo of schedule, bill, to-do list, calendar):
        1. Extract all habit-like items (tasks, recurring events, goals, responsibilities)
        2. Infer frequency from visual cues:
           - Daily checkboxes → FrequencyUnit: Daily, FrequencyQuantity: 1
           - Week columns (Mon-Sun) → FrequencyUnit: Weekly, FrequencyQuantity: 1
           - Month labels → FrequencyUnit: Monthly, FrequencyQuantity: 1
           - Specific days listed → Use Days array (Monday, Tuesday, etc.)
        3. Extract due dates from dates visible in image (format: YYYY-MM-DD)
        4. Extract amounts for financial habits (bill amount, subscription cost)
        5. IMPORTANT: For image-based habit extraction, ALWAYS use SuggestBreakdown action type
           - NEVER create habits directly from image analysis
           - User must explicitly confirm which suggestions to create
        6. Include extracted text/context in habit descriptions for clarity

        Example image analysis response:
        {
          "aiMessage": "I found 3 recurring tasks in your schedule image.",
          "actions": [
            {
              "type": "SuggestBreakdown",
              "title": "Weekly Schedule from Image",
              "suggestedSubHabits": [
                { "title": "Morning jog", "frequencyUnit": "Weekly", "frequencyQuantity": 3, "days": ["Monday", "Wednesday", "Friday"] },
                { "title": "Team meeting", "frequencyUnit": "Weekly", "frequencyQuantity": 1, "days": ["Tuesday"] },
                { "title": "Grocery shopping", "frequencyUnit": "Weekly", "frequencyQuantity": 1, "days": ["Saturday"] }
              ]
            }
          ]
        }
        """ : string.Empty;

        return existingPrompt + imageInstructions;
    }
}
```

### Example 5: Integration Test with Image Upload

```csharp
// Source: Existing AiChatIntegrationTests.cs pattern
[Fact]
public async Task Chat_UploadScheduleImage_ShouldSuggestHabits()
{
    // Arrange
    var imageBytes = await File.ReadAllBytesAsync("TestData/sample-schedule.jpg");
    using var content = new MultipartFormDataContent();

    var jsonContent = new StringContent(
        JsonSerializer.Serialize(new { message = "Create habits from this schedule" }),
        Encoding.UTF8,
        "application/json");
    content.Add(jsonContent, "json");

    var imageContent = new ByteArrayContent(imageBytes);
    imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
    content.Add(imageContent, "image", "schedule.jpg");

    // Act
    var response = await _client.PostAsync("/api/chat", content);

    // Assert
    response.StatusCode.Should().Be(HttpStatusCode.OK);
    var result = await response.Content.ReadFromJsonAsync<ChatResponse>();

    result!.Actions.Should().ContainSingle(a => a.Type == AiActionType.SuggestBreakdown);
    var suggestion = result.Actions.First(a => a.Type == AiActionType.SuggestBreakdown);
    suggestion.Status.Should().Be(ActionStatus.Suggestion);
    suggestion.SuggestedSubHabits.Should().NotBeEmpty();
    suggestion.SuggestedSubHabits.Should().AllSatisfy(h =>
    {
        h.Title.Should().NotBeNullOrWhiteSpace();
        h.FrequencyUnit.Should().NotBeNull();
        h.FrequencyQuantity.Should().NotBeNull();
    });
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Separate OCR + NLP pipeline | Gemini Vision native multimodal | Gemini 2.0 (Dec 2024) | Single API call replaces multi-stage processing. Eliminates OCR library dependencies |
| File API upload for all images | Inline base64 for <20MB | Gemini API v1beta | Simplified implementation, fewer API calls, immediate processing |
| Extension-based file validation | Magic byte signature validation | OWASP 2020+ | Prevents renamed malicious files from bypassing filters |
| Separate text/image endpoints | Multipart unified endpoint | Modern API design | Better UX, maintains conversation context across modalities |
| Custom JSON parsing in multipart | Custom IModelBinder pattern | ASP.NET Core 2.0+ | Leverages framework's FormFileModelBinder, reduces custom code |

**Deprecated/outdated:**
- **Gemini 2.0 Flash model:** Will be shut down March 31, 2026. Use Gemini 2.5 Flash (current production model)
- **System.Drawing on non-Windows platforms:** .NET 6+ moved to libgdiplus (Linux) with limited support. Consider SixLabors.ImageSharp for cross-platform if metadata validation needed
- **File extension allowlists alone:** OWASP now requires signature validation. Extensions are insufficient for security

## Open Questions

1. **Image resolution optimization:**
   - What we know: Gemini accepts 20MB inline, handles resolution internally, has low/medium/high media_resolution parameter
   - What's unclear: Whether client-side resizing improves response time or accuracy for habit extraction from photos
   - Recommendation: Start with unmodified uploads. Monitor Gemini response times and accuracy. Add client-side resize only if >10MB images become common

2. **Concurrent image + fact extraction:**
   - What we know: Fact extraction is non-blocking (try-catch in ProcessUserChatCommand). Image requests may take longer than text (larger payloads)
   - What's unclear: Whether image requests should skip fact extraction to optimize response time, or maintain dual-pass pipeline
   - Recommendation: Maintain existing dual-pass architecture. Image-based suggestions don't create habits (SuggestBreakdown), so fact extraction timing is less critical

3. **Image storage for audit/retraining:**
   - What we know: Gemini doesn't persist uploaded images. Current architecture has no file storage layer
   - What's unclear: Whether images should be stored for user history, debugging, or future model retraining
   - Recommendation: Phase 6 implements ephemeral processing (no storage). Defer persistent storage to future phase if user history features are needed

4. **Rate limit tier recommendation:**
   - What we know: Free tier = 15 RPM, Tier 1 = 150-300 RPM (~$0.15/million tokens), image requests count same as text
   - What's unclear: At what user scale image uploads will exhaust free tier
   - Recommendation: Launch on free tier with existing retry logic. Monitor 429 frequency in production logs. Upgrade to Tier 1 if retry failures exceed 5% of requests

## Sources

### Primary (HIGH confidence)
- [Gemini API Image Understanding](https://ai.google.dev/gemini-api/docs/image-understanding) - Inline data format, supported MIME types, best practices
- [Gemini API Structured Output](https://ai.google.dev/gemini-api/docs/structured-output) - JSON schema configuration, response_mime_type
- [Gemini API File Input Methods](https://ai.google.dev/gemini-api/docs/file-input-methods) - Inline base64 vs File API comparison, size limits
- [ASP.NET Core File Uploads (.NET 10)](https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-10.0) - IFormFile, MultipartReader, security best practices
- [Gemini API Rate Limits](https://ai.google.dev/gemini-api/docs/rate-limits) - RPM/TPM/RPD quotas, tier structure
- [OWASP File Upload Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html) - Security validation requirements

### Secondary (MEDIUM confidence)
- [Thomas Levesque's Multipart JSON Pattern](https://thomaslevesque.com/2018/09/04/handling-multipart-requests-with-json-and-file-uploads-in-asp-net-core/) - Custom model binder implementation (verified by ASP.NET community)
- [FileSignatures NuGet Package](https://www.nuget.org/packages/FileSignatures) - Magic byte validation library (6.1.1, 500k+ downloads)
- [Gemini 2.5 Flash Image API Guide](https://blog.laozhang.ai/api-guides/gemini-flash-image-api/) - Real-world usage patterns, cost analysis
- [Code Maze: ASP.NET Core Multipart Form-Data](https://code-maze.com/aspnetcore-multipart-form-data-in-httpclient/) - HttpClient multipart patterns

### Tertiary (LOW confidence)
- [Medium: Gemini Structured Output](https://medium.com/google-cloud/structured-output-with-gemini-models-begging-borrowing-and-json-ing-f70ffd60eae6) - Community examples, not official docs
- [Medium: .NET File Streaming Performance](https://medium.com/@ma1f/file-streaming-performance-in-dotnet-4dee608dd953) - Base64 performance benchmarks (needs validation with production data)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - Gemini Vision and ASP.NET Core IFormFile are official, well-documented, already integrated
- Architecture: HIGH - Multipart JSON + file pattern is established (Thomas Levesque), official ASP.NET Core examples align
- Pitfalls: MEDIUM - Security issues (OWASP verified), rate limits (official docs), but some edge cases need production validation
- Integration complexity: LOW - Existing GeminiIntentService requires minimal changes (extend GeminiPart DTO, add optional image params)

**Research date:** 2026-02-09
**Valid until:** 2026-04-09 (60 days - Gemini API stable, ASP.NET Core patterns established)

**Key risks mitigated:**
- File upload security vulnerabilities (OWASP-compliant validation)
- Gemini rate limiting (existing retry logic handles 429s)
- Performance degradation (base64 encoding limited to 20MB max)
- User safety (SuggestBreakdown confirmation flow prevents unwanted habit creation)
