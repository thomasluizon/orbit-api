using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Chat;

public class HabitListCardBuilderTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static Habit CreateHabit(
        string title,
        DateOnly dueDate,
        int position = 0,
        bool isGeneral = false,
        bool isBadHabit = false,
        Guid? parentId = null,
        string? emoji = null)
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, title,
            isGeneral ? null : FrequencyUnit.Day,
            isGeneral ? null : 1,
            DueDate: dueDate,
            IsGeneral: isGeneral,
            IsBadHabit: isBadHabit,
            ParentHabitId: parentId,
            Emoji: emoji)).Value;
        habit.SetPosition(position);
        return habit;
    }

    [Fact]
    public void TryExtractScope_NoDirective_ReturnsFalse()
    {
        var found = HabitListCardBuilder.TryExtractScope("Here are your habits.", out _, out var stripped);

        found.Should().BeFalse();
        stripped.Should().Be("Here are your habits.");
    }

    [Fact]
    public void TryExtractScope_TodayDirective_ReturnsScopeAndStripsToken()
    {
        var found = HabitListCardBuilder.TryExtractScope(
            "Here are your habits for today:\n[[orbit:habits:today]]", out var scope, out var stripped);

        found.Should().BeTrue();
        scope.Should().Be(HabitListCardBuilder.ScopeToday);
        stripped.Should().Be("Here are your habits for today:");
    }

    [Fact]
    public void TryExtractScope_AllDirective_ReturnsScope()
    {
        var found = HabitListCardBuilder.TryExtractScope("All of them:\n[[ORBIT:HABITS:ALL]]", out var scope, out _);

        found.Should().BeTrue();
        scope.Should().Be(HabitListCardBuilder.ScopeAll);
    }

    [Fact]
    public void Build_TodayScope_IncludesDueTodayAndOverdue_ExcludesFutureAndGeneral()
    {
        var dueToday = CreateHabit("Meditate", Today, position: 0);
        var overdue = CreateHabit("Floss", Today.AddDays(-2), position: 1);
        var future = CreateHabit("Taxes", Today.AddDays(5), position: 2);
        var general = CreateHabit("Read", Today, position: 3, isGeneral: true);

        var card = HabitListCardBuilder.Build([dueToday, overdue, future, general], Today, HabitListCardBuilder.ScopeToday);

        var titles = card.Items.Select(item => item.Title).ToList();
        titles.Should().Contain("Meditate");
        titles.Should().Contain("Floss");
        titles.Should().NotContain("Taxes");
        titles.Should().NotContain("Read");
    }

    [Fact]
    public void Build_TodayScope_AssignsStatuses()
    {
        var dueToday = CreateHabit("Meditate", Today);
        var overdue = Habit.Create(new HabitCreateParams(
            UserId, "Floss", null, null, DueDate: Today.AddDays(-2))).Value;

        var card = HabitListCardBuilder.Build([dueToday, overdue], Today, HabitListCardBuilder.ScopeToday);

        card.Items.Single(item => item.Title == "Meditate").Status.Should().Be(HabitListCardBuilder.StatusToday);
        card.Items.Single(item => item.Title == "Floss").Status.Should().Be(HabitListCardBuilder.StatusOverdue);
    }

    [Fact]
    public void Build_TodayScope_KeepsLoggedDailyAndWeeklyHabitsDone()
    {
        var daily = CreateHabit("Water", Today);
        daily.Log(Today).IsSuccess.Should().BeTrue();
        var weekly = Habit.Create(new HabitCreateParams(
            UserId, "Walk", FrequencyUnit.Week, 1, DueDate: Today)).Value;
        weekly.Log(Today).IsSuccess.Should().BeTrue();
        var monthly = Habit.Create(new HabitCreateParams(
            UserId, "Budget", FrequencyUnit.Month, 1, DueDate: Today.AddDays(3))).Value;
        monthly.Log(Today).IsSuccess.Should().BeTrue();

        var habits = new[] { daily, weekly, monthly };
        var logDays = habits.SelectMany(habit => habit.Logs.Select(log => new Orbit.Domain.Interfaces.HabitScheduleLogDay(
            habit.Id, log.Date, log.Value > 0 ? 1 : 0, log.Value == 0 ? 1 : 0, true))).ToList();
        var facts = HabitTodaySnapshot.Build(habits, Today, 1, logDays, new HashSet<Guid>());
        var card = HabitListCardBuilder.Build(habits, Today, HabitListCardBuilder.ScopeToday, facts, supportsDoneStatus: true);

        card.Items.Select(item => item.Title).Should().BeEquivalentTo(["Water", "Walk"]);
        card.Items.Should().OnlyContain(item => item.Status == "done");
    }

    [Theory]
    [InlineData("today")]
    [InlineData("all")]
    public void Build_LoggedHabitWithoutDoneCapability_KeepsLegacyCardShape(string scope)
    {
        var logged = CreateHabit("Water", Today);
        logged.Log(Today).IsSuccess.Should().BeTrue();
        var due = CreateHabit("Meditate", Today);

        var card = HabitListCardBuilder.Build([logged, due], Today, scope);

        card.Items.Should().OnlyContain(item =>
            item.Status == HabitListCardBuilder.StatusToday || item.Status == HabitListCardBuilder.StatusNone);
        if (scope == HabitListCardBuilder.ScopeToday)
            card.Items.Select(item => item.Title).Should().Equal("Meditate");
        else
            card.Items.Single(item => item.Title == "Water").Status.Should().Be(HabitListCardBuilder.StatusNone);
    }

    [Fact]
    public void Build_AllScope_WithDoneCapability_MarksLoggedHabitDone()
    {
        var logged = CreateHabit("Water", Today);
        logged.Log(Today).IsSuccess.Should().BeTrue();

        var card = HabitListCardBuilder.Build(
            [logged], Today, HabitListCardBuilder.ScopeAll, supportsDoneStatus: true);

        card.Items.Should().ContainSingle().Which.Status.Should().Be(HabitListCardBuilder.StatusDone);
    }

    [Fact]
    public void Build_BadHabitSlipToday_DoesNotMarkCardOrPromptDone()
    {
        var bad = CreateHabit("Smoking", Today, isBadHabit: true);
        var slip = bad.Log(Today).Value;
        slip.IsSlip.Should().BeTrue();

        var card = HabitListCardBuilder.Build([bad], Today, HabitListCardBuilder.ScopeAll, supportsDoneStatus: true);
        var facts = HabitTodaySnapshot.FromLoadedHabits([bad], Today);

        card.Items.Single().Status.Should().NotBe(HabitListCardBuilder.StatusDone);
        facts.DoneTodayIds.Should().NotContain(bad.Id);
    }

    [Fact]
    public void Build_TodayScope_IncludesAncestorOfDueChild_WithDepth()
    {
        var parent = CreateHabit("Before Bed", Today.AddDays(10), position: 0);
        var dueChild = CreateHabit("Brush teeth", Today, position: 0, parentId: parent.Id);

        var card = HabitListCardBuilder.Build([parent, dueChild], Today, HabitListCardBuilder.ScopeToday);

        var parentItem = card.Items.Single(item => item.Title == "Before Bed");
        var childItem = card.Items.Single(item => item.Title == "Brush teeth");
        parentItem.Depth.Should().Be(0);
        childItem.Depth.Should().Be(1);
        card.Items.Should().HaveCount(2);
    }

    [Fact]
    public void Build_AllScope_IncludesEveryActiveHabit()
    {
        var dueToday = CreateHabit("Meditate", Today, position: 0);
        var future = CreateHabit("Taxes", Today.AddDays(5), position: 1);
        var general = CreateHabit("Read", Today, position: 2, isGeneral: true);

        var card = HabitListCardBuilder.Build([dueToday, future, general], Today, HabitListCardBuilder.ScopeAll);

        card.Items.Should().HaveCount(3);
        card.Items.Single(item => item.Title == "Read").Status.Should().Be(HabitListCardBuilder.StatusGeneral);
        card.Items.Single(item => item.Title == "Taxes").Status.Should().Be(HabitListCardBuilder.StatusNone);
    }

    [Fact]
    public void Build_PreservesPositionOrder()
    {
        var third = CreateHabit("Third", Today, position: 2);
        var first = CreateHabit("First", Today, position: 0);
        var second = CreateHabit("Second", Today, position: 1);

        var card = HabitListCardBuilder.Build([third, first, second], Today, HabitListCardBuilder.ScopeAll);

        card.Items.Select(item => item.Title).Should().ContainInOrder("First", "Second", "Third");
    }

    [Fact]
    public void Build_CarriesEmojiAndBadHabitFlag()
    {
        var bad = CreateHabit("Smoking", Today, isBadHabit: true, emoji: "🚬");

        var card = HabitListCardBuilder.Build([bad], Today, HabitListCardBuilder.ScopeAll);

        var item = card.Items.Single();
        item.Emoji.Should().Be("🚬");
        item.IsBadHabit.Should().BeTrue();
    }
}
