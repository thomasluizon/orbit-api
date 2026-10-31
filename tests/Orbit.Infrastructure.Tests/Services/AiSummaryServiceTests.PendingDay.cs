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
    public async Task DailySummary_PendingDaySelectsOneStepBeforeGeneratingNarrative(string language)
    {
        var replay = await ReplayPendingDay(language);
        using var request = JsonDocument.Parse(replay.Request);
        var prompt = request.RootElement.GetProperty("input")[1].GetProperty("content")[0].GetProperty("text").GetString()!;
        const string marker = "Selected next step (JSON):\n";
        prompt.Should().Contain(marker);
        var pendingJson = prompt.Split(marker, StringSplitOptions.None)[1].Split('\n')[0];
        using var pending = JsonDocument.Parse(pendingJson);
        var selected = pending.RootElement;

        selected.GetProperty("habitId").GetGuid().Should().Be(replay.Habits.Single(habit => habit.Title == "Lunch").Id);
        selected.GetProperty("title").GetString().Should().Be("Lunch");
        selected.GetProperty("parentTitle").GetString().Should().Be("Meals");
        selected.GetProperty("orderBasis").GetString().Should().Be("first scheduled time");
        selected.GetProperty("closesRoutine").GetBoolean().Should().BeFalse();
        prompt.Should().Contain("The server selected this item from the complete pending day by due date, then DueTime or DueEndTime, then DayOrder; untimed items follow timed items.");
        prompt.Should().Contain("Return only a JSON object with exactly these fields: \"habitId\" (copy the selected habitId exactly) and \"summary\" (the sentence).");
        prompt.Should().NotContain("Afternoon snack").And.NotContain("Dinner").And.NotContain("Water");
        prompt.Should().NotContain("Today's habits:").And.NotContain("history:").And.NotContain("Progress:");
        prompt.Should().NotContain("Breakfast", "a next-step generation must not receive completed rows to recap");
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
        prompt.Should().Contain($"Write exactly one sentence naming only this item naturally in {languageName}, followed by one short reason based on orderBasis.");
        prompt.Should().NotContain("using its supplied title in natural phrasing");
        replay.Summary.Should().Be(expectedSentence).And.NotContain("Lunch").And.NotContain("Afternoon snack");
        replay.Habits.Select(habit => habit.Title).Should().Contain("Lunch").And.Contain("Afternoon snack");
        replay.Habits.Select(habit => habit.Title).Should().Equal(replay.StoredTitles);
    }

    [Theory]
    [InlineData("en", "Breakfast")]
    [InlineData("pt-BR", "Breakfast")]
    [InlineData("en", "Meals")]
    [InlineData("pt-BR", "Meals")]
    [InlineData("en", "Afternoon snack")]
    [InlineData("pt-BR", "Afternoon snack")]
    [InlineData("en", "unknown")]
    [InlineData("pt-BR", "unknown")]
    [InlineData("en", "missing")]
    [InlineData("pt-BR", "missing")]
    [InlineData("en", "malformed")]
    [InlineData("pt-BR", "malformed")]
    public async Task DailySummary_UnboundOrInvalidNextStepDropsOnlyNarrative(string language, string focus)
    {
        var replay = await ReplayPendingDay(language, habits => focus == "malformed" ? "{\"habitId\":\"invalid\"}"
            : JsonSerializer.Serialize(new
            {
                habitId = focus == "missing" ? (Guid?)null : focus == "unknown" ? Guid.Empty
                    : habits.Single(habit => habit.Title == focus).Id,
                summary = "A recap that must not reach the person."
            }));

        replay.Summary.Should().BeEmpty();
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DailySummary_WithoutPendingStepsKeepsHistoryInsights(string language)
    {
        var habit = CreateHabit("Reading", dueDate: Today.AddDays(-2));
        habit.Log(Today.AddDays(-2)).IsSuccess.Should().BeTrue();
        habit.Log(Today.AddDays(-1)).IsSuccess.Should().BeTrue();
        habit.Log(Today).IsSuccess.Should().BeTrue();
        string? body = null;
        var sentence = language == "pt-BR" ? "A leitura tem feito parte da sua rotina."
            : "Reading has been part of your routine.";
        var service = BuildSummaryService(JsonSerializer.Serialize(new { summary = sentence }), request => body = request);

        var result = await service.GenerateSummaryAsync([habit], new(Today, Today, Today, language,
            null, 0, 0, new Dictionary<Guid, DateOnly>(), UserId: ValidUserId, PendingHabits: []));

        result.IsSuccess.Should().BeTrue();
        result.Value.Summary.Should().Be(sentence);
        body.Should().Contain("14-day history: done=3, skipped=0, missed=0").And.NotContain("Selected next step");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DailySummary_AlmostClosedRoutineUsesAClosingFactWithoutCompletedRows(string language)
    {
        var replay = await ReplayPendingDay(language, nearlyClosed: true);
        using var request = JsonDocument.Parse(replay.Request);
        var prompt = request.RootElement.GetProperty("input")[1].GetProperty("content")[0].GetProperty("text").GetString()!;
        var selectedJson = prompt.Split("Selected next step (JSON):\n", StringSplitOptions.None)[1].Split('\n')[0];
        using var selected = JsonDocument.Parse(selectedJson);

        selected.RootElement.GetProperty("title").GetString().Should().Be("Lunch");
        selected.RootElement.GetProperty("closesRoutine").GetBoolean().Should().BeTrue();
        prompt.Should().Contain("When closesRoutine is true, the reason may connect this next step to closing its parent routine without describing any completed step.");
        prompt.Should().NotContain("Breakfast").And.NotContain("Afternoon snack").And.NotContain("Dinner");
        replay.Summary.Should().NotBeEmpty();
    }

    private static async Task<(string Request, string Summary, List<Habit> Habits, string[] StoredTitles)> ReplayPendingDay(
        string language, Func<List<Habit>, string>? modelReply = null, bool nearlyClosed = false)
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
        var sentence = language == "pt-BR"
            ? "Seu almoço é o próximo passo por ser a primeira refeição pendente."
            : "Your lunch is the next step because it is the earliest pending meal.";
        var service = BuildSummaryService(modelReply?.Invoke(habits)
            ?? JsonSerializer.Serialize(new { habitId = habits.Single(habit => habit.Title == "Lunch").Id, summary = sentence }),
            request => body = request);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new GetDailySummaryQueryHandler(repo, users, Substitute.For<IGenericRepository<HabitLog>>(),
            summaryLogs, gate, service, cache, dates, mediator);

        var result = await handler.Handle(new(ValidUserId, Today, Today, language == "pt-BR" ? "en" : "pt-BR"), default);

        result.IsSuccess.Should().BeTrue();
        return (body!, result.Value.Summary, habits, storedTitles);
    }
}
