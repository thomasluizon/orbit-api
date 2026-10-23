using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Chat;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class GoalReviewTool(
    IGenericRepository<Goal> goalRepository,
    IUserDateService userDateService,
    IGoalProgressReadSyncer goalProgressReadSyncer) : IAiTool
{
    public string Name => "review_goals";
    public string Description => "Review active goals: progress metrics, projections and linked habit performance.";
    public bool IsReadOnly => true;

    public object GetParameterSchema() => new { type = JsonSchemaTypes.Object, properties = new { } };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var userToday = JsonArgumentParser.ParseDateOnly(args, "as_of") ?? await userDateService.GetUserTodayAsync(userId, ct);
        var weekStartDay = await userDateService.GetUserWeekStartDayAsync(userId, ct);
        var freshValues = await goalProgressReadSyncer.ComputeFreshValuesAsync(userId, userToday, ct);
        var streakWindowStart = userToday.AddDays(-AppConstants.MaxStreakLookbackDays);

        var goals = await goalRepository.FindAsync(
            g => g.UserId == userId && g.Status == GoalStatus.Active,
            q => q.Include(g => g.ProgressLogs).Include(g => g.Habits).ThenInclude(h => h.Logs.Where(l => l.Date >= streakWindowStart)),
            ct);

        if (goals.Count == 0)
            return new ToolResult(true, EntityName: "No active goals found.", Payload: new GoalListCard([]));

        var sb = new StringBuilder();
        foreach (var goal in goals)
        {
            if (freshValues.TryGetValue(goal.Id, out var freshValue))
                GoalProgressSyncService.ApplyReadValue(goal, freshValue);

            var m = GoalMetricsCalculator.Calculate(goal, userToday, weekStartDay);
            sb.AppendLine(CultureInfo.InvariantCulture, $"Goal: \"{goal.Title}\" | {goal.CurrentValue}/{goal.TargetValue} {goal.Unit} ({m.ProgressPercentage}%)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Status: {m.TrackingStatus} | Velocity: {m.VelocityPerDay} {goal.Unit}/day");
            if (m.ProjectedCompletionDate.HasValue)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Projected completion: {m.ProjectedCompletionDate:yyyy-MM-dd}");
            if (goal.Deadline.HasValue)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Deadline: {goal.Deadline:yyyy-MM-dd} ({m.DaysToDeadline} days)");
            foreach (var h in m.HabitAdherence)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Linked habit: \"{h.HabitTitle}\" | Weekly: {h.WeeklyCompletionRate}% | Streak: {h.CurrentStreak}d");
        }

        return new ToolResult(true, EntityName: sb.ToString(),
            Payload: GoalListCardBuilder.Build(goals.ToList(), userToday, weekStartDay) with
            {
                SourceRevision = ReadRevision.Hash(goals.OrderBy(g => g.Id).Select(g => new
                {
                    g.Id,
                    g.UpdatedAtUtc,
                    Logs = g.ProgressLogs.OrderBy(l => l.Id).Select(l => new { l.Id, l.CreatedAtUtc }),
                    Habits = g.Habits.OrderBy(h => h.Id).Select(h => new
                    {
                        h.Id,
                        h.UpdatedAtUtc,
                        Logs = h.Logs.Where(l => !l.IsDeleted).OrderBy(l => l.Id).Select(l => new { l.Id, l.Date, l.Value, l.UpdatedAtUtc })
                    })
                }))
            });
    }
}
