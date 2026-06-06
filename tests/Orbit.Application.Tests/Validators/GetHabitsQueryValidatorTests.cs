using FluentValidation.TestHelper;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Validators;

namespace Orbit.Application.Tests.Validators;

public class GetHabitsQueryValidatorTests
{
    private readonly GetHabitsQueryValidator _validator = new();

    private static GetHabitsQuery ValidQuery() => new(
        UserId: Guid.NewGuid());

    [Fact]
    public void Validate_Valid_NoErrors()
    {
        var query = ValidQuery();

        var result = _validator.TestValidate(query);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_SearchOver200Chars_HasError()
    {
        var query = ValidQuery() with { Search = new string('s', 201) };

        var result = _validator.TestValidate(query);

        result.ShouldHaveValidationErrorFor(x => x.Search);
    }

    [Fact]
    public void Validate_InvalidFrequencyUnit_HasError()
    {
        var query = ValidQuery() with { FrequencyUnitFilter = "Invalid" };

        var result = _validator.TestValidate(query);

        result.ShouldHaveValidationErrorFor(x => x.FrequencyUnitFilter);
    }

    [Theory]
    [InlineData("Day")]
    [InlineData("Week")]
    [InlineData("Month")]
    [InlineData("Year")]
    [InlineData("none")]
    public void Validate_ValidFrequencyUnits_NoErrors(string unit)
    {
        var query = ValidQuery() with { FrequencyUnitFilter = unit };

        var result = _validator.TestValidate(query);

        result.ShouldNotHaveValidationErrorFor(x => x.FrequencyUnitFilter);
    }

    [Fact]
    public void Validate_DateToBeforeDateFrom_HasError()
    {
        var query = ValidQuery() with
        {
            DueDateFrom = new DateOnly(2025, 3, 1),
            DueDateTo = new DateOnly(2025, 2, 1)
        };

        var result = _validator.TestValidate(query);

        result.ShouldHaveValidationErrorFor(x => x.DueDateTo);
    }
}
