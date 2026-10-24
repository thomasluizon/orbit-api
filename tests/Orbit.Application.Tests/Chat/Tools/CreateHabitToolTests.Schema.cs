using System.Text.Json;
using FluentAssertions;

namespace Orbit.Application.Tests.Chat.Tools;

public partial class CreateHabitToolTests
{
    [Fact]
    public void GetParameterSchema_MatchesReviewedContract()
    {
        using var stream = typeof(CreateHabitToolTests).Assembly.GetManifestResourceStream("CreateHabitTool.schema.json")
            ?? throw new InvalidOperationException("The create_habit schema contract must be embedded in the test assembly.");
        using var expected = JsonDocument.Parse(stream);

        JsonSerializer.Serialize(_tool.GetParameterSchema()).Should().Be(JsonSerializer.Serialize(expected.RootElement));
    }
}
