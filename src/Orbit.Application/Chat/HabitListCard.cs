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
    private const string ScopeRemaining = "remaining";

    public const string StatusToday = "today";
    public const string StatusOverdue = "overdue";
    public const string StatusGeneral = "general";
    public const string StatusNone = "none";
    public const string StatusDone = "done";

    public const string PromptInstruction = """
        ## Habit list rendering (this client)
        This app can display the user's habits as a live, interactive card. When the user asks to see or list their habits, or what is due, scheduled, left, or overdue (for example "what are my habits today", "show my habits", "list everything", "o que tenho pra hoje"), do NOT write the habits out as text and do NOT enumerate them. This rule overrides any earlier instruction to list habits from the index.
        Instead reply with a brief one-line intro and then, on its own final line, exactly ONE directive token:
        - [[orbit:habits:today]] - the user's habits due today, including ones already logged today, plus anything overdue.
        - [[orbit:habits:remaining]] - habits still due today or overdue, excluding every habit done today. Use this when the user asks what is left or remains.
        - [[orbit:habits:all]] - every active habit.
        The app replaces the directive with the rendered habit list, so never list the habits yourself when you emit a directive. Emit at most one directive, always as the last thing in your reply. For every other kind of question, answer normally and do not emit a directive.
        """;

    public static bool TryExtractScope(string? message, out string scope, out string stripped)
        => TryExtractScope(message, out scope, out stripped, out _);

    internal static bool TryExtractScope(string? message, out string scope, out string stripped, out bool remaining)
    {
        scope = ScopeAll;
        stripped = message ?? string.Empty;
        remaining = false;
        if (string.IsNullOrEmpty(message))
            return false;

        var match = DirectiveRegex().Match(message);
        if (!match.Success)
            return false;

        var directiveScope = match.Groups[1].Value;
        remaining = directiveScope.Equals(ScopeRemaining, StringComparison.OrdinalIgnoreCase);
        scope = remaining || directiveScope.Equals(ScopeToday, StringComparison.OrdinalIgnoreCase)
            ? ScopeToday : ScopeAll;
        stripped = DirectiveRegex().Replace(message, string.Empty).Trim();
        return true;
    }

    public static HabitListCard Build(
        IReadOnlyList<Habit> activeHabits,
        DateOnly today,
        string scope,
        bool supportsDoneStatus = false)
        => Build(activeHabits, today, scope, HabitTodaySnapshot.FromLoadedHabits(activeHabits, today), supportsDoneStatus);

    internal static HabitListCard Build(
        IReadOnlyList<Habit> activeHabits,
        DateOnly today,
        string scope,
        HabitTodaySnapshot todayFacts,
        bool supportsDoneStatus = false,
        bool remaining = false)
    {
        var includedIds = ResolveIncludedIds(activeHabits, scope, todayFacts, remaining);
        var items = new List<HabitListCardItem>();
        AppendLevel(activeHabits, parentId: null, depth: 0, today, todayFacts, includedIds, supportsDoneStatus, items);
        return new HabitListCard(scope, items);
    }

    private static HashSet<Guid> ResolveIncludedIds(
        IReadOnlyList<Habit> activeHabits,
        string scope,
        HabitTodaySnapshot todayFacts,
        bool remaining)
    {
        if (!scope.Equals(ScopeToday, StringComparison.OrdinalIgnoreCase))
            return activeHabits.Select(habit => habit.Id).ToHashSet();

        var byId = activeHabits.ToDictionary(habit => habit.Id);
        var included = new HashSet<Guid>();
        foreach (var habit in activeHabits.Where(habit =>
            (todayFacts.TodayIds.Contains(habit.Id) || todayFacts.OverdueIds.Contains(habit.Id))
            && (!remaining || !todayFacts.DoneTodayIds.Contains(habit.Id))))
        {
            var current = habit;
            while ((!remaining || !todayFacts.DoneTodayIds.Contains(current.Id))
                && included.Add(current.Id)
                && current.ParentHabitId is Guid parentId
                && byId.TryGetValue(parentId, out var parent))
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
        HabitTodaySnapshot todayFacts,
        HashSet<Guid> includedIds,
        bool supportsDoneStatus,
        List<HabitListCardItem> items)
    {
        var children = activeHabits
            .Where(habit => includedIds.Contains(habit.Id)
                && (parentId is null
                    ? habit.ParentHabitId is null || !includedIds.Contains(habit.ParentHabitId.Value)
                    : habit.ParentHabitId == parentId))
            .OrderBy(habit => habit.Position);

        foreach (var habit in children)
        {
            items.Add(new HabitListCardItem(
                habit.Id.ToString(),
                habit.Title,
                string.IsNullOrWhiteSpace(habit.Emoji) ? null : habit.Emoji,
                depth,
                habit.IsBadHabit,
                ResolveStatus(habit, today, todayFacts, supportsDoneStatus)));
            AppendLevel(activeHabits, habit.Id, depth + 1, today, todayFacts, includedIds, supportsDoneStatus, items);
        }
    }

    private static string ResolveStatus(
        Habit habit,
        DateOnly today,
        HabitTodaySnapshot todayFacts,
        bool supportsDoneStatus)
    {
        if (!supportsDoneStatus)
        {
            if (habit.IsGeneral)
                return StatusGeneral;
            if (habit.DueDate < today)
                return StatusOverdue;
            return habit.DueDate == today ? StatusToday : StatusNone;
        }

        if (todayFacts.DoneTodayIds.Contains(habit.Id))
            return StatusDone;
        if (habit.IsGeneral)
            return StatusGeneral;
        if (todayFacts.OverdueIds.Contains(habit.Id))
            return StatusOverdue;
        if (todayFacts.TodayIds.Contains(habit.Id))
            return StatusToday;
        return StatusNone;
    }

    [GeneratedRegex(@"\[\[orbit:habits:(today|remaining|all)\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex DirectiveRegex();
}
