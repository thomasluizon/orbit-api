using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Accountability.Services;

public class AccountabilityPairService(
    IGenericRepository<AccountabilityPair> pairRepository,
    IGenericRepository<AccountabilityPairHabit> pairHabitRepository,
    IGenericRepository<Habit> habitRepository)
{
    public async Task<AccountabilityPair?> FindActivePairBetweenAsync(Guid first, Guid second, CancellationToken cancellationToken)
    {
        return await pairRepository.FindOneTrackedAsync(
            p => p.Status != AccountabilityPairStatus.Ended
                 && ((p.RequesterId == first && p.AddresseeId == second)
                     || (p.RequesterId == second && p.AddresseeId == first)),
            cancellationToken: cancellationToken);
    }

    public async Task<AccountabilityPair?> FindParticipantPairAsync(Guid pairId, Guid userId, CancellationToken cancellationToken)
    {
        return await pairRepository.FindOneTrackedAsync(
            p => p.Id == pairId && (p.RequesterId == userId || p.AddresseeId == userId),
            cancellationToken: cancellationToken);
    }

    public async Task<int> CountActivePairsAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await pairRepository.CountAsync(
            p => p.Status != AccountabilityPairStatus.Ended
                 && (p.RequesterId == userId || p.AddresseeId == userId),
            cancellationToken);
    }

    public async Task<Result> ReplaceLinkedHabitsAsync(
        AccountabilityPair pair,
        Guid userId,
        IReadOnlyList<Guid> habitIds,
        CancellationToken cancellationToken)
    {
        var distinctHabitIds = habitIds.Distinct().ToList();

        var ownedCount = await habitRepository.CountAsync(
            h => distinctHabitIds.Contains(h.Id) && h.UserId == userId,
            cancellationToken);
        if (ownedCount != distinctHabitIds.Count)
            return Result.Failure(ErrorMessages.HabitNotFound);

        var existing = await pairHabitRepository.FindTrackedAsync(
            ph => ph.PairId == pair.Id && ph.UserId == userId,
            cancellationToken);
        pairHabitRepository.RemoveRange(existing);

        foreach (var habitId in distinctHabitIds)
            await pairHabitRepository.AddAsync(
                AccountabilityPairHabit.Create(pair.Id, userId, habitId), cancellationToken);

        return Result.Success();
    }
}
