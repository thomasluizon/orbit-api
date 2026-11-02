using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Domain.Models;
using Orbit.Application.Common;

namespace Orbit.Application.Chat;

public static partial class ProactiveProposal
{
    private static string? FieldInstruction(string field) => field switch
    {
        "reminder_enabled" => "For a timed OneTime task without a reminder preference, set true and mark reminder_enabled and reminder_times in suggested_fields; preserve explicit false.",
        "reminder_times" => $"Minutes before due_time; suggest [{AppConstants.DefaultReminderMinutes}] with reminder_enabled=true, marking both in suggested_fields. Prescribed times use scheduled_reminders.",
        "parent_habit_id" => "Set a fitting offer_account_facts.Habits ID; mark parent_habit_id in suggested_fields. Resolve ambiguity in preview; leave the bulk parent unset.",
        "goal_ids" => "Set matching offer_account_facts.ActiveGoals IDs; mark unrequested goal_ids in suggested_fields. Preserve links; never requery or create equivalent goals.",
        _ => null
    };

    internal static AiToolCall CompleteCreateReminder(AiToolCall call, AgentClientContext? client)
    {
        if (!Enabled(client)
            || call.Name is not ("create_habit" or "bulk_create_habits") || call.Args.ValueKind != JsonValueKind.Object)
            return call;
        var root = JsonNode.Parse(call.Args.GetRawText())!.AsObject();
        if (call.Name == "bulk_create_habits" && root["habits"] is JsonArray habits)
        {
            foreach (var item in habits.OfType<JsonObject>()) CompleteReminderNode(item);
        }
        else CompleteReminderNode(root);
        return call with { Args = JsonSerializer.SerializeToElement(root) };
    }

    private static void CompleteReminderNode(JsonObject node)
    {
        if (node["sub_habits"] is JsonArray children)
            foreach (var child in children.OfType<JsonObject>()) CompleteReminderNode(child);
        if (node["habit_id"] is not null || node["frequency_unit"]?.ToString() != "OneTime"
            || !DateOnly.TryParseExact(node["due_date"]?.ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || !TimeOnly.TryParseExact(node["due_time"]?.ToString(), ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || ReminderFields.Any(field => HasValue(node[field]))
            || node[Argument] is not (null or JsonArray or JsonObject)) return;
        if (node[Argument] is JsonObject values)
        {
            if (ReminderFields.Any(field => HasValue(values[field]))) return;
            values["reminder_enabled"] = true;
            values["reminder_times"] = new JsonArray(AppConstants.DefaultReminderMinutes);
            return;
        }
        if (node[Argument] is not JsonArray names)
        {
            names = new JsonArray();
            node[Argument] = names;
        }
        node["reminder_enabled"] = true;
        node["reminder_times"] = new JsonArray(AppConstants.DefaultReminderMinutes);
        foreach (var field in new[] { "reminder_enabled", "reminder_times" })
            if (!names.Any(name => name?.ToString() == field)) names.Add(field);
    }
}
