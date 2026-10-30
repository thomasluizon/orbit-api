using System.Text.Json;
using FluentAssertions;

namespace Orbit.Infrastructure.Tests.Controllers;

public class StreakInfoContractTests
{
    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    [InlineData("openapi_v1-r2.json")]
    [InlineData("openapi_v2-r2.json")]
    public void GeneratedOpenApi_StreakInfoAppendsOptionalNullableFirstEligibleDate(string documentName)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../..", "src/Orbit.Api", documentName));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var response = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("StreakInfoResponse");

        response.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()).Should().NotContain("firstEligibleDate");
        var firstEligibleDate = response.GetProperty("properties").GetProperty("firstEligibleDate");
        firstEligibleDate.GetProperty("type").EnumerateArray()
            .Select(item => item.GetString()).Should().BeEquivalentTo("string", "null");
        firstEligibleDate.GetProperty("format").GetString().Should().Be("date");
    }

    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    public void GeneratedOpenApi_BulkLogResponseAppendsOptionalNullableStreaks(string documentName)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../..", "src/Orbit.Api", documentName));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        schemas.TryGetProperty("BulkLogResult", out var response).Should().BeTrue();
        var properties = response.GetProperty("properties");
        var required = response.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()).ToArray();

        properties.GetProperty("results").GetProperty("type").GetString().Should().Be("array");
        required.Should().BeEquivalentTo(new[] { "results" });
        foreach (var field in new[] { "previousStreak", "currentStreak" })
        {
            properties.TryGetProperty(field, out var streak).Should().BeTrue();
            streak.GetProperty("type").EnumerateArray()
                .Select(item => item.GetString()).Should().BeEquivalentTo(new[] { "null", "integer", "string" });
        }
    }

    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    public void GeneratedOpenApi_LogResponseAppendsOptionalNullablePreviousStreak(string documentName)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../..", "src/Orbit.Api", documentName));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var response = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("LogHabitResponse");
        var properties = response.GetProperty("properties");
        var required = response.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()).ToArray();

        properties.TryGetProperty("currentStreak", out _).Should().BeTrue();
        properties.TryGetProperty("previousStreak", out var previousStreak).Should().BeTrue();
        required.Should().NotContain("previousStreak");
        previousStreak.GetProperty("type").EnumerateArray()
            .Select(item => item.GetString()).Should().BeEquivalentTo(new[] { "null", "integer", "string" });
    }

    [Fact]
    public void GeneratedOpenApi_KeepsExistingFieldsAndAppendsOptionalNullableOrigin()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../..", "src/Orbit.Api/openapi.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var response = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("StreakInfoResponse");
        var properties = response.GetProperty("properties");
        var required = response.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()).ToArray();

        properties.TryGetProperty("lastFreezeCoveredDate", out _).Should().BeTrue();
        properties.TryGetProperty("freezeBankRemaining", out _).Should().BeTrue();
        properties.TryGetProperty("lastFreezeCoveredOrigin", out var origin).Should().BeTrue();
        required.Should().NotContain("lastFreezeCoveredOrigin");
        origin.GetProperty("type").EnumerateArray()
            .Select(item => item.GetString()).Should().BeEquivalentTo(new[] { "null", "string" });
    }
}
