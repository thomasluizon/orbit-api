using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class GetTodaysScheduleTool(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<User> userRepository) : IAiTool
{
    public string Name => "get_todays_schedule";
    public bool IsReadOnly => true;

    public string Description =>
        "Get all habits scheduled for today, including sub-habits, checklist progress, and completion status. Returns habits in the user's custom order. Call this when the user asks about today's habits, routine, or schedule.";

    public object GetParameterSchema() => new
    {
        type = "OBJECT",
        properties = new { },
        required = Array.Empty<string>()
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return new ToolResult(false, Error: "User not found.");

        var today = HabitMetricsCalculator.GetUserToday(user);

        var habits = await habitRepository.FindAsync(
            h => h.UserId == userId,
            q => q.Include(h => h.Tags).Include(h => h.Logs),
            ct);

        var todayHabits = habits
            .Where(h => h.ParentHabitId is null && !h.IsGeneral && h.DueDate <= today && !h.IsCompleted)
            .OrderBy(h => h.Position)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Today: {today:yyyy-MM-dd} ({today.DayOfWeek})");
        sb.AppendLine($"Habits due today or overdue: {todayHabits.Count}");
        sb.AppendLine();

        foreach (var habit in todayHabits)
        {
            var metrics = HabitMetricsCalculator.Calculate(habit, today);
            var isOverdue = !habit.IsGeneral && habit.DueDate < today;
            var status = isOverdue ? "OVERDUE" : "DUE TODAY";
            var loggedToday = habit.Logs.Any(l => l.Date == today);
            var logStatus = loggedToday ? " [DONE]" : "";
            var badLabel = habit.IsBadHabit ? " [BAD HABIT]" : "";
            var tagsLabel = habit.Tags.Count > 0 ? $" | Tags: {string.Join(", ", habit.Tags.Select(t => t.Name))}" : "";
            var streakLabel = metrics.CurrentStreak > 0 ? $" | Streak: {metrics.CurrentStreak}d" : "";

            var checklistLabel = "";
            if (habit.ChecklistItems.Count > 0)
            {
                var done = habit.ChecklistItems.Count(i => i.IsChecked);
                checklistLabel = $" | Checklist: {done}/{habit.ChecklistItems.Count}";
            }

            var freqLabel = habit.FrequencyUnit is null ? "One-time" :
                habit.FrequencyQuantity == 1 ? $"Every {habit.FrequencyUnit.ToString()!.ToLower()}" :
                $"Every {habit.FrequencyQuantity} {habit.FrequencyUnit.ToString()!.ToLower()}s";

            sb.AppendLine($"- \"{habit.Title}\" | ID: {habit.Id} | {freqLabel} | {status}{logStatus}{badLabel}{tagsLabel}{streakLabel}{checklistLabel}");

            // Sub-habits
            var children = habits
                .Where(h => h.ParentHabitId == habit.Id)
                .OrderBy(h => h.Position);

            foreach (var child in children)
            {
                var childLogged = child.Logs.Any(l => l.Date == today);
                var childStatus = childLogged ? " [DONE]" : "";
                sb.AppendLine($"  - \"{child.Title}\" | ID: {child.Id}{childStatus}");
            }
        }

        return new ToolResult(true, EntityName: sb.ToString());
    }
}
