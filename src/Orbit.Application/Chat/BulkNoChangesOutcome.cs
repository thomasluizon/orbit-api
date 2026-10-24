using System.Text.Json;
using Orbit.Application.Common;
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

    internal string Message(string? language) => ErrorCopy.Resolve(
        IconsAlreadyPresent ? "chat.bulk.iconsAlreadyPresent" : "chat.bulk.noChanges",
        LocaleHelper.IsPortuguese(language));
}
