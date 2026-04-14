using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Orbit.IntegrationTests;

/// <summary>
/// Integration tests for AI tool calling.
/// Requires: AI API key configured.
/// Tests are fully repeatable - create test user, run tests, clean up everything.
/// </summary>
[Collection("Sequential")]
public class AiToolCallingTests : IAsyncLifetime
{
    private readonly HttpClient _client;
    private string? _testUserId;
    private readonly string _testEmail = $"ai-tool-test-{Guid.NewGuid()}@integration.test";
    private const string TestCode = "999999";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Rate limiting: AI API rate limits
    private static readonly SemaphoreSlim RateLimitSemaphore = new(1, 1);
    private static DateTime LastApiCall = DateTime.MinValue;

    public AiToolCallingTests(IntegrationTestWebApplicationFactory factory)
    {
        IntegrationTestHelpers.RegisterTestAccount(_testEmail, TestCode);

        _client = factory.CreateClient();
        _client.Timeout = TimeSpan.FromMinutes(5);
    }

    public async Task InitializeAsync()
    {
        var loginResult = await IntegrationTestHelpers.AuthenticateWithCodeAsync(_client, _testEmail, TestCode, JsonOptions);
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
            catch { /* cleanup best-effort */ }
        }

        _client.Dispose();
    }

    // ───────────────────────────────────────────────────────────────
    //  Habit Creation
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateHabit_SimpleDaily_CreatesWithCorrectFrequency()
    {
        var response = await SendChat("create a habit to meditate daily");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("CreateHabit");
        response.Actions[0].Status.Should().Be("Success");
        response.Actions[0].EntityId.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateHabit_WeeklyWithDays_CreatesWithDayRestrictions()
    {
        var response = await SendChat("create a habit to go to the gym on monday wednesday and friday");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("CreateHabit");
        response.Actions[0].Status.Should().Be("Success");
    }

    [Fact]
    public async Task CreateHabit_OneTime_CreatesWithoutFrequency()
    {
        var response = await SendChat("remind me to buy groceries tomorrow");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("CreateHabit");
        response.Actions[0].Status.Should().Be("Success");
    }

    [Fact]
    public async Task CreateHabit_BadHabit_SetsIsBadHabitTrue()
    {
        var response = await SendChat("i want to track when i smoke so i can quit");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("CreateHabit");
        response.Actions[0].Status.Should().Be("Success");
    }

    [Fact]
    public async Task CreateHabit_WithDescription_SetsDescription()
    {
        var response = await SendChat("create a habit called Reading with description: read for 30 minutes before bed");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("CreateHabit");
        response.Actions[0].Status.Should().Be("Success");
    }

    [Fact]
    public async Task CreateHabit_WithChecklist_SetsChecklistItems()
    {
        var response = await SendChat("create a morning routine checklist with items: brush teeth, shower, have breakfast");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("CreateHabit");
        response.Actions[0].Status.Should().Be("Success");
    }

    // ───────────────────────────────────────────────────────────────
    //  Habit Logging
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task LogHabit_ExistingHabit_LogsSuccessfully()
    {
        await CreateHabitViaApi("Morning Run", "Day");

        var response = await SendChat("log my Morning Run habit as done for today");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("LogHabit");
        response.Actions[0].Status.Should().Be("Success");
    }

    [Fact]
    public async Task LogHabit_ByName_MatchesCorrectHabit()
    {
        await CreateHabitViaApi("Yoga", "Day");
        await CreateHabitViaApi("Meditation", "Day");

        var response = await SendChat("I just finished yoga");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("LogHabit");
        response.Actions[0].Status.Should().Be("Success");
        response.Actions[0].EntityName.Should().NotBeNull();
        response.Actions[0].EntityName!.ToLower().Should().Contain("yoga");
    }

    [Fact]
    public async Task LogHabit_WithNote_PassesNoteThrough()
    {
        await CreateHabitViaApi("Running", "Day");

        var response = await SendChat("I ran 5km in 25 minutes today");

        response.Actions.Should().Contain(a => a.Type == "LogHabit" && a.Status == "Success");
    }

    // ───────────────────────────────────────────────────────────────
    //  Habit Update
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateHabit_ChangeTitle_UpdatesTitle()
    {
        await CreateHabitViaApi("Jogging", "Day");

        var response = await SendChat("rename the habit Jogging to Morning Run");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("UpdateHabit");
        response.Actions[0].Status.Should().Be("Success");
    }

    [Fact]
    public async Task UpdateHabit_ChangeFrequency_UpdatesFrequency()
    {
        await CreateHabitViaApi("Stretching", "Day");

        var response = await SendChat("change Stretching to 3 times per week instead of daily");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("UpdateHabit");
        response.Actions[0].Status.Should().Be("Success");
    }

    [Fact]
    public async Task UpdateHabit_AddDescription_SetsDescription()
    {
        await CreateHabitViaApi("Piano Practice", "Day");

        var response = await SendChat("add a description to Piano Practice: practice scales and one song");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("UpdateHabit");
        response.Actions[0].Status.Should().Be("Success");
    }

    [Fact]
    public async Task UpdateHabit_ChangeDueTime_UpdatesTime()
    {
        await CreateHabitViaApi("Wake Up Early", "Day");

        var response = await SendChat("set the time of Wake Up Early to 6:30 AM");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("UpdateHabit");
        response.Actions[0].Status.Should().Be("Success");
    }

    // ───────────────────────────────────────────────────────────────
    //  Habit Deletion
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteHabit_SingleHabit_DeletesSuccessfully()
    {
        await CreateHabitViaApi("Temporary Habit", "Day");

        var response = await SendChat("delete the habit Temporary Habit");

        response.Actions.Should().BeEmpty();
        response.PendingOperations.Should().ContainSingle();
        response.PendingOperations![0].CapabilityId.Should().Be("habits.delete");
    }

    [Fact]
    public async Task DeleteHabit_ByExplicitRequest_DeletesCorrectOne()
    {
        await CreateHabitViaApi("Keep This", "Day");
        await CreateHabitViaApi("Remove This", "Day");

        var response = await SendChat("delete Remove This");

        response.Actions.Should().BeEmpty();
        response.PendingOperations.Should().ContainSingle();
        response.PendingOperations![0].CapabilityId.Should().Be("habits.delete");
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    // ───────────────────────────────────────────────────────────────
    //  Skip Habit
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SkipHabit_RecurringDueToday_SkipsSuccessfully()
    {
        await CreateHabitViaApi("Daily Walk", "Day");

        var response = await SendChat("skip Daily Walk for today");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("SkipHabit");
        response.Actions[0].Status.Should().Be("Success");
    }

    // ───────────────────────────────────────────────────────────────
    //  Sub-habits
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSubHabit_UnderExistingParent_CreatesCorrectly()
    {
        await CreateHabitViaApi("Exercise", "Day");

        var response = await SendChat("add a sub-habit called Push-ups under Exercise");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("CreateSubHabit");
        response.Actions[0].Status.Should().Be("Success");
    }

    [Fact]
    public async Task DuplicateHabit_ExistingHabit_CreatesClone()
    {
        await CreateHabitViaApi("Template Habit", "Day");

        var response = await SendChat("duplicate the habit Template Habit");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("DuplicateHabit");
        response.Actions[0].Status.Should().Be("Success");
    }

    [Fact]
    public async Task MoveHabit_ToNewParent_MovesSuccessfully()
    {
        await CreateHabitViaApi("Fitness", "Day");
        await CreateHabitViaApi("Squats", "Day");

        var response = await SendChat("move the habit Squats under Fitness");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("MoveHabit");
        response.Actions[0].Status.Should().Be("Success");
    }

    // ───────────────────────────────────────────────────────────────
    //  Tags
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AssignTags_ToExistingHabit_AssignsCorrectly()
    {
        await CreateHabitViaApi("Swimming", "Week");

        var response = await SendChat("tag Swimming with Health and Fitness");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("AssignTags");
        response.Actions[0].Status.Should().Be("Success");
    }

    // ───────────────────────────────────────────────────────────────
    //  Bulk Operations
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkCreate_MultipleHabits_CreatesAll()
    {
        var response = await SendChat("create these habits: meditate daily, run 3 times per week, read before bed every day");

        response.Actions.Should().HaveCountGreaterThanOrEqualTo(3);
        response.Actions.Should().OnlyContain(a => a.Type == "CreateHabit" && a.Status == "Success");
    }

    [Fact]
    public async Task BulkLog_MultipleHabits_LogsAll()
    {
        await CreateHabitViaApi("Meditation", "Day");
        await CreateHabitViaApi("Reading", "Day");

        var response = await SendChat("I meditated and read today");

        response.Actions.Should().HaveCountGreaterThanOrEqualTo(2);
        response.Actions.Should().OnlyContain(a => a.Type == "LogHabit" && a.Status == "Success");
    }

    [Fact]
    public async Task BulkAction_CreateAndLog_HandlesMixed()
    {
        await CreateHabitViaApi("Running", "Day");

        var response = await SendChat("I just ran 5k today, also create a habit to stretch daily");

        response.Actions.Should().HaveCountGreaterThanOrEqualTo(2);
        response.Actions.Should().Contain(a => a.Type == "LogHabit");
        response.Actions.Should().Contain(a => a.Type == "CreateHabit");
    }

    // ───────────────────────────────────────────────────────────────
    //  Edge Cases
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task OutOfScope_GeneralQuestion_NoActions()
    {
        var response = await SendChat("what is the capital of France?");

        response.Actions.Should().BeEmpty();
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task LanguageDetection_PortugueseMessage_RespondsInPortuguese()
    {
        var response = await SendChat("crie um habito de meditar todos os dias");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("CreateHabit");
        response.Actions[0].Status.Should().Be("Success");
    }

    [Fact]
    public async Task SuggestBreakdown_ComplexGoal_ReturnsSuggestions()
    {
        var response = await SendChat("help me break down learning to play guitar into sub-habits");

        response.Actions.Should().Contain(a => a.Type == "SuggestBreakdown");
    }

    // ───────────────────────────────────────────────────────────────
    //  Helpers
    // ───────────────────────────────────────────────────────────────

    private async Task<ChatResponse> SendChat(string message, int maxRetries = 2)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            await RateLimitSemaphore.WaitAsync();
            try
            {
                var timeSinceLastCall = DateTime.UtcNow - LastApiCall;
                var minDelay = TimeSpan.FromSeconds(10);
                if (timeSinceLastCall < minDelay)
                    await Task.Delay(minDelay - timeSinceLastCall);

                using var content = new MultipartFormDataContent();
                content.Add(new StringContent(message), "message");

                var httpResponse = await _client.PostAsync("/api/chat", content);
                LastApiCall = DateTime.UtcNow;

                if (!httpResponse.IsSuccessStatusCode)
                {
                    var errorContent = await httpResponse.Content.ReadAsStringAsync();
                    if (attempt < maxRetries && errorContent.Contains("empty response"))
                    {
                        Console.WriteLine($"Retry {attempt + 1}/{maxRetries} for '{message}' (rate limited, waiting 15s)");
                        await Task.Delay(TimeSpan.FromSeconds(15));
                        continue;
                    }

                    httpResponse.StatusCode.Should().Be(HttpStatusCode.OK,
                        $"Chat failed for: '{message}'. Error: {errorContent}");
                }

                var response = await httpResponse.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions);
                response.Should().NotBeNull();
                return response!;
            }
            finally
            {
                RateLimitSemaphore.Release();
            }
        }

        throw new InvalidOperationException($"All retries exhausted for: '{message}'");
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

    // ───────────────────────────────────────────────────────────────
    //  DTOs
    // ───────────────────────────────────────────────────────────────

    private record LoginResponse(Guid UserId, string Token, string Name, string Email);
    private record ChatResponse(
        string? AiMessage,
        List<ActionResultDto> Actions,
        List<PendingOperationDto>? PendingOperations = null);
    private record ActionResultDto(string Type, string Status, Guid? EntityId = null, string? EntityName = null, string? Error = null);
    private record PendingOperationDto(Guid Id, string CapabilityId, string DisplayName, string Summary);
    private record HabitDto(Guid Id, string Title);
    private record PaginatedResponse<T>(List<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);
}
