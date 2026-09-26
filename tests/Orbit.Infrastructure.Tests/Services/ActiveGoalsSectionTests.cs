using System.Globalization;
using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Services.Prompts;
using Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;

namespace Orbit.Infrastructure.Tests.Services;

public class ActiveGoalsSectionTests
{
    [Fact]
    public void Build_DecimalProgress_UsesInvariantCultureInPrompt()
    {
        var goal = Goal.Create(Guid.NewGuid(), "Run", 10.5m, "miles").Value;
        goal.UpdateProgress(3.5m);
        var context = new PromptContext([], [], false, null, null, null, null, [goal]);
        var previousCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            var result = new ActiveGoalsSection().Build(context);

            result.Should().Contain("Progress: 3.5/10.5 \"miles\"");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
