using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task<Result<ChatResponse>?> TryHandlePendingParentTurnAsync(
        ProcessUserChatCommand request, ChatContext context, CancellationToken ct)
    {
        var normalized = HabitNameMatcher.Normalize(request.Message);
        var parentIntent = Regex.IsMatch(normalized,
            @"\b(sub habitos?|sub habits?|subhabits?|children|parent|pais?)\b",
            RegexOptions.None, TimeSpan.FromSeconds(1));
        var correction = Regex.IsMatch(normalized,
            @"^(?:(?:entao|then|please|por favor) )?(?:corrige|corrija|conserta|fix it|make them sub habits|make them subhabits)$",
            RegexOptions.None, TimeSpan.FromSeconds(1));
        correction |= parentIntent && Regex.IsMatch(normalized, @"\b(corrig\w*|corrija|consert\w*|fix\w*|make|torne|transforme)\b",
            RegexOptions.None, TimeSpan.FromSeconds(1));
        var confirmation = request.Message.Contains('?') && parentIntent;
        if (!correction && !confirmation) return null;
        var proposals = execution.PendingOperationStore.GetPendingHabitCreations(request.UserId)
            .Where(item => item.PendingOperationId.HasValue && item.ExpiresAtUtc.HasValue)
            .DistinctBy(item => item.PendingOperationId).ToArray();
        if (proposals.Length == 0) return null;
        if (proposals.Length != 1)
            return Result.Failure<ChatResponse>("pending_preview_ambiguous", "pending_preview_ambiguous");
        var proposal = proposals[0];
        var stored = execution.PendingOperationStore.GetExecution(request.UserId, proposal.PendingOperationId!.Value);
        if (stored is null || stored.RequiresReproposal || stored.Surface != AgentExecutionSurface.Chat
            || !execution.PendingOperationStore.IsAwaitingConfirmation(request.UserId, stored.PendingOperationId))
            return null;
        var previewer = execution.PendingOperationPreviewer ?? new PendingOperationChangePreviewer(
            data.HabitRepository, data.GoalRepository, data.TagRepository, execution.UserDateService, ai.ToolRegistry);
        if (!correction)
        {
            var original = await previewer.PreviewAsync(request.UserId, stored.OperationId, stored.Arguments, ct);
            if (original.IsFailure || original.Value is null) return null;
            return Result.Success(PendingParentReply(request, PendingFromStored(stored, proposal, original.Value)));
        }
        var revised = ParentCorrectionArguments(request, context, stored);
        if (revised is null) return Result.Failure<ChatResponse>("parent_resolution_required", "parent_resolution_required");
        var preview = await previewer.PreviewAsync(request.UserId, revised.OperationId, revised.Arguments, ct);
        if (preview.IsFailure || preview.Value?.Items is not { Count: > 0 })
            return Result.Failure<ChatResponse>("preview_unavailable");
        AgentExecuteOperationResponse? replacement = null;
        try
        {
            await execution.UnitOfWork.ExecuteInTransactionAsync(async token =>
            {
                await execution.UnitOfWork.AcquireAdvisoryLockAsync($"astra-blocks:{request.UserId}", token);
                if (!execution.PendingOperationStore.Cancel(request.UserId, stored.PendingOperationId,
                    AgentOperationFingerprint.Compute(stored.OperationId, stored.Arguments.GetRawText())))
                    throw new ParentRevisionConflictException();
                replacement = await DispatchToolCallAsync(new AiToolCall(revised.OperationId, Guid.NewGuid().ToString(), revised.Arguments),
                    request, execution.OperationExecutor, null, token);
                if (replacement.Operation.Status != AgentOperationStatus.PendingConfirmation || replacement.PendingOperation is null)
                    throw new ParentRevisionConflictException();
            }, ct);
        }
        catch (ParentRevisionConflictException)
        {
            return Result.Failure<ChatResponse>("revision_conflict");
        }
        return Result.Success(PendingParentReply(request, replacement!.PendingOperation!));
    }

    private sealed record ParentRevision(string OperationId, JsonElement Arguments);

    private static ParentRevision? ParentCorrectionArguments(ProcessUserChatCommand request, ChatContext context,
        PendingAgentOperationExecution stored)
    {
        if (JsonNode.Parse(stored.Arguments.GetRawText()) is not JsonObject root) return null;
        if (stored.OperationId is "create_habit" or "create_sub_habit")
            root = new JsonObject { ["habits"] = new JsonArray(root.DeepClone()) };
        if (stored.OperationId is not ("bulk_create_habits" or "create_habit" or "create_sub_habit")
            || root["habits"] is not JsonArray items) return null;
        var sources = new[] { request.Message }.Concat((request.History ?? []).AsEnumerable().Reverse()
            .Where(message => message.Role == "user").Select(message => message.Content));
        var headings = sources.Select(source => HabitHeadingParents.Read(source, context.UserHabits))
            .FirstOrDefault(values => values.Count > 0 && items.OfType<JsonObject>().All(item => HabitHeadingParents.ForItem(item, values) is not null));
        var named = context.UserHabits.Where(habit => HabitNameMatcher.Normalize(request.Message)
            .Contains(HabitNameMatcher.Normalize(habit.Title), StringComparison.Ordinal)).ToArray();
        foreach (var item in items.OfType<JsonObject>())
        {
            if (headings is not null && HabitHeadingParents.ForItem(item, headings) is { } heading)
                HabitHeadingParents.Bind(item, heading, removeInventedDescription: false);
            else if (named.Length == 1)
                item["parent_habit_id"] = named[0].Id.ToString();
            else if (Guid.TryParse((item["parent_habit_id"] ?? root["parent_habit_id"])?.ToString(), out var parentId)
                && context.UserHabits.Any(habit => habit.Id == parentId))
                item["parent_habit_id"] = parentId.ToString();
            else return null;
        }
        root.Remove("parent_habit_id");
        if (stored.OperationId != "bulk_create_habits" && items.Count == 1 && items[0] is JsonObject single
            && Guid.TryParse(single["parent_habit_id"]?.ToString(), out _))
            return new ParentRevision("create_sub_habit", JsonSerializer.SerializeToElement(single));
        return new ParentRevision("bulk_create_habits", JsonSerializer.SerializeToElement(root));
    }

    private static PendingAgentOperation PendingFromStored(PendingAgentOperationExecution stored,
        PendingHabitCreation proposal, PendingOperationChangePreview preview) => new(
            stored.PendingOperationId, stored.CapabilityId, stored.OperationId, stored.OperationId,
            AgentRiskClass.Low, stored.ConfirmationRequirement, proposal.ExpiresAtUtc!.Value,
            preview.Changes, preview.ChangeTargetCount, preview.Items, preview.PreviewFingerprint,
            proposal.ActionKey);

    private static ChatResponse PendingParentReply(ProcessUserChatCommand request, PendingAgentOperation pending) =>
        new(null, [], PendingOperations: [pending], CorrelationId: request.CorrelationId,
            Parts: request.ClientContext?.SupportsParts == true ? [] : null)
        { GroundingFacts = PreviewFacts([pending]) };

    private sealed class ParentRevisionConflictException : Exception;
}
