using FluentAssertions;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;

namespace Orbit.Application.Tests.Validators;

public class BulkCreateHabitsTreeLimitTests
{
    private static BulkHabitItem Node(IReadOnlyList<BulkHabitItem>? children = null) => new("Read", null, null, null, SubHabits: children);

    [Fact]
    public void TwentyOneChildrenUnderNestedNode_IsRejected()
    {
        var root = Node([Node(Enumerable.Range(0, 21).Select(_ => Node()).ToArray())]);
        new BulkCreateHabitsCommandValidator().Validate(new BulkCreateHabitsCommand(Guid.NewGuid(), [root])).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void TotalAcrossThreeRoots_IncludesEveryDescendant(int count, bool valid)
    {
        var roots = new List<BulkHabitItem>();
        var remaining = count - 3;
        for (var root = 0; root < 3; root++)
        {
            var children = new List<BulkHabitItem>();
            while (remaining > 0 && children.Count < 2)
            {
                var leaves = Math.Min(remaining - 1, 20);
                children.Add(Node(Enumerable.Range(0, leaves).Select(_ => Node()).ToArray()));
                remaining -= 1 + leaves;
            }
            roots.Add(Node(children));
        }
        new BulkCreateHabitsCommandValidator().Validate(new BulkCreateHabitsCommand(Guid.NewGuid(), roots)).IsValid.Should().Be(valid);
    }
}
