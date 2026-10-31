using System.Text.Json;
using System.Linq.Expressions;
using NSubstitute;
using Orbit.Domain.Common;
using Orbit.Infrastructure.Services;
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

    private static JsonDocument ReadFacts(string prompt) => JsonDocument.Parse(prompt.Split("Daily facts (JSON):\n")[1]);

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DailySummary_FactSheetContainsOwnerRoutinesWithoutHistory(string language)
    {
        var (habits, context) = await OwnerDay(language);
        using var facts = ReadFacts(AiSummaryService.BuildSummaryPrompt(habits, context));
        var root = facts.RootElement;
        root.GetProperty("partOfDay").GetString().Should().Be("afternoon");
        var routines = root.GetProperty("routines").EnumerateArray().ToList();
        routines.Select(item => item.GetProperty("title").GetString()).Should().BeEquivalentTo(
            ["Water", "Meals", "Post Lunch", "Waking up"]);
        var waking = routines.Single(item => item.GetProperty("title").GetString() == "Waking up");
        waking.GetProperty("state").GetString().Should().Be("nearly done");
        waking.GetProperty("completedSteps").GetInt32().Should().Be(2);
        waking.GetProperty("totalSteps").GetInt32().Should().Be(3);
        waking.GetProperty("pendingSteps")[0].GetProperty("isOverdue").GetBoolean().Should().BeTrue();
        root.GetProperty("badHabits").EnumerateArray().Should().OnlyContain(item => item.GetProperty("state").GetString() == "clean");
        root.GetProperty("nextStep").GetProperty("title").GetString().Should().Be("Psyllium 5g");
        root.ToString().Should().NotContain("history").And.NotContain("streak");
    }

    [Fact]
    public async Task DailySummary_StaticInstructionsAreIdenticalAcrossProfilesAndStates()
    {
        var systems = new List<string>();
        foreach (var language in new[] { "en", "pt-BR" })
        {
            var (habits, context) = await OwnerDay(language);
            foreach (var pending in new[] { context.PendingHabits, Array.Empty<DailySummaryPendingHabit>() })
            {
                var next = DailySummaryPendingProjection.SelectNextStep(pending);
                var service = BuildSummaryService(JsonSerializer.Serialize(new { summary = "Your day is taking shape.", nextStepHabitId = next?.Id }), body =>
                {
                    using var request = JsonDocument.Parse(body);
                    systems.Add(request.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()!);
                });
                (await service.GenerateSummaryAsync(habits, context with { PendingHabits = pending })).IsSuccess.Should().BeTrue();
            }
        }
        systems.Should().HaveCount(4).And.OnlyContain(system => system == AiSummaryService.SystemPrompt);
    }

    [Fact]
    public async Task DailySummary_InventedFigureWithCorrectNextStepIsRejected()
    {
        var (habits, context) = await OwnerDay("en");
        var calls = 0;
        var service = BuildSummaryService(JsonSerializer.Serialize(new
        {
            nextStepHabitId = DailySummaryPendingProjection.SelectNextStep(context.PendingHabits)!.Id,
            summary = "With 99 routines open, start with Psyllium 5g."
        }), _ => calls++);
        (await service.GenerateSummaryAsync(habits, context)).IsFailure.Should().BeTrue();
        calls.Should().Be(2);
    }

    [Fact]
    public async Task DailySummary_RetryCanRecoverFromMalformedOutput()
    {
        var (habits, context) = await OwnerDay("pt-BR");
        var next = DailySummaryPendingProjection.SelectNextStep(context.PendingHabits)!;
        const string sentence = "As refeições seguem em aberto, começando por Psyllium 5g.";
        var calls = 0;
        var service = BuildSummaryService(attempt => attempt == 1 ? "{invalid}" : JsonSerializer.Serialize(new
        {
            nextStepHabitId = next.Id,
            summary = sentence
        }), _ => calls++);
        var result = await service.GenerateSummaryAsync(habits, context);
        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Summary.Should().Be(sentence);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task DailySummary_DigitsInParentTitleAreAllowed()
    {
        var (habits, context) = await OwnerDay("en", "Routine 2");
        var next = DailySummaryPendingProjection.SelectNextStep(context.PendingHabits)!;
        const string sentence = "Routine 2 is nearly done, starting with Psyllium 5g.";
        var service = BuildSummaryService(JsonSerializer.Serialize(new { nextStepHabitId = next.Id, summary = sentence }));
        (await service.GenerateSummaryAsync(habits, context)).Value.Summary.Should().Be(sentence);
    }

    [Fact]
    public async Task DailySummary_CompletedDayUsesSameFactSheetWithoutInventingNextStep()
    {
        var habit = CreateHabit("Read");
        habit.Log(Today).IsSuccess.Should().BeTrue();
        var context = new DailySummaryContext(Today, Today, Today, "en", new TimeOnly(20, 0), 0, 0,
            new Dictionary<Guid, DateOnly>(), PendingHabits: []);
        using var facts = ReadFacts(AiSummaryService.BuildSummaryPrompt([habit], context));
        facts.RootElement.GetProperty("nextStep").ValueKind.Should().Be(JsonValueKind.Null);
        facts.RootElement.GetProperty("standaloneTasks")[0].GetProperty("state").GetString().Should().Be("done");
        var service = BuildSummaryService("{\"summary\":\"Your reading is done and the day has no open steps.\",\"nextStepHabitId\":null}");
        (await service.GenerateSummaryAsync([habit], context)).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("99. Meals are open, starting with Psyllium 5g.")]
    [InlineData("** **")]
    public async Task DailySummary_NormalizationCannotHideInvalidContent(string sentence)
    {
        var (habits, context) = await OwnerDay("en");
        var next = DailySummaryPendingProjection.SelectNextStep(context.PendingHabits)!;
        var service = BuildSummaryService(JsonSerializer.Serialize(new { nextStepHabitId = next.Id, summary = sentence }));
        (await service.GenerateSummaryAsync(habits, context)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task DailySummary_PartialFlexibleQuotaIsStillInProgress()
    {
        var (habits, context) = await OwnerDay("en", includeFlexible: true);
        context.PendingHabits!.Single(item => item.Title == "Gym").CompletedCount.Should().Be(1);
        using var facts = ReadFacts(AiSummaryService.BuildSummaryPrompt(habits, context));
        var exercise = facts.RootElement.GetProperty("routines").EnumerateArray()
            .Single(item => item.GetProperty("title").GetString() == "Exercise");
        exercise.GetProperty("state").GetString().Should().Be("in progress");
        exercise.GetProperty("completedSteps").GetInt32().Should().Be(0);
        exercise.GetProperty("pendingSteps").EnumerateArray().Should().ContainSingle();
    }

    private static async Task<(List<Habit> Habits, DailySummaryContext Context)> OwnerDay(string language, string wakingTitle = "Waking up", bool includeFlexible = false)
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
        var waking = Add(wakingTitle);
        Add("Morning skin care", waking.Id, new TimeOnly(8, 0)).Log(Today);
        Add("Check money to receive", waking.Id, new TimeOnly(8, 30)).Log(Today);
        Add("Psyllium 5g", waking.Id, new TimeOnly(10, 30));
        var bad = Add("Tobacco", bad: true);
        Add("Smoking", bad.Id, bad: true);

        if (includeFlexible)
        {
            var exercise = Add("Exercise");
            var gym = Habit.Create(new HabitCreateParams(HabitSchedule.Flexible, ValidUserId, "Gym",
                FrequencyUnit.Week, 3, DueDate: Today, ParentHabitId: exercise.Id)).Value;
            gym.Log(Today).IsSuccess.Should().BeTrue();
            habits.Add(gym);
        }

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
