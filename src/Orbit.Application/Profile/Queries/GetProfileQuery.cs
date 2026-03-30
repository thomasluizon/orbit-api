using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Queries;

public record ProfileResponse(
    string Name,
    string Email,
    string? TimeZone,
    bool AiMemoryEnabled,
    bool AiSummaryEnabled,
    bool HasCompletedOnboarding,
    bool HasDismissedMissions,
    string? CompletedTours,
    string? Language,
    string Plan,
    bool HasProAccess,
    bool IsTrialActive,
    DateTime? TrialEndsAt,
    DateTime? PlanExpiresAt,
    int AiMessagesUsed,
    int AiMessagesLimit,
    bool HasImportedCalendar,
    bool HasGoogleConnection,
    string? SubscriptionInterval,
    bool IsLifetimePro,
    int WeekStartDay,
    int TotalXp,
    int Level,
    string LevelTitle,
    int AdRewardsClaimedToday);

public record GetProfileQuery(Guid UserId) : IRequest<Result<ProfileResponse>>;

public class GetProfileQueryHandler(
    IGenericRepository<User> userRepository,
    IPayGateService payGate) : IRequestHandler<GetProfileQuery, Result<ProfileResponse>>
{
    public async Task<Result<ProfileResponse>> Handle(GetProfileQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);

        if (user is null)
            return Result.Failure<ProfileResponse>(ErrorMessages.UserNotFound);

        var aiMessageLimit = await payGate.GetAiMessageLimit(request.UserId, cancellationToken);

        var levelTitle = LevelDefinitions.GetLevelForXp(user.TotalXp).Title;

        return Result.Success(new ProfileResponse(
            user.Name,
            user.Email,
            user.TimeZone,
            user.AiMemoryEnabled,
            user.AiSummaryEnabled,
            user.HasCompletedOnboarding,
            user.HasDismissedMissions,
            user.CompletedTours,
            user.Language,
            user.HasProAccess ? "pro" : "free",
            user.HasProAccess,
            user.IsTrialActive,
            user.TrialEndsAt,
            user.PlanExpiresAt,
            user.AiMessagesUsedThisMonth,
            aiMessageLimit,
            user.HasImportedCalendar,
            user.GoogleAccessToken is not null,
            user.SubscriptionInterval?.ToString().ToLowerInvariant(),
            user.IsLifetimePro,
            user.WeekStartDay,
            user.TotalXp,
            user.Level,
            levelTitle,
            user.LastAdRewardAt.HasValue && user.LastAdRewardAt.Value.Date == DateTime.UtcNow.Date
                ? user.AdRewardsClaimedToday
                : 0));
    }
}
