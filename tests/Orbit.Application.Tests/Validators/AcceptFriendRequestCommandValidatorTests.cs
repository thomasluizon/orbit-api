using FluentValidation.TestHelper;
using Orbit.Application.Social.Commands;
using Orbit.Application.Social.Validators;

namespace Orbit.Application.Tests.Validators;

public class AcceptFriendRequestCommandValidatorTests
{
    private readonly AcceptFriendRequestCommandValidator _validator = new();

    [Fact]
    public void Valid_NoErrors() =>
        _validator.TestValidate(new AcceptFriendRequestCommand(Guid.NewGuid(), Guid.NewGuid()))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void EmptyUserId_HasError() =>
        _validator.TestValidate(new AcceptFriendRequestCommand(Guid.Empty, Guid.NewGuid()))
            .ShouldHaveValidationErrorFor(x => x.UserId);

    [Fact]
    public void EmptyFriendshipId_HasError() =>
        _validator.TestValidate(new AcceptFriendRequestCommand(Guid.NewGuid(), Guid.Empty))
            .ShouldHaveValidationErrorFor(x => x.FriendshipId);
}
