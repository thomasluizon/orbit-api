# Codebase Concerns

**Analysis Date:** 2026-02-07

## Tech Debt

**Full User Load for Email Lookups:**
- Issue: `RegisterCommand` and `LoginQuery` call `userRepository.GetAllAsync()` and filter in-memory for email matching instead of using database queries
- Files: `src/Orbit.Application/Auth/Commands/RegisterCommand.cs` (line 19), `src/Orbit.Application/Auth/Queries/LoginQuery.cs` (line 21)
- Impact: Does not scale with user count. When user table grows to thousands, every login/register call loads all users into memory
- Fix approach: Add `FindByEmailAsync()` method to repository that executes a database query with `WHERE Email = ...` clause; replace GetAllAsync calls with direct query method

**No Database Migrations Framework:**
- Issue: Using `EnsureCreatedAsync()` in `Program.cs` (line 138) instead of EF Core migrations
- Files: `src/Orbit.Api/Program.cs`, `src/Orbit.Infrastructure/Persistence/OrbitDbContext.cs`
- Impact: Cannot version schema changes or deploy to production safely. Fresh `EnsureCreatedAsync()` calls on existing databases will silently fail. No rollback capability
- Fix approach: Run `dotnet ef migrations add InitialCreate` then `dotnet ef database update` instead; add migration files to source control

**AI Provider Mock/Stub Not Present:**
- Issue: `GeminiIntentService` and `OllamaIntentService` make real HTTP calls to external APIs during unit testing
- Files: `src/Orbit.Infrastructure/Services/GeminiIntentService.cs`, `src/Orbit.Infrastructure/Services/AiIntentService.cs`
- Impact: Integration tests require running Ollama locally or having Gemini API key; no way to test handlers without actual AI provider
- Fix approach: Create `MockAiIntentService` implementation for unit tests; use dependency injection to swap provider in test setup

## Known Bugs

**Ollama Response Parsing Fragility:**
- Symptoms: Ollama service strips markdown code blocks manually via substring operations instead of structured parsing
- Files: `src/Orbit.Infrastructure/Services/AiIntentService.cs` (lines 79-89)
- Trigger: When Ollama returns response with variant markdown syntax (e.g., ` ```JSON` instead of ` ```json`)
- Workaround: Restart Ollama service if responses have consistent formatting issues; use Gemini as fallback
- Root cause: String manipulation is not robust to edge cases in LLM response formatting

**Gemini API Retry Logic Does Not Preserve State:**
- Symptoms: Exponential backoff during rate limiting (2s, 4s, 8s) may extend API call past request timeout
- Files: `src/Orbit.Infrastructure/Services/GeminiIntentService.cs` (lines 71-92)
- Trigger: Rate limiting (429) response after 2 failed attempts means 14 seconds total retry delay
- Workaround: Increase HTTP client timeout or reduce retry count
- Root cause: No timeout mechanism wraps the retry loop

## Security Considerations

**Secrets in Plain appsettings.json:**
- Risk: `appsettings.json` contains placeholder JWT secret key (`REPLACE-IN-DEVELOPMENT-JSON`)
- Files: `src/Orbit.Api/appsettings.json` (line 17)
- Current mitigation: File is not committed; `appsettings.Development.json` is gitignored
- Recommendations:
  1. Use `dotnet user-secrets` for local development instead of json files
  2. Never log JWT secret values
  3. Add `.gitignore` check in CI to prevent accidental commits

**Email Validation Too Permissive:**
- Risk: Regex in User entity `^[^@\s]+@[^@\s]+\.[^@\s]+$` allows emails like `a@b.c` which may not exist
- Files: `src/Orbit.Domain/Entities/User.cs` (line 8-10)
- Current mitigation: None - accepts any format with @, domain, and TLD
- Recommendations: Send verification email before allowing login; or use stricter RFC 5322 validation

**No Password Reset Capability:**
- Risk: Users with forgotten passwords cannot recover account
- Files: No password reset endpoint exists
- Impact: User data becomes inaccessible if password forgotten
- Recommendation: Implement password reset flow with secure token delivery (email link with GUID)

**AI Service Response Not Validated:**
- Risk: `ProcessUserChatCommand` executes AI-generated actions without validating they belong to correct user
- Files: `src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs` (lines 116-138, 140-161)
- Impact: If AI service returns wrong user's habit/task IDs, commands will still execute them
- Recommendation: Add user ownership check for all action types; fail if habit/task user doesn't match request.UserId

**No Rate Limiting on Chat Endpoint:**
- Risk: Unbounded requests to `/api/chat` can be abused for DoS or incur high AI API costs
- Files: `src/Orbit.Api/Controllers/ChatController.cs`
- Impact: Single malicious client could max out Gemini/Ollama quota or cause service degradation
- Recommendation: Implement per-user rate limiter (e.g., 10 requests/minute); add cost tracking for Gemini API usage

## Performance Bottlenecks

**Email Lookups Fetch Entire User Table:**
- Problem: Every register/login loads all users from database (N+1 when multiple auth requests)
- Files: `src/Orbit.Application/Auth/Commands/RegisterCommand.cs`, `src/Orbit.Application/Auth/Queries/LoginQuery.cs`
- Cause: Repository lacks email-specific query method
- Current performance: Linear with user count; 10k users = 10k entity loads per auth request
- Improvement path: Add indexed email column (already exists) and use LINQ `FirstOrDefault(u => u.Email == ...)` with database execution

**System Prompt Rebuilds for Every Chat:**
- Problem: `SystemPromptBuilder.BuildSystemPrompt()` concatenates entire active habits + pending tasks list as string for every request
- Files: `src/Orbit.Infrastructure/Services/GeminiIntentService.cs` (line 39), `src/Orbit.Infrastructure/Services/AiIntentService.cs` (line 39)
- Cause: No caching of user context between requests
- Current performance: 1-5ms per rebuild; becomes noticeable at scale
- Improvement path: Cache prompt context at user session level; invalidate on habit/task mutation

**Ollama Performance Severe (30s vs Gemini 1.6s):**
- Problem: `phi3.5:3.8b` model is 18x slower than Gemini for equivalent responses
- Files: `src/Orbit.Api/appsettings.json` (line 14)
- Cause: Local LLM has limited capabilities and inference speed
- Improvement path: Use larger quantization (Q8) if hardware allows; or switch to Ollama's faster models like `mistral` (~3s)

**No Connection Pooling Configured:**
- Problem: PostgreSQL connection string does not specify pooling parameters
- Files: `src/Orbit.Api/appsettings.json` (line 10)
- Impact: Under load, DbContext creation may exhaust available connections
- Improvement path: Add `Minimum Pool Size=10;Maximum Pool Size=100` to connection string

## Fragile Areas

**Chat Action Execution Loop:**
- Files: `src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs` (lines 70-92)
- Why fragile: Executes multiple actions in sequence; if action N fails, actions 1..N-1 already persisted. No transaction rollback capability
- Safe modification: Add try-catch around each action; or use database transaction that rolls back all actions if any fail
- Test coverage: Integration tests have high coverage but fail intermittently on Ollama (65% pass rate)

**Entity ID Assignment Pattern:**
- Files: `src/Orbit.Domain/Common/Entity.cs` (line 5)
- Why fragile: `Id = Guid.NewGuid()` runs at init time; cannot set specific IDs for testing/seeding without reflection
- Safe modification: Avoid tests that depend on exact ID values; use factory methods that return Result type
- Test coverage: LogHabitCommand test (line 33) relies on ID generation being reliable

**Habit.Log() Duplicate Detection:**
- Files: `src/Orbit.Domain/Entities/Habit.cs` (line 74)
- Why fragile: Checks `_logs.Exists(l => l.Date == date)` in-memory; if habit is freshly loaded and date already logged by another process, this check passes and creates duplicate
- Safe modification: Check database before adding; or use unique constraint on (HabitId, Date) at database level
- Test coverage: No test for concurrent log attempts on same habit+date

**Days Feature Only Valid When FrequencyQuantity == 1:**
- Files: `src/Orbit.Domain/Entities/Habit.cs` (line 48-49)
- Why fragile: Validation happens at entity creation; nothing prevents future code from setting `Days = [Monday]` with `FrequencyQuantity = 2` after creation
- Safe modification: Make Days setter private; add `SetDays()` method that validates frequency
- Test coverage: AI prompt emphasizes this rule but no unit test validates the constraint

## Scaling Limits

**Database: EnsureCreatedAsync() No Schema Versioning:**
- Current capacity: Works for MVP (1000s of records per table)
- Limit: Cannot add/remove columns or constraints without manual intervention; no deployment safety
- Scaling path: Migrate to EF Core migrations framework before production launch

**PostgreSQL Text Array for Days:**
- Current capacity: 7 DayOfWeek values per habit; works fine
- Limit: If querying "habits with Monday" becomes common, text[] array requires LIKE operator (slow)
- Scaling path: Create separate HabitDay junction table if querying by day becomes critical

**Gemini Rate Limiting (1 req/min during integration tests):**
- Current capacity: Tests run with SemaphoreSlim(1) to avoid quota exhaustion
- Limit: Free tier ~15 RPM; premium tier has higher limits but costs scale with usage
- Scaling path: Implement request queue with cost estimation; cache common intents; use fallback to Ollama

**AI Service Prompt Size (currently ~270 lines):**
- Current capacity: ~5000 tokens for system prompt alone at current habit/task counts
- Limit: Gemini/Ollama context windows are large (2M/4M tokens) but sending full habit history increases latency
- Scaling path: Summarize old habits; limit active habit count via UI; use embedding-based retrieval instead

## Dependencies at Risk

**Microsoft.AspNetCore.Mvc.Testing (WebApplicationFactory):**
- Risk: Integration test factory creates new DbContext per test; may exhaust connection pool with 31 tests
- Impact: Tests fail with "connection pool timeout" under CI environments
- Migration plan: Use testcontainers-dotnet to provision isolated PostgreSQL per test if scale becomes issue

**Npgsql 10.0.0 Early Adoption:**
- Risk: Very recent version; potential compatibility issues with EF Core or NpgsqlDbContext
- Impact: Breaking changes in minor versions could break builds
- Migration plan: Pin to 10.0.x in .csproj; monitor release notes before upgrading

## Missing Critical Features

**No Audit Trail / Event Log:**
- Problem: No way to see who created/modified habits/tasks, when changes occurred
- Blocks: Cannot implement undo, cannot debug accidental deletions, cannot track user activity
- Solution: Add CreatedBy, ModifiedBy, CreatedAtUtc, ModifiedAtUtc to all entities; log to separate audit table

**No Bulk Operations:**
- Problem: Deleting all habits requires N delete calls; no bulk import
- Blocks: Users cannot migrate data from other tools; cannot efficiently clean up test data
- Solution: Add bulk delete endpoint; implement CSV import handler

**No Habit/Task Search:**
- Problem: Users cannot search habits by name; only list all
- Blocks: Users with 100+ habits cannot find specific one
- Solution: Add search parameter to GetHabits query; implement full-text search in PostgreSQL

**No Notifications/Reminders:**
- Problem: User creates habit but receives no reminder to complete it
- Blocks: Habit adoption/completion is fully manual; system has no proactive engagement
- Solution: Add background job queue (Hangfire); send email/push reminders at configured times

## Test Coverage Gaps

**No Unit Tests for Domain Entities:**
- What's not tested: Habit.Log() duplicate detection, Days validation, Frequency constraints
- Files: `src/Orbit.Domain/Entities/` (all entity files lack corresponding .Tests files)
- Risk: Refactoring domain logic may introduce bugs undetected until integration tests
- Priority: High - domain rules are critical business logic

**No Error Case Testing for AI Services:**
- What's not tested: Malformed JSON from Ollama, timeout scenarios, invalid action types from AI
- Files: `src/Orbit.Infrastructure/Services/` (no unit tests for GeminiIntentService or OllamaIntentService)
- Risk: Deserialization errors silently fail; users see "AI service error" with no detail
- Priority: High - production failures will be opaque

**No Concurrent Request Testing:**
- What's not tested: Two users logging same habit simultaneously, race conditions on email unique constraint
- Files: Test setup creates one user per test; no parallel test scenarios
- Risk: Hidden concurrency bugs only surface under production load
- Priority: Medium - MVP likely single-user; critical before multi-tenant expansion

**No Performance Benchmarking:**
- What's not tested: Response times under load, memory usage with large habit/task lists
- Files: No benchmark project exists
- Risk: Scaling limits discovered in production rather than dev
- Priority: Medium - can wait until usage grows

---

*Concerns audit: 2026-02-07*
