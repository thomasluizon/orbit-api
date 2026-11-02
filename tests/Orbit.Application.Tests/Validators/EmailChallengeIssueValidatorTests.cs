using FluentAssertions;
using Orbit.Application.Auth.Services;
using Orbit.Application.Auth.Validators;

namespace Orbit.Application.Tests.Validators;

public class EmailChallengeIssueValidatorTests
{
    [Theory]
    [InlineData("user@example.com", null, true)]
    [InlineData("user@example.com", "123456", true)]
    [InlineData("", "123456", false)]
    [InlineData("invalid", "123456", false)]
    [InlineData("user@example.com", "abc123", false)]
    [InlineData("user@example.com", "12345", false)]
    public void Validate_ChecksEmailAndFixedCode(string email, string? code, bool valid)
    {
        new EmailChallengeIssueValidator().Validate(new EmailChallengeIssue(EmailChallengeOperation.SignIn, email, code))
            .IsValid.Should().Be(valid);
    }

    [Fact]
    public void Validate_RejectsUnknownOperation()
    {
        new EmailChallengeIssueValidator().Validate(new EmailChallengeIssue((EmailChallengeOperation)100, "user@example.com", null))
            .IsValid.Should().BeFalse();
    }
}
