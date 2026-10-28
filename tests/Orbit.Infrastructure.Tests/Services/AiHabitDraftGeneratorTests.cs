using System.Text.Json;
using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AiHabitDraftGeneratorTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public void Prompt_ContainsCurrentPersonalDataAndOnlyTheDraftBeingRefined(string language)
    {
        var userId = Guid.NewGuid();
        var today = new DateOnly(2026, 10, 26);
        var draft = new HabitSuggestionDraft(Guid.NewGuid(), "📖", "Read", "Habit", FrequencyUnit.Day, 1,
            [], today, new(22, 0), new(22, 15), null, [], "A quiet pause before bed.");
        var habit = draft.ToHabit(userId).Value;
        var goal = Goal.Create(userId, "Read more", 10, "books").Value;
        var request = new HabitSuggestionRequest(userId, "weekdays at 22:30", language, 1, today, [habit], [goal], draft);
        using var prompt = JsonDocument.Parse(AiHabitDraftGenerator.BuildPrompt(request));
        prompt.RootElement.GetProperty("language").GetString().Should().Be(language);
        prompt.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        prompt.RootElement.GetProperty("original").GetProperty("id").GetGuid().Should().Be(draft.Id);
        prompt.RootElement.GetProperty("habits")[0].GetProperty("id").GetGuid().Should().Be(habit.Id);
        prompt.RootElement.GetProperty("goals")[0].GetProperty("title").GetString().Should().Be(goal.Title);
        AiHabitDraftGenerator.Instructions.Should().Contain("existingHabitId").And.Contain("unoccupied time")
            .And.Contain("untrusted data").And.Contain("Keep fields unrelated")
            .And.Contain("required non-null HH:mm strings for every draft, with end after start");
        AiHabitDraftGenerator.Instructions.Should().Contain("For new activities, only populate what the person requested plus fields required by the draft contract")
            .And.Contain("Leave unrequested days and checklistItems empty, and reminderMinutesBefore null")
            .And.Contain("For an adjustment with existingHabitId, return the existing values for every field the person did not ask to change")
            .And.Contain("including days, checklistItems and reminderMinutesBefore")
            .And.Contain("Empty days and checklistItems replace the stored collections; null reminderMinutesBefore disables reminders")
            .And.Contain("Do not add a daily cadence when an existing habit already has a different cadence")
            .And.Contain("Required choices are editable proposals, never facts supplied by the person");
    }
}
