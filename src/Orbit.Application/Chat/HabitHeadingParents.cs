using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Orbit.Domain.Entities;

namespace Orbit.Application.Chat;

internal static partial class HabitHeadingParents
{
    internal const string CandidateIdsField = "preview_parent_candidate_ids";

    internal sealed record Heading(string Name, string Body, IReadOnlyList<Habit> Candidates);

    public static IReadOnlyList<Heading> Read(string message, IReadOnlyList<Habit> habits)
    {
        var matches = Separator().Matches(message);
        return matches.Select((match, index) =>
        {
            var name = match.Groups["heading"].Value.Trim();
            var end = index + 1 < matches.Count ? matches[index + 1].Index : message.Length;
            var body = message[(match.Index + match.Length)..end];
            return new Heading(name, body, Candidates(name, habits));
        }).ToArray();
    }

    public static Heading? ForItem(JsonObject item, IReadOnlyList<Heading> headings)
    {
        var title = HabitNameMatcher.Normalize(item["title"]?.ToString() ?? string.Empty);
        if (title.Length == 0) return null;
        var matches = headings.Where(heading => HabitNameMatcher.Normalize(heading.Body).Contains(title, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static bool Bind(JsonObject item, Heading heading, bool removeInventedDescription)
    {
        item.Remove(CandidateIdsField);
        if (heading.Candidates.Count == 0)
            item.Remove("parent_habit_id");
        else
            item["parent_habit_id"] = heading.Candidates.Count == 1
                ? heading.Candidates[0].Id.ToString() : heading.Name;
        if (heading.Candidates.Count > 1)
            item[CandidateIdsField] = new JsonArray(heading.Candidates.OrderBy(candidate => candidate.Id)
                .Select(candidate => (JsonNode?)JsonValue.Create(candidate.Id.ToString())).ToArray());
        if (removeInventedDescription && item["description"] is { } description
            && !HabitNameMatcher.Normalize(heading.Body).Contains(HabitNameMatcher.Normalize(description.ToString()), StringComparison.Ordinal))
            item.Remove("description");
        return heading.Candidates.Count == 1;
    }

    internal static IReadOnlyList<Habit> Candidates(string name, IReadOnlyList<Habit> habits)
    {
        var exact = habits.Where(habit => HabitNameMatcher.Normalize(habit.Title) == HabitNameMatcher.Normalize(name)).ToArray();
        return exact.Length > 0 ? exact : habits.Where(habit => CloseName(habit.Title, name)).ToArray();
    }

    private static bool CloseName(string left, string right)
    {
        var a = HabitNameMatcher.Normalize(left);
        var b = HabitNameMatcher.Normalize(right);
        if (Math.Min(a.Length, b.Length) < 5 || Math.Abs(a.Length - b.Length) > 1) return false;
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var next = new int[b.Length + 1];
            next[0] = i;
            for (var j = 1; j <= b.Length; j++)
                next[j] = Math.Min(Math.Min(next[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = next;
        }
        return previous[b.Length] <= 1;
    }

    [GeneratedRegex(@"(?<heading>[\p{L}\p{N}][\p{L}\p{N} \t]*?)\s*(?!(?<=\p{N})[:/](?=\p{N}))(?:[:=|>/\u2192\uFF1A]+|[\-\u2013\u2014]+(?:>+|(?=\s)))\s*", RegexOptions.CultureInvariant)]
    private static partial Regex Separator();
}
