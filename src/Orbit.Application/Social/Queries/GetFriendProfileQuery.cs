using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Application.Profile.Queries;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Queries;

/// <summary>
/// A friend's profile stats, returned only to an accepted friend. Unlike the public profile this
/// carries no visibility flags: friends see the full streak, level, achievements, recent activity,
/// top habits, and any shared accountability or challenge context. Reuses <see cref="PublicAchievement"/>
/// and <see cref="TopHabit"/> so friend and public surfaces render the same shapes on the client.
/// </summary>
public record FriendProfileView(
    Guid UserId,
    string Handle,
    string DisplayName,
    int CurrentStreak,
    int LongestStreak,
    int Level,
    string LevelTitle,
    int TotalXp,
    DateTime? FriendsSinceUtc,
    IReadOnlyList<int> WeeklyActivity,
    IReadOnlyList<PublicAchievement> Achievements,
    IReadOnlyList<TopHabit> TopHabits,
    bool IsAccountabilityPartner,
    IReadOnlyList<FriendSharedChallenge> SharedChallenges);

public record FriendSharedChallenge(Guid Id, string Title);

public record GetFriendProfileQuery(Guid UserId, Guid FriendUserId) : IRequest<Result<FriendProfileView>>;

public class GetFriendProfileQueryHandler(
    SocialAccessGuard socialAccessGuard,
    FriendGraphService friendGraphService,
    IUserDateService userDateService,
    IGenericRepository<User> userRepository,
    IGenericRepository<UserAchievement> achievementRepository,
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<AccountabilityPair> accountabilityPairRepository,
    IGenericRepository<Challenge> challengeRepository) : IRequestHandler<GetFriendProfileQuery, Result<FriendProfileView>>
{
    private const int ActivityWindowDays = 7;

    public async Task<Result<FriendProfileView>> Handle(GetFriendProfileQuery request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError<FriendProfileView>();

        var friendship = await friendGraphService.FindFriendshipAsync(request.UserId, request.FriendUserId, cancellationToken);
        if (friendship is null || friendship.Status != FriendshipStatus.Accepted)
            return Result.Failure<FriendProfileView>(ErrorMessages.UserNotFound);

        var matches = await userRepository.FindAsync(u => u.Id == request.FriendUserId, cancellationToken);
        var friend = matches.FirstOrDefault();
        if (friend is null)
            return Result.Failure<FriendProfileView>(ErrorMessages.UserNotFound);

        var level = LevelDefinitions.GetLevelForXp(friend.TotalXp);
        var achievements = await PublicAchievementsBuilder.BuildAsync(achievementRepository, friend.Id, cancellationToken);

        var friendHabits = await habitRepository.FindAsync(
            h => h.UserId == friend.Id,
            q => q.Include(h => h.Logs.Where(l => l.Value > 0)),
            cancellationToken);

        var friendToday = await userDateService.GetUserTodayAsync(friend.Id, cancellationToken);
        var weeklyActivity = BuildWeeklyActivity(friendHabits, friendToday);
        var topHabits = TopHabitsBuilder.Build(friendHabits);

        var isAccountabilityPartner = await accountabilityPairRepository.AnyAsync(
            p => p.Status == AccountabilityPairStatus.Accepted
                 && ((p.RequesterId == request.UserId && p.AddresseeId == request.FriendUserId)
                     || (p.RequesterId == request.FriendUserId && p.AddresseeId == request.UserId)),
            cancellationToken);

        var sharedChallenges = await BuildSharedChallengesAsync(request.UserId, request.FriendUserId, cancellationToken);

        return Result.Success(new FriendProfileView(
            friend.Id,
            friend.Handle ?? string.Empty,
            friend.Name,
            friend.CurrentStreak,
            friend.LongestStreak,
            level.Level,
            level.Title,
            friend.TotalXp,
            friendship?.RespondedAtUtc,
            weeklyActivity,
            achievements,
            topHabits,
            isAccountabilityPartner,
            sharedChallenges));
    }

    private static IReadOnlyList<int> BuildWeeklyActivity(IEnumerable<Habit> habits, DateOnly today)
    {
        var windowStart = today.AddDays(-(ActivityWindowDays - 1));
        var counts = new int[ActivityWindowDays];

        foreach (var log in habits.SelectMany(h => h.Logs))
        {
            if (log.Value <= 0)
                continue;

            var index = log.Date.DayNumber - windowStart.DayNumber;
            if (index >= 0 && index < ActivityWindowDays)
                counts[index]++;
        }

        return counts;
    }

    private async Task<IReadOnlyList<FriendSharedChallenge>> BuildSharedChallengesAsync(
        Guid callerId, Guid friendId, CancellationToken cancellationToken)
    {
        var challenges = await challengeRepository.FindAsync(
            c => c.Status == ChallengeStatus.Active
                 && c.Participants.Any(p => p.UserId == callerId && p.LeftAtUtc == null)
                 && c.Participants.Any(p => p.UserId == friendId && p.LeftAtUtc == null),
            cancellationToken);

        return challenges
            .OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .Select(c => new FriendSharedChallenge(c.Id, c.Title))
            .ToList();
    }
}
