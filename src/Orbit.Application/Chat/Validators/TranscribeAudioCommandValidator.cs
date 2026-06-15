using FluentValidation;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;

namespace Orbit.Application.Chat.Validators;

public class TranscribeAudioCommandValidator : AbstractValidator<TranscribeAudioCommand>
{
    private static readonly HashSet<string> AllowedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".webm", ".m4a", ".mp4", ".mp3", ".wav", ".ogg", ".oga", ".mpeg", ".mpga", ".flac"
    };

    public TranscribeAudioCommandValidator()
    {
        RuleFor(x => x.Audio)
            .Must(audio => audio.Length > 0)
            .WithMessage("Audio file is required.")
            .Must(audio => audio.Length <= AppConstants.MaxAudioBytes)
            .WithMessage($"Audio exceeds the maximum size of {AppConstants.MaxAudioBytes / (1024 * 1024)}MB.");

        RuleFor(x => x.FileName)
            .Must(HasAllowedExtension)
            .WithMessage(x => $"Audio format '{Path.GetExtension(x.FileName)}' is not supported.");
    }

    private static bool HasAllowedExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return !string.IsNullOrWhiteSpace(extension) && AllowedAudioExtensions.Contains(extension);
    }
}
