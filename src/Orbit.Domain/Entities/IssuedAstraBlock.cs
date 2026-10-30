using System.Text.Json;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Domain.Entities;

public sealed class IssuedAstraBlock : Entity
{
    public Guid UserId { get; private set; }
    public string Form { get; private set; } = string.Empty;
    public string DefinitionJson { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? AnsweredAtUtc { get; private set; }
    public DateTime? InvalidatedAtUtc { get; private set; }
    public string? AnswerJson { get; private set; }
    public string? ResponseJson { get; private set; }
    public string? CommittedResponseJson { get; private set; }
    public DateOnly? ReservedAllowanceDate { get; private set; }

    private IssuedAstraBlock() { }

    public AstraBlockDefinition ReadDefinition() =>
        JsonSerializer.Deserialize<AstraBlockDefinition>(DefinitionJson)
            ?? throw new InvalidOperationException("Stored block has no definition.");

    public static IssuedAstraBlock Create(Guid userId, AstraBlockDefinition definition)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("User id is required.", nameof(userId));
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Title is null || definition.Form != AstraBlockKinds.Question && string.IsNullOrWhiteSpace(definition.Title))
            throw new ArgumentException("question for choice or title for pickList must be nonempty.", nameof(definition));
        ValidateDefinition(definition);

        var createdAtUtc = DateTime.UtcNow;
        return new IssuedAstraBlock
        {
            UserId = userId,
            Form = definition.Form,
            DefinitionJson = JsonSerializer.Serialize(definition),
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = createdAtUtc.AddHours(24)
        };
    }

    private static void ValidateDefinition(AstraBlockDefinition definition)
    {
        var error = DefinitionError(definition);
        if (error is not null) throw new ArgumentException(error, nameof(definition));
    }

    private static string? DefinitionError(AstraBlockDefinition definition)
    {
        if (definition.DraftClarification is { } draft
            && (string.IsNullOrWhiteSpace(draft.DraftCallId) || string.IsNullOrWhiteSpace(draft.MissingField)
                || draft.ClarificationId == Guid.Empty || definition.Form == AstraBlockKinds.PickList
                || definition.Form == AstraBlockKinds.Question
                    && (definition.Questions is not [var question] || question.Id != draft.MissingField)))
            return "Draft clarification requires a call id, missing field, stored clarification id and a matching question or choice.";
        if (definition.Options is null || definition.Items is null) return "options and items must be arrays.";
        if (definition.Form != AstraBlockKinds.Question
            && (definition.Questions is not null || definition.Intro is not null || definition.Progress is not null))
            return "questions, intro and progress are only allowed for form question.";
        return definition.Form switch
        {
            AstraBlockKinds.Choice => ChoiceError(definition),
            AstraBlockKinds.PickList => PickListError(definition),
            AstraBlockKinds.Question => AstraQuestionRules.DefinitionError(definition),
            _ => "form must be choice, pickList or question."
        };
    }

    private static string? ChoiceError(AstraBlockDefinition definition)
    {
        if (definition.Title.Length > 120) return "question must have at most 120 characters.";
        if (definition.Options.Count is < 2 or > 4) return "options must contain 2 to 4 options for choice.";
        if (definition.Items.Count != 0 || definition.Action is not null) return "items and action must be omitted or null for choice.";
        if (definition.Options.Any(option => option is null || string.IsNullOrWhiteSpace(option.Id)
            || string.IsNullOrWhiteSpace(option.Label) || option.Label.Length > 20
            || option.Description?.Length > 24))
            return "options require nonempty ids and labels, labels up to 20 characters and descriptions up to 24 characters.";
        return definition.Options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() == definition.Options.Count
            ? null : "options must have unique ids.";
    }

    private static string? PickListError(AstraBlockDefinition definition)
    {
        if (definition.Items.Count is < 1 or > 10) return "items must contain 1 to 10 items for pickList.";
        if (definition.Options.Count != 0) return "options must be omitted or null for pickList.";
        if (definition.Action is not ("create_habits" or "log_habits" or "skip_habits"))
            return "action must be create_habits, log_habits or skip_habits for pickList.";
        if (!definition.MultiSelect || definition.AllowFreeText) return "pickList requires multiSelect=true and allowFreeText=false.";
        if (definition.Items.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id)
            || string.IsNullOrWhiteSpace(item.Label) || item.Arguments.ValueKind != JsonValueKind.Object || item.Detail?.Length > 24))
            return "items require nonempty ids and labels, object arguments and details up to 24 characters.";
        return definition.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == definition.Items.Count
            ? null : "items must have unique ids.";
    }

    public void MarkAnswered(DateTime answeredAtUtc)
    {
        if (AnsweredAtUtc.HasValue || InvalidatedAtUtc.HasValue || answeredAtUtc >= ExpiresAtUtc
            || answeredAtUtc < CreatedAtUtc)
            throw new InvalidOperationException("Block is not open.");
        AnsweredAtUtc = answeredAtUtc;
    }

    public static string SerializeAnswer(AstraBlockAnswer answer) =>
        JsonSerializer.Serialize(answer with
        {
            SelectedIds = answer.SelectedIds.Order(StringComparer.Ordinal).ToArray(),
            Answers = answer.Answers?.OrderBy(value => value.QuestionId, StringComparer.Ordinal)
                .Select(value => value with { SelectedIds = (value.SelectedIds ?? []).Order(StringComparer.Ordinal).ToArray() }).ToArray()
        });

    public bool MatchesAnswer(AstraBlockAnswer answer)
    {
        var stored = AnswerJson is null ? null : JsonSerializer.Deserialize<AstraBlockAnswer>(AnswerJson);
        return stored is not null && stored.BlockId == answer.BlockId && stored.FreeText == answer.FreeText
            && SameSelection(stored.SelectedIds, answer.SelectedIds) && SameQuestionAnswers(stored.Answers, answer.Answers);
    }

    private static bool SameSelection(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal));

    private static bool SameQuestionAnswers(IReadOnlyList<AstraQuestionAnswer>? left, IReadOnlyList<AstraQuestionAnswer>? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.Count == right.Count && left.OrderBy(value => value.QuestionId, StringComparer.Ordinal)
            .Zip(right.OrderBy(value => value.QuestionId, StringComparer.Ordinal), (first, second) =>
                first.QuestionId == second.QuestionId && first.Number == second.Number && first.Boolean == second.Boolean
                && first.FreeText == second.FreeText && SameSelection(first.SelectedIds ?? [], second.SelectedIds ?? []))
            .All(matches => matches);
    }
}
