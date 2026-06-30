using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Accountability.Queries;

public record AccountabilityBuddySummary(Guid UserId, string Handle, string DisplayName);

public record AccountabilityPairDto(
    Guid Id,
    AccountabilityBuddySummary Buddy,
    AccountabilityCadence Cadence,
    AccountabilityPairStatus Status,
    bool IsInitiatedByMe,
    IReadOnlyList<Guid> MyHabitIds,
    IReadOnlyList<Guid> BuddyHabitIds,
    DateOnly? MyLastCheckInDate,
    DateOnly? BuddyLastCheckInDate,
    DateTime CreatedAtUtc);

public record AccountabilityPairsResponse(
    IReadOnlyList<AccountabilityPairDto> ActivePairs,
    IReadOnlyList<AccountabilityPairDto> IncomingInvites,
    IReadOnlyList<AccountabilityPairDto> OutgoingInvites);

public record GetAccountabilityPairsQuery(Guid UserId) : IRequest<Result<AccountabilityPairsResponse>>;

public class GetAccountabilityPairsQueryHandler(
    SocialAccessGuard socialAccessGuard,
    IGenericRepository<AccountabilityPair> pairRepository,
    IGenericRepository<AccountabilityPairHabit> pairHabitRepository,
    IGenericRepository<AccountabilityCheckIn> checkInRepository,
    IGenericRepository<User> userRepository) : IRequestHandler<GetAccountabilityPairsQuery, Result<AccountabilityPairsResponse>>
{
    public async Task<Result<AccountabilityPairsResponse>> Handle(GetAccountabilityPairsQuery request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError<AccountabilityPairsResponse>();

        var pairs = await pairRepository.FindAsync(
            p => p.Status != AccountabilityPairStatus.Ended
                 && (p.RequesterId == request.UserId || p.AddresseeId == request.UserId),
            cancellationToken);

        if (pairs.Count == 0)
            return Result.Success(new AccountabilityPairsResponse([], [], []));

        var pairIds = pairs.Select(p => p.Id).ToHashSet();
        var buddyIds = pairs.Select(p => OtherId(p, request.UserId)).ToHashSet();

        var buddies = await userRepository.FindAsync(u => buddyIds.Contains(u.Id), cancellationToken);
        var buddiesById = buddies.ToDictionary(u => u.Id);

        var linkedHabits = await pairHabitRepository.FindAsync(ph => pairIds.Contains(ph.PairId), cancellationToken);
        var habitsByPair = linkedHabits
            .GroupBy(ph => ph.PairId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var checkIns = await checkInRepository.FindAsync(c => pairIds.Contains(c.PairId), cancellationToken);
        var lastCheckInByPairUser = checkIns
            .GroupBy(c => (c.PairId, c.UserId))
            .ToDictionary(g => g.Key, g => g.Max(c => c.Date));

        var activePairs = new List<AccountabilityPairDto>();
        var incomingInvites = new List<AccountabilityPairDto>();
        var outgoingInvites = new List<AccountabilityPairDto>();

        foreach (var pair in pairs)
        {
            var buddyId = OtherId(pair, request.UserId);
            if (!buddiesById.TryGetValue(buddyId, out var buddy))
                continue;

            var dto = BuildDto(pair, request.UserId, buddyId, buddy, habitsByPair, lastCheckInByPairUser);

            if (pair.Status == AccountabilityPairStatus.Accepted)
                activePairs.Add(dto);
            else if (pair.AddresseeId == request.UserId)
                incomingInvites.Add(dto);
            else
                outgoingInvites.Add(dto);
        }

        var response = new AccountabilityPairsResponse(
            activePairs.OrderBy(p => p.Buddy.DisplayName).ToList(),
            incomingInvites.OrderByDescending(p => p.CreatedAtUtc).ToList(),
            outgoingInvites.OrderByDescending(p => p.CreatedAtUtc).ToList());

        return Result.Success(response);
    }

    private static AccountabilityPairDto BuildDto(
        AccountabilityPair pair,
        Guid userId,
        Guid buddyId,
        User buddy,
        IReadOnlyDictionary<Guid, List<AccountabilityPairHabit>> habitsByPair,
        IReadOnlyDictionary<(Guid PairId, Guid UserId), DateOnly> lastCheckInByPairUser)
    {
        var linkedHabits = habitsByPair.TryGetValue(pair.Id, out var habits)
            ? habits
            : new List<AccountabilityPairHabit>();
        var myHabitIds = linkedHabits.Where(h => h.UserId == userId).Select(h => h.HabitId).ToList();
        var buddyHabitIds = linkedHabits.Where(h => h.UserId == buddyId).Select(h => h.HabitId).ToList();

        DateOnly? myLastCheckInDate = lastCheckInByPairUser.TryGetValue((pair.Id, userId), out var mine) ? mine : null;
        DateOnly? buddyLastCheckInDate = lastCheckInByPairUser.TryGetValue((pair.Id, buddyId), out var theirs) ? theirs : null;

        return new AccountabilityPairDto(
            pair.Id,
            new AccountabilityBuddySummary(buddyId, buddy.Handle ?? string.Empty, buddy.Name),
            pair.Cadence,
            pair.Status,
            pair.RequesterId == userId,
            myHabitIds,
            buddyHabitIds,
            myLastCheckInDate,
            buddyLastCheckInDate,
            pair.CreatedAtUtc);
    }

    private static Guid OtherId(AccountabilityPair pair, Guid userId) =>
        pair.RequesterId == userId ? pair.AddresseeId : pair.RequesterId;
}
