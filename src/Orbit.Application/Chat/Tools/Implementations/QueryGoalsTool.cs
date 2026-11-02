using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class QueryGoalsTool(
    IGenericRepository<Goal> goalRepository,
    IUserDateService userDateService,
    IGoalProgressReadSyncer goalProgressReadSyncer) : IAiTool
{
    public string Name => "query_goals";
    public bool IsReadOnly => true;

    public string Description =>
        "Read goals, progress, deadlines and linked habits before answering goal questions; indexes do not ground replies. "
        + "For goals or progress, set include_completed=true unless only active goals or a specific status is requested. "
        + "Progress is current value against target. For goal progress, call only query_goals with orbit_read_complete=true; no habit analytics or calendar read is needed.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            search = new { type = JsonSchemaTypes.String, description = "Case-insensitive title/description search." },
            status = new
            {
                type = JsonSchemaTypes.String,
                description = "Filter by goal status.",
                @enum = new[] { "Active", "Completed", "Abandoned" }
            },
            include_completed = new { type = JsonSchemaTypes.Boolean, description = "Include non-active goals when status is not specified. Default: false." },
            include_descriptions = new { type = JsonSchemaTypes.Boolean, description = "Include descriptions. Default: true." },
            include_linked_habits = new { type = JsonSchemaTypes.Boolean, description = "Include linked habits. Default: true." },
            offset = new { type = JsonSchemaTypes.Integer, description = "Result offset. Default: 0." },
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

        var userToday = JsonArgumentParser.ParseDateOnly(args, "as_of") ?? await userDateService.GetUserTodayAsync(userId, ct);
        Expression<Func<Goal, bool>> predicate = g => g.UserId == userId && !g.IsDeleted
            && (status.HasValue ? g.Status == status.Value : includeCompleted || g.Status == GoalStatus.Active);
        var offset = Math.Max(0, JsonArgumentParser.GetOptionalInt(args, "offset") ?? 0);
        int total;
        List<Goal> filtered;
        if (string.IsNullOrWhiteSpace(search))
        {
            total = await goalRepository.CountAsync(predicate, ct);
            filtered = (await goalRepository.FindAsync(predicate,
                q => OrderGoals(q).Skip(offset).Take(limit).Include(g => g.Habits), ct)).ToList();
        }
        else
        {
            var candidates = await goalRepository.FindAsync(predicate, q => OrderGoals(q), ct);
            var matchingIds = candidates.Where(g => g.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || g.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) == true).Select(g => g.Id).ToArray();
            total = matchingIds.Length;
            var pageIds = matchingIds.Skip(offset).Take(limit).ToArray();
            filtered = pageIds.Length == 0 ? [] : (await goalRepository.FindAsync(predicate,
                q => OrderGoals(q.Where(g => pageIds.Contains(g.Id))).Include(g => g.Habits), ct)).ToList();
        }
        var freshProgressValues = await goalProgressReadSyncer.ComputeFreshValuesForGoalsAsync(
            userId, userToday, filtered.Select(goal => goal.Id).ToArray(), ct);
        foreach (var goal in filtered)
            if (freshProgressValues.TryGetValue(goal.Id, out var fresh))
                GoalProgressSyncService.ApplyReadValue(goal, fresh);
        var card = GoalListCardBuilder.Build(filtered, userToday, includeProjections: false)
            with
        { OfferTracking = filtered.ToDictionary(goal => goal.Id, goal => Orbit.Application.Goals.Services.GoalMetricsCalculator.GetTrackingStatus(goal, userToday)), TotalCount = total, SourceRevision = ReadRevision.Hash(filtered.Select(g => new { g.Id, g.UpdatedAtUtc, Fresh = freshProgressValues.TryGetValue(g.Id, out var v) ? v : g.CurrentValue })) };

        if (filtered.Count == 0)
            return new ToolResult(true, EntityName: "No goals found matching the given filters.", Payload: card);

        return new ToolResult(true, EntityName: BuildOutput(filtered, freshProgressValues, includeDescriptions, includeLinkedHabits), Payload: card);
    }

    private static IOrderedQueryable<Goal> OrderGoals(IQueryable<Goal> query) =>
        query.OrderBy(g => g.Position).ThenBy(g => g.CreatedAtUtc).ThenBy(g => g.Id);

    private static GoalStatus? ParseStatus(JsonElement args)
    {
        var statusString = JsonArgumentParser.GetOptionalString(args, "status");
        if (statusString is null)
            return null;

        return Enum.TryParse<GoalStatus>(statusString, ignoreCase: true, out var status)
            ? status
            : null;
    }

    private static string BuildOutput(
        List<Goal> goals,
        IReadOnlyDictionary<Guid, int> freshProgressValues,
        bool includeDescriptions,
        bool includeLinkedHabits)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Found {goals.Count} goal(s):");
        sb.AppendLine();

        foreach (var goal in goals)
        {
            var currentValue = freshProgressValues.TryGetValue(goal.Id, out var fresh) ? fresh : goal.CurrentValue;
            var labels = new List<string>
            {
                $"Status: {goal.Status}",
                $"Type: {goal.Type}",
                string.Create(CultureInfo.InvariantCulture, $"Progress: {currentValue}/{goal.TargetValue} {goal.Unit}")
            };

            if (goal.Deadline.HasValue)
                labels.Add(string.Create(CultureInfo.InvariantCulture, $"Deadline: {goal.Deadline:yyyy-MM-dd}"));

            sb.AppendLine(CultureInfo.InvariantCulture, $"- \"{goal.Title}\" | ID: {goal.Id} | {string.Join(" | ", labels)}");

            if (includeDescriptions && !string.IsNullOrWhiteSpace(goal.Description))
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Description: {goal.Description}");

            if (includeLinkedHabits && goal.Habits.Count > 0)
            {
                var linkedHabits = string.Join(", ", goal.Habits.Select(h => $"{h.Title} ({h.Id})"));
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Linked habits: {linkedHabits}");
            }
        }

        return sb.ToString();
    }
}
