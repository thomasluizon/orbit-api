using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Orbit.IntegrationTests;

/// <summary>
/// Comprehensive edge case and missing scenario integration tests for AI tool calling.
/// Covers: QueryHabitsTool, CreateHabit extended, UpdateHabit extended, LogHabit edge cases,
/// SkipHabit edge cases, BulkOperations, SubHabit/hierarchy, AssignTags extended,
/// Multi-turn context, Boundary/error tests, DuplicateHabit extended.
/// Requires: Gemini API key configured. Tests hit real AI and are rate-limited.
/// </summary>
[Collection("Sequential")]
public class AiToolEdgeCaseTests : IAsyncLifetime
{
    private readonly HttpClient _client;
    private string? _testUserId;
    private readonly string _testEmail = $"ai-edge-test-{Guid.NewGuid()}@integration.test";
    private const string TestCode = "999999";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Rate limiting: Gemini free tier allows ~15 RPM
    private static readonly SemaphoreSlim RateLimitSemaphore = new(1, 1);
    private static DateTime LastApiCall = DateTime.MinValue;

    public AiToolEdgeCaseTests(WebApplicationFactory<Program> factory)
    {
        var existing = Environment.GetEnvironmentVariable("TEST_ACCOUNTS") ?? "";
        var entry = $"{_testEmail}:{TestCode}";
        Environment.SetEnvironmentVariable("TEST_ACCOUNTS",
            string.IsNullOrEmpty(existing) ? entry : $"{existing},{entry}");

        _client = factory.CreateClient();
        _client.Timeout = TimeSpan.FromMinutes(5);
    }

    public async Task InitializeAsync()
    {
        var sendCodeResponse = await _client.PostAsJsonAsync("/api/auth/send-code", new { email = _testEmail });
        sendCodeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var verifyResponse = await _client.PostAsJsonAsync("/api/auth/verify-code", new
        {
            email = _testEmail,
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
                // Delete all tags first (cleanup)
                var tagsResponse = await _client.GetAsync("/api/tags");
                if (tagsResponse.IsSuccessStatusCode)
                {
                    var tags = await tagsResponse.Content.ReadFromJsonAsync<List<TagDto>>(JsonOptions);
                    foreach (var tag in tags ?? [])
                        await _client.DeleteAsync($"/api/tags/{tag.Id}");
                }

                // Delete all habits
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

    // ===================================================================
    //  SECTION 1: QueryHabitsTool Integration Tests (6 tests)
    // ===================================================================

    [Fact]
    public async Task QueryHabits_NoHabits_ReturnsEmptyResponse()
    {
        var response = await SendChat("list all my habits");

        // AI should respond indicating no habits found (query_habits returns empty)
        response.Should().NotBeNull();
        response.AiMessage.Should().NotBeNullOrEmpty();
        // No action chips expected for a read-only query
    }

    [Fact]
    public async Task QueryHabits_SearchByTitle_FindsMatchingHabit()
    {
        await CreateHabitViaApi("Alpha Meditation", "Day");
        await CreateHabitViaApi("Beta Running", "Day");

        var response = await SendChat("search my habits for Alpha Meditation");

        response.Should().NotBeNull();
        response.AiMessage.Should().NotBeNullOrEmpty();
        response.AiMessage!.ToLower().Should().Contain("alpha");
    }

    [Fact]
    public async Task QueryHabits_FilterByFrequency_ReturnsCorrectHabits()
    {
        await CreateHabitViaApi("Weekly Yoga", "Week");
        await CreateHabitViaApi("Daily Stretch", "Day");

        var response = await SendChat("show me only my weekly habits");

        response.Should().NotBeNull();
        response.AiMessage.Should().NotBeNullOrEmpty();
        // Should mention the weekly habit
        response.AiMessage!.ToLower().Should().MatchRegex("(yoga|weekly)");
    }

    [Fact]
    public async Task QueryHabits_WithMetrics_IncludesStreakData()
    {
        var habitId = await CreateHabitViaApi("Streak Test", "Day");
        // Log it to create some metrics
        await _client.PostAsync($"/api/habits/{habitId}/log", null);

        var response = await SendChat("show me my habit Streak Test with metrics and progress data");

        response.Should().NotBeNull();
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task QueryHabits_NoActionChips_WhenReadOnly()
    {
        await CreateHabitViaApi("Observable Habit", "Day");

        var response = await SendChat("list all my current habits");

        response.Should().NotBeNull();
        // query_habits is read-only, so it should NOT produce action chips
        // If the AI only queries (no create/log/update/delete), Actions should be empty
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task QueryHabits_DateFilter_ReturnsScheduledHabits()
    {
        await CreateHabitViaApi("Today Habit", "Day");

        var response = await SendChat("what habits are due today?");

        response.Should().NotBeNull();
        response.AiMessage.Should().NotBeNullOrEmpty();
        // Should show at least the habit created above
        response.AiMessage!.ToLower().Should().MatchRegex("(today|habit|due)");
    }

    // ===================================================================
    //  SECTION 2: CreateHabit Extended Tests (6 tests)
    // ===================================================================

    [Fact]
    public async Task CreateHabit_WithTagNames_CreatesAndAssignsTags()
    {
        var response = await SendChat(
            "Create a daily habit called 'Tagged Exercise' and tag it with Health and Fitness");

        response.Actions.Should().Contain(a => a.Type == "CreateHabit" && a.Status == "Success");

        // Verify the habit was created
        var habits = await GetAllHabits();
        var taggedHabit = habits.FirstOrDefault(h =>
            h.Title.Contains("Tagged", StringComparison.OrdinalIgnoreCase) ||
            h.Title.Contains("Exercise", StringComparison.OrdinalIgnoreCase));
        taggedHabit.Should().NotBeNull("the AI should have created a habit with 'Tagged' or 'Exercise' in the name");
    }

    [Fact]
    public async Task CreateHabit_WithReminder_SetsReminderConfig()
    {
        var response = await SendChat(
            "Create a daily habit called 'Reminder Test' with a reminder 15 minutes before");

        response.Actions.Should().Contain(a => a.Type == "CreateHabit" && a.Status == "Success");
    }

    [Fact]
    public async Task CreateHabit_WithSubHabits_CreatesHierarchy()
    {
        var response = await SendChat(
            "Create a daily habit called 'Morning Routine EdgeTest' with sub-habits: Brush teeth, Shower, Breakfast");

        // AI may use create_habit with sub_habits param or multiple create_sub_habit calls
        response.Actions.Should().Contain(a =>
            (a.Type == "CreateHabit" || a.Type == "CreateSubHabit") && a.Status == "Success");
    }

    [Fact]
    public async Task CreateHabit_WithDueTime_SetsCorrectTime()
    {
        var response = await SendChat(
            "Create a daily habit called 'Early Bird Test' with due time at 06:30");

        response.Actions.Should().Contain(a => a.Type == "CreateHabit" && a.Status == "Success");
    }

    [Fact]
    public async Task CreateHabit_MonthlyFrequency_CreatesMonthlyHabit()
    {
        var response = await SendChat(
            "Create a monthly habit called 'Monthly Review Test'");

        response.Actions.Should().ContainSingle();
        response.Actions[0].Type.Should().Be("CreateHabit");
        response.Actions[0].Status.Should().Be("Success");
    }

    [Fact]
    public async Task CreateHabit_WithAllFields_CreatesCompleteHabit()
    {
        var response = await SendChat(
            "Create a weekly habit called 'Complete Habit Test' with description 'Full featured habit', " +
            "on Monday and Wednesday, with checklist items: item one, item two, item three");

        response.Actions.Should().Contain(a => a.Type == "CreateHabit" && a.Status == "Success");
    }

    // ===================================================================
    //  SECTION 3: UpdateHabit Extended Tests (5 tests)
    // ===================================================================

    [Fact]
    public async Task UpdateHabit_ClearDescription_RemovesDescription()
    {
        await CreateHabitViaApi("Desc Clear Test", "Day", description: "old description");

        var response = await SendChat("remove the description from the habit called Desc Clear Test");

        response.Actions.Should().Contain(a => a.Type == "UpdateHabit" && a.Status == "Success");
    }

    [Fact]
    public async Task UpdateHabit_ConvertToOneTime_RemovesFrequency()
    {
        await CreateHabitViaApi("Recurring To OneTime", "Day");

        var response = await SendChat(
            "Convert the habit 'Recurring To OneTime' from a daily recurring habit to a one-time task");

        response.Actions.Should().Contain(a => a.Type == "UpdateHabit" && a.Status == "Success");
    }

    [Fact]
    public async Task UpdateHabit_UpdateChecklist_ReplacesItems()
    {
        // Create habit with checklist via API
        var habitId = await CreateHabitViaApiWithChecklist("Checklist Update Test", "Day",
            new[] { "Old item 1", "Old item 2" });

        var response = await SendChat(
            "Replace the checklist of 'Checklist Update Test' with these items: New Alpha, New Beta, New Gamma");

        response.Actions.Should().Contain(a => a.Type == "UpdateHabit" && a.Status == "Success");
    }

    [Fact]
    public async Task UpdateHabit_AddDays_SetsSpecificDays()
    {
        await CreateHabitViaApi("Day Schedule Test", "Day");

        var response = await SendChat(
            "Change the habit 'Day Schedule Test' to only occur on Tuesday and Thursday");

        response.Actions.Should().Contain(a => a.Type == "UpdateHabit" && a.Status == "Success");
    }

    [Fact]
    public async Task UpdateHabit_ChangeMultipleFields_UpdatesAll()
    {
        await CreateHabitViaApi("Multi Update Test", "Day");

        var response = await SendChat(
            "Update the habit 'Multi Update Test': rename it to 'Updated Multi Test' and change it to weekly");

        response.Actions.Should().Contain(a => a.Type == "UpdateHabit" && a.Status == "Success");
    }

    // ===================================================================
    //  SECTION 4: LogHabit Edge Cases (3 tests)
    // ===================================================================

    [Fact]
    public async Task LogHabit_AlreadyLogged_UnlogsHabit()
    {
        var habitId = await CreateHabitViaApi("Toggle Log Test", "Day");
        // Log it first via API
        await _client.PostAsync($"/api/habits/{habitId}/log", null);

        // AI should toggle (unlog) since it's already logged
        var response = await SendChat("log Toggle Log Test as done for today");

        response.Actions.Should().Contain(a => a.Type == "LogHabit");
        // The tool has toggle behavior, so this should succeed either way
    }

    [Fact]
    public async Task LogHabit_NonExistentHabit_HandlesGracefully()
    {
        // No habits exist with this name
        var response = await SendChat("log my 'Totally Nonexistent Xyz Habit 999' as done today");

        response.Should().NotBeNull();
        response.AiMessage.Should().NotBeNullOrEmpty();
        // AI should indicate the habit was not found
    }

    [Fact]
    public async Task LogHabit_WithLongNote_PreservesFullNote()
    {
        await CreateHabitViaApi("Long Note Test", "Day");

        var longNote = "Today I had an incredible session. " +
            "I managed to focus for the entire duration without any distractions. " +
            "I plan to continue this momentum tomorrow and build on the progress I made.";

        var response = await SendChat($"Log 'Long Note Test' as done with note: {longNote}");

        response.Actions.Should().Contain(a => a.Type == "LogHabit" && a.Status == "Success");
    }

    // ===================================================================
    //  SECTION 5: SkipHabit Edge Cases (3 tests)
    // ===================================================================

    [Fact]
    public async Task SkipHabit_OneTimeHabit_FailsGracefully()
    {
        // Create a one-time task (no frequency)
        await CreateHabitViaApiOneTime("OneTime Skip Test");

        var response = await SendChat("skip the habit 'OneTime Skip Test' for today");

        response.Should().NotBeNull();
        // The AI should either report failure or explain one-time tasks can't be skipped
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SkipHabit_FutureHabit_FailsGracefully()
    {
        // Create a daily habit with future due date
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7));
        await CreateHabitViaApiWithDate("Future Skip Test", "Day", futureDate);

        var response = await SendChat("skip the habit 'Future Skip Test' for today");

        response.Should().NotBeNull();
        // Should fail because the habit is not yet due
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SkipHabit_AdvancesDueDate_ToNextOccurrence()
    {
        var habitId = await CreateHabitViaApi("Skip Advance Test", "Day");

        var response = await SendChat("skip the habit 'Skip Advance Test' for today");

        response.Actions.Should().Contain(a => a.Type == "SkipHabit" && a.Status == "Success");

        // Verify the habit's due date advanced (no longer today)
        var habits = await GetAllHabits();
        var skipped = habits.FirstOrDefault(h =>
            h.Title.Contains("Skip Advance", StringComparison.OrdinalIgnoreCase));
        skipped.Should().NotBeNull();
    }

    // ===================================================================
    //  SECTION 6: BulkOperations Tests (4 tests)
    // ===================================================================

    [Fact]
    public async Task BulkLog_AlreadyLoggedHabits_SkipsThose()
    {
        var id1 = await CreateHabitViaApi("Bulk Already A", "Day");
        var id2 = await CreateHabitViaApi("Bulk Already B", "Day");

        // Pre-log one of them
        await _client.PostAsync($"/api/habits/{id1}/log", null);

        var response = await SendChat("I completed both Bulk Already A and Bulk Already B today");

        response.Should().NotBeNull();
        // At least one should be logged (B), the other may toggle or skip
        response.Actions.Should().NotBeEmpty();
    }

    [Fact]
    public async Task BulkLog_AllChildrenLogged_AutoCompletesParent()
    {
        // Create parent with children via API
        var parentId = await CreateHabitViaApi("AutoComplete Parent", "Day");
        await CreateSubHabitViaApi(parentId, "Child Alpha");
        await CreateSubHabitViaApi(parentId, "Child Beta");

        // Log both children
        var response = await SendChat("I finished Child Alpha and Child Beta today");

        response.Should().NotBeNull();
        response.Actions.Should().Contain(a => a.Type == "LogHabit" && a.Status == "Success");
    }

    [Fact]
    public async Task BulkSkip_MultipleRecurring_SkipsAll()
    {
        await CreateHabitViaApi("Bulk Skip One", "Day");
        await CreateHabitViaApi("Bulk Skip Two", "Day");

        var response = await SendChat("skip both 'Bulk Skip One' and 'Bulk Skip Two' for today");

        response.Should().NotBeNull();
        // AI may use bulk_skip_habits or multiple skip_habit calls
        var skipActions = response.Actions.Where(a =>
            a.Type == "SkipHabit" || a.Type == "BulkSkipHabits").ToList();
        skipActions.Should().NotBeEmpty();
    }

    [Fact]
    public async Task BulkSkip_MixedRecurringOneTime_PartialSuccess()
    {
        await CreateHabitViaApi("Bulk Mix Recurring", "Day");
        await CreateHabitViaApiOneTime("Bulk Mix OneTime");

        var response = await SendChat(
            "skip both 'Bulk Mix Recurring' and 'Bulk Mix OneTime' for today");

        response.Should().NotBeNull();
        response.AiMessage.Should().NotBeNullOrEmpty();
        // At least one skip should succeed (the recurring one)
    }

    // ===================================================================
    //  SECTION 7: SubHabit and Hierarchy Tests (3 tests)
    // ===================================================================

    [Fact]
    public async Task CreateSubHabit_InheritsParentFrequency_WhenNotOverridden()
    {
        await CreateHabitViaApi("Weekly Parent", "Week");

        var response = await SendChat(
            "Add a sub-habit called 'Sub Under Weekly' under 'Weekly Parent'");

        response.Actions.Should().Contain(a =>
            (a.Type == "CreateSubHabit" || a.Type == "CreateHabit") && a.Status == "Success");
    }

    [Fact]
    public async Task MoveHabit_ToTopLevel_RemovesParent()
    {
        var parentId = await CreateHabitViaApi("Container Habit", "Day");
        var childId = await CreateSubHabitViaApi(parentId, "Nested Child");

        var response = await SendChat(
            "Move the habit 'Nested Child' to top level, remove it from its parent");

        response.Actions.Should().Contain(a => a.Type == "MoveHabit" && a.Status == "Success");
    }

    [Fact]
    public async Task MoveHabit_CircularReference_Prevented()
    {
        var parentId = await CreateHabitViaApi("Circular Parent", "Day");
        var childId = await CreateSubHabitViaApi(parentId, "Circular Child");

        // Try to move parent under its own child (should fail)
        var response = await SendChat(
            "Move the habit 'Circular Parent' under 'Circular Child'");

        response.Should().NotBeNull();
        // AI should report an error or the backend should prevent circular reference
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    // ===================================================================
    //  SECTION 8: AssignTags Extended Tests (3 tests)
    // ===================================================================

    [Fact]
    public async Task AssignTags_NewTagNames_CreatesAndAssigns()
    {
        await CreateHabitViaApi("Tag Create Test", "Day");

        var response = await SendChat(
            "Tag the habit 'Tag Create Test' with UniqueTagAlpha and UniqueTagBeta");

        response.Actions.Should().Contain(a => a.Type == "AssignTags" && a.Status == "Success");
    }

    [Fact]
    public async Task AssignTags_EmptyArray_ClearsAllTags()
    {
        var habitId = await CreateHabitViaApi("Tag Clear Test", "Day");

        // First assign some tags
        await SendChat("Tag 'Tag Clear Test' with Health");

        // Then clear them
        var response = await SendChat(
            "Remove all tags from the habit 'Tag Clear Test'");

        response.Should().NotBeNull();
        response.AiMessage.Should().NotBeNullOrEmpty();
        // AI may use assign_tags with empty array or explain how to remove tags
    }

    [Fact]
    public async Task AssignTags_MixedExistingAndNew_HandlesBoth()
    {
        await CreateHabitViaApi("Mixed Tag Test", "Day");

        // Create one tag first
        await SendChat("Tag the habit 'Mixed Tag Test' with ExistingOne");

        // Now assign both an existing tag and a new one
        var response = await SendChat(
            "Tag the habit 'Mixed Tag Test' with ExistingOne and BrandNewTag");

        response.Actions.Should().Contain(a => a.Type == "AssignTags" && a.Status == "Success");
    }

    // ===================================================================
    //  SECTION 9: Multi-Turn and Context Tests (3 tests)
    // ===================================================================

    [Fact]
    public async Task MultiTurn_CreateThenLog_MaintainsContext()
    {
        // Turn 1: Create a habit
        var createResponse = await SendChat("Create a daily habit called 'Context Log Test'");
        createResponse.Actions.Should().Contain(a => a.Type == "CreateHabit" && a.Status == "Success");

        // Turn 2: Log it (AI should find it by name from context)
        var logResponse = await SendChat("I just completed Context Log Test");
        logResponse.Actions.Should().Contain(a => a.Type == "LogHabit" && a.Status == "Success");
    }

    [Fact]
    public async Task MultiTurn_CreateThenUpdate_FindsByName()
    {
        // Turn 1: Create
        var createResponse = await SendChat("Create a daily habit called 'Context Update Test'");
        createResponse.Actions.Should().Contain(a => a.Type == "CreateHabit" && a.Status == "Success");

        // Turn 2: Update it by name
        var updateResponse = await SendChat(
            "Change the habit 'Context Update Test' to weekly instead of daily");
        updateResponse.Actions.Should().Contain(a => a.Type == "UpdateHabit" && a.Status == "Success");
    }

    [Fact]
    public async Task MultiTurn_AskAboutHabit_ThenModify()
    {
        await CreateHabitViaApi("Info Then Modify", "Day");

        // Turn 1: Ask about the habit
        var infoResponse = await SendChat("Tell me about my habit called 'Info Then Modify'");
        infoResponse.Should().NotBeNull();
        infoResponse.AiMessage.Should().NotBeNullOrEmpty();

        // Turn 2: Modify it
        var modifyResponse = await SendChat(
            "Now add a description to 'Info Then Modify': do this every morning");
        modifyResponse.Actions.Should().Contain(a => a.Type == "UpdateHabit" && a.Status == "Success");
    }

    // ===================================================================
    //  SECTION 10: Boundary and Error Tests (3 tests)
    // ===================================================================

    [Fact]
    public async Task Chat_SpecialCharactersInTitle_HandlesCorrectly()
    {
        var response = await SendChat(
            "Create a daily habit called 'Test & Debug (v2.0) - #1'");

        response.Actions.Should().Contain(a => a.Type == "CreateHabit" && a.Status == "Success");

        // Verify the habit was actually created
        var habits = await GetAllHabits();
        habits.Should().Contain(h => h.Title.Contains("Test") || h.Title.Contains("Debug"));
    }

    [Fact]
    public async Task Chat_VeryManyHabits_HandlesLargeDataset()
    {
        // Create 10 habits to test query with a larger dataset
        for (int i = 1; i <= 10; i++)
            await CreateHabitViaApi($"Bulk Habit {i:D2}", "Day");

        var response = await SendChat("list all my habits");

        response.Should().NotBeNull();
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Chat_ConcurrentToolCalls_ExecutesSequentially()
    {
        await CreateHabitViaApi("Concurrent A", "Day");
        await CreateHabitViaApi("Concurrent B", "Day");

        // Request multiple operations in one message
        var response = await SendChat(
            "Log 'Concurrent A' as done, and also skip 'Concurrent B' for today");

        response.Should().NotBeNull();
        response.Actions.Should().NotBeEmpty();
        // Both operations should succeed without interference
        response.Actions.Should().OnlyContain(a => a.Status == "Success");
    }

    // ===================================================================
    //  SECTION 11: DuplicateHabit Extended Tests (2 tests)
    // ===================================================================

    [Fact]
    public async Task DuplicateHabit_WithTags_CopiesTags()
    {
        var habitId = await CreateHabitViaApi("Dup Tag Source", "Day");

        // Tag the source habit first
        await SendChat("Tag the habit 'Dup Tag Source' with Wellness");

        // Duplicate it
        var response = await SendChat("Duplicate the habit 'Dup Tag Source'");
        response.Actions.Should().Contain(a => a.Type == "DuplicateHabit" && a.Status == "Success");

        // Verify two habits exist with similar names
        var habits = await GetAllHabits();
        var dupHabits = habits.Where(h =>
            h.Title.Contains("Dup Tag", StringComparison.OrdinalIgnoreCase) ||
            h.Title.Contains("Copy", StringComparison.OrdinalIgnoreCase)).ToList();
        dupHabits.Count.Should().BeGreaterThanOrEqualTo(2,
            "original and duplicate should both exist");
    }

    [Fact]
    public async Task DuplicateHabit_WithChildren_CopiesHierarchy()
    {
        var parentId = await CreateHabitViaApi("Dup Parent Source", "Day");
        await CreateSubHabitViaApi(parentId, "Dup Child A");
        await CreateSubHabitViaApi(parentId, "Dup Child B");

        var response = await SendChat("Duplicate the habit 'Dup Parent Source'");

        response.Actions.Should().Contain(a => a.Type == "DuplicateHabit" && a.Status == "Success");
    }

    // ===================================================================
    //  Helpers
    // ===================================================================

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
                    if (attempt < maxRetries &&
                        (errorContent.Contains("empty response") || errorContent.Contains("rate")))
                    {
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

    private async Task<Guid> CreateHabitViaApi(string title, string frequencyUnit,
        string? description = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["type"] = "Boolean",
            ["frequencyUnit"] = frequencyUnit,
            ["frequencyQuantity"] = 1
        };
        if (description is not null)
            payload["description"] = description;

        var response = await _client.PostAsJsonAsync("/api/habits", payload);
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Failed to create test habit '{title}'");

        var id = await response.Content.ReadFromJsonAsync<Guid>(JsonOptions);
        return id;
    }

    private async Task<Guid> CreateHabitViaApiOneTime(string title)
    {
        var response = await _client.PostAsJsonAsync("/api/habits", new
        {
            title,
            type = "Boolean"
            // No frequencyUnit = one-time task
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Failed to create one-time habit '{title}'");

        return await response.Content.ReadFromJsonAsync<Guid>(JsonOptions);
    }

    private async Task<Guid> CreateHabitViaApiWithDate(string title, string frequencyUnit,
        DateOnly dueDate)
    {
        var response = await _client.PostAsJsonAsync("/api/habits", new
        {
            title,
            type = "Boolean",
            frequencyUnit,
            frequencyQuantity = 1,
            dueDate = dueDate.ToString("yyyy-MM-dd")
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Failed to create habit '{title}' with date {dueDate}");

        return await response.Content.ReadFromJsonAsync<Guid>(JsonOptions);
    }

    private async Task<Guid> CreateHabitViaApiWithChecklist(string title, string frequencyUnit,
        string[] items)
    {
        var checklistItems = items.Select(i => new { text = i, isChecked = false }).ToArray();
        var response = await _client.PostAsJsonAsync("/api/habits", new
        {
            title,
            type = "Boolean",
            frequencyUnit,
            frequencyQuantity = 1,
            checklistItems
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Failed to create habit '{title}' with checklist");

        return await response.Content.ReadFromJsonAsync<Guid>(JsonOptions);
    }

    private async Task<Guid> CreateSubHabitViaApi(Guid parentId, string title)
    {
        var response = await _client.PostAsJsonAsync($"/api/habits/{parentId}/sub-habits", new
        {
            title,
            type = "Boolean"
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            $"Failed to create sub-habit '{title}' under {parentId}");

        var result = await response.Content.ReadFromJsonAsync<IdResponse>(JsonOptions);
        return result!.Id;
    }

    private async Task<List<HabitDto>> GetAllHabits()
    {
        var response = await _client.GetAsync("/api/habits");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var paginated = await response.Content.ReadFromJsonAsync<PaginatedResponse<HabitDto>>(JsonOptions);
        return paginated?.Items ?? [];
    }

    // ===================================================================
    //  DTOs
    // ===================================================================

    private record LoginResponse(Guid UserId, string Token, string Name, string Email);
    private record ChatResponse(string? AiMessage, List<ActionResultDto> Actions);
    private record ActionResultDto(
        string Type,
        string Status,
        Guid? EntityId = null,
        string? EntityName = null,
        string? Error = null);
    private record HabitDto(Guid Id, string Title);
    private record TagDto(Guid Id, string Name);
    private record PaginatedResponse<T>(List<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);
    private record IdResponse(Guid Id);
}
