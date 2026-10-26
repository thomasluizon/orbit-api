using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat;

public sealed class HabitMonthModelPayloadTests
{
    private static readonly JsonSerializerOptions ModelSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Theory]
    [InlineData(40, false, false, 31)]
    [InlineData(40, false, false, 1)]
    [InlineData(40, false, true, 31)]
    [InlineData(40, false, true, 1)]
    [InlineData(AppConstants.DefaultFreeMaxHabits, true, true, 31)]
    [InlineData(AppConstants.DefaultFreeMaxHabits, true, true, 1)]
    public async Task SupportedPortfoliosFitModelBudgetAndKeepCompleteCalendar(
        int rootCount, bool descendants, bool escapedTitles, int days)
    {
        var user = Guid.NewGuid();
        var from = new DateOnly(2026, 10, 1);
        var to = from.AddDays(days - 1);
        var habits = new List<Habit>();
        Habit Add(Guid? parent = null)
        {
            var prefix = $"Read {habits.Count:D6} ";
            var title = prefix + new string(escapedTitles ? '\u6f22' : 'a', AppConstants.MaxHabitTitleLength - prefix.Length);
            var created = Habit.Create(new(HabitSchedule.Recurring, user, title, FrequencyUnit.Day, 1, from,
                ParentHabitId: parent));
            created.IsSuccess.Should().BeTrue();
            var habit = created.Value;
            habit.Log(from, advanceDueDate: false).IsSuccess.Should().BeTrue();
            habits.Add(habit);
            return habit;
        }
        for (var index = 0; index < rootCount; index++)
        {
            var root = Add();
            if (!descendants) continue;
            for (var childIndex = 0; childIndex < AppConstants.MaxSubHabits; childIndex++)
                Add(root.Id);
            var parent = habits[^1];
            for (var depth = 2; depth < AppConstants.MaxHabitDepth; depth++)
                parent = Add(parent.Id);
        }
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user, Arg.Any<CancellationToken>()).Returns(to);
        var repository = Substitute.For<IGenericRepository<Habit>>();
        repository.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(habits.AsReadOnly());
        var calendar = (await new GetCalendarMonthQueryHandler(repository, dates, Substitute.For<IUnitOfWork>())
            .Handle(new(user, from, to), default)).Value;
        var card = new HabitMonthCard(from, to, to, 1, calendar);

        var payload = HabitMonthModelPayload.From(card);
        var json = JsonSerializer.Serialize(payload, ModelSerializerOptions);

        Console.WriteLine($"Roots={rootCount}, descendants={descendants}, escaped={escapedTitles}, days={days}: {json.Length} characters");
        json.Length.Should().BeLessThan(AppConstants.MaxAiToolPayloadJsonLength);
        payload.DateFrom.Should().Be(from);
        payload.DateTo.Should().Be(to);
        payload.Habits.Should().NotBeEmpty();
        payload.Habits.Count.Should().BeLessThanOrEqualTo(40);
        payload.OmittedHabits.Should().Be(habits.Count - payload.Habits.Count);
        payload.Instruction.Should().Contain("Do not infer facts about omitted habits")
            .And.Contain("retry with narrower dates").And.Contain("complete habits and daily data");
        payload.Habits.Should().OnlyContain(row => row.Title.Length <= 64);
        if (escapedTitles) payload.OmittedHabits.Should().BePositive();
        payload.Habits.Should().OnlyContain(row => row.Scheduled == days && row.Completed == 1);
        card.Calendar.Should().BeSameAs(calendar);
        card.Calendar.Habits.Should().HaveCount(rootCount);
        card.Calendar.Logs.Should().HaveCount(habits.Count);
        var allRows = card.Calendar.Habits.SelectMany(root => Titles(root.Title, root.ScheduledDates, root.Children)).ToArray();
        allRows.Should().HaveCount(habits.Count);
        allRows.Should().OnlyContain(row => row.Title.Length == AppConstants.MaxHabitTitleLength && row.Days == days);
    }

    private static IEnumerable<(string Title, int Days)> Titles(string title, IReadOnlyList<DateOnly> dates,
        IReadOnlyList<HabitScheduleChildItem> children)
    {
        yield return (title, dates.Count);
        foreach (var child in children)
            foreach (var row in Titles(child.Title, child.ScheduledDates, child.Children))
                yield return row;
    }

    [Fact]
    public void EmptyCalendarRetainsTheWholeWindow()
    {
        var from = new DateOnly(2026, 10, 1);
        var to = new DateOnly(2026, 10, 31);
        var card = new HabitMonthCard(from, to, from.AddDays(25), 1, new([], []));

        var payload = HabitMonthModelPayload.From(card);

        payload.DateFrom.Should().Be(from);
        payload.DateTo.Should().Be(to);
        payload.Today.Should().Be(card.Today);
        payload.Habits.Should().BeEmpty();
        payload.OmittedHabits.Should().Be(0);
    }

    [Fact]
    public async Task CountsCalendarDaysWithoutTreatingSkipsAsCompletionsOrFutureDatesAsMissed()
    {
        var user = Guid.NewGuid();
        var from = new DateOnly(2026, 10, 1);
        var to = new DateOnly(2026, 10, 31);
        var today = from.AddDays(25);
        var habit = Habit.Create(new(HabitSchedule.Flexible, user, "Read", FrequencyUnit.Month, 50, from)).Value;
        habit.Log(from.AddDays(-1), advanceDueDate: false).IsSuccess.Should().BeTrue();
        habit.Log(from, advanceDueDate: false).IsSuccess.Should().BeTrue();
        habit.Log(from, advanceDueDate: false).IsSuccess.Should().BeTrue();
        habit.Log(from.AddDays(27), advanceDueDate: false).IsSuccess.Should().BeTrue();
        habit.SkipFlexible(from).IsSuccess.Should().BeTrue();
        habit.SkipFlexible(from.AddDays(1)).IsSuccess.Should().BeTrue();
        habit.SkipFlexible(from.AddDays(1)).IsSuccess.Should().BeTrue();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user, Arg.Any<CancellationToken>()).Returns(today);
        var repository = Substitute.For<IGenericRepository<Habit>>();
        repository.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());
        var calendar = (await new GetCalendarMonthQueryHandler(repository, dates, Substitute.For<IUnitOfWork>())
            .Handle(new(user, from, to), default)).Value;

        var payload = HabitMonthModelPayload.From(new(from, to, today, 1, calendar));

        var row = payload.Habits.Should().ContainSingle().Subject;
        row.Id.Should().Be(habit.Id);
        row.Title.Should().Be(habit.Title);
        row.Scheduled.Should().Be(31);
        row.Completed.Should().Be(2);
        row.Skipped.Should().Be(1);
        row.Missed.Should().Be(23);
        row.IsFlexible.Should().BeTrue();
        calendar.Logs[habit.Id].Should().HaveCount(6);
    }
}
