using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class AssignTagsTool(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<Tag> tagRepository,
    IUnitOfWork unitOfWork) : IAiTool, IArgumentCheckTool
{
    public string Name => "assign_tags";

    public string Description =>
        "Assign tags to a habit, replacing all existing tags. Provide either tag_names (existing names are reused, new names are auto-created) OR tag_ids (existing tag IDs, no auto-create). Only use when the user explicitly asks to tag a habit. WARNING: This REPLACES all existing tags. To add tags, include the existing tags in the list.";

    public int Order => 2;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            habit_id = new { type = JsonSchemaTypes.String, description = "ID of the habit to tag" },
            tag_names = new
            {
                type = JsonSchemaTypes.Array,
                description = "Tag names to assign. Existing names are reused; new names are auto-created. Provide either tag_names OR tag_ids.",
                items = new { type = JsonSchemaTypes.String }
            },
            tag_ids = new
            {
                type = JsonSchemaTypes.Array,
                description = "Tag IDs (GUIDs). Provide either tag_ids OR tag_names. An empty array removes all tags.",
                items = new { type = JsonSchemaTypes.String }
            }
        },
        required = new[] { "habit_id" }
    };

    public async Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!HabitToolHelpers.TryParseHabitId(args, out var habitId))
            return Orbit.Domain.Common.Result.Failure("Invalid habit_id.");
        var habits = await habitRepository.FindAsync(h => h.Id == habitId && h.UserId == userId, ct);
        if (habits.Count == 0)
            return Orbit.Domain.Common.Result.Failure("Habit not found.");
        var hasIds = args.TryGetProperty("tag_ids", out var ids);
        var hasNames = args.TryGetProperty("tag_names", out var names);
        if (hasIds == hasNames)
            return Orbit.Domain.Common.Result.Failure("Provide either tag_ids or tag_names.");
        var values = hasIds ? ids : names;
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > Orbit.Application.Common.AppConstants.MaxTagsPerHabit)
            return Orbit.Domain.Common.Result.Failure("Invalid tag list.");
        if (!hasIds)
        {
            if (values.GetArrayLength() == 0)
                return Orbit.Domain.Common.Result.Failure("At least one tag name is required.");
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String)
                    return Orbit.Domain.Common.Result.Failure("Invalid tag name.");
                var tag = Tag.Create(userId, value.GetString()!, "#7c3aed");
                if (tag.IsFailure)
                    return tag;
            }
            return Orbit.Domain.Common.Result.Success();
        }
        var tagIds = new List<Guid>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out var id))
                return Orbit.Domain.Common.Result.Failure("Invalid tag ID.");
            tagIds.Add(id);
        }
        var tags = await tagRepository.FindAsync(t => tagIds.Contains(t.Id) && t.UserId == userId, ct);
        return Orbit.Application.Common.OwnershipValidation.AllResolved(tagIds, tags, t => t.Id,
            Orbit.Application.Common.ErrorMessages.TagNotFound);
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("habit_id", out var habitIdEl) ||
            !Guid.TryParse(habitIdEl.GetString(), out var habitId))
            return new ToolResult(false, Error: "habit_id is required and must be a valid GUID.");

        if (JsonArgumentParser.PropertyExists(args, "tag_ids"))
            return await AssignByIdsAsync(habitId, args, userId, ct);

        if (args.TryGetProperty("tag_names", out var tagNamesEl) && tagNamesEl.ValueKind == JsonValueKind.Array)
            return await AssignByNamesAsync(habitId, tagNamesEl, userId, ct);

        return new ToolResult(false, Error: "Provide either tag_ids or tag_names.");
    }

    private async Task<ToolResult> AssignByIdsAsync(Guid habitId, JsonElement args, Guid userId, CancellationToken ct)
    {
        var idList = JsonArgumentParser.ParseGuidArray(args, "tag_ids") ?? new List<Guid>();

        var habit = await LoadHabitAsync(habitId, userId, ct);
        if (habit is null)
            return new ToolResult(false, Error: $"Habit {habitId} not found.");

        var resolvedTags = idList.Count == 0
            ? new List<Tag>()
            : (await tagRepository.FindTrackedAsync(t => idList.Contains(t.Id) && t.UserId == userId, ct)).ToList();

        return await ReplaceTagsAsync(habit, resolvedTags, ct);
    }

    private async Task<ToolResult> AssignByNamesAsync(Guid habitId, JsonElement tagNamesEl, Guid userId, CancellationToken ct)
    {
        var tagNames = new List<string>();
        foreach (var t in tagNamesEl.EnumerateArray())
        {
            var name = t.GetString();
            if (!string.IsNullOrWhiteSpace(name))
                tagNames.Add(name);
        }

        if (tagNames.Count == 0)
            return new ToolResult(false, Error: "At least one tag name is required.");

        var habit = await LoadHabitAsync(habitId, userId, ct);
        if (habit is null)
            return new ToolResult(false, Error: $"Habit {habitId} not found.");

        var resolvedTags = await HabitToolHelpers.ResolveOrCreateTagsAsync(tagRepository, tagNames, userId, ct);
        return await ReplaceTagsAsync(habit, resolvedTags, ct);
    }

    private Task<Habit?> LoadHabitAsync(Guid habitId, Guid userId, CancellationToken ct) =>
        habitRepository.FindOneTrackedAsync(
            h => h.Id == habitId && h.UserId == userId,
            IncludeTags,
            ct);

    /// <summary>
    /// The tags this tool replaces. The approval preview reads the habit through the same
    /// include, so the old value it shows is the set of tags that the replacement removes.
    /// </summary>
    internal static IQueryable<Habit> IncludeTags(IQueryable<Habit> query) =>
        query.Include(h => h.Tags);

    private async Task<ToolResult> ReplaceTagsAsync(Habit habit, List<Tag> resolvedTags, CancellationToken ct)
    {
        foreach (var existing in habit.Tags.ToList())
            habit.RemoveTag(existing);
        foreach (var tag in resolvedTags)
            habit.AddTag(tag);

        await unitOfWork.SaveChangesAsync(ct);

        return new ToolResult(true, EntityId: habit.Id.ToString(), EntityName: habit.Title);
    }
}
