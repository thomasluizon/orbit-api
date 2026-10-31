using System.Text.Json;
using System.Linq.Expressions;
using NSubstitute;
using Orbit.Domain.Common;
using FluentAssertions;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Services;

public partial class AiSummaryServiceTests
{
    [Theory]
    [InlineData("en", "Meals and water are still open, starting with Psyllium 5g.")]
    [InlineData("pt-BR", "As refeições e a hidratação seguem em aberto, começando por Psyllium 5g.")]
    public async Task DailySummary_OwnerDayAllowsDigitsOnlyInStoredTitles(string language, string sentence)
    {
        var (habits, context) = await OwnerDay(language);
        var next = context.PendingHabits!.Single(item => item.Title == "Psyllium 5g");
        var calls = 0;
        var service = BuildSummaryService(JsonSerializer.Serialize(new
        {
            habitId = next.Id,
            nextStepHabitId = next.Id,
            summary = sentence
        }), _ => calls++);

        var result = await service.GenerateSummaryAsync(habits, context);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Summary.Should().Be(sentence);
        calls.Should().Be(1);
    }

    [Theory]
    [InlineData("{\"summary\":\"There are 99 tasks remaining.\"}")]
    [InlineData("{\"summary\":\"   \"}")]
    [InlineData("{\"summary\":\"** **\"}")]
    [InlineData("{\"nextStepHabitId\":\"invalid\"}")]
    public async Task DailySummary_InvalidOutputRetriesThenReturnsUnavailable(string reply)
    {
        var (habits, context) = await OwnerDay("pt-BR");
        var calls = 0;
        var service = BuildSummaryService(reply, _ => calls++);

        var result = await service.GenerateSummaryAsync(habits, context);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.AiSummaryUnavailable.Message);
        calls.Should().Be(2);
    }

    private static async Task<(List<Habit> Habits, DailySummaryContext Context)> OwnerDay(string language)
    {
        var habits = new List<Habit>();
        Habit Add(string title, Guid? parent = null, TimeOnly? time = null, bool bad = false)
        {
            var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, ValidUserId, title,
                FrequencyUnit.Day, 1, DueDate: Today, DueTime: time, ParentHabitId: parent,
                Position: habits.Count, IsBadHabit: bad)).Value;
            habits.Add(habit);
            return habit;
        }
        var water = Add("Water");
        for (var index = 0; index < 6; index++)
            Add("Water - 710ml", water.Id, new TimeOnly(14 + index, 0));
        var meals = Add("Meals");
        Add("Breakfast", meals.Id, new TimeOnly(8, 0)).Log(Today);
        Add("Lunch", meals.Id, new TimeOnly(12, 0));
        Add("Afternoon snack", meals.Id, new TimeOnly(16, 0));
        Add("Dinner", meals.Id, new TimeOnly(20, 0));
        var postLunch = Add("Post Lunch");
        Add("Pills", postLunch.Id);
        Add("Creatine", postLunch.Id);
        var waking = Add("Waking up");
        Add("Morning skin care", waking.Id, new TimeOnly(8, 0)).Log(Today);
        Add("Check money to receive", waking.Id, new TimeOnly(8, 30)).Log(Today);
        Add("Psyllium 5g", waking.Id, new TimeOnly(10, 30));
        var bad = Add("Tobacco", bad: true);
        Add("Smoking", bad.Id, bad: true);

        var repo = Substitute.For<IGenericRepository<Habit>>();
        repo.ProjectAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<HabitScheduleCandidate>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<IQueryable<Habit>, IQueryable<HabitScheduleCandidate>>>(1)(
                habits.Where(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()).AsQueryable()).ToList());
        repo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()).ToList());
        var reader = Substitute.For<IHabitScheduleLogReader>();
        reader.ReadDaysAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => habits.SelectMany(habit => habit.Logs).GroupBy(log => new { log.HabitId, log.Date })
                .Select(group => new HabitScheduleLogDay(group.Key.HabitId, group.Key.Date,
                    group.Count(log => log.Value > 0), group.Count(log => log.Value == 0), true)).ToList());
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(ValidUserId, Arg.Any<CancellationToken>()).Returns(Today);
        dates.GetUserWeekStartDayAsync(ValidUserId, Arg.Any<CancellationToken>()).Returns(1);
        var handler = new GetHabitScheduleQueryHandler(repo, reader, Substitute.For<IHabitSchedulePageLoader>(),
            dates, Substitute.For<IUnitOfWork>(), Substitute.For<IRepeatReminderReader>());
        var schedule = await handler.Handle(new(ValidUserId, Today, Today, IncludeOverdue: true), default);
        schedule.IsSuccess.Should().BeTrue(schedule.Error);
        return (habits, new(Today, Today, Today, language, new TimeOnly(15, 0), 0, 0,
            new Dictionary<Guid, DateOnly>(), UserId: ValidUserId,
            PendingHabits: DailySummaryPendingProjection.Project(schedule.Value.Items, Today)));
    }
}
