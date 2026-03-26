using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class SearchHabitsTool(
    IGenericRepository<Habit> habitRepository) : IAiTool
{
    public string Name => "search_habits";

    public string Description =>
        "Search habits by title. Use this to find a habit's ID when the user mentions a habit not in the Quick Reference. Returns matching habits with their IDs and details.";

    public object GetParameterSchema() => new
    {
        type = "OBJECT",
        properties = new
        {
            query = new { type = "STRING", description = "Search text to match against habit titles (case-insensitive)" },
            limit = new { type = "INTEGER", description = "Maximum results to return. Default: 10" }
        },
        required = new[] { "query" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("query", out var queryEl) ||
            string.IsNullOrWhiteSpace(queryEl.GetString()))
            return new ToolResult(false, Error: "query is required.");

        var searchText = queryEl.GetString()!;
        var limit = 10;
        if (args.TryGetProperty("limit", out var limitEl) && limitEl.ValueKind == JsonValueKind.Number)
            limit = Math.Clamp(limitEl.GetInt32(), 1, 50);

        var habits = await habitRepository.FindAsync(
            h => h.UserId == userId && h.Title.ToLower().Contains(searchText.ToLower()),
            q => q.Include(h => h.Tags),
            ct);

        var results = habits
            .OrderBy(h => h.Position)
            .Take(limit)
            .ToList();

        if (results.Count == 0)
            return new ToolResult(true, EntityName: $"No habits found matching \"{searchText}\".");

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} habit(s) matching \"{searchText}\":");
        sb.AppendLine();

        foreach (var habit in results)
        {
            var parentLabel = habit.ParentHabitId is not null ? " [Sub-habit]" : "";
            var completedLabel = habit.IsCompleted ? " [COMPLETED]" : "";
            var badLabel = habit.IsBadHabit ? " [BAD HABIT]" : "";
            var tagsLabel = habit.Tags.Count > 0 ? $" | Tags: {string.Join(", ", habit.Tags.Select(t => t.Name))}" : "";

            var freqLabel = habit.FrequencyUnit is null ? "One-time" :
                habit.FrequencyQuantity == 1 ? $"Every {habit.FrequencyUnit.ToString()!.ToLower()}" :
                $"Every {habit.FrequencyQuantity} {habit.FrequencyUnit.ToString()!.ToLower()}s";

            sb.AppendLine($"- \"{habit.Title}\" | ID: {habit.Id} | {freqLabel} | Due: {habit.DueDate:yyyy-MM-dd}{parentLabel}{completedLabel}{badLabel}{tagsLabel}");
        }

        return new ToolResult(true, EntityName: sb.ToString());
    }
}
