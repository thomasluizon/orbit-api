using Orbit.Domain.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Orbit.Domain.Models;

public sealed partial class OnboardingInput
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
        if (!IsTitleAllowed(title) || language is not ("en" or "pt-BR"))
            return Result.Failure<OnboardingInput>("ONBOARDING_UNAVAILABLE");

        return Result.Success(new OnboardingInput(title.Trim(), language));
    }

    public static bool IsTitleAllowed([NotNullWhen(true)] string? title) => !string.IsNullOrWhiteSpace(title)
        && title.Length <= MaxTitleLength && !title.Any(char.IsControl) && !ContainsUrl(title);

    public static bool ContainsUrl(string text) => UrlPattern().IsMatch(text);

    [GeneratedRegex(@"\b(?:[a-z][a-z0-9+.-]*://|www\.|(?:[a-z0-9-]+\.)+[a-z]{2,63}\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();
}
