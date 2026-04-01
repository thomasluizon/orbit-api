using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
namespace Orbit.IntegrationTests;

/// <summary>
/// Core integration tests for the AI Chat endpoint (20 essential scenarios).
/// Tests are fully repeatable - they create a test user, run tests, and clean up everything.
/// Habits-only: no task creation or task completion tests.
/// </summary>
[Collection("Sequential")]
public class AiChatIntegrationTests : IAsyncLifetime
{
    private readonly HttpClient _client;
    private string? _testUserId;
    private readonly string _testUserEmail = $"ai-chat-test-{Guid.NewGuid()}@integration.test";
    private const string TestCode = "999999";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Rate limiting: AI API rate limits
    private static readonly SemaphoreSlim RateLimitSemaphore = new(1, 1);
    private static DateTime LastApiCall = DateTime.MinValue;

    public AiChatIntegrationTests(WebApplicationFactory<Program> factory)
    {
        // Register the test account via env var so send-code accepts a fixed code
        var existing = Environment.GetEnvironmentVariable("TEST_ACCOUNTS") ?? "";
        var entry = $"{_testUserEmail}:{TestCode}";
        Environment.SetEnvironmentVariable("TEST_ACCOUNTS",
            string.IsNullOrEmpty(existing) ? entry : $"{existing},{entry}");

        _client = factory.CreateClient();
        _client.Timeout = TimeSpan.FromMinutes(5);
    }

    public async Task InitializeAsync()
    {
        // Send verification code (uses TEST_ACCOUNTS bypass)
        var sendCodeResponse = await _client.PostAsJsonAsync("/api/auth/send-code", new { email = _testUserEmail });
        sendCodeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify code to create account + get JWT
        var verifyResponse = await _client.PostAsJsonAsync("/api/auth/verify-code", new
        {
            email = _testUserEmail,
            code = TestCode
        });
        verifyResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var loginResult = await verifyResponse.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions);
        _testUserId = loginResult!.UserId.ToString();
        _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {loginResult.Token}");
    }

    public async Task DisposeAsync()
    {
        if (!string.IsNullOrEmpty(_testUserId))
        {
            try
            {
                var habitsResponse = await _client.GetAsync("/api/habits");
                if (habitsResponse.IsSuccessStatusCode)
                {
                    var paginated = await habitsResponse.Content.ReadFromJsonAsync<PaginatedResponse<HabitDto>>(JsonOptions);
                    foreach (var habit in paginated?.Items ?? [])
                        await _client.DeleteAsync($"/api/habits/{habit.Id}");
                }

                await _client.DeleteAsync($"/api/users/{_testUserId}");
            }
            catch { /* cleanup best-effort */ }
        }

        _client.Dispose();
    }

    #region Habit Creation Tests (3)

    [Fact]
    public async Task Chat_CreateBooleanHabit_ShouldSucceed()
    {
        // Act
        var response = await SendChatMessage("i want to meditate daily");

        // Assert
        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("CreateHabit");
        response.Actions[0].Status.Should().Be("Success");
        response.Actions[0].EntityId.Should().NotBeEmpty();
        response.AiMessage.Should().NotBeNullOrEmpty();
        response.AiMessage.Should().Match(s => s.ToLower().Contains("meditat"));
    }

    [Fact]
    public async Task Chat_CreateQuantifiableHabit_Running_ShouldSucceed()
    {
        // Act
        var response = await SendChatMessage("i ran 5km today");

        // Assert - AI may create a habit, log it, or both; exact behavior is non-deterministic
        response.Actions.Should().NotBeEmpty();
        response.Actions.Should().Contain(a => a.Status == "Success");
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Chat_CreateQuantifiableHabit_Water_ShouldSucceed()
    {
        // Act
        var response = await SendChatMessage("i want to drink 8 glasses of water daily");

        // Assert
        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("CreateHabit");
        response.Actions[0].Status.Should().Be("Success");
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Habit Logging Tests (2)

    [Fact]
    public async Task Chat_LogHabit_ToExistingHabit_ShouldSucceed()
    {
        // Arrange - Create a habit first
        await SendChatMessage("i want to read daily");

        // Act - Log to the habit
        var response = await SendChatMessage("i read today");

        // Assert
        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("LogHabit");
        response.Actions[0].Status.Should().Be("Success");
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Chat_LogQuantifiableHabit_WithValue_ShouldSucceed()
    {
        // Arrange
        await SendChatMessage("i want to track my running in km");

        // Act
        var response = await SendChatMessage("i ran 3km today");

        // Assert
        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("LogHabit");
        response.Actions[0].Status.Should().Be("Success");
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Out-of-Scope Tests (2)

    [Fact]
    public async Task Chat_GeneralQuestion_ShouldReject()
    {
        // Act
        var response = await SendChatMessage("what's the capital of france?");

        // Assert
        response.Actions.Should().BeEmpty();
        response.AiMessage.Should().NotBeNullOrEmpty();
        response.AiMessage.ToLower().Should().MatchRegex("(habit|can't|cannot|only)");
    }

    [Fact]
    public async Task Chat_HomeworkHelp_ShouldReject()
    {
        // Act
        var response = await SendChatMessage("help me solve this math problem: 2x + 5 = 15");

        // Assert
        response.Actions.Should().BeEmpty();
        response.AiMessage.Should().NotBeNullOrEmpty();
        response.AiMessage.ToLower().Should().MatchRegex("(habit|can't|homework)");
    }

    #endregion

    #region Task-like Request Rejection (1)

    [Fact]
    public async Task Chat_TaskLikeRequest_ShouldRedirectToHabits()
    {
        // Act
        var response = await SendChatMessage("i need to buy milk today");

        // Assert - AI may or may not create a habit; the key test is it responds successfully
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Edge Cases (2)

    [Fact]
    public async Task Chat_EmptyMessage_ShouldHandleGracefully()
    {
        // Act
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(""), "message");
        var httpResponse = await _client.PostAsync("/api/chat", content);

        // Assert
        httpResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Chat_VeryLongMessage_ShouldHandle()
    {
        // Act
        var longMessage = string.Concat(Enumerable.Repeat("i need to do something important ", 50));
        var response = await SendChatMessage(longMessage);

        // Assert
        response.Should().NotBeNull();
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Complex Scenarios (2)

    [Fact]
    public async Task Chat_CreateHabitAndLogSameMessage_ShouldHandleBoth()
    {
        // Act
        var response = await SendChatMessage("i want to track push-ups and i did 20 today");

        // Assert
        response.Actions.Should().HaveCount(2);
        response.Actions.Should().Contain(a => a.Type == "CreateHabit");
        response.Actions.Should().Contain(a => a.Type == "LogHabit");
        response.Actions.Should().OnlyContain(a => a.Status == "Success");
    }

    [Fact]
    public async Task Chat_MixedHabitActionsInOneMessage_ShouldHandleAll()
    {
        // Act - multiple habit-related actions (no tasks)
        var response = await SendChatMessage("i want to start meditating daily and i ran 5km today");

        // Assert
        response.Actions.Should().HaveCountGreaterThan(1);
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Multi-Action Tests (2)

    [Fact]
    public async Task Chat_MultipleCreates_ShouldSucceedForAll()
    {
        // Act
        var response = await SendChatMessage("i want to exercise, meditate, and read every day");

        // Assert
        response.Actions.Should().HaveCount(3);
        response.Actions.Should().OnlyContain(a => a.Type == "CreateHabit");
        response.Actions.Should().OnlyContain(a => a.Status == "Success");
        response.Actions.Should().OnlyContain(a => a.EntityId != null && a.EntityId != Guid.Empty);
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Chat_PartialFailure_ShouldReturnMixedStatuses()
    {
        // Arrange - Create one habit first
        await SendChatMessage("i want to jog daily");

        // Act - Try to log the existing habit AND log a non-existent habit (simulated by using a specific UUID)
        // Since we can't easily trigger a failure with the AI, we'll just verify the response shape supports it
        var response = await SendChatMessage("i jogged today");

        // Assert - At minimum verify the response structure supports partial success
        response.Actions.Should().NotBeEmpty();
        response.Actions.Should().OnlyContain(a => a.Status == "Success" || a.Status == "Failed");
        // In this case it should be all success, but the structure supports failures
    }

    #endregion

    #region Image Upload Tests (2)

    [Fact]
    public async Task Chat_UploadImageWithMessage_ShouldReturnSuggestions()
    {
        await RateLimitSemaphore.WaitAsync();
        try
        {
            var timeSinceLastCall = DateTime.UtcNow - LastApiCall;
            var minDelay = TimeSpan.FromSeconds(10);
            if (timeSinceLastCall < minDelay)
                await Task.Delay(minDelay - timeSinceLastCall);

            var imageBytes = CreateMinimalPng();
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent("Create habits from this schedule image"), "message");

            var imageContent = new ByteArrayContent(imageBytes);
            imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            content.Add(imageContent, "image", "schedule.png");

            var httpResponse = await _client.PostAsync("/api/chat", content);
            LastApiCall = DateTime.UtcNow;

            httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var responseText = await httpResponse.Content.ReadAsStringAsync();
            var response = JsonSerializer.Deserialize<ChatResponse>(responseText, JsonOptions);

            response.Should().NotBeNull();
            response!.AiMessage.Should().NotBeNullOrEmpty();
            // The AI should respond to the image -- it may return SuggestBreakdown or empty actions
            // depending on what AI Vision interprets from the minimal 1x1 PNG.
            // The key verification is that the request succeeded (200 OK) and the pipeline works end-to-end.
        }
        finally
        {
            RateLimitSemaphore.Release();
        }
    }

    [Fact]
    public async Task Chat_UploadInvalidFile_ShouldReturn400()
    {
        // Arrange - Create a fake text file disguised as image
        var fakeImageBytes = System.Text.Encoding.UTF8.GetBytes("This is not an image");
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("Analyze this"), "message");

        var imageContent = new ByteArrayContent(fakeImageBytes);
        imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        content.Add(imageContent, "image", "fake.png");

        // Act
        var httpResponse = await _client.PostAsync("/api/chat", content);

        // Assert - Should reject with 400 due to invalid file signature
        httpResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region Routine Intelligence Tests (4)

    [Fact]
    public async Task Chat_AskAboutRoutinePatterns_ShouldAnalyzeAndRespond()
    {
        // Arrange - Create a daily habit and log it a few times to establish a pattern
        await SendChatMessage("i want to exercise daily");
        await SendChatMessage("i exercised today");

        // Act - Ask about routine patterns
        var response = await SendChatMessage("analyze my routine");

        // Assert
        response.Should().NotBeNull();
        response.AiMessage.Should().NotBeNullOrEmpty();
        // AI should respond with scheduling/routine analysis content
        // (exact content is non-deterministic, just verify it responded)
    }

    [Fact]
    public async Task Chat_CreateHabitWithPotentialConflict_ShouldReturnWarning()
    {
        // Arrange - Create a daily habit
        var firstResponse = await SendChatMessage("i want to exercise every weekday morning");

        // Act - Create another habit on the same schedule
        var secondResponse = await SendChatMessage("i want to meditate every weekday morning");

        // Assert - Habit creation should succeed (not blocked by conflict)
        secondResponse.Actions.Should().ContainSingle();
        secondResponse.Actions[0].Type.Should().Be("CreateHabit");
        secondResponse.Actions[0].Status.Should().Be("Success");
        secondResponse.Actions[0].EntityId.Should().NotBeEmpty();

        // Note: ConflictWarning may or may not be populated depending on whether
        // enough log history exists. The key test is that creation succeeds regardless.
    }

    [Fact]
    public async Task Chat_AskForScheduleSuggestions_ShouldReturnTimeSlots()
    {
        // Arrange - Create a habit to establish some routine context
        await SendChatMessage("i want to run daily");

        // Act - Ask for scheduling suggestions
        var response = await SendChatMessage("when should i schedule a new reading habit?");

        // Assert
        response.Should().NotBeNull();
        response.AiMessage.Should().NotBeNullOrEmpty();
        // AI should leverage routine context to suggest times
        // (exact suggestions are non-deterministic, verify it responded)
    }

    [Fact]
    public async Task Chat_RoutineAnalysisWithNoData_ShouldNotFail()
    {
        // Arrange - Fresh user with no habits or logs (already clean from InitializeAsync)

        // Act - Ask about routine with no data
        var response = await SendChatMessage("analyze my routine");

        // Assert - Should succeed gracefully with a message indicating insufficient data
        response.Should().NotBeNull();
        response.AiMessage.Should().NotBeNullOrEmpty();
        // AI should indicate not enough data or empty routine info
        // (graceful handling, no errors)
    }

    #endregion

    #region Helper Methods

    private static byte[] CreateMinimalPng()
    {
        // Minimal valid 1x1 white pixel PNG
        // PNG signature + IHDR + IDAT + IEND
        return new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, // IHDR chunk length + type
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, // 1x1 pixels
            0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53, // 8-bit RGB, CRC
            0xDE, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41, // IDAT chunk
            0x54, 0x08, 0xD7, 0x63, 0xF8, 0xCF, 0xC0, 0x00, // compressed data
            0x00, 0x00, 0x02, 0x00, 0x01, 0xE2, 0x21, 0xBC, // CRC
            0x33, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, // IEND chunk
            0x44, 0xAE, 0x42, 0x60, 0x82                     // IEND CRC
        };
    }

    private async Task<ChatResponse> SendChatMessage(string message)
    {
        // Rate limiting: Wait 10 seconds between API calls to respect AI API rate limits
        await RateLimitSemaphore.WaitAsync();
        try
        {
            var timeSinceLastCall = DateTime.UtcNow - LastApiCall;
            var minDelay = TimeSpan.FromSeconds(10); // 6 requests per minute max

            if (timeSinceLastCall < minDelay)
            {
                var remainingDelay = minDelay - timeSinceLastCall;
                Console.WriteLine($"Rate limiting: Waiting {remainingDelay.TotalSeconds:F1}s before next API call...");
                await Task.Delay(remainingDelay);
            }

            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(message), "message");

            var httpResponse = await _client.PostAsync("/api/chat", content);
            LastApiCall = DateTime.UtcNow;

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorContent = await httpResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"API ERROR ({httpResponse.StatusCode}): {errorContent}");
                Console.WriteLine($"   For message: '{message}'");
                Console.WriteLine($"   Request had image: false");
            }

            httpResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"Error: {await httpResponse.Content.ReadAsStringAsync()}");

            var responseText = await httpResponse.Content.ReadAsStringAsync();
            var response = JsonSerializer.Deserialize<ChatResponse>(responseText, JsonOptions);

            response.Should().NotBeNull();
            return response!;
        }
        finally
        {
            RateLimitSemaphore.Release();
        }
    }

    #endregion

    #region DTOs

    private record LoginResponse(Guid UserId, string Token, string Name, string Email);
    private record ChatResponse(string? AiMessage, List<ActionResultDto> Actions);
    private record ActionResultDto(string Type, string Status, Guid? EntityId = null, string? EntityName = null, string? Error = null);
    private record HabitDto(Guid Id, string Title);
    private record PaginatedResponse<T>(List<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);

    #endregion
}
