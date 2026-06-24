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
        var overdue = CreateHabit("Floss", Today.AddDays(-2), position: 1);

        var card = HabitListCardBuilder.Build([dueToday, overdue], Today, HabitListCardBuilder.ScopeToday);

        card.Items.Single(item => item.Title == "Meditate").Status.Should().Be(HabitListCardBuilder.StatusToday);
        card.Items.Single(item => item.Title == "Floss").Status.Should().Be(HabitListCardBuilder.StatusOverdue);
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
