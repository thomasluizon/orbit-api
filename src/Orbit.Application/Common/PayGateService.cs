using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Common;

public class PayGateService(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<User> userRepository,
    IAppConfigService appConfig) : IPayGateService
{
    public async Task<Result> CanCreateHabits(Guid userId, int count = 1, CancellationToken ct = default)
    {
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        if (user.HasProAccess)
            return Result.Success();

        var maxHabits = await appConfig.GetAsync(AppConfigKeys.FreeMaxHabits, AppConstants.DefaultFreeMaxHabits, ct);
        var activeHabits = await habitRepository.FindAsync(
            h => h.UserId == userId, ct);

        if (activeHabits.Count + count > maxHabits)
            return Result.PayGateFailure($"You've reached the {maxHabits} habit limit on the free plan. Upgrade to Pro for unlimited habits.");

        return Result.Success();
    }

    public async Task<Result> CanCreateSubHabits(Guid userId, CancellationToken ct = default)
    {
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        var subHabitsProOnly = await appConfig.GetAsync(AppConfigKeys.SubHabitsProOnly, true, ct);
        if (subHabitsProOnly && !user.HasProAccess)
            return Result.PayGateFailure("Sub-habits are a Pro feature. Upgrade to unlock!");

        return Result.Success();
    }

    public async Task<Result> CanSendAiMessage(Guid userId, CancellationToken ct = default)
    {
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        var freeLimit = await appConfig.GetAsync(AppConfigKeys.FreeAiMessagesPerMonth, AppConstants.DefaultFreeAiMessages, ct);
        var proLimit = await appConfig.GetAsync(AppConfigKeys.ProAiMessagesPerMonth, AppConstants.DefaultProAiMessages, ct);
        var baseLimit = user.HasProAccess ? proLimit : freeLimit;
        var messageLimit = baseLimit + user.AdRewardBonusMessages;

        if (user.AiMessagesUsedThisMonth >= messageLimit)
            return Result.PayGateFailure($"You've reached your monthly AI message limit ({messageLimit}). Upgrade to Pro for {proLimit} messages per month.");

        return Result.Success();
    }

    public async Task<Result> CanUseDailySummary(Guid userId, CancellationToken ct = default)
    {
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        var summaryProOnly = await appConfig.GetAsync(AppConfigKeys.DailySummaryProOnly, true, ct);
        if (summaryProOnly && !user.HasProAccess)
            return Result.PayGateFailure("Daily summaries are a Pro feature. Upgrade to unlock!");

        return Result.Success();
    }

    public async Task<Result> CanUseRetrospective(Guid userId, CancellationToken ct = default)
    {
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        var proOnly = await appConfig.GetAsync(AppConfigKeys.RetrospectiveProOnly, true, ct);
        if (proOnly && !user.IsYearlyPro)
            return Result.PayGateFailure("Retrospectives are available on the yearly Pro plan. Upgrade to unlock!");

        return Result.Success();
    }

    public async Task<Result> CanCreateGoals(Guid userId, CancellationToken ct = default)
    {
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        var goalsProOnly = await appConfig.GetAsync(AppConfigKeys.GoalsProOnly, true, ct);
        if (goalsProOnly && !user.HasProAccess)
            return Result.PayGateFailure("Goals are a Pro feature. Upgrade to unlock!");

        return Result.Success();
    }

    /// <summary>
    /// Returns the AI message limit for the given user (used by profile/subscription endpoints).
    /// </summary>
    public async Task<int> GetAiMessageLimit(Guid userId, CancellationToken ct = default)
    {
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null) return AppConstants.DefaultFreeAiMessages;

        var freeLimit = await appConfig.GetAsync(AppConfigKeys.FreeAiMessagesPerMonth, AppConstants.DefaultFreeAiMessages, ct);
        var proLimit = await appConfig.GetAsync(AppConfigKeys.ProAiMessagesPerMonth, AppConstants.DefaultProAiMessages, ct);
        var baseLimit = user.HasProAccess ? proLimit : freeLimit;
        return baseLimit + user.AdRewardBonusMessages;
    }
}
