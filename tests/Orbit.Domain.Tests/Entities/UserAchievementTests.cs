using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class UserAchievementTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();

    [Fact]
    public void Create_ValidInput_SetsProperties()
    {
        var achievement = UserAchievement.Create(ValidUserId, "first_orbit");

        achievement.UserId.Should().Be(ValidUserId);
        achievement.AchievementId.Should().Be("first_orbit");
        achievement.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Create_SetsEarnedAtUtcToNow()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var achievement = UserAchievement.Create(ValidUserId, "liftoff");
        var after = DateTime.UtcNow.AddSeconds(1);

        achievement.EarnedAtUtc.Should().BeOnOrAfter(before);
        achievement.EarnedAtUtc.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void Create_DifferentAchievementIds_CreatesSeparateEntities()
    {
        var achievement1 = UserAchievement.Create(ValidUserId, "first_orbit");
        var achievement2 = UserAchievement.Create(ValidUserId, "liftoff");

        achievement1.AchievementId.Should().NotBe(achievement2.AchievementId);
        achievement1.Id.Should().NotBe(achievement2.Id);
    }
}
