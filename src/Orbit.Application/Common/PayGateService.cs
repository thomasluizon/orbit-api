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
            return Result.Failure("User not found.");

        if (user.HasProAccess)
            return Result.Success();

        var maxHabits = await appConfig.GetAsync("FreeMaxHabits", 10, ct);
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
            return Result.Failure("User not found.");

        var subHabitsProOnly = await appConfig.GetAsync("SubHabitsProOnly", true, ct);
        if (subHabitsProOnly && !user.HasProAccess)
            return Result.PayGateFailure("Sub-habits are a Pro feature. Upgrade to unlock!");

        return Result.Success();
    }

    public async Task<Result> CanSendAiMessage(Guid userId, CancellationToken ct = default)
    {
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return Result.Failure("User not found.");

        var freeLimit = await appConfig.GetAsync("FreeAiMessagesPerMonth", 20, ct);
        var proLimit = await appConfig.GetAsync("ProAiMessagesPerMonth", 500, ct);
        var messageLimit = user.HasProAccess ? proLimit : freeLimit;

        if (user.AiMessagesUsedThisMonth >= messageLimit)
            return Result.PayGateFailure($"You've reached your monthly AI message limit ({messageLimit}). Upgrade to Pro for {proLimit} messages per month.");

        return Result.Success();
    }

    public async Task<Result> CanUseDailySummary(Guid userId, CancellationToken ct = default)
    {
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return Result.Failure("User not found.");

        var summaryProOnly = await appConfig.GetAsync("DailySummaryProOnly", true, ct);
        if (summaryProOnly && !user.HasProAccess)
            return Result.PayGateFailure("Daily summaries are a Pro feature. Upgrade to unlock!");

        return Result.Success();
    }

    public async Task<Result> CanUseRetrospective(Guid userId, CancellationToken ct = default)
    {
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return Result.Failure("User not found.");

        var proOnly = await appConfig.GetAsync("RetrospectiveProOnly", true, ct);
        if (proOnly && !user.HasProAccess)
            return Result.PayGateFailure("Retrospectives are a Pro feature. Upgrade to unlock!");

        return Result.Success();
    }

    /// <summary>
    /// Returns the AI message limit for the given user (used by profile/subscription endpoints).
    /// </summary>
    public async Task<int> GetAiMessageLimit(Guid userId, CancellationToken ct = default)
    {
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null) return 20;

        var freeLimit = await appConfig.GetAsync("FreeAiMessagesPerMonth", 20, ct);
        var proLimit = await appConfig.GetAsync("ProAiMessagesPerMonth", 500, ct);
        return user.HasProAccess ? proLimit : freeLimit;
    }
}
