using FluentAssertions;
using Orbit.Application.Waitlist.Commands;
using Orbit.Application.Waitlist.Validators;

namespace Orbit.Application.Tests.Validators;

public class ConfirmWaitlistCommandValidatorTests
{
    private readonly ConfirmWaitlistCommandValidator _validator = new();

    [Fact]
    public void EmptyToken_IsRejected()
    {
        _validator.Validate(new ConfirmWaitlistCommand("")).IsValid.Should().BeFalse();
        _validator.Validate(new ConfirmWaitlistCommand("signed-token")).IsValid.Should().BeTrue();
    }
}
