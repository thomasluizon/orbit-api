using MediatR;
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
    bool HasGoogleConnection);

public record GetProfileQuery(Guid UserId) : IRequest<ProfileResponse>;

public class GetProfileQueryHandler(
    IGenericRepository<User> userRepository,
    IPayGateService payGate) : IRequestHandler<GetProfileQuery, ProfileResponse>
{
    public async Task<ProfileResponse> Handle(GetProfileQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);

        if (user is null)
            throw new InvalidOperationException("User not found.");

        var aiMessageLimit = await payGate.GetAiMessageLimit(request.UserId, cancellationToken);

        return new ProfileResponse(
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
            user.GoogleAccessToken is not null);
    }
}
