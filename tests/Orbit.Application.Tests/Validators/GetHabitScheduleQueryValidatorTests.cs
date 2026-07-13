using FluentAssertions;
using FluentValidation.TestHelper;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Validators;

namespace Orbit.Application.Tests.Validators;

public class GetHabitScheduleQueryValidatorTests
{
    private readonly GetHabitScheduleQueryValidator _validator = new();

    private static GetHabitScheduleQuery ValidQuery() => new(
        UserId: Guid.NewGuid(),
        DateFrom: new DateOnly(2025, 1, 1),
        DateTo: new DateOnly(2025, 1, 31));

    [Fact]
    public void Validate_ValidRange_NoErrors()
    {
        var query = ValidQuery();

        var result = _validator.TestValidate(query);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_DateToBeforeDateFrom_HasError()
    {
        var query = ValidQuery() with
        {
            DateFrom = new DateOnly(2025, 2, 1),
            DateTo = new DateOnly(2025, 1, 1)
        };

        var result = _validator.TestValidate(query);

        result.ShouldHaveValidationErrorFor(x => x.DateTo);
    }

    [Fact]
    public void Validate_RangeOver366Days_HasError()
    {
        var query = ValidQuery() with
        {
            DateFrom = new DateOnly(2025, 1, 1),
            DateTo = new DateOnly(2026, 1, 3)        };

        var result = _validator.TestValidate(query);

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("366"));
    }

    [Fact]
    public void Validate_PageLessThan1_HasError()
    {
        var query = ValidQuery() with { Page = 0 };

        var result = _validator.TestValidate(query);

        result.ShouldHaveValidationErrorFor(x => x.Page);
    }

    [Fact]
    public void Validate_PageSizeOver200_HasError()
    {
        var query = ValidQuery() with { PageSize = 201 };

        var result = _validator.TestValidate(query);

        result.ShouldHaveValidationErrorFor(x => x.PageSize);
    }

    [Fact]
    public void Validate_PageSize0_HasError()
    {
        var query = ValidQuery() with { PageSize = 0 };

        var result = _validator.TestValidate(query);

        result.ShouldHaveValidationErrorFor(x => x.PageSize);
    }

    [Fact]
    public void Validate_NegativePage_ReportsGreaterThanOrEqualFailure()
    {
        var query = ValidQuery() with { Page = -5 };

        var result = _validator.TestValidate(query);

        var failure = result.Errors.Should()
            .ContainSingle(e => e.PropertyName == nameof(GetHabitScheduleQuery.Page)).Subject;
        failure.ErrorCode.Should().Be("GreaterThanOrEqualValidator");
        failure.AttemptedValue.Should().Be(-5);
    }

    [Fact]
    public void Validate_PageSizeAboveMax_ReportsInclusiveBetweenFailureWithConfiguredMax()
    {
        var overMax = AppConstants.MaxPageSize + 1;
        var query = ValidQuery() with { PageSize = overMax };

        var result = _validator.TestValidate(query);

        var failure = result.Errors.Should()
            .ContainSingle(e => e.PropertyName == nameof(GetHabitScheduleQuery.PageSize)).Subject;
        failure.ErrorCode.Should().Be("InclusiveBetweenValidator");
        failure.AttemptedValue.Should().Be(overMax);
    }

    [Fact]
    public void Validate_PageSizeZero_ReportsInclusiveBetweenFailure()
    {
        var query = ValidQuery() with { PageSize = 0 };

        var result = _validator.TestValidate(query);

        var failure = result.Errors.Should()
            .ContainSingle(e => e.PropertyName == nameof(GetHabitScheduleQuery.PageSize)).Subject;
        failure.ErrorCode.Should().Be("InclusiveBetweenValidator");
        failure.AttemptedValue.Should().Be(0);
    }

    [Fact]
    public void Validate_PageSizeAtMax_NoError()
    {
        var query = ValidQuery() with { PageSize = AppConstants.MaxPageSize };

        var result = _validator.TestValidate(query);

        result.ShouldNotHaveValidationErrorFor(x => x.PageSize);
    }

    [Fact]
    public void Validate_PageAtMinimum_NoError()
    {
        var query = ValidQuery() with { Page = 1 };

        var result = _validator.TestValidate(query);

        result.ShouldNotHaveValidationErrorFor(x => x.Page);
    }
}
