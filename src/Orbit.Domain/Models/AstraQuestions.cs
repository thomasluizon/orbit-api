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
    internal static string? DefinitionError(AstraBlockDefinition definition)
    {
        if (definition.Title.Length > 120) return "title must have at most 120 characters.";
        if (definition.Intro is not null && !Text(definition.Intro, 500)) return "intro must be nonempty and have at most 500 characters.";
        if (definition.Options.Count != 0 || definition.Items.Count != 0 || definition.Action is not null)
            return "question requires options, items and action to be omitted or null at the top level.";
        if (definition.MultiSelect || definition.AllowFreeText)
            return "question requires multiSelect=false and per-question allowFreeText.";
        if (definition.Progress is { } progress && (progress.Current < 0 || progress.Total < 1 || progress.Current > progress.Total))
            return "progress requires 0 <= current <= total and total >= 1.";
        if (definition.Questions is not { Count: >= 1 and <= 10 } questions)
            return "questions must contain 1 to 10 questions for form question.";
        for (var index = 0; index < questions.Count; index++)
            if (QuestionError(questions[index]) is { } error) return $"questions[{index}].{error}";
        if (questions.Select(question => question.Id).Distinct(StringComparer.Ordinal).Count() != questions.Count)
            return "questions must have unique ids.";
        return null;
    }

    private static string? QuestionError(AstraQuestion question)
    {
        if (question is null) return "question must be an object.";
        if (!Text(question.Id, 80)) return "id must be nonempty and have at most 80 characters.";
        if (!Text(question.Prompt, 500)) return "prompt must be nonempty and have at most 500 characters.";
        var choice = question.Type is "singleChoice" or "multipleChoice";
        if (choice)
        {
            if (OptionsError(question) is { } error) return error;
        }
        else if (question.Options is not null) return "options must be omitted or null for scale, slider or yesNo.";
        if (question.AllowFreeText && !choice && question.Type != "yesNo")
            return "allowFreeText must be false or null for scale and slider.";
        if (question.Type != "scale" && (question.LowLabel is not null || question.HighLabel is not null))
            return "lowLabel and highLabel are only allowed for scale.";
        if (question.Type != "slider" && (question.Min is not null || question.Max is not null || question.Step is not null || question.Unit is not null))
            return "min, max, step and unit are only allowed for slider.";
        return question.Type switch
        {
            "singleChoice" or "multipleChoice" or "yesNo" => null,
            "scale" => Text(question.LowLabel, 120) && Text(question.HighLabel, 120)
                ? null : "lowLabel and highLabel must be nonempty and have at most 120 characters for scale.",
            "slider" => SliderError(question),
            _ => "type must be singleChoice, multipleChoice, scale, slider or yesNo."
        };
    }

    private static string? OptionsError(AstraQuestion question)
    {
        if (question.Options is not { Count: >= 2 and <= 10 } options)
            return "options must contain 2 to 10 options for singleChoice or multipleChoice, even with allowFreeText=true.";
        if (options.Any(option => option is null || !Text(option.Id, 80) || !Text(option.Label, 120)))
            return "options require nonempty ids up to 80 characters and labels up to 120 characters.";
        if (options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() != options.Count)
            return "options must have unique ids.";
        return options.Count(option => option.Recommended) <= (question.Type == "singleChoice" ? 1 : 0)
            ? null : "options allow at most one recommended option for singleChoice and none for multipleChoice.";
    }

    private static string? SliderError(AstraQuestion question)
    {
        if (question.Min is not (>= -1000000000 and <= 1000000000)
            || question.Max is not (>= -1000000000 and <= 1000000000) || question.Max <= question.Min)
            return "min and max are required for slider, between -1000000000 and 1000000000, with max > min.";
        if (question.Step is not > 0 || question.Step > question.Max - question.Min || (question.Max - question.Min) % question.Step != 0)
            return "step must be positive and divide max minus min exactly for slider.";
        return Text(question.Unit, 24) ? null : "unit must be nonempty and have at most 24 characters for slider.";
    }

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
