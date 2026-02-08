using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Orbit.IntegrationTests;

[Collection("Sequential")]
public class TagsControllerTests : IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly string _email = $"tags-test-{Guid.NewGuid()}@integration.test";
    private const string Password = "TestPassword123!";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public TagsControllerTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            name = "Tags Test User",
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
            // Delete habits first (they may reference tags)
            var habitsResponse = await _client.GetAsync("/api/habits");
            if (habitsResponse.IsSuccessStatusCode)
            {
                var habits = await habitsResponse.Content.ReadFromJsonAsync<List<HabitDto>>(JsonOptions);
                foreach (var h in habits ?? [])
                    await _client.DeleteAsync($"/api/habits/{h.Id}");
            }

            // Then delete tags
            var tagsResponse = await _client.GetAsync("/api/tags");
            if (tagsResponse.IsSuccessStatusCode)
            {
                var tags = await tagsResponse.Content.ReadFromJsonAsync<List<TagDto>>(JsonOptions);
                foreach (var t in tags ?? [])
                    await _client.DeleteAsync($"/api/tags/{t.Id}");
            }
        }
        catch { }

        _client.Dispose();
    }

    // ── GetTags ───────────────────────────────────────────────

    [Fact]
    public async Task GetTags_Authenticated_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/tags");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var tags = await response.Content.ReadFromJsonAsync<List<TagDto>>(JsonOptions);
        tags.Should().NotBeNull();
        tags.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTags_NoToken_ReturnsUnauthorized()
    {
        using var anonClient = _factory.CreateClient();
        var response = await anonClient.GetAsync("/api/tags");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── CreateTag ─────────────────────────────────────────────

    [Fact]
    public async Task CreateTag_ValidData_ReturnsCreated()
    {
        var response = await _client.PostAsJsonAsync("/api/tags", new
        {
            name = "Fitness",
            color = "#FF5733"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var tagId = await response.Content.ReadFromJsonAsync<Guid>();
        tagId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CreateTag_InvalidColor_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/tags", new
        {
            name = "Bad Color",
            color = "not-a-color"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── DeleteTag ─────────────────────────────────────────────

    [Fact]
    public async Task DeleteTag_ExistingTag_ReturnsNoContent()
    {
        var tagId = await CreateTag("ToDelete", "#AABBCC");

        var response = await _client.DeleteAsync($"/api/tags/{tagId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteTag_NonExistentTag_ReturnsBadRequest()
    {
        var response = await _client.DeleteAsync($"/api/tags/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── AssignTag ─────────────────────────────────────────────

    [Fact]
    public async Task AssignTag_ValidHabitAndTag_ReturnsOk()
    {
        var habitId = await CreateHabit("Tagged habit");
        var tagId = await CreateTag("Work", "#112233");

        var response = await _client.PostAsync($"/api/habits/{habitId}/tags/{tagId}", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AssignTag_NonExistentHabit_ReturnsBadRequest()
    {
        var tagId = await CreateTag("Orphan", "#445566");

        var response = await _client.PostAsync($"/api/habits/{Guid.NewGuid()}/tags/{tagId}", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── UnassignTag ───────────────────────────────────────────

    [Fact]
    public async Task UnassignTag_AssignedTag_ReturnsNoContent()
    {
        var habitId = await CreateHabit("Unassign habit");
        var tagId = await CreateTag("Remove", "#778899");

        await _client.PostAsync($"/api/habits/{habitId}/tags/{tagId}", null);

        var response = await _client.DeleteAsync($"/api/habits/{habitId}/tags/{tagId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task UnassignTag_NonExistentHabit_ReturnsBadRequest()
    {
        var tagId = await CreateTag("Ghost", "#AABBCC");

        var response = await _client.DeleteAsync($"/api/habits/{Guid.NewGuid()}/tags/{tagId}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Helpers ───────────────────────────────────────────────

    private async Task<Guid> CreateTag(string name, string color)
    {
        var response = await _client.PostAsJsonAsync("/api/tags", new { name, color });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Guid>();
    }

    private async Task<Guid> CreateHabit(string title)
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

    // ── DTOs ──────────────────────────────────────────────────

    private record LoginResponse(string UserId, string Token, string Name, string Email);
    private record TagDto(Guid Id, string Name, string Color);
    private record HabitDto(Guid Id, string Title);
}
