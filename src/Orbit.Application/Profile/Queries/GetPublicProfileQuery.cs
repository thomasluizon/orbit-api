using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Queries;

/// <summary>
/// The no-auth public profile projection. Every stat field is nullable and is populated only when
/// the owner's corresponding visibility flag is on; an off field is omitted here, never serialized.
/// Carries no PII beyond the inherently-public display name and handle (no email, no user id).
/// </summary>
public record PublicProfileView(
    string DisplayName,
    string? Handle,
    string? Language,
    int? CurrentStreak,
    int? LongestStreak,
    int? Level,
    string? LevelTitle,
    IReadOnlyList<PublicAchievement>? Achievements,
    IReadOnlyList<string>? TopHabits);

public record PublicAchievement(string Name, string IconKey, string Rarity);

public record GetPublicProfileQuery(string Slug) : IRequest<Result<PublicProfileView>>;

public class GetPublicProfileQueryHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<UserAchievement> achievementRepository,
    IGenericRepository<Habit> habitRepository) : IRequestHandler<GetPublicProfileQuery, Result<PublicProfileView>>
{
    private const int TopHabitsCount = 3;

    public async Task<Result<PublicProfileView>> Handle(GetPublicProfileQuery request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Slug))
            return Result.Failure<PublicProfileView>(ErrorMessages.UserNotFound);

        var matches = await userRepository.FindAsync(u => u.PublicProfileSlug == request.Slug, cancellationToken);
        var user = matches.FirstOrDefault();
        if (user is null)
            return Result.Failure<PublicProfileView>(ErrorMessages.UserNotFound);

        var currentLevel = user.PublicProfileShowLevel ? LevelDefinitions.GetLevelForXp(user.TotalXp) : null;

        var achievements = user.PublicProfileShowAchievements
            ? await PublicAchievementsBuilder.BuildAsync(achievementRepository, user.Id, cancellationToken)
            : null;

        var topHabits = user.PublicProfileShowTopHabits
            ? await BuildTopHabitsAsync(user.Id, cancellationToken)
            : null;

        return Result.Success(new PublicProfileView(
            user.Name,
            user.Handle,
            user.Language,
            user.PublicProfileShowStreak ? user.CurrentStreak : null,
            user.PublicProfileShowStreak ? user.LongestStreak : null,
            currentLevel?.Level,
            currentLevel?.Title,
            achievements,
            topHabits));
    }

    private async Task<IReadOnlyList<string>> BuildTopHabitsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var habits = await habitRepository.FindAsync(
            h => h.UserId == userId && h.ParentHabitId == null && !h.IsBadHabit,
            q => q.Include(h => h.Logs.Where(l => l.Value > 0)),
            cancellationToken);

        return habits
            .Select(h => new { h.Title, CompletionCount = h.Logs.Count })
            .Where(h => h.CompletionCount > 0)
            .OrderByDescending(h => h.CompletionCount)
            .ThenBy(h => h.Title, StringComparer.OrdinalIgnoreCase)
            .Take(TopHabitsCount)
            .Select(h => h.Title)
            .ToList();
    }
}
