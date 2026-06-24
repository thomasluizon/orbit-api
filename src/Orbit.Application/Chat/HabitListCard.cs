using System.Text.RegularExpressions;
using Orbit.Domain.Entities;

namespace Orbit.Application.Chat;

public record HabitListCard(string Scope, IReadOnlyList<HabitListCardItem> Items);

public record HabitListCardItem(
    string Id,
    string Title,
    string? Emoji,
    int Depth,
    bool IsBadHabit,
    string Status);

public static partial class HabitListCardBuilder
{
    public const string ScopeToday = "today";
    public const string ScopeAll = "all";

    public const string StatusToday = "today";
    public const string StatusOverdue = "overdue";
    public const string StatusGeneral = "general";
    public const string StatusNone = "none";

    public const string PromptInstruction = """
        ## Habit list rendering (this client)
        This app can display the user's habits as a live, interactive card. When the user asks to see or list their habits, or what is due, scheduled, left, or overdue (for example "what are my habits today", "show my habits", "list everything", "o que tenho pra hoje"), do NOT write the habits out as text and do NOT enumerate them. This rule overrides any earlier instruction to list habits from the index.
        Instead reply with a brief one-line intro and then, on its own final line, exactly ONE directive token:
        - [[orbit:habits:today]] - the user's habits due today plus anything overdue.
        - [[orbit:habits:all]] - every active habit.
        The app replaces the directive with the rendered habit list, so never list the habits yourself when you emit a directive. Emit at most one directive, always as the last thing in your reply. For every other kind of question, answer normally and do not emit a directive.
        """;

    public static bool TryExtractScope(string? message, out string scope, out string stripped)
    {
        scope = ScopeAll;
        stripped = message ?? string.Empty;
        if (string.IsNullOrEmpty(message))
            return false;

        var match = DirectiveRegex().Match(message);
        if (!match.Success)
            return false;

        scope = match.Groups[1].Value.Equals(ScopeToday, StringComparison.OrdinalIgnoreCase)
            ? ScopeToday
            : ScopeAll;
        stripped = DirectiveRegex().Replace(message, string.Empty).Trim();
        return true;
    }

    public static HabitListCard Build(IReadOnlyList<Habit> activeHabits, DateOnly today, string scope)
    {
        var includedIds = ResolveIncludedIds(activeHabits, today, scope);
        var items = new List<HabitListCardItem>();
        AppendLevel(activeHabits, parentId: null, depth: 0, today, includedIds, items);
        return new HabitListCard(scope, items);
    }

    private static HashSet<Guid> ResolveIncludedIds(IReadOnlyList<Habit> activeHabits, DateOnly today, string scope)
    {
        if (!scope.Equals(ScopeToday, StringComparison.OrdinalIgnoreCase))
            return activeHabits.Select(habit => habit.Id).ToHashSet();

        var byId = activeHabits.ToDictionary(habit => habit.Id);
        var included = new HashSet<Guid>();
        foreach (var habit in activeHabits.Where(habit => !habit.IsGeneral && habit.DueDate <= today))
        {
            var current = habit;
            while (included.Add(current.Id) &&
                   current.ParentHabitId is Guid parentId &&
                   byId.TryGetValue(parentId, out var parent))
            {
                current = parent;
            }
        }

        return included;
    }

    private static void AppendLevel(
        IReadOnlyList<Habit> activeHabits,
        Guid? parentId,
        int depth,
        DateOnly today,
        HashSet<Guid> includedIds,
        List<HabitListCardItem> items)
    {
        var children = activeHabits
            .Where(habit => habit.ParentHabitId == parentId && includedIds.Contains(habit.Id))
            .OrderBy(habit => habit.Position);

        foreach (var habit in children)
        {
            items.Add(new HabitListCardItem(
                habit.Id.ToString(),
                habit.Title,
                string.IsNullOrWhiteSpace(habit.Emoji) ? null : habit.Emoji,
                depth,
                habit.IsBadHabit,
                ResolveStatus(habit, today)));
            AppendLevel(activeHabits, habit.Id, depth + 1, today, includedIds, items);
        }
    }

    private static string ResolveStatus(Habit habit, DateOnly today)
    {
        if (habit.IsGeneral)
            return StatusGeneral;
        if (habit.DueDate < today)
            return StatusOverdue;
        if (habit.DueDate == today)
            return StatusToday;
        return StatusNone;
    }

    [GeneratedRegex(@"\[\[orbit:habits:(today|all)\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex DirectiveRegex();
}
