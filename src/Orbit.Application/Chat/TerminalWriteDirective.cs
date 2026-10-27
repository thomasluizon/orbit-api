using System.Text.Json;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public static class TerminalWriteDirective
{
    public const string CompleteArgument = "orbit_write_complete";

    public const string Instruction = "Plan every requested account change before proposing writes. "
        + "Set orbit_write_complete to true on every write in a round when those calls cover the entire request, including all dates, times and bounded preparation items. "
        + "Use one bulk_create_habits call for independent creations. Set it to false when another lookup or dependent action is still needed. "
        + "Successful complete writes end the turn with the server's approval preview alone. Do not revise that preview without a new correction from the person. "
        + "Corrections to an earlier preview still use revise_pending_changes and preserve all unrelated fields and items.";

    public static object WithCompletionArgument(object schema) => TerminalReadDirective.WithCompletionArgument(schema,
        CompleteArgument, "True when all writes in this round cover the entire request; false when a lookup or dependent action is still needed.");

    public static bool IsComplete(AiResponse response) => TerminalReadDirective.IsComplete(response, CompleteArgument);

    public static JsonElement ExecutionArguments(JsonElement arguments) => TerminalReadDirective.ExecutionArguments(arguments, CompleteArgument);
}
