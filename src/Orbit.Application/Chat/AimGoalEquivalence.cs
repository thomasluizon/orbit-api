using System.Globalization;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Chat;

/// <summary>
/// Compares the aim in the message with the active goals on the server. A goal is equivalent when the
/// message holds every word of its normalized title, or when the message names its target with its unit.
/// </summary>
internal static class AimGoalEquivalence
{
    private static readonly Dictionary<string, string> NumberWords = new(StringComparer.Ordinal)
    {
        ["one"] = "1",
        ["um"] = "1",
        ["uma"] = "1",
        ["two"] = "2",
        ["dois"] = "2",
        ["duas"] = "2",
        ["three"] = "3",
        ["tres"] = "3",
        ["four"] = "4",
        ["quatro"] = "4",
        ["five"] = "5",
        ["cinco"] = "5",
        ["six"] = "6",
        ["seis"] = "6",
        ["seven"] = "7",
        ["sete"] = "7",
        ["eight"] = "8",
        ["oito"] = "8",
        ["nine"] = "9",
        ["nove"] = "9",
        ["ten"] = "10",
        ["dez"] = "10"
    };

    private static readonly string[][] UnitWords =
    [
        ["km", "kms", "kilometer", "kilometers", "kilometre", "kilometres", "quilometro", "quilometros"],
        ["mi", "mile", "miles", "milha", "milhas"],
        ["kg", "kgs", "kilo", "kilos", "quilo", "quilos"],
        ["book", "books", "livro", "livros"]
    ];

    public static bool HasEquivalentActiveGoal(string? message, IEnumerable<Goal> goals)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var words = Tokens(message);
        return goals.Where(goal => goal.Status == GoalStatus.Active && !goal.IsDeleted)
            .Any(goal => SharesTitle(words, goal.Title) || SharesTarget(words, goal));
    }

    private static string[] Tokens(string value) => HabitNameMatcher.Normalize(value)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(word => NumberWords.GetValueOrDefault(word, word)).ToArray();

    private static bool SharesTitle(IReadOnlyCollection<string> message, string title)
    {
        var words = Tokens(title).Where(word => word.Length >= 2).ToArray();
        return words.Length >= 2 && words.All(word => message.Contains(word, StringComparer.Ordinal));
    }

    private static bool SharesTarget(IReadOnlyCollection<string> message, Goal goal)
    {
        if (string.IsNullOrWhiteSpace(goal.Unit)) return false;
        var target = goal.TargetValue.ToString("0.##", CultureInfo.InvariantCulture);
        if (!message.Contains(target, StringComparer.Ordinal)) return false;
        var unit = HabitNameMatcher.Normalize(goal.Unit);
        var aliases = UnitWords.FirstOrDefault(group => group.Contains(unit, StringComparer.Ordinal)) ?? [unit];
        return aliases.Any(alias => message.Contains(alias, StringComparer.Ordinal));
    }
}
