namespace Orbit.Application.Chat;

public static class TerminalReadDirective
{
    public const string Marker = "[[orbit:complete]]";

    public const string Instruction = "When the reads in this first round will fully answer the request, include "
        + Marker + " with the tool calls. Omit it for prerequisite lookups, dependent reads or any request that still needs a write or another tool. "
        + "The server completes a marked round only when every call is a successful read with a typed block. "
        + "In the first round, you may write at most one short framing sentence in the current message's language, before any data exists. "
        + "Use no figures, digits, entity facts or claims about the result, even when spelling a number in words. Never repeat the block. "
        + "English: Here is your habit ranking. Portuguese: Aqui está o ranking dos seus hábitos. "
        + "If another round is needed, the server still uses only that first framing sentence beside the blocks.";

    public static bool IsComplete(string? text) => FollowUpDirective.Parse(text).Segments
        .Any(segment => segment.Directive == Marker);
}
