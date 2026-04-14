using Orbit.Domain.Common;

namespace Orbit.Domain.Interfaces;

public interface IPayGateService
{
    /// <summary>
    /// Checks if the user can create more habits (free plan: max 10 active).
    /// </summary>
    Task<Result> CanCreateHabits(Guid userId, int count = 1, CancellationToken ct = default);

    /// <summary>
    /// Checks if the user can create sub-habits (Pro-only feature).
    /// </summary>
    Task<Result> CanCreateSubHabits(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Checks if the user can send AI messages (free: 20/month, Pro: 500/month).
    /// </summary>
    Task<Result> CanSendAiMessage(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Checks if the user can use daily AI summaries (Pro-only feature).
    /// </summary>
    Task<Result> CanUseDailySummary(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Checks if the user can use AI retrospectives (Pro-only feature).
    /// </summary>
    Task<Result> CanUseRetrospective(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Checks if the user can access goals (Pro-only feature).
    /// </summary>
    Task<Result> CanAccessGoals(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Checks if the user can create goals (Pro-only feature).
    /// </summary>
    Task<Result> CanCreateGoals(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Checks if the user can read calendar integration data (Pro-only feature).
    /// </summary>
    Task<Result> CanAccessCalendar(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Checks if the user can manage calendar integration state (Pro-only feature).
    /// </summary>
    Task<Result> CanManageCalendar(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the AI message limit for the given user.
    /// </summary>
    Task<int> GetAiMessageLimit(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Checks if the user can create API keys (Pro-only feature).
    /// </summary>
    Task<Result> CanCreateApiKeys(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Checks if the user can read API keys (Pro-only feature).
    /// </summary>
    Task<Result> CanReadApiKeys(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Checks if the user can manage API keys (Pro-only feature).
    /// </summary>
    Task<Result> CanManageApiKeys(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Checks if the user can manage AI memory settings (Pro-only feature).
    /// </summary>
    Task<Result> CanManageAiMemory(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Checks if the user can manage AI summary settings (Pro-only feature).
    /// </summary>
    Task<Result> CanManageAiSummary(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Checks if the user can manage premium color schemes (Pro-only feature).
    /// </summary>
    Task<Result> CanManagePremiumColors(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Checks if the user can read AI memory facts (Pro-only feature).
    /// </summary>
    Task<Result> CanReadUserFacts(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Checks if the user can manage AI memory facts (Pro-only feature).
    /// </summary>
    Task<Result> CanManageUserFacts(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Checks if the user can enable or manage slip alerts (Pro-only feature).
    /// </summary>
    Task<Result> CanUseSlipAlerts(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Checks if the user can manage goal links on habits (Pro-only feature).
    /// </summary>
    Task<Result> CanLinkGoalsToHabits(Guid userId, CancellationToken ct = default);
}
