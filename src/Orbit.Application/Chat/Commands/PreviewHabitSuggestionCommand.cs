using System.Globalization;
using System.Text.Json;
using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
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
        var checkedDraft = await suggestions.CheckForPreviewAsync(request.UserId, request.Draft, request.Language, ct);
        if (checkedDraft.IsFailure) return checkedDraft.PropagateError<AgentExecuteOperationResponse>();
        var draft = checkedDraft.Value;
        var arguments = Arguments(draft);
        var result = await executor.ExecuteAsync(new(request.UserId,
            draft.ExistingHabitId.HasValue ? "update_habit" : "create_habit", arguments,
            AgentExecutionSurface.Chat, request.AuthMethod, request.GrantedScopes, request.IsReadOnlyCredential,
            IncludeChangePreview: true, PreviewOnly: true), ct);
        return Result.Success(result);
    }

    internal static JsonElement Arguments(HabitSuggestionDraft draft)
    {
        var fields = new Dictionary<string, object?>
        {
            ["title"] = draft.Title,
            ["emoji"] = draft.Emoji,
            ["due_date"] = draft.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["due_time"] = draft.DueTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["due_end_time"] = draft.DueEndTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["days"] = draft.Days.Select(day => day.ToString()).ToArray(),
            ["reminder_enabled"] = draft.ReminderMinutesBefore.HasValue,
            ["reminder_times"] = draft.ReminderMinutesBefore is { } minutes ? new[] { minutes } : [],
            ["checklist_items"] = draft.ChecklistItems.Select(text => new { text, is_checked = false }).ToArray()
        };
        if (draft.ExistingHabitId is { } existing) fields["habit_id"] = existing.ToString();
        if (draft.FrequencyUnit is { } unit)
        {
            fields["frequency_unit"] = unit.ToString();
            fields["frequency_quantity"] = draft.FrequencyQuantity;
        }
        else if (draft.ExistingHabitId.HasValue)
            fields["clear_fields"] = new[] { "frequency_unit" };
        else fields["frequency_unit"] = "OneTime";
        return JsonSerializer.SerializeToElement(fields);
    }
}
