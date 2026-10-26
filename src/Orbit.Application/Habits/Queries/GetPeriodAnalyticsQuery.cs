using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Chat;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Queries;

public sealed record GetPeriodAnalyticsQuery(
    Guid UserId, DateOnly DateFrom, DateOnly DateTo, string Metric = "positiveLogRecords",
    Guid? HabitId = null, DateOnly? BaselineFrom = null, DateOnly? BaselineTo = null,
    string? Search = null, string? Tag = null, bool? IsBadHabit = null)
    : IRequest<Result<PeriodAnalyticsResponse>>;

public sealed record PeriodAnalyticsResponse(PeriodTotalCard Total, PeriodComparisonCard? Comparison,
    string SourceRevision);

public sealed class GetPeriodAnalyticsQueryHandler(
    IGenericRepository<Habit> habits, IGenericRepository<User> users)
    : IRequestHandler<GetPeriodAnalyticsQuery, Result<PeriodAnalyticsResponse>>
{
    public async Task<Result<PeriodAnalyticsResponse>> Handle(GetPeriodAnalyticsQuery request, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(request.UserId, ct);
        if (user is null)
            return Result.Failure<PeriodAnalyticsResponse>(ErrorMessages.UserNotFound);
        var window = new ReadWindow(request.DateFrom, request.DateTo);
        var baseline = request.BaselineFrom is { } from && request.BaselineTo is { } to
            ? new ReadWindow(from, to) : null;
        var first = baseline is not null && baseline.From < window.From ? baseline.From : window.From;
        var last = baseline is not null && baseline.To > window.To ? baseline.To : window.To;
        var rows = await habits.FindAsync(h => h.UserId == request.UserId && !h.IsDeleted
                && (!request.HabitId.HasValue || h.Id == request.HabitId)
                && (!request.IsBadHabit.HasValue || h.IsBadHabit == request.IsBadHabit),
            q => q.Include(h => h.Tags).Include(h => h.Logs.Where(l => !l.IsDeleted && l.Date >= first && l.Date <= last)).AsSplitQuery(), ct);
        if (request.HabitId.HasValue && rows.Count == 0)
            return Result.Failure<PeriodAnalyticsResponse>(ErrorMessages.HabitNotFound);
        var selected = rows.Where(h => string.IsNullOrWhiteSpace(request.Search) || HabitNameMatcher.Matches(h.Title, request.Search))
            .Where(h => string.IsNullOrWhiteSpace(request.Tag) || h.Tags.Any(t => HabitNameMatcher.Matches(t.Name, request.Tag)))
            .OrderBy(h => h.Id).ToList();
        var legacySelected = selected.Where(h => request.HabitId.HasValue || h.ParentHabitId is null).ToList();
        var zone = TimeZoneHelper.FindTimeZone(user.TimeZone);
        var (current, series) = PeriodAnalyticsCalculator.Calculate(legacySelected, window, zone, user.WeekStartDay);
        current = current with { CompletedHabits = PeriodAnalyticsCalculator.CountCompletedHabits(selected, window) };
        var value = PeriodAnalyticsCalculator.Value(current, request.Metric);
        var unit = request.Metric switch
        {
            "completedHabits" => "habits",
            "positiveLogRecords" => "records",
            _ => "days"
        };
        PeriodComparisonCard? comparison = null;
        if (baseline is not null)
        {
            var (previous, _) = PeriodAnalyticsCalculator.Calculate(legacySelected, baseline, zone, user.WeekStartDay);
            previous = previous with { CompletedHabits = PeriodAnalyticsCalculator.CountCompletedHabits(selected, baseline) };
            var previousValue = PeriodAnalyticsCalculator.Value(previous, request.Metric);
            comparison = new(request.Metric, unit, current, previous, value, previousValue, value - previousValue,
                previousValue == 0 ? null : (decimal)(value - previousValue) / previousValue, request.HabitId);
        }
        var revision = ReadRevision.Hash(new
        {
            user.TimeZone,
            user.WeekStartDay,
            Habits = selected.Select(h => new
            {
                h.Id,
                h.UpdatedAtUtc,
                Logs = h.Logs.Where(l => !l.IsDeleted).OrderBy(l => l.Id)
                .Select(l => new { l.Id, l.Date, l.Value, l.UpdatedAtUtc })
            })
        });
        return Result.Success(new PeriodAnalyticsResponse(
            new(request.Metric, unit, value, current, series, request.HabitId), comparison, revision));
    }
}
