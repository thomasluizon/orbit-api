using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Orbit.Application.Common;

namespace Orbit.Application.Chat;

internal static partial class RoutineCreationProposal
{
    public static JsonElement? Read(string message)
    {
        var match = RoutineRequest().Match(message);
        if (!match.Success) return null;
        var header = match.Groups["header"].Value;
        var body = RoutineSuffix().Replace(match.Groups["steps"].Value, string.Empty).Trim().TrimEnd('.');
        var normalized = HabitNameMatcher.Normalize(header);
        var evening = Evening().IsMatch(normalized);
        var morning = Morning().IsMatch(normalized);
        if (evening == morning || UnsupportedDetails().IsMatch(body)) return null;
        var portuguese = Portuguese().IsMatch(normalized);
        var parent = new JsonObject
        {
            ["title"] = (portuguese, evening) switch
            {
                (true, true) => "Rotina da noite",
                (true, false) => "Rotina da manhã",
                (false, true) => "Evening routine",
                _ => "Morning routine"
            },
            ["frequency_unit"] = "Day",
            ["frequency_quantity"] = 1
        };
        if (!ApplyTime(parent, ref header, evening)
            || !CreationHeader().IsMatch(HabitNameMatcher.Normalize(header))) return null;
        var weekdays = Weekdays().IsMatch(normalized);
        if (weekdays)
            parent["days"] = JsonSerializer.SerializeToNode(new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" });
        var steps = StepSeparator().Split(body);
        if (steps.Length < 2 || steps.Length > AppConstants.MaxSubHabits) return null;
        var children = new JsonArray();
        foreach (var step in steps)
        {
            var title = step.Trim();
            var child = new JsonObject { ["frequency_unit"] = "Day", ["frequency_quantity"] = 1 };
            if (weekdays) child["days"] = parent["days"]!.DeepClone();
            if (!ApplyTime(child, ref title, evening) || title.Length == 0 || UnparsedTime().IsMatch(title)) return null;
            child["title"] = title;
            children.Add(child);
        }
        parent["sub_habits"] = children;
        return JsonSerializer.SerializeToElement(parent);
    }

    private static bool ApplyTime(JsonObject item, ref string text, bool evening)
    {
        var match = ScheduledTime().Match(text);
        if (!match.Success) return true;
        var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups["minute"].Success
            ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
        var meridiem = match.Groups["meridiem"].Value.ToLowerInvariant();
        var uses24Hours = match.Groups["separator"].Value == "h" || hour > 12;
        if (minute > 59 || hour > 23 || meridiem.Length > 0 && (hour < 1 || hour > 12)) return false;
        if (meridiem == "pm" && hour < 12 || meridiem.Length == 0 && !uses24Hours && evening && hour is > 0 and < 12)
            hour += 12;
        if (meridiem == "am" && hour == 12) hour = 0;
        item["due_time"] = new TimeOnly(hour, minute).ToString("HH:mm", CultureInfo.InvariantCulture);
        text = text.Remove(match.Index, match.Length).Trim();
        return true;
    }

    [GeneratedRegex(@"^\s*(?<header>[^\r\n?;]+?):\s+(?<steps>[^\r\n?;]+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex RoutineRequest();

    [GeneratedRegex(@"\s*,?\s+(?:as (?:one|an?) (?:morning|evening) routine|como uma rotina (?:da noite|da manh[ãa]))\s*[.!]?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RoutineSuffix();

    [GeneratedRegex(@"^(?:(?:build me|create|make)(?: a)?(?: calm)? (?:morning|evening) routine(?: on weekdays)?|(?:crie|monte) uma rotina(?: tranquila)? (?:da noite|da manha)(?: nos dias uteis)?|(?:every|each) (?:morning|evening)|(?:toda|todas as) (?:noite|manha|noites|manhas))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreationHeader();

    [GeneratedRegex(@"\b(?:evening|noite|noites)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Evening();

    [GeneratedRegex(@"\b(?:morning|manha|manhas)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Morning();

    [GeneratedRegex(@"\b(?:crie|monte|toda|todas)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Portuguese();

    [GeneratedRegex(@"\b(?:weekdays|dias uteis)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Weekdays();

    [GeneratedRegex(@"\s*,\s*(?:(?:and|e)\s+)?|\s+(?:and|e)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StepSeparator();

    [GeneratedRegex(@"\s+(?:at|by|às|as|at[eé](?:\s+[aà]s)?)\s+(?<hour>\d{1,2})(?:(?<separator>:|h)(?<minute>\d{2})?)?\s*(?<meridiem>am|pm)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScheduledTime();

    [GeneratedRegex(@"\d\s*(?:[:h]|am\b|pm\b)|\b(?:at|às|by|até)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnparsedTime();

    [GeneratedRegex(@"[\r\n?;\""/]|\b(?:tomorrow|today|amanh[ãa]|hoje|once|weekly|weekends?|weeks?|days?|dias?|hours?|horas?|vezes|times|during|durante|only|apenas|s[oó]|until|before|after|semanas?|antes|depois|monday|tuesday|wednesday|thursday|friday|saturday|sunday|segunda|ter[çc]a|quarta|quinta|sexta|s[áa]bado|domingo|lembre|remind|reminder|tag|goal|meta|checklist|emoji|icon|[ií]cone|instead|rather|troque|mude|altere|replace|update|change|delete|remove)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnsupportedDetails();
}
