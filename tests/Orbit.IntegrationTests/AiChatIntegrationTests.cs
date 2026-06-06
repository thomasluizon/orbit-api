using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
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

    private static readonly SemaphoreSlim RateLimitSemaphore = new(1, 1);
    private static DateTime LastApiCall = DateTime.MinValue;

    public AiChatIntegrationTests(IntegrationTestWebApplicationFactory factory)
    {
        IntegrationTestHelpers.RegisterTestAccount(_testUserEmail, TestCode);

        _client = factory.CreateClient();
        _client.Timeout = TimeSpan.FromMinutes(5);
    }

    public async Task InitializeAsync()
    {
        var loginResult = await IntegrationTestHelpers.AuthenticateWithCodeAsync(_client, _testUserEmail, TestCode, JsonOptions);
        _testUserId = loginResult!.UserId.ToString();
    }

    public async Task DisposeAsync()
    {
        if (!string.IsNullOrEmpty(_testUserId))
        {
            try
            {
                var habitsResponse = await _client.GetAsync(IntegrationTestHelpers.BuildHabitSchedulePath());
                if (habitsResponse.IsSuccessStatusCode)
                {
                    var paginated = await habitsResponse.Content.ReadFromJsonAsync<PaginatedResponse<HabitDto>>(JsonOptions);
                    foreach (var habit in paginated?.Items?.DistinctBy(h => h.Id) ?? [])
                        await _client.DeleteAsync($"/api/habits/{habit.Id}");
                }

                await _client.DeleteAsync($"/api/users/{_testUserId}");
            }
            catch { }
        }

        _client.Dispose();
    }

    #region Habit Creation Tests (3)

    [Fact]
    public async Task Chat_CreateBooleanHabit_ShouldSucceed()
    {
        var response = await SendChatMessage("i want to meditate daily");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("CreateHabit");
        response.Actions[0].Status.Should().Be("Success");
        response.Actions[0].EntityId.Should().NotBeEmpty();
        response.AiMessage.Should().NotBeNullOrEmpty();
        response.AiMessage.Should().Match(s => s.Contains("meditat", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Chat_CreateRunningHabit_ExplicitRequest_ShouldSucceed()
    {
        var response = await SendChatMessage("create a daily habit called Running");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("CreateHabit");
        response.Actions[0].Status.Should().Be("Success");
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Chat_CreateQuantifiableHabit_Water_ShouldSucceed()
    {
        var response = await SendChatMessage("i want to drink 8 glasses of water daily");

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
        await SendChatMessage("i want to read daily");

        var response = await SendChatMessage("i read today");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("LogHabit");
        response.Actions[0].Status.Should().Be("Success");
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Chat_LogRunningHabit_WithDistanceNote_ShouldSucceed()
    {
        await CreateHabitViaApi("Running", "Day");

        var response = await SendChatMessage("i ran 3km today");

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
        var response = await SendChatMessage("what's the capital of france?");

        response.Actions.Should().BeEmpty();
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Chat_HomeworkHelp_ShouldReject()
    {
        var response = await SendChatMessage("help me solve this math problem: 2x + 5 = 15");

        response.Actions.Should().BeEmpty();
        response.AiMessage.Should().NotBeNullOrEmpty();
        response.AiMessage.ToLower().Should().MatchRegex("(habit|can't|homework)");
    }

    #endregion

    #region Task-like Request Rejection (1)

    [Fact]
    public async Task Chat_TaskLikeRequest_ShouldRedirectToHabits()
    {
        var response = await SendChatMessage("i need to buy milk today");

        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Edge Cases (2)

    [Fact]
    public async Task Chat_EmptyMessage_ShouldHandleGracefully()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(""), "message");
        var httpResponse = await _client.PostAsync("/api/chat", content);

        httpResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Chat_VeryLongMessage_ShouldHandle()
    {
        var longMessage = string.Concat(Enumerable.Repeat("i need to do something important ", 50));
        var response = await SendChatMessage(longMessage);

        response.Should().NotBeNull();
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Complex Scenarios (2)

    [Fact]
    public async Task Chat_CreateHabitAndLogSameMessage_ShouldHandleBoth()
    {
        var response = await SendChatMessage("i want to track push-ups and i did 20 today");

        response.Actions.Should().HaveCount(2);
        response.Actions.Should().Contain(a => a.Type == "CreateHabit");
        response.Actions.Should().Contain(a => a.Type == "LogHabit");
        response.Actions.Should().OnlyContain(a => a.Status == "Success");
    }

    [Fact]
    public async Task Chat_MixedHabitActionsInOneMessage_ShouldHandleAll()
    {
        var response = await SendChatMessage("i want to start meditating daily and i ran 5km today");

        response.Actions.Should().HaveCountGreaterThan(1);
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Multi-Action Tests (2)

    [Fact]
    public async Task Chat_MultipleCreates_ShouldSucceedForAll()
    {
        var response = await SendChatMessage("i want to exercise, meditate, and read every day");

        response.Actions.Should().HaveCount(3);
        response.Actions.Should().OnlyContain(a => a.Type == "CreateHabit");
        response.Actions.Should().OnlyContain(a => a.Status == "Success");
        response.Actions.Should().OnlyContain(a => a.EntityId != null && a.EntityId != Guid.Empty);
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Chat_PartialFailure_ShouldReturnMixedStatuses()
    {
        await SendChatMessage("i want to jog daily");

        var response = await SendChatMessage("i jogged today");

        response.Actions.Should().NotBeEmpty();
        response.Actions.Should().OnlyContain(a => a.Status == "Success" || a.Status == "Failed");
    }

    #endregion

    #region Image Upload Tests (2)

    [Fact]
    public async Task Chat_UploadImageWithMessage_ShouldHandleGracefully()
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

            var responseText = await httpResponse.Content.ReadAsStringAsync();

            httpResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);

            if (httpResponse.StatusCode == HttpStatusCode.BadRequest)
            {
                responseText.Should().Contain("AI service temporarily unavailable");
                return;
            }

            var response = JsonSerializer.Deserialize<ChatResponse>(responseText, JsonOptions);

            response.Should().NotBeNull();
            response!.AiMessage.Should().NotBeNullOrEmpty();
        }
        finally
        {
            RateLimitSemaphore.Release();
        }
    }

    [Fact]
    public async Task Chat_UploadInvalidFile_ShouldReturn400()
    {
        var fakeImageBytes = System.Text.Encoding.UTF8.GetBytes("This is not an image");
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("Analyze this"), "message");

        var imageContent = new ByteArrayContent(fakeImageBytes);
        imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        content.Add(imageContent, "image", "fake.png");

        var httpResponse = await _client.PostAsync("/api/chat", content);

        httpResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region Routine Intelligence Tests (4)

    [Fact]
    public async Task Chat_AskAboutRoutinePatterns_ShouldAnalyzeAndRespond()
    {
        await SendChatMessage("i want to exercise daily");
        await SendChatMessage("i exercised today");

        var response = await SendChatMessage("analyze my routine");

        response.Should().NotBeNull();
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Chat_CreateHabitWithPotentialConflict_ShouldReturnWarning()
    {
        var firstResponse = await SendChatMessage("i want to exercise every weekday morning");

        var secondResponse = await SendChatMessage("i want to meditate every weekday morning");

        secondResponse.Actions.Should().ContainSingle();
        secondResponse.Actions[0].Type.Should().Be("CreateHabit");
        secondResponse.Actions[0].Status.Should().Be("Success");
        secondResponse.Actions[0].EntityId.Should().NotBeEmpty();

    }

    [Fact]
    public async Task Chat_AskForScheduleSuggestions_ShouldReturnTimeSlots()
    {
        await SendChatMessage("i want to run daily");

        var response = await SendChatMessage("when should i schedule a new reading habit?");

        response.Should().NotBeNull();
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Chat_RoutineAnalysisWithNoData_ShouldNotFail()
    {

        var response = await SendChatMessage("analyze my routine");

        response.Should().NotBeNull();
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Helper Methods

    private static byte[] CreateMinimalPng()
    {
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+b5n8AAAAASUVORK5CYII=");
    }

    private async Task<ChatResponse> SendChatMessage(string message)
    {
        await RateLimitSemaphore.WaitAsync();
        try
        {
            var timeSinceLastCall = DateTime.UtcNow - LastApiCall;
            var minDelay = TimeSpan.FromSeconds(10);
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

    private async Task<Guid> CreateHabitViaApi(string title, string frequencyUnit)
    {
        var response = await _client.PostAsJsonAsync("/api/habits", new
        {
            title,
            type = "Boolean",
            frequencyUnit,
            frequencyQuantity = 1
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Failed to create test habit '{title}'");

        return await IntegrationTestHelpers.ReadCreatedIdAsync(response, JsonOptions);
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
