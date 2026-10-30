using System.Text.Json;
using System.Linq.Expressions;
using NSubstitute;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Goals.Services;
using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public sealed class ProactiveOfferPlannerTests
{
    private static readonly DateOnly Today = new(2026, 10, 30);
    private readonly Guid user = Guid.NewGuid();

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void ReminderGapUsesScheduledOccurrencesAndResolvedLogs(int missed, bool expected)
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user, "Water", FrequencyUnit.Day, 1,
            Today.AddDays(-14), CreatedAtUtc: Today.AddDays(-14).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))).Value;
        var logs = Enumerable.Range(missed + 1, 14 - missed).Select(offset => new HabitScheduleLogDay(habit.Id, Today.AddDays(-offset),
            offset % 2, offset % 2 == 0 ? 1 : 0, true)).ToArray();
        ProactiveOfferPlanner.MissedDates(habit, Today, 1, logs).Should().HaveCount(missed);
        var read = Read(new HabitListCard("today", [new(habit.Id.ToString(), habit.Title, null, 0, false, "overdue")], Today));
        var offers = ProactiveOfferPlanner.Build([read], new(Today, 1, [habit], [], logs, false, false), "en");
        offers.Any(offer => offer.Family == "reminder").Should().Be(expected);
    }

    [Theory]
    [InlineData("complete", true)]
    [InlineData("partial", false)]
    [InlineData("stale", false)]
    public void EmptyDayNeedsACompleteCurrentScope(string completeness, bool expected)
    {
        var read = Read(new HabitListCard("today", [], Today, TotalCount: 0), completeness);
        var offers = ProactiveOfferPlanner.Build([read], new(Today, 1, [], [], [], false, false), "pt-BR");
        offers.Any(offer => offer.Family == "smallStep").Should().Be(expected);
        if (expected) offers.Single(offer => offer.Family == "smallStep").Action.Label.Should().Be("Escolher um passo pequeno");
    }

    [Theory]
    [InlineData("done", false)]
    [InlineData("today", true)]
    public void LogNeedsARealUnresolvedPositiveOccurrence(string status, bool expected)
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user, "Read", FrequencyUnit.Day, 1, Today,
            CreatedAtUtc: Today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))).Value;
        var read = Read(new HabitListCard("today", [new(habit.Id.ToString(), habit.Title, null, 0, false, status)], Today));
        var offers = ProactiveOfferPlanner.Build([read], new(Today, 1, [habit], [], [], false, false), "en");
        offers.Any(offer => offer.Family == "log").Should().Be(expected);
        var historical = read with { Block = ((HabitListCard)read.Block) with { Date = Today.AddDays(-1), Scope = "date" } };
        ProactiveOfferPlanner.Build([historical], new(Today, 1, [habit], [], [], false, false), "en")
            .Should().NotContain(offer => offer.Family == "log");
    }

    [Theory]
    [InlineData("behind", true)]
    [InlineData("at_risk", true)]
    [InlineData("on_track", false)]
    [InlineData("completed", false)]
    [InlineData(null, false)]
    public void GoalActionsNeedTypedBehindOrRiskEvidence(string? tracking, bool expected)
    {
        var id = Guid.NewGuid();
        var read = Read(new GoalListCard([new(id.ToString(), "Read", 2, 10, "books", null, tracking, Status: "Active")]));
        var offers = ProactiveOfferPlanner.Build([read], new(Today, 1, [], [], [], false, false), "en");
        offers.Any(offer => offer.Family is "goalLink" or "goalTarget").Should().Be(expected);
    }

    [Fact]
    public void CalendarAvailabilityDoesNotOfferAnUpgradeOrInventAConnection()
    {
        var read = Read(new HabitListCard("today", [], Today, TotalCount: 0));
        ProactiveOfferPlanner.Build([read], new(Today, 1, [], [], [], false, false), "en")
            .Should().NotContain(offer => offer.Family == "calendar");
        ProactiveOfferPlanner.Build([read], new(Today, 1, [], [], [], true, true), "en")
            .Should().NotContain(offer => offer.Family == "calendar");
        ProactiveOfferPlanner.Build([read], new(Today, 1, [], [], [], true, false), "en")
            .Single(offer => offer.Family == "calendar").Action.Surface.Should().Be("calendar-sync");
    }

    [Fact]
    public void MissedActionKeepsTheActualOccurrenceDate()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, user, "Task", null, null, Today.AddDays(-1))).Value;
        var read = Read(new HabitListCard("today", [new(habit.Id.ToString(), habit.Title, null, 0, false, "overdue")], Today));
        var offers = ProactiveOfferPlanner.Build([read], new(Today, 1, [habit], [], [], false, false), "en");
        offers.Single(offer => offer.Family == "skip").Action.Date.Should().Be(habit.NextDueDate);
    }

    [Fact]
    public async Task GoalEligibilityDoesNotCalculateOrExposeProjections()
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(user, "Read", 1_000_000_000m, "pages", Deadline: Today.AddDays(3))).Value;
        goal.UpdateProgress(1).IsSuccess.Should().BeTrue();
        var goals = Substitute.For<IGenericRepository<Goal>>();
        goals.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>>(), Arg.Any<CancellationToken>()).Returns([goal]);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user, Arg.Any<CancellationToken>()).Returns(Today);
        var sync = Substitute.For<IGoalProgressReadSyncer>();
        sync.ComputeFreshValuesAsync(user, Today, Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, int> { [goal.Id] = 1 });
        var result = await new QueryGoalsTool(goals, dates, sync).ExecuteAsync(JsonSerializer.SerializeToElement(new { }), user, default);
        result.Success.Should().BeTrue();
        var card = result.Payload.Should().BeOfType<GoalListCard>().Subject;
        card.OfferTracking![goal.Id].Should().Be("at_risk");
        card.Items.Single().TrackingStatus.Should().BeNull();
        card.Items.Single().ProjectedCompletionDate.Should().BeNull();
    }

    private static QueryReadBlock Read(object card, string completeness = "complete") => new("habitList", card,
        new(Guid.NewGuid(), "query_habits", JsonSerializer.SerializeToElement(new { date = Today.ToString("yyyy-MM-dd") }), "revision", completeness, "token"));
}
