using System.Text.Json;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

internal static class AskBlockNegotiation
{
    public static JsonElement Arguments(JsonElement args, AgentClientContext client)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("form", out var form)
            || form.ValueKind != JsonValueKind.String || form.GetString() != AstraBlockKinds.Question
            || client.PartKinds?.Contains(AstraBlockKinds.Question) == true
            || client.PartKinds?.Contains(AstraBlockKinds.Choice) != true
            || !args.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array
            || questions.GetArrayLength() != 1)
            return args;
        var question = questions[0];
        if (!IsSingleChoice(question))
            return args;
        var options = question.GetProperty("options");
        var title = args.TryGetProperty("question", out var heading) && heading.ValueKind == JsonValueKind.String
            ? heading : question.TryGetProperty("prompt", out var prompt) ? prompt : default;
        if (title.ValueKind != JsonValueKind.String) return args;
        return JsonSerializer.SerializeToElement(new
        {
            form = AstraBlockKinds.Choice,
            question = title.GetString(),
            allowFreeText = question.TryGetProperty("allowFreeText", out var free) && free.ValueKind == JsonValueKind.True,
            options = options.EnumerateArray().Select(option => new
            {
                id = option.TryGetProperty("id", out var id) ? id : default,
                label = option.TryGetProperty("label", out var label) ? label : default
            })
        });
    }

    private static bool IsSingleChoice(JsonElement question) => question.ValueKind == JsonValueKind.Object
        && question.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
        && type.GetString() == "singleChoice" && question.TryGetProperty("options", out var options)
        && options.ValueKind == JsonValueKind.Array && options.EnumerateArray().All(option => option.ValueKind == JsonValueKind.Object);
}
