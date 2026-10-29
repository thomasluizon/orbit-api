using System.Text.Json;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public sealed partial class PendingOperationChangePreviewer
{
    private async Task<PendingOperationChangePreview?> PreviewChecklistAppendAsync(
        Guid userId, JsonElement arguments, CancellationToken ct)
    {
        if (!TryReadId(arguments, "habit_id", out var habitId)
            || arguments.GetProperty("item_to_add").ValueKind != JsonValueKind.String
            || arguments.TryGetProperty(ChecklistItemsField, out _))
            return null;
        var habits = await habitRepository.FindAsync(habit => habit.Id == habitId
            && habit.UserId == userId && !habit.IsDeleted, ct);
        var habit = habits.FirstOrDefault();
        var text = arguments.GetProperty("item_to_add").GetString()!;
        if (habit is null || habit.CanAppendChecklistItem(text).IsFailure)
            return null;
        var merged = JsonSerializer.SerializeToElement(new
        {
            habit_id = habitId,
            checklist_items = habit.ChecklistItems.Select(item => new { text = item.Text, is_checked = item.IsChecked })
                .Append(new { text, is_checked = false })
        });
        return AgentArgumentPreview.Build("update_checklist", merged, TargetHabit(habit, false), default);
    }
}
