using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Orbit.IntegrationTests;

[Collection("Sequential")]
public class UserFactsControllerTests : IAsyncLifetime
{
    private readonly IntegrationTestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _email = $"userfacts-test-{Guid.NewGuid()}@integration.test";
    private const string TestCode = "999999";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly SemaphoreSlim RateLimitSemaphore = new(1, 1);
    private static DateTime LastApiCall = DateTime.MinValue;

    public UserFactsControllerTests(IntegrationTestWebApplicationFactory factory)
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
            var factsResponse = await _client.GetAsync("/api/user-facts");
            if (factsResponse.IsSuccessStatusCode)
            {
                var facts = await factsResponse.Content.ReadFromJsonAsync<List<UserFactDto>>(JsonOptions);
                foreach (var fact in facts ?? [])
                    await _client.DeleteAsync($"/api/user-facts/{fact.Id}");
            }

            var habitsResponse = await _client.GetAsync(IntegrationTestHelpers.BuildHabitSchedulePath());
            if (habitsResponse.IsSuccessStatusCode)
            {
                var habits = await habitsResponse.Content.ReadFromJsonAsync<PaginatedResponse<HabitDto>>(JsonOptions);
                foreach (var habit in habits?.Items?.DistinctBy(h => h.Id) ?? [])
                    await _client.DeleteAsync($"/api/habits/{habit.Id}");
            }
        }
        catch { }

        _client.Dispose();
    }

    [Fact]
    public async Task GetUserFacts_WhenNoFacts_ReturnsEmptyArray()
    {
        var response = await _client.GetAsync("/api/user-facts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var facts = await response.Content.ReadFromJsonAsync<List<UserFactDto>>(JsonOptions);
        facts.Should().NotBeNull();
        facts.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUserFacts_AfterChat_ReturnsFacts()
    {
        var chatResponse = await SendChatMessage(
            "I'm a morning person and I work as a software developer. " +
            "I prefer exercising in the morning before work. " +
            "Create a daily coding habit for me.");

        chatResponse.Should().NotBeNull();
        chatResponse!.AiMessage.Should().NotBeNullOrEmpty();

        var facts = await WaitForFactsAsync(minimumCount: 1);
        facts.Should().NotBeNull();
        facts.Should().NotBeEmpty("fact extraction should have captured personal info from the message");

        var firstFact = facts!.First();
        firstFact.Id.Should().NotBeEmpty();
        firstFact.FactText.Should().NotBeNullOrEmpty();
        firstFact.ExtractedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task DeleteUserFact_ExistingFact_ReturnsNoContent()
    {
        await SendChatMessage(
            "I'm a night owl and I love working late at night. " +
            "I prefer coffee over tea. " +
            "Create a daily journaling habit.");

        var facts = await WaitForFactsAsync(minimumCount: 1);
        facts.Should().NotBeEmpty();

        var factToDelete = facts!.First();

        var deleteResponse = await _client.DeleteAsync($"/api/user-facts/{factToDelete.Id}");

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterDeleteResponse = await _client.GetAsync("/api/user-facts");
        var afterDeleteFacts = await afterDeleteResponse.Content.ReadFromJsonAsync<List<UserFactDto>>(JsonOptions);
        afterDeleteFacts.Should().NotContain(f => f.Id == factToDelete.Id);
    }

    [Fact]
    public async Task DeleteUserFact_NonExistentId_ReturnsNotFound()
    {
        var randomId = Guid.NewGuid();
        var response = await _client.DeleteAsync($"/api/user-facts/{randomId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetUserFacts_Unauthorized_Returns401()
    {
        using var anonClient = _factory.CreateClient();

        var response = await anonClient.GetAsync("/api/user-facts");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    #region Helper Methods

    private async Task<ChatResponse?> SendChatMessage(string message)
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
                throw new Exception($"Chat API call failed with status {httpResponse.StatusCode}: {errorContent}");
            }

            return await httpResponse.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions);
        }
        finally
        {
            RateLimitSemaphore.Release();
        }
    }

    private async Task<List<UserFactDto>> WaitForFactsAsync(int minimumCount, int maxAttempts = 12)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var factsResponse = await _client.GetAsync("/api/user-facts");
            factsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var facts = await factsResponse.Content.ReadFromJsonAsync<List<UserFactDto>>(JsonOptions) ?? [];
            if (facts.Count >= minimumCount)
                return facts;

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return await _client.GetFromJsonAsync<List<UserFactDto>>("/api/user-facts", JsonOptions) ?? [];
    }

    #endregion

    #region DTOs

    private record PaginatedResponse<T>(List<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);
    private record UserFactDto(Guid Id, string FactText, string? Category, DateTime ExtractedAtUtc, DateTime? UpdatedAtUtc);
    private record ChatResponse(string? AiMessage, object[]? Actions);
    private record HabitDto(Guid Id, string Title);

    #endregion
}
