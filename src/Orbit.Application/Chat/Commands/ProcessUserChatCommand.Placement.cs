using System.Text.RegularExpressions;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task<Result<ChatResponse>?> TryHandlePlacementTurnAsync(ProcessUserChatCommand request, CancellationToken ct)
    {
        if (request.ClientContext?.SupportsPendingOperationPlacement != true) return null;
        var normalized = HabitNameMatcher.Normalize(request.Message).Trim(' ', '.', '!');
        var match = Regex.Match(normalized,
            @"^(?:(?:please|por favor) )?(?:make (?:it|them)|change (?:it|them) to|use|switch to|coloque como|coloca como|mude para|muda para|transforme em|transforma em|faca (?:dele|deles)|faca como) (?:a |an |um |uma |em )?(?<placement>checklist items?|checklist|(?:item|itens) (?:de|da|na) lista|sub habits?|subhabits?|sub habitos?|(?:new |separate |standalone |top level )habit|habito (?:novo|separado|independente|principal))(?: instead| no lugar| em vez disso)?$",
            RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!match.Success || request.ImageData is not null) return null;
        var wording = match.Groups["placement"].Value;
        var placement = wording.Contains("checklist", StringComparison.Ordinal) || wording.Contains("lista", StringComparison.Ordinal)
            ? HabitPlacement.ChecklistItem : wording.StartsWith("sub", StringComparison.Ordinal) ? HabitPlacement.SubHabit : HabitPlacement.NewParent;
        var previewer = execution.PendingOperationPreviewer ?? new PendingOperationChangePreviewer(
            data.HabitRepository, data.GoalRepository, data.TagRepository, execution.UserDateService, ai.ToolRegistry);
        var candidates = new List<(PendingAgentOperationExecution Stored, PendingOperationChangePreview Preview)>();
        foreach (var pending in execution.PendingOperationStore.GetAwaiting(request.UserId)
            .Where(pending => pending.Surface == AgentExecutionSurface.Chat && !pending.RequiresReproposal))
        {
            var preview = await previewer.PreviewAsync(request.UserId, pending.OperationId, pending.Arguments, ct, request.ClientContext);
            if (preview.IsSuccess && preview.Value?.Items?.Any(item => item.Placement is not null) == true)
                candidates.Add((pending, preview.Value));
        }
        if (candidates.Count == 0) return null;
        if (candidates.Count != 1) return Result.Failure<ChatResponse>("pending_preview_ambiguous");
        var (stored, original) = candidates[0];
        var service = new PendingOperationRevisionService(execution.PendingOperationStore, previewer,
            new RevisePendingOperationRequestValidator(), ai.ToolRegistry);
        var revised = await service.ReviseAsync(request.UserId, stored.PendingOperationId,
            new(original.PreviewFingerprint!, original.Items!.Select(item => new RevisedPendingOperationItem(
                item.ItemId, Placement: item.Placement is not null ? placement : null)).ToArray(), request.ClientContext), ct);
        if (!revised.IsSuccess || revised.Preview is not { } updated)
            return Result.Failure<ChatResponse>(revised.Error ?? "invalid_revision");
        var capability = ai.CatalogService.GetCapability(stored.CapabilityId);
        if (capability is null || stored.ExpiresAtUtc is not { } expiresAtUtc)
            return Result.Failure<ChatResponse>("pending_operation_not_found");
        var pendingReply = new PendingAgentOperation(stored.PendingOperationId, stored.CapabilityId,
            stored.OperationId, stored.OperationId, capability.RiskClass, stored.ConfirmationRequirement,
            expiresAtUtc, updated.Changes, updated.ChangeTargetCount, updated.Items, updated.PreviewFingerprint, stored.ActionKey);
        return Result.Success(PendingParentReply(request, pendingReply));
    }
}
