using FluentValidation.TestHelper;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;

namespace Orbit.Application.Tests.Validators;

public class SuggestHabitSetupCommandValidatorTests
{
    private readonly SuggestHabitSetupCommandValidator _validator = new();

    private static SuggestHabitSetupCommand ValidCommand() =>
        new(Guid.NewGuid(), "Read a book", "en");

    [Fact]
    public void Validate_Valid_NoErrors()
    {
        var result = _validator.TestValidate(ValidCommand());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyTitle_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Title = "" });

        result.ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void Validate_TitleOverMaxLength_HasError()
    {
        var result = _validator.TestValidate(
            ValidCommand() with { Title = new string('a', AppConstants.MaxHabitTitleLength + 1) });

        result.ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void Validate_EmptyLanguage_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Language = "" });

        result.ShouldHaveValidationErrorFor(x => x.Language);
    }

    [Fact]
    public void Validate_UnsupportedLanguage_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Language = "xx" });

        result.ShouldHaveValidationErrorFor(x => x.Language);
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { UserId = Guid.Empty });

        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }
}
