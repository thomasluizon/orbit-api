using System.Text.Json.Serialization;

namespace Orbit.Domain.Models;

public sealed record AstraQuestionOption(string Id, string Label,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Recommended = false);

public sealed record AstraQuestionProgress(int Current, int Total);

public sealed record AstraQuestion(
    string Id, string Type, string Prompt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AstraQuestionOption>? Options = null,
    bool AllowFreeText = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LowLabel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HighLabel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Min = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Max = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Step = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unit = null);

public sealed record AstraQuestionAnswer(
    string QuestionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? SelectedIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Number = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Boolean = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FreeText = null);

public sealed record AstraQuestionBlock(Guid BlockId, IReadOnlyList<AstraQuestion> Questions, DateTime ExpiresAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Intro = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AstraQuestionProgress? Progress = null);

public static class AstraQuestionRules
{
    public static bool IsValidDefinition(AstraBlockDefinition definition) =>
        definition.Title.Length <= 120 && (definition.Intro is null || Text(definition.Intro, 500))
        && definition.Options.Count == 0 && definition.Items.Count == 0 && definition.Action is null
        && !definition.MultiSelect && !definition.AllowFreeText
        && (definition.Progress is null || definition.Progress.Current >= 0
            && definition.Progress.Total >= 1 && definition.Progress.Total >= definition.Progress.Current)
        && definition.Questions is { Count: >= 1 and <= 10 } questions
        && questions.All(IsValidQuestion)
        && questions.Select(question => question.Id).Distinct(StringComparer.Ordinal).Count() == questions.Count;

    private static bool IsValidQuestion(AstraQuestion question)
    {
        if (question is null || !Text(question.Id, 80) || !Text(question.Prompt, 500)) return false;
        var choice = question.Type is "singleChoice" or "multipleChoice";
        if (choice)
        {
            if (!ValidOptions(question)) return false;
        }
        else if (question.Options is not null || question.AllowFreeText && question.Type != "yesNo") return false;
        if (question.Type != "scale" && (question.LowLabel is not null || question.HighLabel is not null)) return false;
        if (question.Type != "slider" && (question.Min is not null || question.Max is not null
            || question.Step is not null || question.Unit is not null)) return false;
        return question.Type switch
        {
            "singleChoice" or "multipleChoice" or "yesNo" => true,
            "scale" => Text(question.LowLabel, 120) && Text(question.HighLabel, 120),
            "slider" => ValidSlider(question),
            _ => false
        };
    }

    private static bool ValidOptions(AstraQuestion question) => question.Options is { Count: >= 2 and <= 10 } options
        && options.All(option => option is not null && Text(option.Id, 80) && Text(option.Label, 120))
        && options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() == options.Count
        && options.Count(option => option.Recommended) <= (question.Type == "singleChoice" ? 1 : 0);

    private static bool ValidSlider(AstraQuestion question) => question.Min is >= -1000000000 and <= 1000000000
        && question.Max is >= -1000000000 and <= 1000000000 && question.Max > question.Min
        && question.Step is > 0 && question.Step <= question.Max - question.Min
        && (question.Max - question.Min) % question.Step == 0 && Text(question.Unit, 24);

    public static bool IsValidAnswerShape(AstraQuestionAnswer answer) => answer is not null
        && Text(answer.QuestionId, 80)
        && (answer.FreeText is null || Text(answer.FreeText, 500))
        && (answer.SelectedIds is null || answer.SelectedIds.Count <= 10
            && answer.SelectedIds.All(id => Text(id, 80))
            && answer.SelectedIds.Distinct(StringComparer.Ordinal).Count() == answer.SelectedIds.Count);

    public static bool IsValidAnswer(AstraBlockDefinition definition, AstraBlockAnswer answer)
    {
        if (answer.SelectedIds.Count != 0 || answer.FreeText is not null
            || answer.Answers is not { } answers || answers.Count != definition.Questions!.Count
            || answers.Any(value => !IsValidAnswerShape(value))
            || answers.Select(value => value.QuestionId).Distinct(StringComparer.Ordinal).Count() != answers.Count) return false;
        return definition.Questions.All(question => ValidValue(question, answers.SingleOrDefault(value => value.QuestionId == question.Id)));
    }

    private static bool ValidValue(AstraQuestion question, AstraQuestionAnswer? value)
    {
        if (value is null) return false;
        var selected = value.SelectedIds ?? [];
        var custom = !string.IsNullOrWhiteSpace(value.FreeText);
        if (custom && !question.AllowFreeText) return false;
        return question.Type switch
        {
            "singleChoice" or "multipleChoice" => value.Number is null && value.Boolean is null
                && selected.All(id => question.Options!.Any(option => option.Id == id))
                && (question.Type == "multipleChoice" ? selected.Count > 0 || custom : selected.Count + (custom ? 1 : 0) == 1),
            "scale" => selected.Count == 0 && !custom && value.Boolean is null
                && value.Number is >= 1 and <= 5 && value.Number % 1 == 0,
            "slider" => selected.Count == 0 && !custom && value.Boolean is null
                && value.Number >= question.Min && value.Number <= question.Max
                && (value.Number - question.Min) % question.Step == 0,
            "yesNo" => selected.Count == 0 && value.Number is null && (value.Boolean.HasValue != custom),
            _ => false
        };
    }

    private static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
}
