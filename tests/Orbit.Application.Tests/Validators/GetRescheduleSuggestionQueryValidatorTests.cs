using FluentValidation.TestHelper;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Validators;

namespace Orbit.Application.Tests.Validators;

public class GetRescheduleSuggestionQueryValidatorTests
{
    private readonly GetRescheduleSuggestionQueryValidator _validator = new();

    private static GetRescheduleSuggestionQuery ValidQuery() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "en");

    [Fact]
    public void Validate_Valid_NoErrors()
    {
        var result = _validator.TestValidate(ValidQuery());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyLanguage_NoLanguageError()
    {
        var result = _validator.TestValidate(ValidQuery() with { Language = "" });

        result.ShouldNotHaveValidationErrorFor(x => x.Language);
    }

    [Fact]
    public void Validate_UnsupportedLanguage_HasError()
    {
        var result = _validator.TestValidate(ValidQuery() with { Language = "xx" });

        result.ShouldHaveValidationErrorFor(x => x.Language);
    }

    [Fact]
    public void Validate_EmptyHabitId_HasError()
    {
        var result = _validator.TestValidate(ValidQuery() with { HabitId = Guid.Empty });

        result.ShouldHaveValidationErrorFor(x => x.HabitId);
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var result = _validator.TestValidate(ValidQuery() with { UserId = Guid.Empty });

        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }
}
