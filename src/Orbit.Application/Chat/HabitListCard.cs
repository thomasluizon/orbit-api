using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Chat;

public record HabitListCard(string Scope, IReadOnlyList<HabitListCardItem> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateOnly? Date = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Orbit.Domain.Models.ReadBlockMetadata? Read = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TotalCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceRevision = null) : ReadBlockCard
{
    [JsonIgnore]
    public IReadOnlySet<string> SkippedIds { get; init; } = new HashSet<string>();

    [JsonIgnore]
    public HabitSelectionCandidates? Selection { get; init; }
}

public sealed record HabitSelectionCandidates(string? Reference, IReadOnlyList<HabitListCardItem> Candidates, bool RequiresAnswer);

public record HabitListCardItem(
    string Id,
    string Title,
    string? Emoji,
    int Depth,
    bool IsBadHabit,
    string Status,
    DateOnly? StartDate = null,
    Guid? ParentHabitId = null,
    string? ParentTitle = null,
    IReadOnlyList<Orbit.Application.Habits.Queries.HabitTagItem>? Tags = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsSkippedOnDate = null);

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

    public const string PartsPromptInstruction = """
        ## Habit list rendering (this client)
        When the user asks to see or list habits, or what is due, scheduled, left, or overdue, use a habit list block instead of enumerating habits in text. This overrides earlier instructions to list habits from the index. Place the matching directive on its own line where the block belongs in the reply:
        - [[orbit:habits:today]] for habits due today, including those logged today, plus overdue habits.
        - [[orbit:habits:remaining]] for habits still due today or overdue, excluding habits done today.
        - [[orbit:habits:all]] for every active habit.
        Never repeat the block's habits or figures in text.
        """;

    public const string PromptInstruction = """
        ## Habit list rendering (this client)
        This app can display the user's habits as a live, interactive card. When the user asks to see or list their habits, or what is due, scheduled, left, or overdue (for example "what are my habits today", "show my habits", "list everything", "o que tenho pra hoje"), do NOT write the habits out as text and do NOT enumerate them. This rule overrides any earlier instruction to list habits from the index.
        Instead reply with a brief one-line intro and then, on its own line, exactly ONE directive token:
        - [[orbit:habits:today]] - the user's habits due today, including ones already logged today, plus anything overdue.
        - [[orbit:habits:remaining]] - habits still due today or overdue, excluding every habit done today. Use this when the user asks what is left or remains.
        - [[orbit:habits:all]] - every active habit.
        The app replaces the directive with the rendered habit list, so never list the habits yourself when you emit a directive. Emit at most one directive, after the prose and before optional follow-ups. For every other kind of question, answer normally and do not emit a directive.
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
                ResolveStatus(habit, today, todayFacts, supportsDoneStatus),
                habit.StartDate, habit.ParentHabitId, activeHabits.FirstOrDefault(parent => parent.Id == habit.ParentHabitId)?.Title,
                habit.Tags.Select(t => new Orbit.Application.Habits.Queries.HabitTagItem(t.Id, t.Name, t.Color)).ToList()));
            AppendLevel(activeHabits, habit.Id, depth + 1, today, todayFacts, includedIds, supportsDoneStatus, items);
        }
    }

    private static string ResolveStatus(
        Habit habit,
        DateOnly today,
        HabitTodaySnapshot todayFacts,
        bool supportsDoneStatus)
    {
        if (habit.Schedule == HabitSchedule.General && habit.IsCompleted)
            return supportsDoneStatus ? StatusDone : StatusNone;

        if (!supportsDoneStatus)
        {
            if (habit.IsCompleted && todayFacts.DoneTodayIds.Contains(habit.Id))
                return StatusNone;
            if (habit.Schedule == HabitSchedule.General)
                return StatusGeneral;
            if (habit.NextDueDate < today)
                return StatusOverdue;
            return habit.NextDueDate == today ? StatusToday : StatusNone;
        }

        if (todayFacts.DoneTodayIds.Contains(habit.Id))
            return StatusDone;
        if (habit.Schedule == HabitSchedule.General)
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
