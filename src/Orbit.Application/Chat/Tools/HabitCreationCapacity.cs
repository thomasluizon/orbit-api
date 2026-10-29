using System.Text.Json;
using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat.Tools;

internal static class HabitCreationCapacity
{
    public static Task<Result> CheckAsync(IMediator mediator, Guid userId, IEnumerable<JsonElement> items,
        bool hasSharedParent, CancellationToken ct)
    {
        var count = items.Sum(item => CountNewRoots(item, hasSharedParent, true));
        return count == 0 ? Task.FromResult(Result.Success())
            : mediator.Send(new CheckHabitCreationCapacityQuery(userId, count), ct);
    }

    private static int CountNewRoots(JsonElement item, bool hasParent, bool topLevel)
    {
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

public sealed record CheckHabitCreationCapacityQuery(Guid UserId, int RootCount) : IRequest<Result>;

public sealed class CheckHabitCreationCapacityQueryHandler(IPayGateService payGate, IGenericRepository<User> users)
    : IRequestHandler<CheckHabitCreationCapacityQuery, Result>
{
    public async Task<Result> Handle(CheckHabitCreationCapacityQuery request, CancellationToken ct)
    {
        var result = await payGate.CanCreateHabits(request.UserId, request.RootCount, ct);
        if (result.IsSuccess)
            return result;
        var user = await users.GetByIdAsync(request.UserId, ct);
        return ErrorCopy.TryResolve(result.ErrorCode, LocaleHelper.IsPortuguese(user?.Language), result.ErrorArgs, out var message)
            ? Result.Failure(message, result.ErrorCode, result.ErrorArgs) : result;
    }
}
