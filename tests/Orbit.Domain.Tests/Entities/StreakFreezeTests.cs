using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class StreakFreezeTests
{
    [Fact]
    public void Create_ValidValues_SetsRepairDate()
    {
        var userId = Guid.NewGuid();
        var date = new DateOnly(2026, 8, 21);

        var freeze = StreakFreeze.Create(userId, date);

        freeze.UserId.Should().Be(userId);
        freeze.UsedOnDate.Should().Be(date);
        freeze.CreatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        freeze.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void Create_EmptyUserId_Throws()
    {
        var act = () => StreakFreeze.Create(Guid.Empty, new DateOnly(2026, 8, 21));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_MissingDate_Throws()
    {
        var act = () => StreakFreeze.Create(Guid.NewGuid(), DateOnly.MinValue);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_DifferentInstances_HaveDifferentIds()
    {
        var userId = Guid.NewGuid();
        var date = new DateOnly(2026, 8, 21);

        var first = StreakFreeze.Create(userId, date);
        var second = StreakFreeze.Create(userId, date);

        first.Id.Should().NotBe(second.Id);
    }
}
