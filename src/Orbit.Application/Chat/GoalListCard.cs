using System.Globalization;
using System.Text.RegularExpressions;
using Orbit.Domain.Entities;
using Orbit.Application.Goals.Services;

namespace Orbit.Application.Chat;

public record GoalListCard(IReadOnlyList<GoalListCardItem> Items);

public record GoalListCardItem(
    string Id,
    string Title,
    decimal Current,
    decimal Target,
    string Unit,
    string? Deadline,
    string? TrackingStatus = null,
    decimal? ProgressPercentage = null,
    string? ProjectedCompletionDate = null,
    int? DaysToDeadline = null);

public static partial class GoalListCardBuilder
{
    public const string PromptInstruction = """
        ## Goal list rendering (this client)
        This app can display active goals and their projections. After review_goals succeeds, or when the user asks to list goals, write one short intro without repeating figures, then emit [[orbit:goals]] before optional follow-ups. Emit at most one directive. For other questions, answer normally.
        """;

    public static bool TryExtractDirective(string? message, out string stripped)
    {
        stripped = message ?? string.Empty;
        if (string.IsNullOrEmpty(message))
            return false;

        if (!DirectiveRegex().IsMatch(message))
            return false;

        stripped = DirectiveRegex().Replace(message, string.Empty).Trim();
        return true;
    }

    public static GoalListCard Build(
        IReadOnlyList<Goal> activeGoals, DateOnly? userToday = null,
        int weekStartDay = 1, bool includeProjections = true)
    {
        var items = activeGoals
            .OrderBy(goal => goal.Position)
            .Select(goal => BuildItem(goal, userToday, weekStartDay, includeProjections))
            .ToList();
        return new GoalListCard(items);
    }

    private static GoalListCardItem BuildItem(
        Goal goal, DateOnly? userToday, int weekStartDay, bool includeProjections)
    {
        var metrics = includeProjections && userToday.HasValue && goal.Deadline.HasValue
            && goal.Habits.Count > 0
            ? GoalMetricsCalculator.Calculate(goal, userToday.Value, weekStartDay)
            : null;
        return new GoalListCardItem(
            goal.Id.ToString(), goal.Title, goal.CurrentValue, goal.TargetValue, goal.Unit,
            goal.Deadline?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            metrics?.TrackingStatus,
            metrics?.VelocityPerDay > 0 ? metrics.ProgressPercentage : null,
            metrics?.VelocityPerDay > 0
                ? metrics.ProjectedCompletionDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null,
            metrics?.DaysToDeadline);
    }

    public static GoalListCard WithoutProjections(GoalListCard card) =>
        new(card.Items.Select(item => item with
        {
            TrackingStatus = null,
            ProgressPercentage = null,
            ProjectedCompletionDate = null,
            DaysToDeadline = null
        }).ToList());

    [GeneratedRegex(@"\[\[orbit:goals\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex DirectiveRegex();
}
