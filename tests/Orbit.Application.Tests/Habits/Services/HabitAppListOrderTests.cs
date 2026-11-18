using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Habits.Services;

public class HabitAppListOrderTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateTime Start = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

    private static Habit Create(string title, int? position, int minutes, Guid? parent = null) =>
        Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId, title, null, null,
            DueDate: new DateOnly(2026, 11, 1), ParentHabitId: parent, Position: position,
            CreatedAtUtc: Start.AddMinutes(minutes))).Value;

    [Fact]
    public void Order_IsDepthFirstAndEachLevelByPositionThenCreationThenId()
    {
        var evening = Create("Evening", 2, 0);
        var morning = Create("Morning", 1, 5);
        var loose = Create("Loose", null, 1);
        var late = Create("Water", null, 9, morning.Id);
        var second = Create("Water", 2, 3, morning.Id);
        var first = Create("Water", 1, 4, morning.Id);
        var night = Create("Water", null, 2, evening.Id);

        HabitAppListOrder.Order([night, loose, late, evening, first, second, morning])
            .Should().Equal(morning, first, second, late, evening, night, loose);
    }

    [Fact]
    public void Order_BreaksAnEqualPositionAndCreationTimeById()
    {
        var a = Create("Walk", 1, 0);
        var b = Create("Walk", 1, 0);
        var expected = new[] { a, b }.OrderBy(habit => habit.Id).ToArray();

        HabitAppListOrder.Order([b, a]).Should().Equal(expected);
    }

    [Fact]
    public void Order_StartsABranchForARecordWhoseParentIsNotInTheSet()
    {
        var parent = Create("Parent", 1, 0);
        var orphan = Create("Child", 1, 1, parent.Id);
        var root = Create("Root", 2, 2);

        HabitAppListOrder.Order([root, orphan]).Should().Equal(orphan, root);
    }

    [Fact]
    public void SameTitleGroup_KeepsTheAppListOrderAndIgnoresCaseAndAccents()
    {
        var morning = Create("Morning", 1, 0);
        var evening = Create("Evening", 2, 1);
        var eveningWater = Create("ÁGUA", 1, 2, evening.Id);
        var morningWater = Create("Água", 2, 3, morning.Id);
        var other = Create("Walk", 1, 4, morning.Id);

        HabitAppListOrder.SameTitleGroup(eveningWater, [eveningWater, other, morningWater, evening, morning], HabitNameMatcher.TitleKey)
            .Should().Equal(morningWater, eveningWater);
    }
}
