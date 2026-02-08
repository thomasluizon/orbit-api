using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Orbit.IntegrationTests;

[Collection("Sequential")]
public class ProfileControllerTests : IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly string _email = $"profile-test-{Guid.NewGuid()}@integration.test";
    private const string Password = "TestPassword123!";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ProfileControllerTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            name = "Profile Test User",
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
        _client.Dispose();
    }

    // ── GetProfile ────────────────────────────────────────────

    [Fact]
    public async Task GetProfile_Authenticated_ReturnsProfile()
    {
        var response = await _client.GetAsync("/api/profile");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var profile = await response.Content.ReadFromJsonAsync<ProfileResponse>(JsonOptions);
        profile.Should().NotBeNull();
        profile!.Name.Should().Be("Profile Test User");
        profile.Email.Should().Be(_email);
    }

    [Fact]
    public async Task GetProfile_NoToken_ReturnsUnauthorized()
    {
        using var anonClient = _factory.CreateClient();
        var response = await anonClient.GetAsync("/api/profile");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── SetTimezone ───────────────────────────────────────────

    [Fact]
    public async Task SetTimezone_ValidTimezone_ReturnsOk()
    {
        var response = await _client.PutAsJsonAsync("/api/profile/timezone", new
        {
            timeZone = "America/New_York"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify it was persisted
        var profileResponse = await _client.GetAsync("/api/profile");
        var profile = await profileResponse.Content.ReadFromJsonAsync<ProfileResponse>(JsonOptions);
        profile!.TimeZone.Should().Be("America/New_York");
    }

    [Fact]
    public async Task SetTimezone_InvalidTimezone_ReturnsBadRequest()
    {
        var response = await _client.PutAsJsonAsync("/api/profile/timezone", new
        {
            timeZone = "Not/A/Real/Timezone"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── DTOs ──────────────────────────────────────────────────

    private record LoginResponse(string UserId, string Token, string Name, string Email);
    private record ProfileResponse(string Name, string Email, string? TimeZone);
}
