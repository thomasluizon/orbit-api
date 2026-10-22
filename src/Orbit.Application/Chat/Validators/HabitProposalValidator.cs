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
        var start = JsonArgumentParser.ParseDateOnly(item, "due_date");
        if (bounded && (!end.HasValue || bad))
            Reject("end_date", "TEMPORARY_HABIT_REQUIRES_END", "Temporary preparation must be a positive Task or have an explicit event end date.");
        if (bounded && end is { } cutoff && EventDate().Matches(source).LastOrDefault() is { } reference
            && !MatchesDate(reference.Value, cutoff))
            Reject("end_date", "TEMPORARY_HABIT_EVENT_CUTOFF", "Temporary preparation must end on the referenced event date.");
        var datedEvent = EventName().IsMatch(title) && !HabitSemantics.HasNegatedName(title)
            && !BoundedPeriod().IsMatch(title + " " + description);
        if (!repeated && HasDate().IsMatch(source) && (datedEvent || !bounded || !end.HasValue || start >= end))
            Reject("frequency_unit", "DATED_INSTRUCTION_REQUIRES_TASK", "A single dated event or instruction is a one-time Task. Repetition requires an explicit request.");

        void Reject(string field, string code, string error) =>
            context.AddFailure(new ValidationFailure(path + field, error) { ErrorCode = code });
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

    public static JsonElement LocalizeTextDates(JsonElement arguments, string? locale)
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
                    obj[key] = IsoDate().Replace(content, match =>
                        DateOnly.TryParseExact(match.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var date) ? date.ToString("d", culture) : match.Value);
                else
                    Visit(value);
            }
        }
    }

    [GeneratedRegex(@"\b(?:\d+|three|two|one|três|tres|dois|um)\s*(?:dias?|days?|horas?|hours?)\s*(?:antes|before)\b|\b(?:até|ate|until|before|antes)\s+(?:(?:o|a|the)\s+)?(?:exame|exam|test|coleta|collection)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BoundedPeriod();

    [GeneratedRegex(@"\b(?:todo\s+dia|todos\s+os\s+dias|diari[oa]|diariamente|semanal|mensal|anual|daily|weekly|monthly|yearly|every\s+(?:day|week|month|year|\d+\s+days)|cada\s+\d+\s+dias)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Repetition();

    internal static bool RequestsRepetition(string message) => Repetition().IsMatch(message);

    [GeneratedRegex(@"\b(?:exames?|exams?|blood\s+tests?|appointments?|consultas?|dentista|dentist|meetings?|reuni[ãa]o|flights?|voos?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EventName();

    [GeneratedRegex(@"\b(?:\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}(?:/\d{2,4})?|tomorrow|amanhã|amanha|hoje|today)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HasDate();

    [GeneratedRegex(@"\b(?:\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}(?:/\d{4})?)\b", RegexOptions.CultureInvariant)]
    private static partial Regex EventDate();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();
}
