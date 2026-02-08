using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Extensions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Orbit.IntegrationTests;

/// <summary>
/// Core integration tests for the AI Chat endpoint (12 essential scenarios).
/// Tests are fully repeatable - they create a test user, run tests, and clean up everything.
/// Habits-only: no task creation or task completion tests.
/// </summary>
[Collection("Sequential")]
public class AiChatIntegrationTests : IAsyncLifetime
{
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _factory;
    private string? _testUserId;
    private string? _authToken;
    private readonly string _testUserEmail = $"test-{Guid.NewGuid()}@integration.test";
    private const string TestUserPassword = "TestPassword123!";

    // Rate limiting: Gemini free tier allows ~15 RPM
    private static readonly SemaphoreSlim RateLimitSemaphore = new(1, 1);
    private static DateTime LastApiCall = DateTime.MinValue;

    public AiChatIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        // Register test user
        var registerResponse = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            name = "AI Test User",
            email = _testUserEmail,
            password = TestUserPassword
        });

        registerResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var registerResult = await registerResponse.Content.ReadFromJsonAsync<RegisterResponse>();
        _testUserId = registerResult!.UserId;

        // Login to get auth token
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            email = _testUserEmail,
            password = TestUserPassword
        });

        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
        _authToken = loginResult!.Token;

        _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_authToken}");
    }

    public async Task DisposeAsync()
    {
        // Clean up all data created by this test user
        if (!string.IsNullOrEmpty(_testUserId))
        {
            try
            {
                // Delete all habits (will cascade delete habit logs)
                var habitsResponse = await _client.GetAsync("/api/habits");
                if (habitsResponse.IsSuccessStatusCode)
                {
                    var habits = await habitsResponse.Content.ReadFromJsonAsync<List<HabitDto>>();
                    foreach (var habit in habits ?? [])
                    {
                        await _client.DeleteAsync($"/api/habits/{habit.Id}");
                    }
                }

                // Delete the test user
                await _client.DeleteAsync($"/api/users/{_testUserId}");
            }
            catch
            {
                // Cleanup failed - log but don't throw to avoid masking test failures
            }
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
        response.ExecutedActions.Should().ContainSingle()
            .Which.Should().StartWith("CreateHabit:");
        response.AiMessage.Should().NotBeNullOrEmpty();
        response.AiMessage.Should().Match(s => s.ToLower().Contains("meditat"));
    }

    [Fact]
    public async Task Chat_CreateQuantifiableHabit_Running_ShouldSucceed()
    {
        // Act
        var response = await SendChatMessage("i ran 5km today");

        // Assert
        response.ExecutedActions.Should().ContainSingle()
            .Which.Should().StartWith("CreateHabit:");
        response.AiMessage.Should().NotBeNullOrEmpty();
        response.AiMessage.Should().Match(s => s.ToLower().Contains("run"));
    }

    [Fact]
    public async Task Chat_CreateQuantifiableHabit_Water_ShouldSucceed()
    {
        // Act
        var response = await SendChatMessage("i want to drink 8 glasses of water daily");

        // Assert
        response.ExecutedActions.Should().ContainSingle()
            .Which.Should().StartWith("CreateHabit:");
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
        response.ExecutedActions.Should().ContainSingle()
            .Which.Should().StartWith("LogHabit:");
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
        response.ExecutedActions.Should().ContainSingle()
            .Which.Should().StartWith("LogHabit:");
        response.AiMessage.Should().NotBeNullOrEmpty();
        response.AiMessage.Should().Match(s => s.Contains("3"));
    }

    #endregion

    #region Out-of-Scope Tests (2)

    [Fact]
    public async Task Chat_GeneralQuestion_ShouldReject()
    {
        // Act
        var response = await SendChatMessage("what's the capital of france?");

        // Assert
        response.ExecutedActions.Should().BeEmpty();
        response.AiMessage.Should().NotBeNullOrEmpty();
        response.AiMessage.ToLower().Should().MatchRegex("(habit|can't|cannot|only)");
    }

    [Fact]
    public async Task Chat_HomeworkHelp_ShouldReject()
    {
        // Act
        var response = await SendChatMessage("help me solve this math problem: 2x + 5 = 15");

        // Assert
        response.ExecutedActions.Should().BeEmpty();
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

        // Assert - should return no actions and a redirect message
        response.ExecutedActions.Should().BeEmpty();
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Edge Cases (2)

    [Fact]
    public async Task Chat_EmptyMessage_ShouldHandleGracefully()
    {
        // Act
        var httpResponse = await _client.PostAsJsonAsync("/api/chat", new { message = "" });

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
        response.ExecutedActions.Should().HaveCount(2);
        response.ExecutedActions.Should().Contain(a => a.StartsWith("CreateHabit:"));
        response.ExecutedActions.Should().Contain(a => a.StartsWith("LogHabit:"));
    }

    [Fact]
    public async Task Chat_MixedHabitActionsInOneMessage_ShouldHandleAll()
    {
        // Act - multiple habit-related actions (no tasks)
        var response = await SendChatMessage("i want to start meditating daily and i ran 5km today");

        // Assert
        response.ExecutedActions.Should().HaveCountGreaterThan(1);
        response.AiMessage.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Helper Methods

    private async Task<ChatResponse> SendChatMessage(string message)
    {
        // Rate limiting: Wait 10 seconds between API calls to respect Gemini's rate limits
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

            var httpResponse = await _client.PostAsJsonAsync("/api/chat", new { message });
            LastApiCall = DateTime.UtcNow;

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorContent = await httpResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"API ERROR ({httpResponse.StatusCode}): {errorContent}");
                Console.WriteLine($"   For message: '{message}'");
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
        finally
        {
            RateLimitSemaphore.Release();
        }
    }

    #endregion

    #region DTOs

    private record RegisterResponse(string UserId, string Message);
    private record LoginResponse(string UserId, string Token, string Name, string Email);
    private record ChatResponse(List<string> ExecutedActions, string? AiMessage);
    private record HabitDto(Guid Id, string Title);

    #endregion
}
