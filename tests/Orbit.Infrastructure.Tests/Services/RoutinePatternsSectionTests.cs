using System.Globalization;
using FluentAssertions;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services.Prompts;
using Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;

namespace Orbit.Infrastructure.Tests.Services;

public class RoutinePatternsSectionTests
{
    [Fact]
    public void Build_ConsistencyScore_UsesInvariantPercentage()
    {
        var pattern = new RoutinePattern
        {
            HabitId = Guid.NewGuid(),
            HabitTitle = "Run",
            Description = "Mornings",
            Confidence = "high",
            ConsistencyScore = 0.85m,
            TimeBlocks = []
        };
        var context = new PromptContext([], [], false, [pattern], null, null, null);
        var previousCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var result = new RoutinePatternsSection().Build(context);

            result.Should().Contain("confidence: high, consistency: 85 %)");
            result.Should().NotContain("85٪");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
