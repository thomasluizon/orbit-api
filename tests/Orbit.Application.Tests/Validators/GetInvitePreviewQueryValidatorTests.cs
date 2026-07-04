using FluentValidation.TestHelper;
using Orbit.Application.Social.Queries;
using Orbit.Application.Social.Validators;

namespace Orbit.Application.Tests.Validators;

public class GetInvitePreviewQueryValidatorTests
{
    private readonly GetInvitePreviewQueryValidator _validator = new();

    [Fact]
    public void Validate_ValidQuery_NoErrors()
    {
        var result = _validator.TestValidate(new GetInvitePreviewQuery(Guid.NewGuid(), "ABCD2345"));
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var result = _validator.TestValidate(new GetInvitePreviewQuery(Guid.Empty, "ABCD2345"));
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Validate_EmptyCode_NoValidatorError()
    {
        var result = _validator.TestValidate(new GetInvitePreviewQuery(Guid.NewGuid(), string.Empty));
        result.ShouldNotHaveValidationErrorFor(x => x.ReferralCode);
    }
}
