using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class UserSignupTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("friend")]
    public void BeginSignup_PreservesOriginalInputs(string? referralCode)
    {
        var user = User.Create("Person", "person@example.com").Value;

        user.BeginSignup("pt-BR", referralCode);
        user.BeginSignup("en", "replacement");

        user.SignupLanguage.Should().Be("pt-BR");
        user.SignupReferralCode.Should().Be(referralCode);
        user.SignupCompletedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("language-too-long")]
    public void BeginSignup_RejectsInvalidLanguage(string language)
    {
        var user = User.Create("Person", "person@example.com").Value;

        var act = () => user.BeginSignup(language, null);

        act.Should().Throw<ArgumentException>();
        user.SignupLanguage.Should().BeNull();
    }

    [Fact]
    public void Completion_RequiresSignupAndAllThreeEffectsAndIsIdempotent()
    {
        var user = User.Create("Person", "person@example.com").Value;
        var referralBeforeSignup = () => user.CompleteSignupReferral();
        var analyticsBeforeSignup = () => user.CompleteSignupAnalytics();
        var welcomeBeforeSignup = () => user.CompleteSignupWelcome();
        referralBeforeSignup.Should().Throw<InvalidOperationException>();
        analyticsBeforeSignup.Should().Throw<InvalidOperationException>();
        welcomeBeforeSignup.Should().Throw<InvalidOperationException>();
        user.BeginSignup("en", null);

        user.CompleteSignupReferral();
        user.CompleteSignupAnalytics();
        user.SignupCompletedAtUtc.Should().BeNull();
        user.CompleteSignupWelcome();
        var completed = (user.SignupCompletedAtUtc, user.SignupReferralCompletedAtUtc,
            user.SignupAnalyticsCompletedAtUtc, user.SignupWelcomeCompletedAtUtc);

        user.CompleteSignupReferral();
        user.CompleteSignupAnalytics();
        user.CompleteSignupWelcome();

        user.SignupCompletedAtUtc.Should().NotBeNull();
        (user.SignupCompletedAtUtc, user.SignupReferralCompletedAtUtc,
            user.SignupAnalyticsCompletedAtUtc, user.SignupWelcomeCompletedAtUtc).Should().Be(completed);
    }
}
