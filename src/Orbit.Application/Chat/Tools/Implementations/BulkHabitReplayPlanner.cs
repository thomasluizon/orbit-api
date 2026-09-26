using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed record BulkHabitReplayPlan(DateOnly Date, IReadOnlyList<Guid> HabitIds);

public sealed class BulkHabitReplayPlanner(
    IIdempotencyContext idempotencyContext,
    IIdempotencyStore idempotencyStore,
    IUnitOfWork unitOfWork)
{
    public async Task<BulkHabitReplayPlan> GetOrCreateAsync(
        Guid userId,
        string commandType,
        Func<CancellationToken, Task<BulkHabitReplayPlan>> createPlan,
        CancellationToken cancellationToken)
    {
        if (!idempotencyContext.TryGetRequestKey(out var keyUserId, out var idempotencyKey)
            || keyUserId != userId)
            return await createPlan(cancellationToken);

        var planType = $"{commandType}:plan";
        var planOrdinal = idempotencyContext.NextRequestOrdinal(planType);
        var stored = await idempotencyStore.FindResponseBodyAsync(
            userId, idempotencyKey, planType, planOrdinal, cancellationToken);
        if (stored is not null)
            return Deserialize(stored);

        var plan = await createPlan(cancellationToken);
        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                var reservation = idempotencyStore.Reserve(userId, idempotencyKey, planType, planOrdinal);
                reservation.SetResponseBody(JsonSerializer.Serialize(plan));
                await unitOfWork.SaveChangesAsync(ct);
            }, cancellationToken);
        }
        catch (DbUpdateException exception) when (DbUniqueViolation.IsUniqueViolation(exception))
        {
            stored = await idempotencyStore.FindResponseBodyAsync(
                userId, idempotencyKey, planType, planOrdinal, cancellationToken);
            if (stored is null)
                throw;
            return Deserialize(stored);
        }

        return plan;
    }

    private static BulkHabitReplayPlan Deserialize(string body) =>
        JsonSerializer.Deserialize<BulkHabitReplayPlan>(body)!;
}
