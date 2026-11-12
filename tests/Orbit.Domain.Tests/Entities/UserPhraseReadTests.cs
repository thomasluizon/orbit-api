using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class UserPhraseReadTests
{
    private static readonly DateOnly Today = new(2026, 11, 12);

    private static User NewUser() => User.Create("Test User", "test@example.com").Value;

    [Fact]
    public void TryReservePhraseRead_PassesUntilTheCapThenRefuses()
    {
        var user = NewUser();

        for (var read = 0; read < 30; read++)
            user.TryReservePhraseRead(Today, 30).Should().BeTrue();

        user.TryReservePhraseRead(Today, 30).Should().BeFalse();
        user.PhraseReadsToday.Should().Be(30);
    }

    [Fact]
    public void TryReservePhraseRead_StartsAtZeroWhenTheAccountDayChanges()
    {
        var user = NewUser();
        for (var read = 0; read < 30; read++) user.TryReservePhraseRead(Today, 30);

        user.TryReservePhraseRead(Today.AddDays(1), 30).Should().BeTrue();

        user.PhraseReadsToday.Should().Be(1);
        user.PhraseReadsLocalDate.Should().Be(Today.AddDays(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TryReservePhraseRead_RefusesWhenTheCapIsBelowOne(int cap)
    {
        var user = NewUser();

        user.TryReservePhraseRead(Today, cap).Should().BeFalse();
        user.PhraseReadsToday.Should().Be(0);
    }

    [Fact]
    public void MascotMotion_DefaultsToOnAndStoresTheChoice()
    {
        var user = NewUser();
        user.MascotMotionEnabled.Should().BeTrue();

        user.SetMascotMotion(false);
        user.MascotMotionEnabled.Should().BeFalse();

        user.SetMascotMotion(true);
        user.MascotMotionEnabled.Should().BeTrue();
    }
}
