using FluentValidation.TestHelper;
using Orbit.Application.Tags.Queries;
using Orbit.Application.Tags.Validators;

namespace Orbit.Application.Tests.Validators;

public class SuggestTagsQueryValidatorTests
{
    private readonly SuggestTagsQueryValidator _validator = new();

    private static SuggestTagsQuery ValidQuery() =>
        new(Guid.NewGuid(), "Morning run", null, "en");

    [Fact]
    public void Validate_Valid_NoErrors()
    {
        var result = _validator.TestValidate(ValidQuery());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyTitle_HasError()
    {
        var result = _validator.TestValidate(ValidQuery() with { Title = "" });

        result.ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void Validate_UnsupportedLanguage_HasError()
    {
        var result = _validator.TestValidate(ValidQuery() with { Language = "xx" });

        result.ShouldHaveValidationErrorFor(x => x.Language);
    }

    [Fact]
    public void Validate_SupportedNonDefaultLanguage_NoLanguageError()
    {
        var result = _validator.TestValidate(ValidQuery() with { Language = "pt-BR" });

        result.ShouldNotHaveValidationErrorFor(x => x.Language);
    }
}
