using Orbit.Domain.Interfaces;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Application.Common;

internal static class HabitCeilingLock
{
    public static string ForUser(Guid userId) => $"habit-ceiling:{userId}";

    public static Task AcquireAsync(
        IUnitOfWork unitOfWork,
        Guid userId,
        CancellationToken cancellationToken) =>
        unitOfWork.AcquireAdvisoryLockAsync(ForUser(userId), cancellationToken);

    public static Task<T> ExecuteAsync<T>(
        IUnitOfWork unitOfWork,
        Guid userId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
        {
            await AcquireAsync(unitOfWork, userId, transactionToken);
            return await operation(transactionToken);
        }, cancellationToken);

    public static Task<Result> ExecuteEntryAsync<TState>(
        IUnitOfWork unitOfWork,
        Guid userId,
        IPayGateService payGate,
        Func<CancellationToken, Task<Result<TState>>> prepare,
        Func<TState, bool> entersLiveRootSet,
        Func<TState, CancellationToken, Task<Result>> mutation,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            unitOfWork,
            userId,
            async transactionToken =>
            {
                var prepared = await prepare(transactionToken);
                if (prepared.IsFailure)
                    return prepared.PropagateError();

                if (entersLiveRootSet(prepared.Value))
                {
                    var allowance = await payGate.CanCreateHabits(userId, 1, transactionToken);
                    if (allowance.IsFailure)
                        return allowance;
                }

                return await mutation(prepared.Value, transactionToken);
            },
            cancellationToken);

    public static Task<Result<TResult>> ExecuteEntryAsync<TState, TResult>(
        IUnitOfWork unitOfWork,
        Guid userId,
        IPayGateService payGate,
        Func<CancellationToken, Task<Result<TState>>> prepare,
        Func<TState, bool> entersLiveRootSet,
        Func<TState, CancellationToken, Task<Result<TResult>>> mutation,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            unitOfWork,
            userId,
            async transactionToken =>
            {
                var prepared = await prepare(transactionToken);
                if (prepared.IsFailure)
                    return prepared.PropagateError<TResult>();

                if (entersLiveRootSet(prepared.Value))
                {
                    var allowance = await payGate.CanCreateHabits(userId, 1, transactionToken);
                    if (allowance.IsFailure)
                        return allowance.PropagateError<TResult>();
                }

                return await mutation(prepared.Value, transactionToken);
            },
            cancellationToken);
}

internal static class HabitLiveRootEntry
{
    public static bool FromRestore(Habit habit) =>
        habit.IsDeleted && habit.ParentHabitId is null && !habit.IsCompleted;

    public static bool FromPromotion(Habit habit, Guid? newParentId) =>
        !habit.IsDeleted
        && !habit.IsCompleted
        && habit.ParentHabitId is not null
        && newParentId is null;

    public static bool FromUnlog(Habit habit) =>
        !habit.IsDeleted && habit.IsCompleted && habit.ParentHabitId is null;

    public static bool FromUpdate(Habit habit, HabitUpdateParams update)
    {
        if (habit.IsDeleted || !habit.IsCompleted || habit.ParentHabitId is not null)
            return false;

        var remainsCompleted = habit.IsCompleted;
        if (update.IsGeneral == true)
            remainsCompleted = false;

        if ((update.ClearEndDate == true || update.EndDate.HasValue) && update.FrequencyUnit is not null)
        {
            var effectiveEndDate = update.ClearEndDate == true ? null : update.EndDate;
            var effectiveDueDate = update.DueDate ?? habit.DueDate;
            remainsCompleted = effectiveEndDate.HasValue && effectiveDueDate > effectiveEndDate.Value;
        }

        return !remainsCompleted;
    }
}
