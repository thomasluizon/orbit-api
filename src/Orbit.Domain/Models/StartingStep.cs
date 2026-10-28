namespace Orbit.Domain.Models;

public sealed record StartingStep
{
    public const int MaxTextLength = 200;
    public string Goal { get; }
    public string Step { get; }
    public string Language { get; }

    public StartingStep(string goal, string step, string language)
    {
        if (!IsValidText(goal) || !IsValidText(step) || !IsValidLanguage(language))
            throw new ArgumentException("A starting step requires a goal, a single short action and a supported language.");
        Goal = goal.Trim();
        Step = step.Trim();
        Language = language;
    }

    public static bool IsValidText(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaxTextLength && value.IndexOfAny(['\r', '\n']) < 0;

    public static bool IsValidLanguage(string? language) => language is "en" or "pt-BR";
}
