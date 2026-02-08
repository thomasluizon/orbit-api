using System.Globalization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Queries;

public record GetHabitTrendQuery(Guid UserId, Guid HabitId) : IRequest<Result<HabitTrend>>;

public class GetHabitTrendQueryHandler(
    IGenericRepository<Habit> habitRepository) : IRequestHandler<GetHabitTrendQuery, Result<HabitTrend>>
{
    public async Task<Result<HabitTrend>> Handle(GetHabitTrendQuery request, CancellationToken cancellationToken)
    {
        var habits = await habitRepository.FindAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId && h.IsActive,
            q => q.Include(h => h.Logs),
            cancellationToken);

        var habit = habits.FirstOrDefault();
        if (habit is null)
            return Result.Failure<HabitTrend>("Habit not found.");

        if (habit.Type != HabitType.Quantifiable)
            return Result.Failure<HabitTrend>("Trends are only available for quantifiable habits.");

        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-12);
        var recentLogs = habit.Logs.Where(l => l.Date >= cutoff).ToList();

        if (recentLogs.Count == 0)
        {
            return Result.Success(new HabitTrend(
                Array.Empty<TrendPoint>(),
                Array.Empty<TrendPoint>()));
        }

        var weekly = CalculateWeeklyTrends(recentLogs);
        var monthly = CalculateMonthlyTrends(recentLogs);

        return Result.Success(new HabitTrend(weekly, monthly));
    }

    private static IReadOnlyList<TrendPoint> CalculateWeeklyTrends(List<HabitLog> logs)
    {
        var calendar = CultureInfo.InvariantCulture.Calendar;

        var weeklyGroups = logs
            .GroupBy(l =>
            {
                var dateTime = l.Date.ToDateTime(TimeOnly.MinValue);
                var year = calendar.GetYear(dateTime);
                var week = calendar.GetWeekOfYear(
                    dateTime,
                    CalendarWeekRule.FirstDay,
                    DayOfWeek.Monday);

                // Handle week 53 that crosses year boundary
                if (week == 53 && dateTime.Month == 1)
                {
                    year--;
                }

                return new { Year = year, Week = week };
            })
            .Select(g => new TrendPoint(
                Period: $"{g.Key.Year}-W{g.Key.Week:00}",
                Average: Math.Round(g.Average(l => l.Value), 2),
                Minimum: g.Min(l => l.Value),
                Maximum: g.Max(l => l.Value),
                Count: g.Count()))
            .OrderBy(t => t.Period)
            .ToList();

        return weeklyGroups;
    }

    private static IReadOnlyList<TrendPoint> CalculateMonthlyTrends(List<HabitLog> logs)
    {
        var monthlyGroups = logs
            .GroupBy(l => new { l.Date.Year, l.Date.Month })
            .Select(g => new TrendPoint(
                Period: $"{g.Key.Year}-{g.Key.Month:00}",
                Average: Math.Round(g.Average(l => l.Value), 2),
                Minimum: g.Min(l => l.Value),
                Maximum: g.Max(l => l.Value),
                Count: g.Count()))
            .OrderBy(t => t.Period)
            .ToList();

        return monthlyGroups;
    }
}
