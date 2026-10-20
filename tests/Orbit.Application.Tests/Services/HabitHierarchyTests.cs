using FluentAssertions;
using Orbit.Application.Habits.Services;

namespace Orbit.Application.Tests.Services;

public class HabitHierarchyTests
{
    [Fact]
    public void Helpers_MeasureAncestorsAndDeepestDescendant()
    {
        var root = Guid.NewGuid();
        var child = Guid.NewGuid();
        var leaf = Guid.NewGuid();
        var sibling = Guid.NewGuid();
        var parents = new Dictionary<Guid, Guid?>
        {
            [root] = null,
            [child] = root,
            [leaf] = child,
            [sibling] = root
        };

        HabitHierarchy.GetDepth(root, parents).Should().Be(0);
        HabitHierarchy.GetDepth(leaf, parents).Should().Be(2);
        HabitHierarchy.GetSubtreeHeight(root, parents).Should().Be(2);
        HabitHierarchy.GetSubtreeHeight(sibling, parents).Should().Be(0);
        HabitHierarchy.WouldCreateCycle(root, leaf, parents).Should().BeTrue();
        HabitHierarchy.WouldCreateCycle(child, child, parents).Should().BeTrue();
        HabitHierarchy.WouldCreateCycle(leaf, sibling, parents).Should().BeFalse();
    }

    [Fact]
    public void Helpers_UseProjectedParents()
    {
        var ancestor = Guid.NewGuid();
        var child = Guid.NewGuid();
        var target = Guid.NewGuid();
        var parents = new Dictionary<Guid, Guid?>
        {
            [ancestor] = null,
            [child] = ancestor,
            [target] = child
        };

        HabitHierarchy.WouldCreateCycle(ancestor, target, parents).Should().BeTrue();
        parents[child] = null;
        HabitHierarchy.WouldCreateCycle(ancestor, target, parents).Should().BeFalse();
        HabitHierarchy.GetSubtreeHeight(ancestor, parents).Should().Be(0);
    }
}
