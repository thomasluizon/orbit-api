using Orbit.Domain.Common;

namespace Orbit.Domain.Models;

public sealed class OnboardingInput
{
    public const int MaxTitleLength = 100;
    public string Title { get; }
    public string Language { get; }

    private OnboardingInput(string title, string language)
    {
        Title = title;
        Language = language;
    }

    public static Result<OnboardingInput> Create(string? title, string? language)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > MaxTitleLength
            || title.Any(char.IsControl) || title.Contains("://", StringComparison.Ordinal)
            || title.Contains("www.", StringComparison.OrdinalIgnoreCase)
            || language is not ("en" or "pt-BR"))
            return Result.Failure<OnboardingInput>("ONBOARDING_UNAVAILABLE");

        return Result.Success(new OnboardingInput(title.Trim(), language));
    }
}
