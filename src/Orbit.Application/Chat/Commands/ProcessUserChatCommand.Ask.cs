using System.Text.Json;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static bool IsAskKind(string kind) => kind is AstraBlockKinds.Choice or AstraBlockKinds.PickList or AstraBlockKinds.Question;

    private async Task<ToolCallOutcome> ExecuteAskCallAsync(AiToolCall call, ProcessUserChatCommand request,
        ToolExecutionAccumulator results, CancellationToken ct)
    {
        using var phase = ChatTurnTimer.Measure(ChatPhase.ToolCall);
        ToolCallOutcome Failed(string error) => new(new AiToolCallResult(call.Name, call.Id, false, null, null,
            error + " Ask in plain text instead."), null, null, null, null);
        var flags = await data.FeatureFlagService.GetEnabledKeysForUserAsync(request.UserId, ct);
        if (request.AuthMethod != AgentAuthMethod.Jwt || flags.Contains(FeatureFlagKeys.AstraAskBlocksDisabled)
            || flags.Contains(FeatureFlagKeys.AstraPartsDisabled)
            || request.ClientContext?.SupportsParts != true || results.AskBlock is not null)
            return Failed("Interactive questions are unavailable.");
        if (call.Args.ValueKind != JsonValueKind.Object || !call.Args.TryGetProperty("form", out var form) || form.ValueKind != JsonValueKind.String
            || request.ClientContext.PartKinds?.Contains(form.GetString()!, StringComparer.Ordinal) != true)
            return Failed("The client does not support this block kind.");
        var tool = ai.ToolRegistry.GetTool("ask");
        if (tool is null)
            return Failed("The ask tool is unavailable.");
        var result = await tool.ExecuteAsync(call.Args, request.UserId, ct);
        if (!result.Success || result.Payload is not AstraBlockDefinition definition)
            return Failed(result.Error ?? "Invalid ask arguments.");
        var block = IssuedAstraBlock.Create(request.UserId, definition);
        await execution.BlockStore.AddAsync(block, ct);
        object payload = definition.Form switch
        {
            AstraBlockKinds.Question => new AstraQuestionBlock(block.Id, definition.Questions!, block.ExpiresAtUtc,
                string.IsNullOrWhiteSpace(definition.Title) ? null : definition.Title, definition.Intro, definition.Progress),
            AstraBlockKinds.Choice => new AstraChoiceBlock(block.Id, definition.Title, definition.Options, definition.MultiSelect,
                definition.AllowFreeText, block.ExpiresAtUtc),
            _ => new AstraPickListBlock(block.Id, definition.Title,
                definition.Items.Select(item => new AstraPickListItem(item.Id, item.Label, item.Detail)).ToArray(),
                definition.Action!, block.ExpiresAtUtc)
        };
        results.AskBlock = new ChatBlockPart(definition.Form, payload);
        var user = await data.UserRepository.GetByIdAsync(request.UserId, ct);
        AnalyticsCapture.SafeCaptureUserEvent(execution.ProductAnalytics, logger, request.UserId,
            user?.Plan.ToString() ?? "Free", "astra_block_issued", new Dictionary<string, object>
            {
                ["form"] = definition.Form,
                ["count"] = definition.Questions?.Count ?? (definition.Form == AstraBlockKinds.Choice ? definition.Options.Count : definition.Items.Count)
            });
        return new ToolCallOutcome(new AiToolCallResult(call.Name, call.Id, true, block.Id.ToString(), null, null),
            null, null, null, null);
    }
}
