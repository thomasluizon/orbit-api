using System.Globalization;
using System.Text.Json;
using FluentValidation;
using Orbit.Domain.Entities;

namespace Orbit.Application.Chat.Validators;

internal sealed record RestartPlanProposal(JsonElement Arguments, DateOnly Today,
    IReadOnlyList<Habit> CurrentHabits, IReadOnlyList<Habit> AccountHabits, IReadOnlyList<HabitListCard> Reads);

internal sealed class RestartPlanValidator : AbstractValidator<RestartPlanProposal>
{
    internal const string FreshReadRequired = "Restart plans require a successful query_habits for the complete remaining due and overdue set in this turn. Read date=today with include_overdue=true, is_completed=false, for_selection=false and orbit_read_complete=false, including every page, then retry the complete plan.";
    internal const string InvalidItems = "Restart plans require revised_items with a unique habit_id and only updates.due_date in YYYY-MM-DD format for every habit.";
    internal const string SmallerToday = "Restart plans must keep strictly fewer habits today than the complete remaining due and overdue set.";
    internal const string DateWindow = "Restart dates must be today or within the next seven days, using the account's local today.";

    public RestartPlanValidator()
    {
        RuleFor(proposal => proposal).Custom((proposal, context) =>
        {
            var currentIds = proposal.CurrentHabits.Select(habit => habit.Id).ToHashSet();
            if (!HasFreshRead(proposal, currentIds))
                context.AddFailure(FreshReadRequired);
            if (!TryReadDates(proposal.Arguments, out var dates))
            {
                context.AddFailure(InvalidItems);
                return;
            }
            var missing = proposal.CurrentHabits.Where(habit => !dates.ContainsKey(habit.Id))
                .Select(habit => habit.Title).ToArray();
            var extra = dates.Keys.Where(id => !currentIds.Contains(id)).Select(id =>
                proposal.AccountHabits.FirstOrDefault(habit => habit.Id == id)?.Title ?? id.ToString()).ToArray();
            if (missing.Length > 0 || extra.Length > 0)
                context.AddFailure($"Restart plan does not match the current set. Missing habits: {Names(missing)}. Extra habits: {Names(extra)}. Read query_habits again and retry with exactly the returned set.");
            if (dates.Values.Any(date => date < proposal.Today || date > proposal.Today.AddDays(7)))
                context.AddFailure(DateWindow);
            if (dates.Values.Count(date => date == proposal.Today) >= currentIds.Count)
                context.AddFailure(SmallerToday);
        });
    }

    private static string Names(IReadOnlyList<string> names) => names.Count == 0 ? "none" : string.Join(", ", names);

    private static bool HasFreshRead(RestartPlanProposal proposal, IReadOnlySet<Guid> currentIds) =>
        proposal.Reads.Where(read => read.Scope == HabitListCardBuilder.ScopeToday && read.Date == proposal.Today
                && read.TotalCount == currentIds.Count && read.Selection is not { RequiresAnswer: true }
                && read.Items.All(item => item.Status is HabitListCardBuilder.StatusToday or HabitListCardBuilder.StatusOverdue))
            .GroupBy(read => read.SourceRevision)
            .Any(pages => currentIds.SetEquals(pages.SelectMany(page => page.Items)
                .Select(item => Guid.TryParse(item.Id, out var id) ? id : Guid.Empty)));

    private static bool TryReadDates(JsonElement arguments, out Dictionary<Guid, DateOnly> dates)
    {
        dates = [];
        if (!arguments.TryGetProperty("revised_items", out var items)
            || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            return false;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("habit_id", out var idValue) || idValue.ValueKind != JsonValueKind.String
                || !Guid.TryParse(idValue.GetString(), out var id)
                || !item.TryGetProperty("updates", out var updates) || updates.ValueKind != JsonValueKind.Object
                || updates.EnumerateObject().Count() != 1
                || !updates.TryGetProperty("due_date", out var dateValue) || dateValue.ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(dateValue.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date) || !dates.TryAdd(id, date))
                return false;
        }
        return true;
    }
}
