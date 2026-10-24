using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Habits;

public class GetHabitRecurrenceOriginTests
{
    [Theory]
    [InlineData(false, FrequencyUnit.Month, 2025, 1, 31)]
    [InlineData(true, FrequencyUnit.Month, 2025, 1, 31)]
    [InlineData(false, FrequencyUnit.Year, 2024, 2, 29)]
    [InlineData(true, FrequencyUnit.Year, 2024, 2, 29)]
    public async Task Detail_AfterShortMonthCompletion_ReturnsPersistedRecurrenceOrigin(
        bool fullDetail, FrequencyUnit frequency, int year, int month, int day)
    {
        var origin = new DateOnly(year, month, day);
        var habit = Habit.Create(new HabitCreateParams(((FrequencyUnit?)(frequency)) is null ? HabitSchedule.OneTime : HabitSchedule.Recurring,
            Guid.NewGuid(), "Recurring habit", frequency, 1, origin)).Value;
        habit.Log(origin).IsSuccess.Should().BeTrue();
        var nextDueDate = frequency == FrequencyUnit.Month ? origin.AddMonths(1) : origin.AddYears(1);
        habit.NextDueDate.Should().Be(nextDueDate);
        habit.ScheduledStartDate.Should().Be(origin);
        habit.OriginalDayOfMonth.Should().Be(day);

        var detail = await ReadDetail(habit, fullDetail);

        detail.GetProperty("dueDate").GetString().Should().Be(nextDueDate.ToString("yyyy-MM-dd"));
        detail.GetProperty("scheduledStartDate").GetString().Should().Be(origin.ToString("yyyy-MM-dd"));
        detail.GetProperty("originalDayOfMonth").GetInt32().Should().Be(day);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detail_DailyHabit_ReturnsStartAndNullOriginalDay(bool fullDetail)
    {
        var origin = new DateOnly(2025, 1, 31);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            Guid.NewGuid(), "Daily habit", FrequencyUnit.Day, 1, origin)).Value;

        var detail = await ReadDetail(habit, fullDetail);

        detail.GetProperty("scheduledStartDate").GetString().Should().Be("2025-01-31");
        detail.GetProperty("originalDayOfMonth").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detail_LegacyHabitWithoutOrigins_ReturnsNullWithoutInferringFromDueDate(bool fullDetail)
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            Guid.NewGuid(), "Legacy habit", FrequencyUnit.Month, 1, new DateOnly(2025, 2, 28))).Value;
        typeof(Habit).GetProperty(nameof(Habit.ScheduledStartDate))!.SetValue(habit, null);
        typeof(Habit).GetProperty(nameof(Habit.OriginalDayOfMonth))!.SetValue(habit, null);

        var detail = await ReadDetail(habit, fullDetail);

        detail.GetProperty("dueDate").GetString().Should().Be("2025-02-28");
        detail.GetProperty("scheduledStartDate").ValueKind.Should().Be(JsonValueKind.Null);
        detail.GetProperty("originalDayOfMonth").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static async Task<JsonElement> ReadDetail(Habit habit, bool fullDetail)
    {
        var habits = Substitute.For<IGenericRepository<Habit>>();
        var logs = Substitute.For<IGenericRepository<HabitLog>>();
        var dates = Substitute.For<IUserDateService>();
        habits.FindAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());
        logs.FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(habit.Logs.ToList().AsReadOnly());
        dates.GetUserTodayAsync(habit.UserId, Arg.Any<CancellationToken>()).Returns(habit.NextDueDate);
        dates.GetUserWeekStartDayAsync(habit.UserId, Arg.Any<CancellationToken>()).Returns(1);

        HabitDetailResponse detail;
        if (fullDetail)
        {
            var users = Substitute.For<IGenericRepository<User>>();
            users.GetByIdAsync(habit.UserId, Arg.Any<CancellationToken>())
                .Returns(User.Create("Alex", "alex@example.com").Value);
            var result = await new GetHabitFullDetailQueryHandler(habits, logs, users, dates)
                .Handle(new GetHabitFullDetailQuery(habit.UserId, habit.Id), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            detail = result.Value.Habit;
        }
        else
        {
            var result = await new GetHabitByIdQueryHandler(habits, logs, dates)
                .Handle(new GetHabitByIdQuery(habit.UserId, habit.Id), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            detail = result.Value;
        }

        return JsonSerializer.SerializeToElement(detail, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
