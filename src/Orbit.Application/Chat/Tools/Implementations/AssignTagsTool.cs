using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class AssignTagsTool(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<Tag> tagRepository) : IAiTool
{
    public string Name => "assign_tags";

    public string Description =>
        "Assign tags to a habit by name. Existing tags with matching names will be reused. New tag names will be auto-created. Only use when the user explicitly asks to tag a habit. WARNING: This REPLACES all existing tags. To add tags, include the existing tags in the list.";

    public object GetParameterSchema() => new
    {
        type = "object",
        properties = new
        {
            habit_id = new { type = "string", description = "ID of the habit to tag" },
            tag_names = new
            {
                type = "array",
                description = "Tag names to assign",
                items = new { type = "string" }
            }
        },
        required = new[] { "habit_id", "tag_names" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("habit_id", out var habitIdEl) ||
            !Guid.TryParse(habitIdEl.GetString(), out var habitId))
            return new ToolResult(false, Error: "habit_id is required and must be a valid GUID.");

        if (!args.TryGetProperty("tag_names", out var tagNamesEl) || tagNamesEl.ValueKind != JsonValueKind.Array)
            return new ToolResult(false, Error: "tag_names is required and must be an array.");

        var tagNames = new List<string>();
        foreach (var t in tagNamesEl.EnumerateArray())
        {
            var name = t.GetString();
            if (!string.IsNullOrWhiteSpace(name))
                tagNames.Add(name);
        }

        if (tagNames.Count == 0)
            return new ToolResult(false, Error: "At least one tag name is required.");

        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == habitId && h.UserId == userId,
            q => q.Include(h => h.Tags),
            ct);

        if (habit is null)
            return new ToolResult(false, Error: $"Habit {habitId} not found.");

        var resolvedTags = await ResolveTagsByNameAsync(tagNames, userId, ct);

        // Clear existing and assign new
        foreach (var existing in habit.Tags.ToList())
            habit.RemoveTag(existing);
        foreach (var tag in resolvedTags)
            habit.AddTag(tag);

        return new ToolResult(true, EntityId: habit.Id.ToString(), EntityName: habit.Title);
    }

    private async Task<List<Tag>> ResolveTagsByNameAsync(List<string> tagNames, Guid userId, CancellationToken ct)
    {
        var resolved = new List<Tag>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in tagNames)
        {
            var capitalized = Capitalize(name.Trim());
            if (string.IsNullOrEmpty(capitalized) || !seen.Add(capitalized)) continue;

            var existing = await tagRepository.FindOneTrackedAsync(
                t => t.UserId == userId && t.Name == capitalized, cancellationToken: ct);

            if (existing is not null)
            {
                resolved.Add(existing);
            }
            else
            {
                var createResult = Tag.Create(userId, capitalized, "#7c3aed");
                if (createResult.IsSuccess)
                {
                    await tagRepository.AddAsync(createResult.Value, ct);
                    resolved.Add(createResult.Value);
                }
            }
        }

        return resolved;
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..].ToLower();
}
