using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class GoalReviewTool(
    IGenericRepository<Goal> goalRepository,
    IUserDateService userDateService) : IAiTool
{
    public string Name => "review_goals";
    public string Description => "Review all active goals with progress metrics, projections, and linked habit performance. Use when user asks about their goals progress.";
    public bool IsReadOnly => true;

    public object GetParameterSchema() => new { type = JsonSchemaTypes.Object, properties = new { } };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var goals = await goalRepository.FindAsync(
            g => g.UserId == userId && g.Status == GoalStatus.Active,
            q => q.Include(g => g.ProgressLogs).Include(g => g.Habits).ThenInclude(h => h.Logs),
            ct);

        if (goals.Count == 0)
            return new ToolResult(true, EntityName: "No active goals found.");

        var userToday = await userDateService.GetUserTodayAsync(userId, ct);

        var sb = new StringBuilder();
        foreach (var goal in goals)
        {
            var m = GoalMetricsCalculator.Calculate(goal, userToday);
            sb.AppendLine($"Goal: \"{goal.Title}\" | {goal.CurrentValue}/{goal.TargetValue} {goal.Unit} ({m.ProgressPercentage}%)");
            sb.AppendLine($"  Status: {m.TrackingStatus} | Velocity: {m.VelocityPerDay} {goal.Unit}/day");
            if (m.ProjectedCompletionDate.HasValue)
                sb.AppendLine($"  Projected completion: {m.ProjectedCompletionDate:yyyy-MM-dd}");
            if (goal.Deadline.HasValue)
                sb.AppendLine($"  Deadline: {goal.Deadline:yyyy-MM-dd} ({m.DaysToDeadline} days)");
            foreach (var h in m.HabitAdherence)
                sb.AppendLine($"  Linked habit: \"{h.HabitTitle}\" | Weekly: {h.WeeklyCompletionRate}% | Streak: {h.CurrentStreak}d");
        }

        return new ToolResult(true, EntityName: sb.ToString());
    }
}
