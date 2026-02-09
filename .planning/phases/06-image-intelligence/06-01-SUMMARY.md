---
phase: 06-image-intelligence
plan: 01
subsystem: ai-chat, infrastructure
tags: [gemini-vision, multipart-upload, image-validation, multimodal-ai]
dependency_graph:
  requires: [05-02-fact-management-endpoints]
  provides: [image-upload-infrastructure, gemini-vision-integration]
  affects: [chat-endpoint, ai-intent-service]
tech_stack:
  added: [FileSignatures-6.1.1, Microsoft.AspNetCore.Http.Features-5.0.17]
  patterns: [multipart-form-data, magic-byte-validation, base64-inline-data]
key_files:
  created:
    - src/Orbit.Domain/Interfaces/IImageValidationService.cs
    - src/Orbit.Infrastructure/Services/ImageValidationService.cs
  modified:
    - src/Orbit.Domain/Interfaces/IAiIntentService.cs
    - src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs
    - src/Orbit.Infrastructure/Services/GeminiIntentService.cs
    - src/Orbit.Infrastructure/Services/AiIntentService.cs (OllamaIntentService)
    - src/Orbit.Api/Controllers/ChatController.cs
    - src/Orbit.Api/Program.cs
decisions:
  - title: IFormFile in Domain layer
    rationale: Pragmatic tradeoff similar to EF Core in Application - needed for interface definition
    outcome: Added Microsoft.AspNetCore.Http.Features to Domain project
  - title: Multipart form-data over separate endpoints
    rationale: Single endpoint maintains conversational flow and simplifies state management
    outcome: Changed ChatController from [FromBody] JSON to [FromForm] multipart (breaking change)
  - title: Base64 inline_data over File API
    rationale: Simpler for images <20MB, avoids upload/reference/cleanup overhead
    outcome: Convert IFormFile to byte[] and base64 encode for Gemini inline_data
  - title: Ollama image support deferred
    rationale: Ollama doesn't support vision - would need separate provider or custom OCR
    outcome: Accept image parameters but log warning, continue text-only for Ollama
metrics:
  duration: 5min
  completed_at: 2026-02-09
---

# Phase 6 Plan 1: Image Upload Infrastructure Summary

**One-liner:** Added secure image upload validation and Gemini Vision multimodal support with base64 inline_data encoding

## What Was Built

Implemented the foundation for image-based habit creation by extending the chat endpoint to accept multipart/form-data requests containing optional images alongside text messages. Images are validated using magic byte signature verification (FileSignatures library), converted to base64, and sent to Gemini Vision API as inline_data for multimodal analysis.

**Infrastructure layer:**
- IImageValidationService interface for secure file validation
- ImageValidationService with 20MB limit, JPEG/PNG/WebP support, magic byte verification
- FileSignatures library integration for format detection beyond extension checking

**AI integration layer:**
- Extended IAiIntentService.InterpretAsync with optional imageData/imageMimeType parameters
- Updated GeminiIntentService with InlineData record for Gemini Vision inline_data format
- Dynamic Parts array building - text part always present, image part conditionally added
- OllamaIntentService signature updated (logs warning if image provided, continues text-only)

**API layer:**
- ChatController now accepts [FromForm] multipart requests: message (string) + optional image (IFormFile)
- RequestFormLimits attribute for 20MB max body size
- Image validation runs before command dispatch - invalid images return 400 error
- ProcessUserChatCommand accepts optional ImageData and ImageMimeType

**Backward compatibility note:** This is a breaking change to the chat endpoint contract. Previously accepted `[FromBody] ChatRequest` with JSON, now requires `[FromForm]` with form fields. Integration tests will need updates in plan 06-02.

## Key Implementation Details

**ImageValidationService validation pipeline:**
1. Size check: Reject files >20MB or empty files
2. Extension check: Only allow .jpg/.jpeg/.png/.webp
3. Magic byte check: Use FileSignatures.FileFormatInspector.DetermineFileFormat() to verify true format
4. Format allowlist: Only Jpeg, Png, Webp formats accepted
5. Return: (MimeType, Size) from detected format's MediaType property

**Gemini Vision request structure:**
```csharp
var parts = new List<GeminiPart>
{
    new GeminiPart { Text = $"{systemPrompt}\n\nUser: {userMessage}" }
};

if (imageData != null && !string.IsNullOrWhiteSpace(imageMimeType))
{
    parts.Add(new GeminiPart
    {
        InlineData = new InlineData
        {
            MimeType = imageMimeType,
            Data = Convert.ToBase64String(imageData)
        }
    });
}
```

**Text-only requests:** Work exactly as before by passing null for imageData/imageMimeType parameters throughout the pipeline.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Missing package reference for IFormFile**
- **Found during:** Task 1 - IImageValidationService interface creation
- **Issue:** Domain project didn't have reference to Microsoft.AspNetCore.Http for IFormFile
- **Fix:** Added Microsoft.AspNetCore.Http.Features NuGet package to Domain project
- **Files modified:** src/Orbit.Domain/Orbit.Domain.csproj
- **Commit:** 96ab16b

**2. [Rule 1 - Bug] FileSignatures API method name mismatch**
- **Found during:** Task 2 - Initial build after ImageValidationService creation
- **Issue:** Used DetermineFileFormatAsync (doesn't exist) instead of synchronous DetermineFileFormat
- **Fix:** Changed to synchronous method, wrapped result in Task.FromResult for async signature
- **Files modified:** src/Orbit.Infrastructure/Services/ImageValidationService.cs
- **Commit:** dedc986

**3. [Rule 1 - Bug] Incorrect FileSignatures type casing**
- **Found during:** Task 2 - Second build attempt
- **Issue:** Used WebP (incorrect) instead of Webp (correct casing from library)
- **Fix:** Changed to Webp() - verified by listing all types in FileSignatures.Formats namespace
- **Files modified:** src/Orbit.Infrastructure/Services/ImageValidationService.cs
- **Commit:** dedc986

## Verification Results

**Build verification:**
- `dotnet build` passes for entire solution
- Only warnings: Pre-existing MSB3277 EF Core version conflict (cosmetic, documented in STATE.md)
- GeminiIntentService compiles with InlineData record and multipart Parts array
- ChatController compiles with [FromForm] binding and IImageValidationService injection
- ImageValidationService compiles with FileSignatures magic byte validation
- All implementations match updated IAiIntentService interface signature

**Integration test impact:**
- Tests will fail at runtime until updated to send multipart/form-data instead of JSON
- Compilation succeeds - no breaking changes to command/query signatures beyond chat endpoint
- Plan 06-02 will update test helpers and add image upload test scenarios

## Next Phase Readiness

**Blockers:** None

**Requirements for 06-02 (Image Upload Integration Tests):**
- Update SendChatMessage test helper to send MultipartFormDataContent
- Add test scenarios: text-only (verify backward compat), image validation errors, successful image upload
- Add sample test images (JPEG, PNG, WebP) to test fixtures
- Test oversized image rejection (>20MB)
- Test invalid format rejection (magic byte mismatch)

**Requirements for 06-03 (Image Analysis Prompt Engineering):**
- System prompt extension with image analysis instructions
- Guidance for habit extraction from visual content (schedules, bills, lists)
- SuggestBreakdown usage for image-based suggestions (confirmation required)
- Frequency inference from visual cues (daily checkboxes, week columns, etc.)

## Decisions Made

1. **IFormFile in Domain layer** - Accepted the pragmatic clean architecture tradeoff (similar to EF Core in Application). Added Microsoft.AspNetCore.Http.Features to Domain project.

2. **Multipart form-data over separate endpoints** - Single endpoint maintains conversational flow. Trade-off: Breaking change to chat endpoint contract, but better UX.

3. **Base64 inline_data over File API** - Simpler implementation for images <20MB. Avoids file lifecycle management (upload → reference → cleanup).

4. **Ollama image support deferred** - Ollama doesn't support vision APIs. Logs warning if image provided, continues with text-only processing. Future: Could add custom OCR for Ollama or make images Gemini-only.

## Self-Check: PASSED

**Created files verified:**
```
FOUND: src/Orbit.Domain/Interfaces/IImageValidationService.cs
FOUND: src/Orbit.Infrastructure/Services/ImageValidationService.cs
```

**Modified files verified:**
```
FOUND: src/Orbit.Domain/Interfaces/IAiIntentService.cs
FOUND: src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs
FOUND: src/Orbit.Infrastructure/Services/GeminiIntentService.cs
FOUND: src/Orbit.Infrastructure/Services/AiIntentService.cs
FOUND: src/Orbit.Api/Controllers/ChatController.cs
FOUND: src/Orbit.Api/Program.cs
```

**Commits verified:**
```
FOUND: 96ab16b - feat(06-01): add image validation service and update IAiIntentService interface
FOUND: dedc986 - feat(06-01): add Gemini Vision support and multipart form-data endpoint
```

**Build verification:**
```
PASSED: dotnet build completes successfully with 0 errors
```
