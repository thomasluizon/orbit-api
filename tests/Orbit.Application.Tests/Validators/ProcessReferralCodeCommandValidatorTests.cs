using FluentValidation.TestHelper;
using Orbit.Application.Referrals.Commands;
using Orbit.Application.Referrals.Validators;

namespace Orbit.Application.Tests.Validators;

public class ProcessReferralCodeCommandValidatorTests
{
    private readonly ProcessReferralCodeCommandValidator _validator = new();

    private static ProcessReferralCodeCommand ValidCommand() => new(
        NewUserId: Guid.NewGuid(),
        ReferralCode: "ABC123");

    [Fact]
    public void Validate_ValidCommand_NoErrors()
    {
        var result = _validator.TestValidate(ValidCommand());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyNewUserId_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { NewUserId = Guid.Empty });
        result.ShouldHaveValidationErrorFor(x => x.NewUserId);
    }

    [Fact]
    public void Validate_EmptyReferralCode_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { ReferralCode = "" });
        result.ShouldHaveValidationErrorFor(x => x.ReferralCode);
    }

    [Fact]
    public void Validate_NullReferralCode_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { ReferralCode = null! });
        result.ShouldHaveValidationErrorFor(x => x.ReferralCode);
    }

    [Fact]
    public void Validate_ReferralCodeOver50Chars_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { ReferralCode = new string('X', 51) });
        result.ShouldHaveValidationErrorFor(x => x.ReferralCode);
    }

    [Fact]
    public void Validate_ReferralCodeExactly50Chars_NoError()
    {
        var result = _validator.TestValidate(ValidCommand() with { ReferralCode = new string('X', 50) });
        result.ShouldNotHaveValidationErrorFor(x => x.ReferralCode);
    }
}
