using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

/// <summary>
/// The change kinds of the typed change list on the <c>ai_change_plan</c> path. A change kind is a write tool that exists,
/// and its arguments are the schema of that tool. The single form and the bulk form of one write are one kind, named by
/// the bulk tool, when the bulk schema holds every field of the single schema. <c>update_habit</c> stays its own kind,
/// because its schema holds fields that <c>bulk_update_habits</c> does not hold.
/// </summary>
public static class ChangeKinds
{
    private static readonly IReadOnlyDictionary<string, string> MergedForms = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["delete_habit"] = "bulk_delete_habits",
        ["log_habit"] = "bulk_log_habits",
        ["skip_habit"] = "bulk_skip_habits",
        ["create_habit"] = "bulk_create_habits",
        ["create_sub_habit"] = "bulk_create_habits"
    };

    private const string CreateKind = "bulk_create_habits";
    private const string SingleCreate = "create_habit";

    /// <summary>The write tools of the registry that are change kinds, in name order.</summary>
    public static IReadOnlyList<IAiTool> All(AiToolRegistry registry) => registry.GetAll()
        .Where(IsKind).OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();

    public static bool IsKind(IAiTool tool) => !tool.IsReadOnly && tool is not IMcpOnlyAiTool && !MergedForms.ContainsKey(tool.Name);

    /// <summary>The kind that holds the write tool <paramref name="toolName"/>.</summary>
    public static string KindOf(string toolName) => MergedForms.GetValueOrDefault(toolName, toolName);

    /// <summary>
    /// The write tool call that runs one change in a chat round. A create change with one habit runs <c>create_habit</c>,
    /// so it keeps the typed missing-field result of that tool; its top-level parent moves onto the habit. Every other
    /// change runs the tool of its kind with its own arguments.
    /// </summary>
    public static AiToolCall Dispatch(AiToolCall change)
    {
        if (change.Name != CreateKind || change.Args.ValueKind != JsonValueKind.Object
            || !change.Args.TryGetProperty(ToolArgumentNames.Habits, out var habits)
            || habits.ValueKind != JsonValueKind.Array || habits.GetArrayLength() != 1
            || habits[0].ValueKind != JsonValueKind.Object)
            return change;
        var habit = JsonNode.Parse(habits[0].GetRawText())!.AsObject();
        if (change.Args.TryGetProperty(ToolArgumentNames.ParentHabitId, out var parent)
            && parent.ValueKind == JsonValueKind.String
            && (!habit.TryGetPropertyValue(ToolArgumentNames.ParentHabitId, out var own) || own is null))
            habit[ToolArgumentNames.ParentHabitId] = parent.GetString();
        return new AiToolCall(SingleCreate, change.Id, JsonSerializer.SerializeToElement(habit));
    }
}
