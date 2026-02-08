# Testing Patterns

**Analysis Date:** 2026-02-07

## Test Framework

**Runner:**
- xUnit 2.9.3
- Config: No explicit `xunit.runner.json` - uses default configuration
- Located: `tests/Orbit.IntegrationTests/`

**Assertion Library:**
- FluentAssertions 8.8.0
- Method chaining style: `response.ExecutedActions.Should().ContainSingle().Which.Should().StartWith("CreateTask:")`

**Run Commands:**
```bash
# Run all tests (from project root)
dotnet test

# Run specific test file
dotnet test tests/Orbit.IntegrationTests/AiChatIntegrationTests.cs

# Watch mode (requires dotnet watch)
dotnet watch test

# Coverage (requires coverlet)
dotnet test /p:CollectCoverage=true /p:CoverageFormat=opencover
```

**Test Infrastructure:**
- `Microsoft.AspNetCore.Mvc.Testing` 10.0.2 for WebApplicationFactory
- `coverlet.collector` 6.0.4 for code coverage collection

## Test File Organization

**Location:**
- Integration tests only (no unit tests found)
- Path: `tests/Orbit.IntegrationTests/AiChatIntegrationTests.cs`
- Single test class model for integration testing

**Naming:**
- Test class: `AiChatIntegrationTests`
- Test methods: `Chat_[Action]_[Scenario]_Should[Expected]`
- Examples: `Chat_CreateTask_WithToday_ShouldSucceed()`, `Chat_LogQuantifiableHabit_WithValue_ShouldSucceed()`

**Structure:**
```
tests/
└── Orbit.IntegrationTests/
    ├── Orbit.IntegrationTests.csproj
    ├── AiChatIntegrationTests.cs      # 379 lines, 15 test scenarios
    └── SequentialTestCollection.cs    # xUnit collection definition
```

## Test Structure

**Suite Organization:**
```csharp
[Collection("Sequential")]  // Custom xUnit collection
public class AiChatIntegrationTests : IAsyncLifetime
{
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _factory;
    private string? _testUserId;
    private string? _authToken;

    public AiChatIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync() { ... }  // IAsyncLifetime setup
    public async Task DisposeAsync() { ... }     // IAsyncLifetime teardown

    #region Task Creation Tests (3)
    [Fact]
    public async Task Chat_CreateTask_WithToday_ShouldSucceed() { ... }
    #endregion
}
```

**Patterns:**

**Setup (InitializeAsync - IAsyncLifetime):**
- Create unique test user per test run: `_testUserEmail = $"test-{Guid.NewGuid()}@integration.test"`
- Register user via `/api/auth/register` endpoint
- Login and extract JWT token
- Store token in default request headers: `_client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_authToken}")`

**Teardown (DisposeAsync - IAsyncLifetime):**
- Delete all habits via DELETE `/api/habits/{id}`
- Delete all tasks via DELETE `/api/tasks/{id}`
- Delete test user via DELETE `/api/users/{_testUserId}`
- Wrapped in try-catch to prevent cleanup failures from masking test failures

**Test Method Structure (Arrange-Act-Assert):**
```csharp
[Fact]
public async Task Chat_LogHabit_ToExistingHabit_ShouldSucceed()
{
    // Arrange - Create a habit first
    await SendChatMessage("i want to read daily");

    // Act - Log to the habit
    var response = await SendChatMessage("i read today");

    // Assert
    response.ExecutedActions.Should().ContainSingle()
        .Which.Should().StartWith("LogHabit:");
    response.AiMessage.Should().NotBeNullOrEmpty();
}
```

## Mocking

**Framework:** No explicit mocking framework detected

**Approach:**
- Uses real HttpClient against running WebApplication (integration testing style)
- No Moq or NSubstitute observed
- External AI provider called directly: Gemini or Ollama configured at runtime
- Database interactions are real (test user isolation via unique emails)

**Rate Limiting (External API Handling):**
```csharp
private static readonly SemaphoreSlim RateLimitSemaphore = new(1, 1);
private static DateTime LastApiCall = DateTime.MinValue;

private async Task<ChatResponse> SendChatMessage(string message)
{
    await RateLimitSemaphore.WaitAsync();
    try
    {
        var timeSinceLastCall = DateTime.UtcNow - LastApiCall;
        var minDelay = TimeSpan.FromSeconds(10);  // 6 requests per minute max

        if (timeSinceLastCall < minDelay)
        {
            await Task.Delay(remainingDelay);
        }

        var httpResponse = await _client.PostAsJsonAsync("/api/chat", new { message });
        LastApiCall = DateTime.UtcNow;
        // ...
    }
    finally
    {
        RateLimitSemaphore.Release();
    }
}
```

**What NOT to Mock:**
- Database (uses real test user per run)
- HTTP endpoints (uses real WebApplicationFactory)
- External AI provider (calls real Gemini or Ollama)
- This approach ensures realistic integration testing

## Test Data & Fixtures

**Test User Data:**
```csharp
private readonly string _testUserEmail = $"test-{Guid.NewGuid()}@integration.test";
private const string TestUserPassword = "TestPassword123!";

// Per-test isolation via unique email addresses
```

**Location:**
- Defined inline in test class properties
- No separate fixture files or builders observed
- Test data embedded in test methods or helper methods

**Registration Pattern:**
```csharp
var registerResponse = await _client.PostAsJsonAsync("/api/auth/register", new
{
    name = "AI Test User",
    email = _testUserEmail,
    password = TestUserPassword
});
```

## Coverage

**Requirements:** No code coverage requirements enforced

**View Coverage:**
```bash
dotnet test /p:CollectCoverage=true /p:CoverageFormat=opencover
# Creates coverage reports in: TestResults/coverage.opencover.xml
```

**Observed Coverage:**
- 15 AI chat scenarios covering task creation, habit creation/logging, completions, rejections, edge cases
- Happy-path focused (no negative unit test scenarios observed)
- Integration-level coverage (real API calls, real database)

## Test Types

**Unit Tests:**
- Not found in codebase
- Domain entities could use unit tests (factory methods, validations) but currently untested
- Application handlers could use unit tests with mocked repositories but currently integration-tested

**Integration Tests:**
- Comprehensive: `tests/Orbit.IntegrationTests/AiChatIntegrationTests.cs` (15 scenarios)
- Full stack: Creates real users, hits real endpoints, calls real AI provider
- Test scenarios (31 in memory, 15 executed per run):
  - **Task Creation (3):** Today, Tomorrow, Multiple
  - **Habit Creation (3):** Boolean, Quantifiable (Running), Quantifiable (Water)
  - **Habit Logging (2):** Existing habit, Quantifiable with value
  - **Task Completion (1):** Mark task complete
  - **Out-of-Scope Rejection (2):** General questions, Homework help
  - **Edge Cases (2):** Empty message, Very long message
  - **Complex Scenarios (2):** Create & log same message, Mixed actions
- Setup via `InitializeAsync()` registers test user once per test class
- Teardown via `DisposeAsync()` cleans all created data

**E2E Tests:**
- Not formally separated from integration tests
- `AiChatIntegrationTests` effectively E2E: Real HTTP, real database, real AI
- No Selenium/Playwright (browser automation) tests

**Sequential Execution:**
```csharp
// SequentialTestCollection.cs
[CollectionDefinition("Sequential", DisableParallelization = true)]
public class SequentialTestCollection : ICollectionFixture<WebApplicationFactory<Program>>
{
}

// Applied to test class
[Collection("Sequential")]
public class AiChatIntegrationTests : IAsyncLifetime { }
```

**Rationale:** Prevents concurrent API calls that would hit rate limits and cause flakiness

## Common Patterns

**Async Testing:**
```csharp
public async Task InitializeAsync()
{
    var registerResponse = await _client.PostAsJsonAsync("/api/auth/register", new { ... });
    registerResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    var registerResult = await registerResponse.Content.ReadFromJsonAsync<RegisterResponse>();
    _testUserId = registerResult!.UserId;
}

// Test method
[Fact]
public async Task Chat_CreateTask_WithToday_ShouldSucceed()
{
    var response = await SendChatMessage("i need to buy milk today");
    // assertions
}
```

**Error Assertions (Content Inspection):**
```csharp
private async Task<ChatResponse> SendChatMessage(string message)
{
    var httpResponse = await _client.PostAsJsonAsync("/api/chat", new { message });

    if (!httpResponse.IsSuccessStatusCode)
    {
        var errorContent = await httpResponse.Content.ReadAsStringAsync();
        Console.WriteLine($"❌ API ERROR ({httpResponse.StatusCode}): {errorContent}");
    }

    httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);

    var content = await httpResponse.Content.ReadAsStringAsync();
    var response = JsonSerializer.Deserialize<ChatResponse>(content, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    response.Should().NotBeNull();
    return response!;
}
```

**String Matching Assertions:**
```csharp
// Exact content checks
response.AiMessage.Should().Match(s => s.ToLower().Contains("milk"));

// Regex patterns
response.AiMessage.ToLower().Should().MatchRegex("(habit|task|can't|cannot|only)");

// Collection assertions
response.ExecutedActions.Should().ContainSingle().Which.Should().StartWith("CreateTask:");
response.ExecutedActions.Should().HaveCountGreaterThan(1);
response.ExecutedActions.Should().Contain(a => a.StartsWith("CreateHabit:"));
```

## Test Reliability

**Known Flakiness Factors:**
- Gemini (~95% pass rate): Fast, reliable JSON, consistent responses
- Ollama (~65% pass rate): Slow (30s), inconsistent JSON, non-deterministic responses
- Rate limiting: 10s delay enforced between API calls to respect Gemini free tier limits
- AI Provider switching: Set `"AiProvider": "Gemini"` or `"Ollama"` in appsettings

**Output Capture:**
- Inline logging with emoji prefixes aids debugging: `Console.WriteLine($"⏱️  Rate limiting: ...")`
- API errors logged to console for quick triage
- Test execution time impacts due to rate limiting (15 tests × 10s minimum = 150s minimum)

## Test Configuration

**CollectionDefinition:**
- Location: `tests/Orbit.IntegrationTests/SequentialTestCollection.cs`
- Prevents test parallelization to honor API rate limits
- Provides singleton `WebApplicationFactory<Program>` fixture

**Database per Test Run:**
- No explicit test database configuration
- EnsureCreatedAsync() in Program.cs creates schema on startup
- Uses real PostgreSQL configured in `appsettings.Development.json`
- Test cleanup via DELETE endpoints ensures isolation without DB recreation

---

*Testing analysis: 2026-02-07*
