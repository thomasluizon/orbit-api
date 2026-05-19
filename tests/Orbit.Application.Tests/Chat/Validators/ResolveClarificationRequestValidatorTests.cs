using FluentAssertions;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Validators;

namespace Orbit.Application.Tests.Chat.Validators;

public class ResolveClarificationRequestValidatorTests
{
    private readonly ResolveClarificationRequestValidator _validator = new();

    [Fact]
    public void Valid_Value_Passes()
    {
        var result = _validator.Validate(new ResolveClarificationRequest("{\"frequency_unit\":\"Day\"}"));
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Empty_Value_Fails()
    {
        var result = _validator.Validate(new ResolveClarificationRequest(""));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Value");
    }

    [Fact]
    public void Whitespace_Value_Fails()
    {
        var result = _validator.Validate(new ResolveClarificationRequest("   "));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void TooLong_Value_Fails()
    {
        var payload = new string('x', ResolveClarificationRequestValidator.MaxValueLength + 1);
        var result = _validator.Validate(new ResolveClarificationRequest(payload));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("cannot exceed"));
    }

    [Fact]
    public void MaxLength_Value_Passes()
    {
        var payload = new string('x', ResolveClarificationRequestValidator.MaxValueLength);
        var result = _validator.Validate(new ResolveClarificationRequest(payload));
        result.IsValid.Should().BeTrue();
    }
}
