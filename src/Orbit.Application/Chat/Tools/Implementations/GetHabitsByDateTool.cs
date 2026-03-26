using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class GetHabitsByDateTool(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<User> userRepository) : IAiTool
{
    public string Name => "get_habits_by_date";
    public bool IsReadOnly => true;

    public string Description =>
        "Get habits scheduled for a specific date. Use this when the user asks about habits on a particular day (tomorrow, next Friday, etc.). Optionally include overdue habits.";

    public object GetParameterSchema() => new
    {
        type = "OBJECT",
        properties = new
        {
            date = new { type = "STRING", description = "Date in YYYY-MM-DD format" },
            include_overdue = new { type = "BOOLEAN", description = "Include habits overdue before this date. Default: false" }
        },
        required = new[] { "date" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("date", out var dateEl) ||
            !DateOnly.TryParse(dateEl.GetString(), out var targetDate))
            return new ToolResult(false, Error: "date is required in YYYY-MM-DD format.");

        var includeOverdue = args.TryGetProperty("include_overdue", out var overdueEl) &&
                             overdueEl.ValueKind == JsonValueKind.True;

        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return new ToolResult(false, Error: "User not found.");

        var habits = await habitRepository.FindAsync(
            h => h.UserId == userId,
            q => q.Include(h => h.Tags),
            ct);

        var matchingHabits = habits
            .Where(h => h.ParentHabitId is null && !h.IsCompleted)
            .Where(h => includeOverdue ? h.DueDate <= targetDate : h.DueDate == targetDate)
            .OrderBy(h => h.Position)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Date: {targetDate:yyyy-MM-dd} ({targetDate.DayOfWeek})");
        sb.AppendLine($"Habits found: {matchingHabits.Count}");
        sb.AppendLine();

        foreach (var habit in matchingHabits)
        {
            var isOverdue = !habit.IsGeneral && habit.DueDate < targetDate;
            var status = isOverdue ? "OVERDUE" : "DUE";
            var badLabel = habit.IsBadHabit ? " [BAD HABIT]" : "";
            var tagsLabel = habit.Tags.Count > 0 ? $" | Tags: {string.Join(", ", habit.Tags.Select(t => t.Name))}" : "";

            var freqLabel = habit.FrequencyUnit is null ? "One-time" :
                habit.FrequencyQuantity == 1 ? $"Every {habit.FrequencyUnit.ToString()!.ToLower()}" :
                $"Every {habit.FrequencyQuantity} {habit.FrequencyUnit.ToString()!.ToLower()}s";

            sb.AppendLine($"- \"{habit.Title}\" | ID: {habit.Id} | {freqLabel} | {status}{badLabel}{tagsLabel}");

            // Sub-habits
            var children = habits
                .Where(h => h.ParentHabitId == habit.Id)
                .OrderBy(h => h.Position);

            foreach (var child in children)
                sb.AppendLine($"  - \"{child.Title}\" | ID: {child.Id}");
        }

        return new ToolResult(true, EntityName: sb.ToString());
    }
}
