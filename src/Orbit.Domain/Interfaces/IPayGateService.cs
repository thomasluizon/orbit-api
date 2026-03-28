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
    /// Checks if the user can create goals (Pro-only feature).
    /// </summary>
    Task<Result> CanCreateGoals(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the AI message limit for the given user.
    /// </summary>
    Task<int> GetAiMessageLimit(Guid userId, CancellationToken ct = default);
}
