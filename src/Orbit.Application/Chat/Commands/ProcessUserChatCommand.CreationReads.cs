using System.Text.Json.Serialization;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static ToolCallOutcome AwaitCreationReads(AiToolCall call) => new(
        new AiToolCallResult(call.Name, call.Id, true, null, null, null,
            new CreationReadDependency("awaiting_reads",
                "Nothing was previewed or saved. Read the results from this round first, then call bulk_create_habits once with the complete set. Reuse each existing habit's ID and stored title verbatim. Copy selected suggestion draft fields unchanged, without inventing quantities, times or activities.")),
        null, null, null, null);

    private sealed record CreationReadDependency(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("instruction")] string Instruction);
}
