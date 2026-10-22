using System.Text.RegularExpressions;
using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public static partial class HabitSemantics
{
    public static bool HasNegatedName(string? title) => !string.IsNullOrWhiteSpace(title) && NegatedName().IsMatch(title);

    public static AppError? ValidatePolarity(string? title, bool isBadHabit) =>
        isBadHabit && HasNegatedName(title) ? DomainErrors.BadHabitNegatedName : null;

    [GeneratedRegex(@"^[\s\p{P}\p{S}\uFE0F]*(?:não|nao|sem|parar\s+de|evitar|don['’]t|do\s+not|stop|avoid|without|not|no)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NegatedName();
}
