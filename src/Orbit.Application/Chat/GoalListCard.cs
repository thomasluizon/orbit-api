using System.Globalization;
using System.Text.RegularExpressions;
using Orbit.Domain.Entities;

namespace Orbit.Application.Chat;

public record GoalListCard(IReadOnlyList<GoalListCardItem> Items);

public record GoalListCardItem(
    string Id,
    string Title,
    decimal Current,
    decimal Target,
    string Unit,
    string? Deadline);

public static partial class GoalListCardBuilder
{
    public const string PromptInstruction = """
        ## Goal list rendering (this client)
        This app can display the user's active goals as a live card showing each goal's progress. When the user asks to see, list, or review their goals or goal progress (for example "what are my goals", "show my goals", "how are my goals going", "meus objetivos"), do NOT write the goals out as text and do NOT enumerate them. Instead reply with a brief one-line intro and then, on its own final line, exactly ONE directive token: [[orbit:goals]]. The app replaces the directive with the rendered goal list, so never list the goals yourself when you emit it. Emit at most one directive, always as the last thing in your reply. For every other kind of question, answer normally and do not emit a directive.
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

    public static GoalListCard Build(IReadOnlyList<Goal> activeGoals)
    {
        var items = activeGoals
            .OrderBy(goal => goal.Position)
            .Select(goal => new GoalListCardItem(
                goal.Id.ToString(),
                goal.Title,
                goal.CurrentValue,
                goal.TargetValue,
                goal.Unit,
                goal.Deadline?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
            .ToList();
        return new GoalListCard(items);
    }

    [GeneratedRegex(@"\[\[orbit:goals\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex DirectiveRegex();
}
