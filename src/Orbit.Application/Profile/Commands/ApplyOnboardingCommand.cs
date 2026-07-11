using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Profile.Commands;

public record ApplyHabitInput(
    string Title,
    string? Description,
    string? Emoji,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    IReadOnlyList<DayOfWeek>? Days = null,
    bool IsBadHabit = false,
    bool IsGeneral = false,
    bool IsFlexible = false,
    DateOnly? DueDate = null,
    TimeOnly? DueTime = null,
    bool ReminderEnabled = false,
    IReadOnlyList<int>? ReminderTimes = null,
    IReadOnlyList<ChecklistItem>? ChecklistItems = null);

public record ApplyLogInput(int HabitIndex, DateOnly Date);

public record ApplyGoalInput(
    string Title,
    string? Description,
    decimal TargetValue,
    string Unit,
    DateOnly? Deadline = null,
    GoalType Type = GoalType.Standard);

public record ApplyOnboardingResponse(
    bool Applied,
    int CreatedHabitCount,
    bool CreatedGoal,
    bool LoggedFirstHabit);

public record ApplyOnboardingCommand(
    Guid UserId,
    IReadOnlyList<ApplyHabitInput> Habits,
    ApplyLogInput? FirstLog,
    ApplyGoalInput? Goal,
    int? WeekStartDay,
    string? ColorScheme) : IRequest<Result<ApplyOnboardingResponse>>, IConcurrencyRetryable;

/// <summary>
/// Applies the buffer of answers a user built during pre-auth onboarding in a single transaction:
/// creates the habits (trimmed to the free-plan allowance), an optional first log, an optional
/// Pro-gated goal, week-start/color preferences, and flips <c>HasCompletedOnboarding</c>. Idempotent
/// by construction — an already-onboarded user is a no-op (<c>Applied:false</c>) — so the client can
/// flush unconditionally after any successful auth and retry safely under the concurrency pipeline.
/// </summary>
public class ApplyOnboardingCommandHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<Goal> goalRepository,
    IPayGateService payGate,
    IUserDateService userDateService,
    IAppConfigService appConfig,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<ApplyOnboardingCommand, Result<ApplyOnboardingResponse>>
{
    public async Task<Result<ApplyOnboardingResponse>> Handle(
        ApplyOnboardingCommand request, CancellationToken cancellationToken)
    {
        var result = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await unitOfWork.AcquireAdvisoryLockAsync($"onboarding-apply:{request.UserId}", ct);

            var user = await userRepository.FindOneTrackedAsync(
                u => u.Id == request.UserId, cancellationToken: ct);

            if (user is null)
                return Result.Failure<ApplyOnboardingResponse>(ErrorMessages.UserNotFound);

            if (user.HasCompletedOnboarding)
                return Result.Success(new ApplyOnboardingResponse(false, 0, false, false));

            var today = await userDateService.GetUserTodayAsync(request.UserId, ct);

            var habitsToCreate = await TrimToAllowanceAsync(user, request.Habits, ct);

            var createResult = await CreateHabitsAsync(request.UserId, habitsToCreate, today, ct);
            if (createResult.IsFailure)
                return createResult.PropagateError<ApplyOnboardingResponse>();

            var createdHabits = createResult.Value;

            var loggedFirstHabit = false;
            if (request.FirstLog is { } firstLog
                && firstLog.HabitIndex >= 0
                && firstLog.HabitIndex < createdHabits.Count)
            {
                var logResult = createdHabits[firstLog.HabitIndex].Log(firstLog.Date);
                if (logResult.IsFailure)
                    return logResult.PropagateError<ApplyOnboardingResponse>();
                loggedFirstHabit = true;
            }

            var goalResult = await CreateGoalIfAllowedAsync(request.UserId, request.Goal, today, ct);
            if (goalResult.IsFailure)
                return goalResult.PropagateError<ApplyOnboardingResponse>();
            var createdGoal = goalResult.Value;

            var prefsResult = ApplyPreferences(user, request.WeekStartDay, request.ColorScheme);
            if (prefsResult.IsFailure)
                return prefsResult.PropagateError<ApplyOnboardingResponse>();

            user.CompleteOnboarding();

            await unitOfWork.SaveChangesAsync(ct);

            return Result.Success(
                new ApplyOnboardingResponse(true, createdHabits.Count, createdGoal, loggedFirstHabit));
        }, cancellationToken);

        if (result.IsSuccess && result.Value.Applied)
            CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId);

        return result;
    }

    private async Task<IReadOnlyList<ApplyHabitInput>> TrimToAllowanceAsync(
        User user, IReadOnlyList<ApplyHabitInput> habits, CancellationToken cancellationToken)
    {
        if (user.HasProAccess)
            return habits;

        var maxHabits = await appConfig.GetAsync(
            AppConfigKeys.FreeMaxHabits, AppConstants.DefaultFreeMaxHabits, cancellationToken);
        var existingRoots = await habitRepository.CountAsync(
            h => h.UserId == user.Id && h.ParentHabitId == null, cancellationToken);
        var allowance = Math.Max(0, maxHabits - existingRoots);

        return allowance >= habits.Count ? habits : habits.Take(allowance).ToList();
    }

    private async Task<Result<List<Habit>>> CreateHabitsAsync(
        Guid userId, IReadOnlyList<ApplyHabitInput> habits, DateOnly today, CancellationToken cancellationToken)
    {
        var createdHabits = new List<Habit>();
        var position = 0;

        foreach (var item in habits)
        {
            var habitResult = Habit.Create(new HabitCreateParams(
                userId,
                item.Title,
                item.FrequencyUnit,
                item.FrequencyQuantity,
                item.Description,
                Emoji: item.Emoji,
                Days: item.Days,
                IsBadHabit: item.IsBadHabit,
                DueDate: item.DueDate ?? today,
                DueTime: item.DueTime,
                ReminderEnabled: item.ReminderEnabled,
                ReminderTimes: item.ReminderTimes,
                ChecklistItems: item.ChecklistItems,
                IsGeneral: item.IsGeneral,
                IsFlexible: item.IsFlexible,
                Position: position++));

            if (habitResult.IsFailure)
                return habitResult.PropagateError<List<Habit>>();

            await habitRepository.AddAsync(habitResult.Value, cancellationToken);
            createdHabits.Add(habitResult.Value);
        }

        return Result.Success(createdHabits);
    }

    private async Task<Result<bool>> CreateGoalIfAllowedAsync(
        Guid userId, ApplyGoalInput? goalInput, DateOnly today, CancellationToken cancellationToken)
    {
        if (goalInput is null)
            return Result.Success(false);

        var goalGate = await payGate.CanAccessGoals(userId, cancellationToken);
        if (goalGate.IsFailure)
            return Result.Success(false);

        if (goalInput.Deadline is { } deadline && deadline < today)
            return Result.Failure<bool>(ErrorMessages.DeadlineInPast);

        var goalResult = Goal.Create(new Goal.CreateGoalParams(
            userId,
            goalInput.Title,
            goalInput.TargetValue,
            goalInput.Unit,
            goalInput.Description,
            goalInput.Deadline,
            0,
            goalInput.Type));

        if (goalResult.IsFailure)
            return goalResult.PropagateError<bool>();

        await goalRepository.AddAsync(goalResult.Value, cancellationToken);
        return Result.Success(true);
    }

    private static Result ApplyPreferences(User user, int? weekStartDay, string? colorScheme)
    {
        if (weekStartDay is { } day)
        {
            var weekStartResult = user.SetWeekStartDay(day);
            if (weekStartResult.IsFailure)
                return weekStartResult;
        }

        if (colorScheme is not null)
        {
            var colorResult = user.SetColorScheme(colorScheme);
            if (colorResult.IsFailure)
                return colorResult;
        }

        return Result.Success();
    }
}
