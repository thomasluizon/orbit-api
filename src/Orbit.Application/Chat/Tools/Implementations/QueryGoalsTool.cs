using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class QueryGoalsTool(
    IGenericRepository<Goal> goalRepository) : IAiTool
{
    public string Name => "query_goals";
    public bool IsReadOnly => true;

    public string Description =>
        "Query and filter the user's goals. Use this tool whenever you need to look up, list, or find goals, including titles, descriptions, progress, linked habits, deadlines, and status. Always call this before answering questions about goals.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            search = new { type = JsonSchemaTypes.String, description = "Search goals by title or description (case-insensitive contains match)." },
            status = new
            {
                type = JsonSchemaTypes.String,
                description = "Filter by goal status.",
                @enum = new[] { "Active", "Completed", "Abandoned" }
            },
            include_completed = new { type = JsonSchemaTypes.Boolean, description = "Include non-active goals when status is not specified. Default: false." },
            include_descriptions = new { type = JsonSchemaTypes.Boolean, description = "Include goal descriptions in the response. Default: true." },
            include_linked_habits = new { type = JsonSchemaTypes.Boolean, description = "Include linked habit names in the response. Default: true." },
            limit = new { type = JsonSchemaTypes.Integer, description = "Maximum results to return. Default: 50." }
        },
        required = Array.Empty<string>()
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var search = JsonArgumentParser.GetOptionalString(args, "search");
        var status = ParseStatus(args);
        var includeCompleted =
            JsonArgumentParser.GetOptionalBool(args, "include_completed") ?? false;
        var includeDescriptions =
            JsonArgumentParser.GetOptionalBool(args, "include_descriptions") ?? true;
        var includeLinkedHabits =
            JsonArgumentParser.GetOptionalBool(args, "include_linked_habits") ?? true;
        var limit = Math.Clamp(JsonArgumentParser.GetOptionalInt(args, "limit") ?? 50, 1, 200);

        var goals = await goalRepository.FindAsync(
            g => g.UserId == userId
                && !g.IsDeleted
                && (status.HasValue
                    ? g.Status == status.Value
                    : includeCompleted || g.Status == GoalStatus.Active),
            q => q.Include(g => g.Habits),
            ct);

        var filtered = goals
            .Where(g => MatchesSearch(g, search))
            .OrderBy(g => g.Position)
            .ThenBy(g => g.CreatedAtUtc)
            .Take(limit)
            .ToList();

        if (filtered.Count == 0)
            return new ToolResult(true, EntityName: "No goals found matching the given filters.");

        return new ToolResult(true, EntityName: BuildOutput(filtered, includeDescriptions, includeLinkedHabits));
    }

    private static GoalStatus? ParseStatus(JsonElement args)
    {
        var statusString = JsonArgumentParser.GetOptionalString(args, "status");
        if (statusString is null)
            return null;

        return Enum.TryParse<GoalStatus>(statusString, ignoreCase: true, out var status)
            ? status
            : null;
    }

    private static bool MatchesSearch(Goal goal, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;

        return goal.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(goal.Description)
                && goal.Description.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildOutput(
        IReadOnlyList<Goal> goals,
        bool includeDescriptions,
        bool includeLinkedHabits)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Found {goals.Count} goal(s):");
        sb.AppendLine();

        foreach (var goal in goals)
        {
            var labels = new List<string>
            {
                $"Status: {goal.Status}",
                $"Type: {goal.Type}",
                $"Progress: {goal.CurrentValue}/{goal.TargetValue} {goal.Unit}"
            };

            if (goal.Deadline.HasValue)
                labels.Add($"Deadline: {goal.Deadline:yyyy-MM-dd}");

            sb.AppendLine($"- \"{goal.Title}\" | ID: {goal.Id} | {string.Join(" | ", labels)}");

            if (includeDescriptions && !string.IsNullOrWhiteSpace(goal.Description))
                sb.AppendLine($"  Description: {goal.Description}");

            if (includeLinkedHabits && goal.Habits.Count > 0)
            {
                var linkedHabits = string.Join(", ", goal.Habits.Select(h => h.Title));
                sb.AppendLine($"  Linked habits: {linkedHabits}");
            }
        }

        return sb.ToString();
    }
}
