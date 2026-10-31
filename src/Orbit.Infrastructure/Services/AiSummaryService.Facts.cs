using System.Text.Json;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Services;

public sealed partial class AiSummaryService
{
    internal static string BuildSummaryPrompt(List<Habit> scheduledHabits, DailySummaryContext context) =>
        new SummaryFacts(scheduledHabits, context).Serialize();

    private sealed class SummaryFacts(List<Habit> scheduledHabits, DailySummaryContext context)
    {
        private readonly ILookup<Guid?, Habit> children = scheduledHabits.ToLookup(habit => habit.ParentHabitId);
        private readonly IReadOnlyList<DailySummaryPendingHabit> pending = context.PendingHabits ?? [];

        internal string Serialize()
        {
            var nextStep = SelectNextStep(context);
            var facts = new
            {
                profileLanguage = LocaleHelper.GetAiLanguageName(context.Language),
                partOfDay = context.CurrentLocalTime.HasValue
                    ? ResolveDayPeriod(context.CurrentLocalTime.Value).ToString().ToLowerInvariant() : "not provided",
                routines = children[null].Where(habit => !habit.IsBadHabit && children[habit.Id].Any())
                    .Select(Routine).ToArray(),
                standaloneTasks = children[null].Where(habit => !habit.IsBadHabit && !children[habit.Id].Any())
                    .Select(Item).ToArray(),
                skippedItems = scheduledHabits.Where(habit => HasSkipLogInRange(habit, context.DateFrom, context.DateTo))
                    .Select(habit => new { id = habit.Id, title = habit.Title }).ToArray(),
                badHabits = scheduledHabits.Where(habit => habit.IsBadHabit).Select(habit => new
                {
                    id = habit.Id,
                    title = habit.Title,
                    state = Leaves(habit).Any(item => IsDoneInRange(item, context.DateFrom, context.DateTo))
                        || IsDoneInRange(habit, context.DateFrom, context.DateTo) ? "slipped" : "clean"
                }).ToArray(),
                nextStep = nextStep is null ? null : new
                {
                    id = nextStep.Id,
                    title = nextStep.Title,
                    parentTitle = nextStep.ParentTitle,
                    orderBasis = nextStep.IsOverdue ? "oldest overdue occurrence"
                        : nextStep.DueTime.HasValue || nextStep.DueEndTime.HasValue ? "first scheduled time" : "personal day order"
                }
            };
            return "Daily facts (JSON):\n" + JsonSerializer.Serialize(facts, JsonSerializerOptions.Web);
        }

        private IEnumerable<Habit> Leaves(Habit habit) => children[habit.Id].Any()
            ? children[habit.Id].SelectMany(Leaves) : [habit];

        private string State(Habit habit)
        {
            if (HasSkipLogInRange(habit, context.DateFrom, context.DateTo))
                return "skipped";
            var open = pending.FirstOrDefault(item => item.Id == habit.Id);
            if (open is not null)
                return open.CompletedCount > 0 ? "in progress" : "pending";
            return IsDoneInRange(habit, context.DateFrom, context.DateTo) ? "done" : "pending";
        }

        private SummaryItem Item(Habit habit) => new(habit.Id, habit.Title, State(habit));

        private SummaryRoutine Routine(Habit habit)
        {
            var steps = Leaves(habit).Where(step => !step.IsBadHabit).ToList();
            var completed = steps.Count(step => State(step) == "done");
            var skipped = steps.Count(step => State(step) == "skipped");
            var ids = steps.Select(step => step.Id).ToHashSet();
            var state = steps.Count == 0 ? "no open steps"
                : completed == steps.Count ? "done"
                : skipped == steps.Count ? "skipped"
                : completed + skipped == steps.Count ? "closed with skips"
                : completed == 0 ? steps.Any(step => State(step) == "in progress") ? "in progress" : "not started"
                : completed + skipped == steps.Count - 1 ? "nearly done" : "in progress";
            var openSteps = pending.Where(item => !item.HasPendingChildren && ids.Contains(item.Id))
                .Select(item => new PendingStep(item.Id, item.Title, item.ParentTitle, item.DayOrder,
                    item.IsOverdue || item.DueDate < context.UserToday
                    || item.DueDate == context.UserToday && context.CurrentLocalTime.HasValue
                    && (item.DueTime ?? item.DueEndTime) < context.CurrentLocalTime)).ToArray();
            return new(habit.Id, habit.Title, state, completed, steps.Count, openSteps,
                children[habit.Id].Where(child => children[child.Id].Any() && !child.IsBadHabit).Select(Routine).ToArray());
        }
    }

    private sealed record SummaryItem(Guid Id, string Title, string State);
    private sealed record PendingStep(Guid Id, string Title, string? ParentTitle, int DayOrder, bool IsOverdue);
    private sealed record SummaryRoutine(Guid Id, string Title, string State, int CompletedSteps, int TotalSteps,
        PendingStep[] PendingSteps, SummaryRoutine[] Routines);

}
