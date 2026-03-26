using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Orbit.IntegrationTests;

[Collection("Sequential")]
public class AuthControllerTests : IAsyncLifetime
{
    private readonly HttpClient _client;
    private readonly string _email = $"auth-test-{Guid.NewGuid()}@integration.test";
    private const string TestCode = "999999";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public AuthControllerTests(WebApplicationFactory<Program> factory)
    {
        var existing = Environment.GetEnvironmentVariable("TEST_ACCOUNTS") ?? "";
        var entry = $"{_email}:{TestCode}";
        Environment.SetEnvironmentVariable("TEST_ACCOUNTS",
            string.IsNullOrEmpty(existing) ? entry : $"{existing},{entry}");

        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    // -- SendCode -----------------------------------------------

    [Fact]
    public async Task SendCode_ValidEmail_ReturnsOk()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/send-code", new { email = _email });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // -- VerifyCode ---------------------------------------------

    [Fact]
    public async Task VerifyCode_ValidCode_ReturnsToken()
    {
        await _client.PostAsJsonAsync("/api/auth/send-code", new { email = _email });

        var response = await _client.PostAsJsonAsync("/api/auth/verify-code", new
        {
            email = _email,
            code = TestCode
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions);
        body!.Token.Should().NotBeNullOrEmpty();
        body.UserId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task VerifyCode_WrongCode_ReturnsUnauthorized()
    {
        await _client.PostAsJsonAsync("/api/auth/send-code", new { email = _email });

        var response = await _client.PostAsJsonAsync("/api/auth/verify-code", new
        {
            email = _email,
            code = "000000"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task VerifyCode_SameEmail_ReturnsSameUser()
    {
        await _client.PostAsJsonAsync("/api/auth/send-code", new { email = _email });

        var first = await _client.PostAsJsonAsync("/api/auth/verify-code", new
        {
            email = _email,
            code = TestCode
        });
        var firstResult = await first.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions);

        await _client.PostAsJsonAsync("/api/auth/send-code", new { email = _email });

        var second = await _client.PostAsJsonAsync("/api/auth/verify-code", new
        {
            email = _email,
            code = TestCode
        });
        var secondResult = await second.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions);

        secondResult!.UserId.Should().Be(firstResult!.UserId);
    }

    // -- DTOs ---------------------------------------------------

    private record LoginResponse(Guid UserId, string Token, string Name, string Email);
}
