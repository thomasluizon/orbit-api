using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Orbit.IntegrationTests;

[Collection("Sequential")]
public class HabitsControllerTests : IAsyncLifetime
{
    private readonly IntegrationTestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _email = $"habits-test-{Guid.NewGuid()}@integration.test";
    private const string TestCode = "999999";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public HabitsControllerTests(IntegrationTestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        IntegrationTestHelpers.RegisterTestAccount(_email, TestCode);
    }

    public async Task InitializeAsync()
    {
        await IntegrationTestHelpers.AuthenticateWithCodeAsync(_client, _email, TestCode, JsonOptions);
    }

    public async Task DisposeAsync()
    {
        try
        {
            var habitsResponse = await _client.GetAsync(IntegrationTestHelpers.BuildHabitSchedulePath());
            if (habitsResponse.IsSuccessStatusCode)
            {
                var paginated = await habitsResponse.Content.ReadFromJsonAsync<PaginatedResponse<HabitDto>>(JsonOptions);
                foreach (var h in paginated?.Items?.DistinctBy(h => h.Id) ?? [])
                    await _client.DeleteAsync($"/api/habits/{h.Id}");
            }
        }
        catch { }

        _client.Dispose();
    }

    // ── GetHabits ─────────────────────────────────────────────

    [Fact]
    public async Task GetHabits_Authenticated_ReturnsOk()
    {
        var response = await _client.GetAsync(IntegrationTestHelpers.BuildHabitSchedulePath());

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var paginated = await response.Content.ReadFromJsonAsync<PaginatedResponse<HabitDto>>(JsonOptions);
        paginated.Should().NotBeNull();
        paginated!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHabits_NoToken_ReturnsUnauthorized()
    {
        using var anonClient = _factory.CreateClient();
        var response = await anonClient.GetAsync(IntegrationTestHelpers.BuildHabitSchedulePath());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── CreateHabit ───────────────────────────────────────────

    [Fact]
    public async Task CreateHabit_ValidBoolean_ReturnsCreated()
    {
        var response = await _client.PostAsJsonAsync("/api/habits", new
        {
            title = "Meditate",
            type = "Boolean",
            frequencyUnit = "Day",
            frequencyQuantity = 1
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var habitId = await IntegrationTestHelpers.ReadCreatedIdAsync(response, JsonOptions);
        habitId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CreateHabit_MissingTitle_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/habits", new
        {
            title = "",
            type = "Boolean",
            frequencyUnit = "Day",
            frequencyQuantity = 1
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── LogHabit ──────────────────────────────────────────────

    [Fact]
    public async Task LogHabit_BooleanHabit_ReturnsOkWithLogId()
    {
        var habitId = await CreateBooleanHabit("Log test habit");

        var response = await _client.PostAsJsonAsync($"/api/habits/{habitId}/log", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<LogResponse>(JsonOptions);
        body!.LogId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task LogHabit_NonExistentHabit_ReturnsBadRequest()
    {
        var fakeId = Guid.NewGuid();

        var response = await _client.PostAsJsonAsync($"/api/habits/{fakeId}/log", new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── DeleteHabit ───────────────────────────────────────────

    [Fact]
    public async Task DeleteHabit_ExistingHabit_ReturnsNoContent()
    {
        var habitId = await CreateBooleanHabit("Delete me");

        var response = await _client.DeleteAsync($"/api/habits/{habitId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteHabit_NonExistentHabit_ReturnsBadRequest()
    {
        var response = await _client.DeleteAsync($"/api/habits/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── GetMetrics ────────────────────────────────────────────

    [Fact]
    public async Task GetMetrics_ExistingHabit_ReturnsOk()
    {
        var habitId = await CreateBooleanHabit("Metrics habit");

        var response = await _client.GetAsync($"/api/habits/{habitId}/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var metrics = await response.Content.ReadFromJsonAsync<MetricsDto>(JsonOptions);
        metrics.Should().NotBeNull();
        metrics!.TotalCompletions.Should().Be(0);
    }

    [Fact]
    public async Task GetMetrics_NonExistentHabit_ReturnsBadRequest()
    {
        var response = await _client.GetAsync($"/api/habits/{Guid.NewGuid()}/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── BulkCreate ────────────────────────────────────────────

    [Fact]
    public async Task BulkCreate_WithParentChild_ReturnsSuccess()
    {
        var response = await _client.PostAsJsonAsync("/api/habits/bulk", new
        {
            habits = new object[]
            {
                new
                {
                    title = "Parent Habit A",
                    frequencyUnit = "Day",
                    frequencyQuantity = 1
                },
                new
                {
                    title = "Parent Habit B",
                    frequencyUnit = "Day",
                    frequencyQuantity = 1,
                    subHabits = new[]
                    {
                        new { title = "Child B1", frequencyUnit = "Day", frequencyQuantity = 1 },
                        new { title = "Child B2", frequencyUnit = "Day", frequencyQuantity = 1 }
                    }
                }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = await response.Content.ReadFromJsonAsync<BulkCreateResultDto>(JsonOptions);
        result.Should().NotBeNull();
        result!.Results.Should().HaveCount(2);
        result.Results.Should().AllSatisfy(r => r.Status.Should().Be("Success"));
        result.Results.Should().AllSatisfy(r => r.HabitId.Should().NotBeEmpty());

        // Verify parent B has children
        var habitsResponse = await _client.GetAsync(IntegrationTestHelpers.BuildHabitSchedulePath());
        var paginatedHabits = await habitsResponse.Content.ReadFromJsonAsync<PaginatedResponse<HabitWithChildrenDto>>(JsonOptions);
        var parentB = paginatedHabits!.Items.FirstOrDefault(h => h.Title == "Parent Habit B");
        parentB.Should().NotBeNull();
        parentB!.Children.Should().HaveCount(2);
    }

    [Fact]
    public async Task BulkCreate_WithInvalidItem_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/habits/bulk", new
        {
            habits = new[]
            {
                new { title = "Valid Habit 1", frequencyUnit = "Day", frequencyQuantity = 1 },
                new { title = "", frequencyUnit = "Day", frequencyQuantity = 1 }, // Invalid: empty title
                new { title = "Valid Habit 2", frequencyUnit = "Day", frequencyQuantity = 1 }
            }
        });

        // Validation rejects the entire request when any item has an empty title
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BulkCreate_EmptyArray_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/habits/bulk", new
        {
            habits = Array.Empty<object>()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── BulkDelete ────────────────────────────────────────────

    [Fact]
    public async Task BulkDelete_MultipleHabits_ReturnsSuccess()
    {
        var id1 = await CreateBooleanHabit("Delete Bulk 1");
        var id2 = await CreateBooleanHabit("Delete Bulk 2");
        var id3 = await CreateBooleanHabit("Delete Bulk 3");

        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/habits/bulk")
        {
            Content = JsonContent.Create(new { habitIds = new[] { id1, id2, id3 } })
        };

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<BulkDeleteResultDto>(JsonOptions);
        result.Should().NotBeNull();
        result!.Results.Should().HaveCount(3);
        result.Results.Should().AllSatisfy(r => r.Status.Should().Be("Success"));

        // Verify all deleted
        var habitsResponse = await _client.GetAsync(IntegrationTestHelpers.BuildHabitSchedulePath());
        var paginatedHabits = await habitsResponse.Content.ReadFromJsonAsync<PaginatedResponse<HabitDto>>(JsonOptions);
        paginatedHabits!.Items.Should().NotContain(h => h.Id == id1 || h.Id == id2 || h.Id == id3);
    }

    [Fact]
    public async Task BulkDelete_PartialFailure_DeletesValidOnes()
    {
        var id1 = await CreateBooleanHabit("Delete Valid 1");
        var id2 = await CreateBooleanHabit("Delete Valid 2");
        var fakeId = Guid.NewGuid();

        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/habits/bulk")
        {
            Content = JsonContent.Create(new { habitIds = new[] { id1, fakeId, id2 } })
        };

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<BulkDeleteResultDto>(JsonOptions);
        result.Should().NotBeNull();
        result!.Results.Should().HaveCount(3);

        result.Results[0].Status.Should().Be("Success");
        result.Results[1].Status.Should().Be("Failed");
        result.Results[1].Error.Should().Contain("not found");
        result.Results[2].Status.Should().Be("Success");

        // Verify valid ones deleted
        var habitsResponse = await _client.GetAsync(IntegrationTestHelpers.BuildHabitSchedulePath());
        var paginatedHabits = await habitsResponse.Content.ReadFromJsonAsync<PaginatedResponse<HabitDto>>(JsonOptions);
        paginatedHabits!.Items.Should().NotContain(h => h.Id == id1 || h.Id == id2);
    }

    [Fact]
    public async Task BulkDelete_EmptyArray_ReturnsBadRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/habits/bulk")
        {
            Content = JsonContent.Create(new { habitIds = Array.Empty<Guid>() })
        };

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Helpers ───────────────────────────────────────────────

    private async Task<Guid> CreateBooleanHabit(string title)
    {
        var response = await _client.PostAsJsonAsync("/api/habits", new
        {
            title,
            type = "Boolean",
            frequencyUnit = "Day",
            frequencyQuantity = 1
        });

        response.EnsureSuccessStatusCode();
        return await IntegrationTestHelpers.ReadCreatedIdAsync(response, JsonOptions);
    }

    // ── DTOs ──────────────────────────────────────────────────

    private record HabitDto(Guid Id, string Title);
    private record PaginatedResponse<T>(List<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);
    private record HabitWithChildrenDto(Guid Id, string Title, List<HabitDto> Children);
    private record LogResponse(Guid LogId);
    private record MetricsDto(int CurrentStreak, int LongestStreak, decimal WeeklyCompletionRate,
        decimal MonthlyCompletionRate, int TotalCompletions, string? LastCompletedDate);
    private record BulkCreateResultDto(List<BulkCreateItemResultDto> Results);
    private record BulkCreateItemResultDto(int Index, string Status, Guid? HabitId, string? Title, string? Error, string? Field);
    private record BulkDeleteResultDto(List<BulkDeleteItemResultDto> Results);
    private record BulkDeleteItemResultDto(int Index, string Status, Guid HabitId, string? Error);
}
