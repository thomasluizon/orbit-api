using Microsoft.Extensions.Logging;
using Orbit.Application.Chat.Grounding;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private sealed record GroundingTurn(bool Enforce, IReadOnlyList<string> KnownNames, string? Language);
    private sealed record GroundedReply(string? Message, IReadOnlyList<string>? FollowUps, TurnFactSet Facts);

    private async Task<GroundingTurn> LoadGroundingTurnAsync(ChatContext context, CancellationToken cancellationToken) => new(
        await appConfig.GetAsync(AppConfigKeys.AstraGroundingEnforce, false, cancellationToken),
        context.ActiveHabits.Select(habit => habit.Title)
            .Concat(context.ActiveGoals.Select(goal => goal.Title))
            .Concat(context.UserTags.Select(tag => tag.Name))
            .Concat(context.ChecklistTemplates.Select(template => template.Name)).ToArray(),
        GetUserLanguage(context.User));

    private static async Task<string?> GetGroundedCachedFaqAnswerAsync(
        (string Key, string Locale)? match, GroundingTurn turn, Func<ChatStreamEvent, Task>? sink)
    {
        var cached = TryGetCachedFaqAnswer(match);
        if (cached is null || GroundednessChecker.Check(cached, TurnFactSet.Empty, turn.KnownNames, turn.Language).Count > 0)
            return null;
        if (turn.Enforce && sink is not null)
            await sink(ChatStreamEvent.Delta(cached));
        return cached;
    }

    private async Task<GroundedReply> ApplyGroundingAsync(
        string? message, IReadOnlyList<string>? followUps, ToolExecutionAccumulator results,
        ProcessUserChatCommand request, GroundingTurn turn)
    {
        var facts = TurnFactSet.FromToolResults(results.ToolResults);
        var textViolations = CheckGrounding(message, "Message", facts, request, turn);
        var followUpViolations = CheckGrounding(followUps is null ? null : string.Join("\n", followUps), "FollowUps", facts, request, turn);
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

    private bool CheckGrounding(string? text, string location, TurnFactSet facts, ProcessUserChatCommand request, GroundingTurn turn)
    {
        var violations = GroundednessChecker.Check(text, facts, turn.KnownNames, turn.Language);
        foreach (var violation in violations)
        {
            logger.LogWarning("Grounding violation {ViolationKind} {Claim} at {Location} for {UserId} {CorrelationId}; enforced {Enforced}",
                violation.Kind, violation.Claim, location, request.UserId, request.CorrelationId, turn.Enforce);
        }
        return violations.Count > 0;
    }
}
