using System.Globalization;
using System.Text.Json;
using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public sealed record PreviewHabitSuggestionRequest(HabitSuggestionDraft Draft, string Language = "en");

public sealed record PreviewHabitSuggestionCommand(Guid UserId, HabitSuggestionDraft Draft, string Language = "en",
    AgentAuthMethod AuthMethod = AgentAuthMethod.Jwt, IReadOnlyList<string>? GrantedScopes = null,
    bool IsReadOnlyCredential = false) : IRequest<Result<AgentExecuteOperationResponse>>;

public sealed class PreviewHabitSuggestionCommandHandler(HabitSuggestionService suggestions, IAgentOperationExecutor executor)
    : IRequestHandler<PreviewHabitSuggestionCommand, Result<AgentExecuteOperationResponse>>
{
    public async Task<Result<AgentExecuteOperationResponse>> Handle(PreviewHabitSuggestionCommand request, CancellationToken ct)
    {
        var prepared = await suggestions.PrepareAllForPreviewAsync(request.UserId, [request.Draft], request.Language, ct);
        if (prepared.IsFailure) return prepared.PropagateError<AgentExecuteOperationResponse>();
        var (draft, arguments) = prepared.Value[0];
        var result = await executor.ExecuteAsync(new(request.UserId,
            draft.ExistingHabitId.HasValue ? "update_habit" : "create_habit", arguments,
            AgentExecutionSurface.Chat, request.AuthMethod, request.GrantedScopes, request.IsReadOnlyCredential,
            IncludeChangePreview: true, PreviewOnly: true), ct);
        return Result.Success(result);
    }

    internal static JsonElement Arguments(HabitSuggestionDraft draft, Habit? target)
    {
        var fields = new Dictionary<string, object?>
        {
            ["title"] = draft.Title,
            ["emoji"] = draft.Emoji,
            ["due_date"] = draft.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["due_time"] = draft.DueTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["due_end_time"] = draft.DueEndTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["days"] = draft.Days.Select(day => day.ToString()).ToArray(),
            ["is_general"] = false,
            ["is_flexible"] = false,
            ["interval_weeks"] = 1,
            ["reminder_enabled"] = draft.ReminderMinutesBefore.HasValue,
            ["reminder_times"] = draft.ReminderMinutesBefore is { } minutes ? new[] { minutes } : [],
            ["checklist_items"] = draft.ChecklistItems.Select(text => new { text, is_checked = false }).ToArray()
        };
        var clearFields = new List<string>();
        var replacesCadence = target is null || target.Schedule == HabitSchedule.General
            || target.FrequencyUnit != draft.FrequencyUnit || target.FrequencyQuantity != draft.FrequencyQuantity
            || !target.Days.ToHashSet().SetEquals(draft.Days);
        if (draft.ExistingHabitId is { } existing)
        {
            fields["habit_id"] = existing.ToString();
            if (replacesCadence) clearFields.Add("end_date");
        }
        if (draft.FrequencyUnit is { } unit)
        {
            fields["frequency_unit"] = unit.ToString();
            fields["frequency_quantity"] = draft.FrequencyQuantity;
            if (replacesCadence) fields["start_date"] = draft.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        else if (draft.ExistingHabitId.HasValue)
            clearFields.Add("frequency_unit");
        else fields["frequency_unit"] = "OneTime";
        if (target is not null)
        {
            if (!replacesCadence)
                foreach (var field in new[] { "is_general", "is_flexible", "interval_weeks" }) fields.Remove(field);
            if (draft.ChecklistItems.SequenceEqual(target.ChecklistItems.Select(item => item.Text)))
                fields.Remove("checklist_items");
            if (target.ReminderEnabled
                ? draft.ReminderMinutesBefore is { } retainedMinutes && target.ReminderTimes.Contains(retainedMinutes)
                : draft.ReminderMinutesBefore is null)
            {
                fields.Remove("reminder_enabled");
                fields.Remove("reminder_times");
            }
        }
        if (clearFields.Count > 0) fields["clear_fields"] = clearFields;
        return JsonSerializer.SerializeToElement(fields);
    }
}
