using System.Text.Json;
using FluentAssertions;

namespace Orbit.Infrastructure.Tests.Controllers;

public class StreakInfoContractTests
{
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
