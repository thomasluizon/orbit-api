using FluentAssertions;
using Orbit.Application.Marketing.Commands;
using Orbit.Application.Marketing.Validators;

namespace Orbit.Application.Tests.Validators;

public class SendMarketingBroadcastCommandValidatorTests
{
    private readonly SendMarketingBroadcastCommandValidator _validator = new();

    private static SendMarketingBroadcastCommand Valid() =>
        new("EN subject", "PT assunto", "<p>en</p>", "<p>pt</p>", TestEmail: null);

    [Fact]
    public void Valid_Passes()
    {
        _validator.Validate(Valid()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "PT", "en", "pt")]
    [InlineData("EN", "", "en", "pt")]
    [InlineData("EN", "PT", "", "pt")]
    [InlineData("EN", "PT", "en", "")]
    public void MissingSubjectOrBody_Fails(string subjectEn, string subjectPt, string bodyEn, string bodyPt)
    {
        var command = new SendMarketingBroadcastCommand(subjectEn, subjectPt, bodyEn, bodyPt, TestEmail: null);

        _validator.Validate(command).IsValid.Should().BeFalse();
    }

    [Fact]
    public void OversizedSubject_Fails()
    {
        var command = Valid() with { SubjectEn = new string('x', 201) };

        _validator.Validate(command).IsValid.Should().BeFalse();
    }

    [Fact]
    public void InvalidTestEmail_Fails()
    {
        var command = Valid() with { TestEmail = "not-an-email" };

        _validator.Validate(command).IsValid.Should().BeFalse();
    }

    [Fact]
    public void ValidTestEmail_Passes()
    {
        var command = Valid() with { TestEmail = "preview@example.com" };

        _validator.Validate(command).IsValid.Should().BeTrue();
    }
}
