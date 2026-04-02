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
                  .Include(h => h.Logs),
            cancellationToken);

        var habit = habits.FirstOrDefault();
        if (habit is null)
            return Result.Failure<HabitFullDetailResponse>(ErrorMessages.HabitNotFound);

        // Build detail (same mapping as GetHabitByIdQueryHandler)
        var children = habit.Children
            .OrderBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c => MapChild(c))
            .ToList();

        var detail = new HabitDetailResponse(
            habit.Id, habit.Title, habit.Description,
            habit.FrequencyUnit, habit.FrequencyQuantity,
            habit.IsBadHabit, habit.IsCompleted, habit.IsGeneral, habit.IsFlexible,
            habit.DueDate, habit.DueTime, habit.DueEndTime, habit.EndDate,
            habit.Days.ToList(), habit.Position,
            habit.ReminderEnabled, habit.ReminderTimes, habit.ScheduledReminders,
            habit.ChecklistItems, habit.CreatedAtUtc, children);

        // Build metrics
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<HabitFullDetailResponse>(ErrorMessages.UserNotFound);

        var userTimeZone = user.TimeZone is not null
            ? TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone)
            : TimeZoneInfo.Utc;
        var today = HabitMetricsCalculator.GetUserToday(user);
        var metrics = HabitMetricsCalculator.Calculate(habit, today, userTimeZone);

        // Build logs (365-day lookback, same as GetHabitLogsQueryHandler)
        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var cutoff = userToday.AddDays(-DefaultLookbackDays);

        var allLogs = await habitLogRepository.FindAsync(
            l => l.HabitId == request.HabitId && l.Date >= cutoff,
            cancellationToken);

        var logs = allLogs
            .OrderByDescending(l => l.Date)
            .Select(l => new HabitLogResponse(l.Id, l.Date, l.Value, l.Note, l.CreatedAtUtc))
            .ToList();

        return Result.Success(new HabitFullDetailResponse(detail, metrics, logs));
    }

    private static HabitChildResponse MapChild(Habit c) => new(
        c.Id, c.Title, c.Description,
        c.FrequencyUnit, c.FrequencyQuantity, c.IsBadHabit, c.IsCompleted, c.IsGeneral, c.IsFlexible,
        c.Days.ToList(), c.DueDate, c.DueTime, c.DueEndTime, c.EndDate,
        c.Position, c.ChecklistItems, MapChildren(c));

    private static List<HabitChildResponse> MapChildren(Habit parent) =>
        parent.Children
            .OrderBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c => MapChild(c))
            .ToList();
}
