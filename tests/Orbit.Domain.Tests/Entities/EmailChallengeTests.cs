using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class EmailChallengeTests
{
    [Fact]
    public void Create_RejectsInvalidKeysHashLifetimeAndInstant()
    {
        var nowAtUtc = DateTime.UtcNow;
        Action emptyOperation = () => EmailChallenge.Create("", "user@example.com", new byte[32], nowAtUtc, TimeSpan.FromMinutes(5));
        Action longSubject = () => EmailChallenge.Create("SignIn", new string('a', 321), new byte[32], nowAtUtc, TimeSpan.FromMinutes(5));
        Action rawCode = () => EmailChallenge.Create("SignIn", "user@example.com", "123456"u8.ToArray(), nowAtUtc, TimeSpan.FromMinutes(5));
        Action expiredLifetime = () => EmailChallenge.Create("SignIn", "user@example.com", new byte[32], nowAtUtc, TimeSpan.Zero);
        Action localInstant = () => EmailChallenge.Create("SignIn", "user@example.com", new byte[32], DateTime.SpecifyKind(nowAtUtc, DateTimeKind.Unspecified), TimeSpan.FromMinutes(5));

        emptyOperation.Should().Throw<ArgumentException>();
        longSubject.Should().Throw<ArgumentException>();
        rawCode.Should().Throw<ArgumentException>();
        expiredLifetime.Should().Throw<ArgumentException>();
        localInstant.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Reissue_PreservesAttemptsUntilTheWindowEnds()
    {
        var nowAtUtc = DateTime.UtcNow;
        var challenge = EmailChallenge.Create("SignIn", "user@example.com", new byte[32], nowAtUtc, TimeSpan.FromMinutes(5));
        for (var i = 0; i < 10; i++)
            challenge.RecordFailedAttempt();
        challenge.Consume();
        challenge.Issue(new byte[32], nowAtUtc.AddMinutes(1), TimeSpan.FromMinutes(5));

        challenge.Attempts.Should().Be(3);
        challenge.Consumed.Should().BeFalse();
        challenge.Issue(new byte[32], nowAtUtc.AddMinutes(15), TimeSpan.FromMinutes(5));
        challenge.Attempts.Should().Be(0);
    }

    [Fact]
    public void Hash_IsCopiedAndCompared()
    {
        var hash = new byte[32];
        var challenge = EmailChallenge.Create("SignIn", "user@example.com", hash, DateTime.UtcNow, TimeSpan.FromMinutes(5));
        hash[0] = 1;

        challenge.Matches(new byte[32]).Should().BeTrue();
        challenge.Matches(hash).Should().BeFalse();
    }
}
