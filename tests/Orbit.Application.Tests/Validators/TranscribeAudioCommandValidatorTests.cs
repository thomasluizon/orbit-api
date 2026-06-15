using FluentValidation.TestHelper;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Validators;

namespace Orbit.Application.Tests.Validators;

public class TranscribeAudioCommandValidatorTests
{
    private readonly TranscribeAudioCommandValidator _validator = new();

    private static TranscribeAudioCommand ValidCommand() => new(
        Audio: [1, 2, 3],
        FileName: "clip.webm");

    [Fact]
    public void Validate_ValidCommand_NoErrors()
    {
        var result = _validator.TestValidate(ValidCommand());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyAudio_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Audio = [] });
        result.ShouldHaveValidationErrorFor(x => x.Audio);
    }

    [Fact]
    public void Validate_DisallowedExtension_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { FileName = "clip.txt" });
        result.ShouldHaveValidationErrorFor(x => x.FileName);
    }

    [Fact]
    public void Validate_MissingExtension_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { FileName = "clip" });
        result.ShouldHaveValidationErrorFor(x => x.FileName);
    }

    [Fact]
    public void Validate_AllowedExtensionDifferentCase_NoError()
    {
        var result = _validator.TestValidate(ValidCommand() with { FileName = "clip.M4A" });
        result.ShouldNotHaveValidationErrorFor(x => x.FileName);
    }
}
