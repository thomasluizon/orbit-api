using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed record BulkHabitReplayPlan(DateOnly Date, IReadOnlyList<Guid> HabitIds);

public sealed class BulkHabitReplayPlanner(
    IIdempotencyContext idempotencyContext,
    IIdempotencyStore idempotencyStore,
    IUnitOfWork unitOfWork)
{
    public async Task<ToolResult> ExecuteAsync<TItem, TResult>(
        JsonElement args,
        Guid userId,
        IGenericRepository<Habit> habitRepository,
        IUserDateService userDateService,
        string commandType,
        Func<Guid, DateOnly, TItem> createItem,
        Func<IReadOnlyList<TItem>, CancellationToken, Task<Result<TResult>>> executeChunk,
        Func<TResult, int> countApplied,
        string verb,
        string noMatchError,
        CancellationToken cancellationToken)
    {
        var (filter, filterError) = BulkHabitToolArguments.ParseActionFilter(args);
        if (filterError is not null)
            return new ToolResult(false, Error: filterError);
        if (args.TryGetProperty("date", out var dateElement)
            && dateElement.ValueKind != JsonValueKind.Null
            && (dateElement.ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(dateElement.GetString(), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            return new ToolResult(false, Error: "date must use YYYY-MM-DD format.");

        var hasDate = args.TryGetProperty("date", out var receivedDate);
        var invocation = JsonSerializer.Serialize(new
        {
            filter,
            hasDate,
            date = hasDate ? receivedDate.GetRawText() : null
        });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(invocation)));
        var plan = await GetOrCreateAsync(userId, commandType, fingerprint, async token =>
        {
            var date = !args.TryGetProperty("date", out var selectedDate) || selectedDate.ValueKind == JsonValueKind.Null
                ? await userDateService.GetUserTodayAsync(userId, token)
                : DateOnly.ParseExact(selectedDate.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var habits = await BulkHabitSelection.LoadAsync(habitRepository, userId, filter!, token);
            return new BulkHabitReplayPlan(date, habits.Select(habit => habit.Id).ToArray());
        }, cancellationToken);
        if (plan is null)
            return new ToolResult(false, Error: "This request was started by an earlier server version. Nothing more was changed. Check the habits and repeat the request.");
        if (plan.HabitIds.Count == 0)
            return new ToolResult(false, Error: noMatchError);

        return await BulkUpdateHabitsTool.ExecuteInChunksAsync(
            plan.HabitIds.Select(id => createItem(id, plan.Date)).ToList(),
            executeChunk,
            countApplied,
            verb,
            cancellationToken);
    }

    public async Task<BulkHabitReplayPlan?> GetOrCreateAsync(
        Guid userId,
        string commandType,
        string invocationFingerprint,
        Func<CancellationToken, Task<BulkHabitReplayPlan>> createPlan,
        CancellationToken cancellationToken)
    {
        if (!idempotencyContext.TryGetRequestKey(out var keyUserId, out var idempotencyKey)
            || keyUserId != userId)
            return await createPlan(cancellationToken);

        var planType = $"{commandType}:plan:{invocationFingerprint}";
        var planOrdinal = idempotencyContext.NextRequestOrdinal(planType);
        var stored = await idempotencyStore.FindResponseBodyAsync(
            userId, idempotencyKey, planType, planOrdinal, cancellationToken);
        if (stored is not null)
            return Deserialize(stored);

        if (await idempotencyStore.FindResponseBodyAsync(
                userId, idempotencyKey, commandType, 0, cancellationToken) is not null)
            return null;

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
