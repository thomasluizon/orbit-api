using static Orbit.Application.Chat.Tools.ToolArgumentNames;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Orbit.Domain.Common;
using Orbit.Application.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task<Result<ChatResponse>?> RecoverExplicitHeadingsAsync(
        ProcessUserChatCommand request, ChatContext context, Result<AiResponse> response, CancellationToken ct)
    {
        if (request.BlockAnswer is not null || response.IsFailure && response.ErrorCode != ErrorCodes.AiEmptyResponse)
            return null;
        var headings = HabitHeadingParents.Read(request.Message, context.UserHabits);
        if (headings.Count == 0 || headings.Any(heading => heading.Candidates.Count == 0)
            || !request.Message.TrimStart().StartsWith(headings[0].Name, StringComparison.OrdinalIgnoreCase))
            return null;
        var items = ExplicitHeadingItems(headings);
        if (items is null || HasActionableHeadingCall(response, headings)) return null;
        var arguments = JsonSerializer.SerializeToElement(new JsonObject { ["habits"] = items });
        var held = await DispatchToolCallAsync(new(BulkCreateHabits, Guid.NewGuid().ToString(), arguments),
            request, execution.OperationExecutor, null, ct);
        return held.PendingOperation is { } pending
            ? Result.Success(PendingParentReply(request, pending))
            : Result.Failure<ChatResponse>(held.Operation.PolicyReason ?? "preview_unavailable", held.Operation.ErrorCode ?? "preview_unavailable");
    }

    private static bool HasActionableHeadingCall(Result<AiResponse> response, IReadOnlyList<HabitHeadingParents.Heading> headings) =>
        response.IsSuccess && response.Value.HasToolCalls && response.Value.ToolCalls!.Any(call =>
            call.Name != "create_habit" || call.Args.TryGetProperty("title", out var title)
                && headings.All(heading => HabitNameMatcher.Normalize(heading.Name) != HabitNameMatcher.Normalize(title.ToString())));

    private static JsonArray? ExplicitHeadingItems(IReadOnlyList<HabitHeadingParents.Heading> headings)
    {
        var items = new JsonArray();
        foreach (var heading in headings)
        {
            var match = ExplicitHeadingActivity().Match(heading.Body);
            if (!match.Success) return null;
            var cadence = HabitNameMatcher.Normalize(match.Groups["frequency"].Value);
            var item = new JsonObject
            {
                ["title"] = match.Groups["title"].Value.Trim(),
                ["frequency_unit"] = cadence is "once a week" or "weekly" or "uma vez por semana" or "semanalmente" ? "Week" : "Day",
                ["frequency_quantity"] = 1
            };
            HabitHeadingParents.Bind(item, heading, removeInventedDescription: false);
            items.Add(item);
        }
        return items;
    }

    [GeneratedRegex(@"^\s*(?<title>[^()\r\n]+?)\s*\((?<frequency>once a week|weekly|uma vez por semana|semanalmente|everyday|every day|daily|todo dia|todos os dias|diariamente)\)\s*[.;]?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitHeadingActivity();
}
