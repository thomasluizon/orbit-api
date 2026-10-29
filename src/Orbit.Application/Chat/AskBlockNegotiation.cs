using System.Text.Json;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

internal static class AskBlockNegotiation
{
    public static AstraBlockDefinition Definition(AstraBlockDefinition definition, AgentClientContext client, JsonElement arguments)
    {
        if (definition.Form != AstraBlockKinds.Question
            || client.PartKinds?.Contains(AstraBlockKinds.Choice) != true
            || arguments.TryGetProperty("title", out var titleField) && titleField.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(titleField.GetString())
            || definition.Intro is not null || definition.Progress is not null
            || definition.Questions is not [var question] || question.Type != "singleChoice"
            || question.Options is not { Count: >= 2 and <= 4 } options
            || options.Any(option => option.Label.Length > 20 || option.Recommended))
            return definition;
        var title = string.IsNullOrWhiteSpace(definition.Title) ? question.Prompt : definition.Title;
        if (title.Length > 120) return definition;
        return new AstraBlockDefinition(AstraBlockKinds.Choice, title,
            options.Select(option => new AstraBlockOption(option.Id, option.Label)).ToArray(), [],
            AllowFreeText: question.AllowFreeText);
    }
}
