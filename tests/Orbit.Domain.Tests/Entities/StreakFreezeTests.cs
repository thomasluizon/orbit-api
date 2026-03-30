using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class StreakFreezeTests
{
    [Fact]
    public void Create_SetsProperties()
    {
        var userId = Guid.NewGuid();
        var date = new DateOnly(2026, 3, 30);

        var freeze = StreakFreeze.Create(userId, date);

        freeze.UserId.Should().Be(userId);
        freeze.UsedOnDate.Should().Be(date);
        freeze.CreatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        freeze.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void Create_DifferentInstances_HaveDifferentIds()
    {
        var userId = Guid.NewGuid();
        var date = new DateOnly(2026, 3, 30);

        var freeze1 = StreakFreeze.Create(userId, date);
        var freeze2 = StreakFreeze.Create(userId, date);

        freeze1.Id.Should().NotBe(freeze2.Id);
    }
}
