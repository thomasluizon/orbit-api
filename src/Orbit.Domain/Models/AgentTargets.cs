using System.Text.Json;

namespace Orbit.Domain.Models;

/// <summary>
/// A kind of record that an agent call names by id. <see cref="Name"/> is the resource in a <c>target_not_owned</c>
/// denial reason, and <see cref="Fields"/> are the argument fields that hold its ids.
/// </summary>
public sealed record AgentTargetResource(string Name, IReadOnlyList<string> Fields);

/// <summary>One target field of an agent call: its resource, its name, its value, and whether the root object holds it.</summary>
public sealed record AgentTargetField(AgentTargetResource Resource, string Name, JsonElement Value, bool AtRoot);

/// <summary>
/// The one catalog of the argument fields that name records of the person. The ownership check reads the ids that it
/// checks from it, and the chat correction of a denied write reads the targets and the change of a write from it.
/// </summary>
public static class AgentTargets
{
    public static readonly AgentTargetResource Habit = new("habit", ["habit_id", "habit_ids", "parent_habit_id", "new_parent_id", "parent_id"]);
    public static readonly AgentTargetResource Tag = new("tag", ["tag_id", "tag_ids"]);
    public static readonly AgentTargetResource Goal = new("goal", ["goal_id", "goal_ids"]);
    public static readonly AgentTargetResource Notification = new("notification", ["notification_id", "notification_ids"]);
    public static readonly AgentTargetResource Template = new("template", ["template_id"]);
    public static readonly AgentTargetResource ApiKey = new("api_key", ["key_id"]);
    public static readonly AgentTargetResource CalendarSuggestion = new("calendar_suggestion", ["suggestion_id"]);

    /// <summary>Every resource, in the order that the ownership check reads them.</summary>
    public static IReadOnlyList<AgentTargetResource> Resources { get; } =
        [Habit, Tag, Goal, Notification, Template, ApiKey, CalendarSuggestion];

    /// <summary>The fields whose objects hold more target fields: positions, habits, sub-habits and a filter.</summary>
    private static readonly string[] Containers = ["positions", "habits", "sub_habits", "filter"];

    public static bool IsTargetField(string name) => ResourceOf(name) is not null;

    /// <summary>
    /// Each target field of the arguments: in the root object, in the objects of an array, and in the container fields
    /// below them. A target field holds ids, so the walk does not go into its value.
    /// </summary>
    public static IReadOnlyList<AgentTargetField> Collect(JsonElement arguments)
    {
        var fields = new List<AgentTargetField>();
        Walk(arguments, root: true);
        return fields;

        void Walk(JsonElement node, bool root)
        {
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray()) Walk(item, root: false);
                return;
            }
            if (node.ValueKind != JsonValueKind.Object) return;
            foreach (var property in node.EnumerateObject())
            {
                if (ResourceOf(property.Name) is { } resource)
                    fields.Add(new AgentTargetField(resource, property.Name, property.Value, root));
                else if (Containers.Contains(property.Name, StringComparer.Ordinal))
                    Walk(property.Value, root: false);
            }
        }
    }

    private static AgentTargetResource? ResourceOf(string name) =>
        Resources.FirstOrDefault(resource => resource.Fields.Contains(name, StringComparer.Ordinal));
}
