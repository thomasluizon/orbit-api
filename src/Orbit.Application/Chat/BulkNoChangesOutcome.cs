using System.Text.Json;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;

namespace Orbit.Application.Chat;

public sealed record BulkNoChangesOutcome(bool IconsAlreadyPresent)
{
    public static bool Supports(string operationId) => operationId is
        "bulk_update_habit_emojis" or "bulk_update_habits" or "bulk_log_habits"
        or "bulk_skip_habits" or "bulk_reschedule_habits" or "bulk_delete_habits";

    public static BulkNoChangesOutcome For(string operationId, JsonElement arguments) => new(
        operationId == "bulk_update_habit_emojis"
        && BulkUpdateHabitEmojisTool.ResolveEmojiOperation(arguments).Infer
        && JsonArgumentParser.GetOptionalBool(arguments, "replace_existing") != true);

    internal string Message(string? language) => (IconsAlreadyPresent,
        language?.StartsWith("pt", StringComparison.OrdinalIgnoreCase) == true) switch
    {
        (true, true) => "Todos os seus hábitos já têm ícone.",
        (true, false) => "All your habits already have an icon.",
        (false, true) => "Nenhum hábito precisa de alterações.",
        (false, false) => "No habits need changes."
    };
}
