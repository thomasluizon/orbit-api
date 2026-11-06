using System.Text.Json;
using System.Text.RegularExpressions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Chat;

/// <summary>
/// Server matches between the message and the owned records in <c>offer_account_facts</c>. The model gets
/// the fitting owned parent and the active goals as explicit candidates, not only as a rule to apply.
/// </summary>
internal static partial class ProactiveSuggestionCandidates
{
    internal const string Instruction = "offer_suggestion_candidates holds the server matches for this message from offer_account_facts. "
        + "For a create, set the single ParentHabitIds candidate as parent_habit_id and every GoalIds candidate in goal_ids, and list each field in suggested_fields, unless the person names another placement or link. "
        + "Several parent candidates stay one preview choice. When RepeatThroughoutDay is true, propose a repeat_reminder in the preview. "
        + "When SupportedDefault is true, prepare the preview with these defaults and never ask a question in its place.";

    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
    {
        "daily", "diaria", "diario", "habit", "habito", "habitos", "habits", "goal", "goals", "meta", "metas",
        "create", "crie", "criar", "adicione", "minute", "minutes", "minuto", "minutos", "session", "sessao",
        "every", "todo", "todos", "before", "after", "antes", "depois", "post", "with", "para", "uma", "that", "this", "your", "minha", "meu"
    };

    private static readonly string[][] TimeOfDay =
    [
        ["night", "nightly", "tonight", "evening", "bed", "bedtime", "sleep", "noite", "noturno", "noturna", "dormir", "deitar", "cama"],
        ["morning", "wake", "breakfast", "manha", "matinal", "acordar"],
        ["lunch", "noon", "almoco"]
    ];

    internal sealed record Candidates(IReadOnlyList<Guid> ParentHabitIds, IReadOnlyList<Guid> GoalIds, bool RepeatThroughoutDay)
    {
        public bool SupportedDefault => ParentHabitIds.Count == 1 || GoalIds.Count > 0 || RepeatThroughoutDay;
    }

    internal static Candidates For(string message, IEnumerable<Habit> habits, IEnumerable<Goal> goals)
    {
        var words = Words(message);
        var active = habits.Where(habit => !habit.IsDeleted && !habit.IsCompleted).ToArray();
        var parents = RootPlacement().IsMatch(message) ? [] : active
            .Where(habit => habit.ParentHabitId is null && active.Any(child => child.ParentHabitId == habit.Id))
            .Where(habit => SharesWord(words, Words(habit.Title)) || SharesTimeOfDay(words, Words(habit.Title)))
            .Select(habit => habit.Id).ToArray();
        var linked = goals.Where(goal => goal.Status == GoalStatus.Active && !goal.IsDeleted)
            .Where(goal => SharesWord(words, Words(goal.Title))).Select(goal => goal.Id).ToArray();
        return new Candidates(parents, linked, ThroughoutDay().IsMatch(message));
    }

    internal static string Serialize(Candidates candidates) => JsonSerializer.Serialize(new
    {
        offer_suggestion_candidates = new
        {
            candidates.ParentHabitIds,
            candidates.GoalIds,
            candidates.RepeatThroughoutDay,
            candidates.SupportedDefault
        }
    });

    internal static string[] Words(string value) => HabitNameMatcher.Normalize(value)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(word => word.Length >= 3 && !CommonWords.Contains(word)).ToArray();

    private static bool SharesWord(IReadOnlyCollection<string> message, IEnumerable<string> title) =>
        title.Any(word => word.Length >= 4 && message.Contains(word, StringComparer.Ordinal));

    private static bool SharesTimeOfDay(IReadOnlyCollection<string> message, IReadOnlyCollection<string> title) =>
        TimeOfDay.Any(bucket => bucket.Any(word => message.Contains(word, StringComparer.Ordinal))
            && bucket.Any(word => title.Contains(word, StringComparer.Ordinal)));

    [GeneratedRegex(@"\b(?:top[ -]level|without\s+a\s+parent|no\s+parent|at\s+the\s+root|sem\s+pai|n[íi]vel\s+principal|sem\s+rotina)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RootPlacement();

    [GeneratedRegex(@"\b(?:throughout\s+the\s+day|all\s+day(?:\s+long)?|during\s+the\s+day|ao\s+longo\s+do\s+dia|durante\s+o\s+dia|o\s+dia\s+(?:todo|inteiro))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ThroughoutDay();
}
