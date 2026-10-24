using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Orbit.Application.Habits.Commands;

public record UpdateHabitCommand(
    Guid UserId,
    Guid HabitId,
    string Title,
    string? Description,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    bool IsBadHabit = false,
    DateOnly? DueDate = null,
    bool? IsGeneral = null,
    bool? ClearEndDate = null,
    UpdateHabitCommandOptions? Options = null,
    IReadOnlyList<Guid>? GoalIds = null,
    string? Emoji = null,
    int? IntervalWeeks = null,
    DateOnly? StartDate = null,
    IReadOnlyList<Guid>? TagIds = null,
    Guid? ParentId = null,
    bool? ClearParent = null) : IRequest<Result>;

public sealed record UpdateHabitFieldsCommand(
    Guid UserId,
    Guid HabitId,
    BulkHabitChanges Changes,
    IReadOnlyList<Guid>? GoalIds = null,
    IReadOnlyList<Guid>? TagIds = null,
    Guid? ParentId = null,
    bool? ClearParent = null) : IRequest<Result<string>>;

public class UpdateHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<SentReminder> sentReminderRepository,
    IGenericRepository<Goal> goalRepository,
    IPayGateService payGate,
    IGoalCompletionService goalCompletionService,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    IGenericRepository<Tag> tagRepository,
    IAppConfigService appConfigService) : IRequestHandler<UpdateHabitCommand, Result>,
    IRequestHandler<UpdateHabitFieldsCommand, Result<string>>
{
    public async Task<Result> Handle(UpdateHabitCommand request, CancellationToken cancellationToken)
    {
        return await HabitCeilingLock.ExecuteEntryAsync(
            unitOfWork,
            request.UserId,
            payGate,
            ct => PrepareUpdateAsync(request.UserId, request.HabitId, request.GoalIds, request.TagIds,
                (habit, today) => ResolveUpdate(request, habit, today), false, request.ParentId, request.ClearParent, ct),
            state => HabitLiveRootEntry.FromUpdate(state.Habit, state.Update, state.ParentId),
            (state, ct) => ApplyUpdateAsync(request, state, ct),
            cancellationToken);
    }

    public Task<Result<string>> Handle(UpdateHabitFieldsCommand request, CancellationToken cancellationToken) =>
        HabitCeilingLock.ExecuteEntryAsync<UpdateState, string>(
            unitOfWork,
            request.UserId,
            payGate,
            ct => PrepareUpdateAsync(request.UserId, request.HabitId, request.GoalIds, request.TagIds,
                (habit, today) => HabitUpdateValidation.ResolveSingleUpdate(habit, request.Changes, today), true, request.ParentId, request.ClearParent, ct),
            state => HabitLiveRootEntry.FromUpdate(state.Habit, state.Update, state.ParentId),
            async (state, ct) =>
            {
                var command = HabitUpdateValidation.ToCommand(request.UserId, request.HabitId, state.Update) with
                { GoalIds = request.GoalIds, TagIds = request.TagIds, ParentId = request.ParentId, ClearParent = request.ClearParent };
                var result = await ApplyUpdateAsync(command, state, ct);
                return result.IsFailure ? result.PropagateError<string>() : Result.Success(state.Habit.Title);
            },
            cancellationToken);

    private async Task<Result<UpdateState>> PrepareUpdateAsync(
        Guid userId,
        Guid habitId,
        IReadOnlyList<Guid>? goalIds,
        IReadOnlyList<Guid>? tagIds,
        Func<Habit, DateOnly, HabitUpdateParams> resolveUpdate,
        bool validateFields,
        Guid? requestedParentId,
        bool? clearParent,
        CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == habitId && h.UserId == userId,
            q =>
            {
                if (goalIds is not null)
                    q = q.Include(h => h.Goals);
                if (tagIds is not null)
                    q = q.Include(h => h.Tags);
                return q;
            },
            cancellationToken);

        if (habit is null || habit.IsDeleted)
            return Result.Failure<UpdateState>(ErrorMessages.HabitNotFound);

        await habitRepository.ReloadAsync(habit, cancellationToken);
        if (habit.IsDeleted)
            return Result.Failure<UpdateState>(ErrorMessages.HabitNotFound);
        var today = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        var update = resolveUpdate(habit, today);
        if (habit.RequiresScheduleReanchor(update))
        {
            update = update with { WeekStartDay = await userDateService.GetUserWeekStartDayAsync(userId, cancellationToken) };
            var through = update.ClearEndDate == true ? DateOnly.MaxValue : update.EndDate ?? habit.EndDate ?? DateOnly.MaxValue;
            await habitRepository.FindOneTrackedAsync(
                h => h.Id == habitId && h.UserId == userId,
                q => q.Include(h => h.Logs.Where(log => !log.IsDeleted && log.Date >= today && log.Date <= through)),
                cancellationToken);
        }
        if (validateFields)
        {
            var validation = await HabitUpdateValidation.ValidateAsync(userId, habit, update, habitRepository, cancellationToken);
            if (validation.IsFailure)
                return validation.PropagateError<UpdateState>();
        }

        if (update.SlipAlertEnabled == true && !habit.SlipAlertEnabled)
        {
            var slipAlertGate = await payGate.CanUseSlipAlerts(userId, cancellationToken);
            if (slipAlertGate.IsFailure)
                return slipAlertGate.PropagateError<UpdateState>();
        }

        var parent = await PrepareParentAsync(userId, habit, update, requestedParentId, clearParent,
            validateFields, cancellationToken);
        if (parent.IsFailure)
            return parent.PropagateError<UpdateState>();

        var tagLinks = await ResolveTagLinksAsync(userId, tagIds, cancellationToken);
        if (tagLinks.IsFailure)
            return tagLinks.PropagateError<UpdateState>();

        GoalLinks? goalLinks = null;
        if (goalIds is not null)
        {
            var resolved = await ResolveGoalLinksAsync(habit, userId, goalIds, cancellationToken);
            if (resolved.IsFailure)
                return resolved.PropagateError<UpdateState>();
            goalLinks = resolved.Value;
        }

        return Result.Success(new UpdateState(habit, update, today, tagLinks.Value, goalLinks, parent.Value));
    }

    private async Task<Result<Guid?>> PrepareParentAsync(Guid userId, Habit habit, HabitUpdateParams update,
        Guid? requestedParentId, bool? clearParent, bool validateFields, CancellationToken cancellationToken)
    {
        if (clearParent == true && requestedParentId.HasValue)
            return Result.Failure<Guid?>(new AppError(ErrorCodes.ValidationError, ErrorCopy.English(ErrorCodes.ValidationError)));
        var finalParentId = clearParent == true ? null : requestedParentId ?? habit.ParentHabitId;
        var finalIsGeneral = update.IsGeneral ?? habit.Schedule == HabitSchedule.General;

        if (requestedParentId.HasValue)
        {
            var parents = await habitRepository.FindAsync(
                h => h.Id == requestedParentId && h.UserId == userId && !h.IsDeleted, cancellationToken);
            var moveValidation = await MoveHabitParentCommandHandler.ValidateMoveAsync(
                new(userId, habit.Id, requestedParentId), habit, parents.FirstOrDefault(),
                habitRepository, appConfigService, cancellationToken, finalIsGeneral);
            if (moveValidation.IsFailure)
                return moveValidation.PropagateError<Guid?>();
        }

        if (!validateFields && (update.IsGeneral.HasValue || requestedParentId.HasValue || clearParent == true))
        {
            var related = await HabitUpdateValidation.LoadRelatedAsync([habit], userId, habitRepository, cancellationToken);
            var generalMismatch = HabitUpdateValidation.ValidateGeneral(habit, finalIsGeneral, related, finalParentId);
            if (generalMismatch.IsFailure)
                return generalMismatch.PropagateError<Guid?>();
        }

        return Result.Success(finalParentId);
    }

    private async Task<Result<TagLinks?>> ResolveTagLinksAsync(
        Guid userId, IReadOnlyList<Guid>? tagIds, CancellationToken cancellationToken)
    {
        if (tagIds is null)
            return Result.Success<TagLinks?>(null);

        var maxTags = await appConfigService.GetAsync(
            AppConfigKeys.MaxTagsPerHabit, AppConstants.MaxTagsPerHabit, cancellationToken);
        if (tagIds.Count > maxTags)
            return Result.Failure<TagLinks?>(ErrorMessages.MaxTagsPerHabit.Format(maxTags));

        var tags = await tagRepository.FindTrackedAsync(
            tag => tagIds.Contains(tag.Id) && tag.UserId == userId && !tag.IsDeleted,
            cancellationToken);
        var resolved = OwnershipValidation.AllResolved(tagIds, tags, tag => tag.Id, ErrorMessages.TagNotFound);
        return resolved.IsFailure
            ? resolved.PropagateError<TagLinks?>()
            : Result.Success<TagLinks?>(new TagLinks(tags, maxTags));
    }

    private static HabitUpdateParams ResolveUpdate(UpdateHabitCommand request, Habit habit, DateOnly today)
    {
        var opts = request.Options ?? new UpdateHabitCommandOptions();
        return new HabitUpdateParams(
            request.Title,
            request.Description,
            request.FrequencyUnit,
            request.FrequencyQuantity,
            opts.Days,
            request.IsBadHabit,
            request.DueDate,
            DueTime: opts.DueTime,
            DueEndTime: opts.DueEndTime,
            ReminderEnabled: opts.ReminderEnabled,
            ReminderTimes: opts.ReminderTimes,
            SlipAlertEnabled: opts.SlipAlertEnabled,
            ChecklistItems: opts.ChecklistItems,
            IsGeneral: request.IsGeneral,
            IsFlexible: opts.IsFlexible,
            EndDate: opts.EndDate,
            ClearEndDate: request.ClearEndDate,
            ScheduledReminders: opts.ScheduledReminders,
            RelativeReminders: opts.RelativeReminders,
            Emoji: request.Emoji,
            UserToday: today,
            IntervalWeeks: request.IntervalWeeks ?? habit.IntervalWeeks,
            StartDate: request.StartDate);

    }

    private async Task<Result> ApplyUpdateAsync(
        UpdateHabitCommand request,
        UpdateState state,
        CancellationToken cancellationToken)
    {
        var habit = state.Habit;
        var previousDueTime = habit.DueTime;
        var previousReminderEnabled = habit.ReminderEnabled;
        var previousReminderTimes = GetReminderTimes(habit).ToHashSet();
        cancellationToken.ThrowIfCancellationRequested();
        var result = habit.Update(state.Update, state.TagLinks?.Requested, state.TagLinks?.MaxTags ?? AppConstants.MaxTagsPerHabit);

        if (result.IsFailure)
            return result;

        if (habit.ParentHabitId != state.ParentId)
            habit.SetParentHabitId(state.ParentId);

        if (previousDueTime != habit.DueTime
            || previousReminderEnabled != habit.ReminderEnabled
            || !previousReminderTimes.SetEquals(GetReminderTimes(habit)))
            await ClearTodaySentRemindersAsync(request.UserId, request.HabitId, cancellationToken);

        if (state.GoalLinks is { } goalLinks)
        {
            foreach (var existingGoal in habit.Goals.ToList())
                habit.RemoveGoal(existingGoal);
            foreach (var goal in goalLinks.Requested)
                habit.AddGoal(goal);

            await goalCompletionService.SyncDerivedGoalsAsync(
                request.UserId,
                goalLinks.AffectedIds,
                state.Today,
                cancellationToken: cancellationToken);
        }
        else
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, state.Today);

        return Result.Success();
    }

    private static IEnumerable<RelativeReminderTime> GetReminderTimes(Habit habit) =>
        habit.ScheduledReminders.Select(RelativeReminderTime.FromScheduled)
            .Concat(habit.DueTime.HasValue
                ? habit.RelativeReminders.Concat(habit.ReminderTimes.Select(minutes => new RelativeReminderTime(minutes)))
                : []);

    private sealed record UpdateState(
        Habit Habit,
        HabitUpdateParams Update,
        DateOnly Today,
        TagLinks? TagLinks,
        GoalLinks? GoalLinks,
        Guid? ParentId);

    private sealed record TagLinks(IReadOnlyList<Tag> Requested, int MaxTags);

    private sealed record GoalLinks(IReadOnlyList<Goal> Requested, IReadOnlyList<Guid> AffectedIds);

    private async Task ClearTodaySentRemindersAsync(Guid userId, Guid habitId, CancellationToken cancellationToken)
    {
        var userToday = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        var existing = await sentReminderRepository.FindAsync(
            r => r.HabitId == habitId && r.Date == userToday,
            cancellationToken);
        foreach (var r in existing)
            sentReminderRepository.Remove(r);
    }

    private async Task<Result<GoalLinks>> ResolveGoalLinksAsync(
        Habit habit, Guid userId, IReadOnlyList<Guid> goalIds, CancellationToken cancellationToken)
    {
        var affectedGoalIds = habit.Goals.Select(g => g.Id).Concat(goalIds).ToHashSet();
        var goals = await goalRepository.FindTrackedAsync(
            g => affectedGoalIds.Contains(g.Id) && g.UserId == userId && !g.IsDeleted,
            q => q.Include(g => g.Habits).ThenInclude(h => h.Logs),
            cancellationToken);

        var requestedGoals = goals.Where(g => goalIds.Contains(g.Id)).ToList();
        var goalsResolved = OwnershipValidation.AllResolved(goalIds, requestedGoals, g => g.Id, ErrorMessages.GoalNotFound);
        if (goalsResolved.IsFailure)
            return goalsResolved.PropagateError<GoalLinks>();

        return Result.Success(new GoalLinks(requestedGoals, affectedGoalIds.ToList()));
    }
}
