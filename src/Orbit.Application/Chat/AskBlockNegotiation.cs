using System.Text.Json;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

internal static class AskBlockNegotiation
{
    public static AiBlockDefinition Definition(AiBlockDefinition definition, AgentClientContext client, JsonElement arguments)
    {
        if (definition.Form != AiBlockKinds.Question
            || client.PartKinds?.Contains(AiBlockKinds.Choice) != true
            || definition.Questions is not [var question] || question.Type != "singleChoice"
            || question.Options is not { Count: >= 2 and <= 4 } options
            || options.Any(option => option.Label.Length > 20 || option.Recommended))
            return definition;
        var title = TopLevelQuestion(arguments) ?? (string.IsNullOrWhiteSpace(question.Prompt) ? definition.Title : question.Prompt);
        if (string.IsNullOrWhiteSpace(title) || title.Length > 120) return definition;
        return new AiBlockDefinition(AiBlockKinds.Choice, title,
            options.Select(option => new AiBlockOption(option.Id, option.Label)).ToArray(), [],
            AllowFreeText: question.AllowFreeText);
    }

    private static string? TopLevelQuestion(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("question", out var field)
            && field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString())
            ? field.GetString() : null;
}
