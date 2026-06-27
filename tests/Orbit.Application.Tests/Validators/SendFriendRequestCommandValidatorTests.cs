using FluentAssertions;
using FluentValidation.TestHelper;
using Orbit.Application.Common;
using Orbit.Application.Social.Commands;
using Orbit.Application.Social.Validators;

namespace Orbit.Application.Tests.Validators;

public class SendFriendRequestCommandValidatorTests
{
    private readonly SendFriendRequestCommandValidator _validator = new();

    [Fact]
    public void Validate_HandleOnly_NoErrors()
    {
        var result = _validator.TestValidate(new SendFriendRequestCommand(Guid.NewGuid(), "alice", null));
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ReferralCodeOnly_NoErrors()
    {
        var result = _validator.TestValidate(new SendFriendRequestCommand(Guid.NewGuid(), null, "ABC123"));
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var result = _validator.TestValidate(new SendFriendRequestCommand(Guid.Empty, "alice", null));
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Validate_BothIdentifiers_HasError()
    {
        var result = _validator.TestValidate(new SendFriendRequestCommand(Guid.NewGuid(), "alice", "ABC123"));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_NeitherIdentifier_HasError()
    {
        var result = _validator.TestValidate(new SendFriendRequestCommand(Guid.NewGuid(), null, null));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_HandleOverMaxLength_HasError()
    {
        var handle = new string('a', AppConstants.HandleMaxLength + 1);
        var result = _validator.TestValidate(new SendFriendRequestCommand(Guid.NewGuid(), handle, null));
        result.ShouldHaveValidationErrorFor(x => x.Handle);
    }

    [Fact]
    public void Validate_HandleAtMaxLength_NoHandleError()
    {
        var handle = new string('a', AppConstants.HandleMaxLength);
        var result = _validator.TestValidate(new SendFriendRequestCommand(Guid.NewGuid(), handle, null));
        result.ShouldNotHaveValidationErrorFor(x => x.Handle);
    }

    [Fact]
    public void Validate_ReferralCodeOver64Chars_HasError()
    {
        var referralCode = new string('A', 65);
        var result = _validator.TestValidate(new SendFriendRequestCommand(Guid.NewGuid(), null, referralCode));
        result.ShouldHaveValidationErrorFor(x => x.ReferralCode);
    }

    [Fact]
    public void Validate_ReferralCodeExactly64Chars_NoReferralCodeError()
    {
        var referralCode = new string('A', 64);
        var result = _validator.TestValidate(new SendFriendRequestCommand(Guid.NewGuid(), null, referralCode));
        result.ShouldNotHaveValidationErrorFor(x => x.ReferralCode);
    }
}
