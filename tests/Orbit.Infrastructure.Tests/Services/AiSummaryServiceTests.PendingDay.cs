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
    public async Task DailySummary_PendingDayIncludesEveryStepInScheduleOrder(string language)
    {
        var replay = await ReplayPendingDay(language);
        using var request = JsonDocument.Parse(replay.Request);
        var prompt = request.RootElement.GetProperty("input")[1].GetProperty("content")[0].GetProperty("text").GetString()!;
        const string marker = "Pending habits in day order (JSON):\n";
        prompt.Should().Contain(marker);
        var pendingJson = prompt.Split(marker, StringSplitOptions.None)[1].Split('\n')[0];
        using var pending = JsonDocument.Parse(pendingJson);
        var rows = pending.RootElement.EnumerateArray().ToList();

        rows.Should().HaveCount(11, "both pending parents, three meals and all six waters must reach the model");
        rows[0].GetProperty("Title").GetString().Should().Be("Lunch");
        rows[0].GetProperty("DueTime").GetString().Should().Be("12:00:00");
        rows[0].GetProperty("DueEndTime").GetString().Should().Be("13:00:00");
        rows[0].GetProperty("Position").GetInt32().Should().Be(1);
        rows[0].GetProperty("ParentTitle").GetString().Should().Be("Meals");
        rows[0].GetProperty("DueDate").GetString().Should().Be(Today.ToString("yyyy-MM-dd"));
        var waterRows = rows.Where(row => row.GetProperty("Title").GetString() == "Water - 710ml").ToList();
        waterRows.Should().HaveCount(6);
        waterRows.Select(row => row.GetProperty("Position").GetInt32()).Should().Equal(0, 1, 2, 3, 4, 5);
        waterRows.Should().OnlyContain(row => row.GetProperty("ParentTitle").GetString() == "Water");
        var meals = rows.Single(row => row.GetProperty("Title").GetString() == "Meals");
        meals.GetProperty("CompletedCount").GetInt32().Should().Be(1);
        meals.GetProperty("TotalCount").GetInt32().Should().Be(4);
        var water = rows.Single(row => row.GetProperty("Title").GetString() == "Water");
        water.GetProperty("CompletedCount").GetInt32().Should().Be(0);
        water.GetProperty("TotalCount").GetInt32().Should().Be(6);
        rows.Should().NotContain(row => row.GetProperty("Title").GetString() == "Breakfast");
        prompt.Should().Contain("Choose the first actionable pending item in day order");
        prompt.Should().Contain("Never choose a later pending sibling while an earlier sibling is pending");
        prompt.Should().Contain("explain the priority in one short clause");
        replay.Summary.Should().NotContain("Afternoon snack");
    }

    [Theory]
    [InlineData("en", "English", "Your lunch is the next step because it is the earliest pending meal.")]
    [InlineData("pt-BR", "Brazilian Portuguese", "Seu almoço é o próximo passo por ser a primeira refeição pendente.")]
    public async Task DailySummary_LocalizesForeignTitlesWithoutChangingStoredData(
        string language, string languageName, string expectedSentence)
    {
        var replay = await ReplayPendingDay(language);
        using var request = JsonDocument.Parse(replay.Request);
        var system = request.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
        var prompt = request.RootElement.GetProperty("input")[1].GetProperty("content")[0].GetProperty("text").GetString()!;

        system.Should().Contain("Refer to a stored title in another language naturally in the profile language");
        system.Should().Contain("never insert the raw foreign-language title into the sentence");
        prompt.Should().Contain($"Write ONLY in {languageName}");
        prompt.Should().NotContain("using its supplied title in natural phrasing");
        replay.Summary.Should().Be(expectedSentence).And.NotContain("Lunch").And.NotContain("Afternoon snack");
        replay.Habits.Select(habit => habit.Title).Should().Contain("Lunch").And.Contain("Afternoon snack");
        replay.Habits.Select(habit => habit.Title).Should().Equal(replay.StoredTitles);
    }

    private static async Task<(string Request, string Summary, List<Habit> Habits, string[] StoredTitles)> ReplayPendingDay(string language)
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
        var sentence = language == "pt-BR"
            ? "Seu almoço é o próximo passo por ser a primeira refeição pendente."
            : "Your lunch is the next step because it is the earliest pending meal.";
        var service = BuildSummaryService(JsonSerializer.Serialize(new { summary = sentence }), request => body = request);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new GetDailySummaryQueryHandler(repo, users, Substitute.For<IGenericRepository<HabitLog>>(),
            summaryLogs, gate, service, cache, dates, mediator);

        var result = await handler.Handle(new(ValidUserId, Today, Today, language == "pt-BR" ? "en" : "pt-BR"), default);

        result.IsSuccess.Should().BeTrue();
        return (body!, result.Value.Summary, habits, storedTitles);
    }
}
