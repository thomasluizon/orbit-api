using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Habits.Queries;

public record HabitFullDetailResponse(
    HabitDetailResponse Habit,
    HabitMetrics Metrics,
    IReadOnlyList<HabitLogResponse> Logs);

public record GetHabitFullDetailQuery(
    Guid UserId,
    Guid HabitId) : IRequest<Result<HabitFullDetailResponse>>;

public sealed record HabitFullDetailLogReadRow(Guid HabitId, DateOnly Date, decimal Value, bool IsDeleted,
    Guid? ResponseId, DateTime? ResponseCreatedAtUtc, DateTime? TakenAtUtc, DateTime? ReminderAtUtc);

public class GetHabitFullDetailQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<User> userRepository,
    IUserDateService userDateService,
    IRepeatReminderReader repeatReminderReader) : IRequestHandler<GetHabitFullDetailQuery, Result<HabitFullDetailResponse>>
{
    private const int DefaultLookbackDays = 365;

    public async Task<Result<HabitFullDetailResponse>> Handle(GetHabitFullDetailQuery request, CancellationToken cancellationToken)
    {
        var habits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId
                && (h.Id == request.HabitId || h.Children.Any(child => child.Id == request.HabitId)),
            q => q.Include(h => h.Children.Where(child => child.ParentHabitId == request.HabitId)).ThenInclude(c => c.Children)
                  .Include(h => h.Goals)
                  .AsSplitQuery(),
            cancellationToken);

        var habit = habits.FirstOrDefault(h => h.Id == request.HabitId);
        if (habit is null)
            return Result.Failure<HabitFullDetailResponse>(ErrorMessages.HabitNotFound);

        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<HabitFullDetailResponse>(ErrorMessages.UserNotFound);

        var userTimeZone = user.TimeZone is not null
            ? TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone)
            : TimeZoneInfo.Utc;
        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var descendantLogsByHabitId = await HabitDetailDescendantLogLoader.LoadAsync(
            habitLogRepository,
            habit,
            userToday,
            cancellationToken);
        var children = HabitDetailChildMapper.MapChildren(
            habit,
            userToday,
            user.WeekStartDay,
            descendantLogsByHabitId);
        var cutoff = userToday.AddDays(-DefaultLookbackDays);
        var allRootLogs = await habitLogRepository.ProjectAsync(
            log => log.HabitId == request.HabitId,
            query => query.Select(log => new HabitFullDetailLogReadRow(log.HabitId, log.Date, log.Value, log.IsDeleted,
                log.Date >= cutoff ? log.Id : (Guid?)null,
                log.Date >= cutoff ? log.CreatedAtUtc : (DateTime?)null,
                log.Date >= cutoff ? log.TakenAtUtc : null,
                log.Date >= cutoff ? log.ReminderAtUtc : null)), cancellationToken);
        var parentTitle = habits.FirstOrDefault(h => h.Id == habit.ParentHabitId)?.Title;
        var detail = new HabitDetailResponse(
            habit.Id, habit.Title, habit.Description,
            habit.FrequencyUnit, habit.FrequencyQuantity,
            habit.IsBadHabit, habit.IsCompleted, habit.Schedule == HabitSchedule.General, habit.Schedule == HabitSchedule.Flexible,
            habit.NextDueDate, habit.DueTime, habit.DueEndTime, habit.EndDate,
            habit.Days.ToList(), habit.Position,
            habit.ReminderEnabled, habit.ReminderTimes, habit.GetScheduledRemindersForLegacyClients(),
            habit.ChecklistItems, habit.CreatedAtUtc, children,
            Emoji: habit.Emoji,
            IntervalWeeks: habit.IntervalWeeks,
            MonthDays: habit.MonthDays,
            LinkedGoals: habit.ParentHabitId.HasValue
                ? habit.Goals.Select(goal => new LinkedGoalDto(goal.Id, goal.Title)).ToList()
                : null,
            SlipAlertEnabled: habit.ParentHabitId.HasValue ? habit.SlipAlertEnabled : null,
            RelativeReminders: habit.RelativeReminders,
            RepeatReminder: habit.RepeatReminder,
            Schedule: habit.Schedule,
            StartDate: habit.StartDate,
            ScheduledStartDate: habit.ScheduledStartDate,
            OriginalDayOfMonth: habit.OriginalDayOfMonth,
            ParentId: habit.ParentHabitId,
            ParentHabitId: habit.ParentHabitId,
            ParentTitle: parentTitle);

        var metrics = HabitMetricsCalculator.CalculateProjected(
            habit,
            allRootLogs.Select(log => new HabitMetricLog(log.HabitId, log.Date, log.Value, log.IsDeleted)).ToList(),
            userToday,
            user.WeekStartDay,
            userTimeZone);

        var logs = allRootLogs.Where(log => log.ResponseId.HasValue && log.ResponseCreatedAtUtc.HasValue)
            .OrderByDescending(log => log.Date)
            .Select(log => new HabitLogResponse(log.ResponseId.GetValueOrDefault(), log.Date, log.Value,
                log.ResponseCreatedAtUtc.GetValueOrDefault(), log.TakenAtUtc, log.ReminderAtUtc)).ToList();

        detail = await RepeatReminderResponseEnricher.EnrichAsync(detail, request.UserId, repeatReminderReader, cancellationToken);
        return Result.Success(new HabitFullDetailResponse(detail, metrics, logs));
    }
}
