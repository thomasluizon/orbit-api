using Orbit.Application.Common;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Habits.Commands;

internal static class BulkHabitSubtree
{
    private const string DefaultTagColor = "#7c3aed";
    private static readonly BulkHabitItemValidator RootValidator = new(isRoot: true);
    private static readonly BulkHabitItemValidator ChildValidator = new(isRoot: false);

    internal sealed record Node(Habit Habit, IReadOnlyList<Tag> Tags);

    internal static Result<IReadOnlyList<Node>> Build(
        Guid userId, BulkHabitItem root, DateOnly today, int rootPosition, int maxDepth,
        CancellationToken ct, out string? field)
    {
        field = null;
        var nodes = new List<Node>();
        var pending = new Stack<(BulkHabitItem Item, Habit? Parent, int Position, int Depth)>();
        pending.Push((root, null, rootPosition, 0));
        while (pending.TryPop(out var next))
        {
            ct.ThrowIfCancellationRequested();
            if (next.Depth >= maxDepth)
            {
                field = "SubHabits";
                return Result.Failure<IReadOnlyList<Node>>(ErrorMessages.MaxDepthReached.Format(maxDepth));
            }

            var habit = BuildHabit(userId, next.Item, next.Parent, today, next.Position);
            if (habit.IsFailure)
            {
                field = next.Parent is null ? DetermineField(habit.Error) : "SubHabits";
                return Failure(habit, next.Item, next.Parent);
            }

            var validation = (next.Parent is null ? RootValidator : ChildValidator).Validate(next.Item);
            if (!validation.IsValid)
            {
                field = next.Parent is null ? validation.Errors[0].PropertyName : "SubHabits";
                return Failure(Result.Failure(validation.ToString(), validation.Errors[0].ErrorCode), next.Item, next.Parent);
            }

            var tags = new List<Tag>();
            foreach (var name in next.Item.Tags ?? [])
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var tag = Tag.Create(userId, name, DefaultTagColor);
                if (tag.IsFailure)
                {
                    field = next.Parent is null ? "Tags" : "SubHabits";
                    return Failure(tag, next.Item, next.Parent);
                }
                tags.Add(tag.Value);
            }

            nodes.Add(new(habit.Value, tags));
            var children = next.Item.SubHabits ?? [];
            for (var i = children.Count - 1; i >= 0; i--)
                pending.Push((children[i], habit.Value, i, next.Depth + 1));
        }

        return Result.Success<IReadOnlyList<Node>>(nodes);
    }

    private static Result<IReadOnlyList<Node>> Failure(Result failure, BulkHabitItem item, Habit? parent) =>
        new(default, false, parent is null ? failure.Error : $"Sub-habit '{item.Title}' failed: {failure.Error}",
            failure.ErrorCode, failure.ErrorArgs);

    private static Result<Habit> BuildHabit(
        Guid userId, BulkHabitItem item, Habit? parent, DateOnly today, int position)
    {
        var hasExplicitCadence = item.FrequencyUnit is not null
            || item.FrequencyQuantity is not null
            || item.IntervalWeeks is not null
            || item.Days is not null;
        var frequencyUnit = item.FrequencyUnit ?? parent?.FrequencyUnit;
        var frequencyQuantity = item.FrequencyQuantity ?? parent?.FrequencyQuantity;
        var days = item.Days;
        if (days is null && frequencyUnit == FrequencyUnit.Day && frequencyQuantity == 1)
            days = parent?.Days.ToList();

        return Habit.Create(new HabitCreateParams(
            userId, item.Title, frequencyUnit, frequencyQuantity,
            item.DueDate ?? parent?.DueDate ?? today, item.Description,
            Emoji: item.Emoji,
            Days: days,
            IsBadHabit: item.IsBadHabit,
            DueTime: item.DueTime,
            DueEndTime: item.DueEndTime,
            ReminderEnabled: item.ReminderEnabled,
            ReminderTimes: item.ReminderTimes,
            ScheduledReminders: item.ScheduledReminders,
            RelativeReminders: item.RelativeReminders,
            EndDate: item.EndDate,
            ChecklistItems: item.ChecklistItems,
            ParentHabitId: parent?.Id,
            IsGeneral: parent?.IsGeneral ?? item.IsGeneral,
            IsFlexible: item.IsFlexible,
            Position: position,
            GoogleEventId: item.GoogleEventId,
            IntervalWeeks: item.IntervalWeeks ?? (hasExplicitCadence ? null : parent?.IntervalWeeks)));
    }

    private static string? DetermineField(string error)
    {
        if (error.Contains("title", StringComparison.OrdinalIgnoreCase))
            return "Title";
        if (error.Contains("frequency", StringComparison.OrdinalIgnoreCase))
            return "FrequencyUnit";
        if (error.Contains("days", StringComparison.OrdinalIgnoreCase))
            return "Days";
        return null;
    }
}
