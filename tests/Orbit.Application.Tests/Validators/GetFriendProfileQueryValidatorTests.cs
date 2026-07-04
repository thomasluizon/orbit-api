using FluentValidation.TestHelper;
using Orbit.Application.Social.Queries;
using Orbit.Application.Social.Validators;

namespace Orbit.Application.Tests.Validators;

public class GetFriendProfileQueryValidatorTests
{
    private readonly GetFriendProfileQueryValidator _validator = new();

    [Fact]
    public void Validate_DistinctIds_NoErrors()
    {
        var result = _validator.TestValidate(new GetFriendProfileQuery(Guid.NewGuid(), Guid.NewGuid()));
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var result = _validator.TestValidate(new GetFriendProfileQuery(Guid.Empty, Guid.NewGuid()));
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Validate_EmptyFriendUserId_HasError()
    {
        var result = _validator.TestValidate(new GetFriendProfileQuery(Guid.NewGuid(), Guid.Empty));
        result.ShouldHaveValidationErrorFor(x => x.FriendUserId);
    }

    [Fact]
    public void Validate_FriendUserIdEqualsUserId_HasError()
    {
        var id = Guid.NewGuid();
        var result = _validator.TestValidate(new GetFriendProfileQuery(id, id));
        result.ShouldHaveValidationErrorFor(x => x.FriendUserId);
    }
}
