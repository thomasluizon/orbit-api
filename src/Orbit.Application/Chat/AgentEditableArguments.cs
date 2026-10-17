using System.Text.Json;

namespace Orbit.Application.Chat;

internal static class AgentEditableArguments
{
    public static bool IsConsumed(string operationId, JsonElement args, string field)
    {
        if (operationId == "suggest_breakdown")
            return field == "title";
        if (!args.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String)
            return true;
        var fields = operationId switch
        {
            "update_profile_preferences" => action.GetString() switch
            {
                "set_timezone" => new[] { "timezone" },
                "set_language" => ["language"],
                "set_week_start_day" => ["week_start_day"],
                "set_clock_format" => ["uses_24_hour_clock"],
                "set_theme_preference" => ["theme_preference"],
                _ => []
            },
            "manage_api_keys" when action.GetString() == "create" => ["name", "scopes", "is_read_only", "expires_at_utc"],
            "manage_subscription" when action.GetString() == "create_checkout" => ["interval"],
            "manage_account" when action.GetString() == "confirm_deletion" => ["code"],
            "manage_calendar_sync" when action.GetString() == "set_auto_sync" => ["enabled"],
            "update_notifications" => action.GetString() switch
            {
                "subscribe_push" => new[] { "endpoint", "p256dh", "auth" },
                "unsubscribe_push" => ["endpoint", "p256dh", "auth", "release_other_account"],
                _ => []
            },
            _ => []
        };
        return fields.Contains(field, StringComparer.Ordinal);
    }
}
