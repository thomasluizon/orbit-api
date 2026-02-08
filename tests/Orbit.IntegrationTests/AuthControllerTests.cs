using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Orbit.IntegrationTests;

[Collection("Sequential")]
public class AuthControllerTests(WebApplicationFactory<Program> factory) : IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly string _email = $"auth-test-{Guid.NewGuid()}@integration.test";
    private const string Password = "TestPassword123!";

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    // ── Register ──────────────────────────────────────────────

    [Fact]
    public async Task Register_ValidData_ReturnsUserId()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            name = "Test User",
            email = _email,
            password = Password
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<RegisterResponse>();
        body!.UserId.Should().NotBeNullOrEmpty();
        body.Message.Should().Be("Registration successful");
    }

    [Fact]
    public async Task Register_DuplicateEmail_ReturnsBadRequest()
    {
        var email = $"dup-{Guid.NewGuid()}@integration.test";

        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            name = "First",
            email,
            password = Password
        });

        var response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            name = "Second",
            email,
            password = Password
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Login ─────────────────────────────────────────────────

    [Fact]
    public async Task Login_ValidCredentials_ReturnsToken()
    {
        var email = $"login-{Guid.NewGuid()}@integration.test";

        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            name = "Login User",
            email,
            password = Password
        });

        var response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password = Password
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        body!.Token.Should().NotBeNullOrEmpty();
        body.Email.Should().Be(email);
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorized()
    {
        var email = $"badpw-{Guid.NewGuid()}@integration.test";

        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            name = "Bad PW User",
            email,
            password = Password
        });

        var response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password = "WrongPassword999!"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── DTOs ──────────────────────────────────────────────────

    private record RegisterResponse(string UserId, string Message);
    private record LoginResponse(string UserId, string Token, string Name, string Email);
}
