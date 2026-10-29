using System.Text.Json;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public static class TerminalWriteDirective
{
    public const string CompleteArgument = "orbit_write_complete";

    public const string Instruction = "Plan every requested account change before proposing writes. "
        + "Account for each requested action, target and occurrence, even when different actions affect different habits. "
        + "A lookup never fulfills a requested mutation. After resolving a target, call its write tool. "
        + "A read showing an occurrence as skipped does not fulfill a requested skip. Call the matching skip tool for the requested target and date, and let it validate the current state. "
        + "For example, rename and skip require both update_habit and skip_habit in the same round. "
        + "Skipping one occurrence uses skip_habit; skipping several habits uses bulk_skip_habits, never log_habit or bulk_log_habits. "
        + "Set orbit_write_complete to true on every write in a round when those calls cover the entire request, including all dates, times and bounded preparation items. "
        + "Do not declare completion after only the first action or target. "
        + "Use one bulk_create_habits call for independent creations. Set it to false when another lookup or dependent action is still needed. "
        + "Successful complete writes end the turn with the server's approval preview alone. Do not revise that preview without a new correction from the person. "
        + "If a write returns HABIT_LIMIT_REACHED, explain the returned limit in the person's language. It applies to every plan; do not propose upgrading, deleting data or bypassing it. "
        + "Corrections to an earlier preview still use revise_pending_changes and preserve all unrelated fields and items.";

    public static object WithCompletionArgument(object schema) => TerminalReadDirective.WithCompletionArgument(schema,
        CompleteArgument, "True only when every requested mutation has its matching write call in this round; a read status never fulfills a write. False when a lookup or dependent action is still needed.");

    public static bool IsComplete(AiResponse response) => TerminalReadDirective.IsComplete(response, CompleteArgument);

    public static JsonElement ExecutionArguments(JsonElement arguments) => TerminalReadDirective.ExecutionArguments(arguments, CompleteArgument);
}
