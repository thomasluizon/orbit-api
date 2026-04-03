using FluentValidation.TestHelper;
using Orbit.Application.Common;
using Orbit.Application.Tags.Commands;
using Orbit.Application.Tags.Validators;

namespace Orbit.Application.Tests.Validators;

public class AssignTagsCommandValidatorTests
{
    private readonly AssignTagsCommandValidator _validator = new();

    private static AssignTagsCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        HabitId: Guid.NewGuid(),
        TagIds: [Guid.NewGuid()]);

    [Fact]
    public void Validate_ValidCommand_NoErrors()
    {
        var result = _validator.TestValidate(ValidCommand());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { UserId = Guid.Empty });
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Validate_EmptyHabitId_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { HabitId = Guid.Empty });
        result.ShouldHaveValidationErrorFor(x => x.HabitId);
    }

    [Fact]
    public void Validate_NullTagIds_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { TagIds = null! });
        result.ShouldHaveValidationErrorFor(x => x.TagIds);
    }

    [Fact]
    public void Validate_TooManyTags_HasError()
    {
        var tags = Enumerable.Range(0, AppConstants.MaxTagsPerHabit + 1)
            .Select(_ => Guid.NewGuid()).ToList();

        var result = _validator.TestValidate(ValidCommand() with { TagIds = tags });
        result.ShouldHaveValidationErrorFor(x => x.TagIds);
    }

    [Fact]
    public void Validate_ExactlyMaxTags_NoError()
    {
        var tags = Enumerable.Range(0, AppConstants.MaxTagsPerHabit)
            .Select(_ => Guid.NewGuid()).ToList();

        var result = _validator.TestValidate(ValidCommand() with { TagIds = tags });
        result.ShouldNotHaveValidationErrorFor(x => x.TagIds);
    }

    [Fact]
    public void Validate_EmptyTagIds_NoError()
    {
        var result = _validator.TestValidate(ValidCommand() with { TagIds = [] });
        result.ShouldNotHaveAnyValidationErrors();
    }
}
