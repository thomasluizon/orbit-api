using System.Text.Json;
using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat.Tools;

public interface IHabitCreationCapacityTool
{
    Task<Result> CheckCreationCapacityAsync(JsonElement args, Guid userId, CancellationToken ct);
}

internal static class HabitCreationCapacity
{
    public static Task<Result> CheckAsync(IMediator mediator, Guid userId, IEnumerable<JsonElement> items,
        bool hasSharedParent, CancellationToken ct)
    {
        var count = items.Sum(item => CountNewRoots(item, hasSharedParent, true));
        return count == 0 ? Task.FromResult(Result.Success())
            : mediator.Send(new CheckChatCommandQuery(new HabitCreationCapacityRequest(userId, count)), ct);
    }

    private static int CountNewRoots(JsonElement item, bool hasParent, bool topLevel)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return 0;
        if (topLevel && item.TryGetProperty(ToolArgumentNames.ParentHabitId, out var parent)
            && parent.ValueKind == JsonValueKind.String)
            hasParent = true;
        if (JsonArgumentParser.GetOptionalString(item, HabitPlacementRecommendation.Argument) == HabitPlacement.NewParent)
            hasParent = false;
        var count = !hasParent && !item.TryGetProperty(ToolArgumentNames.HabitId, out _) ? 1 : 0;
        return count + (item.TryGetProperty(ToolArgumentNames.SubHabits, out var children)
            && children.ValueKind == JsonValueKind.Array
                ? children.EnumerateArray().Sum(child => CountNewRoots(child, true, false)) : 0);
    }
}

internal sealed record HabitCreationCapacityRequest(Guid UserId, int RootCount);
