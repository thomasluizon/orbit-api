using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Orbit.IntegrationTests;

[Collection("Sequential")]
public class HabitsControllerTests : IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly string _email = $"habits-test-{Guid.NewGuid()}@integration.test";
    private const string Password = "TestPassword123!";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public HabitsControllerTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            name = "Habits Test User",
            email = _email,
            password = Password
        });

        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            email = _email,
            password = Password
        });

        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions);
        _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {login!.Token}");
    }

    public async Task DisposeAsync()
    {
        try
        {
            var habitsResponse = await _client.GetAsync("/api/habits");
            if (habitsResponse.IsSuccessStatusCode)
            {
                var habits = await habitsResponse.Content.ReadFromJsonAsync<List<HabitDto>>(JsonOptions);
                foreach (var h in habits ?? [])
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
        var response = await _client.GetAsync("/api/habits");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var habits = await response.Content.ReadFromJsonAsync<List<HabitDto>>(JsonOptions);
        habits.Should().NotBeNull();
        habits.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHabits_NoToken_ReturnsUnauthorized()
    {
        using var anonClient = _factory.CreateClient();
        var response = await anonClient.GetAsync("/api/habits");

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

        var habitId = await response.Content.ReadFromJsonAsync<Guid>();
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

    // ── GetTrends ─────────────────────────────────────────────

    [Fact]
    public async Task GetTrends_QuantifiableHabit_ReturnsOk()
    {
        var habitId = await CreateQuantifiableHabit("Running", "km");

        var response = await _client.GetAsync($"/api/habits/{habitId}/trends");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var trends = await response.Content.ReadFromJsonAsync<TrendDto>(JsonOptions);
        trends.Should().NotBeNull();
        trends!.Weekly.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTrends_NonExistentHabit_ReturnsBadRequest()
    {
        var response = await _client.GetAsync($"/api/habits/{Guid.NewGuid()}/trends");

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

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<BulkCreateResultDto>(JsonOptions);
        result.Should().NotBeNull();
        result!.Results.Should().HaveCount(2);
        result.Results.Should().AllSatisfy(r => r.Status.Should().Be("Success"));
        result.Results.Should().AllSatisfy(r => r.HabitId.Should().NotBeEmpty());

        // Verify parent B has children
        var habitsResponse = await _client.GetAsync("/api/habits");
        var habits = await habitsResponse.Content.ReadFromJsonAsync<List<HabitWithChildrenDto>>(JsonOptions);
        var parentB = habits!.FirstOrDefault(h => h.Title == "Parent Habit B");
        parentB.Should().NotBeNull();
        parentB!.Children.Should().HaveCount(2);
    }

    [Fact]
    public async Task BulkCreate_PartialFailure_KeepsSuccesses()
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

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<BulkCreateResultDto>(JsonOptions);
        result.Should().NotBeNull();
        result!.Results.Should().HaveCount(3);

        result.Results[0].Status.Should().Be("Success");
        result.Results[0].HabitId.Should().NotBeEmpty();

        result.Results[1].Status.Should().Be("Failed");
        result.Results[1].Error.Should().NotBeNullOrEmpty();

        result.Results[2].Status.Should().Be("Success");
        result.Results[2].HabitId.Should().NotBeEmpty();

        // Verify 2 habits created
        var habitsResponse = await _client.GetAsync("/api/habits");
        var habits = await habitsResponse.Content.ReadFromJsonAsync<List<HabitDto>>(JsonOptions);
        habits.Should().HaveCountGreaterThanOrEqualTo(2);
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
        var habitsResponse = await _client.GetAsync("/api/habits");
        var habits = await habitsResponse.Content.ReadFromJsonAsync<List<HabitDto>>(JsonOptions);
        habits!.Should().NotContain(h => h.Id == id1 || h.Id == id2 || h.Id == id3);
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
        var habitsResponse = await _client.GetAsync("/api/habits");
        var habits = await habitsResponse.Content.ReadFromJsonAsync<List<HabitDto>>(JsonOptions);
        habits!.Should().NotContain(h => h.Id == id1 || h.Id == id2);
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
        return await response.Content.ReadFromJsonAsync<Guid>();
    }

    private async Task<Guid> CreateQuantifiableHabit(string title, string unit)
    {
        var response = await _client.PostAsJsonAsync("/api/habits", new
        {
            title,
            type = "Quantifiable",
            unit,
            frequencyUnit = "Day",
            frequencyQuantity = 1
        });

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Guid>();
    }

    // ── DTOs ──────────────────────────────────────────────────

    private record LoginResponse(string UserId, string Token, string Name, string Email);
    private record HabitDto(Guid Id, string Title);
    private record HabitWithChildrenDto(Guid Id, string Title, List<HabitDto> Children);
    private record LogResponse(Guid LogId);
    private record MetricsDto(int CurrentStreak, int LongestStreak, decimal WeeklyCompletionRate,
        decimal MonthlyCompletionRate, int TotalCompletions, string? LastCompletedDate);
    private record TrendDto(List<TrendPointDto> Weekly, List<TrendPointDto> Monthly);
    private record TrendPointDto(string Period, decimal Average, decimal Minimum, decimal Maximum, int Count);
    private record BulkCreateResultDto(List<BulkCreateItemResultDto> Results);
    private record BulkCreateItemResultDto(int Index, string Status, Guid? HabitId, string? Title, string? Error, string? Field);
    private record BulkDeleteResultDto(List<BulkDeleteItemResultDto> Results);
    private record BulkDeleteItemResultDto(int Index, string Status, Guid HabitId, string? Error);
}
