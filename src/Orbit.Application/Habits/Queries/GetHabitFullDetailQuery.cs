using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
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

public class GetHabitFullDetailQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<User> userRepository,
    IUserDateService userDateService) : IRequestHandler<GetHabitFullDetailQuery, Result<HabitFullDetailResponse>>
{
    private const int DefaultLookbackDays = 365;

    public async Task<Result<HabitFullDetailResponse>> Handle(GetHabitFullDetailQuery request, CancellationToken cancellationToken)
    {
        // Load habit with children for detail
        var habits = await habitRepository.FindAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            q => q.Include(h => h.Children).ThenInclude(c => c.Children)
                  .Include(h => h.Children).ThenInclude(c => c.Logs)
                  .Include(h => h.Children).ThenInclude(c => c.Children).ThenInclude(gc => gc.Logs)
                  .Include(h => h.Logs),
            cancellationToken);

        var habit = habits.Count > 0 ? habits[0] : null;
        if (habit is null)
            return Result.Failure<HabitFullDetailResponse>(ErrorMessages.HabitNotFound, ErrorCodes.HabitNotFound);

        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<HabitFullDetailResponse>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        var userTimeZone = user.TimeZone is not null
            ? TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone)
            : TimeZoneInfo.Utc;
        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var children = HabitDetailChildMapper.MapChildren(habit, userToday);

        var detail = new HabitDetailResponse(
            habit.Id, habit.Title, habit.Description,
            habit.FrequencyUnit, habit.FrequencyQuantity,
            habit.IsBadHabit, habit.IsCompleted, habit.IsGeneral, habit.IsFlexible,
            habit.DueDate, habit.DueTime, habit.DueEndTime, habit.EndDate,
            habit.Days.ToList(), habit.Position,
            habit.ReminderEnabled, habit.ReminderTimes, habit.ScheduledReminders,
            habit.ChecklistItems, habit.CreatedAtUtc, children);

        var metrics = HabitMetricsCalculator.Calculate(habit, userToday, userTimeZone);

        // Build logs (365-day lookback, same as GetHabitLogsQueryHandler)
        var cutoff = userToday.AddDays(-DefaultLookbackDays);

        var allLogs = await habitLogRepository.FindAsync(
            l => l.HabitId == request.HabitId && l.Date >= cutoff,
            cancellationToken);

        var logs = allLogs
            .OrderByDescending(l => l.Date)
            .Select(l => new HabitLogResponse(l.Id, l.Date, l.Value, l.CreatedAtUtc))
            .ToList();

        return Result.Success(new HabitFullDetailResponse(detail, metrics, logs));
    }
}
