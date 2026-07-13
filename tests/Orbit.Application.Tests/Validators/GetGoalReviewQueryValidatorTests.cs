using FluentValidation.TestHelper;
using Orbit.Application.Goals.Queries;
using Orbit.Application.Goals.Validators;

namespace Orbit.Application.Tests.Validators;

public class GetGoalReviewQueryValidatorTests
{
    private readonly GetGoalReviewQueryValidator _validator = new();

    private static GetGoalReviewQuery ValidQuery() => new(
        UserId: Guid.NewGuid(),
        Language: "pt-BR");

    [Fact]
    public void Validate_ValidQuery_NoErrors()
    {
        var result = _validator.TestValidate(ValidQuery());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var query = ValidQuery() with { UserId = Guid.Empty };

        var result = _validator.TestValidate(query);

        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Validate_UnsupportedLanguage_HasError()
    {
        var query = ValidQuery() with { Language = "xx" };

        var result = _validator.TestValidate(query);

        result.ShouldHaveValidationErrorFor(x => x.Language);
    }

    [Fact]
    public void Validate_EmptyLanguage_NoLanguageError()
    {
        var query = ValidQuery() with { Language = string.Empty };

        var result = _validator.TestValidate(query);

        result.ShouldNotHaveValidationErrorFor(x => x.Language);
    }
}
