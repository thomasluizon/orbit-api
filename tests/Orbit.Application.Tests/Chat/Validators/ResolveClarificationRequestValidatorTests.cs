using FluentAssertions;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Chat.Validators;

public class ResolveClarificationRequestValidatorTests
{
    private readonly ResolveClarificationRequestValidator _validator = new();

    [Fact]
    public void ValidJsonObjectValue_Passes()
    {
        var result = _validator.Validate(new ResolveClarificationRequest("{\"frequency_unit\":\"Day\"}"));
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyValue_Fails()
    {
        var result = _validator.Validate(new ResolveClarificationRequest(""));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Value");
    }

    [Fact]
    public void WhitespaceValue_Fails()
    {
        var result = _validator.Validate(new ResolveClarificationRequest("   "));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Value");
    }

    [Fact]
    public void TooLongValue_Fails()
    {
        var payload = new string('x', AppConstants.MaxClarificationValueLength + 1);
        var result = _validator.Validate(new ResolveClarificationRequest(payload));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("cannot exceed"));
    }

    [Fact]
    public void MaxLengthValidJsonObject_Passes()
    {
        const string prefix = "{\"k\":\"";
        const string suffix = "\"}";
        var fillerLength = AppConstants.MaxClarificationValueLength - prefix.Length - suffix.Length;
        var payload = prefix + new string('x', fillerLength) + suffix;
        payload.Length.Should().Be(AppConstants.MaxClarificationValueLength);

        var result = _validator.Validate(new ResolveClarificationRequest(payload));
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]    [InlineData("\"a string\"")]    [InlineData("42")]    [InlineData("null")]    [InlineData("true")]    public void NonObjectValue_Fails(string value)
    {
        var result = _validator.Validate(new ResolveClarificationRequest(value));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Value");
    }
}
