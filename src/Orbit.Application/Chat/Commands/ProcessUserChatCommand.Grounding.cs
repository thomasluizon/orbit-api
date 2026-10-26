using Microsoft.Extensions.Logging;
using Orbit.Application.Chat.Grounding;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private sealed record GroundingTurn(bool Enforce, IReadOnlyList<string> KnownNames, string? Language);
    private sealed record GroundedReply(string? Message, IReadOnlyList<string>? FollowUps, TurnFactSet Facts);

    private static ResponseCards ApplyGroundingToCards(ResponseCards cards, string? message) => cards with
    {
        AiMessage = message,
        Parts = message is null
            ? cards.Parts?.Where(part => part is not ChatTextPart).ToArray()
            : cards.Parts
    };

    private async Task<GroundingTurn> LoadGroundingTurnAsync(ChatContext context, CancellationToken cancellationToken) => new(
        await appConfig.GetAsync(AppConfigKeys.AstraGroundingEnforce, false, cancellationToken),
        context.ActiveHabits.Select(habit => habit.Title)
            .Concat(context.ActiveGoals.Select(goal => goal.Title))
            .Concat(context.UserTags.Select(tag => tag.Name))
            .Concat(context.ChecklistTemplateNames).ToArray(),
        GetUserLanguage(context.User));

    private static async Task<string?> GetGroundedCachedFaqAnswerAsync(
        (string Key, string Locale)? match, GroundingTurn turn, ProcessUserChatCommand request, bool suppressDeltas = false)
    {
        if (request.BlockAnswer is not null)
            return null;
        var cached = TryGetCachedFaqAnswer(match);
        if (cached is null || GroundednessChecker.Check(cached, TurnFactSet.Empty, turn.KnownNames, turn.Language).Count > 0)
            return null;
        if (turn.Enforce && request.StreamSink is not null && !suppressDeltas)
            await request.StreamSink(ChatStreamEvent.Delta(cached));
        return cached;
    }

    private async Task<GroundedReply> ApplyGroundingAsync(
        string? message, IReadOnlyList<string>? followUps, ToolExecutionAccumulator results,
        ProcessUserChatCommand request, GroundingTurn turn)
    {
        var previewClaims = results.PendingOperations.Count > 0;
        if (previewClaims) turn = turn with { Enforce = true };
        var facts = previewClaims
            ? PreviewFacts(results.PendingOperations)
            : TurnFactSet.FromToolResults(results.ToolResults);
        var textViolations = CheckGrounding(message, "Message", facts, request, turn);
        var followUpViolations = CheckGrounding(followUps is null ? null : string.Join("\n", followUps), "FollowUps", facts, request, turn);
        if (HasPendingWritePreview(results) && !results.IsPreviewDiscussion)
        {
            message = null;
            followUps = null;
        }
        if (turn.Enforce)
        {
            if (textViolations)
                message = null;
            if (textViolations || followUpViolations)
                followUps = null;
            if (request.StreamSink is not null)
            {
                await request.StreamSink(ChatStreamEvent.Reset());
                if (!string.IsNullOrEmpty(message))
                    await request.StreamSink(ChatStreamEvent.Delta(message));
            }
        }
        return new GroundedReply(message, followUps, facts);
    }

    private static TurnFactSet PreviewFacts(IEnumerable<Orbit.Domain.Models.PendingAgentOperation> previews) =>
        TurnFactSet.FromToolResults(previews.Select(pending => new Orbit.Domain.Models.AiToolCallResult(
            pending.CapabilityId, pending.Id.ToString(), true, null, null, null,
            new
            {
                count = pending.Items?.Count,
                preview_items = pending.Items?.Select(item => new
                {
                    item.EntityName,
                    parents = item.Fields.Where(field => field.Field == "parent_habit_id"
                        && field.ProposedValue is { ValueKind: System.Text.Json.JsonValueKind.String } value
                        && Guid.TryParse(value.GetString(), out _)).Select(field => new { EntityName = field.NewValue }),
                    fields = item.Fields.Select(field => new { field.Field, field.NewValue, field.ProposedValue })
                })
            })));

    private bool CheckGrounding(string? text, string location, TurnFactSet facts, ProcessUserChatCommand request, GroundingTurn turn)
    {
        var violations = GroundednessChecker.Check(text, facts, turn.KnownNames, turn.Language, request.Message);
        foreach (var violation in violations)
        {
            logger.LogWarning("Grounding violation {ViolationKind} {Claim} at {Location} for {UserId} {CorrelationId}; enforced {Enforced}",
                violation.Kind, violation.Claim, location, request.UserId, request.CorrelationId, turn.Enforce);
        }
        return violations.Count > 0;
    }
}
