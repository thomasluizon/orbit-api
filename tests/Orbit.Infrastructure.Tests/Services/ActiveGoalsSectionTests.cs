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

    [Fact]
    public void Build_DeadlineAndLinkedHabit_UseInvariantDateInPrompt()
    {
        var userId = Guid.NewGuid();
        var goal = Goal.Create(new Goal.CreateGoalParams(
            userId, "Run", 10.5m, "miles", "Race training", Deadline: new DateOnly(2026, 12, 31))).Value;
        var habit = Habit.Create(new HabitCreateParams(
            userId, "Morning run", Orbit.Domain.Enums.FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 1, 1))).Value;
        goal.AddHabit(habit);
        var context = new PromptContext([], [], false, null, null, null, null, [goal]);
        var previousCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var result = new ActiveGoalsSection().Build(context);

            result.Should().Contain("## User's Active Goals (1 total)");
            result.Should().Contain("Deadline: 2026-12-31");
            result.Should().Contain("Description: \"Race training\"");
            result.Should().Contain("Linked habits: \"Morning run\"");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
