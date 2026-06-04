using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Orbit.IntegrationTests;

[Collection("Sequential")]
public class ExportUserDataTests : IAsyncLifetime
{
    private readonly IntegrationTestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _email = $"export-test-{Guid.NewGuid()}@integration.test";
    private const string TestCode = "999999";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ExportUserDataTests(IntegrationTestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        IntegrationTestHelpers.RegisterTestAccount(_email, TestCode);
    }

    public async Task InitializeAsync()
    {
        await IntegrationTestHelpers.AuthenticateWithCodeAsync(_client, _email, TestCode, JsonOptions);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Export_NoToken_ReturnsUnauthorized()
    {
        using var anonClient = _factory.CreateClient();
        var response = await anonClient.GetAsync("/api/profile/export");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Export_ReturnsDownloadableJsonAttachment()
    {
        var response = await _client.GetAsync("/api/profile/export");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileName.Should().Contain("orbit-data-export");
    }

    [Fact]
    public async Task Export_ContainsCreatedUserData_ScopedToRequestingUser()
    {
        var habitResponse = await _client.PostAsJsonAsync("/api/habits", new
        {
            title = "Export Meditation",
            type = "Boolean",
            frequencyUnit = "Day",
            frequencyQuantity = 1
        });
        habitResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var habitId = await IntegrationTestHelpers.ReadCreatedIdAsync(habitResponse, JsonOptions);

        var logResponse = await _client.PostAsJsonAsync($"/api/habits/{habitId}/log", new { });
        logResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var goalResponse = await _client.PostAsJsonAsync("/api/goals", new
        {
            title = "Export Goal",
            targetValue = 100m,
            unit = "pages"
        });
        goalResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var tagResponse = await _client.PostAsJsonAsync("/api/tags", new
        {
            name = "ExportTag",
            color = "#FF0000"
        });
        tagResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var export = await _client.GetFromJsonAsync<ExportDto>("/api/profile/export", JsonOptions);

        export.Should().NotBeNull();
        export!.Account.Email.Should().Be(_email);
        export.Habits.Should().ContainSingle(h => h.Title == "Export Meditation")
            .Which.Logs.Should().ContainSingle();
        export.Goals.Should().ContainSingle(g => g.Title == "Export Goal");
        export.Tags.Should().ContainSingle(t => t.Name == "Exporttag");

        using var otherClient = _factory.CreateClient();
        var otherEmail = $"export-other-{Guid.NewGuid()}@integration.test";
        IntegrationTestHelpers.RegisterTestAccount(otherEmail, TestCode);
        await IntegrationTestHelpers.AuthenticateWithCodeAsync(otherClient, otherEmail, TestCode, JsonOptions);

        var otherExport = await otherClient.GetFromJsonAsync<ExportDto>("/api/profile/export", JsonOptions);

        otherExport.Should().NotBeNull();
        otherExport!.Account.Email.Should().Be(otherEmail);
        otherExport.Habits.Should().BeEmpty();
        otherExport.Goals.Should().BeEmpty();
        otherExport.Tags.Should().BeEmpty();
    }

    private sealed record ExportDto(
        ExportAccountDto Account,
        List<ExportHabitDto> Habits,
        List<ExportGoalDto> Goals,
        List<ExportTagDto> Tags);

    private sealed record ExportAccountDto(string Email);
    private sealed record ExportHabitDto(string Title, List<ExportLogDto> Logs);
    private sealed record ExportLogDto(decimal Value);
    private sealed record ExportGoalDto(string Title);
    private sealed record ExportTagDto(string Name);
}
