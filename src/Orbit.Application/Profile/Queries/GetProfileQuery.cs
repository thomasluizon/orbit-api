using MediatR;
using Orbit.Application.ApiKeys.Services;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Queries;

public record ProfileResponse(
    Guid UserId,
    string Name,
    string Email,
    string? TimeZone,
    /// <summary>Required by published client schemas; retirement is tracked in thomasluizon/orbit-tickets#685.</summary>
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
    /// <summary>Required by published client schemas; retirement is tracked in thomasluizon/orbit-tickets#685.</summary>
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
    /// <summary>Always null; retained for installed clients until thomasluizon/orbit-tickets#685 permits removal under thomasluizon/orbit-tickets#1654.</summary>
    string? Handle,
    /// <summary>Compatibility field retained until thomasluizon/orbit-tickets#685.</summary>
    bool SocialOptIn,
    bool Uses24HourClock = true,
    PublicProfileSettings? PublicProfile = null,
    bool ProactiveAstraEnabled = false,
    bool? MarketingEmailConsent = null,
    DateOnly? LastCompletionDate = null,
    int? ActiveApiKeyCount = null,
    string? BillingProvider = null,
    string? AiMessagesPeriod = null,
    DateTime? AiMessagesResetsAt = null);

public record GetProfileQuery(Guid UserId) : IRequest<Result<ProfileResponse>>;

/// <summary>Groups the services used to resolve streaks as of the user's local day.</summary>
public record ProfileStreakServices(IUserDateService UserDateService, IUserStreakService UserStreakService);

public class GetProfileQueryHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<ApiKey> apiKeyRepository,
    IGenericRepository<StreakFreeze> streakFreezeRepository,
    IHabitLogReader habitLogReader,
    ProfileStreakServices streakServices,
    IFeatureFlagService featureFlagService,
    IPayGateService payGate) : IRequestHandler<GetProfileQuery, Result<ProfileResponse>>
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

        var today = await streakServices.UserDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var currentStreak = user.LastActiveDate < today.AddDays(-1)
            ? (await streakServices.UserStreakService.CalculateAsync(request.UserId, cancellationToken))?.CurrentStreak ?? 0
            : user.CurrentStreak;
        var windowStart = today.AddDays(-29);
        var recentFreezes = await streakFreezeRepository.FindAsync(
            sf => sf.UserId == request.UserId && sf.UsedOnDate >= windowStart,
            cancellationToken);
        var freezesAvailable = Math.Max(0, AppConstants.MaxStreakFreezesPerMonth - recentFreezes.Count);
        var liveCompletionDate = await habitLogReader.GetLastCompletionDateAsync(request.UserId, cancellationToken);
        var lastCompletionDate = user.GetLastCompletionDate(liveCompletionDate);
        var nowAtUtc = DateTime.UtcNow;
        var activeApiKeyCount = await apiKeyRepository.CountAsync(
            ActiveApiKeyPredicate.ForUser(request.UserId, nowAtUtc),
            cancellationToken);

        return Result.Success(new ProfileResponse(
            user.Id,
            user.Name,
            user.Email,
            user.TimeZone,
            false,
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
            user.GetAiMessagesUsedForQuota(today),
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
            currentStreak,
            user.LongestStreak,
            freezesAvailable,
            user.ThemePreference,
            ColorSchemes.Granted,
            user.GoogleCalendarAutoSyncEnabled,
            user.GoogleCalendarAutoSyncStatus ?? GoogleCalendarAutoSyncStatus.Idle,
            user.GoogleCalendarLastSyncedAt,
            canViewGamification,
            null,
            false,
            TimeFormatResolver.Uses24HourClock(user.Uses24HourClockPreference, user.TimeZone),
            null,
            user.EffectiveProactivePushEnabled,
            user.MarketingEmailConsent,
            lastCompletionDate,
            activeApiKeyCount,
            user.SubscriptionSource.ToBillingProvider(),
            AiAllowancePeriod.ActiveFor(today, user.HasProAccess, user.WeekStartDay, user.AiMessagesWeekStart).Name,
            AiAllowanceReset.GetResetsAtUtc(user, today)));
    }
}
