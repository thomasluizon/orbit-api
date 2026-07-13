using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

/// <summary>
/// Shared execution helpers for the habit-oriented AI tools: single-habit resolution,
/// bulk habit-id parsing/iteration, and tag find-or-create. Centralizes the boilerplate
/// those tools would otherwise duplicate verbatim.
/// </summary>
internal static class HabitToolHelpers
{
    private const string TagColor = "#7c3aed";

    public static bool TryParseHabitId(JsonElement args, out Guid habitId)
    {
        habitId = Guid.Empty;
        return args.TryGetProperty("habit_id", out var habitIdEl)
            && Guid.TryParse(habitIdEl.GetString(), out habitId);
    }

    public static ToolResult InvalidHabitIdResult() =>
        new(false, Error: "habit_id is required and must be a valid GUID.");

    public static ToolResult HabitNotFoundResult(Guid habitId) =>
        new(false, Error: $"Habit {habitId} not found.");

    public static Task<Habit?> FindHabitAsync(
        IGenericRepository<Habit> habitRepository, Guid habitId, Guid userId, CancellationToken ct) =>
        habitRepository.FindOneTrackedAsync(
            h => h.Id == habitId && h.UserId == userId,
            cancellationToken: ct);

    public static (List<Guid> HabitIds, ToolResult? Error) ParseHabitIds(JsonElement args)
    {
        if (!args.TryGetProperty("habit_ids", out var idsEl) || idsEl.ValueKind != JsonValueKind.Array)
            return (new List<Guid>(), new ToolResult(false, Error: "habit_ids is required and must be an array of GUIDs."));

        var habitIds = new List<Guid>();
        foreach (var el in idsEl.EnumerateArray())
        {
            if (Guid.TryParse(el.GetString(), out var id))
                habitIds.Add(id);
        }

        if (habitIds.Count == 0)
            return (habitIds, new ToolResult(false, Error: "No valid habit IDs provided."));

        return (habitIds, null);
    }

    /// <summary>
    /// Loads the requested habits (with their logs) for the user, applies <paramref name="tryApply"/>
    /// to each in request order, and returns the titles of the habits the action succeeded on.
    /// </summary>
    public static async Task<List<string>> ApplyToHabitsAsync(
        IGenericRepository<Habit> habitRepository,
        Guid userId,
        List<Guid> habitIds,
        Func<Habit, Task<bool>> tryApply,
        CancellationToken ct)
    {
        var habits = await habitRepository.FindTrackedAsync(
            h => habitIds.Contains(h.Id) && h.UserId == userId,
            q => q.Include(h => h.Logs),
            ct);

        var appliedTitles = new List<string>();
        foreach (var habitId in habitIds)
        {
            var habit = habits.FirstOrDefault(h => h.Id == habitId);
            if (habit is not null && await tryApply(habit))
                appliedTitles.Add(habit.Title);
        }

        return appliedTitles;
    }

    /// <summary>
    /// Resolves tag names to entities, reusing the user's existing tags (case-insensitive, capitalized)
    /// and creating any that are missing. Newly created tags are added to the repository.
    /// </summary>
    public static async Task<List<Tag>> ResolveOrCreateTagsAsync(
        IGenericRepository<Tag> tagRepository, IEnumerable<string> tagNames, Guid userId, CancellationToken ct)
    {
        var capitalizedNames = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in tagNames)
        {
            var capitalized = Capitalize(name.Trim());
            if (!string.IsNullOrEmpty(capitalized) && seen.Add(capitalized))
                capitalizedNames.Add(capitalized);
        }

        var existingByName = (await tagRepository.FindTrackedAsync(
                t => t.UserId == userId && capitalizedNames.Contains(t.Name), ct))
            .ToDictionary(t => t.Name, StringComparer.Ordinal);

        var resolved = new List<Tag>();
        foreach (var capitalized in capitalizedNames)
        {
            if (existingByName.TryGetValue(capitalized, out var existing))
            {
                resolved.Add(existing);
            }
            else
            {
                var createResult = Tag.Create(userId, capitalized, TagColor);
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
