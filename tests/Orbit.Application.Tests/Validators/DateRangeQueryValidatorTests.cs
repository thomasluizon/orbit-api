using FluentAssertions;
using FluentValidation.TestHelper;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Goals.Queries;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Tests.Validators;

public class DateRangeQueryValidatorTests
{
    private static readonly DateOnly Start = new(2026, 1, 1);

    [Fact]
    public void GetStreakHistory_EqualDates_IsValid()
    {
        var validator = new GetStreakHistoryQueryValidator();

        var result = validator.TestValidate(new GetStreakHistoryQuery(Guid.NewGuid(), Start, Start));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void GetStreakHistory_InvertedRange_HasDateFromError()
    {
        var validator = new GetStreakHistoryQueryValidator();

        var result = validator.TestValidate(new GetStreakHistoryQuery(Guid.NewGuid(), Start.AddDays(5), Start));

        result.ShouldHaveValidationErrorFor(x => x.DateFrom);
    }

    [Fact]
    public void GetStreakHistory_RangeExceedingCap_IsInvalid()
    {
        var validator = new GetStreakHistoryQueryValidator();

        var result = validator.TestValidate(
            new GetStreakHistoryQuery(Guid.NewGuid(), Start, Start.AddDays(AppConstants.MaxRangeDays + 1)));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void GetStreakHistory_RangeAtCap_IsValid()
    {
        var validator = new GetStreakHistoryQueryValidator();

        var result = validator.TestValidate(
            new GetStreakHistoryQuery(Guid.NewGuid(), Start, Start.AddDays(AppConstants.MaxRangeDays)));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void GetXpHistory_EqualDates_IsValid()
    {
        var validator = new GetXpHistoryQueryValidator();

        var result = validator.TestValidate(new GetXpHistoryQuery(Guid.NewGuid(), Start, Start));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void GetXpHistory_InvertedRange_HasDateFromError()
    {
        var validator = new GetXpHistoryQueryValidator();

        var result = validator.TestValidate(new GetXpHistoryQuery(Guid.NewGuid(), Start.AddDays(1), Start));

        result.ShouldHaveValidationErrorFor(x => x.DateFrom);
    }

    [Fact]
    public void GetXpHistory_RangeExceedingCap_IsInvalid()
    {
        var validator = new GetXpHistoryQueryValidator();

        var result = validator.TestValidate(
            new GetXpHistoryQuery(Guid.NewGuid(), Start, Start.AddDays(AppConstants.MaxRangeDays + 1)));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void GetGoalProgressHistory_EqualDates_IsValid()
    {
        var validator = new GetGoalProgressHistoryQueryValidator();

        var result = validator.TestValidate(
            new GetGoalProgressHistoryQuery(Guid.NewGuid(), Guid.NewGuid(), Start, Start));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void GetGoalProgressHistory_InvertedRange_HasDateFromError()
    {
        var validator = new GetGoalProgressHistoryQueryValidator();

        var result = validator.TestValidate(
            new GetGoalProgressHistoryQuery(Guid.NewGuid(), Guid.NewGuid(), Start.AddDays(10), Start));

        result.ShouldHaveValidationErrorFor(x => x.DateFrom);
    }

    [Fact]
    public void GetGoalProgressHistory_RangeExceedingCap_IsInvalid()
    {
        var validator = new GetGoalProgressHistoryQueryValidator();

        var result = validator.TestValidate(
            new GetGoalProgressHistoryQuery(
                Guid.NewGuid(), Guid.NewGuid(), Start, Start.AddDays(AppConstants.MaxRangeDays + 1)));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void GetHabitsCompletionTrends_EqualDates_IsValid()
    {
        var validator = new GetHabitsCompletionTrendsQueryValidator();

        var result = validator.TestValidate(new GetHabitsCompletionTrendsQuery(Guid.NewGuid(), Start, Start));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void GetHabitsCompletionTrends_InvertedRange_HasDateFromError()
    {
        var validator = new GetHabitsCompletionTrendsQueryValidator();

        var result = validator.TestValidate(
            new GetHabitsCompletionTrendsQuery(Guid.NewGuid(), Start.AddDays(2), Start));

        result.ShouldHaveValidationErrorFor(x => x.DateFrom);
    }

    [Fact]
    public void GetHabitsCompletionTrends_RangeExceedingCap_IsInvalid()
    {
        var validator = new GetHabitsCompletionTrendsQueryValidator();

        var result = validator.TestValidate(
            new GetHabitsCompletionTrendsQuery(Guid.NewGuid(), Start, Start.AddDays(AppConstants.MaxRangeDays + 1)));

        result.IsValid.Should().BeFalse();
    }
}
