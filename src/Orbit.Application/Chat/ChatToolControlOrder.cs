using System.Text.Json.Nodes;

namespace Orbit.Application.Chat;

public static class ChatToolControlOrder
{
    public static void Apply(JsonObject schema)
    {
        if (schema["properties"] is not JsonObject properties) return;
        var ordered = properties.OrderBy(property => Priority(property.Key)).ToArray();
        properties.Clear();
        foreach (var property in ordered) properties.Add(property.Key, property.Value);
        if (schema["required"] is not JsonArray required) return;
        var names = required.Select(value => value!.GetValue<string>()).OrderBy(Priority).ToArray();
        required.Clear();
        foreach (var name in names) required.Add(name);
    }

    private static int Priority(string name) => name switch
    {
        TerminalReadDirective.CompleteArgument => 0,
        TerminalWriteDirective.CompleteArgument => 1,
        ProactiveOfferDismissal.Argument => 2,
        ProactiveProposal.Argument => 3,
        ProactiveProposal.Metadata => 4,
        ProactiveReadOffer.Argument => 5,
        _ => 6
    };
}
