using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentValidation;
using FluentValidation.Results;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Entities;

namespace Orbit.Application.Chat.Validators;

public sealed record HabitProposal(string Message, JsonElement Arguments);

public sealed partial class HabitProposalValidator : AbstractValidator<HabitProposal>
{
    public HabitProposalValidator()
    {
        RuleFor(proposal => proposal).Custom((proposal, context) =>
        {
            foreach (var (item, path) in Items(proposal.Arguments))
                ValidateItem(item, path, proposal.Message, context);
        });
    }

    private static void ValidateItem(JsonElement item, string path, string message,
        ValidationContext<HabitProposal> context)
    {
        var title = JsonArgumentParser.GetOptionalString(item, "title") ?? string.Empty;
        var description = JsonArgumentParser.GetOptionalString(item, "description") ?? string.Empty;
        var bad = JsonArgumentParser.GetOptionalBool(item, "is_bad_habit") == true;
        var polarity = HabitSemantics.ValidatePolarity(title, bad);
        if (polarity is not null)
            Reject("is_bad_habit", polarity.Code, polarity.Message);

        var frequency = JsonArgumentParser.ParseFrequencyUnit(item);
        if (frequency.IsFailure || frequency.Value is null)
            return;
        var source = string.IsNullOrWhiteSpace(message) ? title + " " + description : message;
        var bounded = BoundedPeriod().IsMatch(source);
        var repeated = Repetition().IsMatch(source);
        var end = JsonArgumentParser.ParseDateOnly(item, "end_date");
        if (bounded && (!end.HasValue || bad))
            Reject("end_date", "TEMPORARY_HABIT_REQUIRES_END", "Temporary preparation must be a positive Task or have an explicit event end date.");
        if (bounded && end is { } cutoff && MatchesReferencedEvent(source, cutoff) == false)
            Reject("end_date", "TEMPORARY_HABIT_EVENT_CUTOFF", "Temporary preparation must end on the referenced event date.");
        var datedInstruction = title.Length > 0 && source.StartsWith(title, StringComparison.OrdinalIgnoreCase)
            && SingleInstructionDate().IsMatch(source[title.Length..]);
        if (!repeated && HasDate().IsMatch(source) && (datedInstruction || SingleOccurrence().IsMatch(source)))
            Reject("frequency_unit", "DATED_INSTRUCTION_REQUIRES_TASK", "A single dated event or instruction is a one-time Task. Repetition requires an explicit request.");

        void Reject(string field, string code, string error) =>
            context.AddFailure(new ValidationFailure(path + field, error) { ErrorCode = code });
    }

    private static bool? MatchesReferencedEvent(string source, DateOnly date)
    {
        var references = EventReference().Matches(source).Select(match => match.Groups["date"].Value).Distinct().ToArray();
        var dates = EventDate().Matches(source).Select(match => match.Value).Distinct().ToArray();
        if (references.Length == 1)
            return int.TryParse(references[0], out var day)
                ? day == date.Day && (dates.Length == 0 || dates.Any(text => MatchesDate(text, date)))
                : MatchesDate(references[0], date);
        return references.Length == 0 && dates.Length == 1 ? MatchesDate(dates[0], date) : null;
    }

    private static bool MatchesDate(string text, DateOnly date) =>
        text == date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        || new[] { "d/M", "M/d", "d/M/yyyy", "M/d/yyyy" }.Any(format =>
            DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            && parsed.Month == date.Month && parsed.Day == date.Day
            && (text.Count(character => character == '/') < 2 || parsed.Year == date.Year));

    private static IEnumerable<(JsonElement Item, string Path)> Items(JsonElement arguments, string path = "")
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            yield break;
        if (arguments.TryGetProperty("habits", out var habits) && habits.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in habits.EnumerateArray())
                foreach (var nested in Items(item, $"{path}habits[{index++}]."))
                    yield return nested;
            yield break;
        }
        yield return (arguments, path);
        if (arguments.TryGetProperty("sub_habits", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in children.EnumerateArray())
                foreach (var nested in Items(child, $"{path}sub_habits[{index++}]."))
                    yield return nested;
        }
    }

    public static JsonElement LocalizeTextDates(JsonElement arguments, string? locale, string message = "")
    {
        var culture = CultureInfo.GetCultureInfo(string.IsNullOrWhiteSpace(locale) ? "en-US" : locale);
        var node = JsonNode.Parse(arguments.GetRawText());
        Visit(node);
        return JsonSerializer.SerializeToElement(node);

        void Visit(JsonNode? current)
        {
            if (current is JsonArray array)
                foreach (var child in array) Visit(child);
            if (current is not JsonObject obj)
                return;
            foreach (var (key, value) in obj.ToList())
            {
                if (key is "title" or "description" && value is JsonValue text && text.TryGetValue<string>(out var content))
                {
                    if (key == "title" && SourceTitle(content, message) is { } original)
                    {
                        obj[key] = original;
                        continue;
                    }
                    obj[key] = IsoDate().Replace(content, match =>
                        DateOnly.TryParseExact(match.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var date) ? date.ToString("d", culture) : match.Value);
                }
                else
                    Visit(value);
            }
        }
    }

    private static string? SourceTitle(string title, string message)
    {
        if (title.Length == 0) return null;
        var start = 0;
        while (message.IndexOf(title, start, StringComparison.OrdinalIgnoreCase) is var index && index >= 0)
        {
            var end = index + title.Length;
            if ((index == 0 || !char.IsLetterOrDigit(message[index - 1]))
                && (end == message.Length || !char.IsLetterOrDigit(message[end])))
                return message.Substring(index, title.Length);
            start = end;
        }
        return null;
    }

    [GeneratedRegex(@"\b(?:\d+|three|two|one|três|tres|dois|um)\s*(?:dias?|days?|horas?|hours?)\s*(?:antes|before)\b|\b(?:até|ate|until|before|antes)\s+(?:(?:o|a|the)\s+)?(?:exame|exam|test|coleta|collection)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BoundedPeriod();

    [GeneratedRegex(@"\b(?:todo\s+dia|todos\s+os\s+dias|diari[oa]|diariamente|semanal|mensal|anual|daily|weekly|monthly|yearly|every\s+(?:day|week|month|year|\d+\s+days)|cada\s+\d+\s+dias|(?:tod[oa]|every|each)\s+(?:(?:as|os)\s+)?(?:segunda|ter[çc]a|quarta|quinta|sexta|s[áa]bado|domingo|monday|tuesday|wednesday|thursday|friday|saturday|sunday)|[2-6][aª]s)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Repetition();

    internal static bool RequestsRepetition(string message) => Repetition().IsMatch(message);

    [GeneratedRegex(@"\b(?:one[ -]time|one[ -]off|uma\s+(?:[uú]nica|s[oó])\s+vez|uma\s+vez\s+s[oó])\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SingleOccurrence();

    [GeneratedRegex(@"\b(?:\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}(?:/\d{2,4})?|tomorrow|amanhã|amanha|hoje|today)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HasDate();

    [GeneratedRegex(@"^\s+(?:(?:on|dia|no\s+dia)\s+)?(?:\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}(?:/\d{2,4})?|tomorrow|amanhã|amanha|hoje|today)(?:\s+(?:at|às|as)\s+\d{1,2}(?::|h)\d{2})?[.!?]*\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SingleInstructionDate();

    [GeneratedRegex(@"\b(?:\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}(?:/\d{4})?)\b", RegexOptions.CultureInvariant)]
    private static partial Regex EventDate();

    [GeneratedRegex(@"\b(?:for|para|pro)\s+(?:(?:o|a|the)\s+)?(?:exame|exam|test)\s+(?:(?:do\s+dia|on|dia)\s+)?(?<date>\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}(?:/\d{4})?|\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EventReference();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();
}
