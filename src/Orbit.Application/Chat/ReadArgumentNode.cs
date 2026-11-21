using System.Text.Json.Nodes;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;

namespace Orbit.Application.Chat;

/// <summary>
/// Reads one model argument from a read's scope. An absent or null key is a valid empty value. A value of another
/// JSON type is a rule the model's arguments broke, so it answers a failure that names the argument.
/// </summary>
internal static class ReadArgumentNode
{
    public static Result<string?> OptionalString(JsonObject node, string key) => node[key] switch
    {
        null => Result.Success<string?>(null),
        JsonValue value when value.TryGetValue<string>(out var text) => Result.Success<string?>(text),
        _ => Result.Failure<string?>($"{key} must be a string.", ToolFailureCodes.InvalidArguments)
    };

    public static Result<int?> OptionalInt(JsonObject node, string key) => node[key] switch
    {
        null => Result.Success<int?>(null),
        JsonValue value when value.TryGetValue<int>(out var number) => Result.Success<int?>(number),
        _ => Result.Failure<int?>($"{key} must be an integer.", ToolFailureCodes.InvalidArguments)
    };
}
