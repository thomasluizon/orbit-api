using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Orbit.IntegrationTests;

[Collection("Sequential")]
public class ProfileControllerTests : IAsyncLifetime
{
    private readonly IntegrationTestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _email = $"profile-test-{Guid.NewGuid()}@integration.test";
    private const string TestCode = "999999";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ProfileControllerTests(IntegrationTestWebApplicationFactory factory)
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
        profile!.Name.Should().Be(_email.Split('@')[0]);
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
    public async Task SetTimezone_ValidTimezone_ReturnsNoContent()
    {
        var response = await _client.PutAsJsonAsync("/api/profile/timezone", new
        {
            timeZone = "America/New_York"
        });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

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

    private record ProfileResponse(string Name, string Email, string? TimeZone);
}
