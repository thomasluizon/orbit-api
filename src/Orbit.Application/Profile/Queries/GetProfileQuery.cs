using MediatR;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Queries;

public record ProfileResponse(
    string Name,
    string Email,
    string? TimeZone,
    bool AiMemoryEnabled,
    bool AiSummaryEnabled,
    bool HasCompletedOnboarding,
    bool HasCompletedTour,
    bool HasCreatedFirstHabit,
    bool HasLoggedFirstHabit,
    bool HasTriedAstra,
    bool HasCompletedOnboardingChecklist,
    string? Language,
    string Plan,
    bool HasProAccess,
    bool IsTrialActive,
    DateTime? TrialEndsAt,
    DateTime? PlanExpiresAt,
    int AiMessagesUsed,
    int AiMessagesLimit,
    bool HasImportedCalendar,
    bool HasSeenImportPrompt,
    bool HasGoogleConnection,
    string? SubscriptionInterval,
    string? SubscriptionSource,
    bool IsLifetimePro,
    int WeekStartDay,
    int TotalXp,
    int Level,
    string LevelTitle,
    int AdRewardsClaimedToday,
    int CurrentStreak,
    int LongestStreak,
    int StreakFreezesAvailable,
    string? ThemePreference,
    string? ColorScheme,
    bool GoogleCalendarAutoSyncEnabled,
    GoogleCalendarAutoSyncStatus GoogleCalendarAutoSyncStatus,
    DateTime? GoogleCalendarLastSyncedAt,
    bool CanViewGamification,
    string? Handle,
    bool SocialOptIn,
    bool Uses24HourClock = true,
    PublicProfileSettings? PublicProfile = null,
    bool ProactiveAstraEnabled = false);

public record GetProfileQuery(Guid UserId) : IRequest<Result<ProfileResponse>>;

public class GetProfileQueryHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<StreakFreeze> streakFreezeRepository,
    IUserDateService userDateService,
    IFeatureFlagService featureFlagService,
    IPayGateService payGate,
    IOptions<FrontendSettings> frontendSettings) : IRequestHandler<GetProfileQuery, Result<ProfileResponse>>
{
    public async Task<Result<ProfileResponse>> Handle(GetProfileQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);

        if (user is null)
            return Result.Failure<ProfileResponse>(ErrorMessages.UserNotFound);

        var aiMessageLimit = await payGate.GetAiMessageLimit(request.UserId, cancellationToken);

        var currentLevel = LevelDefinitions.GetLevelForXp(user.TotalXp);
        var levelTitle = currentLevel.Title;

        var enabledFlags = await featureFlagService.GetEnabledKeysForUserAsync(request.UserId, cancellationToken);
        var canViewGamification = user.HasProAccess || enabledFlags.Contains(FeatureFlagKeys.GamificationFreeTier);

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var windowStart = today.AddDays(-29);
        var recentFreezes = await streakFreezeRepository.FindAsync(
            sf => sf.UserId == request.UserId && sf.UsedOnDate >= windowStart,
            cancellationToken);
        var freezesAvailable = Math.Max(0, AppConstants.MaxStreakFreezesPerMonth - recentFreezes.Count);

        var publicProfile = new PublicProfileSettings(
            user.PublicProfileSlug is not null,
            user.PublicProfileSlug,
            user.PublicProfileSlug is null ? null : $"{frontendSettings.Value.BaseUrl}/u/{user.PublicProfileSlug}",
            user.PublicProfileShowStreak,
            user.PublicProfileShowLevel,
            user.PublicProfileShowAchievements,
            user.PublicProfileShowTopHabits);

        return Result.Success(new ProfileResponse(
            user.Name,
            user.Email,
            user.TimeZone,
            user.AiMemoryEnabled,
            user.AiSummaryEnabled,
            user.HasCompletedOnboarding,
            user.HasCompletedTour,
            user.HasCreatedFirstHabit,
            user.HasLoggedFirstHabit,
            user.HasTriedAstra,
            user.HasCompletedOnboardingChecklist,
            user.Language,
            user.HasProAccess ? "pro" : "free",
            user.HasProAccess,
            user.IsTrialActive,
            user.TrialEndsAt,
            user.PlanExpiresAt,
            user.AiMessagesUsedThisMonth,
            aiMessageLimit,
            user.HasImportedCalendar,
            user.HasSeenImportPrompt,
            user.GoogleAccessToken is not null,
            user.SubscriptionInterval?.ToString().ToLowerInvariant(),
            user.SubscriptionSource.ToApiValue(),
            user.IsLifetimePro,
            user.WeekStartDay,
            user.TotalXp,
            currentLevel.Level,
            levelTitle,
            user.LastAdRewardLocalDate == today
                ? user.AdRewardsClaimedToday
                : 0,
            user.CurrentStreak,
            user.LongestStreak,
            freezesAvailable,
            user.ThemePreference,
            user.ColorScheme,
            user.GoogleCalendarAutoSyncEnabled,
            user.GoogleCalendarAutoSyncStatus ?? GoogleCalendarAutoSyncStatus.Idle,
            user.GoogleCalendarLastSyncedAt,
            canViewGamification,
            user.Handle,
            user.SocialOptIn,
            TimeFormatResolver.Uses24HourClock(user.TimeZone),
            publicProfile,
            user.ProactiveAstraEnabled));
    }
}
