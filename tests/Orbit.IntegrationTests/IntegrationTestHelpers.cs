using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Orbit.IntegrationTests;

internal static class IntegrationTestHelpers
{
    public static void RegisterTestAccount(string email, string code)
    {
        var existing = Environment.GetEnvironmentVariable("TEST_ACCOUNTS") ?? string.Empty;
        var entry = $"{email}:{code}";

        if (existing.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(account => string.Equals(account, entry, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Environment.SetEnvironmentVariable(
            "TEST_ACCOUNTS",
            string.IsNullOrEmpty(existing) ? entry : $"{existing},{entry}");
    }

    public static async Task<LoginResponse> AuthenticateWithCodeAsync(
        HttpClient client,
        string email,
        string code,
        JsonSerializerOptions jsonOptions)
    {
        var sendCodeResponse = await client.PostAsJsonAsync("/api/auth/send-code", new { email });
        sendCodeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var verifyResponse = await client.PostAsJsonAsync("/api/auth/verify-code", new
        {
            email,
            code
        });
        verifyResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var login = await verifyResponse.Content.ReadFromJsonAsync<LoginResponse>(jsonOptions);
        login.Should().NotBeNull();

        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {login!.Token}");

        return login;
    }

    public static string BuildHabitSchedulePath(
        DateOnly? dateFrom = null,
        DateOnly? dateTo = null,
        bool includeOverdue = true,
        bool includeGeneral = true,
        int page = 1,
        int pageSize = 100)
    {
        var from = dateFrom ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var to = dateTo ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7));

        return $"/api/habits?dateFrom={from:yyyy-MM-dd}&dateTo={to:yyyy-MM-dd}&includeOverdue={includeOverdue.ToString().ToLowerInvariant()}&includeGeneral={includeGeneral.ToString().ToLowerInvariant()}&page={page}&pageSize={pageSize}";
    }

    public static async Task<Guid> ReadCreatedIdAsync(
        HttpResponseMessage response,
        JsonSerializerOptions jsonOptions)
    {
        var payload = await response.Content.ReadFromJsonAsync<CreatedIdResponse>(jsonOptions);
        payload.Should().NotBeNull();
        payload!.Id.Should().NotBeEmpty();
        return payload.Id;
    }

    internal sealed record LoginResponse(
        Guid UserId,
        string Token,
        string Name,
        string Email,
        bool WasReactivated = false,
        string? RefreshToken = null);

    private sealed record CreatedIdResponse(Guid Id);
}
