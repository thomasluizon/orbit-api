using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Services;

public partial class AiSummaryServiceTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DailySummary_PendingDayIncludesWholeDayAndServerSelectedStep(string language)
    {
        var replay = await ReplayPendingDay(language);
        using var request = JsonDocument.Parse(replay.Request);
        var prompt = request.RootElement.GetProperty("input")[1].GetProperty("content")[0].GetProperty("text").GetString()!;
        using var facts = ReadFacts(prompt);
        var selected = facts.RootElement.GetProperty("nextStep");
        selected.GetProperty("id").GetGuid().Should().Be(replay.Habits.Single(habit => habit.Title == "Lunch").Id);
        selected.GetProperty("parentTitle").GetString().Should().Be("Meals");
        selected.GetProperty("orderBasis").GetString().Should().Be("first scheduled time");
        prompt.Should().Contain("Afternoon snack").And.Contain("Dinner").And.Contain("Water - 710ml");
        prompt.Should().NotContain("history");
        replay.Habits.Select(habit => habit.Title).Should().Equal(replay.StoredTitles);
        replay.Summary.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("en", "Meals has open steps, starting with lunch.")]
    [InlineData("pt-BR", "As refeições seguem em aberto, começando pelo almoço.")]
    public async Task DailySummary_LocalizesForeignTitlesWithoutChangingStoredData(string language, string expectedSentence)
    {
        var replay = await ReplayPendingDay(language);
        replay.Summary.Should().Be(expectedSentence);
        replay.Habits.Select(habit => habit.Title).Should().Equal(replay.StoredTitles);
        replay.Habits.Select(habit => habit.Title).Should().Contain("Lunch");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DailySummary_AlmostClosedRoutineIncludesCompletedProgress(string language)
    {
        var replay = await ReplayPendingDay(language, nearlyClosed: true);
        using var request = JsonDocument.Parse(replay.Request);
        using var facts = ReadFacts(request.RootElement.GetProperty("input")[1].GetProperty("content")[0].GetProperty("text").GetString()!);
        var meals = facts.RootElement.GetProperty("routines").EnumerateArray().Single(item => item.GetProperty("title").GetString() == "Meals");
        meals.GetProperty("state").GetString().Should().Be("nearly done");
        meals.GetProperty("completedSteps").GetInt32().Should().Be(3);
        meals.GetProperty("totalSteps").GetInt32().Should().Be(4);
        meals.GetProperty("pendingSteps").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("title").GetString().Should().Be("Lunch");
    }

    [Fact]
    public async Task DailySummary_RejectedOutputIsNotCachedAndSecondRequestCallsModelAgain()
    {
        var replay = await ReplayPendingDay("pt-BR", habits => JsonSerializer.Serialize(new
        {
            nextStepHabitId = habits.Single(habit => habit.Title == "Lunch").Id,
            summary = "Faltam 99 hábitos, começando pelo almoço."
        }), expectFailure: true, repeatRequests: 2);
        replay.Calls.Should().Be(4);
    }

    private static async Task<(string Request, string Summary, List<Habit> Habits, string[] StoredTitles, int Calls)> ReplayPendingDay(
        string language, Func<List<Habit>, string>? modelReply = null, bool nearlyClosed = false, bool expectFailure = false, int repeatRequests = 1)
    {
        var habits = new List<Habit>();
        Habit Add(string title, int position, Guid? parent = null, TimeOnly? time = null, TimeOnly? end = null)
        {
            var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, ValidUserId, title,
                FrequencyUnit.Day, 1, DueDate: Today, DueTime: time, DueEndTime: end,
                ParentHabitId: parent, Position: position)).Value;
            habits.Add(habit);
            return habit;
        }
        var meals = Add("Meals", 0);
        var water = Add("Water", 1);
        Add("Afternoon snack", 2, meals.Id, new TimeOnly(16, 0));
        Add("Dinner", 3, meals.Id, new TimeOnly(20, 0));
        Add("Lunch", 1, meals.Id, new TimeOnly(12, 0), new TimeOnly(13, 0));
        var breakfast = Add("Breakfast", 0, meals.Id, new TimeOnly(8, 0));
        breakfast.Log(Today).IsSuccess.Should().BeTrue();
        if (nearlyClosed)
            foreach (var habit in habits.Where(habit => habit.Title is "Afternoon snack" or "Dinner"))
                habit.Log(Today).IsSuccess.Should().BeTrue();
        for (var index = 5; index >= 0; index--)
            Add("Water - 710ml", index, water.Id, new TimeOnly(14, 0));
        var storedTitles = habits.Select(habit => habit.Title).ToArray();
        var logs = habits.SelectMany(habit => habit.Logs).ToList();
        var repo = Substitute.For<IGenericRepository<Habit>>();
        repo.ProjectAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<HabitScheduleCandidate>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<IQueryable<Habit>, IQueryable<HabitScheduleCandidate>>>(1)(
                habits.Where(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()).AsQueryable()).ToList());
        repo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()).ToList());
        repo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()).ToList());
        var reader = Substitute.For<IHabitScheduleLogReader>();
        reader.ReadDaysAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<CancellationToken>())
            .Returns(logs.GroupBy(log => new { log.HabitId, log.Date }).Select(group => new HabitScheduleLogDay(
                group.Key.HabitId, group.Key.Date, group.Count(log => log.Value > 0), group.Count(log => log.Value == 0), true)).ToList());
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(ValidUserId, Arg.Any<CancellationToken>()).Returns(Today);
        dates.GetUserWeekStartDayAsync(ValidUserId, Arg.Any<CancellationToken>()).Returns(1);
        var schedule = new GetHabitScheduleQueryHandler(repo, reader, Substitute.For<IHabitSchedulePageLoader>(),
            dates, Substitute.For<IUnitOfWork>(), Substitute.For<IRepeatReminderReader>());
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetHabitScheduleQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => schedule.Handle(call.Arg<GetHabitScheduleQuery>(), call.Arg<CancellationToken>()));
        var user = User.Create("Person", "person@example.com").Value;
        user.SetLanguage(language);
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(ValidUserId, Arg.Any<CancellationToken>()).Returns(user);
        var gate = Substitute.For<IPayGateService>();
        gate.CanUseDailySummary(ValidUserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var summaryLogs = Substitute.For<IHabitSummaryLogReader>();
        summaryLogs.ReadAsync(Arg.Any<IReadOnlyCollection<HabitSummaryLogWindow>>(), Arg.Any<CancellationToken>())
            .Returns(logs.Select(log => new HabitSummaryLogFact(log.HabitId, log.Date, log.Value)).ToList());
        string? body = null;
        var calls = 0;
        var sentence = language == "pt-BR"
            ? "As refeições seguem em aberto, começando pelo almoço."
            : "Meals has open steps, starting with lunch.";
        var service = BuildSummaryService(modelReply?.Invoke(habits)
            ?? JsonSerializer.Serialize(new { nextStepHabitId = habits.Single(habit => habit.Title == "Lunch").Id, summary = sentence }),
            request => { body = request; calls++; });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new GetDailySummaryQueryHandler(repo, users, Substitute.For<IGenericRepository<HabitLog>>(),
            summaryLogs, gate, service, cache, dates, mediator);

        Result<DailySummaryResponse>? result = null;
        for (var request = 0; request < repeatRequests; request++)
        {
            result = await handler.Handle(new(ValidUserId, Today, Today, language == "pt-BR" ? "en" : "pt-BR"), default);
            result.IsFailure.Should().Be(expectFailure);
        }
        return (body!, result!.IsSuccess ? result.Value.Summary : string.Empty, habits, storedTitles, calls);
    }
}
