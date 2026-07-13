using FluentAssertions;
using FluentValidation.TestHelper;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Validators;

namespace Orbit.Application.Tests.Validators;

public class GetRetrospectiveQueryValidatorTests
{
    private readonly GetRetrospectiveQueryValidator _validator = new();

    private static GetRetrospectiveQuery ValidQuery() => new(
        UserId: Guid.NewGuid(),
        DateFrom: new DateOnly(2026, 4, 1),
        DateTo: new DateOnly(2026, 4, 30),
        Period: "month",
        Language: "en");

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
    public void Validate_UnknownPeriod_HasError()
    {
        var query = ValidQuery() with { Period = "decade" };

        var result = _validator.TestValidate(query);

        result.ShouldHaveValidationErrorFor(x => x.Period);
    }

    [Fact]
    public void Validate_DateFromAfterDateTo_HasError()
    {
        var query = ValidQuery() with
        {
            DateFrom = new DateOnly(2026, 5, 1),
            DateTo = new DateOnly(2026, 4, 1)
        };

        var result = _validator.TestValidate(query);

        result.ShouldHaveValidationErrorFor(x => x.DateFrom);
    }

    [Fact]
    public void Validate_RangeOver366Days_HasError()
    {
        var query = ValidQuery() with
        {
            DateFrom = new DateOnly(2026, 1, 1),
            DateTo = new DateOnly(2027, 1, 3)
        };

        var result = _validator.TestValidate(query);

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("366"));
    }

    [Fact]
    public void Validate_UnsupportedLanguage_HasError()
    {
        var query = ValidQuery() with { Language = "fr" };

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
