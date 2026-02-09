using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Orbit.IntegrationTests;

[Collection("Sequential")]
public class UserFactsControllerTests : IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly string _email = $"userfacts-test-{Guid.NewGuid()}@integration.test";
    private const string Password = "TestPassword123!";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Rate limiting: Gemini free tier allows ~15 RPM
    private static readonly SemaphoreSlim RateLimitSemaphore = new(1, 1);
    private static DateTime LastApiCall = DateTime.MinValue;

    public UserFactsControllerTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            name = "UserFacts Test User",
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
            var factsResponse = await _client.GetAsync("/api/user-facts");
            if (factsResponse.IsSuccessStatusCode)
            {
                var facts = await factsResponse.Content.ReadFromJsonAsync<List<UserFactDto>>(JsonOptions);
                foreach (var fact in facts ?? [])
                    await _client.DeleteAsync($"/api/user-facts/{fact.Id}");
            }

            // Also clean up habits created during tests
            var habitsResponse = await _client.GetAsync("/api/habits");
            if (habitsResponse.IsSuccessStatusCode)
            {
                var habits = await habitsResponse.Content.ReadFromJsonAsync<List<HabitDto>>(JsonOptions);
                foreach (var habit in habits ?? [])
                    await _client.DeleteAsync($"/api/habits/{habit.Id}");
            }
        }
        catch { }

        _client.Dispose();
    }

    [Fact]
    public async Task GetUserFacts_WhenNoFacts_ReturnsEmptyArray()
    {
        // Act
        var response = await _client.GetAsync("/api/user-facts");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var facts = await response.Content.ReadFromJsonAsync<List<UserFactDto>>(JsonOptions);
        facts.Should().NotBeNull();
        facts.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUserFacts_AfterChat_ReturnsFacts()
    {
        // Act - Send a chat message rich with personal context
        var chatResponse = await SendChatMessage(
            "I'm a morning person and I work as a software developer. " +
            "I prefer exercising in the morning before work. " +
            "Create a daily coding habit for me.");

        chatResponse.Should().NotBeNull();
        chatResponse!.AiMessage.Should().NotBeNullOrEmpty();

        // Give a moment for fact extraction to complete (it's synchronous but happens after response)
        await Task.Delay(1000);

        // Assert - Facts should be extracted
        var factsResponse = await _client.GetAsync("/api/user-facts");
        factsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var facts = await factsResponse.Content.ReadFromJsonAsync<List<UserFactDto>>(JsonOptions);
        facts.Should().NotBeNull();
        facts.Should().NotBeEmpty("fact extraction should have captured personal info from the message");

        // Verify fact structure
        var firstFact = facts!.First();
        firstFact.Id.Should().NotBeEmpty();
        firstFact.FactText.Should().NotBeNullOrEmpty();
        firstFact.ExtractedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task DeleteUserFact_ExistingFact_ReturnsNoContent()
    {
        // Arrange - Create a fact via chat
        await SendChatMessage(
            "I'm a night owl and I love working late at night. " +
            "I prefer coffee over tea. " +
            "Create a daily journaling habit.");

        await Task.Delay(1000); // Wait for fact extraction

        var factsResponse = await _client.GetAsync("/api/user-facts");
        var facts = await factsResponse.Content.ReadFromJsonAsync<List<UserFactDto>>(JsonOptions);
        facts.Should().NotBeEmpty();

        var factToDelete = facts!.First();

        // Act - Delete the fact
        var deleteResponse = await _client.DeleteAsync($"/api/user-facts/{factToDelete.Id}");

        // Assert
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify the fact is no longer in the list
        var afterDeleteResponse = await _client.GetAsync("/api/user-facts");
        var afterDeleteFacts = await afterDeleteResponse.Content.ReadFromJsonAsync<List<UserFactDto>>(JsonOptions);
        afterDeleteFacts.Should().NotContain(f => f.Id == factToDelete.Id);
    }

    [Fact]
    public async Task DeleteUserFact_NonExistentId_ReturnsNotFound()
    {
        // Act
        var randomId = Guid.NewGuid();
        var response = await _client.DeleteAsync($"/api/user-facts/{randomId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetUserFacts_Unauthorized_Returns401()
    {
        // Arrange - Create anonymous client without auth token
        using var anonClient = _factory.CreateClient();

        // Act
        var response = await anonClient.GetAsync("/api/user-facts");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    #region Helper Methods

    private async Task<ChatResponse?> SendChatMessage(string message)
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
                throw new Exception($"Chat API call failed with status {httpResponse.StatusCode}: {errorContent}");
            }

            return await httpResponse.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions);
        }
        finally
        {
            RateLimitSemaphore.Release();
        }
    }

    #endregion

    #region DTOs

    private record LoginResponse(string UserId, string Token, string Name, string Email);
    private record UserFactDto(Guid Id, string FactText, string? Category, DateTime ExtractedAtUtc, DateTime? UpdatedAtUtc);
    private record ChatResponse(string? AiMessage, object[]? Actions);
    private record HabitDto(Guid Id, string Title);

    #endregion
}
