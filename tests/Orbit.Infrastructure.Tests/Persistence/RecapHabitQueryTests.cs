using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Habits.Services;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class RecapHabitQueryTests
{
    [Theory]
    [InlineData("America/Sao_Paulo", "month")]
    [InlineData("Pacific/Kiritimati", "week")]
    [InlineData("Europe/Berlin", "year")]
    public async Task ClosedRecap_ExcludesEarlierDeletionsAtLocalPeriodBoundary(string timeZoneId, string period)
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var context = factory.Context;
        var dateFrom = new DateOnly(2026, period == "year" ? 1 : 6, 1);
        var dateTo = period switch
        {
            "year" => new DateOnly(2026, 12, 31),
            "week" => dateFrom.AddDays(6),
            _ => dateFrom.AddMonths(1).AddDays(-1)
        };
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var boundaryUtc = TimeZoneInfo.ConvertTimeToUtc(dateFrom.ToDateTime(TimeOnly.MinValue), timeZone);
        var user = User.Create("Recap User", $"recap-{Guid.NewGuid():N}@example.com").Value;
        user.SetTimeZone(timeZoneId).IsSuccess.Should().BeTrue();
        typeof(User).GetProperty(nameof(User.CreatedAtUtc))!.SetValue(user,
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        context.Users.Add(user);

        Habit CreateHabit()
        {
            var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Habit",
                FrequencyUnit.Day, 1, DueDate: dateFrom,
                CreatedAtUtc: boundaryUtc.AddDays(-30))).Value;
            context.Habits.Add(habit);
            return habit;
        }

        var live = CreateHabit();
        live.Log(dateFrom, advanceDueDate: false);
        var atBoundary = CreateHabit();
        atBoundary.Log(dateFrom, advanceDueDate: false);
        atBoundary.SoftDelete(boundaryUtc);
        var during = CreateHabit();
        during.Log(dateFrom.AddDays(1), advanceDueDate: false);
        during.SoftDelete(boundaryUtc.AddDays(2));
        var after = CreateHabit();
        after.SoftDelete(boundaryUtc.AddYears(1));
        var legacy = CreateHabit();
        legacy.SoftDelete(boundaryUtc);
        typeof(Habit).GetProperty(nameof(Habit.DeletedAtUtc))!.SetValue(legacy, null);
        var included = new[] { live, atBoundary, during, after, legacy };
        var all = included.ToList();
        for (var index = 0; index < 250; index++)
        {
            var old = CreateHabit();
            old.SoftDelete(boundaryUtc.AddTicks(-1 - index));
            all.Add(old);
        }
        var expected = RetrospectiveMetricsCalculator.ComputeHistorical(
            all, dateFrom, dateTo, 3, 8, timeZone);
        expected.Should().BeEquivalentTo(RetrospectiveMetricsCalculator.ComputeHistorical(
            included.ToList(), dateFrom, dateTo, 3, 8, timeZone));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetOrCreateReferralCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("ABCD2345"));
        var streaks = Substitute.For<IUserStreakService>();
        streaks.RecalculateAsync(user.Id, false, Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(3, 8, dateTo));
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<RecapResponse>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<Result<RecapResponse>>>>(0)(
                call.ArgAt<CancellationToken>(1)));
        var recapStore = Substitute.For<IClosedMonthRecapStore>();
        recapStore.FindResponseJsonAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var handler = new GetRecapQueryHandler(new GenericRepository<Habit>(context),
            new GenericRepository<Goal>(context), new GenericRepository<User>(context), streaks,
            Options.Create(new FrontendSettings { BaseUrl = "https://app.useorbit.org" }), mediator,
            recapStore, unitOfWork);
        var request = new GetRecapQuery(user.Id, dateFrom, dateTo, period,
            ClosedYear: period == "week" ? null : dateFrom.Year,
            ClosedMonth: period == "month" ? dateFrom.Month : null,
            ClosedWeekStart: period == "week" ? dateFrom : null);

        counter.Reset();
        var result = await handler.Handle(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Metrics.Should().BeEquivalentTo(expected);
        var habitRead = counter.Commands.Single(command => command.Sql.Contains("FROM \"Habits\"", StringComparison.Ordinal)
            && !command.Sql.Contains("JOIN", StringComparison.Ordinal));
        habitRead.Rows.Should().Be(included.Length);
    }
}
