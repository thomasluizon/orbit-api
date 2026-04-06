using FluentValidation.TestHelper;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Application.Subscriptions.Validators;

namespace Orbit.Application.Tests.Validators;

public class CreateCheckoutCommandValidatorTests
{
    private readonly CreateCheckoutCommandValidator _validator = new();

    private static CreateCheckoutCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        Interval: "monthly",
        CountryCode: null,
        IpAddress: null);

    [Fact]
    public void Validate_ValidMonthly_NoErrors()
    {
        var result = _validator.TestValidate(ValidCommand());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ValidYearly_NoErrors()
    {
        var result = _validator.TestValidate(ValidCommand() with { Interval = "yearly" });
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { UserId = Guid.Empty });
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Validate_EmptyInterval_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Interval = "" });
        result.ShouldHaveValidationErrorFor(x => x.Interval);
    }

    [Fact]
    public void Validate_NullInterval_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Interval = null! });
        result.ShouldHaveValidationErrorFor(x => x.Interval);
    }

    [Theory]
    [InlineData("weekly")]
    [InlineData("daily")]
    [InlineData("biannual")]
    [InlineData("invalid")]
    public void Validate_InvalidInterval_HasError(string interval)
    {
        var result = _validator.TestValidate(ValidCommand() with { Interval = interval });
        result.ShouldHaveValidationErrorFor(x => x.Interval);
    }

    [Theory]
    [InlineData("Monthly")]
    [InlineData("MONTHLY")]
    [InlineData("Yearly")]
    [InlineData("YEARLY")]
    public void Validate_IntervalCaseInsensitive_NoErrors(string interval)
    {
        var result = _validator.TestValidate(ValidCommand() with { Interval = interval });
        result.ShouldNotHaveAnyValidationErrors();
    }
}
