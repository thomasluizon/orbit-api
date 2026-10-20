using System.Text.Json;
using FluentAssertions;

namespace Orbit.Infrastructure.Tests.Controllers;

public class HabitRecurrenceOriginContractTests
{
    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    public void HabitDetail_RecurrenceOriginsAreOptionalAndNullable(string file)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../..", "src/Orbit.Api", file));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var schema = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("HabitDetailResponse");

        schema.GetProperty("required").EnumerateArray().Select(item => item.GetString())
            .Should().NotContain("scheduledStartDate").And.NotContain("originalDayOfMonth");
        var properties = schema.GetProperty("properties");
        var start = properties.GetProperty("scheduledStartDate");
        start.GetProperty("format").GetString().Should().Be("date");
        start.GetProperty("type").EnumerateArray().Select(item => item.GetString())
            .Should().Contain("null").And.Contain("string");
        var day = properties.GetProperty("originalDayOfMonth");
        day.GetProperty("format").GetString().Should().Be("int32");
        day.GetProperty("type").EnumerateArray().Select(item => item.GetString())
            .Should().Contain("null").And.Contain("integer");
    }
}
