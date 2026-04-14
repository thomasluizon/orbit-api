using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Infrastructure.Persistence;

namespace Orbit.IntegrationTests;

[Collection("Sequential")]
public class ApiKeyTests : IAsyncLifetime
{
    private readonly IntegrationTestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _email = $"apikey-test-{Guid.NewGuid()}@integration.test";
    private const string TestCode = "999999";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ApiKeyTests(IntegrationTestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();

        var existing = Environment.GetEnvironmentVariable("TEST_ACCOUNTS") ?? "";
        var entry = $"{_email}:{TestCode}";
        Environment.SetEnvironmentVariable("TEST_ACCOUNTS",
            string.IsNullOrEmpty(existing) ? entry : $"{existing},{entry}");
    }

    public async Task InitializeAsync()
    {
        await _client.PostAsJsonAsync("/api/auth/send-code", new { email = _email });

        var verifyResponse = await _client.PostAsJsonAsync("/api/auth/verify-code", new
        {
            email = _email,
            code = TestCode
        });

        var login = await verifyResponse.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions);
        _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {login!.Token}");

        await ExpireTrialAsync();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    private async Task ExpireTrialAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var user = await dbContext.Users.SingleAsync(u => u.Email == _email);
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        await dbContext.SaveChangesAsync();
    }

    // ── Create API Key ───────────────────────────────────────

    [Fact]
    public async Task CreateApiKey_FreeUser_ReturnsForbidden()
    {
        var response = await _client.PostAsJsonAsync("/api/api-keys", new { name = "Test Key" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var body = await response.Content.ReadFromJsonAsync<ErrorWithCodeResponse>(JsonOptions);
        body!.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task CreateApiKey_MissingName_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/api-keys", new { name = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── List API Keys ────────────────────────────────────────

    [Fact]
    public async Task ListApiKeys_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/api-keys");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var keys = await response.Content.ReadFromJsonAsync<List<ApiKeyListItem>>(JsonOptions);
        keys.Should().NotBeNull();
    }

    // ── Revoke API Key ───────────────────────────────────────

    [Fact]
    public async Task RevokeApiKey_NonExistentKey_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync($"/api/api-keys/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── API Key Authentication ───────────────────────────────

    [Fact]
    public async Task ApiKeyAuth_InvalidKey_ReturnsUnauthorized()
    {
        using var keyClient = _factory.CreateClient();
        keyClient.DefaultRequestHeaders.Add("Authorization", "Bearer orb_invalidkey12345678901234567890");

        var response = await keyClient.GetAsync("/api/api-keys");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ApiKeyAuth_NonOrbPrefix_RoutesToJwt()
    {
        using var keyClient = _factory.CreateClient();
        keyClient.DefaultRequestHeaders.Add("Authorization", "Bearer not_an_api_key_token");

        var response = await keyClient.GetAsync("/api/api-keys");

        // JWT handler will reject this as an invalid JWT
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task JwtAuth_ContinuesToWork()
    {
        // The main _client uses JWT auth -- verify it still works
        var response = await _client.GetAsync("/api/api-keys");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── DTO classes for deserialization ──────────────────────

    private record LoginResponse(Guid UserId, string Token, string Name, string Email);

    private record ApiKeyListItem(
        Guid Id,
        string Name,
        string KeyPrefix,
        DateTime CreatedAtUtc,
        DateTime? LastUsedAtUtc,
        bool IsRevoked);

    private record ErrorWithCodeResponse(string Error, string? ErrorCode);
}
