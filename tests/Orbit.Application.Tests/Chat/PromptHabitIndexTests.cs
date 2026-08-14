using FluentAssertions;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Chat;

public class PromptHabitIndexTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 8, 14);

    [Fact]
    public void BuildPromptHabitIndex_OverLimit_PrioritizesDueHabitsAndKeepsAncestors()
    {
        var completedParent = CreateHabit("Completed parent", Today, null, isOneTime: true);
        completedParent.Log(Today);
        var overdue = Enumerable.Range(0, 40)
            .Select(index => CreateHabit(
                $"Overdue {index}",
                Today.AddDays(-1),
                index == 0 ? completedParent.Id : null))
            .ToList();
        var dueToday = Enumerable.Range(0, 40)
            .Select(index => CreateHabit($"Today {index}", Today))
            .ToList();
        var future = Enumerable.Range(0, 920)
            .Select(index => CreateHabit($"Future {index}", Today.AddDays(1)))
            .ToList();
        var habits = new[] { completedParent }
            .Concat(overdue)
            .Concat(dueToday)
            .Concat(future)
            .ToList();

        var result = ProcessUserChatCommandHandler.BuildPromptHabitIndex(habits, Today);
        var selectedIds = result.Habits.Select(habit => habit.Id).ToHashSet();

        result.IsPartial.Should().BeTrue();
        result.Habits.Should().HaveCount(AppConstants.MaxPromptHabitEntries);
        overdue.Should().OnlyContain(habit => selectedIds.Contains(habit.Id));
        dueToday.Should().OnlyContain(habit => selectedIds.Contains(habit.Id));
        foreach (var selectedHabit in result.Habits.Where(habit => habit.ParentHabitId.HasValue))
            selectedIds.Should().Contain(selectedHabit.ParentHabitId!.Value);
    }

    private static Habit CreateHabit(
        string title,
        DateOnly dueDate,
        Guid? parentHabitId = null,
        bool isOneTime = false)
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId,
            title,
            isOneTime ? null : FrequencyUnit.Day,
            isOneTime ? null : 1,
            DueDate: dueDate,
            ParentHabitId: parentHabitId)).Value;
        if (int.TryParse(title.Split(' ').LastOrDefault(), out var position))
            habit.SetPosition(position);
        return habit;
    }
}
